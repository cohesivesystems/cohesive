using Cohesive.Control;
using Cohesive.Storage;

namespace Cohesive.Tests.Storage.Control;

public sealed class ControlContractTests
{
    [Fact]
    public void PublicControlSurface_LivesInControlNamespaceInsideStorageAssembly()
    {
        Type[] representativeContracts =
        [
            typeof(ControlLoopDefinition),
            typeof(ControlQuantity),
            typeof(ControlHardLimits),
            typeof(ControlObservation),
            typeof(ControlRecommendation),
            typeof(ControlActuation),
            typeof(AimdControlPolicy),
            typeof(AimdControlReferenceRegulator),
            typeof(ControlBoundedAdmission)
        ];

        var storageAssembly = typeof(IEntityRepository).Assembly;
        Assert.Equal("Cohesive.Storage", storageAssembly.GetName().Name);
        Assert.All(representativeContracts, contract =>
        {
            Assert.Equal("Cohesive.Control", contract.Namespace);
            Assert.Same(storageAssembly, contract.Assembly);
        });
    }

    [Theory]
    [InlineData(ControlActuatorKind.Concurrency, ControlUnit.Count)]
    [InlineData(ControlActuatorKind.BatchItems, ControlUnit.Count)]
    [InlineData(ControlActuatorKind.BatchBytes, ControlUnit.Bytes)]
    [InlineData(ControlActuatorKind.ItemRate, ControlUnit.ItemsPerSecond)]
    [InlineData(ControlActuatorKind.ByteRate, ControlUnit.BytesPerSecond)]
    [InlineData(ControlActuatorKind.BufferedItems, ControlUnit.Count)]
    [InlineData(ControlActuatorKind.BufferedBytes, ControlUnit.Bytes)]
    public void ActuatorUnits_AreCanonical(ControlActuatorKind actuator, ControlUnit expectedUnit)
    {
        Assert.Equal(expectedUnit, ControlUnitCatalog.ForActuator(actuator));
    }

    [Theory]
    [InlineData(ControlMetricKind.ProcessorUtilization, ControlUnit.BasisPoints)]
    [InlineData(ControlMetricKind.MemoryUtilization, ControlUnit.BasisPoints)]
    [InlineData(ControlMetricKind.Latency, ControlUnit.Milliseconds)]
    [InlineData(ControlMetricKind.ItemThroughput, ControlUnit.ItemsPerSecond)]
    [InlineData(ControlMetricKind.ByteThroughput, ControlUnit.BytesPerSecond)]
    [InlineData(ControlMetricKind.RejectionRatio, ControlUnit.BasisPoints)]
    [InlineData(ControlMetricKind.LagItems, ControlUnit.Count)]
    [InlineData(ControlMetricKind.LagDuration, ControlUnit.Milliseconds)]
    [InlineData(ControlMetricKind.BackpressureUtilization, ControlUnit.BasisPoints)]
    public void MeasurementUnits_AreCanonical(ControlMetricKind metric, ControlUnit expectedUnit)
    {
        Assert.Equal(expectedUnit, ControlUnitCatalog.ForMetric(metric));
    }

    [Fact]
    public void TypedValues_RejectUnitMismatchAtEverySemanticBoundary()
    {
        Assert.Throws<ArgumentException>(() => new ControlActuatorValue(
            ControlActuatorKind.BatchBytes,
            new ControlQuantity(10, ControlUnit.Count)));
        Assert.Throws<ArgumentException>(() => new ControlRange(
            ControlActuatorKind.ItemRate,
            new ControlQuantity(1, ControlUnit.ItemsPerSecond),
            new ControlQuantity(10, ControlUnit.Count)));
        Assert.Throws<ArgumentException>(() => new ControlMeasurement(
            ControlMetricKind.Latency,
            ControlStatisticKind.P95,
            ControlMeasurementAvailability.Available,
            new ControlQuantity(50, ControlUnit.Count),
            sampleCount: 1));
        Assert.Throws<ArgumentException>(() => new ControlObjective(
            ControlMetricKind.ProcessorUtilization,
            ControlStatisticKind.Mean,
            ControlObjectiveDirection.HigherIsCongested,
            new ControlQuantity(5_000, ControlUnit.BasisPoints),
            new ControlQuantity(8_000, ControlUnit.Count)));
        Assert.Throws<ArgumentException>(() => new ControlWorkloadBudget(
            ControlActuatorKind.ByteRate,
            new ControlQuantity(1_000, ControlUnit.BytesPerSecond),
            new ControlQuantity(100, ControlUnit.Bytes),
            ControlHardLimitOrigin.Deployment,
            "deployment/limits-v1"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_000)]
    public void RatioMeasurement_AcceptsInclusivePortableBounds(long value)
    {
        var measurement = new ControlMeasurement(
            ControlMetricKind.RejectionRatio,
            ControlStatisticKind.Mean,
            ControlMeasurementAvailability.Available,
            new ControlQuantity(value, ControlUnit.BasisPoints),
            sampleCount: 2);

        Assert.Equal(value, measurement.Value?.Value);
    }

    [Fact]
    public void RatioContracts_RejectValuesAboveOneWhole()
    {
        Assert.Throws<ArgumentException>(() => new ControlMeasurement(
            ControlMetricKind.RejectionRatio,
            ControlStatisticKind.Mean,
            ControlMeasurementAvailability.Available,
            new ControlQuantity(10_001, ControlUnit.BasisPoints),
            sampleCount: 1));
        Assert.Throws<ArgumentException>(() => new ControlObjective(
            ControlMetricKind.MemoryUtilization,
            ControlStatisticKind.Mean,
            ControlObjectiveDirection.HigherIsCongested,
            new ControlQuantity(5_000, ControlUnit.BasisPoints),
            new ControlQuantity(10_001, ControlUnit.BasisPoints)));
    }

    [Fact]
    public void UnavailableMeasurement_CarriesFailureEvidenceInsteadOfFabricatedData()
    {
        var measurement = new ControlMeasurement(
            ControlMetricKind.ProcessorUtilization,
            ControlStatisticKind.Mean,
            ControlMeasurementAvailability.Unavailable,
            failureCode: "  sampler.not-supported  ");

        Assert.Equal(ControlMeasurementAvailability.Unavailable, measurement.Availability);
        Assert.Null(measurement.Value);
        Assert.Equal(0, measurement.SampleCount);
        Assert.Equal("sampler.not-supported", measurement.FailureCode);

        Assert.Throws<ArgumentException>(() => new ControlMeasurement(
            ControlMetricKind.ProcessorUtilization,
            ControlStatisticKind.Mean,
            ControlMeasurementAvailability.Unavailable));
        Assert.Throws<ArgumentException>(() => new ControlMeasurement(
            ControlMetricKind.ProcessorUtilization,
            ControlStatisticKind.Mean,
            ControlMeasurementAvailability.Unavailable,
            new ControlQuantity(5_000, ControlUnit.BasisPoints),
            sampleCount: 1,
            failureCode: "sampler.failed"));
    }

    [Fact]
    public void DurableContracts_RejectDefaultRevisionAndApplicationFenceValues()
    {
        var definition = ControlRegulatorFixture.Definition();
        var state = ControlRegulatorFixture.InitialState(definition);
        var observedAtUtc = state.UpdatedAtUtc.AddSeconds(1);
        var measurement = new ControlMeasurement(
            ControlMetricKind.ProcessorUtilization,
            ControlStatisticKind.P95,
            ControlMeasurementAvailability.Available,
            new ControlQuantity(5_000, ControlUnit.BasisPoints),
            sampleCount: 3);

        Assert.Throws<ArgumentException>(() => new ControlObservation(
            ControlLoopDefinition.CurrentSchemaVersion,
            new("observation/default-revision"),
            state.LoopId,
            state.DefinitionFingerprint,
            state.Target,
            state.Epoch,
            default,
            observedAtUtc.AddSeconds(-1),
            observedAtUtc.AddMilliseconds(-1),
            observedAtUtc,
            "runtime/sampler-v1",
            [measurement]));
        Assert.Throws<ArgumentException>(() => new ControlApplicationPoint(
            ControlLoopDefinition.CurrentSchemaVersion,
            new("safe-point/default-revision"),
            state.LoopId,
            state.DefinitionFingerprint,
            state.Target,
            state.Epoch,
            default,
            new("1"),
            ControlApplicationPointKind.WorkAdmissionBoundary,
            observedAtUtc,
            "cohesive.processes/reference-v1",
            "process:safe-point"));
        Assert.Throws<ArgumentException>(() => new ControlApplicationPoint(
            ControlLoopDefinition.CurrentSchemaVersion,
            new("safe-point/default-fence"),
            state.LoopId,
            state.DefinitionFingerprint,
            state.Target,
            state.Epoch,
            state.Revision,
            default,
            ControlApplicationPointKind.WorkAdmissionBoundary,
            observedAtUtc,
            "cohesive.processes/reference-v1",
            "process:safe-point"));
        Assert.Throws<ArgumentException>(() => new ControlLoopState(
            ControlLoopDefinition.CurrentSchemaVersion,
            state.LoopId,
            state.Target,
            state.Epoch,
            default,
            definition.Fingerprint,
            state.OperatingPoint,
            healthyObservationCount: 0,
            state.CreatedAtUtc,
            state.UpdatedAtUtc));
    }

    [Fact]
    public void Recommendation_RequiresPairedPriorActuationEvidence()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var state = ControlRegulatorFixture.InitialState(definition);
        var observation = ControlRegulatorFixture.Observation(
            state,
            "paired-prior-actuation-evidence",
            value: 5_000);
        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            state,
            observation,
            observation.ObservedAtUtc);
        var recommendation = Assert.IsType<ControlRecommendation>(decision.Recommendation);

        Assert.Throws<ArgumentException>(() => CloneRecommendation(
            recommendation,
            new ControlActuationId("prior-actuation"),
            priorActuationRevision: null));
        Assert.Throws<ArgumentException>(() => CloneRecommendation(
            recommendation,
            priorActuationId: null,
            priorActuationRevision: new ControlRevision("3")));
    }

    static ControlRecommendation CloneRecommendation(
        ControlRecommendation recommendation,
        ControlActuationId? priorActuationId,
        ControlRevision? priorActuationRevision) =>
        new(
            recommendation.Id,
            recommendation.LoopId,
            recommendation.DefinitionFingerprint,
            recommendation.Target,
            recommendation.Epoch,
            recommendation.ExpectedRevision,
            recommendation.ObservationId,
            recommendation.Actuator,
            recommendation.Direction,
            recommendation.AuthorizingHealthyObservationCount,
            recommendation.PriorOperatingPoint,
            recommendation.ProposedOperatingPoint,
            recommendation.IssuedAtUtc,
            priorActuationId,
            priorActuationRevision);
}
