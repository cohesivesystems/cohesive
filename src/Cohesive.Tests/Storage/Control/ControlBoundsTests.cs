using Cohesive.Control;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Storage.Control;

public sealed class ControlBoundsTests
{
    [Fact]
    public void HardLimits_IntersectEveryAuthorityWithoutExceedingAnyLimit()
    {
        var limits = ControlTestFixture.Limits(
            ControlTestFixture.Limit(
                ControlActuatorKind.Concurrency,
                minimum: 1,
                maximum: 64,
                ControlHardLimitOrigin.Semantic,
                "plan/index-sync-v1"),
            ControlTestFixture.Limit(
                ControlActuatorKind.Concurrency,
                minimum: 2,
                maximum: 32,
                ControlHardLimitOrigin.Adapter,
                "elastic/capabilities-v8"),
            ControlTestFixture.Limit(
                ControlActuatorKind.Concurrency,
                minimum: 4,
                maximum: 48,
                ControlHardLimitOrigin.Deployment,
                "production/capacity-v3"));

        var effective = limits.GetEffectiveRange(ControlActuatorKind.Concurrency);

        Assert.Equal(4, effective.Minimum.Value);
        Assert.Equal(32, effective.Maximum.Value);
        Assert.All(limits.Constraints, constraint =>
        {
            Assert.True(effective.Minimum.Value >= constraint.Range.Minimum.Value);
            Assert.True(effective.Maximum.Value <= constraint.Range.Maximum.Value);
        });
    }

    [Fact]
    public void HardLimits_NormalizeDeterministicallyIndependentOfInputOrder()
    {
        ControlHardLimit[] constraints =
        [
            ControlTestFixture.Limit(
                ControlActuatorKind.BatchBytes,
                1_024,
                1_000_000,
                ControlHardLimitOrigin.Adapter,
                "elastic/v8"),
            ControlTestFixture.Limit(
                ControlActuatorKind.Concurrency,
                1,
                16,
                ControlHardLimitOrigin.Semantic,
                "plan/v1"),
            ControlTestFixture.Limit(
                ControlActuatorKind.BatchBytes,
                1,
                2_000_000,
                ControlHardLimitOrigin.Semantic,
                "plan/v1")
        ];

        var forward = ControlTestFixture.Limits(constraints);
        var reverse = ControlTestFixture.Limits([.. constraints.Reverse()]);

        Assert.Equal(forward, reverse);
        Assert.Equal(forward.GetHashCode(), reverse.GetHashCode());
        Assert.Equal(
            [ControlActuatorKind.Concurrency, ControlActuatorKind.BatchBytes, ControlActuatorKind.BatchBytes],
            forward.Constraints.Select(static constraint => constraint.Range.Actuator));
        Assert.Equal(
            [ControlHardLimitOrigin.Semantic, ControlHardLimitOrigin.Semantic, ControlHardLimitOrigin.Adapter],
            forward.Constraints.Select(static constraint => constraint.Origin));
    }

    [Fact]
    public void HardLimits_RejectEmptyIntersectionAndDuplicateAuthorityEvidence()
    {
        Assert.Throws<ArgumentException>(() => ControlTestFixture.Limits(
            ControlTestFixture.Limit(
                ControlActuatorKind.Concurrency,
                1,
                4,
                ControlHardLimitOrigin.Semantic,
                "plan/v1"),
            ControlTestFixture.Limit(
                ControlActuatorKind.Concurrency,
                5,
                10,
                ControlHardLimitOrigin.Adapter,
                "target/v1")));

        var duplicated = ControlTestFixture.Limit(
            ControlActuatorKind.Concurrency,
            1,
            4,
            ControlHardLimitOrigin.Semantic,
            "plan/v1");
        Assert.Throws<ArgumentException>(() => ControlTestFixture.Limits(duplicated, duplicated));
    }

    [Fact]
    public void WorkloadReservation_ExposesOnlyPositiveSurplusCapacity()
    {
        var budget = ControlTestFixture.Budget(
            ControlActuatorKind.Concurrency,
            capacity: 20,
            reserved: 7);

        Assert.Equal(new ControlQuantity(13, ControlUnit.Count), budget.Available);
        Assert.Throws<ArgumentException>(() => ControlTestFixture.Budget(
            ControlActuatorKind.Concurrency,
            capacity: 20,
            reserved: 20));
        Assert.Throws<ArgumentException>(() => ControlTestFixture.Budget(
            ControlActuatorKind.Concurrency,
            capacity: 20,
            reserved: 21));
    }

    [Fact]
    public void Definition_IntersectsWorkloadSurplusWithPhysicalHardLimit()
    {
        var limits = ControlTestFixture.Limits(ControlTestFixture.Limit(
            ControlActuatorKind.Concurrency,
            minimum: 1,
            maximum: 100,
            ControlHardLimitOrigin.Adapter,
            "target/v1"));
        var definition = ControlTestFixture.Definition(
            limits,
            ControlTestFixture.Point((ControlActuatorKind.Concurrency, 60)),
            [ControlTestFixture.Budget(ControlActuatorKind.Concurrency, capacity: 80, reserved: 20)]);

        var effective = definition.GetEffectiveRange(ControlActuatorKind.Concurrency);

        Assert.Equal(1, effective.Minimum.Value);
        Assert.Equal(60, effective.Maximum.Value);
    }

    [Fact]
    public void OperatingPoint_NormalizesShapeAndRejectsDuplicateActuators()
    {
        var point = ControlTestFixture.Point(
            (ControlActuatorKind.BatchBytes, 10_000),
            (ControlActuatorKind.Concurrency, 4),
            (ControlActuatorKind.BatchItems, 100));

        Assert.Equal(
            [ControlActuatorKind.Concurrency, ControlActuatorKind.BatchItems, ControlActuatorKind.BatchBytes],
            point.Values.Select(static value => value.Actuator));
        Assert.Throws<ArgumentException>(() => ControlTestFixture.Point(
            (ControlActuatorKind.Concurrency, 2),
            (ControlActuatorKind.Concurrency, 3)));
    }

    [Fact]
    public void Definition_RequiresOperatingPointToMatchExactlyTheBoundedShape()
    {
        var limits = TwoDimensionalLimits();

        Assert.Throws<ArgumentException>(() => ControlTestFixture.Definition(
            limits,
            ControlTestFixture.Point((ControlActuatorKind.Concurrency, 4))));

        var definition = ControlTestFixture.Definition(
            limits,
            ControlTestFixture.Point(
                (ControlActuatorKind.Concurrency, 4),
                (ControlActuatorKind.BatchItems, 50)));
        var candidate = ControlTestFixture.Point(
            (ControlActuatorKind.Concurrency, 4),
            (ControlActuatorKind.BatchBytes, 10_000));

        var validation = definition.ValidateOperatingPoint(candidate);

        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ControlDiagnosticCodes.OperatingPointShapeMismatch, diagnostic.Code);
    }

    [Fact]
    public void OperatingPointValidation_ReportsHardLimitEvidenceWithoutWeakeningIt()
    {
        var definition = ControlTestFixture.Definition(
            TwoDimensionalLimits(),
            ControlTestFixture.Point(
                (ControlActuatorKind.Concurrency, 4),
                (ControlActuatorKind.BatchItems, 50)));
        var candidate = ControlTestFixture.Point(
            (ControlActuatorKind.Concurrency, 17),
            (ControlActuatorKind.BatchItems, 50));

        var validation = definition.ValidateOperatingPoint(candidate);

        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ControlDiagnosticCodes.HardLimitExceeded, diagnostic.Code);
        var evidence = Assert.IsType<DocumentDiagnosticEvidence>(diagnostic.Evidence);
        Assert.Contains("adapter/concurrency-v1", evidence.SourceReferences);
        Assert.Equal("17 Count", evidence.Observed);
    }

    [Fact]
    public void OperatingPointValidation_ReportsReservedCapacitySeparatelyFromHardLimits()
    {
        var limits = ControlTestFixture.Limits(ControlTestFixture.Limit(
            ControlActuatorKind.Concurrency,
            1,
            20,
            ControlHardLimitOrigin.Adapter,
            "adapter/concurrency-v1"));
        var definition = ControlTestFixture.Definition(
            limits,
            ControlTestFixture.Point((ControlActuatorKind.Concurrency, 10)),
            [ControlTestFixture.Budget(ControlActuatorKind.Concurrency, capacity: 20, reserved: 5)]);

        var validation = definition.ValidateOperatingPoint(
            ControlTestFixture.Point((ControlActuatorKind.Concurrency, 16)));

        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ControlDiagnosticCodes.WorkloadBudgetExceeded, diagnostic.Code);
        var evidence = Assert.IsType<DocumentDiagnosticEvidence>(diagnostic.Evidence);
        Assert.Contains("deployment/capacity-v1", evidence.SourceReferences);
    }

    [Fact]
    public void Definition_RejectsAnInitiallyOutOfBoundsOrReservedOperatingPoint()
    {
        var hardLimited = ControlTestFixture.Limits(ControlTestFixture.Limit(
            ControlActuatorKind.Concurrency,
            1,
            8,
            ControlHardLimitOrigin.Adapter,
            "adapter/v1"));
        Assert.Throws<ArgumentException>(() => ControlTestFixture.Definition(
            hardLimited,
            ControlTestFixture.Point((ControlActuatorKind.Concurrency, 9))));

        var capacityLimited = ControlTestFixture.Limits(ControlTestFixture.Limit(
            ControlActuatorKind.Concurrency,
            1,
            20,
            ControlHardLimitOrigin.Adapter,
            "adapter/v1"));
        Assert.Throws<ArgumentException>(() => ControlTestFixture.Definition(
            capacityLimited,
            ControlTestFixture.Point((ControlActuatorKind.Concurrency, 16)),
            [ControlTestFixture.Budget(ControlActuatorKind.Concurrency, capacity: 20, reserved: 5)]));
    }

    static ControlHardLimits TwoDimensionalLimits() =>
        ControlTestFixture.Limits(
            ControlTestFixture.Limit(
                ControlActuatorKind.Concurrency,
                1,
                16,
                ControlHardLimitOrigin.Adapter,
                "adapter/concurrency-v1"),
            ControlTestFixture.Limit(
                ControlActuatorKind.BatchItems,
                1,
                100,
                ControlHardLimitOrigin.Semantic,
                "plan/batch-v1"));
}
