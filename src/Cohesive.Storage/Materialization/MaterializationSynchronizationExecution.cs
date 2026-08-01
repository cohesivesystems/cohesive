using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

sealed class MaterializationSynchronizationBoundaryException(string message)
    : InvalidOperationException(message);

/// <summary>Stable diagnostics emitted by the reference incremental synchronization interpreter.</summary>
public static class MaterializationSynchronizationDiagnosticCodes
{
    /// <summary>Baseline completion or another required runtime precondition is absent.</summary>
    public const string NotReady = "materialization.synchronization.notReady";

    /// <summary>The exact source feed could not be read or interpreted conclusively.</summary>
    public const string SourceOrImpactFailed = "materialization.synchronization.sourceOrImpact.failed";

    /// <summary>One target mutation failed or exhausted its bounded retry policy.</summary>
    public const string TargetMutationFailed = "materialization.synchronization.target.mutationFailed";

    /// <summary>A source page or target mutation crossed a finite declared operating boundary.</summary>
    public const string OperatingBoundaryExceeded = "materialization.synchronization.boundary.exceeded";

    /// <summary>A progress or synchronization-work owner was superseded.</summary>
    public const string Fenced = "materialization.synchronization.fenced";

    /// <summary>Replay produced content that conflicts with already durable work.</summary>
    public const string ReplayConflict = "materialization.synchronization.replay.conflict";

    /// <summary>Explicit source settlement failed after the exact application checkpoint became durable.</summary>
    public const string SettlementFailed = "materialization.synchronization.settlement.failed";
}

/// <summary>Stable identity of one bounded synchronization activation, retained across exact durable retry.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Cohesive.Model.Serialization.SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationSynchronizationInvocationId
{
    /// <summary>Creates one durable synchronization invocation identity.</summary>
    /// <param name="value">Stable identity reused only for an exact activation retry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or ill-formed Unicode.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public MaterializationSynchronizationInvocationId(string value) =>
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Raw stable invocation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable invocation identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one physical worker activation owning synchronization effects.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationSynchronizationWorkerId
{
    /// <summary>Creates one explicit physical worker identity.</summary>
    /// <param name="value">Non-empty worker or lease identity unique among overlapping activations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationSynchronizationWorkerId(string value) =>
        Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw physical worker identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw worker identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Bounded outcome of one independently checkpointed incremental feed invocation.</summary>
public enum MaterializationCatchUpFeedDisposition
{
    /// <summary>The source reported a current caught-up boundary and exact durable evidence was retained.</summary>
    CaughtUp = 0,

    /// <summary>The invocation advanced durable work but reached its finite page budget before catch-up.</summary>
    WorkRemaining = 1,

    /// <summary>The candidate baseline or target generation was not in a writable synchronization state.</summary>
    NotReady = 2,

    /// <summary>Source acquisition, impact resolution, or Relations hydration failed conclusively.</summary>
    SourceOrImpactFailed = 3,

    /// <summary>Target application or explicit source settlement failed under the declared bounded policy.</summary>
    TargetOrSettlementFailed = 4,

    /// <summary>A source page or target mutation exceeded a finite operating bound.</summary>
    BoundaryExceeded = 5,

    /// <summary>A newer progress or synchronization worker fence superseded this invocation.</summary>
    Fenced = 6,

    /// <summary>Exact replay content diverged, so the owning Process attempt must restart with a new generation.</summary>
    RestartRequired = 7
}

/// <summary>Typed terminal evidence for one bounded change-feed synchronization invocation.</summary>
public sealed record MaterializationCatchUpFeedResult
{
    /// <summary>Creates one feed invocation result.</summary>
    /// <param name="disposition">Observable terminal disposition.</param>
    /// <param name="feed">Stable persisted feed identity.</param>
    /// <param name="generation">Candidate or active generation receiving work.</param>
    /// <param name="pagesRead">Number of bounded source pages read by this invocation.</param>
    /// <param name="mutationsApplied">Number of concrete target item mutations completed by this invocation.</param>
    /// <param name="progress">Latest exact durable application progress, when available.</param>
    /// <param name="evidence">Caught-up evidence, present exactly for <see cref="MaterializationCatchUpFeedDisposition.CaughtUp"/>.</param>
    /// <param name="diagnostics">Structured diagnostics, present exactly for failed dispositions.</param>
    /// <exception cref="ArgumentException">Result evidence contradicts <paramref name="disposition"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="disposition"/> is unsupported or a count is negative.
    /// </exception>
    public MaterializationCatchUpFeedResult(
        MaterializationCatchUpFeedDisposition disposition,
        MaterializationChangeFeedId feed,
        MaterializationGenerationId generation,
        int pagesRead,
        long mutationsApplied,
        MaterializationProgressSnapshot? progress,
        MaterializationCatchUpFeedEvidence? evidence,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported catch-up disposition.");
        MaterializationContract.RequireDefinedIdentity(feed.Value, nameof(feed));
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        if (pagesRead < 0)
            throw new ArgumentOutOfRangeException(nameof(pagesRead), pagesRead, "A page count cannot be negative.");
        if (mutationsApplied < 0)
            throw new ArgumentOutOfRangeException(nameof(mutationsApplied), mutationsApplied, "A mutation count cannot be negative.");
        if (progress is not null && progress.Key.Generation != generation)
            throw new ArgumentException("Feed progress must belong to the exact result generation.", nameof(progress));
        if ((disposition == MaterializationCatchUpFeedDisposition.CaughtUp) != (evidence is not null))
            throw new ArgumentException("Exactly a caught-up result requires source-head evidence.", nameof(evidence));
        if (evidence is not null && (evidence.Feed != feed || evidence.Scope != progress?.Key.Scope))
            throw new ArgumentException("Caught-up evidence must identify the exact result feed and progress scope.", nameof(evidence));

        var normalized = MaterializationContract.NormalizeDiagnostics(diagnostics, nameof(diagnostics));
        var failed = disposition is not (
            MaterializationCatchUpFeedDisposition.CaughtUp
            or MaterializationCatchUpFeedDisposition.WorkRemaining);
        if (failed == normalized.IsDefaultOrEmpty)
            throw new ArgumentException("Exactly a failed catch-up result requires diagnostics.", nameof(diagnostics));

        Disposition = disposition;
        Feed = feed;
        Generation = generation;
        PagesRead = pagesRead;
        MutationsApplied = mutationsApplied;
        Progress = progress;
        Evidence = evidence;
        Diagnostics = normalized;
    }

    /// <summary>Observable terminal disposition.</summary>
    public MaterializationCatchUpFeedDisposition Disposition { get; }

    /// <summary>Stable persisted feed identity.</summary>
    public MaterializationChangeFeedId Feed { get; }

    /// <summary>Candidate or active generation receiving work.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Number of bounded source pages read by this invocation.</summary>
    public int PagesRead { get; }

    /// <summary>Number of concrete target item mutations completed by this invocation.</summary>
    public long MutationsApplied { get; }

    /// <summary>Latest exact durable application progress, when available.</summary>
    public MaterializationProgressSnapshot? Progress { get; }

    /// <summary>Caught-up source-head evidence, when <see cref="Disposition"/> is caught up.</summary>
    public MaterializationCatchUpFeedEvidence? Evidence { get; }

    /// <summary>Structured deterministic failure diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>Outcome of one bounded all-feed convergence activation.</summary>
public enum MaterializationSynchronizationRunDisposition
{
    /// <summary>Every planned feed is caught up and a fresh catalog-complete receipt was produced.</summary>
    Converged = 0,

    /// <summary>At least one feed advanced but requires another bounded activation.</summary>
    WorkRemaining = 1,

    /// <summary>The baseline or generation is not ready for synchronization.</summary>
    NotReady = 2,

    /// <summary>Source, impact, target, settlement, or proof validation failed.</summary>
    Failed = 3,

    /// <summary>A newer durable worker fence superseded this activation.</summary>
    Fenced = 4,

    /// <summary>Exact replay diverged and the owning Process attempt must restart.</summary>
    RestartRequired = 5
}

/// <summary>Catalog-wide bounded synchronization evidence.</summary>
public sealed record MaterializationSynchronizationRunResult
{
    /// <summary>Creates one all-feed synchronization result.</summary>
    /// <param name="disposition">Observable all-feed disposition.</param>
    /// <param name="generation">Exact candidate or active generation.</param>
    /// <param name="feeds">Feed results produced in canonical plan order.</param>
    /// <param name="receipt">Catalog-complete convergence receipt, present exactly when converged.</param>
    /// <param name="diagnostics">Structured diagnostics for a failed, fenced, or restart-required result.</param>
    /// <exception cref="ArgumentException">Result evidence contradicts <paramref name="disposition"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    public MaterializationSynchronizationRunResult(
        MaterializationSynchronizationRunDisposition disposition,
        MaterializationGenerationId generation,
        ImmutableArray<MaterializationCatchUpFeedResult> feeds,
        MaterializationConvergenceReceipt? receipt,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported synchronization disposition.");
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        var normalizedFeeds = feeds.IsDefault ? [] : feeds;
        if (normalizedFeeds.Any(static feed => feed is null)
            || normalizedFeeds.Any(feed => feed.Generation != generation))
        {
            throw new ArgumentException("Synchronization feed results must be non-null and generation-exact.", nameof(feeds));
        }
        if ((disposition == MaterializationSynchronizationRunDisposition.Converged) != (receipt is not null))
            throw new ArgumentException("Exactly a converged synchronization result requires a receipt.", nameof(receipt));
        if (receipt is not null && receipt.Generation != generation)
            throw new ArgumentException("A convergence receipt must belong to the exact result generation.", nameof(receipt));

        var normalizedDiagnostics = MaterializationContract.NormalizeDiagnostics(diagnostics, nameof(diagnostics));
        var failure = disposition is MaterializationSynchronizationRunDisposition.NotReady
            or MaterializationSynchronizationRunDisposition.Failed
            or MaterializationSynchronizationRunDisposition.Fenced
            or MaterializationSynchronizationRunDisposition.RestartRequired;
        if (failure == normalizedDiagnostics.IsDefaultOrEmpty)
            throw new ArgumentException("Exactly a failed synchronization result requires diagnostics.", nameof(diagnostics));

        Disposition = disposition;
        Generation = generation;
        Feeds = normalizedFeeds;
        Receipt = receipt;
        Diagnostics = normalizedDiagnostics;
    }

    /// <summary>Observable all-feed disposition.</summary>
    public MaterializationSynchronizationRunDisposition Disposition { get; }

    /// <summary>Exact candidate or active generation.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Feed results produced in canonical plan order.</summary>
    public ImmutableArray<MaterializationCatchUpFeedResult> Feeds { get; }

    /// <summary>Catalog-complete convergence receipt, when converged.</summary>
    public MaterializationConvergenceReceipt? Receipt { get; }

    /// <summary>Structured failure diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>Deterministic identities owned by one exact synchronization plan and Process attempt.</summary>
static class MaterializationSynchronizationIdentities
{
    const string Prefix = "materialization-synchronization/v1";

    internal static MaterializationProgressMutationId WorkFence(
        MaterializationSynchronizationWorkKey key,
        MaterializationRebuildAttempt attempt,
        MaterializationProgressRevision? revision,
        string owner) =>
        new($"{Prefix}/work-fence/{MaterializationStableIdentity.Digest(
            key.RebuildPlanFingerprint.Value,
            key.ImpactPlanFingerprint.Value,
            key.Generation.Value,
            attempt.Continuation.ProcessInstanceId.Value,
            attempt.Continuation.ProcessAttemptId.Value,
            revision?.Value ?? "absent",
            owner)}");

    internal static MaterializationProgressMutationId Preparation(
        MaterializationSynchronizationWorkKey key,
        MaterializationChangeFeedPlan feed,
        MaterializationSynchronizationInvocationId invocation,
        MaterializationSourcePosition afterPosition,
        DateTimeOffset readStartedAtUtc) =>
        new($"{Prefix}/prepare/{MaterializationStableIdentity.Digest(
            key.RebuildPlanFingerprint.Value,
            key.Generation.Value,
            feed.Id.Value,
            invocation.Value,
            afterPosition.FormatVersion.ToString(CultureInfo.InvariantCulture),
            afterPosition.Value,
            readStartedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture))}");

    internal static MaterializationCheckpointId Checkpoint(MaterializationProgressMutationId preparation) =>
        new($"{Prefix}/checkpoint/{MaterializationStableIdentity.Digest(preparation.Value)}");

    internal static MaterializationProgressMutationId CheckpointMutation(MaterializationCheckpointId checkpoint) =>
        new($"{Prefix}/checkpoint-mutation/{MaterializationStableIdentity.Digest(checkpoint.Value)}");

    internal static MaterializationProgressMutationId Completion(
        MaterializationPreparedSynchronizationWork work,
        string owner,
        MaterializationProgressFence fence) =>
        new($"{Prefix}/complete/{MaterializationStableIdentity.Digest(
            work.PreparationId.Value,
            work.Version?.Value ?? "effect-free",
            owner,
            fence.Value)}");

    internal static MaterializationSettlementId Settlement(MaterializationApplicationCheckpoint checkpoint) =>
        new($"{Prefix}/settlement/{MaterializationStableIdentity.Digest(checkpoint.Id.Value)}");

    internal static MaterializationProgressMutationId SettlementMutation(
        MaterializationApplicationCheckpoint checkpoint) =>
        new($"{Prefix}/settlement-mutation/{MaterializationStableIdentity.Digest(checkpoint.Id.Value)}");

    internal static MaterializationBatchId Batch(
        MaterializationPreparedSynchronizationWork work,
        int chunk,
        int retry) =>
        new($"{Prefix}/batch/{MaterializationStableIdentity.Digest(
            work.PreparationId.Value,
            work.Version?.Value ?? "effect-free",
            chunk.ToString(CultureInfo.InvariantCulture),
            retry.ToString(CultureInfo.InvariantCulture))}");
}

/// <summary>
/// Storage-owned reference interpreter for bounded catch-up and post-promotion real-time maintenance.
/// </summary>
/// <remarks>
/// The interpreter serializes generation-wide target versions through
/// <see cref="IMaterializationSynchronizationWorkStore"/> while each source scope retains independent application
/// checkpoints and settlement. For each page it durably prepares exact target intent, applies effects, persists the
/// application checkpoint, completes prepared work, and only then settles the source. An unfinished settlement is
/// always drained before another read from that scope.
/// </remarks>
public sealed class MaterializationSynchronizationExecutor
{
    static readonly JsonSerializerOptions CanonicalJsonOptions = MaterializationJsonSerializer.CreateOptions();

    readonly ResolvedMaterializationRebuildPlan resolved;
    readonly IMaterializationSynchronizationWorkStore workStore;

    /// <summary>Creates a synchronization executor over exact runtime bindings and one durable work authority.</summary>
    /// <param name="resolved">Exact persisted plan resolved to source, Relations, progress, and target ports.</param>
    /// <param name="workStore">Generation-wide durable target-work and item-version authority.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MaterializationSynchronizationExecutor(
        ResolvedMaterializationRebuildPlan resolved,
        IMaterializationSynchronizationWorkStore workStore)
    {
        this.resolved = resolved ?? throw new ArgumentNullException(nameof(resolved));
        this.workStore = workStore ?? throw new ArgumentNullException(nameof(workStore));
    }

    /// <summary>Exact persisted synchronization plan interpreted by this executor.</summary>
    public MaterializationRebuildPlan Plan => resolved.Plan;

    /// <summary>Creates the exact generation-wide work key for one Process attempt.</summary>
    /// <param name="attempt">Exact Process attempt owning the candidate generation.</param>
    /// <returns>The definition-, plan-, impact-, and generation-fenced synchronization key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attempt"/> is <see langword="null"/>.</exception>
    public MaterializationSynchronizationWorkKey GetWorkKey(MaterializationRebuildAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var plan = resolved.Plan;
        return new(
            materialization: plan.Materialization.Definition.Id,
            definitionFingerprint: plan.Materialization.DefinitionFingerprint,
            rebuildPlanFingerprint: plan.Fingerprint,
            impactPlanFingerprint: plan.ImpactPlan.Fingerprint,
            generation: MaterializationRebuildIdentities.Generation(plan, attempt));
    }

    /// <summary>Runs every planned feed once and produces a fresh catalog-complete convergence receipt when possible.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="attempt">Exact Process attempt retaining the generation across continuation.</param>
    /// <param name="invocation">Stable bounded activation identity retained across durable retry.</param>
    /// <param name="worker">Explicit physical worker or lease identity used to fence overlapping activations.</param>
    /// <returns>Converged, work-remaining, failed, fenced, or restart-required catalog evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="attempt"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<MaterializationSynchronizationRunResult> ConvergeAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationSynchronizationInvocationId invocation,
        MaterializationSynchronizationWorkerId worker)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(attempt);
        MaterializationContract.RequireDefinedIdentity(invocation.Value, nameof(invocation));
        MaterializationContract.RequireDefinedIdentity(worker.Value, nameof(worker));
        context.ThrowIfCancellationRequested();
        var generation = MaterializationRebuildIdentities.Generation(resolved.Plan, attempt);
        var workerOwner = CreateWorkerOwner(attempt, invocation, worker);
        var results = ImmutableArray.CreateBuilder<MaterializationCatchUpFeedResult>(resolved.Plan.ChangeFeeds.Length);
        foreach (var feed in resolved.Plan.ChangeFeeds)
        {
            var result = await RunFeedAsync(context, attempt, feed.Id, invocation, workerOwner).ConfigureAwait(false);
            results.Add(result);
        }

        var evaluatedAtUtc = context.UtcNow;
        var complete = results.MoveToImmutable();
        if (complete.Any(static result =>
                result.Disposition is not MaterializationCatchUpFeedDisposition.CaughtUp))
        {
            var disposition = AggregateDisposition(complete);
            return new(
                disposition,
                generation,
                complete,
                receipt: null,
                diagnostics: [.. complete.SelectMany(static result => result.Diagnostics)]);
        }

        var receipt = new MaterializationConvergenceReceipt(
            schemaVersion: MaterializationConvergenceReceipt.CurrentSchemaVersion,
            synchronization: GetWorkKey(attempt),
            feeds: [.. complete.Select(static result => result.Evidence!)],
            evaluatedAtUtc,
            freshnessDemand: resolved.Plan.Materialization.Definition.FreshnessPolicy,
            validation: DocumentValidationResult.Valid);
        var validation = receipt.ValidateAgainst(resolved.Plan, evaluatedAtUtc);
        if (!validation.IsValid)
        {
            return new(
                MaterializationSynchronizationRunDisposition.Failed,
                generation,
                complete,
                receipt: null,
                validation.Diagnostics);
        }
        return new(
            MaterializationSynchronizationRunDisposition.Converged,
            generation,
            complete,
            receipt);
    }

    /// <summary>Runs or resumes one change feed through a finite page budget.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="attempt">Exact Process attempt retaining the generation across pause and continuation.</param>
    /// <param name="feedId">Stable persisted feed identity.</param>
    /// <param name="invocation">Stable bounded activation identity retained across durable retry.</param>
    /// <param name="worker">Explicit physical worker or lease identity used to fence overlapping activations.</param>
    /// <returns>Caught-up, work-remaining, failed, fenced, or restart-required evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="attempt"/> is null.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="feedId"/> is absent from the plan.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public Task<MaterializationCatchUpFeedResult> RunFeedAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationChangeFeedId feedId,
        MaterializationSynchronizationInvocationId invocation,
        MaterializationSynchronizationWorkerId worker)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(attempt);
        MaterializationContract.RequireDefinedIdentity(invocation.Value, nameof(invocation));
        MaterializationContract.RequireDefinedIdentity(worker.Value, nameof(worker));
        return RunFeedAsync(
            context,
            attempt,
            feedId,
            invocation,
            CreateWorkerOwner(attempt, invocation, worker));
    }

    async Task<MaterializationCatchUpFeedResult> RunFeedAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationChangeFeedId feedId,
        MaterializationSynchronizationInvocationId invocation,
        string workerOwner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(attempt);
        MaterializationContract.RequireDefinedIdentity(invocation.Value, nameof(invocation));
        context.ThrowIfCancellationRequested();

        var plan = resolved.Plan;
        var feed = plan.ChangeFeeds.Single(candidate => candidate.Id == feedId);
        var binding = resolved.GetChangeFeed(feedId);
        var generation = MaterializationRebuildIdentities.Generation(plan, attempt);
        var generationSnapshot = await resolved.Target.InspectGenerationAsync(context, generation).ConfigureAwait(false);
        if (generationSnapshot is null
            || generationSnapshot.MaterializationId != plan.Materialization.Definition.Id
            || generationSnapshot.DefinitionFingerprint != plan.Materialization.DefinitionFingerprint
            || generationSnapshot.State is not (MaterializationGenerationState.Loading or MaterializationGenerationState.Active))
        {
            return Failure(
                MaterializationCatchUpFeedDisposition.NotReady,
                feed,
                generation,
                pagesRead: 0,
                mutationsApplied: 0,
                progress: null,
                MaterializationSynchronizationDiagnosticCodes.NotReady,
                "The exact generation is absent or does not accept incremental synchronization work.");
        }

        if (generationSnapshot.State == MaterializationGenerationState.Loading
            && !await HasCompleteBaselineAsync(context, generation).ConfigureAwait(false))
        {
            return Failure(
                MaterializationCatchUpFeedDisposition.NotReady,
                feed,
                generation,
                pagesRead: 0,
                mutationsApplied: 0,
                progress: null,
                MaterializationSynchronizationDiagnosticCodes.NotReady,
                "Incremental candidate catch-up requires exact completion of every baseline shard.");
        }

        var workKey = GetWorkKey(attempt);
        var work = await AcquireWorkFenceAsync(context, workKey, attempt, workerOwner).ConfigureAwait(false);
        if (work is null)
        {
            return Failure(
                MaterializationCatchUpFeedDisposition.Fenced,
                feed,
                generation,
                pagesRead: 0,
                mutationsApplied: 0,
                progress: null,
                MaterializationSynchronizationDiagnosticCodes.Fenced,
                "The generation-wide synchronization-work fence could not be acquired.");
        }

        long mutationsApplied = 0;
        var recovered = await DrainPendingWorkAsync(context, attempt, workKey, work).ConfigureAwait(false);
        if (recovered.Failure is not null)
            return recovered.Failure;
        work = recovered.Work;
        mutationsApplied = checked(mutationsApplied + recovered.MutationsApplied);
        if (recovered.CaughtUpEvidence is { } recoveredEvidence && recoveredEvidence.Feed == feedId)
        {
            var recoveredProgress = await resolved.ProgressStore.LoadAsync(
                    context,
                    MaterializationRebuildExecutor.ProgressKey(plan, generation, feed.Scope))
                .ConfigureAwait(false);
            return Success(
                feed,
                generation,
                pagesRead: 0,
                mutationsApplied,
                recoveredProgress!,
                recoveredEvidence);
        }

        var progressKey = MaterializationRebuildExecutor.ProgressKey(plan, generation, feed.Scope);
        var owner = MaterializationRebuildExecutor.Owner(attempt, feed.Scope);
        var progress = await resolved.ProgressStore.LoadAsync(context, progressKey).ConfigureAwait(false);
        if (progress is null
            || !string.Equals(progress.FenceOwner, owner, StringComparison.Ordinal)
            || progress.LatestChangeCheckpoint is not { Position: { } afterPosition })
        {
            return Failure(
                MaterializationCatchUpFeedDisposition.NotReady,
                feed,
                generation,
                pagesRead: 0,
                mutationsApplied,
                progress,
                MaterializationSynchronizationDiagnosticCodes.NotReady,
                "The feed lacks its exact initialized change cut or current progress ownership.");
        }

        var settled = await DrainSettlementAsync(context, feed, binding, progress, owner).ConfigureAwait(false);
        if (settled.Failure is not null)
            return WithCounts(settled.Failure, pagesRead: 0, mutationsApplied);
        progress = settled.Progress;
        afterPosition = progress.LatestChangeCheckpoint!.Position!;

        var pagesRead = 0;
        while (pagesRead < plan.Limits.MaximumPagesPerShard)
        {
            context.ThrowIfCancellationRequested();
            var readStartedAtUtc = context.UtcNow;
            MaterializationChangePage page;
            try
            {
                page = await binding.Source.ReadChangesAsync(
                        context,
                        new MaterializationChangeReadRequest(
                            scope: feed.Scope,
                            afterPosition,
                            maximumDeliveries: plan.Limits.MaximumPageItems,
                            maximumBytes: plan.Limits.MaximumPageBytes))
                    .ConfigureAwait(false);
                ValidateOrdinaryPageBounds(feed, binding.Source, page, plan.Limits);
                if (page.ThroughPosition.Scope != feed.Scope)
                {
                    throw new InvalidOperationException(
                        "The source returned a change position outside the exact persisted feed scope.");
                }
                if (page.ThroughPosition == afterPosition
                    && (!page.Deliveries.IsDefaultOrEmpty
                        || page.State is not MaterializationChangePageState.CaughtUp))
                {
                    throw new InvalidOperationException(
                        "A source page may retain its requested position only when it is empty and caught up.");
                }
            }
            catch (MaterializationSynchronizationBoundaryException exception)
            {
                return Failure(
                    MaterializationCatchUpFeedDisposition.BoundaryExceeded,
                    feed,
                    generation,
                    pagesRead,
                    mutationsApplied,
                    progress,
                    MaterializationSynchronizationDiagnosticCodes.OperatingBoundaryExceeded,
                    exception.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Failure(
                    MaterializationCatchUpFeedDisposition.SourceOrImpactFailed,
                    feed,
                    generation,
                    pagesRead,
                    mutationsApplied,
                    progress,
                    MaterializationSynchronizationDiagnosticCodes.SourceOrImpactFailed,
                    exception.Message);
            }
            var readCompletedAtUtc = context.UtcNow;
            pagesRead++;

            var preparationId = MaterializationSynchronizationIdentities.Preparation(
                workKey,
                feed,
                invocation,
                afterPosition,
                readStartedAtUtc);
            var checkpointId = MaterializationSynchronizationIdentities.Checkpoint(preparationId);
            var pageIntent = new MaterializationSynchronizationPageIntent(
                feed: feed.Id,
                checkpoint: checkpointId,
                throughPosition: page.ThroughPosition,
                appliedDeliveries: [.. page.Deliveries.Select(static delivery => delivery.Id)],
                state: page.State,
                readStartedAtUtc,
                readCompletedAtUtc);

            ImmutableArray<MaterializationSynchronizationItemIntent> itemIntents;
            try
            {
                var projections = await binding.Interpreter.InterpretAsync(context, feed, generation, page)
                    .ConfigureAwait(false);
                itemIntents = ProjectItemIntents(projections);
            }
            catch (MaterializationAffectedRootBoundExceededException exception)
            {
                return Failure(
                    MaterializationCatchUpFeedDisposition.BoundaryExceeded,
                    feed,
                    generation,
                    pagesRead,
                    mutationsApplied,
                    progress,
                    MaterializationSynchronizationDiagnosticCodes.OperatingBoundaryExceeded,
                    exception.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Failure(
                    MaterializationCatchUpFeedDisposition.SourceOrImpactFailed,
                    feed,
                    generation,
                    pagesRead,
                    mutationsApplied,
                    progress,
                    MaterializationSynchronizationDiagnosticCodes.SourceOrImpactFailed,
                    exception.Message);
            }

            var preparedResult = await workStore.PrepareAsync(
                    context,
                    workKey,
                    preparationId,
                    work.Revision,
                    work.FenceOwner,
                    work.Fence,
                    new MaterializationSynchronizationWorkIntent(pageIntent, itemIntents))
                .ConfigureAwait(false);
            if (preparedResult.Disposition is not (
                MaterializationSynchronizationWorkMutationDisposition.Applied
                or MaterializationSynchronizationWorkMutationDisposition.Replayed))
            {
                if (preparedResult.Disposition is
                    MaterializationSynchronizationWorkMutationDisposition.RevisionConflict
                    or MaterializationSynchronizationWorkMutationDisposition.PendingWorkConflict)
                {
                    return WorkRemaining(feed, generation, pagesRead, mutationsApplied, progress);
                }
                return WorkStoreFailure(feed, generation, pagesRead, mutationsApplied, progress, preparedResult);
            }

            work = preparedResult.Snapshot!;
            var prepared = preparedResult.PreparedWork
                ?? throw new InvalidOperationException("Successful synchronization preparation omitted durable work.");
            if (work.PendingWork is { } currentPending
                && currentPending.PreparationId != prepared.PreparationId)
            {
                return WorkRemaining(feed, generation, pagesRead, mutationsApplied, progress);
            }
            if (work.PendingWork is null)
            {
                progress = await resolved.ProgressStore.LoadAsync(context, progressKey).ConfigureAwait(false)
                    ?? progress;
                settled = await DrainSettlementAsync(context, feed, binding, progress, owner).ConfigureAwait(false);
                if (settled.Failure is not null)
                    return WithCounts(settled.Failure, pagesRead, mutationsApplied);
                progress = settled.Progress;
                if (prepared.Page.State == MaterializationChangePageState.CaughtUp)
                {
                    return Success(
                        feed,
                        generation,
                        pagesRead,
                        mutationsApplied,
                        progress,
                        CreateEvidence(
                            feed,
                            progress,
                            prepared.Page.ReadStartedAtUtc,
                            prepared.Page.ReadCompletedAtUtc));
                }
                afterPosition = prepared.Page.ThroughPosition;
                continue;
            }

            var applied = await ApplyPreparedWorkAsync(context, prepared, generation, work.Fence).ConfigureAwait(false);
            if (applied is not null)
                return ApplyFailure(feed, generation, pagesRead, mutationsApplied, progress, applied.Value);
            mutationsApplied = checked(mutationsApplied + prepared.Mutations.Length);

            var noProgress = page.Deliveries.IsDefaultOrEmpty
                && page.ThroughPosition == afterPosition;
            if (!noProgress || !prepared.Mutations.IsDefaultOrEmpty)
            {
                var saved = await SavePageCheckpointAsync(
                        context,
                        progressKey,
                        owner,
                        progress,
                        pageIntent)
                    .ConfigureAwait(false);
                if (saved.Disposition is not (
                    MaterializationProgressMutationDisposition.Applied
                    or MaterializationProgressMutationDisposition.Replayed))
                {
                    if (saved.Snapshot is not { } current
                        || !IsExactCheckpoint(current.LatestChangeCheckpoint, pageIntent))
                    {
                        return Failure(
                            saved.Disposition == MaterializationProgressMutationDisposition.IdentityConflict
                                ? MaterializationCatchUpFeedDisposition.RestartRequired
                                : MaterializationCatchUpFeedDisposition.Fenced,
                            feed,
                            generation,
                            pagesRead,
                            mutationsApplied,
                            saved.Snapshot ?? progress,
                            saved.Disposition == MaterializationProgressMutationDisposition.IdentityConflict
                                ? MaterializationSynchronizationDiagnosticCodes.ReplayConflict
                                : MaterializationSynchronizationDiagnosticCodes.Fenced,
                            $"The exact page checkpoint was rejected with '{saved.Disposition}'.");
                    }

                    progress = current;
                }
                else
                {
                    progress = saved.Snapshot!;
                }
            }

            var completed = await CompletePreparedWorkAsync(context, workKey, work, prepared).ConfigureAwait(false);
            if (completed.Disposition is not (
                MaterializationSynchronizationWorkMutationDisposition.Applied
                or MaterializationSynchronizationWorkMutationDisposition.Replayed))
            {
                if (WasCompletedByTakeover(completed, workKey, prepared, progress))
                {
                    return Failure(
                        MaterializationCatchUpFeedDisposition.Fenced,
                        feed,
                        generation,
                        pagesRead,
                        mutationsApplied,
                        progress,
                        MaterializationSynchronizationDiagnosticCodes.Fenced,
                        "Another synchronization worker durably completed the exact checkpointed page.");
                }

                return WorkStoreFailure(feed, generation, pagesRead, mutationsApplied, progress, completed);
            }
            work = completed.Snapshot!;

            settled = await DrainSettlementAsync(context, feed, binding, progress, owner).ConfigureAwait(false);
            if (settled.Failure is not null)
                return WithCounts(settled.Failure, pagesRead, mutationsApplied);
            progress = settled.Progress;

            if (page.State == MaterializationChangePageState.CaughtUp)
            {
                return Success(
                    feed,
                    generation,
                    pagesRead,
                    mutationsApplied,
                    progress,
                    CreateEvidence(feed, progress, readStartedAtUtc, readCompletedAtUtc));
            }

            afterPosition = page.ThroughPosition;
        }

        return new MaterializationCatchUpFeedResult(
            MaterializationCatchUpFeedDisposition.WorkRemaining,
            feed.Id,
            generation,
            pagesRead,
            mutationsApplied,
            progress,
            evidence: null);
    }

    async Task<bool> HasCompleteBaselineAsync(
        OperationContext context,
        MaterializationGenerationId generation)
    {
        foreach (var shard in resolved.Plan.Shards)
        {
            var progress = await resolved.ProgressStore.LoadAsync(
                    context,
                    MaterializationRebuildExecutor.ProgressKey(resolved.Plan, generation, shard.Scope))
                .ConfigureAwait(false);
            if (progress is null
                || !MaterializationRebuildProgressSemantics.IsExactCompletedBaseline(
                    resolved.Plan,
                    shard,
                    generation,
                    progress))
            {
                return false;
            }
        }

        return true;
    }

    async Task<MaterializationSynchronizationWorkSnapshot?> AcquireWorkFenceAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationRebuildAttempt attempt,
        string owner)
    {
        var current = await workStore.LoadAsync(context, key).ConfigureAwait(false);
        if (current?.Activation is { IsComplete: false })
            return null;
        if (current is not null && string.Equals(current.FenceOwner, owner, StringComparison.Ordinal))
            return current;

        var acquired = await workStore.AcquireFenceAsync(
                context,
                key,
                MaterializationSynchronizationIdentities.WorkFence(key, attempt, current?.Revision, owner),
                current?.Revision,
                owner)
            .ConfigureAwait(false);
        return acquired.Disposition is MaterializationSynchronizationWorkMutationDisposition.Applied
                or MaterializationSynchronizationWorkMutationDisposition.Replayed
            ? acquired.Snapshot
            : null;
    }

    async Task<PendingDrainResult> DrainPendingWorkAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationSynchronizationWorkKey key,
        MaterializationSynchronizationWorkSnapshot work)
    {
        if (work.PendingWork is not { } pending)
            return new(work, 0, null, null);

        var feed = resolved.Plan.ChangeFeeds.Single(candidate => candidate.Id == pending.Page.Feed);
        var binding = resolved.GetChangeFeed(feed.Id);
        var generation = key.Generation;
        var progressKey = MaterializationRebuildExecutor.ProgressKey(resolved.Plan, generation, feed.Scope);
        var owner = MaterializationRebuildExecutor.Owner(attempt, feed.Scope);
        var progress = await resolved.ProgressStore.LoadAsync(context, progressKey).ConfigureAwait(false);
        if (progress is null || !string.Equals(progress.FenceOwner, owner, StringComparison.Ordinal))
        {
            return new(
                work,
                0,
                null,
                Failure(
                    MaterializationCatchUpFeedDisposition.Fenced,
                    feed,
                    generation,
                    pagesRead: 0,
                    mutationsApplied: 0,
                    progress,
                    MaterializationSynchronizationDiagnosticCodes.Fenced,
                    "Prepared synchronization work lost its exact source-progress fence."));
        }

        var checkpointAlreadyDurable = IsExactCheckpoint(progress.LatestChangeCheckpoint, pending.Page);
        if (!checkpointAlreadyDurable)
        {
            var applied = await ApplyPreparedWorkAsync(context, pending, generation, work.Fence).ConfigureAwait(false);
            if (applied is not null)
            {
                return new(
                    work,
                    0,
                    null,
                    ApplyFailure(feed, generation, 0, 0, progress, applied.Value));
            }

            var saved = await SavePageCheckpointAsync(
                    context,
                    progressKey,
                    owner,
                    progress,
                    pending.Page)
                .ConfigureAwait(false);
            if (saved.Disposition is not (
                MaterializationProgressMutationDisposition.Applied
                or MaterializationProgressMutationDisposition.Replayed))
            {
                if (saved.Snapshot is not { } current
                    || !IsExactCheckpoint(current.LatestChangeCheckpoint, pending.Page))
                {
                    return new(
                        work,
                        0,
                        null,
                        Failure(
                            saved.Disposition == MaterializationProgressMutationDisposition.IdentityConflict
                                ? MaterializationCatchUpFeedDisposition.RestartRequired
                                : MaterializationCatchUpFeedDisposition.Fenced,
                            feed,
                            generation,
                            pagesRead: 0,
                            mutationsApplied: 0,
                            saved.Snapshot ?? progress,
                            saved.Disposition == MaterializationProgressMutationDisposition.IdentityConflict
                                ? MaterializationSynchronizationDiagnosticCodes.ReplayConflict
                                : MaterializationSynchronizationDiagnosticCodes.Fenced,
                            $"Prepared-work recovery checkpoint was rejected with '{saved.Disposition}'."));
                }

                progress = current;
            }
            else
            {
                progress = saved.Snapshot!;
            }
        }

        var completed = await CompletePreparedWorkAsync(context, key, work, pending).ConfigureAwait(false);
        if (completed.Disposition is not (
            MaterializationSynchronizationWorkMutationDisposition.Applied
            or MaterializationSynchronizationWorkMutationDisposition.Replayed))
        {
            if (WasCompletedByTakeover(completed, key, pending, progress))
            {
                return new(
                    completed.Snapshot!,
                    0,
                    null,
                    Failure(
                        MaterializationCatchUpFeedDisposition.Fenced,
                        feed,
                        generation,
                        pagesRead: 0,
                        mutationsApplied: 0,
                        progress,
                        MaterializationSynchronizationDiagnosticCodes.Fenced,
                        "Another synchronization worker durably completed the exact recovered checkpointed page."));
            }

            return new(
                work,
                0,
                null,
                WorkStoreFailure(feed, generation, 0, 0, progress, completed));
        }
        work = completed.Snapshot!;

        var settled = await DrainSettlementAsync(context, feed, binding, progress, owner).ConfigureAwait(false);
        if (settled.Failure is not null)
            return new(work, pending.Mutations.Length, null, settled.Failure);

        var evidence = pending.Page.State == MaterializationChangePageState.CaughtUp
            ? CreateEvidence(
                feed,
                settled.Progress,
                pending.Page.ReadStartedAtUtc,
                pending.Page.ReadCompletedAtUtc)
            : null;
        return new(work, pending.Mutations.Length, evidence, null);
    }

    async ValueTask<MaterializationTargetWriteResult?> ApplyPreparedWorkAsync(
        OperationContext context,
        MaterializationPreparedSynchronizationWork work,
        MaterializationGenerationId generation,
        MaterializationProgressFence workFence)
    {
        var result = await MaterializationTargetBatchWriter.ApplyAsync(
                context,
                resolved.Target,
                generation,
                new MaterializationWorkerFence(workFence.Value),
                work.Mutations,
                resolved.Plan.Limits.MaximumBulkItems,
                resolved.Plan.Limits.MaximumBulkBytes,
                resolved.Plan.Materialization.Definition.FailurePolicy.MaximumAttempts,
                (chunk, retry) => MaterializationSynchronizationIdentities.Batch(work, chunk, retry))
            .ConfigureAwait(false);
        return result.Disposition == MaterializationTargetWriteDisposition.Applied ? null : result;
    }

    async Task<MaterializationProgressMutationResult> SavePageCheckpointAsync(
        OperationContext context,
        MaterializationProgressKey key,
        string owner,
        MaterializationProgressSnapshot progress,
        MaterializationSynchronizationPageIntent page)
    {
        var checkpoint = new MaterializationApplicationCheckpoint(
            id: page.Checkpoint,
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: page.ThroughPosition,
            appliedDeliveries: page.AppliedDeliveries,
            committedAtUtc: context.UtcNow,
            evidenceReference: page.Feed.Value,
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(page.ThroughPosition));
        return await resolved.ProgressStore.SaveCheckpointAsync(
                context,
                key,
                MaterializationSynchronizationIdentities.CheckpointMutation(page.Checkpoint),
                progress.Revision,
                owner,
                progress.Fence,
                checkpoint)
            .ConfigureAwait(false);
    }

    async Task<MaterializationSynchronizationWorkMutationResult> CompletePreparedWorkAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationSynchronizationWorkSnapshot work,
        MaterializationPreparedSynchronizationWork prepared) =>
        await workStore.CompleteAsync(
                context,
                key,
                MaterializationSynchronizationIdentities.Completion(
                    prepared,
                    owner: work.FenceOwner,
                    fence: work.Fence),
                work.Revision,
                work.FenceOwner,
                work.Fence,
                prepared.PreparationId,
                prepared.Version)
            .ConfigureAwait(false);

    async Task<SettlementDrainResult> DrainSettlementAsync(
        OperationContext context,
        MaterializationChangeFeedPlan feed,
        MaterializationChangeFeedBinding binding,
        MaterializationProgressSnapshot progress,
        string owner)
    {
        if (!RequiresExplicitSettlement(feed))
            return new(progress, null);
        var checkpoint = progress.LatestChangeCheckpoint!;
        if (progress.LatestSettlement is { } retained && retained.IsCoveredBy(checkpoint, feed.Scope))
            return new(progress, null);
        if (binding.Source is not IMaterializationSettlingSource settlingSource)
        {
            return new(
                progress,
                Failure(
                    MaterializationCatchUpFeedDisposition.TargetOrSettlementFailed,
                    feed,
                    progress.Key.Generation,
                    pagesRead: 0,
                    mutationsApplied: 0,
                    progress,
                    MaterializationSynchronizationDiagnosticCodes.SettlementFailed,
                    "The selected source advertises explicit settlement but does not bind its settlement port."));
        }

        var settled = await settlingSource.SettleAsync(
                context,
                new MaterializationSourceSettlementRequest(
                    id: MaterializationSynchronizationIdentities.Settlement(checkpoint),
                    checkpoint: checkpoint.Id,
                    position: checkpoint.Position!,
                    requestedAtUtc: checkpoint.CommittedAtUtc))
            .ConfigureAwait(false);
        if (settled.Disposition is not (
                MaterializationSourceSettlementDisposition.Acknowledged
                or MaterializationSourceSettlementDisposition.Replayed)
            || settled.Receipt is not { } receipt)
        {
            return new(
                progress,
                Failure(
                    settled.Disposition == MaterializationSourceSettlementDisposition.IdentityConflict
                        ? MaterializationCatchUpFeedDisposition.RestartRequired
                        : MaterializationCatchUpFeedDisposition.TargetOrSettlementFailed,
                    feed,
                    progress.Key.Generation,
                    pagesRead: 0,
                    mutationsApplied: 0,
                    progress,
                    settled.Disposition == MaterializationSourceSettlementDisposition.IdentityConflict
                        ? MaterializationSynchronizationDiagnosticCodes.ReplayConflict
                        : MaterializationSynchronizationDiagnosticCodes.SettlementFailed,
                    $"Explicit source settlement was rejected with '{settled.Disposition}'."));
        }

        var saved = await resolved.ProgressStore.SaveSettlementAsync(
                context,
                progress.Key,
                MaterializationSynchronizationIdentities.SettlementMutation(checkpoint),
                progress.Revision,
                owner,
                progress.Fence,
                receipt)
            .ConfigureAwait(false);
        if (saved.Disposition is not (
            MaterializationProgressMutationDisposition.Applied
            or MaterializationProgressMutationDisposition.Replayed))
        {
            return new(
                saved.Snapshot ?? progress,
                Failure(
                    saved.Disposition == MaterializationProgressMutationDisposition.IdentityConflict
                        ? MaterializationCatchUpFeedDisposition.RestartRequired
                        : MaterializationCatchUpFeedDisposition.Fenced,
                    feed,
                    progress.Key.Generation,
                    pagesRead: 0,
                    mutationsApplied: 0,
                    saved.Snapshot ?? progress,
                    saved.Disposition == MaterializationProgressMutationDisposition.IdentityConflict
                        ? MaterializationSynchronizationDiagnosticCodes.ReplayConflict
                        : MaterializationSynchronizationDiagnosticCodes.Fenced,
                    $"Durable settlement evidence was rejected with '{saved.Disposition}'."));
        }
        return new(saved.Snapshot!, null);
    }

    bool RequiresExplicitSettlement(MaterializationChangeFeedPlan feed)
    {
        var source = resolved.Plan.Sources.Single(candidate => candidate.Input == feed.Scope.Input);
        var requirements = resolved.Plan.Materialization.Definition.Sources
            .Single(candidate => candidate.Input == feed.Scope.Input)
            .Capabilities;
        var incremental = MaterializationCapabilityMatcher.MatchForMode(
            requirements,
            source.Profile,
            MaterializationSynchronizationMode.Incremental);
        return incremental.Decisions.Any(static decision =>
            decision.Requirement.Capability == MaterializationCapabilityKind.SourceSettlement
            && decision.Evidence?.Guarantees.Contains(MaterializationGuaranteeKind.ExplicitSettlement) == true);
    }

    static ImmutableArray<MaterializationSynchronizationItemIntent> ProjectItemIntents(
        ImmutableArray<MaterializationRootProjection> projections)
    {
        if (projections.IsDefaultOrEmpty)
            return [];
        var builder = ImmutableArray.CreateBuilder<MaterializationSynchronizationItemIntent>(projections.Length);
        foreach (var projection in projections)
        {
            var item = MaterializationItemIdentity.FromRootIdentity(projection.Root.Identity);
            builder.Add(projection.Row is { } row
                ? new MaterializationSynchronizationUpsertIntent(item, row.Value)
                : new MaterializationSynchronizationDeleteIntent(item));
        }
        return builder.MoveToImmutable();
    }

    static bool IsExactCheckpoint(
        MaterializationApplicationCheckpoint? checkpoint,
        MaterializationSynchronizationPageIntent page) =>
        checkpoint is
        {
            Kind: MaterializationCheckpointKind.ChangeProgress,
            Continuation: null,
            Completion: null,
            Position: { } position
        }
        && checkpoint.Id == page.Checkpoint
        && position == page.ThroughPosition
        && checkpoint.AppliedDeliveries.SequenceEqual(page.AppliedDeliveries)
        && checkpoint.BatchPageOrdinal is null
        && string.Equals(checkpoint.EvidenceReference, page.Feed.Value, StringComparison.Ordinal)
        && checkpoint.ChannelProgress
            == MaterializationChannelSemantics.CreatePositionedDurableProgress(page.ThroughPosition);

    static bool WasCompletedByTakeover(
        MaterializationSynchronizationWorkMutationResult completion,
        MaterializationSynchronizationWorkKey key,
        MaterializationPreparedSynchronizationWork prepared,
        MaterializationProgressSnapshot progress) =>
        completion.Snapshot is
        {
            PendingWork: null
        } snapshot
        && snapshot.Key == key
        && IsExactCheckpoint(progress.LatestChangeCheckpoint, prepared.Page)
        && (prepared.Version is not { } version
            || snapshot.NextItemVersion.Ordinal > version.Ordinal);

    void ValidateOrdinaryPageBounds(
        MaterializationChangeFeedPlan feed,
        IMaterializationPullChangeSource source,
        MaterializationChangePage page,
        MaterializationRebuildLimits limits)
    {
        var requirements = resolved.Plan.Materialization.Definition.Sources
            .Single(candidate => candidate.Input == feed.Scope.Input)
            .Capabilities;
        var changeEvidence = MaterializationCapabilityMatcher.MatchForMode(
                requirements,
                source.Descriptor.CapabilityProfile,
                MaterializationSynchronizationMode.Incremental)
            .Decisions
            .Single(decision => decision.Requirement.Capability == MaterializationCapabilityKind.SourceChangeDelivery)
            .Evidence!;
        var transactionAligned = changeEvidence.Guarantees.Contains(
            MaterializationGuaranteeKind.TransactionAlignedDelivery);
        var maximumItems = (long)limits.MaximumPageItems;
        var maximumBytes = limits.MaximumPageBytes;
        if (transactionAligned)
        {
            maximumItems = checked(maximumItems + changeEvidence.OperatingLimits
                .Single(limit => limit.Kind == MaterializationLimitKind.TransactionItems)
                .Maximum);
            maximumBytes = checked(maximumBytes + changeEvidence.OperatingLimits
                .Single(limit => limit.Kind == MaterializationLimitKind.TransactionBytes)
                .Maximum);
        }

        if ((long)page.Deliveries.Length > maximumItems)
        {
            throw new MaterializationSynchronizationBoundaryException(
                $"The source returned {page.Deliveries.Length} deliveries across the {maximumItems}-item "
                + (transactionAligned ? "transaction-aligned safety envelope." : "page bound."));
        }

        long bytes = 0;
        foreach (var delivery in page.Deliveries)
        {
            bytes = checked(bytes + StrictDocumentJson.GetCanonicalBytes(delivery, CanonicalJsonOptions).LongLength);
            if (bytes > maximumBytes)
            {
                throw new MaterializationSynchronizationBoundaryException(
                    $"The source returned {bytes} canonical delivery bytes across the {maximumBytes}-byte "
                    + (transactionAligned ? "transaction-aligned safety envelope." : "page bound."));
            }
        }
    }

    MaterializationCatchUpFeedEvidence CreateEvidence(
        MaterializationChangeFeedPlan feed,
        MaterializationProgressSnapshot progress,
        DateTimeOffset readStartedAtUtc,
        DateTimeOffset readCompletedAtUtc)
    {
        var checkpoint = progress.LatestChangeCheckpoint
            ?? throw new InvalidOperationException("Caught-up evidence requires durable change progress.");
        return new(
            feed: feed.Id,
            scope: feed.Scope,
            latestChangeCheckpoint: checkpoint.Id,
            throughPosition: checkpoint.Position!,
            caughtUpReadStartedAtUtc: readStartedAtUtc,
            caughtUpReadCompletedAtUtc: readCompletedAtUtc,
            checkpointCommittedAtUtc: checkpoint.CommittedAtUtc,
            settlementRequirement: RequiresExplicitSettlement(feed)
                ? MaterializationConvergenceSettlementRequirement.Explicit
                : MaterializationConvergenceSettlementRequirement.NotRequired,
            settlement: progress.LatestSettlement is { } settlement
                && settlement.IsCoveredBy(checkpoint, feed.Scope)
                    ? settlement
                    : null);
    }

    static MaterializationCatchUpFeedResult Success(
        MaterializationChangeFeedPlan feed,
        MaterializationGenerationId generation,
        int pagesRead,
        long mutationsApplied,
        MaterializationProgressSnapshot progress,
        MaterializationCatchUpFeedEvidence evidence) =>
        new(
            MaterializationCatchUpFeedDisposition.CaughtUp,
            feed.Id,
            generation,
            pagesRead,
            mutationsApplied,
            progress,
            evidence);

    static MaterializationCatchUpFeedResult WorkRemaining(
        MaterializationChangeFeedPlan feed,
        MaterializationGenerationId generation,
        int pagesRead,
        long mutationsApplied,
        MaterializationProgressSnapshot? progress) =>
        new(
            MaterializationCatchUpFeedDisposition.WorkRemaining,
            feed.Id,
            generation,
            pagesRead,
            mutationsApplied,
            progress,
            evidence: null);

    static string CreateWorkerOwner(
        MaterializationRebuildAttempt attempt,
        MaterializationSynchronizationInvocationId invocation,
        MaterializationSynchronizationWorkerId worker) =>
        $"{attempt.Continuation.ProcessInstanceId.Value}/{attempt.Continuation.ProcessAttemptId.Value}/"
        + $"{invocation.Value}/{worker.Value}";

    static MaterializationSynchronizationRunDisposition AggregateDisposition(
        ImmutableArray<MaterializationCatchUpFeedResult> results)
    {
        var aggregate = MaterializationSynchronizationRunDisposition.WorkRemaining;
        var aggregatePriority = 0;
        foreach (var result in results)
        {
            var (disposition, priority) = result.Disposition switch
            {
                MaterializationCatchUpFeedDisposition.CaughtUp or
                MaterializationCatchUpFeedDisposition.WorkRemaining =>
                    (MaterializationSynchronizationRunDisposition.WorkRemaining, 0),
                MaterializationCatchUpFeedDisposition.NotReady =>
                    (MaterializationSynchronizationRunDisposition.NotReady, 1),
                MaterializationCatchUpFeedDisposition.Fenced =>
                    (MaterializationSynchronizationRunDisposition.Fenced, 3),
                MaterializationCatchUpFeedDisposition.RestartRequired =>
                    (MaterializationSynchronizationRunDisposition.RestartRequired, 4),
                _ => (MaterializationSynchronizationRunDisposition.Failed, 2)
            };
            if (priority > aggregatePriority)
            {
                aggregate = disposition;
                aggregatePriority = priority;
            }
        }

        return aggregate;
    }

    static MaterializationCatchUpFeedResult ApplyFailure(
        MaterializationChangeFeedPlan feed,
        MaterializationGenerationId generation,
        int pagesRead,
        long mutationsApplied,
        MaterializationProgressSnapshot progress,
        MaterializationTargetWriteResult write) =>
        Failure(
            write.Disposition switch
            {
                MaterializationTargetWriteDisposition.BoundaryExceeded =>
                    MaterializationCatchUpFeedDisposition.BoundaryExceeded,
                MaterializationTargetWriteDisposition.IdentityConflict =>
                    MaterializationCatchUpFeedDisposition.RestartRequired,
                MaterializationTargetWriteDisposition.StaleFence =>
                    MaterializationCatchUpFeedDisposition.Fenced,
                _ => MaterializationCatchUpFeedDisposition.TargetOrSettlementFailed
            },
            feed,
            generation,
            pagesRead,
            mutationsApplied,
            progress,
            write.Disposition switch
            {
                MaterializationTargetWriteDisposition.BoundaryExceeded =>
                    MaterializationSynchronizationDiagnosticCodes.OperatingBoundaryExceeded,
                MaterializationTargetWriteDisposition.IdentityConflict =>
                    MaterializationSynchronizationDiagnosticCodes.ReplayConflict,
                MaterializationTargetWriteDisposition.StaleFence =>
                    MaterializationSynchronizationDiagnosticCodes.Fenced,
                _ => MaterializationSynchronizationDiagnosticCodes.TargetMutationFailed
            },
            write.Message ?? "Target synchronization failed.");

    static MaterializationCatchUpFeedResult WorkStoreFailure(
        MaterializationChangeFeedPlan feed,
        MaterializationGenerationId generation,
        int pagesRead,
        long mutationsApplied,
        MaterializationProgressSnapshot? progress,
        MaterializationSynchronizationWorkMutationResult result) =>
        Failure(
            result.Disposition == MaterializationSynchronizationWorkMutationDisposition.IdentityConflict
                ? MaterializationCatchUpFeedDisposition.RestartRequired
                : MaterializationCatchUpFeedDisposition.Fenced,
            feed,
            generation,
            pagesRead,
            mutationsApplied,
            progress,
            result.Disposition == MaterializationSynchronizationWorkMutationDisposition.IdentityConflict
                ? MaterializationSynchronizationDiagnosticCodes.ReplayConflict
                : MaterializationSynchronizationDiagnosticCodes.Fenced,
            $"Synchronization work was rejected with '{result.Disposition}'.");

    static MaterializationCatchUpFeedResult Failure(
        MaterializationCatchUpFeedDisposition disposition,
        MaterializationChangeFeedPlan feed,
        MaterializationGenerationId generation,
        int pagesRead,
        long mutationsApplied,
        MaterializationProgressSnapshot? progress,
        string code,
        string message) =>
        new(
            disposition,
            feed.Id,
            generation,
            pagesRead,
            mutationsApplied,
            progress,
            evidence: null,
            diagnostics:
            [
                MaterializationContract.CreateDiagnostic(
                    code,
                    DiagnosticSeverity.Error,
                    message,
                    "/synchronization",
                    "materialization-synchronization-executor",
                    feed.Id.Value,
                    [feed.Scope.Source.Value, feed.Scope.Input.Value],
                    "exact durable application before progress and settlement",
                    disposition.ToString())
            ]);

    static MaterializationCatchUpFeedResult WithCounts(
        MaterializationCatchUpFeedResult result,
        int pagesRead,
        long mutationsApplied) =>
        new(
            result.Disposition,
            result.Feed,
            result.Generation,
            pagesRead,
            mutationsApplied,
            result.Progress,
            result.Evidence,
            result.Diagnostics);

    readonly record struct SettlementDrainResult(
        MaterializationProgressSnapshot Progress,
        MaterializationCatchUpFeedResult? Failure);

    readonly record struct PendingDrainResult(
        MaterializationSynchronizationWorkSnapshot Work,
        long MutationsApplied,
        MaterializationCatchUpFeedEvidence? CaughtUpEvidence,
        MaterializationCatchUpFeedResult? Failure);
}
