using Cohesive.Infra.Local;
using Cohesive.Model;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureLocalTargetDecisionTests
{
    [Fact]
    public void Decision_normalizes_evidence_under_a_target_neutral_concern()
    {
        var decision = new InfrastructureLocalTargetDecision(
            target: "target/test/v1",
            concern: "local/health/timing",
            kind: CapabilityRealizationKind.Constrained,
            rationale: "The target owns scheduling within an explicit boundary.",
            boundaries: ["boundary/z", "boundary/a"],
            sourceReferences: ["source/z", "source/a"]);

        Assert.Equal("target/test/v1", decision.Target);
        Assert.Equal("local/health/timing", decision.Concern);
        Assert.Equal<string>(["boundary/a", "boundary/z"], decision.Boundaries);
        Assert.Equal<string>(["source/a", "source/z"], decision.SourceReferences);
    }

    [Fact]
    public void Decision_rejects_missing_attribution_and_malformed_values()
    {
        Assert.Throws<ArgumentException>(() => new InfrastructureLocalTargetDecision(
            target: "target/test/v1",
            concern: "local/health/timing",
            kind: CapabilityRealizationKind.Native,
            rationale: "Native.",
            boundaries: [],
            sourceReferences: []));
        Assert.Throws<ArgumentException>(() => new InfrastructureLocalTargetDecision(
            target: " ",
            concern: "local/health/timing",
            kind: CapabilityRealizationKind.Native,
            rationale: "Native.",
            boundaries: [],
            sourceReferences: ["source/test"]));
        Assert.Throws<ArgumentException>(() => new InfrastructureLocalTargetDecision(
            target: "target/test/v1",
            concern: "local/health/timing",
            kind: CapabilityRealizationKind.Native,
            rationale: "Invalid native boundary.",
            boundaries: ["boundary/test"],
            sourceReferences: ["source/test"]));
        Assert.Throws<ArgumentException>(() => new InfrastructureLocalTargetDecision(
            target: "target/test/v1",
            concern: "local/health/timing",
            kind: CapabilityRealizationKind.Constrained,
            rationale: "Missing constrained boundary.",
            boundaries: [],
            sourceReferences: ["source/test"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InfrastructureLocalTargetDecision(
            target: "target/test/v1",
            concern: "local/health/timing",
            kind: (CapabilityRealizationKind)int.MaxValue,
            rationale: "Invalid.",
            boundaries: [],
            sourceReferences: ["source/test"]));
    }
}
