using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Storage.Processes;
using Cohesive.Tests.Storage.Control;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessControlAdmissionProjectionTests
{
    [Fact]
    public void ProjectFork_UsesTheDurableEffectiveControlPointAndRevision()
    {
        var definition = ControlRegulatorFixture.Definition(
            initial: 2,
            minimum: 1,
            maximum: 4);
        var state = ControlRegulatorFixture.InitialState(definition);

        var point = ProcessControlAdmissionProjection.ProjectFork(new("fork"), state);

        Assert.Equal(new ExecutionNodeId("fork"), point.Node);
        Assert.Equal(2, point.MaximumParallelism);
        Assert.Equal(1, point.Revision);
        Assert.False(string.IsNullOrWhiteSpace(point.Authority));
        Assert.Contains(state.LoopId.Value, point.EvidenceReference, StringComparison.Ordinal);
        Assert.Contains(state.Revision.Value, point.EvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectFork_RetainsTheExactAppliedAdaptiveSafePoint()
    {
        var definition = ControlRegulatorFixture.Definition(
            initial: 2,
            minimum: 1,
            maximum: 4,
            multiplicativeDecreaseBasisPoints: 5_000,
            minimumDwellMilliseconds: 0);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var observation = ControlRegulatorFixture.Observation(
            initial,
            "fork-congestion",
            value: 8_000);
        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            initial,
            observation,
            observation.ObservedAtUtc);
        var recommendation = Assert.IsType<ControlRecommendation>(decision.Recommendation);
        Assert.Equal(1, recommendation.ProposedOperatingPoint
            .Get(ControlActuatorKind.Concurrency)
            .Quantity.Value);
        var applicationPoint = ControlRegulatorFixture.ApplicationPoint(
            decision.State,
            "fork-admission-safe-point",
            fence: 1,
            decision.State.UpdatedAtUtc.AddMilliseconds(1),
            sourceReference: "process/fork/admission-safe-point",
            authority: "cohesive.processes/reference-v1");
        var applied = AimdControlReferenceRegulator.Apply(
            definition,
            decision.State,
            applicationPoint,
            applicationPoint.ObservedAtUtc);
        Assert.Equal(ControlActuationDisposition.Applied, applied.Disposition);

        var point = ProcessControlAdmissionProjection.ProjectFork(new("fork"), applied.State);

        Assert.Equal(1, point.MaximumParallelism);
        Assert.Equal(3, point.Revision);
        Assert.Equal(applicationPoint.Authority, point.Authority);
        Assert.Equal(applicationPoint.SourceReference, point.EvidenceReference);
    }
}
