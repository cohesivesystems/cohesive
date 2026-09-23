using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cohesive.Observability;

namespace Cohesive.Tests.Observability;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OperationTelemetryEmitterTestCollection
{
    public const string Name = "Operation telemetry emitter";
}

[Collection(OperationTelemetryEmitterTestCollection.Name)]
public sealed class OperationTelemetryEmitterTests
{
    [Fact]
    public void CompleteOperation_ProjectsOwnerTagsAndFailureAcrossNativeSignals()
    {
        var instrumentationName = $"Cohesive.Tests.OperationTelemetry.{Guid.NewGuid():N}";
        Activity? completed = null;
        using ActivityListener activityListener = new()
        {
            ShouldListenTo = source => string.Equals(source.Name, instrumentationName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => completed = activity
        };
        ActivitySource.AddActivityListener(activityListener);

        double? elapsedSeconds = null;
        long failures = 0;
        string? durationStage = null;
        string? failureStage = null;
        using MeterListener meterListener = new();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, instrumentationName, StringComparison.Ordinal))
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            elapsedSeconds = value;
            durationStage = ReadTag(tags, "test.stage");
        });
        meterListener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            failures += value;
            failureStage = ReadTag(tags, "test.stage");
        });
        meterListener.Start();

        using ActivitySource source = new(instrumentationName);
        using Meter meter = new(instrumentationName);
        var duration = meter.CreateHistogram<double>("test.operation.duration", "s");
        var failureCounter = meter.CreateCounter<long>("test.operation.failures", "{failure}");
        OperationTelemetryEmitter emitter = new(source, duration, failureCounter);
        TagList tags = default;
        tags.Add("test.stage", "catalog");
        var expected = new InvalidOperationException("This message must not become an activity attribute.");

        var activity = emitter.StartActivity("test.operation");
        var started = emitter.StartTimer();
        emitter.CompleteOperation(activity, started, ActivityStatusCode.Error, tags, expected);

        Assert.NotNull(completed);
        Assert.Equal(ActivityStatusCode.Error, completed.Status);
        Assert.Equal("catalog", completed.GetTagItem("test.stage"));
        Assert.Equal(typeof(InvalidOperationException).FullName, completed.GetTagItem("error.type"));
        Assert.DoesNotContain(
            expected.Message,
            string.Join('|', completed.TagObjects.Select(static tag => tag.Value)),
            StringComparison.Ordinal);
        Assert.NotNull(elapsedSeconds);
        Assert.True(elapsedSeconds >= 0);
        Assert.Equal("catalog", durationStage);
        Assert.Equal(1, failures);
        Assert.Equal("catalog", failureStage);
    }

    [Fact]
    public void DisabledPath_DoesNotStartTelemetryOrAllocate()
    {
        var instrumentationName = $"Cohesive.Tests.OperationTelemetry.Disabled.{Guid.NewGuid():N}";
        using ActivitySource source = new(instrumentationName);
        using Meter meter = new(instrumentationName);
        var duration = meter.CreateHistogram<double>("test.operation.duration", "s");
        var failures = meter.CreateCounter<long>("test.operation.failures", "{failure}");
        OperationTelemetryEmitter emitter = new(source, duration, failures);
        const int Iterations = 10_000;
        var unexpectedTelemetry = false;

        // Warm the same workload that is measured, including tiered/runtime metadata paths.
        for (var index = 0; index < Iterations; index++)
        {
            unexpectedTelemetry |= emitter.IsEnabled;
            unexpectedTelemetry |= emitter.StartActivity("test.operation") is not null;
            unexpectedTelemetry |= emitter.StartTimer() != 0L;
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Iterations; index++)
        {
            unexpectedTelemetry |= emitter.IsEnabled;
            unexpectedTelemetry |= emitter.StartActivity("test.operation") is not null;
            unexpectedTelemetry |= emitter.StartTimer() != 0L;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.False(unexpectedTelemetry);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void StartActivity_UsesExplicitDistributedTraceParent()
    {
        var instrumentationName = $"Cohesive.Tests.OperationTelemetry.Parent.{Guid.NewGuid():N}";
        Activity? completed = null;
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => string.Equals(source.Name, instrumentationName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => completed = activity
        };
        ActivitySource.AddActivityListener(listener);
        using Activity parent = new("test.parent");
        parent.SetIdFormat(ActivityIdFormat.W3C);
        parent.Start();
        using ActivitySource source = new(instrumentationName);
        OperationTelemetryEmitter emitter = new(source, duration: null);
        TagList tags = default;

        var activity = emitter.StartActivity("test.operation", ActivityKind.Consumer, parent.Context);
        emitter.CompleteOperation(activity, started: 0, ActivityStatusCode.Ok, tags);

        Assert.NotNull(completed);
        Assert.Equal(parent.SpanId, completed.ParentSpanId);
        Assert.Equal(ActivityKind.Consumer, completed.Kind);
        Assert.Equal(ActivityStatusCode.Ok, completed.Status);
    }

    [Fact]
    public void CompleteOperation_ContainsEachSynchronousObserverFailure()
    {
        var instrumentationName = $"Cohesive.Tests.OperationTelemetry.ObserverFailure.{Guid.NewGuid():N}";
        var stopped = 0;
        using ActivityListener activityListener = new()
        {
            ShouldListenTo = source => string.Equals(source.Name, instrumentationName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ =>
            {
                Interlocked.Increment(ref stopped);
                throw new InvalidOperationException("The activity observer failed.");
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        var durations = 0;
        var failures = 0;
        using MeterListener meterListener = new();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, instrumentationName, StringComparison.Ordinal))
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<double>((_, _, _, _) =>
        {
            Interlocked.Increment(ref durations);
            throw new InvalidOperationException("The duration observer failed.");
        });
        meterListener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            Interlocked.Increment(ref failures);
            throw new InvalidOperationException("The failure observer failed.");
        });
        meterListener.Start();

        using ActivitySource source = new(instrumentationName);
        using Meter meter = new(instrumentationName);
        OperationTelemetryEmitter emitter = new(
            source,
            meter.CreateHistogram<double>("test.operation.duration", "s"),
            meter.CreateCounter<long>("test.operation.failures", "{failure}"));
        var activity = emitter.StartActivity("test.operation");
        var started = emitter.StartTimer();
        TagList tags = default;

        var exception = Record.Exception(() => emitter.CompleteOperation(
            activity,
            started,
            ActivityStatusCode.Error,
            tags,
            new InvalidOperationException("The operation failed.")));

        Assert.Null(exception);
        Assert.Equal(1, Volatile.Read(ref durations));
        Assert.Equal(1, Volatile.Read(ref failures));
        Assert.Equal(1, Volatile.Read(ref stopped));
    }

    [Fact]
    public void CompleteOperation_RejectsExceptionWithoutErrorStatus()
    {
        OperationTelemetryEmitter emitter = new(activitySource: null, duration: null);
        TagList tags = default;

        var exception = Assert.Throws<ArgumentException>(() => emitter.CompleteOperation(
            activity: null,
            started: 0,
            ActivityStatusCode.Ok,
            tags,
            new InvalidOperationException("Expected.")));

        Assert.Equal("exception", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsDurationHistogramWithoutSecondsUnit()
    {
        using Meter meter = new($"Cohesive.Tests.OperationTelemetry.Unit.{Guid.NewGuid():N}");
        var duration = meter.CreateHistogram<double>("test.operation.duration", "ms");

        var exception = Assert.Throws<ArgumentException>(() => new OperationTelemetryEmitter(
            activitySource: null,
            duration));

        Assert.Equal("duration", exception.ParamName);
    }

    static string? ReadTag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string name)
    {
        foreach (var tag in tags)
        {
            if (string.Equals(tag.Key, name, StringComparison.Ordinal))
                return tag.Value as string;
        }

        return null;
    }
}
