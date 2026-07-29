using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

/// <summary>
/// Characterizes current canonical reference semantics and legacy Transitions and Processes behavior against
/// the EK-01 through EK-09 scenarios. These tests intentionally describe current compatibility boundaries; they
/// do not promote a reference protocol to a physically durable runtime realization.
/// </summary>
public sealed class ExecutionKernelCharacterizationTests
{
    static readonly IReadOnlyList<KernelScenarioClassification> ScenarioClassifications =
    [
        new("EK-01", KernelScenarioStatus.Pass, "Canonical structured Transition IR, path-sensitive static compilation, full-state and sparse non-I/O reference interpretation, actual execution evidence, conflict detection, and fingerprint-bound Machine-edge linking satisfy the EK-01 reference path."),
        new("EK-02", KernelScenarioStatus.Partial, "The canonical Process reference interpreter retains complete AwaitMatch registrations, computed timers, early inputs, winner and loser evidence, and deterministic priority/clause/input arbitration in immutable continuation state; physical durable inbox and atomic checkpoint persistence remain ARI-166 work."),
        new("EK-03", KernelScenarioStatus.Partial, "The canonical Process reference interpreter emits one stable typed Request, retains its response obligation, admits an exact Reply outcome once, and prevents a linear inbound Reply obligation from being consumed across a Fork or resurrected by Join state; DurableOperation retry/reconciliation exists separately, while physical operation-ledger and checkpoint coupling remain ARI-166 work."),
        new("EK-04", KernelScenarioStatus.Partial, "The canonical Process reference interpreter now executes a stable token set with Fork membership, independent branch bindings, deterministic scheduling, reciprocal Join thresholds and tie-breaks, and replay-stable trace evidence; physical multi-token checkpoint persistence remains ARI-166 work."),
        new("EK-05", KernelScenarioStatus.Partial, "Canonical Process invocation can coordinate independent exact Transition subjects without copying aggregate state; canonical Process IR still has no scope, guarantee-demand, capability-evidence, compensation, or reconciliation construct."),
        new("EK-06", KernelScenarioStatus.Partial, "The canonical reference protocol models stable logical identity, scoped deduplication, fenced claims, attempt history, failure phases, acknowledgement, reconciliation, and replay-safe result admission across the crash cuts; no Storage-backed atomic operation ledger, outbox, or checkpoint integration exists yet."),
        new("EK-07", KernelScenarioStatus.Partial, "Canonical Process reference state now buffers early Signals, deduplicates logical emissions, selects exactly one AwaitMatch winner by stable policy, and dispositions late losers without reopening the wait; a Storage-backed atomic inbox and a wait-occurrence target fence remain absent."),
        new("EK-08", KernelScenarioStatus.Partial, "Canonical Process control now models stable attempt and activation identity, safe-point-aware pause/continue and restart, and write-once attempt affinities that can carry an index candidate generation; Storage-backed control persistence, generation allocation and cleanup, retry/recovery integration, and fenced idempotent promotion do not exist."),
        new("EK-09", KernelScenarioStatus.Partial, "Representative entity Transitions lower from typed C# to fingerprint-equivalent canonical IR, and Processes now have direct callback-free canonical IR; Process C# lowering and runtime migration remain incomplete.")
    ];

    [Fact]
    public void EK01ThroughEK09_HaveExplicitCompatibilityClassifications()
    {
        Assert.Equal(
            ["EK-01", "EK-02", "EK-03", "EK-04", "EK-05", "EK-06", "EK-07", "EK-08", "EK-09"],
            ScenarioClassifications.Select(static scenario => scenario.Id));
        Assert.All(ScenarioClassifications, static scenario => Assert.NotEmpty(scenario.Evidence));
        var passing = Assert.Single(
            ScenarioClassifications,
            static scenario => scenario.Status == KernelScenarioStatus.Pass);
        Assert.Equal("EK-01", passing.Id);
    }

    [Fact]
    public void EK09_RepresentativeEntityTransition_UsesOnlyCanonicalDocumentActivation()
    {
        var entity = new ReviewEntity();
        var state = entity.CreateState(
            entityId: "review-1",
            stateObject: new { Status = "Pending" },
            version: 7);
        var compilation = entity.Review.Compile();

        Assert.True(entity.Review.IsValid);
        Assert.True(compilation.IsSuccessful);
        var plan = Assert.IsType<CompiledTransitionPlan>(compilation.Plan);
        var input = PortableValue.Concrete(
            plan.Definition.Input,
            ObservationValue.FromObject(new ReviewEntity.ReviewInput(IsApproved: true)));
        var observation = PortableValue.Concrete(
            plan.Definition.Observation,
            ObservationValue.FromObject(state.Fields));

        var first = TransitionReferenceInterpreter.DecideFullState(
            plan,
            new("characterization/review/approved"),
            input,
            observation);
        var replay = TransitionReferenceInterpreter.DecideFullState(
            plan,
            new("characterization/review/approved"),
            input,
            observation);

        Assert.Equal(first.Kind, replay.Kind);
        Assert.Equal(first.Outcome, replay.Outcome);
        Assert.Equal(
            first.Patch.Select(static patch => (patch.Path, patch.Before, patch.After)),
            replay.Patch.Select(static patch => (patch.Path, patch.Before, patch.After)));
        Assert.Equal(
            first.Emissions.Select(static emission => (emission.Node, emission.Contract, emission.Payload)),
            replay.Emissions.Select(static emission => (emission.Node, emission.Contract, emission.Payload)));
        Assert.Equal(TransitionDecisionKind.Applied, first.Kind);
        Assert.Equal("Approved", first.Outcome?.Value?.String);
        var patch = Assert.Single(first.Patch);
        Assert.Equal(nameof(ReviewEntity.Status), patch.Path.ToString());
        Assert.Equal("Approved", patch.After.Value?.String);
        var emission = Assert.Single(first.Emissions);
        Assert.Equal(ReviewEntity.Semantics.ReviewDecidedContract, emission.Contract);
        var payload = emission.Payload.Value.GetValueOrDefault();
        Assert.True(payload.TryGetProperty(nameof(ReviewEntity.ReviewDecided.IsApproved), out var approved));
        Assert.True(approved.Bool);
    }

    [Fact]
    public void EK01AndEK09_ReplanningSameCheckpoint_ReevaluatesHostBranchDelegates()
    {
        var chooseFirstBranch = true;
        var process = new ProcessDefinition(
            name: "DelegateBackedBranch",
            entryNode: "choose",
            nodes:
            [
                new BranchingNode(
                    name: "choose",
                    branches:
                    [
                        new(ctx => chooseFirstBranch, "first"),
                        new(ctx => true, "second")
                    ],
                    elseNode: "fallback"),
                new EndNode("first"),
                new EndNode("second"),
                new EndNode("fallback")
            ]);
        var context = CreateOperationContext();
        var engine = CreateEngine();
        var checkpoint = engine.CreateCheckpoint(
            context,
            process,
            runOptions: new() { ProcessId = "branch-replay" });

        var firstPlan = engine.PlanNextStep(context, process, checkpoint);
        chooseFirstBranch = false;
        var replayPlan = engine.PlanNextStep(context, process, checkpoint);

        Assert.Equal(ProcessExecutionPlanKind.Advance, firstPlan.Kind);
        Assert.Equal("first", firstPlan.Checkpoint.CurrentNode);
        Assert.Equal("second", replayPlan.Checkpoint.CurrentNode);
    }

    [Fact]
    public void EK02_WaitIsCheckpointedBeforeYield_ButOneCheckpointCanBeResumedTwice()
    {
        var process = new ProcessDefinition(
            name: "HumanReview",
            entryNode: "wait",
            nodes:
            [
                new WaitNode(
                    name: "wait",
                    waitType: ProcessWaitType.ExternalEvent,
                    keyExpression: _ => "review:review-1",
                    captureVar: "decision",
                    nextNode: "end"),
                new EndNode("end")
            ]);
        var context = CreateOperationContext();
        var engine = CreateEngine();
        var initial = engine.CreateCheckpoint(
            context,
            process,
            runOptions: new() { ProcessId = "human-review" });

        var waitPlan = engine.PlanNextStep(context, process, initial);

        Assert.Equal(ProcessExecutionPlanKind.Wait, waitPlan.Kind);
        Assert.Equal(ProcessExecutionStatus.Waiting, waitPlan.Checkpoint.Status);
        Assert.Equal("wait", waitPlan.Checkpoint.CurrentNode);
        Assert.Equal("review:review-1", waitPlan.Wait?.Key);

        var approved = engine.ResumeWait(context, process, waitPlan.Checkpoint, "approved");
        var rejected = engine.ResumeWait(context, process, waitPlan.Checkpoint, "rejected");

        Assert.Equal("approved", approved.Variables["decision"]);
        Assert.Equal("rejected", rejected.Variables["decision"]);
        Assert.Equal("end", approved.CurrentNode);
        Assert.Equal("end", rejected.CurrentNode);
    }

    [Fact]
    public async Task EK07_EarlyDuplicateSignals_AreBufferedAsDistinctFifoInputs()
    {
        var context = CreateOperationContext();
        var waits = new InMemoryProcessWaitAdapter();

        waits.PublishExternalEvent(key: "approval:review-1", payload: "approved");
        waits.PublishExternalEvent(key: "approval:review-1", payload: "approved");

        var first = await waits.WaitAsync(
            context,
            ProcessWaitType.ExternalEvent,
            key: "approval:review-1",
            timeout: null);
        var duplicate = await waits.WaitAsync(
            context,
            ProcessWaitType.ExternalEvent,
            key: "approval:review-1",
            timeout: null);

        Assert.Equal("approved", first);
        Assert.Equal("approved", duplicate);
    }

    [Fact]
    public void EK04_ProcessDefinitionAcceptsUnboundedCycle_AndCheckpointHasOneCursor()
    {
        var process = new ProcessDefinition(
            name: "UnboundedCycle",
            entryNode: "loop",
            nodes:
            [
                new ComputeValueNode(
                    name: "loop",
                    valueExpression: _ => null,
                    nextNode: "loop")
            ]);
        var checkpoint = CreateEngine().CreateCheckpoint(
            CreateOperationContext(),
            process,
            runOptions: new() { ProcessId = "unbounded-cycle" });

        Assert.Equal("loop", checkpoint.CurrentNode);
        Assert.Empty(checkpoint.ContinuationFrames);
    }

    [Fact]
    public void EK09_CheckpointAcceptsChangedDefinitionWithSameName()
    {
        var original = BranchTo(targetNode: "original");
        var replacement = BranchTo(targetNode: "replacement");
        var context = CreateOperationContext();
        var engine = CreateEngine();
        var checkpoint = engine.CreateCheckpoint(
            context,
            original,
            runOptions: new() { ProcessId = "same-name-definition" });

        var plan = engine.PlanNextStep(context, replacement, checkpoint);

        Assert.Equal(ProcessExecutionPlanKind.Advance, plan.Kind);
        Assert.Equal("replacement", plan.Checkpoint.CurrentNode);
    }

    static ProcessDefinition BranchTo(string targetNode) => new(
        name: "SameName",
        entryNode: "choose",
        nodes:
        [
            new BranchingNode(
                name: "choose",
                branches: [new(_ => true, targetNode)]),
            new EndNode(targetNode)
        ]);

    static OperationContext CreateOperationContext() => OperationContext.Create(
        timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero)));

    static ProcessEngine CreateEngine()
    {
        var storage = new InMemoryProcessStorageAdapter();
        return new(new(
            transitionHost: new DeclarativeTransitionHost(),
            entityRepository: storage,
            checkpointRepository: storage,
            transactionGateway: storage,
            waitAdapter: new InMemoryProcessWaitAdapter(),
            deadLetterSink: new InMemoryProcessDeadLetterSink()));
    }

    enum KernelScenarioStatus
    {
        Pass,
        Partial,
        Absent
    }

    sealed record KernelScenarioClassification(
        string Id,
        KernelScenarioStatus Status,
        string Evidence);

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    sealed class ReviewEntity : Entity
    {
        public ReviewEntity()
        {
            Status = MutableField<string>(nameof(Status));
            Review = Transition<ReviewEntity, ReviewInput, string>(
                Semantics.Metadata,
                transition => transition
                    .Set(
                        Semantics.StatusUpdate,
                        entity => entity.Status,
                        (_, input) => input.IsApproved ? "Approved" : "Rejected")
                    .Emit(
                        Semantics.ReviewDecidedEmission,
                        Semantics.ReviewDecidedContract,
                        (_, input) => new ReviewDecided(input.IsApproved))
                    .Return(
                        Semantics.Outcome,
                        TransitionOutcomeDisposition.Applied,
                        (_, input) => input.IsApproved ? "Approved" : "Rejected"));
        }

        public Field<string> Status { get; }

        public Cohesive.Transitions.Authoring.Transition<ReviewEntity, ReviewInput, string> Review { get; }

        public sealed record ReviewInput(bool IsApproved);

        public sealed record ReviewDecided(bool IsApproved);

        public static class Semantics
        {
            public static readonly TransitionAuthoringMetadata Metadata = new(
                new("characterization/transition/review"),
                new("revision/1"),
                new("review/body"),
                new(
                    new(TransitionAuthoring.Producer),
                    new("tests/execution-kernel/review-entity"),
                    DocumentOrigin.Generated));

            public static readonly ExecutionNodeId StatusUpdate = new("review/update/status");
            public static readonly ExecutionNodeId ReviewDecidedEmission = new("review/emission/decided");
            public static readonly ExecutionNodeId Outcome = new("review/outcome/decided");

            public static readonly ExecutionDefinitionReference ReviewDecidedContract = new(
                new("characterization/interaction/review-decided"),
                new("revision/1"),
                new(
                    ExecutionDefinitionFingerprinter.Algorithm,
                    ExecutionDefinitionFingerprinter.Canonicalization,
                    new string('c', 64)));
        }
    }
}
