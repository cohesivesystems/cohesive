using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

/// <summary>Executable examples for canonical structural relation/query authoring.</summary>
public sealed class RelationQueryStructuralAuthoringExampleTests
{
    static readonly GraphId DomainGraph = new("example/domain/v1");
    static readonly GraphId SearchGraph = new("example/search/v1");
    static readonly QualifiedShapeId LoadShape = new(DomainGraph, new("Load"));
    static readonly QualifiedShapeId CustomerShape = new(DomainGraph, new("Customer"));
    static readonly QualifiedShapeId EquipmentShape = new(DomainGraph, new("Equipment"));
    static readonly QualifiedShapeId LoadSearchShape = new(SearchGraph, new("LoadSearchDto"));
    static readonly RelationshipId LoadCustomer = new("load/customer");
    static readonly RelationshipId LoadEquipment = new("load/equipment");

    [Fact]
    public void LoadCustomerEquipment_AuthorsDtoRelationQueryAndEvaluationFromOneStructuralCore()
    {
        var loadCustomer = Relationship
            .From(LoadShape)
            .Reference(FieldPath.FromField("CustomerId"))
            .To(CustomerShape, LoadCustomer);
        var loadEquipment = Relationship
            .From(LoadShape)
            .Reference(FieldPath.FromField("EquipmentId"))
            .To(EquipmentShape, LoadEquipment);
        var author = RelationQuery.Structural();
        var loads = author.Source(
            LoadShape,
            nodeId: new("loads"),
            bindingId: new("load"));
        var customers = author.Traverse(
            loads.Node,
            loads.Binding,
            loadCustomer.Id,
            joinKind: JoinKind.Left,
            requirement: QueryInputRequirement.Required,
            nodeId: new("load-customer"),
            resultBindingId: new("customer"));
        var equipment = author.Traverse(
            customers.Node,
            loads.Binding,
            loadEquipment.Id,
            joinKind: JoinKind.Left,
            requirement: QueryInputRequirement.Required,
            nodeId: new("load-equipment"),
            resultBindingId: new("equipment"));
        var searchDocuments = author.Project(
            equipment.Node,
            LoadSearchShape,
            assignments:
            [
                new(
                    FieldPath.FromField("Id"),
                    loads.Binding.Field("Id"),
                    id: new("load-search/id")),
                new(
                    FieldPath.FromField("Status"),
                    loads.Binding.Field("Status"),
                    id: new("load-search/status")),
                new(
                    FieldPath.FromField("CustomerName"),
                    customers.Binding.Field("Name"),
                    id: new("load-search/customer-name")),
                new(
                    FieldPath.FromField("EquipmentNumber"),
                    equipment.Binding.Field("Number"),
                    id: new("load-search/equipment-number"))
            ],
            nodeId: new("load-search-documents"),
            resultBindingId: new("load-search"));

        var relation = author.BuildRelation(
            new("load-search-document"),
            new("LoadSearchDocument"),
            loads.Binding,
            searchDocuments.Node,
            LoadSearchShape,
            RelationOutputMode.OnePerRoot,
            key: searchDocuments.Binding.Field("Id"));

        var status = author.Parameter(
            new ScalarTypeRef(ScalarTypeKind.String),
            id: new("status"));
        var filtered = author.Filter(
            searchDocuments.Node,
            Expr.Eq(searchDocuments.Binding.Field("Status"), status.Expression),
            nodeId: new("loads-by-status"));
        var rows = author.Rows(filtered, id: new("rows"));
        var query = author.BuildQuery(
            new("loads-by-status"),
            new("LoadsByStatus"),
            [rows]);

        Assert.True(relation.Validation.IsValid);
        Assert.True(query.Validation.IsValid);
        Assert.Empty(relation.Definition.Body.Parameters);
        Assert.Equal([status.Id], query.Definition.Body.Parameters.Select(static parameter => parameter.Id));
        Assert.Equal(
            [loadCustomer.Id, loadEquipment.Id],
            query.Definition.Body.Nodes
                .OfType<TraverseRelationshipQueryNode>()
                .Select(static traversal => traversal.Relationship));
        Assert.Equal(CustomerShape, loadCustomer.TargetShape);
        Assert.Equal(EquipmentShape, loadEquipment.TargetShape);
        Assert.Equal(
            ["Id", "Status", "CustomerName", "EquipmentNumber"],
            Assert.Single(query.Definition.Body.Nodes.OfType<ProjectQueryNode>())
                .Assignments
                .Select(static assignment => assignment.Target.ToString()));

        var evaluation = query.CreateDocument()
            .Evaluate(new RelationQueryEvaluationId("example/evaluation/1"))
            .Set(status.Id, ObservationValue.FromString("InTransit"))
            .Select(rows.Id)
            .Build();

        Assert.Equal(RelationQueryCompilationDemandKind.QueryResults, evaluation.Demand.Kind);
        var parameter = Assert.Single(evaluation.Parameters);
        Assert.Equal(RelationQueryParameterEvidenceState.Provided, parameter.State);
        Assert.Equal(ObservationValue.FromString("InTransit"), parameter.Value);
    }
}
