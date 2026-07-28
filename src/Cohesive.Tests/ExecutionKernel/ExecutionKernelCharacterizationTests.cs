namespace Cohesive.Tests.ExecutionKernel;

/// <summary>
/// Characterizes the pre-kernel Transitions and Processes behavior against the
/// EK-01 through EK-09 scenarios. These tests intentionally describe current
/// compatibility boundaries; they do not define the future execution semantics.
/// </summary>
public sealed class ExecutionKernelCharacterizationTests
{
    static readonly IReadOnlyList<KernelScenarioClassification> ScenarioClassifications =
    [
        new("EK-01", KernelScenarioStatus.Partial, "Flat transitions and ordered process branches exist; structured transition branches, stable branch identities, typed outcomes, and path-sensitive summaries do not."),
        new("EK-02", KernelScenarioStatus.Partial, "Wait nodes checkpoint before yielding and accept early signals; AwaitMatch, durable admission/claim/consume state, and a typed timeout race do not exist."),
        new("EK-03", KernelScenarioStatus.Partial, "Typed effect handlers, retry, continuation freshness, and dead letters exist; stable request identity and vendor/manual/late-result arbitration do not."),
        new("EK-04", KernelScenarioStatus.Absent, "The process runtime has one cursor and a locality continuation stack, with no fork, token, or join model."),
        new("EK-05", KernelScenarioStatus.Partial, "Multi-entity transaction scopes and coarse place capabilities exist; capability evidence, guarantee matching, independent authority, and authored compensation do not."),
        new("EK-06", KernelScenarioStatus.Partial, "Pending and executed effects plus storage outbox support exist; the runtime has no durable operation attempt/acknowledgement ledger for the crash matrix."),
        new("EK-07", KernelScenarioStatus.Partial, "Signals can be buffered by key; duplicate identity, exclusive admission, winner claims, and stale/losing-signal policy do not exist."),
        new("EK-08", KernelScenarioStatus.Absent, "Process attempts, activation identity, index-generation affinity, pause/continue, restart, and fenced promotion are not modeled."),
        new("EK-09", KernelScenarioStatus.Absent, "Process semantics remain CLR delegate-backed and name-bound, with no canonical normalized process IR, schema version, fingerprint, or authoring-equivalence contract.")
    ];

    [Fact]
    public void EK01ThroughEK09_HaveExplicitCompatibilityClassifications()
    {
        Assert.Equal(
            ["EK-01", "EK-02", "EK-03", "EK-04", "EK-05", "EK-06", "EK-07", "EK-08", "EK-09"],
            ScenarioClassifications.Select(static scenario => scenario.Id));
        Assert.All(ScenarioClassifications, static scenario => Assert.NotEmpty(scenario.Evidence));
        Assert.DoesNotContain(ScenarioClassifications, static scenario => scenario.Status == KernelScenarioStatus.Pass);
    }

    [Fact]
    public void EK01_DirectTransitionActivation_IsValueDeterministicButDefinitionIsFlat()
    {
        var entity = new ReviewEntity();
        var state = entity.CreateState(
            entityId: "review-1",
            stateObject: new { Status = "Pending" },
            version: 7);
        var input = new ReviewEntity.ReviewInput(IsApproved: true);

        var first = entity.Review.Apply(state, input);
        var replay = entity.Review.Apply(state, input);

        Assert.Equal(8, first.NewVersion);
        Assert.Equal("Approved", entity.Status.Get(first.NewState));
        Assert.Equal(entity.Status.Get(first.NewState), entity.Status.Get(replay.NewState));
        Assert.Equal(first.NewVersion, replay.NewVersion);
        Assert.Equal(first.ReadFields, replay.ReadFields);
        Assert.Equal(first.WriteFields, replay.WriteFields);
        Assert.Equal(first.ChangedFields, replay.ChangedFields);

        var firstEffect = Assert.Single(first.Effects);
        var replayEffect = Assert.Single(replay.Effects);
        Assert.Equal(firstEffect.Name, replayEffect.Name);
        Assert.Equal(firstEffect.Payload, replayEffect.Payload);

        Assert.Empty(entity.Review.Definition.Preconditions);
        Assert.Single(entity.Review.Definition.Updates);
        Assert.Single(entity.Review.Definition.Effects);
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
            Review = Transition<ReviewEntity, ReviewInput>(
                nameof(Review),
                transition => transition
                    .Set(
                        entity => entity.Status,
                        (_, input) => input.IsApproved ? "Approved" : "Rejected")
                    .Emit(
                        name: "ReviewDecided",
                        payload: (_, input) => new { input.IsApproved }));
        }

        public Field<string> Status { get; }

        public Transition<ReviewEntity, ReviewInput> Review { get; }

        public sealed record ReviewInput(bool IsApproved);
    }
}
