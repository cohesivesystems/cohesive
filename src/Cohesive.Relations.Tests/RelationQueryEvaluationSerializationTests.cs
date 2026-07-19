using Cohesive.Relations.Compilation;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryEvaluationSerializationTests
{
    [Fact]
    public void QueryEvaluation_RoundTripsDeterministicallyWithDemandAndParameterProvenance()
    {
        RelationQueryFieldReference[] fields =
        [
            new(LoadCustomerRelationFixture.LoadSearchShapeId, LoadCustomerRelationFixture.SearchIdPath),
            new(
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchCustomerNamePath)
        ];
        var demand = RelationQueryCompilationDemand.ForQueryResults(
        [
            QueryResultDemand.SelectedFields(LoadCustomerRelationFixture.RowsResultId, fields)
        ]);
        var compilation = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.RepresentativeQueryDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument,
            demand));
        Assert.True(
            compilation.IsSuccessful,
            string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        var planReference = RelationQueryCompiledPlanReference.From(
            Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan));
        var evaluation = LoadCustomerRelationFixture.RepresentativeQueryDocument
            .Evaluate(
                new("serialization/query"),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument,
                planReference)
            .Set(
                LoadCustomerRelationFixture.CursorParameterId,
                ObservationValue.FromString("load-42"),
                "http/query/cursor")
            .Select(LoadCustomerRelationFixture.RowsResultId, fields)
            .Build();

        var json = RelationQueryEvaluationJsonSerializer.Serialize(evaluation);
        var restored = RelationQueryEvaluationJsonSerializer.Deserialize(json);

        Assert.Equal(evaluation.Fingerprint, restored.Fingerprint);
        Assert.True(evaluation.HasSameSemantics(restored));
        Assert.True(planReference.Inputs.SequenceEqual(restored.PlanReference!.Inputs));
        Assert.Equal(RelationQueryCompilationDemandOrigin.Explicit, restored.DemandOrigin);
        Assert.Equal("http/query/cursor", Assert.Single(
            restored.Parameters,
            parameter => parameter.Input == RelationQueryInputIds.ForParameter(
                LoadCustomerRelationFixture.CursorParameterId)).EvidenceReference);
        Assert.Equal(json, RelationQueryEvaluationJsonSerializer.Serialize(restored));
    }

    [Fact]
    public void SuppliedRootRelationEvaluation_RoundTripsIdentityValuesAndProvenance()
    {
        var evaluation = Relation("serialization/relation", "customer-1", "change-feed/17");

        var restored = RelationQueryEvaluationJsonSerializer.Deserialize(
            RelationQueryEvaluationJsonSerializer.Serialize(evaluation));

        Assert.True(evaluation.HasSameSemantics(restored));
        var roots = Assert.IsType<RelationQuerySuppliedRootSet>(restored.SuppliedRoots);
        Assert.Equal("change-feed/17", roots.EvidenceReference);
        var root = Assert.Single(roots.Observations);
        Assert.Equal("load-1", root.Id);
        Assert.Equal(
            ObservationValue.FromString("customer-1"),
            root.Fields[LoadCustomerRelationFixture.LoadCustomerIdFieldName]);
    }

    [Fact]
    public void FingerprintChangesWithDemandInputsRootsAndProvenance()
    {
        var first = Query("serialization/fingerprint", "load-1", "request/1", rows: true);
        var changedValue = Query("serialization/fingerprint", "load-2", "request/1", rows: true);
        var changedProvenance = Query("serialization/fingerprint", "load-1", "request/2", rows: true);
        var changedDemand = Query("serialization/fingerprint", "load-1", "request/1", rows: false);
        var firstRoot = Relation("serialization/root-fingerprint", "customer-1", "change-feed/1");
        var changedRoot = Relation("serialization/root-fingerprint", "customer-2", "change-feed/1");

        Assert.NotEqual(first.Fingerprint, changedValue.Fingerprint);
        Assert.NotEqual(first.Fingerprint, changedProvenance.Fingerprint);
        Assert.NotEqual(first.Fingerprint, changedDemand.Fingerprint);
        Assert.NotEqual(firstRoot.Fingerprint, changedRoot.Fingerprint);
    }

    [Fact]
    public void DeserializeRejectsAStaleEvaluationFingerprint()
    {
        var json = RelationQueryEvaluationJsonSerializer.Serialize(
            Query("serialization/stale", "load-1", "request/1", rows: true));
        var stale = json.Replace("load-1", "load-2", StringComparison.Ordinal);

        var exception = Assert.Throws<System.Text.Json.JsonException>(() =>
            RelationQueryEvaluationJsonSerializer.Deserialize(stale));

        Assert.Contains("fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    static RelationQueryEvaluation Query(
        string evaluation,
        string cursor,
        string evidenceReference,
        bool rows)
    {
        var builder = LoadCustomerRelationFixture.RepresentativeQueryDocument
            .Evaluate(
                new(evaluation),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Set(
                LoadCustomerRelationFixture.CursorParameterId,
                ObservationValue.FromString(cursor),
                evidenceReference);
        return (rows
                ? builder.Select(LoadCustomerRelationFixture.RowsResultId)
                : builder.Select(LoadCustomerRelationFixture.AggregationResultId))
            .Build();
    }

    static RelationQueryEvaluation Relation(
        string evaluation,
        string customerId,
        string evidenceReference) =>
        LoadCustomerRelationFixture.BaselineRelationDocument
            .Evaluate(
                new(evaluation),
                LoadCustomerRelationFixture.ShapeGraphDocuments,
                LoadCustomerRelationFixture.RelationshipCatalogDocument)
            .Supply(
            [
                new Observation(
                    LoadCustomerRelationFixture.LoadShapeLocalId,
                    "load-1",
                    new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                    {
                        [LoadCustomerRelationFixture.LoadIdFieldName] = ObservationValue.FromString("load-1"),
                        [LoadCustomerRelationFixture.LoadCustomerIdFieldName] = ObservationValue.FromString(customerId)
                    })
            ],
            evidenceReference: evidenceReference)
            .Build();
}
