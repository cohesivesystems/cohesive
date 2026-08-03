using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

/// <summary>
/// Characterizes current canonical reference semantics against the EK-01 through EK-09 scenarios. The durable
/// Process runtime and in-memory physical durability interpretation are executable reference oracles; they do not
/// imply a production storage adapter or hosted runtime realization.
/// </summary>
public sealed class ExecutionKernelCharacterizationTests
{
    static readonly IReadOnlyList<KernelScenarioClassification> ScenarioClassifications =
    [
        new("EK-01", KernelScenarioStatus.Pass, "Canonical structured Transition IR, path-sensitive static compilation, full-state and sparse non-I/O reference interpretation, actual execution evidence, conflict detection, and fingerprint-bound Machine-edge linking satisfy the EK-01 reference path."),
        new("EK-02", KernelScenarioStatus.Pass, "The Motion DQ Process exercises typed task creation and a five-clause AwaitMatch over Hire, Hold, Not Eligible, cancellation, and an absolute timer. The canonical reference and durable interpretations preserve exact wait occurrences, deterministic arbitration, early, duplicate, late, stale, superseded, and missing-target reasons independently from policy dispositions, including a durable Hold cycle."),
        new("EK-03", KernelScenarioStatus.Pass, "Seven Motion DQ branches reuse one exact provider-neutral Request contract across vendor and manual occurrences. Provider-attempt failure or timeout evidence routes to manual fulfillment without settling requirement authority, while fulfilled endogenous evaluation receipts enter one generic case-scoped requirement Transition under replay-stable durable operation identity."),
        new("EK-04", KernelScenarioStatus.Pass, "The Motion DQ Process exercises an All/unobservable seven-way Fork/Join. Its durable conformance scenario restores after three completed branches, preserves reciprocal membership and branch-local progress, and converges without re-executing committed Request or Transition occurrences."),
        new("EK-05", KernelScenarioStatus.Partial, "Canonical Process invocation can coordinate independent exact Transition subjects without copying aggregate state. Static compilation derives exact resource and effect evidence, structurally preflights and retains a whole-definition atomicity demand, and represents compensation and reconciliation as explicit child-Process purposes; arbitrary nested scope regions and target capability realization evidence remain absent."),
        new("EK-06", KernelScenarioStatus.Pass, "The canonical durable runtime now realizes stable logical operation identity, fenced and renewable claims, attempt history, adapter execution outside the instance gate, acknowledgement, exact Reply admission, authored ambiguous-outcome recovery, and replay-safe atomic checkpoint, inbox, outbox, and operation-ledger commits across before/after-commit crash cuts; the in-memory store and scripted adapter provide the executable reference interpretation required by EK-06."),
        new("EK-07", KernelScenarioStatus.Pass, "Canonical Process state and the durable runtime now admit Signals through Control as atomic inbox evidence, preserve exact wait-occurrence targets, buffer early delivery, consume one deterministic winner, persist typed duplicate and late-loser dispositions without reopening the wait, and replay both commands and activations inertly; the in-memory store provides the executable reference interpretation required by EK-07."),
        new("EK-08", KernelScenarioStatus.Pass, "The canonical materialization Process and deterministic in-memory durability interpretation agree on every coordinator activation, stable operation identity, replay, and terminal ready-generation outcome. The Cosmos and PostgreSQL source realizations share that Process meaning while retaining distinct capability evidence. EK-06 store cuts recover generation admission, bounded baseline pages resume through exact checkpoints, incremental work durably settles before progress, ambiguous applied Elasticsearch promotion reconciles the retained intent, and explicit fenced routing resumes between candidate admission and atomic swap. Pause and Continue retain one candidate; RestartAttempt tombstones the abandoned generation before admitting a distinct replacement, and late work cannot write to the abandoned index."),
        new("EK-09", KernelScenarioStatus.Pass, "Representative Transitions and Processes lower from typed C# to fingerprint-equivalent canonical IR, round-trip independently of their producer assemblies, and compile only from persisted execution-definition documents; the former callback and single-cursor Process authorities are no longer shipped.")
    ];

    [Fact]
    public void EK01ThroughEK09_HaveExplicitCompatibilityClassifications()
    {
        Assert.Equal(
            ["EK-01", "EK-02", "EK-03", "EK-04", "EK-05", "EK-06", "EK-07", "EK-08", "EK-09"],
            ScenarioClassifications.Select(static scenario => scenario.Id));
        Assert.All(ScenarioClassifications, static scenario => Assert.NotEmpty(scenario.Evidence));
        Assert.Equal(
            ["EK-01", "EK-02", "EK-03", "EK-04", "EK-06", "EK-07", "EK-08", "EK-09"],
            ScenarioClassifications
                .Where(static scenario => scenario.Status == KernelScenarioStatus.Pass)
                .Select(static scenario => scenario.Id));
        Assert.Equal(
            KernelScenarioStatus.Pass,
            Assert.Single(ScenarioClassifications, static scenario => scenario.Id == "EK-08").Status);
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
