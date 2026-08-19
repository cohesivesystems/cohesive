using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;
using Cohesive.Relations.Observability;
using Cohesive.Relations.TestFixtures;

namespace Cohesive.Relations.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RelationQueryTelemetryTestCollection
{
    public const string Name = "Relation query telemetry";
}

[Collection(RelationQueryTelemetryTestCollection.Name)]
public sealed class RelationQueryTelemetryTests
{
    const string PrivatePayload = "private-payload-7f52a826";

    [Fact]
    public async Task Evaluation_EmitsNestedOperationActivitiesWithoutSemanticPayloads()
    {
        using ActivityCollector collector = new(RelationQueryTelemetry.ActivitySourceName);
        var evaluation = CreateEvaluation(PrivatePayload);
        var evaluator = CreateEvaluator(evaluation, PrivatePayload);

        var outcome = await evaluator.EvaluateAsync(evaluation);

        Assert.True(outcome.IsSuccessful);
        var evaluationActivity = Assert.Single(
            collector.Snapshots,
            static snapshot => snapshot.Name == RelationQueryTelemetry.EvaluationActivityName);
        var compilationActivity = Assert.Single(
            collector.Snapshots,
            snapshot => snapshot.Name == RelationQueryTelemetry.StaticCompilationActivityName
                        && snapshot.ParentSpanId == evaluationActivity.SpanId);
        var feasibilityActivity = Assert.Single(
            collector.Snapshots,
            snapshot => snapshot.Name == RelationQueryTelemetry.ProfileFeasibilityActivityName
                        && snapshot.ParentSpanId == evaluationActivity.SpanId);
        var planningActivity = Assert.Single(
            collector.Snapshots,
            snapshot => snapshot.Name == RelationQueryTelemetry.PhysicalPlanningActivityName
                        && snapshot.ParentSpanId == evaluationActivity.SpanId);
        var executionActivity = Assert.Single(
            collector.Snapshots,
            snapshot => snapshot.Name == RelationQueryTelemetry.PhysicalExecutionActivityName
                        && snapshot.ParentSpanId == evaluationActivity.SpanId);
        var readActivity = Assert.Single(
            collector.Snapshots,
            snapshot => snapshot.Name == RelationQueryTelemetry.SourceReadActivityName
                        && snapshot.ParentSpanId == executionActivity.SpanId);
        var interpretationActivity = Assert.Single(
            collector.Snapshots,
            snapshot => snapshot.Name == RelationQueryTelemetry.InterpretationActivityName
                        && snapshot.ParentSpanId == executionActivity.SpanId);

        Assert.Equal(ActivityKind.Internal, compilationActivity.Kind);
        Assert.Equal(ActivityKind.Internal, feasibilityActivity.Kind);
        Assert.Equal(ActivityKind.Internal, planningActivity.Kind);
        Assert.Equal(ActivityKind.Client, readActivity.Kind);
        Assert.Equal(
            RelationQueryTelemetry.SucceededStatus,
            evaluationActivity.Tags[RelationQueryTelemetry.StatusTagName]);
        Assert.Equal(
            evaluation.Fingerprint.Value,
            evaluationActivity.Tags[RelationQueryTelemetry.EvaluationFingerprintTagName]);
        Assert.Equal(
            "complete",
            readActivity.Tags[RelationQueryTelemetry.StatusTagName]);
        Assert.Equal(
            RelationQueryTelemetry.SucceededStatus,
            interpretationActivity.Tags[RelationQueryTelemetry.StatusTagName]);

        var emitted = string.Join(
            '|',
            collector.Snapshots.SelectMany(static snapshot => snapshot.Tags)
                .Select(static tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain(PrivatePayload, emitted, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence/private", emitted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metrics_UseBoundedTagSetsForSourceRowsGapsAndDtoRows()
    {
        using MeasurementCollector collector = new();

        _ = await RelationDtoMapperTestFixture.ExecuteFederatedAsync();
        var scenario = RelationDtoBenchmarkFixture.CreateJoinedScenario(
            rowCount: 2,
            RelationDtoFixtureVariant.MissingCustomer);
        var compilation = new RelationDtoMapperCompiler().Compile<LoadSearchDto>(scenario.Plan);
        var mapper = Assert.IsType<CompiledRelationDtoMapper<LoadSearchDto>>(compilation.Mapper);
        _ = mapper.Map(
            scenario.Execution,
            RelationDtoMappingFailurePolicy.CollectDiagnostics);

        Assert.Contains(
            collector.Snapshots,
            static measurement => measurement.Instrument == RelationQueryTelemetry.SourceRowsInstrumentName
                                  && measurement.Value > 0);
        Assert.Contains(
            collector.Snapshots,
            static measurement => measurement.Instrument == RelationQueryTelemetry.RequirementGapsInstrumentName
                                  && measurement.Value > 0
                                  && measurement.Tags[RelationQueryTelemetry.GapCauseTagName]
                                  == "related_observation_not_found");
        Assert.Contains(
            collector.Snapshots,
            static measurement => measurement.Instrument == RelationQueryTelemetry.DtoRowsInstrumentName
                                  && measurement.Value > 0
                                  && measurement.Tags[RelationQueryTelemetry.RowOutcomeTagName]
                                  == RelationQueryTelemetry.InputRowOutcome);
        Assert.Contains(
            collector.Snapshots,
            static measurement => measurement.Instrument == RelationQueryTelemetry.OperationDurationInstrumentName
                                  && measurement.Value >= 0
                                  && measurement.Tags[RelationQueryTelemetry.OperationTagName]
                                  == RelationQueryTelemetry.DtoMappingActivityName);

        Assert.All(collector.Snapshots, static measurement =>
        {
            string[] allowedTags = measurement.Instrument switch
            {
                RelationQueryTelemetry.OperationDurationInstrumentName =>
                [
                    RelationQueryTelemetry.OperationTagName,
                    RelationQueryTelemetry.StatusTagName,
                    RelationQueryTelemetry.TerminalPhaseTagName
                ],
                RelationQueryTelemetry.SourceRowsInstrumentName =>
                [RelationQueryTelemetry.ReadKindTagName, RelationQueryTelemetry.StatusTagName],
                RelationQueryTelemetry.DtoRowsInstrumentName =>
                [
                    RelationQueryTelemetry.RowOutcomeTagName,
                    RelationQueryTelemetry.StatusTagName,
                    RelationQueryTelemetry.FailurePolicyTagName
                ],
                RelationQueryTelemetry.RequirementGapsInstrumentName =>
                [RelationQueryTelemetry.GapCauseTagName],
                _ => throw new InvalidOperationException(
                    $"Unexpected relation/query metric '{measurement.Instrument}'.")
            };
            Assert.All(measurement.Tags.Keys, tag => Assert.Contains(tag, allowedTags));
        });
    }

    [Fact]
    public void Emitter_DisabledPathDoesNotStartTelemetryOrAllocate()
    {
        using RelationQueryTelemetryEmitter emitter = new(
            $"Cohesive.Relations.Tests.Disabled.{Guid.NewGuid():N}");
        const int Iterations = 10_000;
        var unexpectedTelemetry = false;

        for (var index = 0; index < 100; index++)
        {
            unexpectedTelemetry |= emitter.IsEnabled;
            unexpectedTelemetry |= emitter.StartActivity(RelationQueryTelemetry.EvaluationActivityName) is not null;
            unexpectedTelemetry |= emitter.StartTimer() != 0L;
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Iterations; index++)
        {
            unexpectedTelemetry |= emitter.IsEnabled;
            unexpectedTelemetry |= emitter.StartActivity(RelationQueryTelemetry.EvaluationActivityName) is not null;
            unexpectedTelemetry |= emitter.StartTimer() != 0L;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.False(unexpectedTelemetry);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void CompilerLifecycle_DisabledPathExecutesStaticCallbackWithoutAllocating()
    {
        using RelationQueryTelemetryEmitter emitter = new(
            $"Cohesive.Relations.Tests.DisabledCompiler.{Guid.NewGuid():N}");
        const int Iterations = 10_000;
        Func<int, int> compile = static state => state + 1;
        Func<int, string> getStatus = static _ => RelationQueryTelemetry.SucceededStatus;
        var sum = 0;
        for (var index = 0; index < 100; index++)
        {
            sum += RelationQueryCompilerTelemetry.Observe(
                emitter,
                RelationQueryTelemetry.NativeCompilationActivityName,
                index,
                compile,
                getStatus);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Iterations; index++)
        {
            sum += RelationQueryCompilerTelemetry.Observe(
                emitter,
                RelationQueryTelemetry.NativeCompilationActivityName,
                index,
                compile,
                getStatus);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(sum > 0);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void CompilerLifecycle_ContainsTelemetryPolicyFailureAndReturnsCompilerResult()
    {
        var instrumentationName = $"Cohesive.Relations.Tests.CompilerPolicyFailure.{Guid.NewGuid():N}";
        var stoppedCount = 0;
        string? status = null;
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => string.Equals(source.Name, instrumentationName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                Interlocked.Increment(ref stoppedCount);
                status = activity.GetTagItem(RelationQueryTelemetry.StatusTagName) as string;
            }
        };
        ActivitySource.AddActivityListener(listener);
        using RelationQueryTelemetryEmitter emitter = new(instrumentationName);

        var result = RelationQueryCompilerTelemetry.Observe(
            emitter,
            RelationQueryTelemetry.NativeCompilationActivityName,
            41,
            static state => state + 1,
            static _ => RelationQueryTelemetry.SucceededStatus,
            static (_, _, _) => throw new InvalidOperationException("The telemetry projector failed."));

        Assert.Equal(42, result);
        Assert.Equal(1, Volatile.Read(ref stoppedCount));
        Assert.Equal(RelationQueryTelemetry.ObservabilityFailureStatus, status);
    }

    [Fact]
    public void Emitter_ContainsSynchronousObserverFailures()
    {
        var instrumentationName = $"Cohesive.Relations.Tests.ObserverFailure.{Guid.NewGuid():N}";
        var stoppedCount = 0;
        var measurementCount = 0;
        using ActivityListener activityListener = new()
        {
            ShouldListenTo = source => string.Equals(
                source.Name,
                instrumentationName,
                StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _ =>
            {
                Interlocked.Increment(ref stoppedCount);
                throw new InvalidOperationException("The test activity observer failed.");
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        using MeterListener meterListener = new();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, instrumentationName, StringComparison.Ordinal)
                && string.Equals(
                    instrument.Name,
                    RelationQueryTelemetry.OperationDurationInstrumentName,
                    StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((_, _, _, _) =>
        {
            Interlocked.Increment(ref measurementCount);
            throw new InvalidOperationException("The test metric observer failed.");
        });
        meterListener.Start();

        using RelationQueryTelemetryEmitter emitter = new(instrumentationName);
        var activity = emitter.StartActivity(RelationQueryTelemetry.EvaluationActivityName);
        var started = emitter.StartTimer();

        emitter.CompleteOperation(
            activity,
            started,
            RelationQueryTelemetry.EvaluationActivityName,
            RelationQueryTelemetry.SucceededStatus);

        Assert.Equal(1, Volatile.Read(ref measurementCount));
        Assert.Equal(1, Volatile.Read(ref stoppedCount));
    }

    [Fact]
    public void Emitter_ContainsActivitySamplingFailures()
    {
        var instrumentationName = $"Cohesive.Relations.Tests.SamplingFailure.{Guid.NewGuid():N}";
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => string.Equals(
                source.Name,
                instrumentationName,
                StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                throw new InvalidOperationException("The test sampler failed."),
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                throw new InvalidOperationException("The test sampler failed.")
        };
        ActivitySource.AddActivityListener(listener);
        using RelationQueryTelemetryEmitter emitter = new(instrumentationName);

        var activity = emitter.StartActivity(RelationQueryTelemetry.EvaluationActivityName);

        Assert.Null(activity);
    }

    [Fact]
    public void Emitter_ContainsSynchronousRegistrationObserverFailures()
    {
        var instrumentationName = $"Cohesive.Relations.Tests.RegistrationFailure.{Guid.NewGuid():N}";
        using ActivityListener activityListener = new()
        {
            ShouldListenTo = source => string.Equals(source.Name, instrumentationName, StringComparison.Ordinal)
                ? throw new InvalidOperationException("The test activity registration observer failed.")
                : false
        };
        ActivitySource.AddActivityListener(activityListener);
        using MeterListener meterListener = new()
        {
            InstrumentPublished = (instrument, _) =>
            {
                if (string.Equals(instrument.Meter.Name, instrumentationName, StringComparison.Ordinal))
                    throw new InvalidOperationException("The test instrument registration observer failed.");
            }
        };
        meterListener.Start();

        using RelationQueryTelemetryEmitter emitter = new(instrumentationName);

        Assert.False(emitter.IsEnabled);
        Assert.Null(emitter.StartActivity(RelationQueryTelemetry.NativeCompilationActivityName));
        Assert.Equal(0L, emitter.StartTimer());
    }

    [Fact]
    public void FingerprintTags_RejectUnboundedOrNonCanonicalValues()
    {
        using Activity activity = new("tests.relations.fingerprint-tag");
        activity.Start();

        var rejected = RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.PlanFingerprintTagName,
            PrivatePayload);
        var accepted = RelationQueryTelemetry.TrySetFingerprintTag(
            activity,
            RelationQueryTelemetry.ArtifactFingerprintTagName,
            new string('a', 64));

        Assert.False(rejected);
        Assert.True(accepted);
        Assert.Null(activity.GetTagItem(RelationQueryTelemetry.PlanFingerprintTagName));
        Assert.Equal(
            new string('a', 64),
            activity.GetTagItem(RelationQueryTelemetry.ArtifactFingerprintTagName));
    }

    [Fact]
    public void DiagnosticEvents_RecordOnlyStructuredCodeAndSeverity()
    {
        using Activity activity = new("tests.relations.diagnostic-event");
        activity.IsAllDataRequested = true;
        activity.Start();

        RelationQueryTelemetry.AddDiagnosticEvent(
            activity,
            "REL-TEST-STRUCTURED",
            DiagnosticSeverity.Warning);

        var diagnostic = Assert.Single(activity.Events);
        Assert.Equal(RelationQueryTelemetry.DiagnosticEventName, diagnostic.Name);
        var tags = diagnostic.Tags.ToImmutableDictionary(
            static tag => tag.Key,
            static tag => Convert.ToString(tag.Value, CultureInfo.InvariantCulture),
            StringComparer.Ordinal);
        Assert.Equal(
            "REL-TEST-STRUCTURED",
            tags[RelationQueryTelemetry.DiagnosticCodeTagName]);
        Assert.Equal("warning", tags[RelationQueryTelemetry.DiagnosticSeverityTagName]);
        Assert.Equal(2, tags.Count);
        Assert.DoesNotContain(PrivatePayload, string.Join('|', tags.Values), StringComparison.Ordinal);
    }

    static RelationQueryEvaluation CreateEvaluation(string privatePayload) =>
        LoadCustomerRelationFixture.BaselineRelationDocument
            .Evaluate(
                new($"evaluation/{privatePayload}"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Supply(
            [
                new Observation(
                    LoadCustomerRelationFixture.LoadShapeLocalId,
                    $"load/{privatePayload}",
                    new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                    {
                        [LoadCustomerRelationFixture.LoadIdFieldName] =
                            ObservationValue.FromString($"load/{privatePayload}"),
                        [LoadCustomerRelationFixture.LoadCustomerIdFieldName] =
                            ObservationValue.FromString($"customer/{privatePayload}")
                    })
            ],
            evidenceReference: $"evidence/private/{privatePayload}")
            .Build();

    static RelationQueryEvaluator CreateEvaluator(
        RelationQueryEvaluation evaluation,
        string privatePayload)
    {
        var compilation = RelationQueryStaticCompiler.Compile(evaluation.Compilation);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var placement = LoadCustomerRelationFixture.CreatePhysicalPlacement(plan);
        var customerSource = placement.SourceInstances.Single(
            static source => source.Id == FederatedLoadPhysicalExecutionFixture.CustomersSource);
        DeterministicRelationQuerySourceReader reader = new(
            new(
                customerSource.Id,
                customerSource.ExecutionDomain,
                customerSource.TargetProfile,
                RelationQueryLogicalPartitionIdentity.WholeSource),
            [
                DeterministicRelationQuerySourceReader.SourceRow.Create(
                    $"customer/{privatePayload}",
                    (
                        LoadCustomerRelationFixture.CustomerIdPath,
                        ObservationValue.FromString($"customer/{privatePayload}")),
                    (
                        LoadCustomerRelationFixture.CustomerNamePath,
                        ObservationValue.FromString($"name/{privatePayload}")),
                    (
                        LoadCustomerRelationFixture.CustomerTypePath,
                        ObservationValue.FromString($"type/{privatePayload}")))
            ]);
        return new(
            static compiledPlan => LoadCustomerRelationFixture.CreatePhysicalPlacement(compiledPlan),
            FederatedLoadPhysicalExecutionFixture.CreatePolicy(),
            [reader]);
    }

    sealed class ActivityCollector : IDisposable
    {
        readonly ActivityListener listener;
        readonly ConcurrentQueue<ActivitySnapshot> snapshots = new();

        public ActivityCollector(string sourceName)
        {
            listener = new()
            {
                ShouldListenTo = source => string.Equals(source.Name, sourceName, StringComparison.Ordinal),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => snapshots.Enqueue(new(
                    activity.OperationName,
                    activity.TraceId,
                    activity.SpanId,
                    activity.ParentSpanId,
                    activity.Kind,
                    activity.Status,
                    activity.TagObjects.ToImmutableDictionary(
                        static tag => tag.Key,
                        static tag => Convert.ToString(tag.Value, CultureInfo.InvariantCulture),
                        StringComparer.Ordinal)))
            };
            ActivitySource.AddActivityListener(listener);
        }

        public ImmutableArray<ActivitySnapshot> Snapshots => [.. snapshots];

        public void Dispose() => listener.Dispose();
    }

    sealed class MeasurementCollector : IDisposable
    {
        readonly MeterListener listener = new();
        readonly ConcurrentQueue<MeasurementSnapshot> snapshots = new();

        public MeasurementCollector()
        {
            listener.InstrumentPublished = static (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, RelationQueryTelemetry.MeterName, StringComparison.Ordinal)
                    && IsSupportedInstrument(instrument.Name))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                snapshots.Enqueue(new(instrument.Name, value, CopyTags(tags))));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                snapshots.Enqueue(new(instrument.Name, value, CopyTags(tags))));
            listener.Start();
        }

        public ImmutableArray<MeasurementSnapshot> Snapshots => [.. snapshots];

        public void Dispose() => listener.Dispose();

        static bool IsSupportedInstrument(string name) => name is
            RelationQueryTelemetry.OperationDurationInstrumentName
            or RelationQueryTelemetry.SourceRowsInstrumentName
            or RelationQueryTelemetry.DtoRowsInstrumentName
            or RelationQueryTelemetry.RequirementGapsInstrumentName;

        static ImmutableDictionary<string, string?> CopyTags(
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags)
                builder.Add(tag.Key, Convert.ToString(tag.Value, CultureInfo.InvariantCulture));
            return builder.ToImmutable();
        }
    }

    sealed record ActivitySnapshot(
        string Name,
        ActivityTraceId TraceId,
        ActivitySpanId SpanId,
        ActivitySpanId ParentSpanId,
        ActivityKind Kind,
        ActivityStatusCode Status,
        ImmutableDictionary<string, string?> Tags);

    sealed record MeasurementSnapshot(
        string Instrument,
        double Value,
        ImmutableDictionary<string, string?> Tags);
}
