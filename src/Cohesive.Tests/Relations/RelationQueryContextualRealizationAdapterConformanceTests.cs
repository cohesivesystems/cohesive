using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Elastic;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Explain;
using Cohesive.Relations.Observability;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Relations;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RelationQueryAdapterTelemetryTestCollection
{
    public const string Name = "Relation query adapter telemetry";
}

[Collection(RelationQueryAdapterTelemetryTestCollection.Name)]
public sealed class RelationQueryContextualRealizationAdapterConformanceTests
{
    static readonly ImmutableArray<RelationQueryAdapterConformanceCase> Cases =
    [
        Model.CosmosRelationQueryCompilerTests.CreateBoundRealizationConformanceCase(),
        Elastic.ElasticRelationQueryCompilerTests.CreateBoundRealizationConformanceCase(),
        Postgres.PostgresRelationQueryCompilerTests.CreateBoundRealizationConformanceCase()
    ];

    [Fact]
    public void SupportedContext_RealizationPredictsExactNativeCompilationDeterministically()
    {
        foreach (var item in Cases)
        {
            var observed = item.ObserveSupported();
            var context = $"{item.Adapter}: {Format(observed.Bound.Diagnostics)}";

            Assert.True(observed.Bound.IsRealizable, context);
            Assert.True(observed.RepeatedBound.IsRealizable, context);
            Assert.Equal(observed.Bound.Fingerprint, observed.RepeatedBound.Fingerprint);
            Assert.Equal(
                observed.Bound.Evidence.Binding.Fingerprint,
                observed.RepeatedBound.Evidence.Binding.Fingerprint);
            Assert.Equal(
                observed.Bound.Evidence.Assessments.Select(static assessment => assessment.Id),
                observed.RepeatedBound.Evidence.Assessments.Select(static assessment => assessment.Id));
            Assert.NotEmpty(observed.Bound.Evidence.Assessments);
            Assert.All(observed.Bound.Evidence.Assessments, assessment =>
                Assert.Equal(RelationQueryBoundAssessmentStatus.Available, assessment.Status));
            Assert.Equal(RelationQueryNativeCompilationStatus.Exact, observed.NativeStatus);
            Assert.Equal(RelationQueryNativeCompilationStatus.Exact, observed.NativeExplanation.Status);
            Assert.NotEmpty(observed.ArtifactBoundRealizations);
            Assert.Equal(observed.ArtifactBoundRealizations.Length, observed.NativeExplanation.Artifacts.Length);
            Assert.All(observed.ArtifactBoundRealizations, fingerprint =>
                Assert.Equal(observed.Bound.Fingerprint, fingerprint));
            Assert.All(observed.NativeExplanation.Artifacts, artifact =>
                Assert.Equal(observed.Bound.Fingerprint, artifact.Provenance.BoundRealization));
        }
    }

    [Fact]
    public void TargetSpecificConstraint_IsRejectedDuringBoundRealizationBeforeNativeCompilation()
    {
        foreach (var item in Cases)
        {
            var observed = item.ObserveRejected();
            var context = $"{item.Adapter}: {Format(observed.Bound.Diagnostics)}";

            Assert.True(observed.Bound.Status == RelationQueryRealizationStatus.NotRealizable, context);
            Assert.Contains(observed.Bound.Evidence.Assessments, assessment =>
                assessment.Status == RelationQueryBoundAssessmentStatus.Unavailable);
            Assert.Contains(observed.Bound.Diagnostics, diagnostic =>
                diagnostic.Code == RelationQueryRealizationDiagnosticCodes.ContextUnavailable
                && diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, observed.CompilationStatus);
            Assert.Equal(RelationQueryNativeCompilationStatus.Unsupported, observed.NativeExplanation.Status);
            Assert.Equal(0, observed.ArtifactCount);
            Assert.Empty(observed.NativeExplanation.Artifacts);
            Assert.Contains(observed.NativeExplanation.Diagnostics, diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error);
        }
    }

    [Fact]
    public void CurrentAdapters_EmitBoundedCompilerActivitiesWithoutPayloadTags()
    {
        HashSet<string> permittedTags =
        [
            RelationQueryTelemetry.OperationTagName,
            RelationQueryTelemetry.StatusTagName,
            RelationQueryTelemetry.RequestKindTagName,
            RelationQueryTelemetry.TargetTagName,
            RelationQueryTelemetry.PlanFingerprintTagName,
            RelationQueryTelemetry.RealizationFingerprintTagName,
            RelationQueryTelemetry.BoundRealizationFingerprintTagName,
            RelationQueryTelemetry.PlacementFingerprintTagName,
            RelationQueryTelemetry.BindingFingerprintTagName,
            RelationQueryTelemetry.ArtifactFingerprintTagName,
            RelationQueryTelemetry.ArtifactCountTagName,
            RelationQueryTelemetry.BranchCountTagName,
            RelationQueryTelemetry.DiagnosticCountTagName
        ];

        foreach (var item in Cases)
        {
            List<Activity> stopped = [];
            List<string?> measuredOperations = [];
            using ActivityListener listener = new()
            {
                ShouldListenTo = source => string.Equals(
                    source.Name,
                    item.InstrumentationName,
                    StringComparison.Ordinal),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = stopped.Add
            };
            ActivitySource.AddActivityListener(listener);
            using MeterListener meterListener = new();
            meterListener.InstrumentPublished = (instrument, observedListener) =>
            {
                if (string.Equals(instrument.Meter.Name, item.InstrumentationName, StringComparison.Ordinal)
                    && string.Equals(
                        instrument.Name,
                        RelationQueryTelemetry.OperationDurationInstrumentName,
                        StringComparison.Ordinal))
                {
                    observedListener.EnableMeasurementEvents(instrument);
                }
            };
            meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (string.Equals(tag.Key, RelationQueryTelemetry.OperationTagName, StringComparison.Ordinal))
                    {
                        measuredOperations.Add(tag.Value as string);
                        break;
                    }
                }
            });
            meterListener.Start();

            var observation = item.ObserveSupported();

            var compilerActivities = stopped.Where(activity =>
                activity.OperationName is RelationQueryTelemetry.RealizationActivityName
                    or RelationQueryTelemetry.NativeCompilationActivityName).ToArray();
            Assert.Equal(
                3,
                compilerActivities.Count(activity =>
                    activity.OperationName == RelationQueryTelemetry.RealizationActivityName));
            var compilationActivity = Assert.Single(compilerActivities, activity =>
                activity.OperationName == RelationQueryTelemetry.NativeCompilationActivityName);
            Assert.Single(
                compilerActivities,
                activity => activity.OperationName == RelationQueryTelemetry.RealizationActivityName
                            && activity.ParentSpanId == compilationActivity.SpanId);
            Assert.Equal(
                RelationQueryTelemetry.BoundRequestKind,
                compilationActivity.GetTagItem(RelationQueryTelemetry.RequestKindTagName));
            Assert.Equal(
                observation.Bound.Fingerprint.Value,
                compilationActivity.GetTagItem(RelationQueryTelemetry.BoundRealizationFingerprintTagName));
            Assert.Equal(
                3,
                measuredOperations.Count(operation =>
                    operation == RelationQueryTelemetry.RealizationActivityName));
            Assert.Equal(
                1,
                measuredOperations.Count(operation =>
                    operation == RelationQueryTelemetry.NativeCompilationActivityName));
            Assert.All(compilerActivities, activity =>
            {
                Assert.NotNull(activity.GetTagItem(RelationQueryTelemetry.StatusTagName));
                Assert.All(activity.TagObjects, tag => Assert.Contains(tag.Key, permittedTags));
                Assert.DoesNotContain(activity.TagObjects, tag =>
                    tag.Value is string text
                    && (text.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("query", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("container", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("index", StringComparison.OrdinalIgnoreCase)));
            });
        }
    }

    static string Format(ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics) =>
        string.Join(
            Environment.NewLine,
            diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}

internal sealed record RelationQueryAdapterConformanceCase(
    string Adapter,
    string InstrumentationName,
    Func<RelationQuerySupportedContextObservation> ObserveSupported,
    Func<RelationQueryRejectedContextObservation> ObserveRejected);

internal sealed record RelationQuerySupportedContextObservation(
    RelationQueryBoundRealizationReport Bound,
    RelationQueryBoundRealizationReport RepeatedBound,
    RelationQueryNativeCompilationStatus NativeStatus,
    ImmutableArray<RelationQueryBoundRealizationFingerprint> ArtifactBoundRealizations,
    RelationQueryNativeCompilationExplanation NativeExplanation);

internal sealed record RelationQueryRejectedContextObservation(
    RelationQueryBoundRealizationReport Bound,
    RelationQueryNativeCompilationStatus CompilationStatus,
    int ArtifactCount,
    RelationQueryNativeCompilationExplanation NativeExplanation);
