using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;

namespace Cohesive.Relations.Tests;

/// <summary>Tests target-neutral canonical query invocation authoring.</summary>
public sealed class RelationQueryInvocationAuthoringTests
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
        var evaluation = new RelationQueryEvaluationId("evaluation/42");

        var invocation = document.Invoke(evaluation).Build();

        Assert.Same(document, invocation.Document);
        Assert.Same(document.Definition, invocation.Query);
        Assert.Equal(evaluation, invocation.Evaluation);
        Assert.Same(RelationQueryCompilationDemand.AllDeclaredOutputs, invocation.Demand);
        Assert.Equal(RelationQueryCompilationDemandOrigin.Convention, invocation.DemandOrigin);
        Assert.Null(invocation.PlanReference);
        Assert.All(
            invocation.Parameters,
            static parameter => Assert.Equal(
                RelationQueryParameterEvidenceState.NotProvided,
                parameter.State));

        var defaultEvidence = Evidence(invocation, Defaulted);
        var defaultDeclaration = Assert.Single(
            invocation.Query.Body.Parameters,
            parameter => parameter.Id == Defaulted);
        Assert.Equal(RelationQueryParameterEvidenceState.NotProvided, defaultEvidence.State);
        Assert.Equal(QueryParameterDefaultKind.Value, defaultDeclaration.DefaultKind);
        Assert.Equal(ObservationValue.FromString("fallback"), defaultDeclaration.DefaultValue);
    }

    [Fact]
    public void Set_OmitNullAndMissing_PreserveDistinctEvidenceStates()
    {
        var invocation = CreateDocument()
            .Invoke(new("evaluation/states"))
            .Set(CustomerName, ObservationValue.FromString("Acme"))
            .SetNull(Nullable)
            .Set(Optional, ObservationValue.Undefined)
            .Omit(Defaulted)
            .Build();

        var provided = Evidence(invocation, CustomerName);
        Assert.Equal(RelationQueryParameterEvidenceState.Provided, provided.State);
        Assert.Equal(ObservationValue.FromString("Acme"), provided.Value);
        Assert.Equal(RelationQueryParameterEvidenceState.Null, Evidence(invocation, Nullable).State);
        Assert.Equal(RelationQueryParameterEvidenceState.Missing, Evidence(invocation, Optional).State);
        Assert.Equal(RelationQueryParameterEvidenceState.NotProvided, Evidence(invocation, Defaulted).State);
        Assert.Equal(RelationQueryParameterEvidenceState.NotProvided, Evidence(invocation, Count).State);
        Assert.Equal("input/parameter/customer%2Fname", provided.Input.Value);
    }

    [Fact]
    public void Set_RejectsLocallyIncompatibleConcreteAndNullValues()
    {
        var concrete = CreateDocument().Invoke(new("evaluation/concrete-type"));
        var concreteException = Assert.Throws<ArgumentException>(() =>
            concrete.Set(Count, ObservationValue.FromString("not-an-integer")));
        Assert.Equal("value", concreteException.ParamName);

        var nullBuilder = CreateDocument().Invoke(new("evaluation/nullability"));
        var nullException = Assert.Throws<ArgumentException>(() => nullBuilder.SetNull(Optional));
        Assert.Equal("parameter", nullException.ParamName);

        var invocation = concrete
            .Set(Count, ObservationValue.FromInt64(12))
            .Build();
        Assert.Equal(RelationQueryParameterEvidenceState.Provided, Evidence(invocation, Count).State);
    }

    [Fact]
    public void ParameterAuthoring_RejectsUnknownAndDuplicateAssignments()
    {
        var builder = CreateDocument()
            .Invoke(new("evaluation/duplicate"))
            .Set(CustomerName, ObservationValue.FromString("Acme"));

        Assert.Throws<InvalidOperationException>(() =>
            builder.Set(CustomerName, ObservationValue.FromString("Other")));
        Assert.Throws<ArgumentException>(() =>
            builder.Set(new QueryParameterId("undeclared"), ObservationValue.FromString("value")));
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

        var invocation = CreateDocument()
            .Invoke(new("evaluation/demand"))
            .Select(Summary)
            .Select(Rows, [customerName, id, customerName])
            .Build();

        Assert.Equal(RelationQueryCompilationDemandOrigin.Explicit, invocation.DemandOrigin);
        Assert.Equal(RelationQueryCompilationDemandKind.QueryResults, invocation.Demand.Kind);
        Assert.Collection(
            invocation.Demand.QueryResults,
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
            .Invoke(new("evaluation/invalid-demand"))
            .Select(Rows);

        Assert.Throws<InvalidOperationException>(() => builder.Select(Rows));
        Assert.Throws<ArgumentException>(() => builder.Select(new QueryResultId("undeclared")));
        Assert.Throws<ArgumentException>(() => CreateDocument()
            .Invoke(new("evaluation/empty-fields"))
            .Select(Summary, []));
    }

    [Fact]
    public void Builder_RejectsDefaultEvaluationIdentity()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateDocument().Invoke(default(RelationQueryEvaluationId)));

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
            .Invoke(new("evaluation/plan"), reference)
            .Build();
        Assert.Same(reference, matching.PlanReference);
        Assert.All(
            plan.RequirementGraph.Inputs.OfType<RelationQueryParameterInput>(),
            input => Assert.Contains(matching.Parameters, evidence => evidence.Input == input.Id));

        var mismatchedDemand = document
            .Invoke(new("evaluation/plan-demand-mismatch"), reference)
            .Select(LoadCustomerRelationFixture.RowsResultId);
        Assert.Throws<InvalidOperationException>(() => mismatchedDemand.Build());
    }

    [Fact]
    public void PlanBoundBuild_PreservesUnusedAssignmentsOutsideRuntimeReadyEvidence()
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

        var invocation = document
            .Invoke(
                new RelationQueryEvaluationId("evaluation/aggregation-only"),
                RelationQueryCompiledPlanReference.From(plan))
            .Set(
                LoadCustomerRelationFixture.CursorParameterId,
                ObservationValue.FromString("unused-cursor"))
            .Select(LoadCustomerRelationFixture.AggregationResultId)
            .Build();

        var authoredCursor = Assert.Single(
            invocation.DeclaredParameters,
            parameter => parameter.Input == cursorInput);
        Assert.Equal(RelationQueryParameterEvidenceState.Provided, authoredCursor.State);
        Assert.Equal(ObservationValue.FromString("unused-cursor"), authoredCursor.Value);
        Assert.DoesNotContain(invocation.Parameters, parameter => parameter.Input == cursorInput);
        Assert.All(
            invocation.Parameters,
            parameter => Assert.Contains(parameter.Input, plan.RequirementGraph.Inputs.Select(static input => input.Id)));

        var runtimeEvidence = new RelationQueryRuntimeEvidence(
            invocation.Evaluation,
            plan,
            parameters: invocation.Parameters);
        var analysis = RelationRequirementGapAnalyzer.Analyze(plan, runtimeEvidence);
        Assert.DoesNotContain(
            analysis.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.EvidenceConflict);
    }

    static RelationQueryDocument CreateDocument()
    {
        var source = new QueryNodeId("source");
        var query = new IRQueryDefinition(
            new QueryId("invocation-tests"),
            new QueryName("Invocation tests"),
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
        RelationQueryInvocation invocation,
        QueryParameterId parameter) =>
        Assert.Single(
            invocation.Parameters,
            evidence => evidence.Input == RelationQueryInputIds.ForParameter(parameter));
}
