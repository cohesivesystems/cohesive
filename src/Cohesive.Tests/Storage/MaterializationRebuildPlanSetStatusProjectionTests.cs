using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;
using Cohesive.Tests.ExecutionKernel;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationRebuildPlanSetStatusProjectionTests
{
    static readonly DateTimeOffset StartedAtUtc =
        new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    static readonly InteractionAuthorityScope Authority =
        new("authority/materialization-rebuild-plan-set-status", "tenant/cohesive");

    [Fact]
    public async Task CreateRuntimeDetails_ProjectsExactPlanSetAndCommonParentFacets()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var snapshot = await InitializeAsync(planSet, artifacts);
        var provenance = Provenance("status-observation");

        var runtime = MaterializationRebuildPlanSetStatusProjector.CreateRuntimeDetails(
            planSet,
            artifacts,
            snapshot,
            provenance);
        var extension = Assert.Single(runtime.Extensions);
        var root = Assert.IsType<ObservationValue>(extension.Value.Value!.Value);

        Assert.Equal(ExecutionStatusDisclosure.Disclosed, runtime.TokensDisclosure);
        Assert.Single(runtime.Tokens);
        Assert.Equal(MaterializationRebuildPlanSetProcessFactory.AdmissionNodeId, runtime.Tokens[0].Node);
        Assert.Equal(ExecutionStatusDisclosure.Disclosed, runtime.WaitsDisclosure);
        Assert.Empty(runtime.Waits);
        Assert.Equal(new ExecutionProgressStatus(0, 2, "leaf-phase"), runtime.Progress);
        Assert.Equal(new ExecutionDemandStatus(0, 0), runtime.Demand);
        Assert.Equal(new ExecutionCapacityStatus(0, 1), runtime.Capacity);
        Assert.Equal(ExecutionHealthStatus.Healthy, runtime.Health);
        Assert.Equal(MaterializationRebuildPlanSetStatusWireNames.ExtensionId, extension.Id);
        Assert.Equal(MaterializationRebuildPlanSetStatusWireNames.SchemaVersion, extension.SchemaVersion);
        Assert.Equal(provenance, extension.Provenance);
        Assert.Equal(
            MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(
                MaterializationRebuildPlanSetReference.FromPlanSet(planSet)),
            root.GetProperty("planSetReference").GetRequiredString());
        Assert.Equal(
            planSet.Request.Request.Value,
            root.GetProperty("requestFingerprint").GetProperty("value").GetRequiredString());
        Assert.Equal(
            planSet.Membership.Fingerprint.Value,
            root.GetProperty("membershipFingerprint").GetProperty("value").GetRequiredString());
        Assert.Equal(
            planSet.Placement.Fingerprint.Value,
            root.GetProperty("placementFingerprint").GetProperty("value").GetRequiredString());
        Assert.Equal(planSet.Promotion.Mode.ToString(), root.GetProperty("promotionMode").GetRequiredString());
        Assert.Equal(
            artifacts.ParentPlan.DefinitionReference.DefinitionId.Value,
            root.GetProperty("parentDefinition").GetProperty("definitionId").GetRequiredString());
        Assert.Equal(snapshot.Revision.Value, root.GetProperty("storageRevision").GetRequiredString());
        Assert.Equal(ExecutionTerminalOutcomeKind.None.ToString(), root.GetProperty("terminalOutcome").GetRequiredString());
        Assert.Equal(ObservationValueKind.Null, root.GetProperty("terminalDetail").Kind);
        var child = Assert.Single(root.GetProperty("children").EnumerateArray());
        Assert.Equal(planSet.LeafPlans[0].Slice.Id.Value, child.GetProperty("sliceId").GetRequiredString());
        Assert.Equal(planSet.LeafPlans[0].Slice.Target.Value, child.GetProperty("target").GetRequiredString());
        Assert.Equal(
            planSet.LeafPlans[0].Slice.Fingerprint.Value,
            child.GetProperty("placementSliceFingerprint").GetProperty("value").GetRequiredString());
        Assert.Equal(
            planSet.LeafPlans[0].Slice.Subjects.Select(static subject => subject.Value),
            child.GetProperty("subjects").EnumerateArray().Select(static subject => subject.GetRequiredString()));
        Assert.Equal(
            planSet.Placement.CapacityBindings[0].CapacityDomain.Value,
            child.GetProperty("capacityDomain").GetRequiredString());
        Assert.Equal(ObservationValueKind.Null, child.GetProperty("buildChild").Kind);
        Assert.Equal(ObservationValueKind.Null, child.GetProperty("buildTerminalOutcome").Kind);
        Assert.Equal(ObservationValueKind.Null, child.GetProperty("buildTerminalResult").Kind);
        Assert.Equal(ObservationValueKind.Null, child.GetProperty("promotionTerminalOutcome").Kind);
        Assert.Equal(ObservationValueKind.Null, child.GetProperty("promotionChildResult").Kind);
        Assert.Equal(ObservationValueKind.Null, child.GetProperty("leafOutcome").Kind);
        Assert.Equal(ObservationValueKind.Null, child.GetProperty("failureEvidence").Kind);
        Assert.Equal(ObservationValueKind.Null, root.GetProperty("readyBarrier").Kind);
        Assert.Equal(ObservationValueKind.Null, root.GetProperty("aggregateOutcome").Kind);
        Assert.Equal(ObservationValueKind.Null, root.GetProperty("aggregateReceipt").Kind);
        Assert.True(PortableExecutionValidator.Validate(extension.Value.Value!).IsValid);

        var status = ExecutionStatusProjector.Project(
            snapshot.Checkpoint.Control,
            runtime,
            snapshot.Checkpoint.Continuation.Terminal);
        Assert.Equal(snapshot.Checkpoint.Continuation.Continuation.ProcessInstanceId, status.ProcessInstanceId);
        Assert.Equal(runtime, status.Runtime);
    }

    [Fact]
    public async Task CreateExtension_RejectsArtifactsAndCheckpointsFromAnotherExactPlanSet()
    {
        var firstPlan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var firstPlanSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(firstPlan);
        var firstArtifacts = MaterializationRebuildPlanSetProcessFactory.Create(firstPlanSet);
        var firstSnapshot = await InitializeAsync(firstPlanSet, firstArtifacts);
        var secondPlan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan(
            [],
            [],
            maximumPageItems: 99);
        var secondPlanSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(secondPlan);
        var secondArtifacts = MaterializationRebuildPlanSetProcessFactory.Create(secondPlanSet);

        Assert.Throws<ArgumentException>(() => MaterializationRebuildPlanSetStatusProjector.CreateExtension(
            firstPlanSet,
            secondArtifacts,
            firstSnapshot,
            Provenance("foreign-artifacts")));
        Assert.Throws<ArgumentException>(() => MaterializationRebuildPlanSetStatusProjector.CreateExtension(
            secondPlanSet,
            secondArtifacts,
            firstSnapshot,
            Provenance("foreign-checkpoint")));
    }

    [Fact]
    public async Task CreateExtension_RejectsForeignStartInputUsingTheExactSpecializedParentDefinition()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process-instance/plan-set-status/foreign-start"),
            processAttemptId: new("process-attempt/1"));
        var exactStart = Start(planSet, artifacts, continuation);
        var exactReference = MaterializationRebuildPlanSetReference.FromPlanSet(planSet);
        var foreignReference = new MaterializationRebuildPlanSetReference(
            MaterializationRebuildPlanSetReference.CurrentSchemaVersion,
            exactReference.Request,
            new(
                exactReference.PlanSet.Algorithm,
                exactReference.PlanSet.Canonicalization,
                new string('f', 64)));
        var foreignStart = new ProcessStartReceipt(
            new(
                exactStart.Request.SchemaVersion,
                exactStart.Request.Definition,
                exactStart.Request.Context,
                exactStart.Request.InitialContinuation,
                PortableValue.Concrete(
                    artifacts.ParentPlan.Definition.Input,
                    ObservationValue.FromString(
                        MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(foreignReference)))),
            exactStart.AcceptedAtUtc);
        var runtime = new ProcessDurableRuntime(
            store: new InMemoryProcessDurableStore(),
            host: RejectingHost.Instance,
            options: new(
                workerId: "worker/plan-set-status/foreign-start",
                workerLease: TimeSpan.FromMinutes(5)));
        var initialized = await runtime.InitializeAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(StartedAtUtc)),
            artifacts.ParentPlan,
            foreignStart);
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);

        Assert.Throws<ArgumentException>(() => MaterializationRebuildPlanSetStatusProjector.CreateExtension(
            planSet,
            artifacts,
            snapshot,
            Provenance("foreign-start")));
    }

    [Fact]
    public async Task CreateExtension_RejectsCompletedParentWithoutCanonicalAggregateReceipt()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var initialized = await InitializeAsync(planSet, artifacts);
        var current = initialized.Checkpoint.Continuation;
        var terminal = new ExecutionTerminalOutcome(
            ExecutionTerminalOutcomeKind.Completed,
            StartedAtUtc,
            ExecutionStatusValue.Disclose(PortableValue.Concrete(
                artifacts.ParentPlan.Definition.Result,
                ObservationValue.FromString("not-an-aggregate-receipt"))));
        var completed = NewContinuation(
            current.Definition,
            current.Continuation,
            completedActivationCount: 0,
            tokens: [],
            forks: [],
            children: [],
            partitions: [],
            recurrences: [],
            waits: [],
            bufferedInputs: [],
            inputReceipts: [],
            outstandingRequests: [],
            terminal);
        var checkpoint = new ProcessDurableCheckpoint(
            initialized.Checkpoint.SchemaVersion,
            initialized.Checkpoint.Start,
            completed,
            initialized.Checkpoint.Control,
            createdAtUtc: initialized.Checkpoint.CreatedAtUtc,
            updatedAtUtc: initialized.Checkpoint.UpdatedAtUtc);
        var malformed = new ProcessDurableStoreSnapshot(
            checkpoint,
            new("2"),
            workerLease: null,
            localState: []);

        Assert.Throws<ArgumentException>(() =>
            MaterializationRebuildPlanSetStatusProjector.CreateExtension(
                planSet,
                artifacts,
                malformed,
                Provenance("completed-without-receipt")));
    }

    [Fact]
    public void StatusPath_IsDerivedFromCanonicalPlanSetAuthority()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var reference = MaterializationRebuildPlanSetReference.FromPlanSet(
            MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan));

        var path = MaterializationRebuildPlanSetStatusWireNames.StatusPath(reference);

        Assert.True(path.Segments.SequenceEqual(
        [
            "materializations",
            reference.Request.Materialization.Materialization.Value,
            "rebuildRequests",
            reference.Request.Request.Value,
            "planSets",
            MaterializationRebuildIdentities.PlanSetIdentity(reference),
            "executionStatus"
        ]));
    }

    [Fact]
    public void StatusPath_DoesNotAliasFingerprintCoordinatesWithTheSameValue()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var reference = MaterializationRebuildPlanSetReference.FromPlanSet(
            MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan));
        var foreignAlgorithm = new MaterializationRebuildPlanSetReference(
            reference.SchemaVersion,
            reference.Request,
            new("sha512", reference.PlanSet.Canonicalization, reference.PlanSet.Value));
        var foreignCanonicalization = new MaterializationRebuildPlanSetReference(
            reference.SchemaVersion,
            reference.Request,
            new(reference.PlanSet.Algorithm, "foreign-plan-set-c14n/v1", reference.PlanSet.Value));

        Assert.NotEqual(
            MaterializationRebuildPlanSetStatusWireNames.StatusPath(reference),
            MaterializationRebuildPlanSetStatusWireNames.StatusPath(foreignAlgorithm));
        Assert.NotEqual(
            MaterializationRebuildPlanSetStatusWireNames.StatusPath(reference),
            MaterializationRebuildPlanSetStatusWireNames.StatusPath(foreignCanonicalization));
    }

    [Fact]
    public async Task CreateExtension_RejectsACompatiblePartialBuildWorkSet()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process-instance/plan-set-status/partial-work"),
            processAttemptId: new("process-attempt/1"));
        var adapter = new DurableOperationFakeAdapter(
            supportedRequest: artifacts.InitializationRequest,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.NaturallyIdempotent);
        var runtime = new ProcessDurableRuntime(
            store: new InMemoryProcessDurableStore(),
            host: RejectingHost.Instance,
            options: new(
                workerId: "worker/plan-set-status-partial-work",
                workerLease: TimeSpan.FromMinutes(5)),
            bindingResolver: new ExactBindingResolver(
            [
                artifacts.InitializationBinding,
                artifacts.LeafInvocationBinding,
                artifacts.ReadinessBarrierBinding
            ]),
            operationAdapterResolver: new ExactAdapterResolver(adapter));
        var context = OperationContext.Create(timeProvider: new FixedTimeProvider(StartedAtUtc));
        var initialized = await runtime.InitializeAsync(
            context,
            artifacts.ParentPlan,
            Start(planSet, artifacts, continuation));
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, initialized.Disposition);

        var requested = await runtime.ActivateAsync(
            context,
            artifacts.ParentPlan,
            continuation,
            Activation(artifacts, "activation/plan-set-status/partial/request", ProcessActivationCause.Start, []));
        var requestSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(requested.Snapshot);
        var operation = Assert.Single(requestSnapshot.Checkpoint.DurableOperations);
        var initializationNode = Assert.IsType<RequestProcessNode>(
            artifacts.ParentPlan.GetNode(MaterializationRebuildPlanSetProcessFactory.InitializationNodeId));
        var workContract = Assert.IsType<ProcessOutputBinding>(initializationNode.Outcomes.Single(outcome =>
            outcome.Outcome == MaterializationRebuildPlanSetProcessFactory.CompletedOutcome).Continuation.Output).Contract;
        var partialWork = PortableValue.Concrete(workContract, ObservationValue.FromImmutableArray([]));
        adapter.Script(
            operation.OperationId,
            new DurableOperationOutcomeObservation(new RequestResultOutcome(
                MaterializationRebuildPlanSetProcessFactory.CompletedOutcome,
                partialWork)));

        var advanced = await runtime.AdvanceOperationAsync(
            context,
            artifacts.ParentPlan,
            continuation.ProcessInstanceId,
            operation.OperationId);
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.CompletedOutcome,
            advanced.Operation?.Acknowledgement?.Outcome.Id);
        var advancedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(advanced.Snapshot).Checkpoint;
        var resumed = await runtime.ActivateAsync(
            context,
            artifacts.ParentPlan,
            continuation,
            Activation(
                artifacts,
                "activation/plan-set-status/partial/admit",
                ProcessActivationCause.Interaction,
                PendingInputs(advancedCheckpoint)));
        var partialSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(resumed.Snapshot);
        Assert.Empty(Assert.Single(partialSnapshot.Checkpoint.Continuation.Partitions).Work);

        Assert.Throws<ArgumentException>(() => MaterializationRebuildPlanSetStatusProjector.CreateExtension(
            planSet,
            artifacts,
            partialSnapshot,
            Provenance("partial-work")));
    }

    [Fact]
    public async Task CreateRuntimeDetails_AdmitsExactCapacityBoundBuildWork()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan);
        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new("process-instance/plan-set-status/exact-work"),
            processAttemptId: new("process-attempt/1"));
        var adapter = new DurableOperationFakeAdapter(
            supportedRequest: artifacts.InitializationRequest,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.NaturallyIdempotent);
        var runtime = new ProcessDurableRuntime(
            store: new InMemoryProcessDurableStore(),
            host: RejectingHost.Instance,
            options: new(
                workerId: "worker/plan-set-status-exact-work",
                workerLease: TimeSpan.FromMinutes(5)),
            bindingResolver: new ExactBindingResolver(
            [
                artifacts.InitializationBinding,
                artifacts.LeafInvocationBinding,
                artifacts.ReadinessBarrierBinding
            ]),
            operationAdapterResolver: new ExactAdapterResolver(adapter));
        var context = OperationContext.Create(timeProvider: new FixedTimeProvider(StartedAtUtc));
        _ = await runtime.InitializeAsync(
            context,
            artifacts.ParentPlan,
            Start(planSet, artifacts, continuation));
        var requested = await runtime.ActivateAsync(
            context,
            artifacts.ParentPlan,
            continuation,
            Activation(artifacts, "activation/plan-set-status/exact/request", ProcessActivationCause.Start, []));
        var operation = Assert.Single(
            Assert.IsType<ProcessDurableStoreSnapshot>(requested.Snapshot).Checkpoint.DurableOperations);
        var initializationNode = Assert.IsType<RequestProcessNode>(
            artifacts.ParentPlan.GetNode(MaterializationRebuildPlanSetProcessFactory.InitializationNodeId));
        var workContract = Assert.IsType<ProcessOutputBinding>(initializationNode.Outcomes.Single(outcome =>
            outcome.Outcome == MaterializationRebuildPlanSetProcessFactory.CompletedOutcome).Continuation.Output).Contract;
        var binding = Assert.Single(planSet.LeafPlans);
        var authority = new MaterializationRebuildLeafExecutionAuthority(
            MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
            MaterializationRebuildPlanSetReference.FromPlanSet(planSet),
            binding);
        var workItem = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["progressId"] = ObservationValue.FromString(PlanSetProjection.ProgressIdentity(0, authority)),
            ["sliceId"] = ObservationValue.FromString(binding.Slice.Id.Value),
            ["capacityDomain"] = ObservationValue.FromString(
                Assert.Single(planSet.Placement.CapacityBindings).CapacityDomain.Value),
            ["payload"] = ObservationValue.FromString(
                MaterializationRebuildWorkReferenceJsonSerializer.SerializeAuthority(authority))
        });
        adapter.Script(
            operation.OperationId,
            new DurableOperationOutcomeObservation(new RequestResultOutcome(
                MaterializationRebuildPlanSetProcessFactory.CompletedOutcome,
                PortableValue.Concrete(workContract, ObservationValue.FromImmutableArray([workItem])))));
        var advanced = await runtime.AdvanceOperationAsync(
            context,
            artifacts.ParentPlan,
            continuation.ProcessInstanceId,
            operation.OperationId);
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.CompletedOutcome,
            advanced.Operation?.Acknowledgement?.Outcome.Id);
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(advanced.Snapshot).Checkpoint;
        var resumed = await runtime.ActivateAsync(
            context,
            artifacts.ParentPlan,
            continuation,
            Activation(
                artifacts,
                "activation/plan-set-status/exact/admit",
                ProcessActivationCause.Interaction,
                PendingInputs(checkpoint)));
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(resumed.Snapshot);
        Assert.Single(snapshot.Checkpoint.Continuation.Partitions);
        var retainedChild = Assert.Single(snapshot.Checkpoint.Continuation.Children);
        Assert.Equal(MaterializationRebuildPlanSetProcessFactory.BuildLeavesNodeId, retainedChild.Node);
        Assert.Equal(PlanSetProjection.ProgressIdentity(0, authority), retainedChild.ProgressIdentity);

        var runtimeDetails = MaterializationRebuildPlanSetStatusProjector.CreateRuntimeDetails(
            planSet,
            artifacts,
            snapshot,
            Provenance("exact-build-work"));
        var root = Assert.IsType<ObservationValue>(Assert.Single(runtimeDetails.Extensions).Value.Value!.Value);
        var child = Assert.Single(root.GetProperty("children").EnumerateArray());

        Assert.Equal(ProcessChildDisposition.Active.ToString(), child.GetProperty("buildDisposition").GetRequiredString());
        Assert.Equal(new ExecutionProgressStatus(0, 2, "leaf-phase"), runtimeDetails.Progress);
        Assert.Equal(new ExecutionCapacityStatus(1, 1), runtimeDetails.Capacity);
        Assert.Equal(1, root.GetProperty("progress").GetProperty("buildStarted").Int64);
    }

    static async Task<ProcessDurableStoreSnapshot> InitializeAsync(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildPlanSetProcessArtifacts artifacts)
    {
        ProcessContinuationIdentity continuation = new(
            processInstanceId: new($"process-instance/plan-set-status/{planSet.Fingerprint.Value}"),
            processAttemptId: new("process-attempt/1"));
        var runtime = new ProcessDurableRuntime(
            store: new InMemoryProcessDurableStore(),
            host: RejectingHost.Instance,
            options: new(
                workerId: "worker/plan-set-status",
                workerLease: TimeSpan.FromMinutes(5)));

        var result = await runtime.InitializeAsync(
            OperationContext.Create(timeProvider: new FixedTimeProvider(StartedAtUtc)),
            artifacts.ParentPlan,
            Start(planSet, artifacts, continuation));

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        return Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot);
    }

    static ProcessStartReceipt Start(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        ProcessContinuationIdentity continuation)
    {
        var request = new ProcessStartRequest(
            schemaVersion: ProcessStartRequest.CurrentSchemaVersion,
            definition: artifacts.ParentPlan.DefinitionReference,
            context: new(
                commandId: new($"command/plan-set-status/{planSet.Fingerprint.Value}"),
                idempotencyKey: new($"idempotency/plan-set-status/{planSet.Fingerprint.Value}"),
                processInstanceId: continuation.ProcessInstanceId,
                authorization: new(
                    actor: "operator/plan-set-status",
                    authorityScope: Authority,
                    evidenceReference: "policy/plan-set-status/allow"),
                issuedAtUtc: StartedAtUtc,
                provenance: Provenance("process-start")),
            initialContinuation: continuation,
            input: PortableValue.Concrete(
                artifacts.ParentPlan.Definition.Input,
                ObservationValue.FromString(
                    MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(
                        MaterializationRebuildPlanSetReference.FromPlanSet(planSet)))));
        return new(request, acceptedAtUtc: StartedAtUtc);
    }

    static ProcessActivation Activation(
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        string id,
        ProcessActivationCause cause,
        ImmutableArray<ProcessActivationInput> inputs) => new(
        id: new(id),
        cause,
        observedAtUtc: StartedAtUtc,
        context: new(
            authorityScope: Authority,
            correlationId: new("correlation/materialization-rebuild-plan-set-status"),
            delivery: new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            provenance: artifacts.ParentProcessDocument.Metadata.Provenance),
        inputs);

    static ImmutableArray<ProcessActivationInput> PendingInputs(ProcessDurableCheckpoint checkpoint) =>
    [
        .. checkpoint.Inbox
            .Where(entry => entry.Receipt is null
                && entry.Input.Target.Continuation == checkpoint.ContinuationIdentity)
            .Select(static entry => entry.Input)
    ];

    static ExecutionProvenance Provenance(string source) => new(
        new ExecutionProducerProvenance("cohesive-tests", "1"),
        new ExecutionSourceProvenance($"tests/materialization-rebuild-plan-set-status/{source}"),
        DocumentOrigin.Generated);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessContinuationState NewContinuation(
        ExecutionDefinitionReference definition,
        ProcessContinuationIdentity continuation,
        long completedActivationCount,
        ImmutableArray<ProcessTokenState> tokens,
        ImmutableArray<ProcessForkState> forks,
        ImmutableArray<ProcessChildState> children,
        ImmutableArray<ProcessPartitionState> partitions,
        ImmutableArray<ProcessRecurrenceState> recurrences,
        ImmutableArray<ProcessWaitState> waits,
        ImmutableArray<ProcessBufferedInput> bufferedInputs,
        ImmutableArray<ProcessInputReceipt> inputReceipts,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests,
        ExecutionTerminalOutcome terminal);

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

    sealed class ExactBindingResolver(ImmutableArray<DurableRequestBinding> bindings)
        : IDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? binding)
        {
            binding = bindings.FirstOrDefault(candidate => candidate.Request == request.Contract);
            return binding is not null;
        }
    }

    sealed class ExactAdapterResolver(IDurableOperationAdapter adapter)
        : IDurableOperationAdapterResolver
    {
        public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? resolved)
        {
            resolved = adapter.Capabilities.Supports(request.Contract) ? adapter : null;
            return resolved is not null;
        }
    }

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
