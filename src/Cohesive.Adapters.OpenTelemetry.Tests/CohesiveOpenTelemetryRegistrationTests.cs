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
    public void AggregateTraceRegistrationCollectsEveryCoreScope()
    {
        var exporter = new CollectingActivityExporter();
        using TracerProvider provider = Sdk.CreateTracerProviderBuilder()
            .AddCohesiveInstrumentation()
            .AddProcessor(new SimpleActivityExportProcessor(exporter))
            .Build();

        EmitActivity(ExecutionTelemetry.ActivitySourceName);
        EmitActivity(RelationQueryTelemetry.ActivitySourceName);
        EmitActivity(ProcessDistributionTelemetry.ActivitySourceName);
        Assert.True(provider.ForceFlush(10_000));

        Assert.Equal(
            [
                ExecutionTelemetry.ActivitySourceName,
                ProcessDistributionTelemetry.ActivitySourceName,
                RelationQueryTelemetry.ActivitySourceName
            ],
            exporter.SourceNames.Order(StringComparer.Ordinal));
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
    public void AggregateMetricRegistrationCollectsEveryCoreScope()
    {
        var exporter = new CollectingMetricExporter();
        using MeterProvider provider = Sdk.CreateMeterProviderBuilder()
            .AddCohesiveInstrumentation()
            .AddReader(new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: 60_000))
            .Build();

        EmitMetric(ExecutionTelemetry.MeterName, "test.execution");
        EmitMetric(RelationQueryTelemetry.MeterName, "test.relations");
        EmitMetric(ProcessDistributionTelemetry.MeterName, "test.distribution");
        Assert.True(provider.ForceFlush(10_000));

        Assert.Equal(
            ["test.distribution", "test.execution", "test.relations"],
            exporter.InstrumentNames.Order(StringComparer.Ordinal));
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
