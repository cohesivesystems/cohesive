using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Cohesive.Adapters.Elastic;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using global::Elastic.Clients.Elasticsearch;
using global::Elastic.Transport;

namespace Cohesive.Tests.Elastic;

public sealed class ElasticMaterializationTelemetryTests
{
    const string TargetIdentity = "target/telemetry-bounded-cardinality";
    static readonly DateTimeOffset Epoch = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    static readonly MaterializationId MaterializationId = new("materialization/telemetry-bounded-cardinality");
    static readonly MaterializationGenerationId GenerationId = new("generation/telemetry-unbounded-identity");
    static readonly ExecutionDefinitionFingerprint DefinitionFingerprint = new(
        "sha256",
        "cohesive-materialization-definition/v1-c14n/v1",
        "0123456789abcdef");

    [Fact]
    public async Task Metrics_ExcludeGenerationIdentityFromEveryTargetMeasurement()
    {
        ConcurrentQueue<MetricMeasurement> measurements = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, currentListener) =>
        {
            if (instrument.Meter.Name == ElasticMaterializationTelemetry.InstrumentationName)
            {
                currentListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            Capture(instrument, measurement, tags, measurements));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            Capture(instrument, measurement, tags, measurements));
        listener.Start();

        var target = CreateTarget();
        var begun = await target.BeginGenerationAsync(
            OperationContext.Create(),
            new(
                MaterializationId,
                GenerationId,
                DefinitionFingerprint,
                new("1"),
                Epoch));
        Assert.Equal(MaterializationTargetOperationDisposition.Applied, begun.Disposition);
        var applied = await target.ApplyBatchAsync(
            OperationContext.Create(),
            new(
                new("batch/telemetry-bounded-cardinality"),
                GenerationId,
                new("1"),
                [
                    new MaterializationUpsert(
                        new("item/telemetry-bounded-cardinality"),
                        new("mutation/telemetry-bounded-cardinality"),
                        new("1"),
                        ObservationValue.FromString("value"))
                ]));
        Assert.Equal(MaterializationBatchDisposition.Applied, applied.Disposition);

        var targetMeasurements = measurements
            .Where(static measurement => measurement.Tags.Any(static tag =>
                tag.Key == ElasticMaterializationTelemetry.TargetIdTagName
                && Equals(tag.Value, TargetIdentity)))
            .ToArray();
        Assert.NotEmpty(targetMeasurements);
        Assert.Contains(
            targetMeasurements,
            static measurement => measurement.InstrumentName == ElasticMaterializationTelemetry.OperationCountName);
        Assert.Contains(
            targetMeasurements,
            static measurement => measurement.InstrumentName == ElasticMaterializationTelemetry.ItemOutcomeCountName);
        Assert.All(
            targetMeasurements,
            static measurement => Assert.DoesNotContain(
                measurement.Tags,
                static tag => tag.Key == ElasticMaterializationTelemetry.GenerationIdTagName));
    }

    static void Capture<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        ConcurrentQueue<MetricMeasurement> measurements)
        where T : struct
    {
        KeyValuePair<string, object?>[] retainedTags = new KeyValuePair<string, object?>[tags.Length];
        tags.CopyTo(retainedTags);
        measurements.Enqueue(new(instrument.Name, measurement, retainedTags));
    }

    static ElasticMaterializationTarget CreateTarget()
    {
        const string readAlias = "telemetry-loads-read";
        ElasticMaterializationTargetBinding binding = new(
            new("tests/elastic-materialization-telemetry/v1"),
            new("cluster-telemetry-uuid"),
            new(TargetIdentity),
            MaterializationId,
            readAlias,
            "telemetry-loads-generation-",
            ".cohesive-materialization-telemetry-control",
            new(
                "telemetry-loads-template",
                new("sha256", "elastic-index-template/v1", new string('a', 64)),
                "tests/elastic-template/v1"),
            new("tests/process-runtime/v1", "search-index/telemetry-loads"),
            new(
                new("tests/elastic-telemetry-search-binding/v1"),
                new RelationQuerySourceInstanceId("search/materialized-telemetry-loads"),
                new RelationQuerySourcePlacementBindingId("search/materialized-telemetry-loads/placement"),
                ElasticRelationQueryTargetProfile.Target,
                ElasticRelationQueryTargetProfile.ProfileId,
                readAlias,
                []));
        ElasticElasticsearchRuntimeBinding runtime = new(
            binding.Cluster,
            new ElasticsearchClient(new ElasticsearchClientSettings(new InMemoryRequestInvoker())),
            "tests/elastic-runtime/v1");
        var transport = new FakeElasticMaterializationTransport();
        return new(binding, ElasticMaterializationTargetPolicy.Default, runtime, transport);
    }

    sealed record MetricMeasurement(
        string InstrumentName,
        object Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
