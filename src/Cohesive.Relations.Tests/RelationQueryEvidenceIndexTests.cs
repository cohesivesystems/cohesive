using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryEvidenceIndexTests
{
    [Fact]
    public void SourceAndTraversalEvidence_ReconstructSparseObservedBindingsAndExactProvenance()
    {
        var plan = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);
        var sourceInput = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>());
        var relationshipInput = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryRelationshipInput>());
        var loadIdInput = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadIdPath);
        var customerReferenceInput = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadCustomerIdPath);
        var customerNameInput = FieldInput(
            plan,
            LoadCustomerRelationFixture.CustomerBinding,
            LoadCustomerRelationFixture.CustomerNamePath);
        var load = new RelationQueryObservationOccurrence(
            new("occurrence/load-2"),
            LoadCustomerRelationFixture.LoadBinding,
            LoadCustomerRelationFixture.LoadShapeId,
            "load-2");
        var customer = new RelationQueryObservationOccurrence(
            new("occurrence/customer-1"),
            LoadCustomerRelationFixture.CustomerBinding,
            LoadCustomerRelationFixture.CustomerShapeId,
            "customer-1");
        var evidence = new RelationQueryRuntimeEvidence(
            new("tests/evidence-index"),
            plan,
            RelationQueryEvidenceCompleteness.Complete,
            sources:
            [
                new(
                    sourceInput.Id,
                    RelationQuerySourceEvidenceState.Provided,
                    [load])
            ],
            fields:
            [
                new(
                    loadIdInput.Id,
                    load.Id,
                    RelationQueryFieldEvidenceState.Value,
                    ObservationValue.FromString("load-2")),
                new(
                    customerReferenceInput.Id,
                    load.Id,
                    RelationQueryFieldEvidenceState.Missing),
                new(
                    customerNameInput.Id,
                    customer.Id,
                    RelationQueryFieldEvidenceState.Null)
            ],
            traversals:
            [
                new(
                    relationshipInput.Id,
                    load.Id,
                    RelationQueryTraversalEvidenceState.Completed,
                    [customer],
                    RelationQueryEvidenceCompleteness.Complete)
            ]);

        var index = new RelationQueryEvidenceIndex(plan, evidence);

        Assert.True(index.TryCreateSourceRows(sourceInput, out var rows));
        var row = Assert.Single(rows);
        Assert.Equal(load, row.Root);
        Assert.Equal(new[] { load }, row.Provenance);
        Assert.True(row.TryGetBinding(LoadCustomerRelationFixture.LoadBinding, out var loadBinding));
        Assert.Equal(RelationQueryRuntimeBindingKind.Observed, loadBinding.Kind);
        Assert.True(RelationQueryObjectValues.TryGet(
            loadBinding.Value,
            LoadCustomerRelationFixture.LoadIdPath,
            out var loadId));
        Assert.Equal(ObservationValue.FromString("load-2"), loadId);
        Assert.False(RelationQueryObjectValues.TryGet(
            loadBinding.Value,
            LoadCustomerRelationFixture.LoadCustomerIdPath,
            out _));

        Assert.True(index.TryGetTraversal(relationshipInput, load, out var traversal));
        Assert.Equal(new[] { customer }, traversal.Results);
        var customerBinding = index.CreateObservedBinding(customer);
        Assert.True(RelationQueryObjectValues.TryGet(
            customerBinding.Value,
            LoadCustomerRelationFixture.CustomerNamePath,
            out var customerName));
        Assert.Equal(ObservationValue.Null, customerName);

        var joined = row.WithBinding(LoadCustomerRelationFixture.CustomerBinding, customerBinding);
        Assert.Equal(
            [customer.Id, load.Id],
            joined.Provenance.Select(static occurrence => occurrence.Id));
        Assert.Equal(load, joined.Root);
    }

    [Theory]
    [InlineData(RelationQueryFieldEvidenceState.Value, (int)RelationQueryMaterializedValueState.Value, true, ObservationValueKind.String)]
    [InlineData(RelationQueryFieldEvidenceState.Null, (int)RelationQueryMaterializedValueState.Null, true, ObservationValueKind.Null)]
    [InlineData(RelationQueryFieldEvidenceState.Missing, (int)RelationQueryMaterializedValueState.Missing, true, ObservationValueKind.Undefined)]
    [InlineData(RelationQueryFieldEvidenceState.NotLoaded, (int)RelationQueryMaterializedValueState.NotLoaded, false, ObservationValueKind.Undefined)]
    [InlineData(RelationQueryFieldEvidenceState.Failed, (int)RelationQueryMaterializedValueState.Failed, false, ObservationValueKind.Undefined)]
    public void MaterializedFieldValue_PreservesAvailabilityState(
        RelationQueryFieldEvidenceState evidenceState,
        int expectedState,
        bool hasSemanticValue,
        ObservationValueKind expectedKind)
    {
        var evidence = new RelationQueryFieldEvidence(
            new("field/input"),
            new("occurrence/owner"),
            evidenceState,
            evidenceState == RelationQueryFieldEvidenceState.Value
                ? ObservationValue.FromString("value")
                : null);

        var materialized = RelationQueryMaterializedValue.FromField(evidence);

        Assert.Equal((RelationQueryMaterializedValueState)expectedState, materialized.State);
        Assert.Equal(hasSemanticValue, materialized.TryGetSemanticValue(out var value));
        if (hasSemanticValue)
            Assert.Equal(expectedKind, value.Kind);
    }

    [Fact]
    public void MaterializedFieldValue_DistinguishesOmittedEvidence()
    {
        var materialized = RelationQueryMaterializedValue.FromField(evidence: null);
        var defaultValue = default(RelationQueryMaterializedValue);

        Assert.Equal(RelationQueryMaterializedValueState.Omitted, materialized.State);
        Assert.False(materialized.TryGetSemanticValue(out _));
        Assert.Equal(RelationQueryMaterializedValueState.Omitted, defaultValue.State);
        Assert.False(defaultValue.TryGetSemanticValue(out _));
    }

    [Fact]
    public void EffectiveParameter_AppliesCanonicalDefaultOnlyAcrossKnownOmissionBoundary()
    {
        var plan = Compile(CreateOptionalParameterQuery());
        var parameterInput = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryParameterInput>());
        var explicitOmission = new RelationQueryRuntimeEvidence(
            new("tests/parameter-explicit-omission"),
            plan,
            RelationQueryEvidenceCompleteness.Partial,
            parameters:
            [
                new(
                    parameterInput.Id,
                    RelationQueryParameterEvidenceState.NotProvided)
            ]);
        var partialOmission = new RelationQueryRuntimeEvidence(
            new("tests/parameter-partial-omission"),
            plan,
            RelationQueryEvidenceCompleteness.Partial);
        var completeOmission = new RelationQueryRuntimeEvidence(
            new("tests/parameter-complete-omission"),
            plan,
            RelationQueryEvidenceCompleteness.Complete);

        var explicitlyDefaulted = new RelationQueryEvidenceIndex(plan, explicitOmission)
            .ResolveEffectiveParameter("status");
        var unknown = new RelationQueryEvidenceIndex(plan, partialOmission)
            .ResolveEffectiveParameter("status");
        var completelyDefaulted = new RelationQueryEvidenceIndex(plan, completeOmission)
            .ResolveEffectiveParameter("status");

        Assert.Equal(RelationQueryMaterializedValueState.Defaulted, explicitlyDefaulted.State);
        Assert.Equal(ObservationValue.FromString("active"), explicitlyDefaulted.Value);
        Assert.Equal(RelationQueryMaterializedValueState.Omitted, unknown.State);
        Assert.Equal(RelationQueryMaterializedValueState.Defaulted, completelyDefaulted.State);
        Assert.Equal(ObservationValue.FromString("active"), completelyDefaulted.Value);
    }

    [Fact]
    public void RuntimeRow_DistinguishesAbsentFromPresentNullAndUnionsProvenance()
    {
        var first = new RelationQueryObservationOccurrence(
            new("occurrence/b"),
            new("first"),
            LoadCustomerRelationFixture.LoadShapeId,
            "load-b");
        var second = new RelationQueryObservationOccurrence(
            new("occurrence/a"),
            new("second"),
            LoadCustomerRelationFixture.CustomerShapeId,
            "customer-a");
        var presentNull = RelationQueryRuntimeBinding.FromObservation(first, ObservationValue.Null);
        var absent = RelationQueryRuntimeBinding.CreateAbsent(LoadCustomerRelationFixture.CustomerShapeId);

        var row = RelationQueryRuntimeRow
            .FromBinding(first.Binding, presentNull, first)
            .WithBinding(second.Binding, absent)
            .WithAdditionalProvenance([second]);

        Assert.True(row.ExpressionBindings[first.Binding].IsPresent);
        Assert.Equal(ObservationValueKind.Null, row.ExpressionBindings[first.Binding].Value.Kind);
        Assert.False(row.ExpressionBindings[second.Binding].IsPresent);
        Assert.Equal([second.Id, first.Id], row.Provenance.Select(static occurrence => occurrence.Id));
        Assert.Equal(first, row.Root);
    }

    static RelationQueryDocument CreateOptionalParameterQuery()
    {
        var source = new QueryNodeId("loads");
        var filtered = new QueryNodeId("filtered-loads");
        var definition = new Cohesive.Relations.IR.QueryDefinition(
            new("optional-parameter-query"),
            new("OptionalParameterQuery"),
            new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(
                        source,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadShapeId),
                    new FilterQueryNode(
                        filtered,
                        source,
                        Expr.Eq(
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadStatusPath),
                            Expr.Param("status")))
                ],
                parameters:
                [
                    new QueryParameterDefinition(
                        new("status"),
                        new ScalarTypeRef(ScalarTypeKind.String),
                        FieldPresence.Optional,
                        ObservationValue.FromString("active"))
                ]),
            results:
            [
                new RowsQueryResultDefinition(new("rows"), filtered)
            ]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static CompiledRelationQueryPlan Compile(RelationQueryDocument document)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static RelationQueryFieldInput FieldInput(
        CompiledRelationQueryPlan plan,
        ValueBindingId binding,
        FieldPath path) =>
        Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Binding == binding && input.Field.Path == path);
}
