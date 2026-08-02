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

    static readonly MaterializationId Materialization = new("materialization/test");

    static readonly MaterializationTargetId Target = new("target/test");

    static readonly MaterializationPlacementSliceReference PlacementSlice = CreatePlacementSlice();

    static readonly MaterializationRebuildPlanReference PlanReference =
        new(PlanFingerprint, PlacementSlice.Fingerprint);

    static readonly MaterializationRebuildLeafExecutionAuthority LeafAuthority =
        CreateAuthority(planSetDigest: 'e');

    static readonly ImmutableArray<MaterializationRebuildShardId> Shards =
        [new("shard-b"), new("shard-a")];

    [Fact]
    public void WorkReferences_RoundTripCanonicalPlanAttemptAndShardEvidence()
    {
        var planReference = PlanReference;
        var shardReference = new MaterializationRebuildShardWorkReference(
            LeafAuthority,
            CoordinatorAttempt,
            new("shard-a"));

        var planJson = MaterializationRebuildWorkReferenceJsonSerializer.SerializePlan(planReference);
        var authorityJson = MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(LeafAuthority);
        var shardJson = MaterializationRebuildWorkReferenceJsonSerializer.SerializeShard(shardReference);
        var changedAttemptJson = MaterializationRebuildWorkReferenceJsonSerializer.SerializeShard(
            new(
                LeafAuthority,
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
            LeafAuthority,
            MaterializationRebuildWorkReferenceJsonSerializer.DeserializeAuthority(authorityJson));
        Assert.Equal(PlacementSlice.Fingerprint, planReference.PlacementSlice);
        Assert.Equal(
            shardReference,
            MaterializationRebuildWorkReferenceJsonSerializer.DeserializeShard(shardJson));
        Assert.NotEqual(shardJson, changedAttemptJson);
        Assert.Throws<JsonException>(() =>
            MaterializationRebuildWorkReferenceJsonSerializer.DeserializeShard(
                shardJson.Replace(
                    MaterializationRebuildShardWorkReference.CurrentSchemaVersion,
                    "cohesive-materialization-rebuild-shard-work-reference/v1",
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
            MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(LeafAuthority),
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
    public async Task Initialization_RejectsAnAuthorityForAnotherPlanSetBeforeExecution()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var beginCalls = 0;
        var execution = Execution(begin: _ =>
        {
            beginCalls++;
            return Task.FromResult(InitializationResult());
        });
        var adapter = new MaterializationRebuildInitializationDurableOperationAdapter(
            artifacts.InitializationRequest,
            new ExactResolver(execution));
        var request = Request(
            artifacts.InitializationRequest,
            MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(
                CreateAuthority(planSetDigest: 'f')),
            CoordinatorAttempt.Continuation,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorInitializationNodeId);

        var observation = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.InitializationBinding, artifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestFailureOutcome>(observation.Outcome);
        var evidence = Assert.IsType<ObservationValue>(outcome.Value.Value).GetRequiredString();

        Assert.Equal(0, beginCalls);
        Assert.Contains(
            MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionInexact,
            evidence,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SynchronizationActivation_RejectsAnAuthorityForAnotherPlanSetBeforeExecution()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var activationCalls = 0;
        var execution = Execution(activate: (_, _, _) =>
        {
            activationCalls++;
            return Task.FromException<MaterializationGenerationActivationResult>(
                new InvalidOperationException("A mismatched placement must be rejected before activation I/O."));
        });
        var adapter = new MaterializationSynchronizationActivationDurableOperationAdapter(
            artifacts.SynchronizationActivationRequest,
            new ExactResolver(execution));
        var request = Request(
            artifacts.SynchronizationActivationRequest,
            MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(
                CreateAuthority(planSetDigest: 'f')),
            CoordinatorAttempt.Continuation,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorSynchronizationActivationNodeId);

        var observation = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.SynchronizationActivationBinding, artifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestFailureOutcome>(observation.Outcome);
        var evidence = Assert.IsType<ObservationValue>(outcome.Value.Value).GetRequiredString();

        Assert.Equal(0, activationCalls);
        Assert.Contains(
            MaterializationRebuildDurableOperationDiagnosticCodes.ExecutionInexact,
            evidence,
            StringComparison.Ordinal);
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
    public async Task SynchronizationWorkRemaining_UsesLogicalEmissionAndExplicitPhysicalAttemptFenceEvidence()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        List<(MaterializationSynchronizationInvocationId Invocation,
            MaterializationSynchronizationWorkerId Worker)> calls = [];
        var synchronization = new MaterializationSynchronizationRunResult(
            MaterializationSynchronizationRunDisposition.WorkRemaining,
            Generation,
            feeds: [],
            receipt: null);
        var result = new MaterializationGenerationActivationResult(
            MaterializationGenerationActivationDisposition.WorkRemaining,
            Generation,
            synchronization);
        var execution = Execution(activate: (_, invocation, worker) =>
        {
            calls.Add((invocation, worker));
            return Task.FromResult(result);
        });
        var adapter = new MaterializationSynchronizationActivationDurableOperationAdapter(
            artifacts.SynchronizationActivationRequest,
            new ExactResolver(execution));
        var request = Request(
            artifacts.SynchronizationActivationRequest,
            MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(LeafAuthority),
            CoordinatorAttempt.Continuation,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorSynchronizationActivationNodeId);

        var executed = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.SynchronizationActivationBinding, artifacts.InteractionCatalog)));
        var reconciled = Assert.IsType<DurableOperationReconciledOutcome>(
            await ReconcileAsync(
                request,
                artifacts.SynchronizationActivationBinding,
                artifacts.InteractionCatalog,
                adapter));

        Assert.Equal(2, calls.Count);
        Assert.All(calls, static call => Assert.Equal(
            "durable-operation/emission/materialization-rebuild",
            call.Invocation.Value));
        Assert.Equal(
            "durable-operation-attempt/operation-attempt/1/fence/1",
            calls[0].Worker.Value);
        Assert.Equal(
            "durable-operation-attempt/operation-attempt/failed/fence/1",
            calls[1].Worker.Value);
        var executedOutcome = Assert.IsType<RequestResultOutcome>(executed.Outcome);
        var reconciledOutcome = Assert.IsType<RequestResultOutcome>(reconciled.Outcome);
        Assert.Equal(MaterializationRebuildProcessFactory.WorkRemainingOutcome, executedOutcome.Id);
        Assert.Equal(executedOutcome, reconciledOutcome);
        Assert.StartsWith(
            "cohesive-materialization-synchronization-progress/v1:sha256:",
            Assert.IsType<ObservationValue>(executedOutcome.Value.Value).GetRequiredString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SynchronizationAmbiguousException_PropagatesAndReconcileRetriesTheLogicalRequest()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        List<(MaterializationSynchronizationInvocationId Invocation,
            MaterializationSynchronizationWorkerId Worker)> calls = [];
        var synchronization = new MaterializationSynchronizationRunResult(
            MaterializationSynchronizationRunDisposition.WorkRemaining,
            Generation,
            feeds: [],
            receipt: null);
        var retained = new MaterializationGenerationActivationResult(
            MaterializationGenerationActivationDisposition.WorkRemaining,
            Generation,
            synchronization);
        var execution = Execution(activate: (_, invocation, worker) =>
        {
            calls.Add((invocation, worker));
            return calls.Count == 1
                ? Task.FromException<MaterializationGenerationActivationResult>(
                    new InjectedAmbiguousActivationException())
                : Task.FromResult(retained);
        });
        var adapter = new MaterializationSynchronizationActivationDurableOperationAdapter(
            artifacts.SynchronizationActivationRequest,
            new ExactResolver(execution));
        var request = Request(
            artifacts.SynchronizationActivationRequest,
            MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(LeafAuthority),
            CoordinatorAttempt.Continuation,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorSynchronizationActivationNodeId);

        await Assert.ThrowsAsync<InjectedAmbiguousActivationException>(async () =>
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.SynchronizationActivationBinding, artifacts.InteractionCatalog)));
        var reconciled = Assert.IsType<DurableOperationReconciledOutcome>(
            await ReconcileAsync(
                request,
                artifacts.SynchronizationActivationBinding,
                artifacts.InteractionCatalog,
                adapter));

        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0].Invocation, calls[1].Invocation);
        Assert.NotEqual(calls[0].Worker, calls[1].Worker);
        Assert.Equal(
            MaterializationRebuildProcessFactory.WorkRemainingOutcome,
            Assert.IsType<RequestResultOutcome>(reconciled.Outcome).Id);
    }

    [Fact]
    public void ActiveGenerationReference_RoundTripsCanonicalVersionedPromotionEvidence()
    {
        var reference = new MaterializationActiveGenerationReference(
            schemaVersion: MaterializationActiveGenerationReference.CurrentSchemaVersion,
            authority: LeafAuthority,
            generation: Generation,
            targetRevision: new("3"),
            promotion: new("promotion/test"),
            promotionFence: new("7"),
            validation: new("validation/test"),
            activatedAtUtc: StartedAtUtc);

        var json = MaterializationActiveGenerationReferenceJsonSerializer.Serialize(reference);
        var restored = MaterializationActiveGenerationReferenceJsonSerializer.Deserialize(json);

        Assert.Equal(reference, restored);
        Assert.Equal(LeafAuthority, restored.Authority);
        Assert.Equal(PlanFingerprint, restored.Plan);
        Assert.Equal(PlacementSlice, restored.PlacementSlice);
        Assert.Equal(Materialization, restored.Materialization);
        Assert.Equal(Target, restored.Target);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("materialization", out _));
        Assert.False(document.RootElement.TryGetProperty("target", out _));
        Assert.Equal(
            json,
            MaterializationActiveGenerationReferenceJsonSerializer.Serialize(restored));
        Assert.Throws<JsonException>(() =>
            MaterializationActiveGenerationReferenceJsonSerializer.Deserialize(
                json.Replace(
                    MaterializationActiveGenerationReference.CurrentSchemaVersion,
                    "cohesive-materialization-active-generation-reference/v1",
                    StringComparison.Ordinal)));
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
            LeafAuthority,
            Shards,
            replacementAttempt,
            new("generation/replacement"),
            Materialization,
            Target,
            _ => Task.FromResult(InitializationResult()),
            (_, _) =>
            {
                calls++;
                return Task.FromResult(ShardResult(
                    MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
                    diagnostics: []));
            },
            (_, _, _) => Task.FromException<MaterializationGenerationActivationResult>(
                new InvalidOperationException("This test does not activate a generation.")));
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
            MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(LeafAuthority),
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
        Func<OperationContext, MaterializationRebuildShardId, Task<MaterializationRebuildShardResult>>? run = null,
        Func<OperationContext, MaterializationSynchronizationInvocationId, MaterializationSynchronizationWorkerId,
            Task<MaterializationGenerationActivationResult>>? activate = null) =>
        new(
            LeafAuthority,
            Shards,
            CoordinatorAttempt,
            Generation,
            Materialization,
            Target,
            begin ?? (_ => Task.FromResult(InitializationResult())),
            run ?? ((_, _) => Task.FromResult(ShardResult(
                MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
                diagnostics: []))),
            activate ?? ((_, _, _) => Task.FromException<MaterializationGenerationActivationResult>(
                new InvalidOperationException("This test does not activate a generation."))));

    static MaterializationPlacementSliceReference CreatePlacementSlice()
    {
        var definition = new ExecutionDefinitionFingerprint(
            algorithm: "sha256",
            canonicalization: "tests/materialization-definition/v1",
            value: new string('b', 64));
        MaterializationDefinitionReference materialization = new(
            MaterializationDefinitionReference.CurrentSchemaVersion,
            Materialization,
            definition);
        MaterializationBackendPoolReference pool = new(
            MaterializationBackendPoolReference.CurrentSchemaVersion,
            new("pool/test"),
            materialization,
            new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-pool/v1",
                value: new string('c', 64)));
        MaterializationRebuildMembershipFingerprint membership = new(
            algorithm: "sha256",
            canonicalization: "tests/materialization-membership/v1",
            value: new string('d', 64));
        return MaterializationPlacementSliceReference.Create(
            materialization,
            membership,
            pool,
            Target,
            [new("subject/test")]);
    }

    static MaterializationRebuildLeafExecutionAuthority CreateAuthority(char planSetDigest)
    {
        MaterializationRebuildRequestReference request = new(
            MaterializationRebuildRequestReference.CurrentSchemaVersion,
            PlacementSlice.Materialization,
            new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-rebuild-request/v1",
                value: new string('d', 64)));
        MaterializationRebuildPlanSetReference planSet = new(
            MaterializationRebuildPlanSetReference.CurrentSchemaVersion,
            request,
            new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-rebuild-plan-set/v1",
                value: new string(planSetDigest, 64)));
        return new(
            MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
            planSet,
            new(PlacementSlice, PlanReference));
    }

    sealed class InjectedAmbiguousActivationException()
        : Exception("Injected ambiguous synchronization-and-activation I/O failure.");

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
                new(LeafAuthority, CoordinatorAttempt, shard)),
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
            MaterializationRebuildLeafExecutionAuthority authority,
            ProcessContinuationIdentity continuation,
            out MaterializationRebuildExecution? resolved)
        {
            LastContinuation = continuation;
            resolved = authority.LeafPlan.Plan == execution.PlanFingerprint
                && continuation == execution.Attempt.Continuation
                ? execution
                : null;
            return resolved is not null;
        }
    }

    sealed class PermissiveResolver(MaterializationRebuildExecution execution)
        : IMaterializationRebuildExecutionResolver
    {
        public bool TryResolve(
            MaterializationRebuildLeafExecutionAuthority authority,
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
            MaterializationRebuildLeafExecutionAuthority authority,
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
