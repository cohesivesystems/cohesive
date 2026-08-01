using System.Collections.Immutable;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Linearizable local reference interpretation of fenced backend-pool routing over explicit target dependencies.
/// </summary>
/// <remarks>
/// The router never performs ambient feature-flag or dependency lookup. A caller resolves configuration into the
/// canonical swap command, while this type owns the pool revision/fence linearization point. Physical target
/// promotion remains target-owned and must precede read admission. Pool retirement stays orthogonal to a target's
/// local generation state, and cleanup consumes an explicit adapter-owned physical receipt.
/// </remarks>
public sealed class InMemoryMaterializationBackendRouter : IMaterializationBackendRouter, IDisposable
{
    readonly SemaphoreSlim gate = new(initialCount: 1, maxCount: 1);
    readonly IMaterializationTargetPool targets;
    readonly TimeProvider timeProvider;
    readonly Dictionary<MaterializationBackendRoutingCommandId, StoredCommandReceipt> receipts = [];
    readonly Dictionary<MaterializationBackendGenerationReference, MaterializationBackendDrainState> draining = [];
    readonly Dictionary<MaterializationBackendGenerationReference, MaterializationBackendRetirementState> retired = [];
    readonly HashSet<MaterializationBackendGenerationReference> cleaned = [];
    MaterializationBackendRoutingRevision revision = MaterializationBackendRoutingRevision.Initial;
    MaterializationBackendRoutingFence? latestFence;
    MaterializationReadableBackendReference? activeRead;
    MaterializationBackendGenerationReference? activeWrite;
    MaterializationBackendGenerationReference? candidate;
    MaterializationBackendRoutingConfiguration? effectiveConfiguration;
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
    public async ValueTask<MaterializationBackendRoutingSnapshot> InspectAsync(OperationContext context)
    {
        RequireContext(context);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            return Snapshot();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRouteBinding> ResolveReadAsync(OperationContext context)
    {
        RequireContext(context);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var route = activeRead?.Generation
                ?? throw new InvalidOperationException("Backend-pool read routing has not been initialized.");
            return new(revision, route, targets.Resolve(route.TargetId));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationBackendRouteBinding> ResolveWriteAsync(OperationContext context)
    {
        RequireContext(context);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var route = activeWrite
                ?? throw new InvalidOperationException("Backend-pool write routing has not been initialized.");
            return new(revision, route, targets.Resolve(route.TargetId));
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
            var prior = BeginCommand(request.Header, request);
            if (prior is not null)
                return prior;

            if (candidate is not null)
                return Reject(MaterializationBackendRoutingDisposition.StateConflict, "Another candidate is already admitted.");
            if (ContainsLifecycleReference(request.Candidate)
                || activeRead?.Generation == request.Candidate
                || activeWrite == request.Candidate)
            {
                return Reject(MaterializationBackendRoutingDisposition.StateConflict, "The requested candidate already has an incompatible pool role.");
            }

            var generation = await InspectGenerationAsync(context, request.Candidate).ConfigureAwait(false);
            if (generation is null)
                return Reject(MaterializationBackendRoutingDisposition.NotFound, "The candidate generation is not retained by its backend.");
            if (generation.State is not (MaterializationGenerationState.Loading
                or MaterializationGenerationState.Sealed
                or MaterializationGenerationState.Validated
                or MaterializationGenerationState.Active))
            {
                return Reject(MaterializationBackendRoutingDisposition.StateConflict, "The physical generation cannot be admitted as a candidate from its current lifecycle state.");
            }

            candidate = request.Candidate;
            return Commit(request.Header, request, MaterializationBackendRoutingOperation.AdmitCandidate);
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
            var prior = BeginCommand(request.Header, request);
            if (prior is not null)
                return prior;
            if (candidate != request.Candidate)
                return Reject(MaterializationBackendRoutingDisposition.NotFound, "The addressed generation is not the current candidate.");
            if (activeRead?.Generation == request.Candidate || activeWrite == request.Candidate)
            {
                return Reject(
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "A routed candidate must leave read and write admission before its pool role can be cleared.");
            }

            var generation = await InspectGenerationAsync(context, request.Candidate).ConfigureAwait(false);
            if (generation is null)
                return Reject(MaterializationBackendRoutingDisposition.NotFound, "The candidate generation is not retained by its backend.");
            if (generation.State != MaterializationGenerationState.Retired
                || generation.RetiredAtUtc != request.Abandonment.AbandonedAtUtc)
            {
                return Reject(
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "A candidate role can be cleared only after target-owned permanent abandonment.");
            }

            candidate = null;
            return Commit(request.Header, request, MaterializationBackendRoutingOperation.AbandonCandidate);
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
            var prior = BeginCommand(request.Header, request);
            if (prior is not null)
                return prior;
            if (request.Read is null || request.Write is null)
                return Reject(MaterializationBackendRoutingDisposition.EvidenceConflict, "A swap requires exact read and write routes.");
            if (request.Read.Generation.DefinitionFingerprint != Document.Definition.DefinitionFingerprint
                || request.Write.DefinitionFingerprint != Document.Definition.DefinitionFingerprint)
            {
                return Reject(MaterializationBackendRoutingDisposition.EvidenceConflict, "A route implements another materialization definition.");
            }
            if (retired.ContainsKey(request.Read.Generation)
                || retired.ContainsKey(request.Write)
                || cleaned.Contains(request.Read.Generation)
                || cleaned.Contains(request.Write))
            {
                return Reject(MaterializationBackendRoutingDisposition.StateConflict, "A retired or cleaned generation cannot be routed.");
            }
            if (request.Configuration.ReadTarget != request.Read.Generation.TargetId
                || request.Configuration.WriteTarget != request.Write.TargetId)
            {
                return Reject(
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "Resolved configuration must select the exact requested read and write targets.");
            }

            var readValidation = await ValidateReadableAsync(context, request.Read).ConfigureAwait(false);
            if (readValidation is not null)
                return readValidation;
            var writeGeneration = await InspectGenerationAsync(context, request.Write).ConfigureAwait(false);
            if (writeGeneration is null)
                return Reject(MaterializationBackendRoutingDisposition.NotFound, "The requested write generation is not retained.");
            if (writeGeneration.State is not (MaterializationGenerationState.Loading or MaterializationGenerationState.Active))
            {
                return Reject(MaterializationBackendRoutingDisposition.StateConflict, "The requested write generation is not writable.");
            }

            var currentReadGeneration = activeRead?.Generation;
            var readGenerationChanged = currentReadGeneration != request.Read.Generation;
            var readEvidenceChanged = activeRead != request.Read;
            var writeChanged = activeWrite != request.Write;
            var configurationChanged = effectiveConfiguration != request.Configuration;
            if (!readEvidenceChanged && !writeChanged && !configurationChanged)
                return Reject(MaterializationBackendRoutingDisposition.StateConflict, "A routing swap must change a route or its effective configuration.");

            var restoredGenerations = RestoredGenerations(
                request.Read.Generation,
                readGenerationChanged,
                request.Write,
                writeChanged);
            if (restoredGenerations.Length > 1)
            {
                return Reject(
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "One atomic swap cannot restore multiple draining generations with a single equivalence proof.");
            }
            if (restoredGenerations.Length == 1
                && !IsExactRollback(request.Rollback, restoredGenerations[0]))
            {
                return Reject(
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "Returning to a draining generation requires exact current-revision equivalence evidence.");
            }
            if (restoredGenerations.IsEmpty && request.Rollback is not null)
            {
                return Reject(MaterializationBackendRoutingDisposition.EvidenceConflict, "Rollback evidence was supplied for a forward-only swap.");
            }

            if (activeRead is not null
                && (readGenerationChanged
                        && !restoredGenerations.Contains(request.Read.Generation)
                        && !IsLegalForwardRoute(request.Read.Generation)
                    || writeChanged
                        && !restoredGenerations.Contains(request.Write)
                        && !IsLegalForwardRoute(request.Write)))
            {
                return Reject(
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "Each changed forward route must select the admitted candidate or an already-routed generation.");
            }

            var nextRevision = revision.Next();
            var priorRead = activeRead?.Generation;
            var priorWrite = activeWrite;
            if (priorWrite is not null && writeChanged)
                BeginDrain(priorWrite, nextRevision);
            if (priorRead is not null
                && readGenerationChanged)
            {
                BeginDrain(priorRead, nextRevision);
            }

            if (restoredGenerations.Length == 1)
            {
                var restored = restoredGenerations[0];
                var simultaneouslyRemovedFromAnotherRoute = priorRead == restored && readGenerationChanged
                    || priorWrite == restored && writeChanged;
                if (!simultaneouslyRemovedFromAnotherRoute)
                    draining.Remove(restored);
            }
            activeRead = request.Read;
            activeWrite = request.Write;
            effectiveConfiguration = request.Configuration;
            if (candidate == request.Read.Generation)
                candidate = null;

            return Commit(request.Header, request, MaterializationBackendRoutingOperation.Swap, nextRevision);
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
            var prior = BeginCommand(request.Header, request);
            if (prior is not null)
                return prior;
            if (request.Proof is null
                || !draining.TryGetValue(request.Proof.Generation, out var drain))
            {
                return Reject(MaterializationBackendRoutingDisposition.NotFound, "The addressed generation is not draining.");
            }
            if (activeRead?.Generation == request.Proof.Generation || activeWrite == request.Proof.Generation)
            {
                return Reject(MaterializationBackendRoutingDisposition.StateConflict, "A routed generation cannot complete drain.");
            }
            if (drain.Proof is not null
                || drain.AdmissionsClosedAtRevision != request.Proof.AdmissionsClosedAtRevision)
            {
                return Reject(MaterializationBackendRoutingDisposition.EvidenceConflict, "Drain evidence does not match the exact admission boundary.");
            }
            if (request.Header.IssuedAtUtc < request.Proof.ObservedAtUtc)
                return Reject(MaterializationBackendRoutingDisposition.EvidenceConflict, "Drain completion cannot predate its quiescence observation.");

            draining[request.Proof.Generation] = new(
                generation: drain.Generation,
                admissionsClosedAtRevision: drain.AdmissionsClosedAtRevision,
                proof: request.Proof);
            return Commit(request.Header, request, MaterializationBackendRoutingOperation.CompleteDrain);
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
            var prior = BeginCommand(request.Header, request);
            if (prior is not null)
                return prior;
            if (!draining.TryGetValue(request.Generation, out var drain) || drain.Proof is null)
            {
                return Reject(MaterializationBackendRoutingDisposition.StateConflict, "Retirement requires completed quiescence evidence.");
            }
            if (activeRead?.Generation == request.Generation
                || activeWrite == request.Generation
                || candidate == request.Generation)
            {
                return Reject(MaterializationBackendRoutingDisposition.StateConflict, "A routed or candidate generation cannot be retired.");
            }

            draining.Remove(request.Generation);
            var nextRevision = revision.Next();
            retired.Add(
                request.Generation,
                new(request.Generation, nextRevision));
            return Commit(request.Header, request, MaterializationBackendRoutingOperation.Retire, nextRevision);
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
            var prior = BeginCommand(request.Header, request);
            if (prior is not null)
                return prior;
            if (request.Proof is null)
                return Reject(MaterializationBackendRoutingDisposition.EvidenceConflict, "Cleanup requires exact adapter-owned physical evidence.");
            if (!retired.TryGetValue(request.Proof.Generation, out var retirement))
                return Reject(MaterializationBackendRoutingDisposition.StateConflict, "Cleanup requires a pool-retired generation.");
            if (request.Proof.RetiredAtRevision != retirement.RetiredAtRevision)
            {
                return Reject(
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "Physical cleanup evidence must cite the exact retained pool-retirement revision.");
            }

            retired.Remove(request.Proof.Generation);
            cleaned.Add(request.Proof.Generation);
            return Commit(request.Header, request, MaterializationBackendRoutingOperation.Cleanup);
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
        OperationContext context,
        MaterializationReadableBackendReference route)
    {
        if (route.Activation.Materialization != Document.Definition.MaterializationId)
            return Reject(MaterializationBackendRoutingDisposition.EvidenceConflict, "Activation evidence belongs to another materialization.");
        var target = TryResolve(route.Generation.TargetId);
        if (target is null)
            return Reject(MaterializationBackendRoutingDisposition.NotFound, "The read backend is absent from the pool.");
        var targetSnapshot = await target.InspectAsync(context).ConfigureAwait(false);
        var generation = await target.InspectGenerationAsync(context, route.Generation.GenerationId).ConfigureAwait(false);
        if (generation is null)
            return Reject(MaterializationBackendRoutingDisposition.NotFound, "The read generation is not retained.");
        if (targetSnapshot.ActiveGenerationId != route.Generation.GenerationId
            || targetSnapshot.Revision != route.Activation.TargetRevision
            || generation.State != MaterializationGenerationState.Active
            || generation.DefinitionFingerprint != route.Generation.DefinitionFingerprint
            || generation.ValidationReceipt is not { Validation.IsValid: true } validation
            || validation.Fingerprint != route.Activation.Validation)
        {
            return Reject(
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
        MaterializationBackendRoutingCommandHeader header,
        object request)
    {
        if (header is null)
            return Reject(MaterializationBackendRoutingDisposition.EvidenceConflict, "A routing command requires a header.");
        if (receipts.TryGetValue(header.CommandId, out var prior))
        {
            if (Equals(prior.Request, request))
            {
                return new(
                    MaterializationBackendRoutingDisposition.Replayed,
                    Snapshot(),
                    prior.Receipt);
            }

            AcceptFenceIfOwned(header);
            return Reject(MaterializationBackendRoutingDisposition.IdentityConflict, "The command identity was reused for different content.");
        }
        if (header.PoolId != Document.Definition.Id
            || header.PoolDefinitionFingerprint != Document.DefinitionFingerprint)
        {
            return Reject(MaterializationBackendRoutingDisposition.RevisionConflict, "The command addresses another pool definition.");
        }

        var staleFence = latestFence is { } latest && header.Fence.Ordinal < latest.Ordinal;
        AcceptFence(header.Fence);
        if (staleFence)
            return Reject(MaterializationBackendRoutingDisposition.StaleFence, "A newer routing authority superseded the command.");
        if (header.ExpectedRevision != revision)
            return Reject(MaterializationBackendRoutingDisposition.RevisionConflict, "The expected routing revision is stale.");
        if (revision.Ordinal == long.MaxValue)
            return Reject(MaterializationBackendRoutingDisposition.StateConflict, "The routing revision space is exhausted.");
        return null;
    }

    MaterializationBackendRoutingResult Commit(
        MaterializationBackendRoutingCommandHeader header,
        object request,
        MaterializationBackendRoutingOperation operation,
        MaterializationBackendRoutingRevision? committedRevision = null)
    {
        revision = committedRevision ?? revision.Next();
        var receipt = new MaterializationBackendRoutingReceipt(
            commandId: header.CommandId,
            operation: operation,
            revision: revision,
            fence: header.Fence,
            committedAtUtc: timeProvider.GetUtcNow());
        receipts.Add(header.CommandId, new(request, receipt));
        return new(MaterializationBackendRoutingDisposition.Applied, Snapshot(), receipt);
    }

    MaterializationBackendRoutingResult Reject(
        MaterializationBackendRoutingDisposition disposition,
        string detail) =>
        new(disposition, Snapshot(), detail: detail);

    MaterializationBackendRoutingSnapshot Snapshot() =>
        new(
            poolId: Document.Definition.Id,
            poolDefinitionFingerprint: Document.DefinitionFingerprint,
            revision: revision,
            latestFence: latestFence,
            activeRead: activeRead,
            activeWrite: activeWrite,
            candidate: candidate,
            draining: [.. draining.Values],
            retired: [.. retired.Values],
            cleaned: [.. cleaned],
            configuration: effectiveConfiguration);

    ImmutableArray<MaterializationBackendGenerationReference> RestoredGenerations(
        MaterializationBackendGenerationReference read,
        bool readChanged,
        MaterializationBackendGenerationReference write,
        bool writeChanged)
    {
        var restored = ImmutableArray.CreateBuilder<MaterializationBackendGenerationReference>(2);
        if (readChanged && draining.ContainsKey(read))
            restored.Add(read);
        if (writeChanged && draining.ContainsKey(write) && !restored.Contains(write))
            restored.Add(write);
        return restored.ToImmutable();
    }

    bool IsLegalForwardRoute(MaterializationBackendGenerationReference generation) =>
        !draining.ContainsKey(generation)
        && (generation == candidate
            || generation == activeRead?.Generation
            || generation == activeWrite);

    bool IsExactRollback(
        MaterializationBackendRollbackProof? proof,
        MaterializationBackendGenerationReference generation) =>
        proof is not null
        && proof.Generation == generation
        && proof.ExpectedRoutingRevision == revision
        && proof.CurrentRead == activeRead
        && proof.CurrentWrite == activeWrite;

    void BeginDrain(
        MaterializationBackendGenerationReference generation,
        MaterializationBackendRoutingRevision admissionsClosedAtRevision)
    {
        draining[generation] = new(generation, admissionsClosedAtRevision);
    }

    bool ContainsLifecycleReference(MaterializationBackendGenerationReference reference) =>
        draining.ContainsKey(reference) || retired.ContainsKey(reference) || cleaned.Contains(reference);

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

    void AcceptFenceIfOwned(MaterializationBackendRoutingCommandHeader header)
    {
        if (header.PoolId == Document.Definition.Id
            && header.PoolDefinitionFingerprint == Document.DefinitionFingerprint)
        {
            AcceptFence(header.Fence);
        }
    }

    void AcceptFence(MaterializationBackendRoutingFence fence)
    {
        if (latestFence is null || fence.Ordinal > latestFence.Value.Ordinal)
            latestFence = fence;
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

    sealed record StoredCommandReceipt(object Request, MaterializationBackendRoutingReceipt Receipt);
}
