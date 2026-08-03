using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.ExecutionKernel;

/// <summary>
/// Retains a representative canonical Transition activation that originated in the execution-kernel compatibility
/// inventory. The complete EK-01 through EK-09 executable index is owned by
/// <see cref="ExecutionKernelConformanceMatrixTests"/>.
/// </summary>
public sealed class ExecutionKernelCharacterizationTests
{
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
