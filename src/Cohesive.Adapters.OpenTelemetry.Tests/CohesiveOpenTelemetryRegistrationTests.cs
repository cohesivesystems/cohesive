using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cohesive.Execution;
using Cohesive.Processes.Distribution;
using Cohesive.Relations.Observability;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Cohesive.Adapters.OpenTelemetry.Tests;

public sealed class CohesiveOpenTelemetryRegistrationTests
{
    [Fact]
    public void AggregateTraceRegistrationMatchesBlockCompositionAndCollectsEveryCoreScope()
    {
        string[] aggregate = CollectActivitySourceNames(
            builder => builder.AddCohesiveCoreInstrumentation());
        string[] composed = CollectActivitySourceNames(
            builder => builder
                .AddCohesiveExecutionInstrumentation()
                .AddCohesiveRelationsInstrumentation()
                .AddCohesiveProcessDistributionInstrumentation());

        Assert.Equal(
            [
                ExecutionTelemetry.ActivitySourceName,
                ProcessDistributionTelemetry.ActivitySourceName,
                RelationQueryTelemetry.ActivitySourceName
            ],
            aggregate);
        Assert.Equal(composed, aggregate);
    }

    [Fact]
    public void BlockTraceRegistrationDoesNotCollectUnselectedScopes()
    {
        var exporter = new CollectingActivityExporter();
        using TracerProvider provider = Sdk.CreateTracerProviderBuilder()
            .AddCohesiveExecutionInstrumentation()
            .AddProcessor(new SimpleActivityExportProcessor(exporter))
            .Build();

        EmitActivity(ExecutionTelemetry.ActivitySourceName);
        Assert.Null(TryStartActivity(RelationQueryTelemetry.ActivitySourceName));
        Assert.Null(TryStartActivity(ProcessDistributionTelemetry.ActivitySourceName));
        Assert.True(provider.ForceFlush(10_000));

        Assert.Equal([ExecutionTelemetry.ActivitySourceName], exporter.SourceNames);
    }

    [Fact]
    public void AggregateMetricRegistrationMatchesBlockCompositionAndCollectsEveryCoreScope()
    {
        string[] aggregate = CollectInstrumentNames(
            builder => builder.AddCohesiveCoreInstrumentation());
        string[] composed = CollectInstrumentNames(
            builder => builder
                .AddCohesiveExecutionInstrumentation()
                .AddCohesiveRelationsInstrumentation()
                .AddCohesiveProcessDistributionInstrumentation());

        Assert.Equal(
            ["test.core.0", "test.core.1", "test.core.2"],
            aggregate);
        Assert.Equal(composed, aggregate);
    }

    [Fact]
    public void BlockMetricRegistrationDoesNotCollectUnselectedScopes()
    {
        var exporter = new CollectingMetricExporter();
        using MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddCohesiveExecutionInstrumentation()
            .AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: 60_000))
            .Build();

        EmitMetric(ExecutionTelemetry.MeterName, "test.execution");
        EmitMetric(RelationQueryTelemetry.MeterName, "test.relations");
        EmitMetric(ProcessDistributionTelemetry.MeterName, "test.distribution");
        Assert.True(provider.ForceFlush(10_000));

        Assert.Equal(["test.execution"], exporter.InstrumentNames);
    }

    static void EmitActivity(string sourceName)
    {
        using ActivitySource source = new(sourceName);
        using Activity? activity = source.StartActivity("test.operation");
        Assert.NotNull(activity);
    }

    static Activity? TryStartActivity(string sourceName)
    {
        using ActivitySource source = new(sourceName);
        return source.StartActivity("test.operation");
    }

    static void EmitMetric(string meterName, string instrumentName)
    {
        using Meter meter = new(meterName);
        meter.CreateCounter<long>(instrumentName).Add(1);
    }

    static string[] CollectActivitySourceNames(
        Func<TracerProviderBuilder, TracerProviderBuilder> configure)
    {
        var exporter = new CollectingActivityExporter();
        using TracerProvider provider = configure(Sdk.CreateTracerProviderBuilder())
            .AddProcessor(new SimpleActivityExportProcessor(exporter))
            .Build();

        foreach (CohesiveInstrumentationScope scope in CohesiveInstrumentationScopes.Core)
            EmitActivity(scope.ActivitySourceName);

        Assert.True(provider.ForceFlush(10_000));

        return exporter.SourceNames.Order(StringComparer.Ordinal).ToArray();
    }

    static string[] CollectInstrumentNames(
        Func<MeterProviderBuilder, MeterProviderBuilder> configure)
    {
        var exporter = new CollectingMetricExporter();
        using MeterProvider provider = configure(Sdk.CreateMeterProviderBuilder())
            .AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: 60_000))
            .Build();

        for (var index = 0; index < CohesiveInstrumentationScopes.Core.Count; index++)
        {
            CohesiveInstrumentationScope scope = CohesiveInstrumentationScopes.Core[index];
            EmitMetric(scope.MeterName, $"test.core.{index}");
        }

        Assert.True(provider.ForceFlush(10_000));

        return exporter.InstrumentNames.Order(StringComparer.Ordinal).ToArray();
    }

    sealed class CollectingActivityExporter : BaseExporter<Activity>
    {
        public List<string> SourceNames { get; } = [];

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (Activity activity in batch)
                SourceNames.Add(activity.Source.Name);

            return ExportResult.Success;
        }
    }

    sealed class CollectingMetricExporter : BaseExporter<Metric>
    {
        public List<string> InstrumentNames { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch)
                InstrumentNames.Add(metric.Name);

            return ExportResult.Success;
        }
    }
}
