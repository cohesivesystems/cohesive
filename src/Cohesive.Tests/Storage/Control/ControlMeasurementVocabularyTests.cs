using System.Text.Json.Nodes;
using Cohesive.Control;

namespace Cohesive.Tests.Storage.Control;

public sealed class ControlMeasurementVocabularyTests
{
    [Fact]
    public void MetricCatalog_ExhaustivelyAssignsOneCanonicalUnit()
    {
        (ControlMetricKind Metric, ControlUnit Unit)[] expected =
        [
            (ControlMetricKind.ProcessorUtilization, ControlUnit.BasisPoints),
            (ControlMetricKind.MemoryUtilization, ControlUnit.BasisPoints),
            (ControlMetricKind.Latency, ControlUnit.Milliseconds),
            (ControlMetricKind.ItemThroughput, ControlUnit.ItemsPerSecond),
            (ControlMetricKind.ByteThroughput, ControlUnit.BytesPerSecond),
            (ControlMetricKind.RejectionRatio, ControlUnit.BasisPoints),
            (ControlMetricKind.LagItems, ControlUnit.Count),
            (ControlMetricKind.LagDuration, ControlUnit.Milliseconds),
            (ControlMetricKind.BackpressureUtilization, ControlUnit.BasisPoints),
            (ControlMetricKind.RequestUnitConsumption, ControlUnit.MilliRequestUnits),
            (ControlMetricKind.QueueDepth, ControlUnit.Count),
            (ControlMetricKind.BatchItems, ControlUnit.Count),
            (ControlMetricKind.BatchBytes, ControlUnit.Bytes)
        ];

        Assert.Equal(Enum.GetValues<ControlMetricKind>().Length, expected.Length);
        Assert.Equal(
            Enum.GetValues<ControlMetricKind>().Order(),
            expected.Select(static entry => entry.Metric).Order());
        Assert.All(expected, entry =>
            Assert.Equal(entry.Unit, ControlUnitCatalog.ForMetric(entry.Metric)));
    }

    [Fact]
    public void AdapterMeasurementVocabulary_RoundTripsThroughTheCurrentCanonicalWire()
    {
        var definition = ControlRegulatorFixture.Definition(
            objectiveMetric: ControlMetricKind.RequestUnitConsumption,
            recoveryBoundary: 5_000,
            congestionBoundary: 10_000);
        var state = ControlRegulatorFixture.InitialState(definition);
        var observedAtUtc = state.UpdatedAtUtc.AddSeconds(2);
        ControlObservation observation = new(
            schemaVersion: ControlLoopDefinition.CurrentSchemaVersion,
            id: new("observation/adapter-vocabulary"),
            loopId: state.LoopId,
            definitionFingerprint: state.DefinitionFingerprint,
            target: state.Target,
            epoch: state.Epoch,
            expectedRevision: state.Revision,
            windowStartedAtUtc: observedAtUtc.AddSeconds(-1),
            windowEndedAtUtc: observedAtUtc.AddMilliseconds(-1),
            observedAtUtc: observedAtUtc,
            source: "tests/adapter-vocabulary-v1",
            measurements:
            [
                Available(
                    metric: ControlMetricKind.RequestUnitConsumption,
                    statistic: ControlStatisticKind.Sum,
                    value: 7_250,
                    unit: ControlUnit.MilliRequestUnits),
                Available(
                    metric: ControlMetricKind.QueueDepth,
                    statistic: ControlStatisticKind.Last,
                    value: 17,
                    unit: ControlUnit.Count),
                Available(
                    metric: ControlMetricKind.BatchItems,
                    statistic: ControlStatisticKind.Last,
                    value: 128,
                    unit: ControlUnit.Count),
                Available(
                    metric: ControlMetricKind.BatchBytes,
                    statistic: ControlStatisticKind.Last,
                    value: 65_536,
                    unit: ControlUnit.Bytes)
            ]);

        var canonical = ControlJsonSerializer.Serialize(observation);
        var restored = ControlJsonSerializer.DeserializeObservation(canonical);
        var root = JsonNode.Parse(canonical)!.AsObject();

        Assert.Equal(ControlLoopDefinition.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal(observation, restored);
        Assert.True(ControlJsonSerializer.GetCanonicalBytes(observation).AsSpan().SequenceEqual(
            ControlJsonSerializer.GetCanonicalBytes(restored)));
        Assert.Contains("\"metric\":\"RequestUnitConsumption\"", canonical, StringComparison.Ordinal);
        Assert.Contains("\"unit\":\"MilliRequestUnits\"", canonical, StringComparison.Ordinal);
        Assert.Equal(ControlLoopDefinition.CurrentSchemaVersion.Value, root["schemaVersion"]!.GetValue<string>());
    }

    [Fact]
    public void DefinitionFingerprint_DistinguishesMetricsThatShareTheSamePhysicalUnit()
    {
        var queueDepth = ControlRegulatorFixture.Definition(
            objectiveMetric: ControlMetricKind.QueueDepth,
            recoveryBoundary: 10,
            congestionBoundary: 20);
        var batchItems = ControlRegulatorFixture.Definition(
            objectiveMetric: ControlMetricKind.BatchItems,
            recoveryBoundary: 10,
            congestionBoundary: 20);

        Assert.Equal(ControlUnit.Count, ControlUnitCatalog.ForMetric(ControlMetricKind.QueueDepth));
        Assert.Equal(ControlUnit.Count, ControlUnitCatalog.ForMetric(ControlMetricKind.BatchItems));
        Assert.NotEqual(queueDepth.Fingerprint, batchItems.Fingerprint);
        Assert.False(ControlJsonSerializer.GetCanonicalBytes(queueDepth).AsSpan().SequenceEqual(
            ControlJsonSerializer.GetCanonicalBytes(batchItems)));
    }

    static ControlMeasurement Available(
        ControlMetricKind metric,
        ControlStatisticKind statistic,
        long value,
        ControlUnit unit) =>
        new(
            metric: metric,
            statistic: statistic,
            availability: ControlMeasurementAvailability.Available,
            value: new(
                value: value,
                unit: unit),
            sampleCount: 1);
}
