using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

enum MaterializationRebuildExecutionResolutionFailureMode
{
    ProjectFailure,
    RemainUnresolved
}

/// <summary>Stable diagnostics emitted by the durable materialization-rebuild Request adapters.</summary>
public static class MaterializationRebuildDurableOperationDiagnosticCodes
{
    /// <summary>The adapter was invoked for a Request contract outside its exact capability set.</summary>
    public const string RequestUnsupported = "storage.materialization.rebuild.adapter.request.unsupported";

    /// <summary>The Request payload was not one concrete non-empty String.</summary>
    public const string RequestPayloadInvalid = "storage.materialization.rebuild.adapter.request.payloadInvalid";

    /// <summary>The initialization Request did not originate from a current Process continuation.</summary>
    public const string RequestOriginInvalid = "storage.materialization.rebuild.adapter.request.originInvalid";

    /// <summary>The Request payload was not a valid current-version durable work reference.</summary>
    public const string WorkReferenceInvalid = "storage.materialization.rebuild.adapter.workReference.invalid";

    /// <summary>No exact persisted plan, runtime binding, executor, and attempt were available.</summary>
    public const string ExecutionUnavailable = "storage.materialization.rebuild.adapter.execution.unavailable";

    /// <summary>The resolver returned execution evidence for another plan or Process attempt.</summary>
    public const string ExecutionInexact = "storage.materialization.rebuild.adapter.execution.inexact";

    /// <summary>The referenced shard is absent from the exact resolved plan.</summary>
    public const string ShardUnavailable = "storage.materialization.rebuild.adapter.shard.unavailable";

    /// <summary>The executor returned evidence for another shard, generation, or plan shape.</summary>
    public const string ResultInexact = "storage.materialization.rebuild.adapter.result.inexact";

    /// <summary>The exact executor deterministically rejected attempt initialization.</summary>
    public const string InitializationRejected = "storage.materialization.rebuild.adapter.initialization.rejected";

    /// <summary>The exact executor returned a terminal failed shard disposition.</summary>
    public const string ShardRejected = "storage.materialization.rebuild.adapter.shard.rejected";

    /// <summary>The synchronization-and-activation interpreter returned a terminal non-active disposition.</summary>
    public const string ActivationRejected = "storage.materialization.rebuild.adapter.activation.rejected";
}

/// <summary>Schema-versioned durable Process result identifying one exact active materialization generation.</summary>
public sealed record MaterializationActiveGenerationReference
{
    /// <summary>Current active-generation-reference wire schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-active-generation-reference/v2";

    /// <summary>Creates one exact active-generation reference.</summary>
    /// <param name="schemaVersion">Exact supported wire schema version.</param>
    /// <param name="authority">Exact linked plan-set, leaf-plan, and placement-slice promotion authority.</param>
    /// <param name="generation">Exact newly active generation.</param>
    /// <param name="targetRevision">Committed target-pointer revision.</param>
    /// <param name="promotion">Stable promotion operation identity.</param>
    /// <param name="promotionFence">Accepted independent target-pointer fence.</param>
    /// <param name="validation">Validation evidence authorizing promotion.</param>
    /// <param name="activatedAtUtc">UTC promotion boundary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is invalid, the schema is unsupported, or time is not UTC.</exception>
    [JsonConstructor]
    public MaterializationActiveGenerationReference(
        string schemaVersion,
        MaterializationRebuildLeafExecutionAuthority authority,
        MaterializationGenerationId generation,
        MaterializationTargetRevision targetRevision,
        MaterializationPromotionId promotion,
        MaterializationPromotionFence promotionFence,
        MaterializationValidationFingerprint validation,
        DateTimeOffset activatedAtUtc)
    {
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException("The active-generation reference schema is unsupported.", nameof(schemaVersion));
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        if (targetRevision.Ordinal <= 0)
            throw new ArgumentException("An active generation requires a committed target revision.", nameof(targetRevision));
        MaterializationContract.RequireDefinedIdentity(promotion.Value, nameof(promotion));
        MaterializationContract.RequireDefinedIdentity(promotionFence.Value, nameof(promotionFence));
        MaterializationContract.RequireDefinedIdentity(validation.Value, nameof(validation));
        MaterializationContract.RequireUtc(activatedAtUtc, nameof(activatedAtUtc));
        SchemaVersion = schemaVersion;
        Generation = generation;
        TargetRevision = targetRevision;
        Promotion = promotion;
        PromotionFence = promotionFence;
        Validation = validation;
        ActivatedAtUtc = activatedAtUtc;
    }

    /// <summary>Exact wire schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact linked plan-set, leaf-plan, and full placement-slice promotion authority.</summary>
    public MaterializationRebuildLeafExecutionAuthority Authority { get; }

    /// <summary>Pinned rebuild-plan fingerprint projected from <see cref="Authority"/>.</summary>
    [JsonIgnore]
    public MaterializationRebuildPlanFingerprint Plan => Authority.LeafPlan.Plan;

    /// <summary>Exact independently promoted placement authority projected from <see cref="Authority"/>.</summary>
    [JsonIgnore]
    public MaterializationPlacementSliceReference PlacementSlice => Authority.PlacementSlice;

    /// <summary>Logical materialization projected from the exact placement authority.</summary>
    [JsonIgnore]
    public MaterializationId Materialization => Authority.PlacementSlice.Materialization.Materialization;

    /// <summary>Physical target projected from the exact placement authority.</summary>
    [JsonIgnore]
    public MaterializationTargetId Target => Authority.PlacementSlice.Target;

    /// <summary>Exact newly active generation.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Committed target-pointer revision.</summary>
    public MaterializationTargetRevision TargetRevision { get; }

    /// <summary>Stable promotion operation identity.</summary>
    public MaterializationPromotionId Promotion { get; }

    /// <summary>Accepted independent target-pointer fence.</summary>
    public MaterializationPromotionFence PromotionFence { get; }

    /// <summary>Validation evidence authorizing promotion.</summary>
    public MaterializationValidationFingerprint Validation { get; }

    /// <summary>UTC promotion boundary.</summary>
    public DateTimeOffset ActivatedAtUtc { get; }
}

/// <summary>Strict canonical JSON serialization for <see cref="MaterializationActiveGenerationReference"/>.</summary>
public static class MaterializationActiveGenerationReferenceJsonSerializer
{
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Serializes one active-generation reference to canonical UTF-8 JSON represented as a String.</summary>
    /// <param name="reference">Exact active-generation reference.</param>
    /// <returns>Canonical JSON preserving the complete reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The reference cannot be serialized under its strict wire contract.</exception>
    /// <exception cref="NotSupportedException">A contained value has no supported JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The reference has no canonical JSON representation.</exception>
    public static string Serialize(MaterializationActiveGenerationReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(reference, Options));
    }

    /// <summary>Deserializes and validates one exact active-generation reference.</summary>
    /// <param name="json">Strict JSON document.</param>
    /// <returns>The validated reference.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document is malformed, noncanonical in shape, or uses another schema.</exception>
    public static MaterializationActiveGenerationReference Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                Options,
                "active-generation reference",
                out MaterializationActiveGenerationReference? reference,
                out var error)
            && reference is not null)
        {
            return reference;
        }

        throw new JsonException(error.Message);
    }
}

/// <summary>
/// Exact attempt-scoped runtime binding resolved from a linked leaf execution authority and Process continuation.
/// </summary>
/// <remarks>
/// The public constructor binds one <see cref="ResolvedMaterializationRebuildPlan"/> to its reference executor. The
/// adapter consumes only the canonical linked leaf authority, shard catalog, attempt, and expected generation
/// so runtime objects do not leak into durable Request payloads.
/// </remarks>
public sealed class MaterializationRebuildExecution
{
    readonly Func<OperationContext, Task<MaterializationRebuildInitializationResult>> beginAttempt;
    readonly Func<OperationContext, MaterializationRebuildShardId, Task<MaterializationRebuildShardResult>> runShard;
    readonly Func<OperationContext, MaterializationSynchronizationInvocationId, MaterializationSynchronizationWorkerId,
        Task<MaterializationGenerationActivationResult>> synchronizeAndActivate;
    readonly Func<OperationContext, DateTimeOffset, Task<bool>> abandonAttempt;

    /// <summary>Creates one exact attempt-scoped reference execution binding.</summary>
    /// <param name="resolved">Exact persisted plan and runtime ports.</param>
    /// <param name="attempt">Exact coordinator Process attempt and its stable UTC start time.</param>
    /// <param name="synchronizationWorkStore">
    /// Shared durable generation-wide synchronization, version-allocation, and activation authority.
    /// </param>
    /// <param name="crashInjector">Optional deterministic conformance and crash-injection hook.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resolved"/>, <paramref name="attempt"/>, or <paramref name="synchronizationWorkStore"/> is
    /// <see langword="null"/>.
    /// </exception>
    public MaterializationRebuildExecution(
        ResolvedMaterializationRebuildPlan resolved,
        MaterializationRebuildAttempt attempt,
        IMaterializationSynchronizationWorkStore synchronizationWorkStore,
        IMaterializationRebuildCrashInjector? crashInjector = null)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(synchronizationWorkStore);
        Attempt = Guard.RequireNotNull(attempt);
        Authority = resolved.Authority;
        Shards = NormalizeShards(resolved.Plan.Shards.Select(static shard => shard.Id));
        ChangeFeedCount = resolved.Plan.ChangeFeeds.Length;
        Generation = MaterializationRebuildIdentities.Generation(resolved.Plan, attempt);
        Materialization = resolved.Plan.Materialization.Definition.Id;
        Target = resolved.Plan.Target.Id;
        var executor = new MaterializationRebuildExecutor(resolved, crashInjector);
        var activationExecutor = new MaterializationGenerationActivationExecutor(
            resolved,
            synchronizationWorkStore);
        beginAttempt = context => executor.BeginAttemptAsync(context, attempt);
        runShard = (context, shard) => executor.RunShardAsync(context, attempt, shard);
        synchronizeAndActivate = (context, invocation, worker) =>
            activationExecutor.ActivateAsync(context, attempt, invocation, worker);
        abandonAttempt = (context, abandonedAtUtc) =>
            executor.AbandonAttemptAsync(context, attempt, abandonedAtUtc);
    }

    internal MaterializationRebuildExecution(
        MaterializationRebuildLeafExecutionAuthority authority,
        ImmutableArray<MaterializationRebuildShardId> shards,
        MaterializationRebuildAttempt attempt,
        MaterializationGenerationId generation,
        MaterializationId materialization,
        MaterializationTargetId target,
        Func<OperationContext, Task<MaterializationRebuildInitializationResult>> beginAttempt,
        Func<OperationContext, MaterializationRebuildShardId, Task<MaterializationRebuildShardResult>> runShard,
        Func<OperationContext, MaterializationSynchronizationInvocationId, MaterializationSynchronizationWorkerId,
            Task<MaterializationGenerationActivationResult>> synchronizeAndActivate,
        Func<OperationContext, DateTimeOffset, Task<bool>>? abandonAttempt = null,
        int? changeFeedCount = null)
    {
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        Shards = NormalizeShards(shards);
        Attempt = Guard.RequireNotNull(attempt);
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        Generation = generation;
        MaterializationContract.RequireDefinedIdentity(materialization.Value, nameof(materialization));
        Materialization = materialization;
        MaterializationContract.RequireDefinedIdentity(target.Value, nameof(target));
        if (Authority.PlacementSlice.Materialization.Materialization != materialization
            || Authority.PlacementSlice.Target != target)
        {
            throw new ArgumentException(
                "A rebuild execution placement slice must address its exact materialization and target.",
                nameof(authority));
        }
        Target = target;
        ChangeFeedCount = changeFeedCount ?? Shards.Length;
        if (ChangeFeedCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(changeFeedCount), changeFeedCount, "A rebuild execution requires a change feed.");
        this.beginAttempt = beginAttempt ?? throw new ArgumentNullException(nameof(beginAttempt));
        this.runShard = runShard ?? throw new ArgumentNullException(nameof(runShard));
        this.synchronizeAndActivate = synchronizeAndActivate
            ?? throw new ArgumentNullException(nameof(synchronizeAndActivate));
        this.abandonAttempt = abandonAttempt ?? ((_, _) => Task.FromResult(false));
    }

    /// <summary>Exact linked plan-set, leaf-plan, and full placement-slice authority resolved for this execution.</summary>
    public MaterializationRebuildLeafExecutionAuthority Authority { get; }

    /// <summary>Exact persisted leaf-plan reference projected from <see cref="Authority"/>.</summary>
    public MaterializationRebuildPlanReference PlanReference => Authority.LeafPlan;

    /// <summary>Exact independently promoted placement authority projected from <see cref="Authority"/>.</summary>
    public MaterializationPlacementSliceReference PlacementSlice => Authority.PlacementSlice;

    /// <summary>Exact persisted plan fingerprint resolved for this execution.</summary>
    public MaterializationRebuildPlanFingerprint PlanFingerprint => PlanReference.Plan;

    /// <summary>Finite canonical shard catalog projected from the exact persisted plan.</summary>
    public ImmutableArray<MaterializationRebuildShardId> Shards { get; }

    /// <summary>Number of independently captured dependency feeds in the exact persisted plan.</summary>
    public int ChangeFeedCount { get; }

    /// <summary>Exact coordinator Process attempt and stable UTC start time.</summary>
    public MaterializationRebuildAttempt Attempt { get; }

    /// <summary>Deterministic candidate generation owned by <see cref="Attempt"/>.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Logical materialization served by this execution's exact target.</summary>
    public MaterializationId Materialization { get; }

    /// <summary>Exact physical target receiving this execution.</summary>
    public MaterializationTargetId Target { get; }

    /// <summary>Idempotently establishes the candidate generation and one initial cut per dependency feed.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <returns>Ready or deterministic rejected initialization evidence from the exact executor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public Task<MaterializationRebuildInitializationResult> BeginAttemptAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return beginAttempt(context);
    }

    /// <summary>Runs or resumes one exact shard through its durable baseline boundary.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="shard">Stable shard identity from <see cref="Shards"/>.</param>
    /// <returns>Completed or deterministic failed shard evidence from the exact executor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="shard"/> is absent from the exact plan.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public Task<MaterializationRebuildShardResult> RunShardAsync(
        OperationContext context,
        MaterializationRebuildShardId shard)
    {
        ArgumentNullException.ThrowIfNull(context);
        return runShard(context, shard);
    }

    /// <summary>Runs or resumes one bounded synchronization-and-generation-activation occurrence.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="invocation">Stable logical durable-Request identity retained across exact retry.</param>
    /// <param name="worker">Explicit physical attempt and operation-fence authority.</param>
    /// <returns>Active, work-remaining, or deterministic terminal failure evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="invocation"/> or <paramref name="worker"/> is default.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public Task<MaterializationGenerationActivationResult> SynchronizeAndActivateAsync(
        OperationContext context,
        MaterializationSynchronizationInvocationId invocation,
        MaterializationSynchronizationWorkerId worker)
    {
        ArgumentNullException.ThrowIfNull(context);
        MaterializationContract.RequireDefinedIdentity(invocation.Value, nameof(invocation));
        MaterializationContract.RequireDefinedIdentity(worker.Value, nameof(worker));
        return synchronizeAndActivate(context, invocation, worker);
    }

    /// <summary>Idempotently abandons or reconciles this attempt's unreadable candidate generation.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="abandonedAtUtc">Stable UTC Process-attempt closure time retained across replay.</param>
    /// <returns>
    /// <see langword="true"/> when absence or durable abandonment is conclusive; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="abandonedAtUtc"/> is not UTC.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public Task<bool> AbandonAttemptAsync(
        OperationContext context,
        DateTimeOffset abandonedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        MaterializationContract.RequireUtc(abandonedAtUtc, nameof(abandonedAtUtc));
        return abandonAttempt(context, abandonedAtUtc);
    }

    /// <summary>Returns whether the exact persisted plan contains a shard identity.</summary>
    /// <param name="shard">Stable shard identity to inspect.</param>
    /// <returns><see langword="true"/> when <paramref name="shard"/> belongs to this execution.</returns>
    public bool ContainsShard(MaterializationRebuildShardId shard) =>
        Shards.BinarySearch(shard, MaterializationRebuildShardIdComparer.Instance) >= 0;

    static ImmutableArray<MaterializationRebuildShardId> NormalizeShards(
        IEnumerable<MaterializationRebuildShardId> shards)
    {
        ArgumentNullException.ThrowIfNull(shards);
        var normalized = shards
            .OrderBy(static shard => shard.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("A rebuild execution requires at least one exact shard.", nameof(shards));
        for (var index = 0; index < normalized.Length; index++)
        {
            MaterializationContract.RequireDefinedIdentity(normalized[index].Value, nameof(shards));
            if (index > 0 && normalized[index - 1] == normalized[index])
                throw new ArgumentException("A rebuild execution cannot repeat a shard identity.", nameof(shards));
        }

        return normalized;
    }

    sealed class MaterializationRebuildShardIdComparer : IComparer<MaterializationRebuildShardId>
    {
        internal static MaterializationRebuildShardIdComparer Instance { get; } = new();

        public int Compare(MaterializationRebuildShardId left, MaterializationRebuildShardId right) =>
            StringComparer.Ordinal.Compare(left.Value, right.Value);
    }
}

/// <summary>Resolves one exact linked leaf, runtime binding, executor, and Process attempt.</summary>
/// <remarks>
/// Implementations should index persisted leaves by their complete execution authority and retain one stable UTC start time
/// for each exact coordinator continuation. Returning an earlier or replacement continuation is never a compatible
/// fallback; rejecting stale child work is part of the port's fencing contract.
/// </remarks>
public interface IMaterializationRebuildExecutionResolver
{
    /// <summary>Attempts to resolve one exact attempt-scoped rebuild execution.</summary>
    /// <param name="authority">Exact linked plan-set, leaf-plan, and full placement-slice authority.</param>
    /// <param name="continuation">Exact owning coordinator Process continuation.</param>
    /// <param name="execution">Receives the exact execution when available.</param>
    /// <returns><see langword="true"/> when the exact plan and attempt are currently resolvable.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authority"/> or <paramref name="continuation"/> is <see langword="null"/>.
    /// </exception>
    bool TryResolve(
        MaterializationRebuildLeafExecutionAuthority authority,
        ProcessContinuationIdentity continuation,
        out MaterializationRebuildExecution? execution);
}

/// <summary>
/// Durable-operation adapter that initializes the current coordinator attempt and returns attempt-bound shard work.
/// </summary>
/// <remarks>
/// The Request's <see cref="ProcessInteractionOrigin"/> supplies the current coordinator continuation after either
/// first start or RestartAttempt. The resolver supplies that continuation's stable start time. Successful
/// initialization returns a finite canonical String collection of shard-work references; those references therefore
/// cannot accidentally retain an abandoned generation across a coordinator restart.
/// </remarks>
public sealed class MaterializationRebuildInitializationDurableOperationAdapter : IDurableOperationAdapter
{
    readonly IMaterializationRebuildExecutionResolver resolver;

    /// <summary>Creates an adapter for one exact rebuild-initialization Request contract.</summary>
    /// <param name="request">Exact initialization Request contract emitted by the coordinator.</param>
    /// <param name="resolver">Resolver for exact attempt-scoped plans and executors.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MaterializationRebuildInitializationDurableOperationAdapter(
        RequestContractReference request,
        IMaterializationRebuildExecutionResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        Capabilities = new(
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            reconciliation: DurableOperationReconciliationCapability.Supported,
            supportedRequests: [request]);
    }

    /// <inheritdoc />
    public DurableOperationAdapterCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        context.ThrowIfCancellationRequested();
        var outcome = await RunAsync(
                context,
                invocation.Request,
                MaterializationRebuildExecutionResolutionFailureMode.ProjectFailure)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Execute resolution failures must project a terminal Request outcome.");
        return new DurableOperationOutcomeObservation(outcome);
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        var outcome = await RunAsync(
                context,
                request.Request,
                MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
            .ConfigureAwait(false);
        return outcome is null
            ? new DurableOperationUnresolved()
            : new DurableOperationReconciledOutcome(outcome);
    }

    async Task<RequestTerminalOutcome?> RunAsync(
        OperationContext context,
        RequestEnvelope request,
        MaterializationRebuildExecutionResolutionFailureMode resolutionFailureMode)
    {
        if (!Capabilities.Supports(request.Contract))
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.RequestUnsupported);
        if (!MaterializationRebuildDurableOperationProjection.TryReadStringPayload(request, out var payload))
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.RequestPayloadInvalid);
        }
        if (!MaterializationRebuildWorkReferenceJsonSerializer.TryDeserializeAuthority(
                payload,
                out var reference,
                out _)
            || reference is null)
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.WorkReferenceInvalid);
        }
        if (request.Context.Origin is not ProcessInteractionOrigin origin)
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.RequestOriginInvalid,
                plan: reference.LeafPlan.Plan);
        if (!resolver.TryResolve(reference, origin.Continuation, out var execution)
            || execution is null)
        {
            if (resolutionFailureMode == MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
                return null;
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionUnavailable,
                plan: reference.LeafPlan.Plan,
                continuation: origin.Continuation);
        }
        if (execution.Authority != reference
            || execution.Attempt.Continuation != origin.Continuation)
        {
            if (resolutionFailureMode == MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
                return null;
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionInexact,
                plan: reference.LeafPlan.Plan,
                continuation: origin.Continuation);
        }

        var result = await execution.BeginAttemptAsync(context).ConfigureAwait(false);
        if (result.Generation != execution.Generation
            || result.Disposition == MaterializationRebuildInitializationDisposition.Ready
            && (result.GenerationSnapshot?.GenerationId != execution.Generation
                || result.Progress.Length != execution.ChangeFeedCount
                || result.Progress.Any(progress => progress.Key.Generation != execution.Generation)))
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ResultInexact,
                execution,
                generation: result.Generation,
                disposition: result.Disposition.ToString());
        }
        if (result.Disposition != MaterializationRebuildInitializationDisposition.Ready)
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationProjection.FirstDiagnosticCode(
                    result.Diagnostics,
                    MaterializationRebuildDurableOperationDiagnosticCodes.InitializationRejected),
                execution,
                generation: result.Generation,
                disposition: result.Disposition.ToString(),
                diagnostics: result.Diagnostics);
        }

        var work = ImmutableArray.CreateBuilder<ObservationValue>(execution.Shards.Length);
        for (var index = 0; index < execution.Shards.Length; index++)
        {
            var referenceValue = new MaterializationRebuildShardWorkReference(
                execution.Authority,
                execution.Attempt,
                execution.Shards[index]);
            work.Add(ObservationValue.FromString(
                MaterializationRebuildWorkReferenceJsonSerializer.SerializeShard(referenceValue)));
        }

        return new RequestResultOutcome(
            MaterializationRebuildProcessFactory.CompletedOutcome,
            PortableValue.Concrete(
                MaterializationRebuildDurableOperationProjection.StringCollectionContract,
                ObservationValue.FromImmutableArray(work.MoveToImmutable())));
    }
}

/// <summary>Durable-operation adapter that runs or resumes one exact materialization rebuild shard.</summary>
/// <remarks>
/// The attempt-bound work reference is the target-deduplication authority. Execute and Reconcile both invoke the
/// same idempotent <see cref="MaterializationRebuildExecution.RunShardAsync"/> operation; the reference executor's
/// deterministic page, bulk, checkpoint, fence, and generation identities make this a reconciliation operation
/// rather than a new logical shard run.
/// </remarks>
public sealed class MaterializationRebuildShardDurableOperationAdapter : IDurableOperationAdapter
{
    readonly IMaterializationRebuildExecutionResolver resolver;

    /// <summary>Creates an adapter for one exact Storage-owned shard-rebuild Request contract.</summary>
    /// <param name="request">Exact shard-rebuild Request contract emitted by the worker Process.</param>
    /// <param name="resolver">Resolver for exact attempt-scoped plans and executors.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MaterializationRebuildShardDurableOperationAdapter(
        RequestContractReference request,
        IMaterializationRebuildExecutionResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        Capabilities = new(
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            reconciliation: DurableOperationReconciliationCapability.Supported,
            supportedRequests: [request]);
    }

    /// <inheritdoc />
    public DurableOperationAdapterCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        context.ThrowIfCancellationRequested();
        var outcome = await RunAsync(
                context,
                invocation.Request,
                MaterializationRebuildExecutionResolutionFailureMode.ProjectFailure)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Execute resolution failures must project a terminal Request outcome.");
        return new DurableOperationOutcomeObservation(outcome);
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        var outcome = await RunAsync(
                context,
                request.Request,
                MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
            .ConfigureAwait(false);
        return outcome is null
            ? new DurableOperationUnresolved()
            : new DurableOperationReconciledOutcome(outcome);
    }

    async Task<RequestTerminalOutcome?> RunAsync(
        OperationContext context,
        RequestEnvelope request,
        MaterializationRebuildExecutionResolutionFailureMode resolutionFailureMode)
    {
        if (!Capabilities.Supports(request.Contract))
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.RequestUnsupported);
        if (!MaterializationRebuildDurableOperationProjection.TryReadStringPayload(request, out var payload))
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.RequestPayloadInvalid);
        }
        if (!MaterializationRebuildWorkReferenceJsonSerializer.TryDeserializeShard(
                payload,
                out var reference,
                out _)
            || reference is null)
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.WorkReferenceInvalid);
        }
        if (!resolver.TryResolve(reference.Authority, reference.Attempt.Continuation, out var execution)
            || execution is null)
        {
            if (resolutionFailureMode == MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
                return null;
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionUnavailable,
                reference);
        }
        if (execution.Authority != reference.Authority || execution.Attempt != reference.Attempt)
        {
            if (resolutionFailureMode == MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
                return null;
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionInexact,
                reference);
        }
        if (!execution.ContainsShard(reference.Shard))
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ShardUnavailable,
                reference);
        }

        var result = await execution.RunShardAsync(context, reference.Shard).ConfigureAwait(false);
        if (result.Shard != reference.Shard
            || result.Generation != execution.Generation
            || result.Progress.Key.Generation != result.Generation)
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ResultInexact,
                reference,
                generation: result.Generation,
                disposition: result.Disposition.ToString());
        }

        var code = result.Disposition == MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired
            ? MaterializationRebuildProcessFactory.BaselineCompleteCatchUpRequired
            : MaterializationRebuildDurableOperationProjection.FirstDiagnosticCode(
                result.Diagnostics,
                MaterializationRebuildDurableOperationDiagnosticCodes.ShardRejected);
        var value = MaterializationRebuildDurableOperationProjection.EvidenceValue(
            code,
            reference.Plan,
            reference.Attempt.Continuation,
            reference.Attempt.StartedAtUtc,
            reference.Shard,
            result.Generation,
            result.Disposition.ToString(),
            result.Pages,
            result.Outputs,
            result.Diagnostics);
        return result.Disposition == MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired
            ? new RequestResultOutcome(MaterializationRebuildProcessFactory.CompletedOutcome, value)
            : new RequestFailureOutcome(MaterializationRebuildProcessFactory.FailedOutcome, value);
    }
}

/// <summary>
/// Durable-operation adapter for one bounded incremental synchronization and candidate-activation occurrence.
/// </summary>
/// <remarks>
/// The logical Request emission becomes the synchronization invocation identity. The exact durable-operation
/// attempt and fence become the physical worker identity. Execute and Reconcile therefore reuse the same authority
/// for one ambiguous attempt, while a later physical attempt is explicit and can be fenced by the shared Storage
/// work authority. A completed retained promotion is reconciled as Active rather than reconstructed or abandoned.
/// </remarks>
public sealed class MaterializationSynchronizationActivationDurableOperationAdapter : IDurableOperationAdapter
{
    const string ProgressFingerprintPrefix = "cohesive-materialization-synchronization-progress/v1:sha256:";

    readonly IMaterializationRebuildExecutionResolver resolver;

    /// <summary>Creates an adapter for one exact synchronization-and-activation Request contract.</summary>
    /// <param name="request">Exact coordinator Request contract.</param>
    /// <param name="resolver">Resolver for exact attempt-scoped plans and Storage executors.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public MaterializationSynchronizationActivationDurableOperationAdapter(
        RequestContractReference request,
        IMaterializationRebuildExecutionResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        Capabilities = new(
            idempotencyEvidence: DurableOperationIdempotencyEvidence.TargetDeduplication,
            reconciliation: DurableOperationReconciliationCapability.Supported,
            supportedRequests: [request]);
    }

    /// <inheritdoc />
    public DurableOperationAdapterCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        context.ThrowIfCancellationRequested();
        var outcome = await RunAsync(
                context,
                invocation.Request,
                invocation.AttemptId,
                invocation.Fence,
                MaterializationRebuildExecutionResolutionFailureMode.ProjectFailure)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Execute resolution failures must project a terminal Request outcome.");
        return new DurableOperationOutcomeObservation(outcome);
    }

    /// <inheritdoc />
    public async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        var outcome = await RunAsync(
                context,
                request.Request,
                request.Identity.SourceAttemptId,
                request.Identity.SourceFence,
                MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
            .ConfigureAwait(false);
        return outcome is null
            ? new DurableOperationUnresolved()
            : new DurableOperationReconciledOutcome(outcome);
    }

    async Task<RequestTerminalOutcome?> RunAsync(
        OperationContext context,
        RequestEnvelope request,
        OperationAttemptId physicalAttempt,
        OperationFence physicalFence,
        MaterializationRebuildExecutionResolutionFailureMode resolutionFailureMode)
    {
        if (!Capabilities.Supports(request.Contract))
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.RequestUnsupported);
        }
        if (!MaterializationRebuildDurableOperationProjection.TryReadStringPayload(request, out var payload))
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.RequestPayloadInvalid);
        }
        if (!MaterializationRebuildWorkReferenceJsonSerializer.TryDeserializeAuthority(
                payload,
                out var reference,
                out _)
            || reference is null)
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.WorkReferenceInvalid);
        }
        if (request.Context.Origin is not ProcessInteractionOrigin origin)
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.RequestOriginInvalid,
                plan: reference.LeafPlan.Plan);
        }
        if (!resolver.TryResolve(reference, origin.Continuation, out var execution)
            || execution is null)
        {
            if (resolutionFailureMode == MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
                return null;
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionUnavailable,
                plan: reference.LeafPlan.Plan,
                continuation: origin.Continuation);
        }
        if (execution.Authority != reference
            || execution.Attempt.Continuation != origin.Continuation)
        {
            if (resolutionFailureMode == MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
                return null;
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionInexact,
                plan: reference.LeafPlan.Plan,
                continuation: origin.Continuation);
        }

        MaterializationSynchronizationInvocationId synchronizationInvocation = new(
            $"durable-operation/{request.Context.EmissionId.Value}");
        MaterializationSynchronizationWorkerId worker = new(
            $"durable-operation-attempt/{physicalAttempt.Value}/fence/{physicalFence.Value.ToString(CultureInfo.InvariantCulture)}");
        var result = await execution.SynchronizeAndActivateAsync(
                context,
                synchronizationInvocation,
                worker)
            .ConfigureAwait(false);
        if (result.Generation != execution.Generation)
        {
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ResultInexact,
                execution,
                generation: result.Generation,
                disposition: result.Disposition.ToString());
        }

        if (result.Disposition == MaterializationGenerationActivationDisposition.WorkRemaining)
        {
            var progress = ProgressFingerprint(execution, result.Synchronization!);
            return new RequestResultOutcome(
                MaterializationRebuildProcessFactory.WorkRemainingOutcome,
                MaterializationRebuildDurableOperationProjection.StringValue(progress));
        }

        if (result.Disposition == MaterializationGenerationActivationDisposition.Active)
        {
            var activation = result.Activation!;
            var target = result.Target!;
            var promotion = activation.PromotionReceipt!;
            if (promotion.TargetId != execution.Target
                || target.TargetId != execution.Target
                || target.MaterializationId != execution.Materialization
                || target.Revision != promotion.TargetRevision
                || target.ActiveGenerationId != execution.Generation)
            {
                return MaterializationRebuildDurableOperationProjection.Failure(
                    MaterializationRebuildDurableOperationDiagnosticCodes.ResultInexact,
                    execution,
                    generation: result.Generation,
                    disposition: result.Disposition.ToString());
            }

            var active = new MaterializationActiveGenerationReference(
                schemaVersion: MaterializationActiveGenerationReference.CurrentSchemaVersion,
                authority: execution.Authority,
                generation: execution.Generation,
                targetRevision: promotion.TargetRevision,
                promotion: promotion.PromotionId,
                promotionFence: promotion.PromotionFence,
                validation: promotion.ValidationFingerprint,
                activatedAtUtc: promotion.PromotedAtUtc);
            return new RequestResultOutcome(
                MaterializationRebuildProcessFactory.ActiveOutcome,
                MaterializationRebuildDurableOperationProjection.StringValue(
                    MaterializationActiveGenerationReferenceJsonSerializer.Serialize(active)));
        }

        return MaterializationRebuildDurableOperationProjection.Failure(
            MaterializationRebuildDurableOperationProjection.FirstDiagnosticCode(
                result.Diagnostics,
                MaterializationRebuildDurableOperationDiagnosticCodes.ActivationRejected),
            execution,
            generation: result.Generation,
            disposition: result.Disposition.ToString(),
            diagnostics: result.Diagnostics);
    }

    static string ProgressFingerprint(
        MaterializationRebuildExecution execution,
        MaterializationSynchronizationRunResult synchronization)
    {
        var values = new string[4 + synchronization.Feeds.Length * 4];
        var index = 0;
        values[index++] = execution.PlanFingerprint.Value;
        values[index++] = execution.Generation.Value;
        values[index++] = execution.Attempt.Continuation.ProcessInstanceId.Value;
        values[index++] = execution.Attempt.Continuation.ProcessAttemptId.Value;
        foreach (var feed in synchronization.Feeds.OrderBy(static feed => feed.Feed.Value, StringComparer.Ordinal))
        {
            values[index++] = feed.Feed.Value;
            values[index++] = feed.Progress?.LatestChangeCheckpoint?.Id.Value ?? "change-absent";
            values[index++] = feed.Progress?.LatestSettlement?.Id.Value ?? "settlement-absent";
            values[index++] = feed.Progress?.LatestBatchCheckpoint?.Id.Value ?? "baseline-absent";
        }

        return ProgressFingerprintPrefix + MaterializationStableIdentity.Digest(values);
    }
}

static class MaterializationRebuildDurableOperationProjection
{
    const string EvidenceSchemaVersion = "cohesive-materialization-rebuild-durable-operation-evidence/v1";

    static readonly JsonSerializerOptions EvidenceOptions = StrictDocumentJson.CreateOptions();

    internal static readonly ValueContract StringContract =
        new(new ScalarTypeRef(ScalarTypeKind.String));

    internal static readonly ValueContract StringCollectionContract =
        new(new ScalarTypeRef(ScalarTypeKind.String), cardinality: FieldCardinality.Many);

    internal static bool TryReadStringPayload(RequestEnvelope request, out string payload)
    {
        if (request.Payload.State == PortableValueState.Concrete
            && request.Payload.Value is { Kind: ObservationValueKind.String, String: { } value }
            && !string.IsNullOrWhiteSpace(value))
        {
            payload = value;
            return true;
        }

        payload = string.Empty;
        return false;
    }

    internal static PortableValue StringValue(string value) => PortableValue.Concrete(
        StringContract,
        ObservationValue.FromString(value));

    internal static RequestFailureOutcome Failure(
        string code,
        MaterializationRebuildExecution execution,
        MaterializationGenerationId? generation = null,
        string? disposition = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default) => Failure(
            code,
            execution.PlanFingerprint,
            execution.Attempt.Continuation,
            execution.Attempt.StartedAtUtc,
            shard: null,
            generation,
            disposition,
            diagnostics: diagnostics);

    internal static RequestFailureOutcome Failure(
        string code,
        MaterializationRebuildShardWorkReference reference,
        MaterializationGenerationId? generation = null,
        string? disposition = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default) => Failure(
            code,
            reference.Plan,
            reference.Attempt.Continuation,
            reference.Attempt.StartedAtUtc,
            reference.Shard,
            generation,
            disposition,
            diagnostics: diagnostics);

    internal static RequestFailureOutcome Failure(
        string code,
        MaterializationRebuildPlanFingerprint? plan = null,
        ProcessContinuationIdentity? continuation = null,
        DateTimeOffset? startedAtUtc = null,
        MaterializationRebuildShardId? shard = null,
        MaterializationGenerationId? generation = null,
        string? disposition = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default) => new(
            MaterializationRebuildProcessFactory.FailedOutcome,
            EvidenceValue(
                code,
                plan,
                continuation,
                startedAtUtc,
                shard,
                generation,
                disposition,
                pages: null,
                outputs: null,
                diagnostics));

    internal static PortableValue EvidenceValue(
        string code,
        MaterializationRebuildPlanFingerprint? plan,
        ProcessContinuationIdentity? continuation,
        DateTimeOffset? startedAtUtc,
        MaterializationRebuildShardId? shard,
        MaterializationGenerationId? generation,
        string? disposition,
        int? pages,
        long? outputs,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var evidence = new MaterializationRebuildOperationEvidence(
            SchemaVersion: EvidenceSchemaVersion,
            Code: code,
            PlanAlgorithm: plan?.Algorithm,
            PlanCanonicalization: plan?.Canonicalization,
            PlanFingerprint: plan?.Value,
            ProcessInstanceId: continuation?.ProcessInstanceId.Value,
            ProcessAttemptId: continuation?.ProcessAttemptId.Value,
            StartedAtUtc: startedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
            ShardId: shard?.Value,
            GenerationId: generation?.Value,
            Disposition: disposition,
            Pages: pages,
            Outputs: outputs?.ToString(CultureInfo.InvariantCulture),
            DiagnosticCodes: DiagnosticCodes(diagnostics));
        var json = Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(evidence, EvidenceOptions));
        return PortableValue.Concrete(StringContract, ObservationValue.FromString(json));
    }

    internal static string FirstDiagnosticCode(
        ImmutableArray<DocumentValidationDiagnostic> diagnostics,
        string fallback)
    {
        var code = diagnostics.IsDefaultOrEmpty
            ? null
            : diagnostics
                .Select(static diagnostic => diagnostic.Code)
                .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
        return code ?? fallback;
    }

    static ImmutableArray<string> DiagnosticCodes(
        ImmutableArray<DocumentValidationDiagnostic> diagnostics) => diagnostics.IsDefaultOrEmpty
            ? []
            : [.. diagnostics
                .Select(static diagnostic => diagnostic.Code)
                .Where(static code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];

    sealed record MaterializationRebuildOperationEvidence(
        string SchemaVersion,
        string Code,
        string? PlanAlgorithm,
        string? PlanCanonicalization,
        string? PlanFingerprint,
        string? ProcessInstanceId,
        string? ProcessAttemptId,
        string? StartedAtUtc,
        string? ShardId,
        string? GenerationId,
        string? Disposition,
        int? Pages,
        string? Outputs,
        ImmutableArray<string> DiagnosticCodes);
}
