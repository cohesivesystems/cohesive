using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;

namespace Cohesive.Relations.Tests;

/// <summary>Tests target-neutral canonical query evaluation authoring.</summary>
public sealed class RelationQueryEvaluationAuthoringTests
{
    static readonly QueryParameterId CustomerName = new("customer/name");
    static readonly QueryParameterId Count = new("count");
    static readonly QueryParameterId Defaulted = new("defaulted");
    static readonly QueryParameterId Nullable = new("nullable");
    static readonly QueryParameterId Optional = new("optional");
    static readonly QueryResultId Rows = new("rows");
    static readonly QueryResultId Summary = new("summary");
    static readonly QualifiedShapeId LoadShape = new(new GraphId("tests"), new ShapeId("load"));

    [Fact]
    public void Build_PreservesExactDocumentEvaluationAndOmittedDefaultProvenance()
    {
        var document = CreateDocument();
        var evaluationId = new RelationQueryEvaluationId("evaluation/42");

        var evaluation = document.Evaluate(evaluationId).Build();

        Assert.Same(document, evaluation.Document);
        var query = Assert.IsType<QueryDefinition>(evaluation.Definition);
        Assert.Same(document.Definition, query);
        Assert.Same(document, evaluation.Compilation.DefinitionDocument);
        Assert.Equal(evaluationId, evaluation.Evaluation);
        Assert.Same(RelationQueryCompilationDemand.AllDeclaredOutputs, evaluation.Demand);
        Assert.Equal(RelationQueryCompilationDemandOrigin.Convention, evaluation.DemandOrigin);
        Assert.Null(evaluation.PlanReference);
        Assert.All(
            evaluation.Parameters,
            static parameter => Assert.Equal(
                RelationQueryParameterEvidenceState.NotProvided,
                parameter.State));

        var defaultEvidence = Evidence(evaluation, Defaulted);
        var defaultDeclaration = Assert.Single(
            query.Body.Parameters,
            parameter => parameter.Id == Defaulted);
        Assert.Equal(RelationQueryParameterEvidenceState.NotProvided, defaultEvidence.State);
        Assert.Equal(QueryParameterDefaultKind.Value, defaultDeclaration.DefaultKind);
        Assert.Equal(ObservationValue.FromString("fallback"), defaultDeclaration.DefaultValue);
    }

    [Fact]
    public void Set_OmitNullAndMissing_PreserveDistinctEvidenceStates()
    {
        var evaluation = CreateDocument()
            .Evaluate(new("evaluation/states"))
            .Set(CustomerName, ObservationValue.FromString("Acme"), "request/query/customer-name")
            .SetNull(Nullable)
            .Set(Optional, ObservationValue.Undefined)
            .Omit(Defaulted)
            .SetFailed(Count, "request/query/count-decode")
            .Build();

        var provided = Evidence(evaluation, CustomerName);
        Assert.Equal(RelationQueryParameterEvidenceState.Provided, provided.State);
        Assert.Equal(ObservationValue.FromString("Acme"), provided.Value);
        Assert.Equal("request/query/customer-name", provided.EvidenceReference);
        Assert.Equal(RelationQueryParameterEvidenceState.Null, Evidence(evaluation, Nullable).State);
        Assert.Equal(RelationQueryParameterEvidenceState.Missing, Evidence(evaluation, Optional).State);
        Assert.Equal(RelationQueryParameterEvidenceState.NotProvided, Evidence(evaluation, Defaulted).State);
        Assert.Equal(RelationQueryParameterEvidenceState.Failed, Evidence(evaluation, Count).State);
        Assert.Equal("request/query/count-decode", Evidence(evaluation, Count).EvidenceReference);
        Assert.Equal("input/parameter/customer%2Fname", provided.Input.Value);
    }

    [Fact]
    public void Set_RejectsLocallyIncompatibleConcreteAndNullValues()
    {
        var concrete = CreateDocument().Evaluate(new("evaluation/concrete-type"));
        var concreteException = Assert.Throws<ArgumentException>(() =>
            concrete.Set(Count, ObservationValue.FromString("not-an-integer")));
        Assert.Equal("value", concreteException.ParamName);

        var nullBuilder = CreateDocument().Evaluate(new("evaluation/nullability"));
        var nullException = Assert.Throws<ArgumentException>(() => nullBuilder.SetNull(Optional));
        Assert.Equal("parameter", nullException.ParamName);

        var evaluation = concrete
            .Set(Count, ObservationValue.FromInt64(12))
            .Build();
        Assert.Equal(RelationQueryParameterEvidenceState.Provided, Evidence(evaluation, Count).State);
    }

    [Fact]
    public void ParameterAuthoring_RejectsUnknownAndDuplicateAssignments()
    {
        var builder = CreateDocument()
            .Evaluate(new("evaluation/duplicate"))
            .Set(CustomerName, ObservationValue.FromString("Acme"));

        Assert.Throws<InvalidOperationException>(() =>
            builder.Set(CustomerName, ObservationValue.FromString("Other")));
        Assert.Throws<ArgumentException>(() =>
            builder.Set(new QueryParameterId("undeclared"), ObservationValue.FromString("value")));
        Assert.Throws<ArgumentException>(() => CreateDocument()
            .Evaluate(new("evaluation/invalid-provenance"))
            .Omit(Optional, " "));
    }

    [Fact]
    public void Select_ProducesDeterministicExplicitResultAndFieldDemand()
    {
        RelationQueryFieldReference customerName = new(
            LoadShape,
            FieldPath.FromField("CustomerName"));
        RelationQueryFieldReference id = new(
            LoadShape,
            FieldPath.FromField("Id"));

        var evaluation = CreateDocument()
            .Evaluate(new("evaluation/demand"))
            .Select(Summary)
            .Select(Rows, [customerName, id, customerName])
            .Build();

        Assert.Equal(RelationQueryCompilationDemandOrigin.Explicit, evaluation.DemandOrigin);
        Assert.Equal(RelationQueryCompilationDemandKind.QueryResults, evaluation.Demand.Kind);
        Assert.Collection(
            evaluation.Demand.QueryResults,
            rows =>
            {
                Assert.Equal(Rows, rows.Result);
                Assert.Equal(RelationQueryFieldSelectionKind.SelectedFields, rows.Selection);
                Assert.Equal([customerName, id], rows.Fields.OrderBy(static field => field.Path.ToString()));
                Assert.Equal(2, rows.Fields.Length);
            },
            summary =>
            {
                Assert.Equal(Summary, summary.Result);
                Assert.Equal(RelationQueryFieldSelectionKind.AllFields, summary.Selection);
                Assert.Empty(summary.Fields);
            });
    }

    [Fact]
    public void Select_RejectsUnknownEmptyAndDuplicateResults()
    {
        var builder = CreateDocument()
            .Evaluate(new("evaluation/invalid-demand"))
            .Select(Rows);

        Assert.Throws<InvalidOperationException>(() => builder.Select(Rows));
        Assert.Throws<ArgumentException>(() => builder.Select(new QueryResultId("undeclared")));
        Assert.Throws<ArgumentException>(() => CreateDocument()
            .Evaluate(new("evaluation/empty-fields"))
            .Select(Summary, []));
    }

    [Fact]
    public void Builder_RejectsDefaultEvaluationIdentity()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateDocument().Evaluate(default(RelationQueryEvaluationId)));

        Assert.Equal("evaluation", exception.ParamName);
    }

    [Fact]
    public void Build_VerifiesOptionalPlanReferenceAgainstEffectiveDemand()
    {
        var document = LoadCustomerRelationFixture.RepresentativeQueryDocument;
        var compilation = RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var reference = RelationQueryCompiledPlanReference.From(plan);

        var matching = document
            .Evaluate(new("evaluation/plan"), planReference: reference)
            .Build();
        Assert.Same(reference, matching.PlanReference);
        Assert.All(
            plan.RequirementGraph.Inputs.OfType<RelationQueryParameterInput>(),
            input => Assert.Contains(matching.Parameters, evidence => evidence.Input == input.Id));

        var mismatchedDemand = document
            .Evaluate(new("evaluation/plan-demand-mismatch"), planReference: reference)
            .Select(LoadCustomerRelationFixture.RowsResultId);
        Assert.Throws<InvalidOperationException>(() => mismatchedDemand.Build());
    }

    [Fact]
    public void PlanBoundBuild_PreservesOneAuthoritativeDeclaredParameterEvidenceSet()
    {
        var document = LoadCustomerRelationFixture.RepresentativeQueryDocument;
        var demand = RelationQueryCompilationDemand.ForQueryResults(
        [
            QueryResultDemand.AllFields(LoadCustomerRelationFixture.AggregationResultId)
        ]);
        var compilation = RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument,
            demand));
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var cursorInput = RelationQueryInputIds.ForParameter(LoadCustomerRelationFixture.CursorParameterId);
        Assert.DoesNotContain(plan.RequirementGraph.Inputs, input => input.Id == cursorInput);

        var evaluation = document
            .Evaluate(
                new RelationQueryEvaluationId("evaluation/aggregation-only"),
                planReference: RelationQueryCompiledPlanReference.From(plan))
            .Set(
                LoadCustomerRelationFixture.CursorParameterId,
                ObservationValue.FromString("unused-cursor"))
            .Select(LoadCustomerRelationFixture.AggregationResultId)
            .Build();

        var authoredCursor = Assert.Single(evaluation.Parameters, parameter => parameter.Input == cursorInput);
        Assert.Equal(RelationQueryParameterEvidenceState.Provided, authoredCursor.State);
        Assert.Equal(ObservationValue.FromString("unused-cursor"), authoredCursor.Value);

        var runtimeEvidence = new RelationQueryRuntimeEvidence(
            evaluation.Evaluation,
            plan,
            parameters:
            [
                .. evaluation.Parameters.Where(parameter =>
                    plan.RequirementGraph.Inputs.Any(input => input.Id == parameter.Input))
            ]);
        var analysis = RelationRequirementGapAnalyzer.Analyze(plan, runtimeEvidence);
        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.EvidenceConflict);
    }

    static RelationQueryDocument CreateDocument()
    {
        var source = new QueryNodeId("source");
        var query = new IRQueryDefinition(
            new QueryId("evaluation-tests"),
            new QueryName("Evaluation tests"),
            new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(source, new ValueBindingId("load"), LoadShape)
                ],
                parameters:
                [
                    new(CustomerName, new ScalarTypeRef(ScalarTypeKind.String)),
                    new(Count, new ScalarTypeRef(ScalarTypeKind.Int32)),
                    new(
                        Defaulted,
                        new ScalarTypeRef(ScalarTypeKind.String),
                        FieldPresence.Optional,
                        ObservationValue.FromString("fallback")),
                    new(
                        Nullable,
                        new ScalarTypeRef(ScalarTypeKind.String),
                        FieldPresence.Optional,
                        ObservationValue.Null),
                    new(Optional, new ScalarTypeRef(ScalarTypeKind.String), FieldPresence.Optional)
                ]),
            results:
            [
                new RowsQueryResultDefinition(Rows, source),
                new RowsQueryResultDefinition(Summary, source)
            ]);
        return RelationQueryDocument.FromDefinition(query);
    }

    static RelationQueryParameterEvidence Evidence(
        RelationQueryEvaluation evaluation,
        QueryParameterId parameter) =>
        Assert.Single(
            evaluation.Parameters,
            evidence => evidence.Input == RelationQueryInputIds.ForParameter(parameter));
}
