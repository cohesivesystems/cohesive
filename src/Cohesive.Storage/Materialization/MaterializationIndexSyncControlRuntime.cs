using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Generation-scoped runtime factory sharing one durable store and physical admission authority.</summary>
public sealed class MaterializationIndexSyncControlRuntimeProvider
{
    readonly IMaterializationIndexSyncControlStateStore store;
    readonly MaterializationIndexSyncAdmissionGate admission;
    readonly InteractionAuthorityScope? authorityScope;

    /// <summary>Creates a provider for one exact persisted plan.</summary>
    /// <param name="plan">Exact persisted realization plan.</param>
    /// <param name="store">Durable Control-state CAS authority.</param>
    /// <param name="admission">Shared physical-resource admission authority.</param>
    /// <param name="authorityScope">Optional authority allowed to submit operator overrides.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    public MaterializationIndexSyncControlRuntimeProvider(
        MaterializationRebuildPlan plan,
        IMaterializationIndexSyncControlStateStore store,
        MaterializationIndexSyncAdmissionGate admission,
        InteractionAuthorityScope? authorityScope = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
        this.authorityScope = authorityScope;
    }

    /// <summary>Exact persisted plan realized by produced runtimes.</summary>
    public MaterializationRebuildPlan Plan { get; }

    /// <summary>Creates a runtime for one new or resumed generation epoch.</summary>
    /// <param name="generation">Exact generation; pause and continue reuse the same value.</param>
    /// <returns>A generation-scoped runtime sharing durable state and physical admission.</returns>
    /// <exception cref="ArgumentException"><paramref name="generation"/> is default.</exception>
    public MaterializationIndexSyncControlRuntime ForGeneration(MaterializationGenerationId generation) =>
        new(Plan, generation, store, admission, authorityScope);

    /// <summary>Retires admission ownership after an exact durable generation lifecycle cut.</summary>
    /// <param name="generation">Generation proven abandoned, displaced, or finished for the workload.</param>
    /// <param name="workload">Workload whose contribution ownership ended.</param>
    /// <exception cref="ArgumentException"><paramref name="generation"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workload"/> is unsupported.</exception>
    internal void RetireAdmissionContributions(
        MaterializationGenerationId generation,
        MaterializationIndexSyncWorkloadKind workload)
    {
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        if (!Enum.IsDefined(workload))
            throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unsupported index-sync workload.");
        admission.RetireContributions(Plan.Fingerprint, Plan.Target.Id, generation, workload);
    }
}

/// <summary>Applied effective operating point for one explicit workload and pipeline stage.</summary>
public sealed record MaterializationIndexSyncStageControlPoint
{
    /// <summary>Creates one applied stage point.</summary>
    /// <param name="workload">Explicit governed workload.</param>
    /// <param name="stage">Source, transform, or target stage.</param>
    /// <param name="maximumConcurrency">Applied concurrency bound.</param>
    /// <param name="maximumBatchItems">Applied item bound.</param>
    /// <param name="maximumBatchBytes">Applied byte bound.</param>
    /// <param name="snapshots">Exact realization/state snapshots contributing to the point.</param>
    /// <exception cref="ArgumentOutOfRangeException">A kind is unsupported or a bound is not positive.</exception>
    /// <exception cref="ArgumentException">Snapshots differ from the workload or stage.</exception>
    public MaterializationIndexSyncStageControlPoint(
        MaterializationIndexSyncWorkloadKind workload,
        ControlStageKind stage,
        int maximumConcurrency,
        int maximumBatchItems,
        long maximumBatchBytes,
        ImmutableArray<MaterializationIndexSyncControlSnapshot> snapshots)
    {
        if (!Enum.IsDefined(workload))
            throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unsupported index-sync workload.");
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported Control stage.");
        if (maximumConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency), maximumConcurrency, "Concurrency must be positive.");
        if (maximumBatchItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBatchItems), maximumBatchItems, "Batch items must be positive.");
        if (maximumBatchBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBatchBytes), maximumBatchBytes, "Batch bytes must be positive.");
        var normalized = snapshots.IsDefault ? [] : snapshots;
        if (normalized.Any(snapshot => snapshot.Realization.Workload != workload
            || snapshot.Realization.EffectiveDefinition.Stage != stage))
        {
            throw new ArgumentException("Stage snapshots must belong to the exact workload and stage.", nameof(snapshots));
        }

        Workload = workload;
        Stage = stage;
        MaximumConcurrency = maximumConcurrency;
        MaximumBatchItems = maximumBatchItems;
        MaximumBatchBytes = maximumBatchBytes;
        Snapshots = [.. normalized.OrderBy(static snapshot => snapshot.Key.LoopId.Value, StringComparer.Ordinal)];
    }

    /// <summary>Explicit governed workload.</summary>
    public MaterializationIndexSyncWorkloadKind Workload { get; }

    /// <summary>Governed pipeline stage.</summary>
    public ControlStageKind Stage { get; }

    /// <summary>Applied maximum concurrency.</summary>
    public int MaximumConcurrency { get; }

    /// <summary>Applied maximum batch items.</summary>
    public int MaximumBatchItems { get; }

    /// <summary>Applied maximum batch bytes.</summary>
    public long MaximumBatchBytes { get; }

    /// <summary>Exact contributing realization/state snapshots.</summary>
    public ImmutableArray<MaterializationIndexSyncControlSnapshot> Snapshots { get; }
}

/// <summary>Durable materialization interpretation of Control evaluation, safe-point actuation, and admission.</summary>
public sealed class MaterializationIndexSyncControlRuntime
{
    readonly MaterializationRebuildPlan plan;
    readonly MaterializationGenerationId generation;
    readonly IMaterializationIndexSyncControlStateStore store;
    readonly MaterializationIndexSyncAdmissionGate admission;
    readonly InteractionAuthorityScope? authorityScope;

    /// <summary>Creates a runtime bound to one exact persisted plan and generation epoch.</summary>
    /// <param name="plan">Exact persisted realization plan.</param>
    /// <param name="generation">Exact generation retaining Control state across pause and continue.</param>
    /// <param name="store">Durable Control-state CAS authority.</param>
    /// <param name="admission">Shared physical-resource admission authority.</param>
    /// <param name="authorityScope">Optional authority allowed to submit operator overrides.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="generation"/> is default.</exception>
    public MaterializationIndexSyncControlRuntime(
        MaterializationRebuildPlan plan,
        MaterializationGenerationId generation,
        IMaterializationIndexSyncControlStateStore store,
        MaterializationIndexSyncAdmissionGate admission,
        InteractionAuthorityScope? authorityScope = null)
    {
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        this.generation = generation;
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
        this.authorityScope = authorityScope;
    }

    /// <summary>Exact generation controlled by this runtime.</summary>
    public MaterializationGenerationId Generation => generation;

    /// <summary>Reads or creates every exact durable Control state and returns immutable validated snapshots.</summary>
    /// <param name="context">Explicit cancellation, time, and tracing context.</param>
    /// <returns>Snapshots in canonical loop-identity order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Durable state conflicts with its exact plan epoch.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async ValueTask<ImmutableArray<MaterializationIndexSyncControlSnapshot>> GetSnapshotsAsync(
        OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var builder = ImmutableArray.CreateBuilder<MaterializationIndexSyncControlSnapshot>(
            plan.ControlRealizations.Length);
        foreach (var realization in plan.ControlRealizations)
            builder.Add(await LoadOrCreateAsync(context, realization).ConfigureAwait(false));
        return builder.MoveToImmutable();
    }

    /// <summary>Evaluates one exact typed observation and durably CAS-persists the resulting controller state.</summary>
    /// <param name="context">Explicit cancellation, time, and tracing context.</param>
    /// <param name="workload">Explicit governed workload.</param>
    /// <param name="observation">Exact revision-fenced typed observation.</param>
    /// <returns>The persisted immutable realization/state snapshot.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">No exact workload/loop realization exists.</exception>
    /// <exception cref="InvalidOperationException">Evaluation is rejected or durable CAS conflicts.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async ValueTask<MaterializationIndexSyncControlSnapshot> ObserveAsync(
        OperationContext context,
        MaterializationIndexSyncWorkloadKind workload,
        ControlObservation observation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observation);
        var realization = Find(workload, observation.LoopId);
        var snapshot = await LoadOrCreateAsync(context, realization).ConfigureAwait(false);
        var mutationId = $"observation/{observation.Id.Value}";
        var mutationFingerprint = Fingerprint(observation);
        var replay = await store.ReadMutationAsync(
                context,
                snapshot.Key,
                mutationId,
                mutationFingerprint)
            .ConfigureAwait(false);
        if (replay.Disposition == MaterializationIndexSyncControlWriteDisposition.Replayed)
            return Snapshot(realization, replay.State!);
        if (replay.Disposition == MaterializationIndexSyncControlWriteDisposition.IdentityConflict)
        {
            throw new InvalidOperationException(
                $"Control observation identity '{observation.Id.Value}' was reused for different canonical evidence.");
        }

        var decision = AimdControlReferenceRegulator.Evaluate(
            realization.EffectiveDefinition,
            snapshot.State,
            observation,
            context.UtcNow);
        if (decision.Disposition == ControlDecisionDisposition.Rejected)
            throw Failure("Control observation was rejected", decision.Diagnostics);
        if (decision.Disposition == ControlDecisionDisposition.Replayed)
            return snapshot;

        var written = await store.CompareExchangeAsync(
                context,
                snapshot.Key,
                mutationId,
                mutationFingerprint,
                snapshot.State.Revision,
                decision.State)
            .ConfigureAwait(false);
        return written.Disposition switch
        {
            MaterializationIndexSyncControlWriteDisposition.Applied
                or MaterializationIndexSyncControlWriteDisposition.Replayed =>
                Snapshot(realization, written.State!),
            MaterializationIndexSyncControlWriteDisposition.RevisionConflict =>
                throw new InvalidOperationException(
                    "Control observation CAS lost its exact revision fence; retry adapter evidence through ObserveStageAsync or submit a freshly fenced observation."),
            _ => throw new InvalidOperationException(
                $"Control observation CAS failed with '{written.Disposition}'.")
        };
    }

    /// <summary>Submits one bounded operator limit update through the same durable state authority used by workers.</summary>
    /// <param name="context">Explicit cancellation, decision time, and tracing context.</param>
    /// <param name="workload">Explicit governed workload.</param>
    /// <param name="command">Canonical revision-fenced operator command.</param>
    /// <param name="decidedAtUtc">Trusted UTC API decision and linearization time.</param>
    /// <returns>The canonical accepted, replayed, stale, unauthorized, or otherwise rejected decision.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">No exact workload, loop, target, and epoch realization exists.</exception>
    /// <exception cref="InvalidOperationException">The durable mutation authority reports an incoherent result.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async ValueTask<ControlLimitUpdateDecision> SubmitLimitUpdateAsync(
        OperationContext context,
        MaterializationIndexSyncWorkloadKind workload,
        ControlLimitUpdateCommand command,
        DateTimeOffset decidedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);
        ControlObservation.RequireUtc(decidedAtUtc, nameof(decidedAtUtc));
        var realization = FindExact(
            workload,
            command.LoopId,
            command.Target,
            command.Epoch);
        var snapshot = await LoadOrCreateAsync(context, realization).ConfigureAwait(false);
        if (command.Authorization.AuthorityScope != snapshot.State.AuthorityScope)
            return Unauthorized(snapshot.State);
        command = CanonicalizeInvocationReplay(snapshot.State, command);
        var decision = ControlLimitUpdateReferenceReducer.Submit(
            realization.EffectiveDefinition,
            snapshot.State,
            command,
            decidedAtUtc);
        if (decision.Disposition != ControlLimitUpdateDecisionDisposition.Accepted)
            return decision;

        var mutationId = $"limit-update/{command.CommandId.Value}";
        var write = await store.CompareExchangeAsync(
                context,
                snapshot.Key,
                mutationId,
                Fingerprint(command),
                snapshot.State.Revision,
                decision.State)
            .ConfigureAwait(false);
        if (write.Disposition == MaterializationIndexSyncControlWriteDisposition.Applied)
            return decision;
        if ((write.Disposition is MaterializationIndexSyncControlWriteDisposition.Replayed
                or MaterializationIndexSyncControlWriteDisposition.RevisionConflict
                or MaterializationIndexSyncControlWriteDisposition.IdentityConflict)
            && write.State is not null)
        {
            var current = Snapshot(realization, write.State!);
            if (command.Authorization.AuthorityScope != current.State.AuthorityScope)
                return Unauthorized(current.State);
            var replayCommand = CanonicalizeInvocationReplay(current.State, command);
            return ControlLimitUpdateReferenceReducer.Submit(
                realization.EffectiveDefinition,
                current.State,
                replayCommand,
                decidedAtUtc);
        }
        throw new InvalidOperationException(
            $"Control limit-update CAS failed with '{write.Disposition}'.");
    }

    /// <summary>Wraps adapter evidence in exact revision-fenced observations for every loop on one stage.</summary>
    /// <param name="context">Explicit cancellation, evaluation time, and tracing context.</param>
    /// <param name="workload">Explicit governed workload.</param>
    /// <param name="stage">Observed source, transform, or target stage.</param>
    /// <param name="windowStartedAtUtc">Inclusive measurement-window start.</param>
    /// <param name="windowEndedAtUtc">Inclusive measurement-window end.</param>
    /// <param name="observedAtUtc">Time the adapter evidence was emitted.</param>
    /// <param name="source">Stable adapter or sampler identity and version.</param>
    /// <param name="evidenceReference">Stable adapter-owned evidence identity.</param>
    /// <param name="measurements">Typed portable measurements, including explicit unavailable values.</param>
    /// <returns>Persisted snapshots for every stage loop in canonical identity order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Evidence is empty, non-UTC, or chronologically invalid.</exception>
    /// <exception cref="InvalidOperationException">Evaluation or durable CAS fails.</exception>
    public async ValueTask<ImmutableArray<MaterializationIndexSyncControlSnapshot>> ObserveStageAsync(
        OperationContext context,
        MaterializationIndexSyncWorkloadKind workload,
        ControlStageKind stage,
        DateTimeOffset windowStartedAtUtc,
        DateTimeOffset windowEndedAtUtc,
        DateTimeOffset observedAtUtc,
        string source,
        string evidenceReference,
        ImmutableArray<ControlMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("A Control observation requires a sampler identity.", nameof(source));
        if (string.IsNullOrWhiteSpace(evidenceReference))
            throw new ArgumentException("A Control observation requires an evidence identity.", nameof(evidenceReference));

        var realizationCount = plan.ControlRealizations.Count(realization =>
            realization.Workload == workload && realization.EffectiveDefinition.Stage == stage);
        var builder = ImmutableArray.CreateBuilder<MaterializationIndexSyncControlSnapshot>(realizationCount);
        foreach (var realization in plan.ControlRealizations)
        {
            if (realization.Workload != workload || realization.EffectiveDefinition.Stage != stage)
                continue;
            builder.Add(await ObserveStageRealizationAsync(
                    context,
                    realization,
                    windowStartedAtUtc,
                    windowEndedAtUtc,
                    observedAtUtc,
                    source,
                    evidenceReference,
                    measurements)
                .ConfigureAwait(false));
        }
        return builder.MoveToImmutable();
    }

    /// <summary>Applies eligible pending changes only at one exact stage-safe application point.</summary>
    /// <param name="context">Explicit cancellation, time, and tracing context.</param>
    /// <param name="workload">Explicit governed workload.</param>
    /// <param name="stage">Exact governed stage.</param>
    /// <param name="kind">Exact invariant-preserving cut attested by the caller.</param>
    /// <param name="sourceReference">Stable evidence reference for this exact cut.</param>
    /// <returns>Applied stage point after all eligible durable CAS transitions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceReference"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">A pending change cannot be applied or durable CAS conflicts.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async ValueTask<MaterializationIndexSyncStageControlPoint> AtSafePointAsync(
        OperationContext context,
        MaterializationIndexSyncWorkloadKind workload,
        ControlStageKind stage,
        ControlApplicationPointKind kind,
        string sourceReference)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(sourceReference))
            throw new ArgumentException("A materialization safe point requires an evidence reference.", nameof(sourceReference));

        var realizationCount = 0;
        foreach (var realization in plan.ControlRealizations)
        {
            if (realization.Workload == workload && realization.EffectiveDefinition.Stage == stage)
                realizationCount++;
        }
        var snapshots = ImmutableArray.CreateBuilder<MaterializationIndexSyncControlSnapshot>(realizationCount);
        foreach (var realization in plan.ControlRealizations)
        {
            if (realization.Workload != workload || realization.EffectiveDefinition.Stage != stage)
                continue;
            var snapshot = await LoadOrCreateAsync(context, realization).ConfigureAwait(false);
            snapshot = await ApplyIfEligibleAsync(context, snapshot, kind, sourceReference).ConfigureAwait(false);
            snapshots.Add(snapshot);
        }

        return CreateStagePoint(workload, stage, snapshots.MoveToImmutable());
    }

    /// <summary>Acquires a realtime-first non-preemptive stage permit after applying its admission safe point.</summary>
    /// <param name="context">Explicit cancellation, time, and tracing context.</param>
    /// <param name="workload">Explicit governed workload.</param>
    /// <param name="stage">Exact governed stage.</param>
    /// <param name="resource">Stable shared physical resource identity.</param>
    /// <param name="sourceReference">Stable safe-point evidence reference.</param>
    /// <returns>A permit releasing the admitted operation exactly once.</returns>
    /// <exception cref="InvalidOperationException">
    /// The Control state is invalid, the admission contribution was retired or unregistered, or its shared physical
    /// limits conflict with another workload contribution.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled while queued.</exception>
    public async ValueTask<MaterializationIndexSyncAdmissionLease> AcquireStageAsync(
        OperationContext context,
        MaterializationIndexSyncWorkloadKind workload,
        ControlStageKind stage,
        string resource,
        string sourceReference)
    {
        var current = await AtSafePointAsync(
                context,
                workload,
                stage,
                ControlApplicationPointKind.WorkAdmissionBoundary,
                sourceReference)
            .ConfigureAwait(false);
        var totalMaximum = FixedConcurrencyMaximum(stage);
        var realtimeReservation = RealtimeConcurrencyReservation(stage);
        if (realtimeReservation >= totalMaximum)
        {
            throw new InvalidOperationException(
                $"Realtime reservation {realtimeReservation} leaves no rebuild surplus on '{stage}' capacity {totalMaximum}.");
        }
        var admissionResource = new MaterializationIndexSyncAdmissionResource(stage, resource);
        var contribution = new MaterializationIndexSyncAdmissionContribution(
            plan.Fingerprint,
            plan.Target.Id,
            generation,
            workload,
            stage,
            totalMaximum,
            maximumConcurrency: Math.Min(current.MaximumConcurrency, totalMaximum),
            realtimeReservation,
            current.Snapshots);
        var limits = admission.ApplyContribution(
            admissionResource,
            contribution);
        return await admission.AcquireAsync(
                admissionResource,
                workload,
                limits,
                contribution.Key,
                context.CancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads applied target batch bounds at an exact batch safe point.</summary>
    /// <param name="context">Explicit cancellation, time, and tracing context.</param>
    /// <param name="workload">Explicit governed workload.</param>
    /// <param name="sourceReference">Stable safe-point evidence reference.</param>
    /// <returns>Currently applied target item and byte limits.</returns>
    internal async ValueTask<MaterializationTargetBatchOperatingLimits> ResolveTargetBatchLimitsAsync(
        OperationContext context,
        MaterializationIndexSyncWorkloadKind workload,
        string sourceReference)
    {
        var point = await AtSafePointAsync(
                context,
                workload,
                ControlStageKind.Target,
                ControlApplicationPointKind.BatchBoundary,
                sourceReference)
            .ConfigureAwait(false);
        return new(point.MaximumBatchItems, point.MaximumBatchBytes);
    }

    async ValueTask<MaterializationIndexSyncControlSnapshot> ObserveStageRealizationAsync(
        OperationContext context,
        MaterializationIndexSyncControlRealization realization,
        DateTimeOffset windowStartedAtUtc,
        DateTimeOffset windowEndedAtUtc,
        DateTimeOffset observedAtUtc,
        string source,
        string evidenceReference,
        ImmutableArray<ControlMeasurement> measurements)
    {
        while (true)
        {
            var snapshot = await LoadOrCreateAsync(context, realization).ConfigureAwait(false);
            var evidenceFingerprint = Fingerprint(new StageObservationEvidence(
                ControlLoopDefinition.CurrentSchemaVersion,
                realization.EffectiveDefinition.Id,
                realization.EffectiveDefinition.Fingerprint,
                realization.EffectiveDefinition.Target,
                snapshot.Key.Epoch,
                windowStartedAtUtc,
                windowEndedAtUtc,
                observedAtUtc,
                source,
                evidenceReference,
                measurements));
            var observationId = new ControlObservationId(
                $"materialization-control-observation/v2/{MaterializationStableIdentity.Digest(
                    snapshot.Key.Epoch.Value,
                    realization.EffectiveDefinition.Id.Value,
                    source,
                    evidenceReference)}");
            var mutationId = $"observation/{observationId.Value}";
            var replay = await store.ReadMutationAsync(
                    context,
                    snapshot.Key,
                    mutationId,
                    evidenceFingerprint)
                .ConfigureAwait(false);
            if (replay.Disposition == MaterializationIndexSyncControlWriteDisposition.Replayed)
                return Snapshot(realization, replay.State!);
            if (replay.Disposition == MaterializationIndexSyncControlWriteDisposition.IdentityConflict)
            {
                throw new InvalidOperationException(
                    $"Control adapter evidence identity '{evidenceReference}' was reused for different canonical evidence.");
            }

            var observation = new ControlObservation(
                ControlLoopDefinition.CurrentSchemaVersion,
                observationId,
                realization.EffectiveDefinition.Id,
                realization.EffectiveDefinition.Fingerprint,
                realization.EffectiveDefinition.Target,
                snapshot.Key.Epoch,
                snapshot.State.Revision,
                windowStartedAtUtc,
                windowEndedAtUtc,
                observedAtUtc,
                source,
                measurements);
            var decision = AimdControlReferenceRegulator.Evaluate(
                realization.EffectiveDefinition,
                snapshot.State,
                observation,
                context.UtcNow);
            if (decision.Disposition == ControlDecisionDisposition.Rejected)
                throw Failure("Control observation was rejected", decision.Diagnostics);
            if (decision.Disposition == ControlDecisionDisposition.Replayed)
                return snapshot;

            var write = await store.CompareExchangeAsync(
                    context,
                    snapshot.Key,
                    mutationId,
                    evidenceFingerprint,
                    snapshot.State.Revision,
                    decision.State)
                .ConfigureAwait(false);
            if (write.Disposition is MaterializationIndexSyncControlWriteDisposition.Applied
                or MaterializationIndexSyncControlWriteDisposition.Replayed)
            {
                return Snapshot(realization, write.State!);
            }
            if (write.Disposition == MaterializationIndexSyncControlWriteDisposition.RevisionConflict)
                continue;
            throw new InvalidOperationException(
                $"Control adapter-observation CAS failed with '{write.Disposition}'.");
        }
    }

    async ValueTask<MaterializationIndexSyncControlSnapshot> ApplyIfEligibleAsync(
        OperationContext context,
        MaterializationIndexSyncControlSnapshot snapshot,
        ControlApplicationPointKind kind,
        string sourceReference)
    {
        while (true)
        {
            var state = snapshot.State;
            var definition = snapshot.Realization.EffectiveDefinition;
            var operatorEligible = state.PendingLimitUpdate is { } pending
                && pending.AcceptedAtUtc < context.UtcNow
                && ControlLimitUpdateReferenceReducer.TryGetApplicationKind(
                    state.OperatingPoint,
                    pending.Command.RequestedOperatingPoint,
                    out var operatorKind,
                    out _)
                && operatorKind == kind;
            var adaptiveEligible = state.PendingRecommendation is { } recommendation
                && ControlApplicationPointCatalog.ForActuator(recommendation.Actuator) == kind;
            if (!operatorEligible && !adaptiveEligible)
                return snapshot;

            var nextFence = new ControlApplicationFence(
                ((state.LastApplicationFence?.Ordinal ?? 0) + 1).ToString(CultureInfo.InvariantCulture));
            var pointId = new ControlApplicationPointId(
                $"materialization-control-point/v1/{MaterializationStableIdentity.Digest(
                    snapshot.Key.Epoch.Value,
                    state.Revision.Value,
                    nextFence.Value,
                    kind.ToString(),
                    sourceReference)}");
            var point = new ControlApplicationPoint(
                ControlLoopDefinition.CurrentSchemaVersion,
                pointId,
                state.LoopId,
                state.DefinitionFingerprint,
                state.Target,
                state.Epoch,
                state.Revision,
                nextFence,
                kind,
                context.UtcNow,
                definition.ApplicationAuthority,
                sourceReference);

            ControlLoopState next;
            if (operatorEligible)
            {
                var result = ControlLimitUpdateReferenceReducer.Apply(definition, state, point, context.UtcNow);
                if (result.Disposition != ControlActuationDisposition.Applied)
                    throw Failure("Operator Control update was not applied at its exact safe point", result.Diagnostics);
                next = result.State;
            }
            else
            {
                var result = AimdControlReferenceRegulator.Apply(definition, state, point, context.UtcNow);
                if (result.Disposition != ControlActuationDisposition.Applied)
                    throw Failure("Adaptive Control recommendation was not applied at its exact safe point", result.Diagnostics);
                next = result.State;
            }

            var write = await store.CompareExchangeAsync(
                    context,
                    snapshot.Key,
                    $"application/{point.Id.Value}",
                    Fingerprint(point),
                    state.Revision,
                    next)
                .ConfigureAwait(false);
            if (write.Disposition is MaterializationIndexSyncControlWriteDisposition.Applied
                or MaterializationIndexSyncControlWriteDisposition.Replayed)
            {
                return Snapshot(snapshot.Realization, write.State!);
            }
            if (write.Disposition == MaterializationIndexSyncControlWriteDisposition.RevisionConflict)
            {
                snapshot = Snapshot(snapshot.Realization, write.State!);
                continue;
            }
            if (write.Disposition == MaterializationIndexSyncControlWriteDisposition.IdentityConflict
                && write.State is { } committed
                && HasApplicationPoint(committed, point.Id))
            {
                return Snapshot(snapshot.Realization, committed);
            }
            throw new InvalidOperationException(
                $"Control safe-point CAS failed with '{write.Disposition}'.");
        }
    }

    MaterializationIndexSyncStageControlPoint CreateStagePoint(
        MaterializationIndexSyncWorkloadKind workload,
        ControlStageKind stage,
        ImmutableArray<MaterializationIndexSyncControlSnapshot> snapshots)
    {
        var concurrency = FixedConcurrencyMaximum(stage);
        var items = stage == ControlStageKind.Target
            ? plan.Limits.MaximumBulkItems
            : plan.Limits.MaximumPageItems;
        var bytes = stage == ControlStageKind.Target
            ? plan.Limits.MaximumBulkBytes
            : plan.Limits.MaximumPageBytes;
        foreach (var snapshot in snapshots)
        {
            foreach (var value in snapshot.State.OperatingPoint.Values)
            {
                switch (value.Actuator)
                {
                    case ControlActuatorKind.Concurrency:
                        concurrency = checked((int)Math.Min(concurrency, value.Quantity.Value));
                        break;
                    case ControlActuatorKind.BatchItems:
                        items = checked((int)Math.Min(items, value.Quantity.Value));
                        break;
                    case ControlActuatorKind.BatchBytes:
                        bytes = Math.Min(bytes, value.Quantity.Value);
                        break;
                }
            }
        }
        return new(workload, stage, concurrency, items, bytes, snapshots);
    }

    int FixedConcurrencyMaximum(ControlStageKind stage) =>
        MaterializationIndexSyncControlCompiler.GetPhysicalConcurrencyMaximum(
            stage,
            plan.Sources,
            plan.Target,
            plan.Limits);

    int RealtimeConcurrencyReservation(ControlStageKind stage)
    {
        foreach (var realization in plan.ControlRealizations)
        {
            if (realization.Workload != MaterializationIndexSyncWorkloadKind.Rebuild
                || realization.EffectiveDefinition.Stage != stage)
            {
                continue;
            }
            foreach (var budget in realization.EffectiveDefinition.Budgets)
            {
                if (budget.Actuator == ControlActuatorKind.Concurrency)
                    return checked((int)budget.Reserved.Value);
            }
        }
        return 0;
    }

    async ValueTask<MaterializationIndexSyncControlSnapshot> LoadOrCreateAsync(
        OperationContext context,
        MaterializationIndexSyncControlRealization realization)
    {
        var key = Key(realization);
        var state = await store.ReadAsync(context, key).ConfigureAwait(false);
        if (state is null)
        {
            var initial = authorityScope is null
                ? ControlLoopState.Create(realization.EffectiveDefinition, key.Epoch, context.UtcNow)
                : ControlLoopState.Create(realization.EffectiveDefinition, key.Epoch, authorityScope, context.UtcNow);
            var created = await store.CreateAsync(
                    context,
                    key,
                    $"initialize/{key.Epoch.Value}/{Guid.NewGuid():N}",
                    Fingerprint(initial),
                    initial)
                .ConfigureAwait(false);
            state = created.Disposition switch
            {
                MaterializationIndexSyncControlWriteDisposition.Applied
                    or MaterializationIndexSyncControlWriteDisposition.Replayed
                    or MaterializationIndexSyncControlWriteDisposition.RevisionConflict => created.State,
                _ => throw new InvalidOperationException(
                    $"Control state initialization failed with '{created.Disposition}'.")
            };
        }
        return Snapshot(realization, state!);
    }

    MaterializationIndexSyncControlStateKey Key(MaterializationIndexSyncControlRealization realization) => new(
        plan.Materialization.Definition.Id,
        plan.Materialization.DefinitionFingerprint,
        realization.EffectiveDefinition.Fingerprint,
        plan.Fingerprint,
        plan.Target.Id,
        generation,
        realization.Workload,
        realization.EffectiveDefinition.Id);

    MaterializationIndexSyncControlSnapshot Snapshot(
        MaterializationIndexSyncControlRealization realization,
        ControlLoopState state) =>
        MaterializationIndexSyncControlSnapshot.Create(plan, generation, realization, state);

    MaterializationIndexSyncControlRealization Find(
        MaterializationIndexSyncWorkloadKind workload,
        ControlLoopId loopId)
    {
        foreach (var realization in plan.ControlRealizations)
        {
            if (realization.Workload == workload && realization.EffectiveDefinition.Id == loopId)
                return realization;
        }
        throw new ArgumentException(
            $"Plan has no '{workload}' Control realization for loop '{loopId.Value}'.",
            nameof(loopId));
    }

    MaterializationIndexSyncControlRealization FindExact(
        MaterializationIndexSyncWorkloadKind workload,
        ControlLoopId loopId,
        string target,
        ControlEpochId epoch)
    {
        foreach (var realization in plan.ControlRealizations)
        {
            if (realization.Workload == workload
                && realization.EffectiveDefinition.Id == loopId
                && string.Equals(realization.EffectiveDefinition.Target, target, StringComparison.Ordinal)
                && Key(realization).Epoch == epoch)
            {
                return realization;
            }
        }
        throw new KeyNotFoundException(
            $"Plan has no exact '{workload}' Control realization for loop '{loopId.Value}', target '{target}', and epoch '{epoch.Value}'.");
    }

    static ControlLimitUpdateDecision Unauthorized(ControlLoopState state) => new(
        ControlLoopDefinition.CurrentSchemaVersion,
        ControlLimitUpdateDecisionDisposition.Unauthorized,
        state,
        diagnostics:
        [
            new DocumentValidationDiagnostic(
                ControlDiagnosticCodes.LimitUpdateUnauthorized,
                DiagnosticSeverity.Error,
                "The command authorization scope does not own this controlled loop.",
                "/authorization/authorityScope")
        ]);

    static ControlLimitUpdateCommand CanonicalizeInvocationReplay(
        ControlLoopState state,
        ControlLimitUpdateCommand command)
    {
        var retained = state.FindLimitUpdateReceipt(command.CommandId);
        return retained is null
            ? command
            : new(
                command.SchemaVersion,
                command.CommandId,
                command.IdempotencyKey,
                command.LoopId,
                command.DefinitionFingerprint,
                command.Target,
                command.Epoch,
                command.ExpectedRevision,
                command.RequestedOperatingPoint,
                retained.Command.Authorization,
                retained.Command.IssuedAtUtc,
                retained.Command.Provenance);
    }

    static bool HasApplicationPoint(ControlLoopState state, ControlApplicationPointId pointId)
    {
        if (state.LastActuation?.ApplicationPoint.Id == pointId)
            return true;
        foreach (var actuation in state.LimitUpdateActuations)
        {
            if (actuation.ApplicationPoint.Id == pointId)
                return true;
        }
        return false;
    }

    static string Fingerprint<T>(T value)
        where T : class =>
        Convert.ToHexStringLower(SHA256.HashData(
            StrictDocumentJson.GetCanonicalBytes(value, ControlJsonSerializer.CreateOptions())));

    static InvalidOperationException Failure(
        string message,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics) =>
        new($"{message}: {string.Join(" ", diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"))}");

    sealed record StageObservationEvidence(
        ExecutionIrSchemaVersion SchemaVersion,
        ControlLoopId LoopId,
        ExecutionDefinitionFingerprint DefinitionFingerprint,
        string Target,
        ControlEpochId Epoch,
        DateTimeOffset WindowStartedAtUtc,
        DateTimeOffset WindowEndedAtUtc,
        DateTimeOffset ObservedAtUtc,
        string Source,
        string EvidenceReference,
        ImmutableArray<ControlMeasurement> Measurements);
}
