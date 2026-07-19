using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Relations;

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
            Assert.NotEmpty(observed.ArtifactBoundRealizations);
            Assert.All(observed.ArtifactBoundRealizations, fingerprint =>
                Assert.Equal(observed.Bound.Fingerprint, fingerprint));
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
            Assert.Equal(0, observed.ArtifactCount);
        }
    }

    static string Format(ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics) =>
        string.Join(
            Environment.NewLine,
            diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}

internal sealed record RelationQueryAdapterConformanceCase(
    string Adapter,
    Func<RelationQuerySupportedContextObservation> ObserveSupported,
    Func<RelationQueryRejectedContextObservation> ObserveRejected);

internal sealed record RelationQuerySupportedContextObservation(
    RelationQueryBoundRealizationReport Bound,
    RelationQueryBoundRealizationReport RepeatedBound,
    RelationQueryNativeCompilationStatus NativeStatus,
    ImmutableArray<RelationQueryBoundRealizationFingerprint> ArtifactBoundRealizations);

internal sealed record RelationQueryRejectedContextObservation(
    RelationQueryBoundRealizationReport Bound,
    RelationQueryNativeCompilationStatus CompilationStatus,
    int ArtifactCount);
