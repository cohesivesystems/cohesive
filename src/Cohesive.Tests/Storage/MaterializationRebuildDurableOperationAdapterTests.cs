using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
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

    static MaterializationGenerationId Generation =>
        MaterializationRebuildIdentities.Generation(LeafAuthority, CoordinatorAttempt);

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
    public async Task SynchronizationPreparation_RejectsAnAuthorityForAnotherPlanSetBeforeExecution()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var activationCalls = 0;
        var execution = Execution(activate: (_, _, _) =>
        {
            activationCalls++;
            return Task.FromException<MaterializationGenerationActivationResult>(
                new InvalidOperationException("A mismatched placement must be rejected before activation I/O."));
        });
        var adapter = new MaterializationSynchronizationPreparationDurableOperationAdapter(
            artifacts.SynchronizationPreparationRequest,
            new ExactResolver(execution));
        var request = Request(
            artifacts.SynchronizationPreparationRequest,
            MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(
                CreateAuthority(planSetDigest: 'f')),
            CoordinatorAttempt.Continuation,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorSynchronizationPreparationNodeId);

        var observation = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.SynchronizationPreparationBinding, artifacts.InteractionCatalog)));
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
        var adapter = new MaterializationSynchronizationPreparationDurableOperationAdapter(
            artifacts.SynchronizationPreparationRequest,
            new ExactResolver(execution));
        var request = Request(
            artifacts.SynchronizationPreparationRequest,
            MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(LeafAuthority),
            CoordinatorAttempt.Continuation,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorSynchronizationPreparationNodeId);

        var executed = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.SynchronizationPreparationBinding, artifacts.InteractionCatalog)));
        var reconciled = Assert.IsType<DurableOperationReconciledOutcome>(
            await ReconcileAsync(
                request,
                artifacts.SynchronizationPreparationBinding,
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
        var adapter = new MaterializationSynchronizationPreparationDurableOperationAdapter(
            artifacts.SynchronizationPreparationRequest,
            new ExactResolver(execution));
        var request = Request(
            artifacts.SynchronizationPreparationRequest,
            MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(LeafAuthority),
            CoordinatorAttempt.Continuation,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorSynchronizationPreparationNodeId);

        await Assert.ThrowsAsync<InjectedAmbiguousActivationException>(async () =>
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.SynchronizationPreparationBinding, artifacts.InteractionCatalog)));
        var reconciled = Assert.IsType<DurableOperationReconciledOutcome>(
            await ReconcileAsync(
                request,
                artifacts.SynchronizationPreparationBinding,
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
    public void ReadyGenerationReference_RoundTripsCanonicalExactPreparationEvidence()
    {
        var preparation = ReadyPreparation();
        var reference = new MaterializationReadyGenerationReference(
            schemaVersion: MaterializationReadyGenerationReference.CurrentSchemaVersion,
            authority: LeafAuthority,
            attempt: CoordinatorAttempt,
            generation: Generation,
            preparation: preparation);

        var json = MaterializationReadyGenerationReferenceJsonSerializer.Serialize(reference);
        var restored = MaterializationReadyGenerationReferenceJsonSerializer.Deserialize(json);

        Assert.Equal(reference, restored);
        Assert.Equal(LeafAuthority, restored.Authority);
        Assert.Equal(CoordinatorAttempt, restored.Attempt);
        Assert.Equal(Generation, restored.Generation);
        Assert.Equal(preparation.Convergence, restored.Convergence);
        Assert.Equal(preparation.ValidationReceipt, restored.Validation);
        Assert.Equal(preparation.PromotionRequest, restored.PromotionIntent);
        Assert.True(restored.Preparation.IsReady);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("plan", out _));
        Assert.False(document.RootElement.TryGetProperty("placementSlice", out _));
        Assert.Equal(json, MaterializationReadyGenerationReferenceJsonSerializer.Serialize(restored));
        Assert.Throws<JsonException>(() =>
            MaterializationReadyGenerationReferenceJsonSerializer.Deserialize(
                json.Replace(
                    MaterializationReadyGenerationReference.CurrentSchemaVersion,
                    "cohesive-materialization-ready-generation-reference/v0",
                    StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() =>
            MaterializationReadyGenerationReferenceJsonSerializer.Deserialize(
                json[..^1] + ",\"unexpected\":true}"));
    }

    [Fact]
    public void ReadyBarrier_CreateRejectsMissingLinkedLeafEvidence()
    {
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []));
        ProcessContinuationIdentity parent = new(
            processInstanceId: new("process/materialization-rebuild-plan-set/barrier"),
            processAttemptId: new("attempt/parent/1"));

        var exception = Assert.Throws<ArgumentException>(() => MaterializationRebuildReadyBarrier.Create(
            planSet: planSet,
            parentContinuation: parent,
            readyGenerations: []));

        Assert.Contains("every linked leaf", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadyBarrier_RoundTripsAndRejectsDuplicateOrForeignEvidence()
    {
        var ready = new MaterializationReadyGenerationReference(
            schemaVersion: MaterializationReadyGenerationReference.CurrentSchemaVersion,
            authority: LeafAuthority,
            attempt: CoordinatorAttempt,
            generation: Generation,
            preparation: ReadyPreparation());
        ProcessContinuationIdentity parent = new(
            processInstanceId: new("process/materialization-rebuild-plan-set/barrier"),
            processAttemptId: new("attempt/parent/1"));
        var barrier = new MaterializationRebuildReadyBarrier(
            schemaVersion: MaterializationRebuildReadyBarrier.CurrentSchemaVersion,
            planSet: LeafAuthority.PlanSet,
            parentContinuation: parent,
            readyGenerations: [ready]);

        var json = MaterializationRebuildReadyBarrierJsonSerializer.Serialize(barrier);
        var restored = MaterializationRebuildReadyBarrierJsonSerializer.DeserializeStructural(json);

        Assert.Equal(barrier, restored);
        Assert.Equal(json, MaterializationRebuildReadyBarrierJsonSerializer.Serialize(restored));
        Assert.Throws<ArgumentException>(() => new MaterializationRebuildReadyBarrier(
            schemaVersion: barrier.SchemaVersion,
            planSet: barrier.PlanSet,
            parentContinuation: parent,
            readyGenerations: [ready, ready]));
        Assert.Throws<ArgumentException>(() => new MaterializationRebuildReadyBarrier(
            schemaVersion: barrier.SchemaVersion,
            planSet: CreateAuthority(planSetDigest: 'f').PlanSet,
            parentContinuation: parent,
            readyGenerations: [ready]));
    }

    [Fact]
    public async Task SynchronizationPreparation_ReturnsCanonicalReadyEvidenceWithoutApplyingActivation()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var preparation = ReadyPreparation();
        var activationCalls = 0;
        var execution = Execution(
            activate: (_, _, _) =>
            {
                activationCalls++;
                return Task.FromException<MaterializationGenerationActivationResult>(
                    new InvalidOperationException("Preparation must not invoke composed activation."));
            },
            prepare: (_, _, _) => Task.FromResult(new MaterializationGenerationActivationResult(
                MaterializationGenerationActivationDisposition.Ready,
                Generation,
                activation: preparation)));
        var adapter = new MaterializationSynchronizationPreparationDurableOperationAdapter(
            artifacts.SynchronizationPreparationRequest,
            new ExactResolver(execution));
        var request = Request(
            artifacts.SynchronizationPreparationRequest,
            MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(LeafAuthority),
            CoordinatorAttempt.Continuation,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorSynchronizationPreparationNodeId);

        var observation = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.SynchronizationPreparationBinding, artifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestResultOutcome>(observation.Outcome);
        var ready = MaterializationReadyGenerationReferenceJsonSerializer.Deserialize(
            Assert.IsType<ObservationValue>(outcome.Value.Value).GetRequiredString());

        Assert.Equal(MaterializationRebuildProcessFactory.ReadyOutcome, outcome.Id);
        Assert.Equal(0, activationCalls);
        Assert.Equal(execution.Authority, ready.Authority);
        Assert.Equal(execution.Attempt, ready.Attempt);
        Assert.Equal(execution.Generation, ready.Generation);
        Assert.Equal(preparation, ready.Preparation);
    }

    [Fact]
    public async Task ReadyActivation_ResolvesTheRetainedChildAttemptAndReturnsCanonicalActiveEvidence()
    {
        var planSetArtifacts = CreatePlanSetArtifacts();
        var preparation = ReadyPreparation();
        var ready = new MaterializationReadyGenerationReference(
            schemaVersion: MaterializationReadyGenerationReference.CurrentSchemaVersion,
            authority: LeafAuthority,
            attempt: CoordinatorAttempt,
            generation: Generation,
            preparation: preparation);
        var requestIntent = preparation.PromotionRequest!;
        MaterializationPromotionReceipt promotion = new(
            promotionId: requestIntent.PromotionId,
            targetId: Target,
            generationId: Generation,
            previousGenerationId: requestIntent.ExpectedActiveGenerationId,
            targetRevision: new("1"),
            generationWorkerFence: requestIntent.GenerationWorkerFence,
            promotionFence: requestIntent.PromotionFence,
            validationFingerprint: requestIntent.ValidationFingerprint,
            promotedAtUtc: requestIntent.PromotedAtUtc);
        var completed = new MaterializationGenerationActivationState(
            preparation.Convergence,
            preparation.SealRequest,
            preparation.SealReceipt,
            preparation.ValidationRequest,
            preparation.ValidationReceipt,
            requestIntent,
            promotion);
        var activeResult = new MaterializationGenerationActivationResult(
            MaterializationGenerationActivationDisposition.Active,
            Generation,
            activation: completed,
            target: new(
                targetId: Target,
                materializationId: Materialization,
                revision: promotion.TargetRevision,
                activeGenerationId: Generation,
                latestPromotionFence: promotion.PromotionFence,
                retainedGenerationCount: 1));
        MaterializationReadyGenerationReference? observedReady = null;
        var execution = Execution(activateReady: (_, observed) =>
        {
            observedReady = observed;
            return Task.FromResult(activeResult);
        });
        var resolver = new ExactResolver(execution);
        var adapter = new MaterializationReadyGenerationActivationDurableOperationAdapter(
            planSetArtifacts.ActivateReadyRequest,
            resolver,
            planSetArtifacts.PromotionWorkerPlan);
        ProcessContinuationIdentity parentContinuation = new(
            processInstanceId: new("process/materialization-rebuild/parent"),
            processAttemptId: new("attempt/parent/1"));
        var request = Request(
            planSetArtifacts.ActivateReadyRequest,
            MaterializationReadyGenerationReferenceJsonSerializer.Serialize(ready),
            parentContinuation,
            planSetArtifacts.PromotionWorkerPlan.DefinitionReference,
            MaterializationRebuildPlanSetProcessFactory.PromotionActivateNodeId);

        var observation = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(
                    request,
                    planSetArtifacts.ActivateReadyBinding,
                    planSetArtifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestResultOutcome>(observation.Outcome);
        var active = MaterializationActiveGenerationReferenceJsonSerializer.Deserialize(
            Assert.IsType<ObservationValue>(outcome.Value.Value).GetRequiredString());

        Assert.Equal(MaterializationRebuildProcessFactory.ActiveOutcome, outcome.Id);
        Assert.Equal(ready, observedReady);
        Assert.Equal(CoordinatorAttempt.Continuation, resolver.LastContinuation);
        Assert.NotEqual(parentContinuation, resolver.LastContinuation);
        Assert.Equal(LeafAuthority, active.Authority);
        Assert.Equal(Generation, active.Generation);
        Assert.Equal(Target, active.Target);
        Assert.Equal(promotion.TargetRevision, active.TargetRevision);
        Assert.Equal(promotion.PromotionId, active.Promotion);
    }

    [Fact]
    public async Task ReadyActivation_RejectsForeignProcessOriginBeforeTargetPromotion()
    {
        var artifacts = MaterializationRebuildProcessFactory.Create();
        var planSetArtifacts = CreatePlanSetArtifacts();
        var preparation = ReadyPreparation();
        var ready = new MaterializationReadyGenerationReference(
            schemaVersion: MaterializationReadyGenerationReference.CurrentSchemaVersion,
            authority: LeafAuthority,
            attempt: CoordinatorAttempt,
            generation: Generation,
            preparation);
        var activationCalls = 0;
        var execution = Execution(activateReady: (_, _) =>
        {
            activationCalls++;
            return Task.FromException<MaterializationGenerationActivationResult>(
                new InvalidOperationException("A foreign Process origin must not reach target activation."));
        });
        var adapter = new MaterializationReadyGenerationActivationDurableOperationAdapter(
            planSetArtifacts.ActivateReadyRequest,
            new ExactResolver(execution),
            planSetArtifacts.PromotionWorkerPlan);
        var request = Request(
            planSetArtifacts.ActivateReadyRequest,
            MaterializationReadyGenerationReferenceJsonSerializer.Serialize(ready),
            CoordinatorAttempt.Continuation,
            artifacts.CoordinatorPlan.DefinitionReference,
            MaterializationRebuildProcessFactory.CoordinatorSynchronizationPreparationNodeId);

        var observation = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(
                    request,
                    planSetArtifacts.ActivateReadyBinding,
                    planSetArtifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestFailureOutcome>(observation.Outcome);
        var evidence = Assert.IsType<ObservationValue>(outcome.Value.Value).GetRequiredString();

        Assert.Equal(0, activationCalls);
        Assert.Contains(
            MaterializationRebuildDurableOperationDiagnosticCodes.RequestOriginInvalid,
            evidence,
            StringComparison.Ordinal);
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
            Task<MaterializationGenerationActivationResult>>? activate = null,
        Func<OperationContext, MaterializationSynchronizationInvocationId, MaterializationSynchronizationWorkerId,
            Task<MaterializationGenerationActivationResult>>? prepare = null,
        Func<OperationContext, MaterializationReadyGenerationReference,
            Task<MaterializationGenerationActivationResult>>? activateReady = null) =>
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
                new InvalidOperationException("This test does not activate a generation."))),
            synchronizeAndPrepare: prepare,
            activateReady: activateReady);

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

    static MaterializationRebuildPlanSetProcessArtifacts CreatePlanSetArtifacts()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan);
        return MaterializationRebuildPlanSetProcessFactory.Create(planSet);
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

    static MaterializationGenerationActivationState ReadyPreparation()
    {
        var scope = Scope(new("shard-a"));
        MaterializationSourcePosition position = new(
            formatVersion: 1,
            scope,
            value: "position/ready");
        MaterializationSynchronizationWorkKey synchronization = new(
            materialization: Materialization,
            definitionFingerprint: PlacementSlice.Materialization.DefinitionFingerprint,
            rebuildPlanFingerprint: PlanFingerprint,
            impactPlanFingerprint: new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-impact-plan/v1",
                value: new string('f', 64)),
            generation: Generation);
        MaterializationConvergenceReceipt convergence = new(
            schemaVersion: MaterializationConvergenceReceipt.CurrentSchemaVersion,
            synchronization,
            feeds:
            [
                new(
                    feed: new("feed/ready"),
                    scope,
                    latestChangeCheckpoint: new("checkpoint/ready"),
                    throughPosition: position,
                    caughtUpReadStartedAtUtc: StartedAtUtc,
                    caughtUpReadCompletedAtUtc: StartedAtUtc.AddSeconds(1),
                    checkpointCommittedAtUtc: StartedAtUtc.AddSeconds(2),
                    settlementRequirement: MaterializationConvergenceSettlementRequirement.NotRequired)
            ],
            evaluatedAtUtc: StartedAtUtc.AddSeconds(3),
            freshnessDemand: new(maximumLagMilliseconds: 60_000),
            validation: DocumentValidationResult.Valid);
        MaterializationSealGenerationRequest sealRequest = new(
            sealId: new("seal/ready"),
            generationId: Generation,
            expectedRevision: new("10"),
            workerFence: new("1"),
            sealedAtUtc: StartedAtUtc.AddSeconds(4));
        MaterializationSealReceipt sealReceipt = new(
            sealId: sealRequest.SealId,
            generationId: Generation,
            generationRevision: new("11"),
            visibleItemCount: 7,
            fingerprint: new("seal-fingerprint/ready"),
            sealedAtUtc: sealRequest.SealedAtUtc);
        MaterializationValidateGenerationRequest validationRequest = new(
            validationId: new("validation/ready"),
            generationId: Generation,
            expectedRevision: sealReceipt.GenerationRevision,
            expectedSealFingerprint: sealReceipt.Fingerprint,
            expectedVisibleItemCount: sealReceipt.VisibleItemCount,
            validator: "tests/activation-validator/v1",
            workerFence: sealRequest.WorkerFence,
            validatedAtUtc: StartedAtUtc.AddSeconds(5));
        MaterializationValidationReceipt validationReceipt = new(
            validationId: validationRequest.ValidationId,
            generationId: Generation,
            generationRevision: new("12"),
            sealFingerprint: sealReceipt.Fingerprint,
            fingerprint: new("validation-fingerprint/ready"),
            validation: DocumentValidationResult.Valid,
            validatedAtUtc: validationRequest.ValidatedAtUtc);
        MaterializationPromoteGenerationRequest promotionRequest = new(
            promotionId: new("promotion/ready"),
            generationId: Generation,
            expectedGenerationRevision: validationReceipt.GenerationRevision,
            validationFingerprint: validationReceipt.Fingerprint,
            expectedActiveGenerationId: null,
            expectedTargetRevision: MaterializationTargetRevision.Initial,
            generationWorkerFence: sealRequest.WorkerFence,
            promotionFence: MaterializationPromotionFence.Initial,
            promotedAtUtc: StartedAtUtc.AddSeconds(6));
        return new(
            convergence,
            sealRequest,
            sealReceipt,
            validationRequest,
            validationReceipt,
            promotionRequest);
    }

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
