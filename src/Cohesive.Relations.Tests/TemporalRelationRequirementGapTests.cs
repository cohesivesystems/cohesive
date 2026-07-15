using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using static Cohesive.Relations.Tests.TemporalRelationQueryFixture;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Contract tests for temporal-join requirement gaps and their expression-site provenance.
/// </summary>
public sealed class TemporalRelationRequirementGapTests
{
    [Fact]
    public void Analyze_MissingTemporalPointRetainsEveryJoinEffectAndExactSiteTrace()
    {
        var plan = Compile();
        var missing = FieldInput(plan, EventShape, OccurredAt);

        var result = RelationRequirementGapAnalyzer.Analyze(
            plan,
            CreateEvidence(plan, missing));

        AssertTemporalGap(
            result,
            missing,
            EventOccurrence,
            RelationQueryExpressionSiteKind.TemporalJoinPoint,
            ordinal: null,
            "query/temporal-query/node/temporal/temporalJoin/pointInInterval/point");
    }

    [Fact]
    public void Analyze_MissingTemporalBoundsRetainEveryJoinEffectAndExactSiteTrace()
    {
        var plan = Compile();

        AssertTemporalGap(
            RelationRequirementGapAnalyzer.Analyze(
                plan,
                CreateEvidence(plan, FieldInput(plan, VersionShape, ValidFrom))),
            FieldInput(plan, VersionShape, ValidFrom),
            VersionOccurrence,
            RelationQueryExpressionSiteKind.TemporalJoinIntervalLowerBound,
            ordinal: 0,
            "query/temporal-query/node/temporal/temporalJoin/pointInInterval/interval/0/lower");
        AssertTemporalGap(
            RelationRequirementGapAnalyzer.Analyze(
                plan,
                CreateEvidence(plan, FieldInput(plan, VersionShape, ValidTo))),
            FieldInput(plan, VersionShape, ValidTo),
            VersionOccurrence,
            RelationQueryExpressionSiteKind.TemporalJoinIntervalUpperBound,
            ordinal: 0,
            "query/temporal-query/node/temporal/temporalJoin/pointInInterval/interval/0/upper");
    }

    [Theory]
    [InlineData(
        RelationQueryParameterEvidenceState.NotProvided,
        RelationRequirementGapCause.InputNotProvided)]
    [InlineData(
        RelationQueryParameterEvidenceState.Missing,
        RelationRequirementGapCause.RequiredValueMissing)]
    public void Analyze_MissingTemporalPointParameterRetainsEveryJoinEffectAndExactSiteTrace(
        RelationQueryParameterEvidenceState state,
        RelationRequirementGapCause expectedCause)
    {
        var parameterId = new QueryParameterId("as-of");
        var fieldMatch = CreatePointMatch();
        var query = CreateQuery(
            new TemporalPointInIntervalMatch(
                Expr.Param(parameterId.Value),
                fieldMatch.Interval));
        var document = RelationQueryDocument.FromDefinition(
            query with
            {
                Body = new LogicalQueryDefinition(
                    query.Body.Nodes,
                    [
                        new QueryParameterDefinition(
                            parameterId,
                            new ScalarTypeRef(ScalarTypeKind.Instant))
                    ])
            });
        var plan = Compile(document);
        var parameter = Assert.Single(plan.InputContract.Parameters);

        var evidence = CreateEvidence(
            plan,
            parameters:
            [
                new RelationQueryParameterEvidence(parameter.Input.Id, state)
            ]);
        var result = RelationRequirementGapAnalyzer.Analyze(plan, evidence);

        Assert.True(result.IsEvidenceValid);
        Assert.True(result.IsConclusive);
        var gap = Assert.Single(result.Gaps);
        Assert.Equal(expectedCause, gap.Cause);
        Assert.Equal(parameter.Input.Id, gap.Input.Id);
        Assert.Null(gap.Occurrence);
        Assert.Equal(
        [
            RelationQueryRequirementEffect.Membership,
            RelationQueryRequirementEffect.Correlation,
            RelationQueryRequirementEffect.Cardinality,
            RelationQueryRequirementEffect.Validation
        ],
            gap.Impacts
                .Select(static impact => impact.Effect)
                .Distinct()
                .OrderBy(static effect => (int)effect));
        Assert.All(gap.Impacts, impact =>
        {
            Assert.Equal(QueryInputRequirement.Required, impact.Requirement);
            Assert.All(impact.Traces, trace =>
            {
                var step = Assert.Single(
                    trace.Steps,
                    candidate => candidate.SiteKind == RelationQueryExpressionSiteKind.TemporalJoinPoint);
                Assert.Equal(RelationQueryRequirementTraceStepKind.ExpressionSite, step.Kind);
                Assert.Equal(TemporalJoin, step.Node);
                Assert.Equal(
                    "query/temporal-query/node/temporal/temporalJoin/pointInInterval/point",
                    step.ExpressionSite?.Value);
                Assert.Null(step.Ordinal);
            });
        });

        var execution = RelationQueryInMemoryInterpreter.Default.Execute(new(plan, evidence));

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, execution.Status);
        var branch = Assert.Single(execution.QueryResults);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, branch.State);
        Assert.Empty(branch.Rows);
        Assert.Contains(
            execution.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive
                && diagnostic.Node == TemporalJoin
                && diagnostic.SemanticSite
                    == "query/temporal-query/node/temporal/temporalJoin/pointInInterval/point");
    }

    [Fact]
    public void Execute_OutputDefaultDoesNotBecomeMissingTemporalMembershipEvidence()
    {
        var plan = Compile();
        var missing = FieldInput(plan, EventShape, OccurredAt);
        var evidence = CreateEvidence(plan, missing);
        var fallback = ObservationValue.FromString("fallback-id");
        var policy = new RelationRequirementGapPolicy(
            new("tests/temporal-output-default-v1"),
            RelationRequirementGapPolicySource.Explicit,
            (_, _) => new(
                RelationRequirementGapDisposition.UseDefault(fallback),
                RelationRequirementGapReportingKind.Suppress));

        var result = RelationQueryInMemoryInterpreter.Default.Execute(new(plan, evidence, policy));

        Assert.Equal(RelationQueryExecutionStatus.Incomplete, result.Status);
        var gap = Assert.Single(result.RequirementGapAnalysis.Gaps);
        Assert.Equal(missing.Id, gap.Input.Id);
        Assert.Contains(
            result.RequirementGapAnalysis.Decisions,
            static decision => decision.Impact.Effect == RelationQueryRequirementEffect.Membership
                && decision.Disposition.Kind == RelationRequirementGapDispositionKind.Unresolved);
        Assert.DoesNotContain(
            result.RequirementGapAnalysis.Decisions,
            static decision => decision.Disposition.Kind
                == RelationRequirementGapDispositionKind.SubstituteDefault);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == RelationRuntimeDiagnosticCodes.DefaultSubstitutionInvalid);
        var branch = Assert.Single(result.QueryResults);
        Assert.Equal(RelationQueryExecutionOutputState.Incomplete, branch.State);
        Assert.Empty(branch.Rows);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.ExecutionEvidenceInconclusive
                && diagnostic.SemanticSite
                    == "query/temporal-query/node/temporal/temporalJoin/pointInInterval/point");
    }

    static readonly RelationQueryObservationOccurrence EventOccurrence = new(
        new("event-1"),
        Event,
        EventShape,
        observationIdentity: "event-1");

    static readonly RelationQueryObservationOccurrence VersionOccurrence = new(
        new("version-1"),
        VersionBinding,
        VersionShape,
        observationIdentity: "version-1");

    static CompiledRelationQueryPlan Compile(RelationQueryDocument? document = null)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            definitionDocument: document ?? CreateQueryDocument(CreatePointMatch()),
            shapeDocuments: [CreateShapeGraphDocument()],
            demand: RelationQueryCompilationDemand.ForQueryResults(
            [
                QueryResultDemand.SelectedFields(
                    new QueryResultId("rows"),
                    [new RelationQueryFieldReference(ResultShape, Id)])
            ])));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message} ({diagnostic.Location})")));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static RelationQueryRuntimeEvidence CreateEvidence(
        CompiledRelationQueryPlan plan,
        RelationQueryFieldInput? missing = null,
        ImmutableArray<RelationQueryParameterEvidence> parameters = default)
    {
        var fields = plan.RequirementGraph.Inputs
            .OfType<RelationQueryFieldInput>()
            .Select(input => missing is not null && input.Id == missing.Id
                ? new RelationQueryFieldEvidence(
                    input.Id,
                    Owner(input).Id,
                    RelationQueryFieldEvidenceState.NotLoaded)
                : new RelationQueryFieldEvidence(
                    input.Id,
                    Owner(input).Id,
                    RelationQueryFieldEvidenceState.Value,
                    Value(input)))
            .ToImmutableArray();

        return new(
            new("tests/temporal-gap-evaluation"),
            plan,
            RelationQueryEvidenceCompleteness.Complete,
            sources:
            [
                SourceEvidence(plan, Event, EventOccurrence),
                SourceEvidence(plan, VersionBinding, VersionOccurrence)
            ],
            fields: fields,
            parameters: parameters,
            capabilities:
            [
                .. plan.InputContract.Capabilities.Select(static capability =>
                    new RelationQueryCapabilityEvidence(
                        capability.Input.Id,
                        RelationQueryCapabilityEvidenceState.Available))
            ]);
    }

    static RelationQuerySourceEvidence SourceEvidence(
        CompiledRelationQueryPlan plan,
        ValueBindingId binding,
        RelationQueryObservationOccurrence occurrence)
    {
        var source = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>(),
            input => input.Binding == binding);
        return new(
            source.Id,
            RelationQuerySourceEvidenceState.Provided,
            [occurrence]);
    }

    static RelationQueryObservationOccurrence Owner(RelationQueryFieldInput input) =>
        input.Binding == Event ? EventOccurrence : VersionOccurrence;

    static ObservationValue Value(RelationQueryFieldInput input)
    {
        if (input.Field.Path == Id)
            return ObservationValue.FromString("event-1");
        if (input.Field.Path == CorrelationKey)
            return ObservationValue.FromString("customer-1");
        if (input.Field.Path == OccurredAt)
        {
            return ObservationValue.FromDateTimeOffset(
                new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        }
        if (input.Field.Path == ValidFrom)
        {
            return ObservationValue.FromDateTimeOffset(
                new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero));
        }
        if (input.Field.Path == ValidTo)
        {
            return ObservationValue.FromDateTimeOffset(
                new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero));
        }

        throw new InvalidOperationException($"Unexpected temporal fixture field '{input.Field}'.");
    }

    static RelationQueryFieldInput FieldInput(
        CompiledRelationQueryPlan plan,
        QualifiedShapeId shape,
        FieldPath path) =>
        Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Field.Shape == shape && input.Field.Path == path);

    static void AssertTemporalGap(
        RelationRequirementGapAnalysisResult result,
        RelationQueryFieldInput expectedInput,
        RelationQueryObservationOccurrence expectedOccurrence,
        RelationQueryExpressionSiteKind expectedSiteKind,
        int? ordinal,
        string expectedSite)
    {
        Assert.True(result.IsEvidenceValid);
        Assert.True(result.IsConclusive);
        var gap = Assert.Single(result.Gaps);
        Assert.Equal(RelationRequirementGapCause.RequiredFieldNotLoaded, gap.Cause);
        Assert.Equal(expectedInput.Id, gap.Input.Id);
        Assert.Equal(expectedOccurrence.Id, gap.Occurrence?.Id);
        Assert.Equal(expectedInput.Field, gap.ValueContext?.Field);
        Assert.Equal(
        [
            RelationQueryRequirementEffect.Membership,
            RelationQueryRequirementEffect.Correlation,
            RelationQueryRequirementEffect.Cardinality,
            RelationQueryRequirementEffect.Validation
        ],
            gap.Impacts
                .Select(static impact => impact.Effect)
                .Distinct()
                .OrderBy(static effect => (int)effect));
        Assert.All(gap.Impacts, impact =>
        {
            Assert.Equal(QueryInputRequirement.Required, impact.Requirement);
            Assert.All(impact.Traces, trace =>
            {
                var step = Assert.Single(
                    trace.Steps,
                    candidate => candidate.SiteKind == expectedSiteKind);
                Assert.Equal(RelationQueryRequirementTraceStepKind.ExpressionSite, step.Kind);
                Assert.Equal(TemporalJoin, step.Node);
                Assert.Equal(expectedSite, step.ExpressionSite?.Value);
                Assert.Equal(ordinal, step.Ordinal);
            });
        });
    }
}
