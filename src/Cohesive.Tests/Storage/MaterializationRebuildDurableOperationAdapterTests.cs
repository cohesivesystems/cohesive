using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationRebuildDurableOperationAdapterTests
{
    static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    static readonly MaterializationRebuildPlanFingerprint PlanFingerprint = new(
        algorithm: "sha256",
        canonicalization: "tests/materialization-rebuild-plan/v1",
        value: new string('a', 64));

    static readonly MaterializationRebuildAttempt CoordinatorAttempt = new(
        continuation: new(
            processInstanceId: new("process/materialization-rebuild/1"),
            processAttemptId: new("attempt/1")),
        startedAtUtc: StartedAtUtc);

    static readonly MaterializationGenerationId Generation = new("generation/materialization-rebuild/1");

    static readonly ImmutableArray<MaterializationRebuildShardId> Shards =
        [new("shard-b"), new("shard-a")];

    [Fact]
    public void WorkReferences_RoundTripCanonicalPlanAttemptAndShardEvidence()
    {
        var planReference = new MaterializationRebuildPlanReference(PlanFingerprint);
        var shardReference = new MaterializationRebuildShardWorkReference(
            PlanFingerprint,
            CoordinatorAttempt,
            new("shard-a"));

        var planJson = MaterializationRebuildWorkReferenceJsonSerializer.SerializePlan(planReference);
        var shardJson = MaterializationRebuildWorkReferenceJsonSerializer.SerializeShard(shardReference);
        var changedAttemptJson = MaterializationRebuildWorkReferenceJsonSerializer.SerializeShard(
            new(
                PlanFingerprint,
                new(
                    new(
                        CoordinatorAttempt.Continuation.ProcessInstanceId,
                        new("attempt/2")),
                    StartedAtUtc.AddMinutes(1)),
                new("shard-a")));

        Assert.Equal(
            planReference,
            MaterializationRebuildWorkReferenceJsonSerializer.DeserializePlan(planJson));
        Assert.Equal(
            shardReference,
            MaterializationRebuildWorkReferenceJsonSerializer.DeserializeShard(shardJson));
        Assert.NotEqual(shardJson, changedAttemptJson);
        Assert.Throws<JsonException>(() =>
            MaterializationRebuildWorkReferenceJsonSerializer.DeserializeShard(
                shardJson.Replace(
                    MaterializationRebuildShardWorkReference.CurrentSchemaVersion,
                    "cohesive-materialization-rebuild-shard-work-reference/v2",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Initialization_UsesCurrentOriginAndReturnsCanonicalAttemptBoundShardWork()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var initialized = InitializationResult();
        var beginCalls = 0;
        var execution = Execution(
            begin: _ =>
            {
                beginCalls++;
                return Task.FromResult(initialized);
            });
        var resolver = new ExactResolver(execution);
        var adapter = new MaterializationRebuildInitializationDurableOperationAdapter(
            artifacts.InitializationRequest,
            resolver);
        var request = Request(
            artifacts.InitializationRequest,
            MaterializationRebuildWorkReferenceJsonSerializer.SerializePlan(
                new(PlanFingerprint)),
            CoordinatorAttempt.Continuation,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId);

        var observation = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.InitializationBinding, artifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestResultOutcome>(observation.Outcome);
        var reconciled = Assert.IsType<DurableOperationReconciledOutcome>(
            await ReconcileAsync(
                request,
                artifacts.InitializationBinding,
                artifacts.InteractionCatalog,
                adapter));
        var encodedWork = Assert.IsType<ObservationValue>(outcome.Value.Value).Array;
        var work = encodedWork
            .Select(static item => MaterializationRebuildWorkReferenceJsonSerializer.DeserializeShard(
                item.GetRequiredString()))
            .ToImmutableArray();

        Assert.Equal(2, beginCalls);
        Assert.Equal(DurableOperationReconciliationCapability.Supported, adapter.Capabilities.Reconciliation);
        Assert.Equal(observation.Outcome, reconciled.Outcome);
        Assert.Equal(CoordinatorAttempt.Continuation, resolver.LastContinuation);
        Assert.Equal(["shard-a", "shard-b"], work.Select(static item => item.Shard.Value));
        Assert.All(work, item =>
        {
            Assert.Equal(PlanFingerprint, item.Plan);
            Assert.Equal(CoordinatorAttempt, item.Attempt);
        });
    }

    [Fact]
    public async Task ShardExecuteAndReconcile_RerunSameIdempotentOperationAndReturnIdenticalEvidence()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        List<MaterializationRebuildShardId> calls = [];
        var completed = ShardResult(
            MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
            diagnostics: []);
        var execution = Execution(run: (_, shard) =>
        {
            calls.Add(shard);
            return Task.FromResult(completed);
        });
        var adapter = new MaterializationRebuildShardDurableOperationAdapter(
            artifacts.ShardRebuildRequest,
            new ExactResolver(execution));
        var request = ShardRequest(artifacts, new("shard-a"));

        var executed = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.ShardRebuildBinding, artifacts.InteractionCatalog)));
        var reconciled = Assert.IsType<DurableOperationReconciledOutcome>(
            await ReconcileAsync(
                request,
                artifacts.ShardRebuildBinding,
                artifacts.InteractionCatalog,
                adapter));

        Assert.Equal([new MaterializationRebuildShardId("shard-a"), new("shard-a")], calls);
        Assert.Equal(DurableOperationReconciliationCapability.Supported, adapter.Capabilities.Reconciliation);
        Assert.Equal(executed.Outcome, reconciled.Outcome);
        var outcome = Assert.IsType<RequestResultOutcome>(executed.Outcome);
        Assert.Equal(MaterializationRebuildProcessFactory.CompletedOutcome, outcome.Id);
        var evidence = Assert.IsType<ObservationValue>(outcome.Value.Value).GetRequiredString();
        Assert.Contains("baseline-complete/catch-up-required", evidence, StringComparison.Ordinal);
        Assert.Contains("\"outputs\":\"5\"", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("failure message", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShardFailure_ProjectsTypedFailureUsingSortedPortableDiagnosticCodes()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        ImmutableArray<DocumentValidationDiagnostic> diagnostics =
        [
            Diagnostic("z.failure", "provider-specific failure message"),
            Diagnostic("a.failure", "another provider-specific message")
        ];
        var failed = ShardResult(MaterializationRebuildShardDisposition.TargetFailed, diagnostics);
        var execution = Execution(run: (_, _) => Task.FromResult(failed));
        var adapter = new MaterializationRebuildShardDurableOperationAdapter(
            artifacts.ShardRebuildRequest,
            new ExactResolver(execution));

        var observed = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(
                    ShardRequest(artifacts, new("shard-a")),
                    artifacts.ShardRebuildBinding,
                    artifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestFailureOutcome>(observed.Outcome);
        var evidence = Assert.IsType<ObservationValue>(outcome.Value.Value).GetRequiredString();

        Assert.Equal(MaterializationRebuildProcessFactory.FailedOutcome, outcome.Id);
        Assert.Contains("\"code\":\"a.failure\"", evidence, StringComparison.Ordinal);
        Assert.Contains("\"diagnosticCodes\":[\"a.failure\",\"z.failure\"]", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-specific", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleAttemptResolution_FailsTypedWithoutRunningShard()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var calls = 0;
        var replacementAttempt = new MaterializationRebuildAttempt(
            new(
                CoordinatorAttempt.Continuation.ProcessInstanceId,
                new("attempt/replacement")),
            StartedAtUtc.AddMinutes(1));
        var execution = new MaterializationRebuildExecution(
            PlanFingerprint,
            Shards,
            replacementAttempt,
            new("generation/replacement"),
            _ => Task.FromResult(InitializationResult()),
            (_, _) =>
            {
                calls++;
                return Task.FromResult(ShardResult(
                    MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
                    diagnostics: []));
            });
        var adapter = new MaterializationRebuildShardDurableOperationAdapter(
            artifacts.ShardRebuildRequest,
            new PermissiveResolver(execution));

        var observed = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(
                    ShardRequest(artifacts, new("shard-a")),
                    artifacts.ShardRebuildBinding,
                    artifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestFailureOutcome>(observed.Outcome);
        var evidence = Assert.IsType<ObservationValue>(outcome.Value.Value).GetRequiredString();

        Assert.Equal(0, calls);
        Assert.Contains(
            MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionInexact,
            evidence,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializationReconcile_TransientExecutionUnavailabilityRemainsUnresolved()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var resolver = new RejectingResolver();
        var adapter = new MaterializationRebuildInitializationDurableOperationAdapter(
            artifacts.InitializationRequest,
            resolver);
        var request = Request(
            artifacts.InitializationRequest,
            MaterializationRebuildWorkReferenceJsonSerializer.SerializePlan(new(PlanFingerprint)),
            CoordinatorAttempt.Continuation,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId);

        var observation = await ReconcileAsync(
            request,
            artifacts.InitializationBinding,
            artifacts.InteractionCatalog,
            adapter);

        Assert.IsType<DurableOperationUnresolved>(observation);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task ShardReconcile_TransientExecutionUnavailabilityRemainsUnresolved()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var resolver = new RejectingResolver();
        var adapter = new MaterializationRebuildShardDurableOperationAdapter(
            artifacts.ShardRebuildRequest,
            resolver);

        var observation = await ReconcileAsync(
            ShardRequest(artifacts, new("shard-a")),
            artifacts.ShardRebuildBinding,
            artifacts.InteractionCatalog,
            adapter);

        Assert.IsType<DurableOperationUnresolved>(observation);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task InvalidShardReference_FailsTypedWithoutResolvingExecution()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var resolver = new RejectingResolver();
        var adapter = new MaterializationRebuildShardDurableOperationAdapter(
            artifacts.ShardRebuildRequest,
            resolver);
        var request = Request(
            artifacts.ShardRebuildRequest,
            "not-json",
            new(new("process/worker/1"), new("attempt/worker/1")),
            artifacts.WorkerPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.WorkerRequestNodeId);

        var observed = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.ShardRebuildBinding, artifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestFailureOutcome>(observed.Outcome);
        var evidence = Assert.IsType<ObservationValue>(outcome.Value.Value).GetRequiredString();

        Assert.Equal(0, resolver.Calls);
        Assert.Contains(
            MaterializationRebuildDurableOperationDiagnosticCodes.WorkReferenceInvalid,
            evidence,
            StringComparison.Ordinal);
    }

    static MaterializationRebuildExecution Execution(
        Func<OperationContext, Task<MaterializationRebuildInitializationResult>>? begin = null,
        Func<OperationContext, MaterializationRebuildShardId, Task<MaterializationRebuildShardResult>>? run = null) =>
        new(
            PlanFingerprint,
            Shards,
            CoordinatorAttempt,
            Generation,
            begin ?? (_ => Task.FromResult(InitializationResult())),
            run ?? ((_, _) => Task.FromResult(ShardResult(
                MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
                diagnostics: []))));

    static MaterializationRebuildInitializationResult InitializationResult()
    {
        var progress = Shards
            .OrderBy(static shard => shard.Value, StringComparer.Ordinal)
            .Select(shard => Progress(shard, Generation))
            .ToImmutableArray();
        return new(
            MaterializationRebuildInitializationDisposition.Ready,
            Generation,
            new MaterializationGenerationSnapshot(
                materializationId: new("materialization/test"),
                generationId: Generation,
                definitionFingerprint: DefinitionFingerprint(),
                state: MaterializationGenerationState.Loading,
                revision: MaterializationGenerationRevision.Initial,
                latestWorkerFence: MaterializationWorkerFence.Initial,
                hasPermanentFailures: false,
                pendingRetryableMutationCount: 0,
                visibleItemCount: 0,
                tombstoneCount: 0,
                sealReceipt: null,
                validationReceipt: null,
                createdAtUtc: StartedAtUtc,
                inactivatedAtUtc: null,
                retiredAtUtc: null),
            progress);
    }

    static MaterializationRebuildShardResult ShardResult(
        MaterializationRebuildShardDisposition disposition,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics) => new(
            disposition,
            new("shard-a"),
            Generation,
            pages: 2,
            outputs: 5,
            Progress(
                new("shard-a"),
                Generation,
                baselineCompleted: disposition
                    == MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired),
            diagnostics);

    static MaterializationProgressSnapshot Progress(
        MaterializationRebuildShardId shard,
        MaterializationGenerationId generation,
        bool baselineCompleted = false)
    {
        var scope = Scope(shard);
        MaterializationSourcePosition changePosition = new(
            formatVersion: 1,
            scope,
            value: $"change-position/{shard.Value}");
        MaterializationApplicationCheckpoint changeCheckpoint = new(
            id: new($"change-checkpoint/{shard.Value}"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: changePosition,
            appliedDeliveries: [],
            committedAtUtc: StartedAtUtc,
            evidenceReference: "tests/materialization-rebuild-change-cut",
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(changePosition));
        MaterializationApplicationCheckpoint? batchCheckpoint = baselineCompleted
            ? new(
                id: new($"batch-checkpoint/{shard.Value}"),
                kind: MaterializationCheckpointKind.BatchCompleted,
                continuation: null,
                completion: new(
                    scope,
                    readFingerprint: new(
                        algorithm: "sha256",
                        canonicalization: "tests/materialization-rebuild-read/v1",
                        value: new string('d', 64)),
                    evidenceState: RelationQuerySourceReadState.Complete,
                    evidenceReference: "tests/materialization-rebuild-read-complete"),
                position: null,
                appliedDeliveries: [],
                committedAtUtc: StartedAtUtc.AddSeconds(1),
                batchPageOrdinal: 2)
            : null;
        return new(
            new MaterializationProgressKey(
                materialization: new("materialization/test"),
                definitionFingerprint: DefinitionFingerprint(),
                generation,
                scope),
            baselineCompleted ? new("3") : new("2"),
            MaterializationProgressFence.Initial,
            fenceOwner: $"owner/{shard.Value}",
            latestBatchCheckpoint: batchCheckpoint,
            latestChangeCheckpoint: changeCheckpoint);
    }

    static MaterializationSourceScope Scope(MaterializationRebuildShardId shard)
    {
        QualifiedShapeId shape = new(new("graph/test"), new("shape/test"));
        RelationQuerySourcePlacementBinding placement = new(
            id: new("placement/test"),
            input: new("input/test"),
            node: new("node/test"),
            binding: new("binding/test"),
            shape,
            source: new("source/test"),
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit);
        return new(
            physicalPlan: new(
                algorithm: "sha256",
                canonicalization: "tests/physical-plan/v1",
                value: new string('b', 64)),
            placement,
            partition: new($"partition/{shard.Value}"),
            orderingScope: new($"ordering/{shard.Value}"));
    }

    static ExecutionDefinitionFingerprint DefinitionFingerprint() => new(
        algorithm: "sha256",
        canonicalization: "tests/materialization-definition/v1",
        value: new string('c', 64));

    static RequestEnvelope ShardRequest(
        MaterializationRebuildProcessArtifacts artifacts,
        MaterializationRebuildShardId shard) => Request(
            artifacts.ShardRebuildRequest,
            MaterializationRebuildWorkReferenceJsonSerializer.SerializeShard(
                new(PlanFingerprint, CoordinatorAttempt, shard)),
            new(new("process/worker/1"), new("attempt/worker/1")),
            artifacts.WorkerPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.WorkerRequestNodeId);

    static RequestEnvelope Request(
        RequestContractReference contract,
        string payload,
        ProcessContinuationIdentity originContinuation,
        ExecutionDefinitionReference originDefinition,
        ExecutionNodeId originNode)
    {
        InteractionAuthorityScope authority = new("authority/tests", "tenant/tests");
        InteractionIdempotencyKey idempotency = new("idempotency/materialization-rebuild");
        return new(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                emissionId: new("emission/materialization-rebuild"),
                origin: new ProcessInteractionOrigin(
                    originDefinition,
                    originNode,
                    originContinuation,
                    new("activation/materialization-rebuild"),
                    new("token/materialization-rebuild")),
                correlationId: new("correlation/materialization-rebuild"),
                causationId: null,
                authority,
                idempotency,
                ordering: null,
                delivery: new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                provenance: new(
                    new ExecutionProducerProvenance("tests", "1"),
                    new ExecutionSourceProvenance("tests/materialization-rebuild-adapter"),
                    DocumentOrigin.Generated)),
            contract,
            PortableValue.Concrete(
                new(new ScalarTypeRef(ScalarTypeKind.String)),
                ObservationValue.FromString(payload)),
            new ProcessTokenInteractionTarget(
                originContinuation,
                new("token/materialization-rebuild/response")));
    }

    static DurableOperationInvocation Invocation(
        RequestEnvelope request,
        DurableRequestBinding binding,
        InteractionContractCatalog catalog)
    {
        var executor = new DurableOperationReferenceExecutor(catalog);
        var validation = executor.TryCreate(request, binding, StartedAtUtc, out var created);
        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        var claimed = executor.Claim(
            Assert.IsType<DurableOperationState>(created),
            new("operation-attempt/1"),
            claimant: "worker/tests",
            observedAtUtc: StartedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            StartedAtUtc.AddSeconds(1));
        return Assert.IsType<DurableOperationInvocation>(dispatched.Invocation);
    }

    static async Task<DurableOperationReconciliationObservation> ReconcileAsync(
        RequestEnvelope request,
        DurableRequestBinding binding,
        InteractionContractCatalog catalog,
        IDurableOperationAdapter adapter)
    {
        var executor = new DurableOperationReferenceExecutor(catalog);
        var validation = executor.TryCreate(request, binding, StartedAtUtc, out var created);
        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        var claimed = executor.Claim(
            Assert.IsType<DurableOperationState>(created),
            new("operation-attempt/failed"),
            claimant: "worker/tests",
            observedAtUtc: StartedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            StartedAtUtc.AddSeconds(1));
        var failed = executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            new DurableOperationFailureObservation(new(
                DurableOperationFailurePhase.PostCommitPreAcknowledgement,
                DurableOperationEffectEvidence.Ambiguous,
                DurableOperationFailureDisposition.Retryable,
                "tests.outcome.ambiguous")),
            StartedAtUtc.AddSeconds(2));
        Assert.Equal(DurableOperationRecoveryRequirement.Reconcile, failed.State.RecoveryRequirement);
        return await DurableOperationReferenceExecutor.ReconcileAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(StartedAtUtc.AddSeconds(3))),
            failed.State,
            adapter);
    }

    static DocumentValidationDiagnostic Diagnostic(string code, string message) => new(
        code,
        DiagnosticSeverity.Error,
        message,
        "/execution");

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed class ExactResolver(MaterializationRebuildExecution execution)
        : IMaterializationRebuildExecutionResolver
    {
        public ProcessContinuationIdentity? LastContinuation { get; private set; }

        public bool TryResolve(
            MaterializationRebuildPlanFingerprint plan,
            ProcessContinuationIdentity continuation,
            out MaterializationRebuildExecution? resolved)
        {
            LastContinuation = continuation;
            resolved = plan == execution.PlanFingerprint && continuation == execution.Attempt.Continuation
                ? execution
                : null;
            return resolved is not null;
        }
    }

    sealed class PermissiveResolver(MaterializationRebuildExecution execution)
        : IMaterializationRebuildExecutionResolver
    {
        public bool TryResolve(
            MaterializationRebuildPlanFingerprint plan,
            ProcessContinuationIdentity continuation,
            out MaterializationRebuildExecution? resolved)
        {
            resolved = execution;
            return true;
        }
    }

    sealed class RejectingResolver : IMaterializationRebuildExecutionResolver
    {
        public int Calls { get; private set; }

        public bool TryResolve(
            MaterializationRebuildPlanFingerprint plan,
            ProcessContinuationIdentity continuation,
            out MaterializationRebuildExecution? execution)
        {
            Calls++;
            execution = null;
            return false;
        }
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
