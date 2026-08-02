using System.Collections.Immutable;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Linearizable local reference interpretation of fenced placement routing over explicit backend-pool dependencies.
/// </summary>
/// <remarks>
/// The router never performs ambient feature-flag or dependency lookup. A caller resolves configuration into the
/// canonical swap command, while this type owns each placement slice's revision/fence linearization point. Physical
/// target promotion remains target-owned and must precede read admission. Placement retirement stays orthogonal to a
/// target's local generation state, and cleanup consumes explicit reservation-bound adapter evidence.
/// </remarks>
public sealed class InMemoryMaterializationBackendRouter : IMaterializationBackendRouter, IDisposable
{
    readonly SemaphoreSlim gate = new(initialCount: 1, maxCount: 1);
    readonly IMaterializationTargetPool targets;
    readonly TimeProvider timeProvider;
    readonly Dictionary<MaterializationPlacementSliceFingerprint, ScopeState> scopes = [];
    readonly Dictionary<MaterializationBackendGenerationReference, PhysicalCleanupState> physicalCleanup = [];
    bool disposed;

    /// <summary>Creates an uninitialized router for one exact canonical backend-pool definition.</summary>
    /// <param name="document">Canonical pool document and content fingerprint.</param>
    /// <param name="targets">Exact-ID dependency pool implementing the document's members.</param>
    /// <param name="timeProvider">Clock used only to timestamp committed routing receipts.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="targets"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The dependency pool implements another definition.</exception>
    public InMemoryMaterializationBackendRouter(
        MaterializationBackendPoolDocument document,
        IMaterializationTargetPool targets,
        TimeProvider? timeProvider = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        this.targets = targets ?? throw new ArgumentNullException(nameof(targets));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (MaterializationBackendPoolFingerprinter.Compute(targets.Definition) != document.DefinitionFingerprint)
            throw new ArgumentException("The target pool must implement the exact routed backend-pool definition.", nameof(targets));
    }

    /// <summary>Canonical pool definition and exact content fingerprint governing this router.</summary>
    public MaterializationBackendPoolDocument Document { get; }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingSnapshot> InspectAsync(
        OperationContext context,
        MaterializationPlacementSliceReference placementSlice)
    {
        ArgumentNullException.ThrowIfNull(placementSlice);
        RequireContext(context);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            return Snapshot(GetOrCreateState(placementSlice));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRouteBinding> ResolveReadAsync(
        OperationContext context,
        MaterializationPlacementSliceReference placementSlice)
    {
        ArgumentNullException.ThrowIfNull(placementSlice);
        RequireContext(context);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var state = GetOrCreateState(placementSlice);
            var route = state.ActiveRead?.Generation
                ?? throw new InvalidOperationException("Backend-pool read routing has not been initialized.");
            return new(placementSlice, state.Revision, route, targets.Resolve(route.TargetId));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRouteBinding> ResolveWriteAsync(
        OperationContext context,
        MaterializationPlacementSliceReference placementSlice)
    {
        ArgumentNullException.ThrowIfNull(placementSlice);
        RequireContext(context);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var state = GetOrCreateState(placementSlice);
            var route = state.ActiveWrite
                ?? throw new InvalidOperationException("Backend-pool write routing has not been initialized.");
            return new(placementSlice, state.Revision, route, targets.Resolve(route.TargetId));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> AdmitCandidateAsync(
        OperationContext context,
        MaterializationAdmitBackendCandidateRequest request)
    {
        RequireContext(context);
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var state = GetOrCreateState(request.Header.PlacementSlice);
            var prior = BeginCommand(state, request.Header, request);
            if (prior is not null)
                return prior;

            if (physicalCleanup.ContainsKey(request.Candidate))
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "A generation reserved or acknowledged for physical cleanup cannot be admitted again.");
            }
            if (state.Candidate is not null)
                return Reject(state, MaterializationBackendRoutingDisposition.StateConflict, "Another candidate is already admitted.");
            if (ContainsLifecycleReference(state, request.Candidate)
                || state.ActiveRead?.Generation == request.Candidate
                || state.ActiveWrite == request.Candidate)
            {
                return Reject(state, MaterializationBackendRoutingDisposition.StateConflict, "The requested candidate already has an incompatible placement role.");
            }
            if (request.Candidate.TargetId != state.PlacementSlice.Target)
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "A candidate must use the exact target assigned by its placement slice.");
            }

            var generation = await InspectGenerationAsync(context, request.Candidate).ConfigureAwait(false);
            if (generation is null)
                return Reject(state, MaterializationBackendRoutingDisposition.NotFound, "The candidate generation is not retained by its backend.");
            if (generation.State is not (MaterializationGenerationState.Loading
                or MaterializationGenerationState.Sealed
                or MaterializationGenerationState.Validated
                or MaterializationGenerationState.Active))
            {
                return Reject(state, MaterializationBackendRoutingDisposition.StateConflict, "The physical generation cannot be admitted as a candidate from its current lifecycle state.");
            }
            if (request.ExpectedFollowUp is { } expectedFollowUp
                && state.Intents.ContainsKey(expectedFollowUp.Header.CommandId))
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.IdentityConflict,
                    "The expected follow-up command identity is already reserved for different content.");
            }

            state.Candidate = request.Candidate;
            if (request.ExpectedFollowUp is { } followUp)
            {
                state.Intents.Add(
                    followUp.Header.CommandId,
                    StoredCommandIntent.ExpectedFollowUp(followUp));
                state.PendingFollowUp = new(followUp);
            }
            return Commit(state, request.Header, request, MaterializationBackendRoutingOperation.AdmitCandidate);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> AbandonCandidateAsync(
        OperationContext context,
        MaterializationAbandonBackendCandidateRequest request)
    {
        RequireContext(context);
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var state = GetOrCreateState(request.Header.PlacementSlice);
            var prior = BeginCommand(state, request.Header, request);
            if (prior is not null)
                return prior;
            if (state.Candidate != request.Candidate)
                return Reject(state, MaterializationBackendRoutingDisposition.NotFound, "The addressed generation is not the current candidate.");
            if (state.ActiveRead?.Generation == request.Candidate || state.ActiveWrite == request.Candidate)
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "A routed candidate must leave read and write admission before its placement role can be cleared.");
            }

            var generation = await InspectGenerationAsync(context, request.Candidate).ConfigureAwait(false);
            if (generation is null)
                return Reject(state, MaterializationBackendRoutingDisposition.NotFound, "The candidate generation is not retained by its backend.");
            if (generation.State != MaterializationGenerationState.Retired
                || generation.RetiredAtUtc != request.Abandonment.AbandonedAtUtc)
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "A candidate role can be cleared only after target-owned permanent abandonment.");
            }

            ClearPendingFollowUpAfterAbandonment(state, request.Candidate);
            state.Candidate = null;
            return Commit(state, request.Header, request, MaterializationBackendRoutingOperation.AbandonCandidate);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> SwapAsync(
        OperationContext context,
        MaterializationSwapBackendRoutingRequest request)
    {
        RequireContext(context);
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var state = GetOrCreateState(request.Header.PlacementSlice);
            var prior = BeginCommand(state, request.Header, request);
            if (prior is not null)
                return prior;
            if (request.Read is null || request.Write is null)
                return Reject(state, MaterializationBackendRoutingDisposition.EvidenceConflict, "A swap requires exact read and write routes.");
            if (request.Read.PlacementSlice != state.PlacementSlice)
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "The readable route belongs to another placement authority.");
            }
            if (request.Read.Generation.DefinitionFingerprint != Document.Definition.DefinitionFingerprint
                || request.Write.DefinitionFingerprint != Document.Definition.DefinitionFingerprint)
            {
                return Reject(state, MaterializationBackendRoutingDisposition.EvidenceConflict, "A route implements another materialization definition.");
            }
            if (physicalCleanup.ContainsKey(request.Read.Generation)
                || physicalCleanup.ContainsKey(request.Write))
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "A generation reserved or acknowledged for physical cleanup cannot be routed.");
            }
            if (state.Retired.ContainsKey(request.Read.Generation)
                || state.Retired.ContainsKey(request.Write)
                || state.Cleaned.Contains(request.Read.Generation)
                || state.Cleaned.Contains(request.Write))
            {
                return Reject(state, MaterializationBackendRoutingDisposition.StateConflict, "A retired or cleaned generation cannot be routed.");
            }
            if (request.Configuration.ReadTarget != request.Read.Generation.TargetId
                || request.Configuration.WriteTarget != request.Write.TargetId)
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "Resolved configuration must select the exact requested read and write targets.");
            }
            if (request.Header.IssuedAtUtc < request.Read.Activation.ActivatedAtUtc)
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "A routing swap cannot predate the activation evidence it admits.");
            }

            var readValidation = await ValidateReadableAsync(state, context, request.Read).ConfigureAwait(false);
            if (readValidation is not null)
                return readValidation;
            var writeGeneration = await InspectGenerationAsync(context, request.Write).ConfigureAwait(false);
            if (writeGeneration is null)
                return Reject(state, MaterializationBackendRoutingDisposition.NotFound, "The requested write generation is not retained.");
            if (writeGeneration.State is not (MaterializationGenerationState.Loading or MaterializationGenerationState.Active))
            {
                return Reject(state, MaterializationBackendRoutingDisposition.StateConflict, "The requested write generation is not writable.");
            }

            var currentReadGeneration = state.ActiveRead?.Generation;
            var readGenerationChanged = currentReadGeneration != request.Read.Generation;
            var readEvidenceChanged = state.ActiveRead != request.Read;
            var writeChanged = state.ActiveWrite != request.Write;
            var configurationChanged = state.EffectiveConfiguration != request.Configuration;
            if (!readEvidenceChanged && !writeChanged && !configurationChanged)
                return Reject(state, MaterializationBackendRoutingDisposition.StateConflict, "A routing swap must change a route or its effective configuration.");

            var restoredGenerations = RestoredGenerations(
                state,
                request.Read.Generation,
                readGenerationChanged,
                request.Write,
                writeChanged);
            if (restoredGenerations.Length > 1)
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "One atomic swap cannot restore multiple draining generations with a single equivalence proof.");
            }
            if (restoredGenerations.Length == 1
                && !IsExactRollback(state, request.Rollback, restoredGenerations[0]))
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "Returning to a draining generation requires exact current-revision equivalence evidence.");
            }
            if (restoredGenerations.IsEmpty && request.Rollback is not null)
            {
                return Reject(state, MaterializationBackendRoutingDisposition.EvidenceConflict, "Rollback evidence was supplied for a forward-only swap.");
            }

            if (state.ActiveRead is null
                && request.Write != request.Read.Generation
                && request.Write != state.Candidate)
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "Initial write routing must select the readable generation or the placement's admitted candidate.");
            }

            if (state.ActiveRead is not null
                && (readGenerationChanged
                        && !restoredGenerations.Contains(request.Read.Generation)
                        && !IsLegalForwardRoute(state, request.Read.Generation)
                    || writeChanged
                        && !restoredGenerations.Contains(request.Write)
                        && !IsLegalForwardRoute(state, request.Write)))
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "Each changed forward route must select the admitted candidate or an already-routed generation.");
            }

            var nextRevision = state.Revision.Next();
            var priorRead = state.ActiveRead?.Generation;
            var priorWrite = state.ActiveWrite;
            if (priorWrite is not null && writeChanged)
                BeginDrain(state, priorWrite, nextRevision);
            if (priorRead is not null
                && readGenerationChanged)
            {
                BeginDrain(state, priorRead, nextRevision);
            }

            if (restoredGenerations.Length == 1)
            {
                var restored = restoredGenerations[0];
                var simultaneouslyRemovedFromAnotherRoute = priorRead == restored && readGenerationChanged
                    || priorWrite == restored && writeChanged;
                if (!simultaneouslyRemovedFromAnotherRoute)
                    state.Draining.Remove(restored);
            }
            state.ActiveRead = request.Read;
            state.ActiveWrite = request.Write;
            state.EffectiveConfiguration = request.Configuration;
            if (state.Candidate == request.Read.Generation)
                state.Candidate = null;
            if (state.PendingFollowUp?.Request == request)
                state.PendingFollowUp = null;

            return Commit(state, request.Header, request, MaterializationBackendRoutingOperation.Swap, nextRevision);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> CompleteDrainAsync(
        OperationContext context,
        MaterializationCompleteBackendDrainRequest request)
    {
        RequireContext(context);
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var state = GetOrCreateState(request.Header.PlacementSlice);
            var prior = BeginCommand(state, request.Header, request);
            if (prior is not null)
                return prior;
            if (request.Proof is null
                || request.Proof.PlacementSlice != state.PlacementSlice
                || !state.Draining.TryGetValue(request.Proof.Generation, out var drain))
            {
                return Reject(state, MaterializationBackendRoutingDisposition.NotFound, "The addressed generation is not draining under this placement authority.");
            }
            if (state.ActiveRead?.Generation == request.Proof.Generation || state.ActiveWrite == request.Proof.Generation)
            {
                return Reject(state, MaterializationBackendRoutingDisposition.StateConflict, "A routed generation cannot complete drain.");
            }
            if (drain.Proof is not null
                || drain.AdmissionsClosedAtRevision != request.Proof.AdmissionsClosedAtRevision)
            {
                return Reject(state, MaterializationBackendRoutingDisposition.EvidenceConflict, "Drain evidence does not match the exact admission boundary.");
            }
            if (request.Header.IssuedAtUtc < request.Proof.ObservedAtUtc)
                return Reject(state, MaterializationBackendRoutingDisposition.EvidenceConflict, "Drain completion cannot predate its quiescence observation.");

            state.Draining[request.Proof.Generation] = new(
                generation: drain.Generation,
                admissionsClosedAtRevision: drain.AdmissionsClosedAtRevision,
                proof: request.Proof);
            return Commit(state, request.Header, request, MaterializationBackendRoutingOperation.CompleteDrain);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> RetireAsync(
        OperationContext context,
        MaterializationRetireBackendGenerationRequest request)
    {
        RequireContext(context);
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var state = GetOrCreateState(request.Header.PlacementSlice);
            var prior = BeginCommand(state, request.Header, request);
            if (prior is not null)
                return prior;
            if (!state.Draining.TryGetValue(request.Generation, out var drain) || drain.Proof is null)
            {
                return Reject(state, MaterializationBackendRoutingDisposition.StateConflict, "Retirement requires completed quiescence evidence.");
            }
            if (state.ActiveRead?.Generation == request.Generation
                || state.ActiveWrite == request.Generation
                || state.Candidate == request.Generation)
            {
                return Reject(state, MaterializationBackendRoutingDisposition.StateConflict, "A routed or candidate generation cannot be retired.");
            }

            state.Draining.Remove(request.Generation);
            var nextRevision = state.Revision.Next();
            state.Retired.Add(
                request.Generation,
                new(request.Generation, nextRevision));
            return Commit(state, request.Header, request, MaterializationBackendRoutingOperation.Retire, nextRevision);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendCleanupReservationResult> ReserveCleanupAsync(
        OperationContext context,
        MaterializationReserveBackendCleanupRequest request)
    {
        RequireContext(context);
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var state = GetOrCreateState(request.Header.PlacementSlice);
            var prior = BeginCommand(state, request.Header, request);
            if (prior is not null)
            {
                var replayedReservation = prior.Disposition == MaterializationBackendRoutingDisposition.Replayed
                    && physicalCleanup.TryGetValue(request.Generation, out var replayedCleanup)
                    && replayedCleanup.Reservation.Receipt == prior.Receipt
                        ? replayedCleanup.Reservation
                        : null;
                return new(prior, replayedReservation);
            }
            if (!state.Retired.TryGetValue(request.Generation, out _))
            {
                return RejectedCleanupReservation(
                    state,
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "Physical cleanup reservation requires a placement-retired generation.");
            }
            if (physicalCleanup.ContainsKey(request.Generation))
            {
                return RejectedCleanupReservation(
                    state,
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "The physical generation already has a cleanup reservation or tombstone.");
            }
            if (HasLiveReference(request.Generation))
            {
                return RejectedCleanupReservation(
                    state,
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "Physical cleanup is unsafe while any placement retains a live routing or draining role.");
            }

            ImmutableArray<MaterializationBackendCleanupRetirementClaim> retirements =
            [
                .. scopes.Values
                    .Where(scope => scope.Retired.ContainsKey(request.Generation))
                    .Select(scope => new MaterializationBackendCleanupRetirementClaim(
                        placementSlice: scope.PlacementSlice,
                        retiredAtRevision: scope.Retired[request.Generation].RetiredAtRevision))
                    .OrderBy(static claim => claim.PlacementSlice.Fingerprint.Value, StringComparer.Ordinal)
            ];
            var routing = Commit(
                state,
                request.Header,
                request,
                MaterializationBackendRoutingOperation.ReserveCleanup);
            var reservation = new MaterializationBackendCleanupReservation(
                generation: request.Generation,
                retirements,
                receipt: routing.Receipt!,
                token: CreateCleanupReservationToken(request.Generation, retirements, routing.Receipt!));
            physicalCleanup.Add(request.Generation, new(reservation));
            return new(routing, reservation);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRoutingResult> CleanupAsync(
        OperationContext context,
        MaterializationCleanupBackendGenerationRequest request)
    {
        RequireContext(context);
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var state = GetOrCreateState(request.Header.PlacementSlice);
            var prior = BeginCommand(state, request.Header, request);
            if (prior is not null)
                return prior;
            if (request.Proof is null || request.Proof.PlacementSlice != state.PlacementSlice)
                return Reject(state, MaterializationBackendRoutingDisposition.EvidenceConflict, "Cleanup requires exact placement-scoped adapter-owned physical evidence.");
            if (!state.Retired.TryGetValue(request.Proof.Generation, out var retirement))
                return Reject(state, MaterializationBackendRoutingDisposition.StateConflict, "Cleanup requires a placement-retired generation.");
            if (request.Proof.RetiredAtRevision != retirement.RetiredAtRevision)
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "Physical cleanup evidence must cite the exact retained placement-retirement revision.");
            }
            if (request.Header.IssuedAtUtc < request.Proof.ObservedAtUtc)
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "Cleanup acknowledgement cannot predate its physical completion observation.");
            }
            if (!physicalCleanup.TryGetValue(request.Proof.Generation, out var cleanup))
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "Physical cleanup evidence requires a prior reservation from this routing authority.");
            }
            if (request.Proof.ObservedAtUtc < cleanup.Reservation.Receipt.CommittedAtUtc)
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "Physical cleanup completion evidence cannot predate its exact routing reservation.");
            }
            var reservationClaim = cleanup.Reservation.Retirements.FirstOrDefault(
                claim => claim.PlacementSlice == state.PlacementSlice);
            if (reservationClaim is null
                || reservationClaim.RetiredAtRevision != retirement.RetiredAtRevision
                || !string.Equals(
                    cleanup.Reservation.Token,
                    request.Proof.ReservationToken,
                    StringComparison.Ordinal))
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "Physical cleanup evidence must cite the exact reservation and captured placement retirement.");
            }
            if (cleanup.Completion is { } priorCompletion
                && (!string.Equals(
                        priorCompletion.CleanupFingerprint,
                        request.Proof.CleanupFingerprint,
                        StringComparison.Ordinal)
                    || priorCompletion.ObservedAtUtc != request.Proof.ObservedAtUtc))
            {
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "All placement acknowledgements must cite the same physical cleanup completion evidence.");
            }
            cleanup.Completion ??= new(
                CleanupFingerprint: request.Proof.CleanupFingerprint,
                ObservedAtUtc: request.Proof.ObservedAtUtc);
            state.Retired.Remove(request.Proof.Generation);
            state.Cleaned.Add(request.Proof.Generation);
            return Commit(state, request.Header, request, MaterializationBackendRoutingOperation.Cleanup);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Releases the internal synchronization primitive.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        gate.Dispose();
    }

    async ValueTask<MaterializationBackendRoutingResult?> ValidateReadableAsync(
        ScopeState state,
        OperationContext context,
        MaterializationReadableBackendReference route)
    {
        if (route.PlacementSlice != state.PlacementSlice
            || route.Activation.Materialization != Document.Definition.MaterializationId
            || route.Activation.PlacementSlice.Pool != state.PlacementSlice.Pool
            || !route.Activation.PlacementSlice.Subjects.SequenceEqual(state.PlacementSlice.Subjects)
            || route.Generation.DefinitionFingerprint != state.PlacementSlice.Materialization.DefinitionFingerprint)
        {
            return Reject(
                state,
                MaterializationBackendRoutingDisposition.EvidenceConflict,
                "Activation evidence belongs to another placement or materialization definition.");
        }
        var target = TryResolve(route.Generation.TargetId);
        if (target is null)
            return Reject(state, MaterializationBackendRoutingDisposition.NotFound, "The read backend is absent from the pool.");
        var targetSnapshot = await target.InspectAsync(context).ConfigureAwait(false);
        var generation = await target.InspectGenerationAsync(context, route.Generation.GenerationId).ConfigureAwait(false);
        if (generation is null)
            return Reject(state, MaterializationBackendRoutingDisposition.NotFound, "The read generation is not retained.");
        if (targetSnapshot.ActiveGenerationId != route.Generation.GenerationId
            || targetSnapshot.Revision != route.Activation.TargetRevision
            || targetSnapshot.LatestPromotionFence != route.Activation.PromotionFence
            || generation.State != MaterializationGenerationState.Active
            || generation.DefinitionFingerprint != route.Generation.DefinitionFingerprint
            || generation.ValidationReceipt is not { Validation.IsValid: true } validation
            || validation.Fingerprint != route.Activation.Validation)
        {
            return Reject(
                state,
                MaterializationBackendRoutingDisposition.EvidenceConflict,
                "Reads require exact current target activation, definition, and successful validation evidence.");
        }

        return null;
    }

    async ValueTask<MaterializationGenerationSnapshot?> InspectGenerationAsync(
        OperationContext context,
        MaterializationBackendGenerationReference reference)
    {
        if (reference.DefinitionFingerprint != Document.Definition.DefinitionFingerprint)
            return null;
        var target = TryResolve(reference.TargetId);
        if (target is null || target.Descriptor.MaterializationId != Document.Definition.MaterializationId)
            return null;
        var generation = await target.InspectGenerationAsync(context, reference.GenerationId).ConfigureAwait(false);
        return generation is not null
            && generation.MaterializationId == Document.Definition.MaterializationId
            && generation.DefinitionFingerprint == reference.DefinitionFingerprint
                ? generation
                : null;
    }

    MaterializationBackendRoutingResult? BeginCommand(
        ScopeState state,
        MaterializationBackendRoutingCommandHeader header,
        object request)
    {
        if (header is null)
            return Reject(state, MaterializationBackendRoutingDisposition.EvidenceConflict, "A routing command requires a header.");
        if (header.PlacementSlice != state.PlacementSlice)
            return Reject(state, MaterializationBackendRoutingDisposition.EvidenceConflict, "The command belongs to another placement authority.");
        if (state.Receipts.TryGetValue(header.CommandId, out var prior))
        {
            if (Equals(prior.Request, request))
            {
                return new(
                    MaterializationBackendRoutingDisposition.Replayed,
                    Snapshot(state),
                    prior.Receipt);
            }

            if (state.PendingFollowUp is null)
                AcceptFence(state, header.Fence);
            return Reject(state, MaterializationBackendRoutingDisposition.IdentityConflict, "The command identity was reused for different content.");
        }

        if (state.Intents.TryGetValue(header.CommandId, out var intent))
        {
            if (intent.IsCancelled || !Equals(intent.Request, request))
            {
                if (state.PendingFollowUp is null)
                    AcceptFence(state, header.Fence);
                return Reject(
                    state,
                    MaterializationBackendRoutingDisposition.IdentityConflict,
                    "The command identity was reused for different content.");
            }
        }
        else
        {
            state.Intents.Add(header.CommandId, new(request));
        }

        if (state.PendingFollowUp is not null && !IsAllowedPendingFollowUpCommand(state, request))
        {
            return Reject(
                state,
                MaterializationBackendRoutingDisposition.StateConflict,
                "Candidate admission reserved an exact follow-up; only that swap or exact candidate abandonment may mutate this placement.");
        }

        var staleFence = state.LatestFence is { } latest && header.Fence.Ordinal < latest.Ordinal;
        AcceptFence(state, header.Fence);
        if (staleFence)
            return Reject(state, MaterializationBackendRoutingDisposition.StaleFence, "A newer routing authority superseded the command.");
        if (header.ExpectedRevision != state.Revision)
            return Reject(state, MaterializationBackendRoutingDisposition.RevisionConflict, "The expected routing revision is stale.");
        if (state.Revision.Ordinal == long.MaxValue)
            return Reject(state, MaterializationBackendRoutingDisposition.StateConflict, "The routing revision space is exhausted.");
        return null;
    }

    MaterializationBackendRoutingResult Commit(
        ScopeState state,
        MaterializationBackendRoutingCommandHeader header,
        object request,
        MaterializationBackendRoutingOperation operation,
        MaterializationBackendRoutingRevision? committedRevision = null)
    {
        state.Revision = committedRevision ?? state.Revision.Next();
        var committedAtUtc = timeProvider.GetUtcNow();
        if (committedAtUtc < header.IssuedAtUtc)
            committedAtUtc = header.IssuedAtUtc;
        var receipt = new MaterializationBackendRoutingReceipt(
            commandId: header.CommandId,
            placementSlice: state.PlacementSlice,
            operation: operation,
            revision: state.Revision,
            fence: header.Fence,
            committedAtUtc);
        if (!state.Intents.TryGetValue(header.CommandId, out var intent)
            || !Equals(intent.Request, request)
            || intent.IsCancelled)
        {
            throw new InvalidOperationException("A routing command can commit only under its exact reserved intent.");
        }
        state.Receipts.Add(header.CommandId, new(request, receipt));
        return new(MaterializationBackendRoutingDisposition.Applied, Snapshot(state), receipt);
    }

    MaterializationBackendRoutingResult Reject(
        ScopeState state,
        MaterializationBackendRoutingDisposition disposition,
        string detail) =>
        new(disposition, Snapshot(state), detail: detail);

    MaterializationBackendCleanupReservationResult RejectedCleanupReservation(
        ScopeState state,
        MaterializationBackendRoutingDisposition disposition,
        string detail) =>
        new(Reject(state, disposition, detail));

    static MaterializationBackendRoutingSnapshot Snapshot(ScopeState state) =>
        new(
            placementSlice: state.PlacementSlice,
            revision: state.Revision,
            latestFence: state.LatestFence,
            activeRead: state.ActiveRead,
            activeWrite: state.ActiveWrite,
            candidate: state.Candidate,
            draining: [.. state.Draining.Values],
            retired: [.. state.Retired.Values],
            cleaned: [.. state.Cleaned],
            configuration: state.EffectiveConfiguration,
            pendingFollowUp: state.PendingFollowUp);

    ImmutableArray<MaterializationBackendGenerationReference> RestoredGenerations(
        ScopeState state,
        MaterializationBackendGenerationReference read,
        bool readChanged,
        MaterializationBackendGenerationReference write,
        bool writeChanged)
    {
        var restored = ImmutableArray.CreateBuilder<MaterializationBackendGenerationReference>(2);
        if (readChanged && state.Draining.ContainsKey(read))
            restored.Add(read);
        if (writeChanged && state.Draining.ContainsKey(write) && !restored.Contains(write))
            restored.Add(write);
        return restored.ToImmutable();
    }

    static bool IsLegalForwardRoute(ScopeState state, MaterializationBackendGenerationReference generation) =>
        !state.Draining.ContainsKey(generation)
        && (generation == state.Candidate
            || generation == state.ActiveRead?.Generation
            || generation == state.ActiveWrite);

    static bool IsExactRollback(
        ScopeState state,
        MaterializationBackendRollbackProof? proof,
        MaterializationBackendGenerationReference generation) =>
        proof is not null
        && proof.PlacementSlice == state.PlacementSlice
        && proof.Generation == generation
        && proof.ExpectedRoutingRevision == state.Revision
        && proof.CurrentRead == state.ActiveRead
        && proof.CurrentWrite == state.ActiveWrite;

    static void BeginDrain(
        ScopeState state,
        MaterializationBackendGenerationReference generation,
        MaterializationBackendRoutingRevision admissionsClosedAtRevision)
    {
        state.Draining[generation] = new(generation, admissionsClosedAtRevision);
    }

    static bool ContainsLifecycleReference(ScopeState state, MaterializationBackendGenerationReference reference) =>
        state.Draining.ContainsKey(reference) || state.Retired.ContainsKey(reference) || state.Cleaned.Contains(reference);

    static bool IsAllowedPendingFollowUpCommand(
        ScopeState state,
        object request) =>
        state.PendingFollowUp is { } pending
        && (request is MaterializationSwapBackendRoutingRequest swap && swap == pending.Request
            || request is MaterializationAbandonBackendCandidateRequest abandonment
                && abandonment.Candidate == pending.Candidate);

    static void ClearPendingFollowUpAfterAbandonment(
        ScopeState state,
        MaterializationBackendGenerationReference candidate)
    {
        if (state.PendingFollowUp is not { } pending || pending.Candidate != candidate)
            return;
        if (!state.Intents.TryGetValue(pending.CommandId, out var intent)
            || !intent.IsExpectedFollowUp
            || !Equals(intent.Request, pending.Request))
        {
            throw new InvalidOperationException("A pending follow-up must retain its exact reserved command intent.");
        }
        intent.IsCancelled = true;
        state.PendingFollowUp = null;
    }

    ScopeState GetOrCreateState(MaterializationPlacementSliceReference placementSlice)
    {
        var expectedPool = MaterializationBackendPoolReference.FromDocument(Document);
        if (placementSlice.Pool != expectedPool
            || placementSlice.Materialization.DefinitionFingerprint != Document.Definition.DefinitionFingerprint
            || !Document.Definition.Members.Any(member => member.Id == placementSlice.Target))
        {
            throw new ArgumentException(
                "The placement slice must identify this exact materialization, backend pool, and declared target.",
                nameof(placementSlice));
        }

        if (scopes.TryGetValue(placementSlice.Fingerprint, out var state))
            return state;
        state = new(placementSlice);
        scopes.Add(placementSlice.Fingerprint, state);
        return state;
    }

    bool HasLiveReference(MaterializationBackendGenerationReference generation) =>
        scopes.Values.Any(state =>
            state.ActiveRead?.Generation == generation
            || state.ActiveWrite == generation
            || state.Candidate == generation
            || state.Draining.ContainsKey(generation));

    static string CreateCleanupReservationToken(
        MaterializationBackendGenerationReference generation,
        ImmutableArray<MaterializationBackendCleanupRetirementClaim> retirements,
        MaterializationBackendRoutingReceipt receipt)
    {
        using MaterializationStableIdentity.DigestBuilder builder = new();
        builder.Append("cohesive-materialization-backend-cleanup-reservation/v1");
        builder.Append(generation.TargetId.Value);
        builder.Append(generation.GenerationId.Value);
        builder.Append(generation.DefinitionFingerprint.Value);
        foreach (var retirement in retirements)
        {
            builder.Append(retirement.PlacementSlice.Fingerprint.Value);
            builder.Append(retirement.RetiredAtRevision.Value);
        }
        builder.Append(receipt.CommandId.Value);
        builder.Append(receipt.Revision.Value);
        return $"cleanup-reservation/{builder.Complete()}";
    }

    IMaterializationTarget? TryResolve(MaterializationTargetId targetId)
    {
        try
        {
            return targets.Resolve(targetId);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static void AcceptFence(ScopeState state, MaterializationBackendRoutingFence fence)
    {
        if (state.LatestFence is null || fence.Ordinal > state.LatestFence.Value.Ordinal)
            state.LatestFence = fence;
    }

    async ValueTask EnterAsync(OperationContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
    }

    static void RequireContext(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
    }

    sealed class ScopeState(MaterializationPlacementSliceReference placementSlice)
    {
        internal MaterializationPlacementSliceReference PlacementSlice { get; } = placementSlice;

        internal Dictionary<MaterializationBackendRoutingCommandId, StoredCommandReceipt> Receipts { get; } = [];

        internal Dictionary<MaterializationBackendRoutingCommandId, StoredCommandIntent> Intents { get; } = [];

        internal Dictionary<MaterializationBackendGenerationReference, MaterializationBackendDrainState> Draining { get; } = [];

        internal Dictionary<MaterializationBackendGenerationReference, MaterializationBackendRetirementState> Retired { get; } = [];

        internal HashSet<MaterializationBackendGenerationReference> Cleaned { get; } = [];

        internal MaterializationBackendRoutingRevision Revision { get; set; } = MaterializationBackendRoutingRevision.Initial;

        internal MaterializationBackendRoutingFence? LatestFence { get; set; }

        internal MaterializationReadableBackendReference? ActiveRead { get; set; }

        internal MaterializationBackendGenerationReference? ActiveWrite { get; set; }

        internal MaterializationBackendGenerationReference? Candidate { get; set; }

        internal MaterializationBackendFollowUpReservation? PendingFollowUp { get; set; }

        internal MaterializationBackendRoutingConfiguration? EffectiveConfiguration { get; set; }
    }

    sealed class StoredCommandIntent(object request, bool isExpectedFollowUp = false)
    {
        internal object Request { get; } = request;

        internal bool IsExpectedFollowUp { get; } = isExpectedFollowUp;

        internal bool IsCancelled { get; set; }

        internal static StoredCommandIntent ExpectedFollowUp(MaterializationSwapBackendRoutingRequest request) =>
            new(request, isExpectedFollowUp: true);
    }

    sealed record StoredCommandReceipt(object Request, MaterializationBackendRoutingReceipt Receipt);

    sealed class PhysicalCleanupState(MaterializationBackendCleanupReservation reservation)
    {
        internal MaterializationBackendCleanupReservation Reservation { get; } = reservation;

        internal PhysicalCleanupCompletion? Completion { get; set; }
    }

    sealed record PhysicalCleanupCompletion(string CleanupFingerprint, DateTimeOffset ObservedAtUtc);
}
