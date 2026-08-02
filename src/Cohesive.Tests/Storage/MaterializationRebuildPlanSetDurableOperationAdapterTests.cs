using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationRebuildPlanSetDurableOperationAdapterTests
{
    static readonly DateTimeOffset StartedAtUtc = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Initialization_ReturnsExactCanonicalCapacityAwareLeafWork()
    {
        var leaf = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(leaf);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var resolver = new ExactResolver(planSet);
        var continuation = new ProcessContinuationIdentity(
            new("process/plan-set/tests"),
            new("attempt/plan-set/tests/1"));
        var request = Request(
            artifacts.InitializationRequest,
            MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(
                MaterializationRebuildPlanSetReference.FromPlanSet(planSet)),
            continuation,
            artifacts.ParentPlan.DefinitionReference,
            MaterializationRebuildPlanSetProcessFactory.InitializationNodeId);
        var adapter = new MaterializationRebuildPlanSetInitializationDurableOperationAdapter(
            artifacts.InitializationRequest,
            resolver,
            artifacts.ParentPlan);

        var observed = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.InitializationBinding, artifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestResultOutcome>(observed.Outcome);
        var item = Assert.Single(Assert.IsType<ObservationValue>(outcome.Value.Value).Array);

        Assert.True(item.TryGetProperty("sliceId", out var slice));
        Assert.True(item.TryGetProperty("capacityDomain", out var capacity));
        Assert.True(item.TryGetProperty("payload", out var payload));
        var authority = MaterializationRebuildWorkReferenceJsonSerializer.DeserializeAuthority(payload.String!);
        Assert.Equal(planSet.LeafPlans.Single().Slice.Id.Value, slice.String);
        Assert.Equal(planSet.Placement.CapacityBindings.Single().CapacityDomain.Value, capacity.String);
        Assert.Equal(MaterializationRebuildPlanSetReference.FromPlanSet(planSet), authority.PlanSet);
        Assert.Equal(planSet.LeafPlans.Single(), authority.Binding);
    }

    [Fact]
    public async Task Initialization_RejectsPlanSetFromAnotherParentSpecializationEvenWhenResolvable()
    {
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []));
        var foreignPlanSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], [], maximumPageItems: 99));
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var continuation = new ProcessContinuationIdentity(
            new("process/plan-set/foreign-specialization"),
            new("attempt/plan-set/foreign-specialization/1"));
        var request = Request(
            artifacts.InitializationRequest,
            MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(
                MaterializationRebuildPlanSetReference.FromPlanSet(foreignPlanSet)),
            continuation,
            artifacts.ParentPlan.DefinitionReference,
            MaterializationRebuildPlanSetProcessFactory.InitializationNodeId);
        var adapter = new MaterializationRebuildPlanSetInitializationDurableOperationAdapter(
            artifacts.InitializationRequest,
            new SetResolver([planSet, foreignPlanSet]),
            artifacts.ParentPlan);

        var observed = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.InitializationBinding, artifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestFailureOutcome>(observed.Outcome);
        var evidence = Assert.IsType<ObservationValue>(outcome.Value.Value).String;

        Assert.NotNull(evidence);
        Assert.Contains(
            MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact,
            evidence,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadinessBarrier_RejectsPlanSetFromAnotherParentSpecializationBeforeStoreAccess()
    {
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []));
        var foreignPlanSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], [], maximumPageItems: 99));
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var continuation = new ProcessContinuationIdentity(
            new("process/plan-set/foreign-barrier"),
            new("attempt/plan-set/foreign-barrier/1"));
        var request = Request(
            artifacts.ReadinessBarrierRequest,
            MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(
                MaterializationRebuildPlanSetReference.FromPlanSet(foreignPlanSet)),
            continuation,
            artifacts.ParentPlan.DefinitionReference,
            MaterializationRebuildPlanSetProcessFactory.ReadinessBarrierNodeId);
        var adapter = new MaterializationRebuildReadyBarrierDurableOperationAdapter(
            artifacts.ReadinessBarrierRequest,
            new SetResolver([planSet, foreignPlanSet]),
            new InMemoryProcessDurableStore(),
            artifacts.ParentPlan);

        var observed = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.ReadinessBarrierBinding, artifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestFailureOutcome>(observed.Outcome);

        Assert.Contains(
            MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact,
            Assert.IsType<ObservationValue>(outcome.Value.Value).GetRequiredString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finalization_RejectsBarrierFromAnotherParentSpecializationBeforeStoreAccess()
    {
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []));
        var foreignPlanSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(
            MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], [], maximumPageItems: 99));
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var continuation = new ProcessContinuationIdentity(
            new("process/plan-set/foreign-finalization"),
            new("attempt/plan-set/foreign-finalization/1"));
        var foreignBarrier = new MaterializationRebuildReadyBarrier(
            MaterializationRebuildReadyBarrier.CurrentSchemaVersion,
            MaterializationRebuildPlanSetReference.FromPlanSet(foreignPlanSet),
            continuation,
            readyGenerations: []);
        var request = Request(
            artifacts.FinalizeRequest,
            MaterializationRebuildReadyBarrierJsonSerializer.Serialize(foreignBarrier),
            continuation,
            artifacts.ParentPlan.DefinitionReference,
            MaterializationRebuildPlanSetProcessFactory.FinalizeNodeId);
        var adapter = new MaterializationRebuildPlanSetFinalizationDurableOperationAdapter(
            artifacts.FinalizeRequest,
            new SetResolver([planSet, foreignPlanSet]),
            new InMemoryProcessDurableStore(),
            artifacts.ParentPlan);

        var observed = Assert.IsType<DurableOperationOutcomeObservation>(
            await adapter.ExecuteAsync(
                OperationContext.Create(),
                Invocation(request, artifacts.FinalizeBinding, artifacts.InteractionCatalog)));
        var outcome = Assert.IsType<RequestFailureOutcome>(observed.Outcome);

        Assert.Contains(
            MaterializationRebuildPlanSetDurableOperationDiagnosticCodes.ExecutionInexact,
            Assert.IsType<ObservationValue>(outcome.Value.Value).GetRequiredString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentPromotionExecutor_UsesExactLinkedAuthorityWithoutFullLeafReload()
    {
        var leaf = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(leaf);
        var reference = MaterializationRebuildPlanSetReference.FromPlanSet(planSet);
        var authority = new MaterializationRebuildLeafExecutionAuthority(
            MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
            reference,
            planSet.LeafPlans.Single());

        _ = new MaterializationIndependentPromotionExecutor(planSet, authority);

        var foreign = new MaterializationRebuildLeafExecutionAuthority(
            MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
            new(
                reference.SchemaVersion,
                reference.Request,
                new(
                    reference.PlanSet.Algorithm,
                    reference.PlanSet.Canonicalization,
                    new string('f', 64))),
            authority.Binding);
        Assert.Throws<ArgumentException>(() =>
            new MaterializationIndependentPromotionExecutor(planSet, foreign));
    }

    [Fact]
    public void ReadyProjection_RejectsMissingLedgerWithoutInventingChildAuthority()
    {
        var leaf = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(leaf);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var continuation = new ProcessContinuationIdentity(
            new("process/plan-set/missing-ledger"),
            new("attempt/plan-set/missing-ledger/1"));
        var start = Start(artifacts, planSet, continuation);
        var state = ProcessReferenceInterpreter.Create(artifacts.ParentPlan, start);
        var checkpoint = new ProcessDurableCheckpoint(
            ProcessDurableCheckpoint.CurrentSchemaVersion,
            start,
            state,
            start.CreateInitialState(),
            createdAtUtc: StartedAtUtc,
            updatedAtUtc: StartedAtUtc);

        Assert.Throws<ArgumentException>(() =>
            PlanSetProjection.ProjectBuildLeaves(planSet, checkpoint, out _));
    }

    [Fact]
    public async Task Initialization_BindingAndAdapterCapabilitiesRunThroughDurableRuntime()
    {
        var leaf = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(leaf);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var resolver = new ExactResolver(planSet);
        var adapter = new MaterializationRebuildPlanSetInitializationDurableOperationAdapter(
            artifacts.InitializationRequest,
            resolver,
            artifacts.ParentPlan);
        var store = new InMemoryProcessDurableStore();
        var runtime = new ProcessDurableRuntime(
            store,
            RejectingHost.Instance,
            new(
                workerId: "worker/plan-set/tests",
                workerLease: TimeSpan.FromMinutes(1)),
            bindingResolver: new ExactBindingResolver([artifacts.InitializationBinding]),
            operationAdapterResolver: new ExactAdapterResolver(adapter));
        var continuation = new ProcessContinuationIdentity(
            new("process/plan-set/runtime"),
            new("attempt/plan-set/runtime/1"));
        var start = Start(artifacts, planSet, continuation);
        var context = OperationContext.Create(timeProvider: new FixedTimeProvider(StartedAtUtc));

        var initialized = await runtime.InitializeAsync(context, artifacts.ParentPlan, start);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, initialized.Disposition);
        var activated = await runtime.ActivateAsync(
            context,
            artifacts.ParentPlan,
            continuation,
            new(
                id: new("activation/plan-set/runtime/start"),
                cause: ProcessActivationCause.Start,
                observedAtUtc: StartedAtUtc,
                context: new(
                    authorityScope: new("authority/plan-set/tests", "tenant/tests"),
                    correlationId: new("correlation/plan-set/runtime"),
                    delivery: new(
                        InteractionDurabilityDemand.Durable,
                        InteractionVisibilityDemand.AfterOriginCommit),
                    provenance: artifacts.ParentProcessDocument.Metadata.Provenance)));
        var operation = Assert.Single(
            Assert.IsType<ProcessDurableStoreSnapshot>(activated.Snapshot).Checkpoint.DurableOperations);

        var advanced = await runtime.AdvanceOperationAsync(
            context,
            artifacts.ParentPlan,
            continuation.ProcessInstanceId,
            operation.OperationId);

        Assert.Equal(DurableOperationStatus.Dispositioned, advanced.Operation?.Status);
        Assert.NotNull(advanced.Operation?.Acknowledgement);
    }

    [Fact]
    public void ProcessBindings_DeclareTheExactAdapterIdempotencyEvidence()
    {
        var leaf = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(
            MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(leaf));

        Assert.Equal(DurableOperationIdempotencyEvidence.NaturallyIdempotent, artifacts.InitializationBinding.IdempotencyEvidence);
        Assert.Equal(DurableOperationIdempotencyEvidence.TargetDeduplication, artifacts.LeafInvocationBinding.IdempotencyEvidence);
        Assert.Equal(DurableOperationIdempotencyEvidence.NaturallyIdempotent, artifacts.ReadinessBarrierBinding.IdempotencyEvidence);
        Assert.Equal(DurableOperationIdempotencyEvidence.TargetDeduplication, artifacts.PromotionInvocationBinding.IdempotencyEvidence);
        Assert.Equal(DurableOperationIdempotencyEvidence.TargetDeduplication, artifacts.ActivateReadyBinding.IdempotencyEvidence);
        Assert.Equal(DurableOperationIdempotencyEvidence.NaturallyIdempotent, artifacts.PreparePromotionBinding.IdempotencyEvidence);
        Assert.Equal(DurableOperationIdempotencyEvidence.TargetDeduplication, artifacts.ApplyPromotionBinding.IdempotencyEvidence);
        Assert.Equal(DurableOperationIdempotencyEvidence.NaturallyIdempotent, artifacts.FinalizeBinding.IdempotencyEvidence);
    }

    static RequestEnvelope Request(
        RequestContractReference contract,
        string payload,
        ProcessContinuationIdentity continuation,
        ExecutionDefinitionReference definition,
        ExecutionNodeId node) => new(
        InteractionEnvelope.CurrentSchemaVersion,
        new(
            emissionId: new("emission/plan-set/tests"),
            origin: new ProcessInteractionOrigin(
                definition,
                node,
                continuation,
                new("activation/plan-set/tests"),
                new("token/plan-set/tests")),
            correlationId: new("correlation/plan-set/tests"),
            causationId: null,
            authorityScope: new("authority/plan-set/tests", "tenant/tests"),
            idempotencyKey: new("idempotency/plan-set/tests"),
            ordering: null,
            delivery: new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            provenance: Provenance()),
        contract,
        PortableValue.Concrete(
            new(new ScalarTypeRef(ScalarTypeKind.String)),
            ObservationValue.FromString(payload)),
        new ProcessTokenInteractionTarget(continuation, new("token/plan-set/tests/response")));

    static DurableOperationInvocation Invocation(
        RequestEnvelope request,
        DurableRequestBinding binding,
        InteractionContractCatalog catalog)
    {
        var executor = new DurableOperationReferenceExecutor(catalog);
        var validation = executor.TryCreate(request, binding, StartedAtUtc, out var created);
        Assert.True(validation.IsValid, Format(validation));
        var claimed = executor.Claim(
            Assert.IsType<DurableOperationState>(created),
            new("operation-attempt/plan-set/tests"),
            claimant: "worker/plan-set/tests",
            observedAtUtc: StartedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            StartedAtUtc.AddSeconds(1));
        return Assert.IsType<DurableOperationInvocation>(dispatched.Invocation);
    }

    static ProcessStartReceipt Start(
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        MaterializationRebuildPlanSet planSet,
        ProcessContinuationIdentity continuation)
    {
        var request = new ProcessStartRequest(
            ProcessStartRequest.CurrentSchemaVersion,
            artifacts.ParentPlan.DefinitionReference,
            new(
                commandId: new("command/plan-set/tests/start"),
                idempotencyKey: new("idempotency/plan-set/tests/start"),
                processInstanceId: continuation.ProcessInstanceId,
                authorization: new(
                    actor: "operator/plan-set/tests",
                    authorityScope: new("authority/plan-set/tests", "tenant/tests"),
                    evidenceReference: "policy/plan-set/tests/allow"),
                issuedAtUtc: StartedAtUtc,
                provenance: Provenance()),
            continuation,
            PortableValue.Concrete(
                artifacts.ParentPlan.Definition.Input,
                ObservationValue.FromString(MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(
                    MaterializationRebuildPlanSetReference.FromPlanSet(planSet)))));
        return new(request, StartedAtUtc);
    }

    static ExecutionProvenance Provenance() => new(
        new ExecutionProducerProvenance("cohesive-tests", "1"),
        new ExecutionSourceProvenance("tests:materialization-rebuild-plan-set-adapters"),
        DocumentOrigin.Generated);

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed class ExactResolver(MaterializationRebuildPlanSet planSet)
        : IMaterializationRebuildPlanSetExecutionResolver
    {
        public bool TryResolve(
            MaterializationRebuildPlanSetReference reference,
            out MaterializationRebuildPlanSet? resolvedPlanSet)
        {
            var exact = reference == MaterializationRebuildPlanSetReference.FromPlanSet(planSet);
            resolvedPlanSet = exact ? planSet : null;
            return exact;
        }
    }

    sealed class SetResolver(ImmutableArray<MaterializationRebuildPlanSet> planSets)
        : IMaterializationRebuildPlanSetExecutionResolver
    {
        public bool TryResolve(
            MaterializationRebuildPlanSetReference reference,
            out MaterializationRebuildPlanSet? resolvedPlanSet)
        {
            resolvedPlanSet = planSets.SingleOrDefault(
                candidate => MaterializationRebuildPlanSetReference.FromPlanSet(candidate) == reference);
            return resolvedPlanSet is not null;
        }
    }

    sealed class ExactBindingResolver(ImmutableArray<DurableRequestBinding> bindings)
        : IProcessDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? binding)
        {
            binding = bindings.FirstOrDefault(candidate => candidate.Request == request.Contract);
            return binding is not null;
        }
    }

    sealed class ExactAdapterResolver(IDurableOperationAdapter adapter)
        : IProcessDurableOperationAdapterResolver
    {
        public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? resolved)
        {
            resolved = adapter.Capabilities.Supports(request.Contract) ? adapter : null;
            return resolved is not null;
        }
    }

    sealed class RejectingHost : IProcessReferenceHost
    {
        internal static RejectingHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation evaluation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }

    sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

}
