using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExecutionTelemetryTestCollection
{
    public const string Name = "Execution telemetry";
}

[Collection(ExecutionTelemetryTestCollection.Name)]
public sealed class ExecutionTelemetryTests
{
    const string PrivatePayload = "private-execution-payload-ari-210";

    [Fact]
    public void Activities_CorrelateExplainAndTraceWithStableHierarchyWithoutPayloads()
    {
        var (explain, trace) = ExplainAndTrace();
        using ActivityCollector collector = new();

        var parent = ExecutionTelemetry.StartActivity(ExecutionTelemetryActivityKind.Activation);
        ExecutionTelemetry.CorrelateActivity(parent, explain, trace);
        var child = ExecutionTelemetry.StartActivity(ExecutionTelemetryActivityKind.Wait);
        ExecutionTelemetry.CompleteActivity(child, ExecutionTelemetryOutcome.Succeeded);
        ExecutionTelemetry.CompleteActivity(parent, ExecutionTelemetryOutcome.Succeeded);

        var activation = Assert.Single(
            collector.Snapshots,
            static snapshot => snapshot.Name == "cohesive.execution.activation");
        var wait = Assert.Single(
            collector.Snapshots,
            static snapshot => snapshot.Name == "cohesive.execution.wait");
        Assert.Equal(activation.SpanId, wait.ParentSpanId);
        Assert.Equal(
            explain.Fingerprint.Value,
            activation.Tags[ExecutionTelemetry.ExplainFingerprintTagName]);
        Assert.Equal(
            ExecutionTraceFingerprinter.ComputeSemantic(trace).Value,
            activation.Tags[ExecutionTelemetry.TraceFingerprintTagName]);
        Assert.Equal("succeeded", activation.Tags[ExecutionTelemetry.OutcomeTagName]);
        Assert.DoesNotContain(
            PrivatePayload,
            string.Join('|', collector.Snapshots.SelectMany(static snapshot => snapshot.Tags.Values)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Metrics_UseOnlyClosedLowCardinalityDimensions()
    {
        using MeasurementCollector collector = new();
        var fixture = ProcessDurabilityTestFixture.Create();

        StorageExecutionTelemetry.RecordCheckpoint(fixture.Checkpoint);
        ExecutionTelemetry.RecordControl(
            ExecutionExplainEvidenceAuthority.Measured,
            ExecutionTelemetryOutcome.Observed);
        ExecutionTelemetry.RecordControl(
            ExecutionExplainEvidenceAuthority.Recommended,
            ExecutionTelemetryOutcome.Pending);
        ExecutionTelemetry.RecordControl(
            ExecutionExplainEvidenceAuthority.Applied,
            ExecutionTelemetryOutcome.Succeeded);
        ExecutionTelemetry.RecordMaterialization(
            backlogCount: 7,
            lagMilliseconds: 1_500,
            shardCount: 3,
            generationCount: 2,
            health: ExecutionHealthStatus.Degraded);

        Assert.Contains(
            collector.Snapshots,
            static snapshot => snapshot.Instrument == ExecutionTelemetry.CheckpointsInstrumentName);
        Assert.Contains(
            collector.Snapshots,
            static snapshot => snapshot.Instrument == ExecutionTelemetry.LagInstrumentName
                && snapshot.Unit == "s"
                && snapshot.Value == 1.5d);
        Assert.Equal(
            ["applied", "measured", "recommended"],
            collector.Snapshots
                .Where(static snapshot => snapshot.Instrument == ExecutionTelemetry.ControlEventsInstrumentName)
                .Select(snapshot => snapshot.Tags[ExecutionTelemetry.AuthorityTagName])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

        Assert.All(collector.Snapshots, static snapshot =>
        {
            string[] allowedTags = snapshot.Instrument switch
            {
                ExecutionTelemetry.StatusObservationsInstrumentName =>
                [
                    ExecutionTelemetry.HealthTagName,
                    ExecutionTelemetry.ReadinessTagName,
                    ExecutionTelemetry.ControlModeTagName,
                    ExecutionTelemetry.OutcomeTagName
                ],
                ExecutionTelemetry.ActivationsInstrumentName => [ExecutionTelemetry.OutcomeTagName],
                ExecutionTelemetry.WaitsInstrumentName => [ExecutionTelemetry.HealthTagName],
                ExecutionTelemetry.SignalsInstrumentName => [ExecutionTelemetry.KindTagName],
                ExecutionTelemetry.RetriesInstrumentName => [ExecutionTelemetry.KindTagName],
                ExecutionTelemetry.CheckpointsInstrumentName => [ExecutionTelemetry.OutcomeTagName],
                ExecutionTelemetry.BacklogInstrumentName =>
                    [ExecutionTelemetry.KindTagName, ExecutionTelemetry.HealthTagName],
                ExecutionTelemetry.LagInstrumentName => [ExecutionTelemetry.HealthTagName],
                ExecutionTelemetry.ControlEventsInstrumentName =>
                    [ExecutionTelemetry.AuthorityTagName, ExecutionTelemetry.OutcomeTagName],
                ExecutionTelemetry.ShardsInstrumentName => [ExecutionTelemetry.HealthTagName],
                ExecutionTelemetry.GenerationsInstrumentName => [ExecutionTelemetry.HealthTagName],
                _ => throw new InvalidOperationException($"Unexpected execution metric '{snapshot.Instrument}'.")
            };
            Assert.All(snapshot.Tags.Keys, key => Assert.Contains(key, allowedTags));
            Assert.DoesNotContain(snapshot.Tags.Keys, static key =>
                key.Contains("fingerprint", StringComparison.OrdinalIgnoreCase)
                || key.Contains("instance", StringComparison.OrdinalIgnoreCase)
                || key.Contains("payload", StringComparison.OrdinalIgnoreCase)
                || key.Contains("physical", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void DisabledPath_DoesNotStartActivitiesOrAllocate()
    {
        const int Iterations = 10_000;
        var unexpectedTelemetry = false;
        var checkpoint = ProcessDurabilityTestFixture.Create().Checkpoint;
        for (var index = 0; index < 100; index++)
        {
            unexpectedTelemetry |= ExecutionTelemetry.IsEnabled;
            unexpectedTelemetry |= ExecutionTelemetry.StartActivity(
                ExecutionTelemetryActivityKind.Activation) is not null;
            StorageExecutionTelemetry.RecordCheckpoint(checkpoint);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Iterations; index++)
        {
            unexpectedTelemetry |= ExecutionTelemetry.IsEnabled;
            unexpectedTelemetry |= ExecutionTelemetry.StartActivity(
                ExecutionTelemetryActivityKind.Activation) is not null;
            StorageExecutionTelemetry.RecordCheckpoint(checkpoint);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.False(unexpectedTelemetry);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void ObserverFailures_DoNotAlterExecutionOrMetricRecordingCallers()
    {
        var stopped = 0;
        var measured = 0;
        using ActivityListener activityListener = new()
        {
            ShouldListenTo = static source => string.Equals(
                source.Name,
                ExecutionTelemetry.ActivitySourceName,
                StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ =>
            {
                Interlocked.Increment(ref stopped);
                throw new InvalidOperationException("The test activity observer failed.");
            }
        };
        ActivitySource.AddActivityListener(activityListener);
        using MeterListener meterListener = new();
        meterListener.InstrumentPublished = static (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, ExecutionTelemetry.MeterName, StringComparison.Ordinal)
                && string.Equals(
                    instrument.Name,
                    ExecutionTelemetry.CheckpointsInstrumentName,
                    StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            Interlocked.Increment(ref measured);
            throw new InvalidOperationException("The test metric observer failed.");
        });
        meterListener.Start();

        var activity = ExecutionTelemetry.StartActivity(ExecutionTelemetryActivityKind.Checkpoint);
        ExecutionTelemetry.CompleteActivity(activity, ExecutionTelemetryOutcome.Succeeded);
        ExecutionTelemetry.RecordCheckpoint(
            signalCount: 0,
            pendingSignalCount: 0,
            retryCount: 0,
            backlogCount: 0,
            outcome: ExecutionTelemetryOutcome.Succeeded);

        Assert.Equal(1, Volatile.Read(ref stopped));
        Assert.Equal(1, Volatile.Read(ref measured));
    }

    [Fact]
    public void DurableCheckpoint_ProjectsCommonHealthAndReadinessFromCanonicalState()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var status = ProcessDurableExecutionStatusProjector.Project(fixture.Checkpoint);
        var health = ExecutionHealthProjector.Project(
            status,
            fixture.Plan.Document.Metadata.Provenance,
            ["checkpoint/tests"]);

        Assert.Equal(fixture.Checkpoint.Continuation.CompletedActivationCount, status.Runtime.Progress?.Completed);
        Assert.Equal(
            fixture.Checkpoint.Continuation.Tokens.Count(token =>
                token.Disposition != ExecutionTokenDisposition.Waiting
                || fixture.Checkpoint.Continuation.Waits.Any(wait => wait.Active && wait.Token == token.Id)),
            status.Runtime.Tokens.Length);
        Assert.Equal(
            fixture.Checkpoint.Continuation.Waits.Count(static wait => wait.Active),
            status.Runtime.Waits.Length);
        Assert.Equal(ExecutionHealthStatus.Healthy, health.Health);
        Assert.Equal(ExecutionReadinessStatus.Ready, health.Readiness);

        var unknownStatus = ExecutionStatusProjector.Project(ProcessControlTestFixture.Create().State());
        var unknownHealth = ExecutionHealthProjector.Project(
            unknownStatus,
            fixture.Plan.Document.Metadata.Provenance);
        Assert.Equal(ExecutionHealthStatus.Unknown, unknownHealth.Health);
        Assert.Equal(ExecutionReadinessStatus.Unknown, unknownHealth.Readiness);
    }

    [Fact]
    public void ProcessStateProjection_RequiresExactContinuationAndControlAffinity()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var checkpoint = fixture.Checkpoint;

        var status = ProcessExecutionStatusProjector.Project(
            checkpoint.Continuation,
            checkpoint.Control,
            checkpoint.DurableOperations);

        Assert.Equal(checkpoint.Definition, status.Definition);
        Assert.Equal(checkpoint.ContinuationIdentity.ProcessInstanceId, status.ProcessInstanceId);
        Assert.Equal(checkpoint.ContinuationIdentity.ProcessAttemptId, status.CurrentAttemptId);
        Assert.Equal(checkpoint.Control.Revision, status.ControlRevision);
        Assert.Equal(checkpoint.Continuation.CompletedActivationCount, status.Runtime.Progress?.Completed);

        var unrelatedControl = ProcessControlTestFixture.Create().State();
        var exception = Assert.Throws<ArgumentException>(() => ProcessExecutionStatusProjector.Project(
            checkpoint.Continuation,
            unrelatedControl,
            checkpoint.DurableOperations));
        Assert.Contains("same exact definition", exception.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(() => ProcessExecutionStatusProjector.Project(
            checkpoint.Continuation,
            checkpoint.Control,
            checkpoint.DurableOperations,
            terminalDetailDisclosure: ExecutionStatusDisclosure.Unknown));
    }

    static (ExecutionExplainArtifact Explain, NormalizedExecutionTrace Trace) ExplainAndTrace()
    {
        var provenance = new ExecutionProvenance(
            new("execution-telemetry-tests", "1"),
            new(PrivatePayload),
            DocumentOrigin.Generated);
        var definition = new ExecutionDefinitionReference(
            new("transition/telemetry-tests"),
            new("revision/1"),
            new(
                "sha256",
                "cohesive-test/v1",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
        ExecutionDefinitionKind kind = new("transition");
        var trace = new NormalizedExecutionTrace(
            NormalizedExecutionTrace.CurrentSchemaVersion,
            kind,
            definition,
            continuation: null,
            new("activation/telemetry-tests"),
            "Applied",
            safePointNode: null,
            durableCommitSequence: null,
            events: []);
        var explainedDefinition = new ExecutionExplainDefinitionReference(
            kind,
            ExecutionDefinitionDocument.CurrentSchemaVersion,
            definition,
            provenance,
            ExecutionSourceMap.Empty);
        var interpreter = new ExecutionInterpreterProfileReference(
            "execution-telemetry-tests/reference",
            "1",
            new([ExecutionDefinitionDocument.CurrentSchemaVersion]),
            [kind],
            provenance);
        var explain = new ExecutionExplainArtifact(
            ExecutionExplainArtifact.CurrentSchemaVersion,
            explainedDefinition,
            interpreter,
            [
                new(
                    ExecutionExplainStageNames.Definition,
                    "execution.definition",
                    definition.DefinitionId.Value,
                    ExecutionExplainEvidenceAuthority.Declared,
                    "Available",
                    sourceReferences: [PrivatePayload])
            ],
            ExecutionExplainTraceReference.From(trace));
        return (explain, trace);
    }

    sealed class ActivityCollector : IDisposable
    {
        readonly ActivityListener listener;
        readonly ConcurrentQueue<ActivitySnapshot> snapshots = new();

        internal ActivityCollector()
        {
            listener = new()
            {
                ShouldListenTo = static source => string.Equals(
                    source.Name,
                    ExecutionTelemetry.ActivitySourceName,
                    StringComparison.Ordinal),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => snapshots.Enqueue(new(
                    activity.OperationName,
                    activity.SpanId,
                    activity.ParentSpanId,
                    activity.Tags.ToImmutableDictionary(
                        static tag => tag.Key,
                        static tag => tag.Value ?? string.Empty,
                        StringComparer.Ordinal)))
            };
            ActivitySource.AddActivityListener(listener);
        }

        internal ImmutableArray<ActivitySnapshot> Snapshots => [.. snapshots];

        public void Dispose() => listener.Dispose();
    }

    sealed class MeasurementCollector : IDisposable
    {
        readonly MeterListener listener = new();
        readonly ConcurrentQueue<MeasurementSnapshot> snapshots = new();

        internal MeasurementCollector()
        {
            listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, ExecutionTelemetry.MeterName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
                Record(instrument, measurement, tags));
            listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
                Record(instrument, measurement, tags));
            listener.Start();
        }

        internal ImmutableArray<MeasurementSnapshot> Snapshots => [.. snapshots];

        void Record<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            where T : struct
        {
            var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                values.Add(tag.Key, tag.Value?.ToString() ?? string.Empty);
            }

            snapshots.Enqueue(new(
                instrument.Name,
                instrument.Unit,
                Convert.ToDouble(measurement),
                values.ToImmutable()));
        }

        public void Dispose() => listener.Dispose();
    }

    sealed record ActivitySnapshot(
        string Name,
        ActivitySpanId SpanId,
        ActivitySpanId ParentSpanId,
        ImmutableDictionary<string, string> Tags);

    sealed record MeasurementSnapshot(
        string Instrument,
        string? Unit,
        double Value,
        ImmutableDictionary<string, string> Tags);
}
