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
}

/// <summary>
/// Exact attempt-scoped runtime binding resolved from a persisted rebuild-plan fingerprint and Process continuation.
/// </summary>
/// <remarks>
/// The public constructor binds one <see cref="ResolvedMaterializationRebuildPlan"/> to its reference executor. The
/// adapter consumes only the canonical plan fingerprint, shard catalog, attempt, and expected generation so runtime
/// objects do not leak into durable Request payloads.
/// </remarks>
public sealed class MaterializationRebuildExecution
{
    readonly Func<OperationContext, Task<MaterializationRebuildInitializationResult>> beginAttempt;
    readonly Func<OperationContext, MaterializationRebuildShardId, Task<MaterializationRebuildShardResult>> runShard;
    readonly Func<OperationContext, DateTimeOffset, Task<bool>> abandonAttempt;

    /// <summary>Creates one exact attempt-scoped reference execution binding.</summary>
    /// <param name="resolved">Exact persisted plan and runtime ports.</param>
    /// <param name="attempt">Exact coordinator Process attempt and its stable UTC start time.</param>
    /// <param name="crashInjector">Optional deterministic conformance and crash-injection hook.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resolved"/> or <paramref name="attempt"/> is <see langword="null"/>.
    /// </exception>
    public MaterializationRebuildExecution(
        ResolvedMaterializationRebuildPlan resolved,
        MaterializationRebuildAttempt attempt,
        IMaterializationRebuildCrashInjector? crashInjector = null)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        Attempt = Guard.RequireNotNull(attempt);
        PlanFingerprint = resolved.Plan.Fingerprint;
        Shards = NormalizeShards(resolved.Plan.Shards.Select(static shard => shard.Id));
        Generation = MaterializationRebuildIdentities.Generation(resolved.Plan, attempt);
        var executor = new MaterializationRebuildExecutor(resolved, crashInjector);
        beginAttempt = context => executor.BeginAttemptAsync(context, attempt);
        runShard = (context, shard) => executor.RunShardAsync(context, attempt, shard);
        abandonAttempt = (context, abandonedAtUtc) =>
            executor.AbandonAttemptAsync(context, attempt, abandonedAtUtc);
    }

    internal MaterializationRebuildExecution(
        MaterializationRebuildPlanFingerprint planFingerprint,
        ImmutableArray<MaterializationRebuildShardId> shards,
        MaterializationRebuildAttempt attempt,
        MaterializationGenerationId generation,
        Func<OperationContext, Task<MaterializationRebuildInitializationResult>> beginAttempt,
        Func<OperationContext, MaterializationRebuildShardId, Task<MaterializationRebuildShardResult>> runShard,
        Func<OperationContext, DateTimeOffset, Task<bool>>? abandonAttempt = null)
    {
        PlanFingerprint = Guard.RequireNotNull(planFingerprint);
        Shards = NormalizeShards(shards);
        Attempt = Guard.RequireNotNull(attempt);
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        Generation = generation;
        this.beginAttempt = beginAttempt ?? throw new ArgumentNullException(nameof(beginAttempt));
        this.runShard = runShard ?? throw new ArgumentNullException(nameof(runShard));
        this.abandonAttempt = abandonAttempt ?? ((_, _) => Task.FromResult(false));
    }

    /// <summary>Exact persisted plan fingerprint resolved for this execution.</summary>
    public MaterializationRebuildPlanFingerprint PlanFingerprint { get; }

    /// <summary>Finite canonical shard catalog projected from the exact persisted plan.</summary>
    public ImmutableArray<MaterializationRebuildShardId> Shards { get; }

    /// <summary>Exact coordinator Process attempt and stable UTC start time.</summary>
    public MaterializationRebuildAttempt Attempt { get; }

    /// <summary>Deterministic candidate generation owned by <see cref="Attempt"/>.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Idempotently establishes the candidate generation and one initial change cut per shard.</summary>
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

/// <summary>Resolves one exact persisted rebuild plan, runtime binding, executor, and Process attempt.</summary>
/// <remarks>
/// Implementations should index persisted plans by their complete fingerprint and retain one stable UTC start time
/// for each exact coordinator continuation. Returning an earlier or replacement continuation is never a compatible
/// fallback; rejecting stale child work is part of the port's fencing contract.
/// </remarks>
public interface IMaterializationRebuildExecutionResolver
{
    /// <summary>Attempts to resolve one exact attempt-scoped rebuild execution.</summary>
    /// <param name="plan">Exact persisted rebuild-plan fingerprint.</param>
    /// <param name="continuation">Exact owning coordinator Process continuation.</param>
    /// <param name="execution">Receives the exact execution when available.</param>
    /// <returns><see langword="true"/> when the exact plan and attempt are currently resolvable.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="continuation"/> is <see langword="null"/>.
    /// </exception>
    bool TryResolve(
        MaterializationRebuildPlanFingerprint plan,
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
        if (!MaterializationRebuildWorkReferenceJsonSerializer.TryDeserializePlan(
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
                plan: reference.Plan);
        if (!resolver.TryResolve(reference.Plan, origin.Continuation, out var execution)
            || execution is null)
        {
            if (resolutionFailureMode == MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
                return null;
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionUnavailable,
                plan: reference.Plan,
                continuation: origin.Continuation);
        }
        if (execution.PlanFingerprint != reference.Plan
            || execution.Attempt.Continuation != origin.Continuation)
        {
            if (resolutionFailureMode == MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
                return null;
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionInexact,
                plan: reference.Plan,
                continuation: origin.Continuation);
        }

        var result = await execution.BeginAttemptAsync(context).ConfigureAwait(false);
        if (result.Generation != execution.Generation
            || result.Disposition == MaterializationRebuildInitializationDisposition.Ready
            && (result.GenerationSnapshot?.GenerationId != execution.Generation
                || result.Progress.Length != execution.Shards.Length
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
                execution.PlanFingerprint,
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
        if (!resolver.TryResolve(reference.Plan, reference.Attempt.Continuation, out var execution)
            || execution is null)
        {
            if (resolutionFailureMode == MaterializationRebuildExecutionResolutionFailureMode.RemainUnresolved)
                return null;
            return MaterializationRebuildDurableOperationProjection.Failure(
                MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionUnavailable,
                reference);
        }
        if (execution.PlanFingerprint != reference.Plan || execution.Attempt != reference.Attempt)
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
