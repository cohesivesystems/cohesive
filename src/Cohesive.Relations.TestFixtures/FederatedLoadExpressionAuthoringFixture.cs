using System.Text.Json.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.TestFixtures;

/// <summary>Typed C# authoring projection over the canonical federated Load relation fixture.</summary>
static class FederatedLoadExpressionAuthoringFixture
{
    public static Scenario Create(
        QueryInputRequirement customerRequirement = QueryInputRequirement.Required,
        QueryInputRequirement equipmentRequirement = QueryInputRequirement.Optional)
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<FederatedLoad>(FederatedLoadRelationFixture.LoadShapeId);
        var customerShape = author.Clr.Shape<FederatedCustomer>(FederatedLoadRelationFixture.CustomerShapeId);
        var equipmentShape = author.Clr.Shape<FederatedEquipment>(FederatedLoadRelationFixture.EquipmentShapeId);
        var outputShape = author.Clr.Shape<FederatedLoadSearchRow>(FederatedLoadRelationFixture.LoadSearchShapeId);
        var loadCustomer = author.Relationship<FederatedLoad, FederatedCustomer>(
            load => load.CustomerId,
            FederatedLoadRelationFixture.LoadCustomerRelationshipId);
        var loadEquipment = author.Relationship<FederatedLoad, FederatedEquipment>(
            load => load.EquipmentId,
            FederatedLoadRelationFixture.LoadEquipmentRelationshipId);
        var loads = author.Source(loadShape, "conformance/federated/source/loads");
        var customers = author.Traverse(
            loads,
            loadCustomer,
            requirement: customerRequirement,
            sourceReference: "conformance/federated/traverse/customer");
        var equipment = author.Traverse(
            customers,
            loads.Binding,
            loadEquipment,
            requirement: equipmentRequirement,
            sourceReference: "conformance/federated/traverse/equipment");
        var projected = author.Project(
            equipment,
            (FederatedLoad load, FederatedCustomer customer, FederatedEquipment unit) =>
                new FederatedLoadSearchRow
                {
                    Id = load.Id,
                    CustomerName = customer.Name,
                    EquipmentNumber = unit.Number
                },
            loads.Binding,
            customers.Binding,
            sourceReference: "conformance/federated/project/load-search");
        var relation = projected.BuildRelation(
            document => document.Id,
            id: FederatedLoadRelationFixture.LoadSearchRelationId,
            name: FederatedLoadRelationFixture.LoadSearchRelationName,
            sourceReference: "conformance/federated/relation/load-search");

        return new(
            author,
            loadShape,
            customerShape,
            equipmentShape,
            outputShape,
            relation,
            author.CreateRelationshipCatalogDocument());
    }

    public static RelationQueryAuthoringResult<RelationDefinition> CreateStructuralEquivalent(
        QueryInputRequirement customerRequirement = QueryInputRequirement.Required,
        QueryInputRequirement equipmentRequirement = QueryInputRequirement.Optional)
    {
        var sourceNode = RelationQueryAuthoringIdentityConvention.CreateNodeId(
            RelationQueryWireNames.SourceNode,
            ordinal: 1);
        var sourceBinding = RelationQueryAuthoringIdentityConvention.CreateBindingId(sourceNode, "source");
        var customerTraversal = RelationQueryAuthoringIdentityConvention.CreateNodeId(
            RelationQueryWireNames.TraverseRelationshipNode,
            ordinal: 1);
        var customerBinding = RelationQueryAuthoringIdentityConvention.CreateBindingId(
            customerTraversal,
            "result");
        var equipmentTraversal = RelationQueryAuthoringIdentityConvention.CreateNodeId(
            RelationQueryWireNames.TraverseRelationshipNode,
            ordinal: 2);
        var equipmentBinding = RelationQueryAuthoringIdentityConvention.CreateBindingId(
            equipmentTraversal,
            "result");
        var projectionNode = RelationQueryAuthoringIdentityConvention.CreateNodeId(
            RelationQueryWireNames.ProjectNode,
            ordinal: 1);
        var projectionBinding = RelationQueryAuthoringIdentityConvention.CreateBindingId(projectionNode, "result");

        var author = RelationQuery.Structural();
        var loads = author.Source(
            FederatedLoadRelationFixture.LoadShapeId,
            sourceNode,
            sourceBinding);
        var customers = author.Traverse(
            loads.Node,
            loads.Binding,
            FederatedLoadRelationFixture.LoadCustomerRelationshipId,
            RelationshipTraversalDirection.Forward,
            JoinKind.Left,
            customerRequirement,
            customerTraversal,
            customerBinding);
        var equipment = author.Traverse(
            customers.Node,
            loads.Binding,
            FederatedLoadRelationFixture.LoadEquipmentRelationshipId,
            RelationshipTraversalDirection.Forward,
            JoinKind.Left,
            equipmentRequirement,
            equipmentTraversal,
            equipmentBinding);
        var projected = author.Project(
            equipment.Node,
            FederatedLoadRelationFixture.LoadSearchShapeId,
            [
                new(
                    FederatedLoadRelationFixture.SearchIdPath,
                    loads.Binding.Field(FederatedLoadRelationFixture.LoadIdPath),
                    RelationQueryAuthoringIdentityConvention.CreateAssignmentId(
                        projectionNode,
                        "projection",
                        ordinal: 1)),
                new(
                    FederatedLoadRelationFixture.SearchCustomerNamePath,
                    customers.Binding.Field(FederatedLoadRelationFixture.CustomerNamePath),
                    RelationQueryAuthoringIdentityConvention.CreateAssignmentId(
                        projectionNode,
                        "projection",
                        ordinal: 2)),
                new(
                    FederatedLoadRelationFixture.SearchEquipmentNumberPath,
                    equipment.Binding.Field(FederatedLoadRelationFixture.EquipmentNumberPath),
                    RelationQueryAuthoringIdentityConvention.CreateAssignmentId(
                        projectionNode,
                        "projection",
                        ordinal: 3))
            ],
            projectionNode,
            projectionBinding);

        return author.BuildRelation(
            FederatedLoadRelationFixture.LoadSearchRelationId,
            FederatedLoadRelationFixture.LoadSearchRelationName,
            loads.Binding,
            projected.Node,
            FederatedLoadRelationFixture.LoadSearchShapeId,
            RelationOutputMode.OnePerRoot,
            projected.Binding.Field(FederatedLoadRelationFixture.SearchIdPath));
    }

    internal sealed record Scenario(
        RelationQueryExpressionAuthoring Author,
        RelationQueryClrShape<FederatedLoad> LoadShape,
        RelationQueryClrShape<FederatedCustomer> CustomerShape,
        RelationQueryClrShape<FederatedEquipment> EquipmentShape,
        RelationQueryClrShape<FederatedLoadSearchRow> OutputShape,
        RelationQueryAuthoringResult<RelationDefinition> Relation,
        RelationshipCatalogDocument RelationshipCatalog);
}

sealed record FederatedLoad
{
    [JsonPropertyName(FederatedLoadRelationFixture.LoadIdFieldName)]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName(FederatedLoadRelationFixture.LoadCustomerIdFieldName)]
    public string CustomerId { get; init; } = string.Empty;

    [JsonPropertyName(FederatedLoadRelationFixture.LoadEquipmentIdFieldName)]
    public string EquipmentId { get; init; } = string.Empty;

    [JsonPropertyName(FederatedLoadRelationFixture.LoadStatusFieldName)]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName(FederatedLoadRelationFixture.LoadAmountFieldName)]
    public decimal Amount { get; init; }
}

sealed record FederatedCustomer
{
    [JsonPropertyName(FederatedLoadRelationFixture.CustomerIdFieldName)]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName(FederatedLoadRelationFixture.CustomerNameFieldName)]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName(FederatedLoadRelationFixture.CustomerTypeFieldName)]
    public string Type { get; init; } = string.Empty;
}

sealed record FederatedEquipment
{
    [JsonPropertyName(FederatedLoadRelationFixture.EquipmentIdFieldName)]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName(FederatedLoadRelationFixture.EquipmentNumberFieldName)]
    public string Number { get; init; } = string.Empty;

    [JsonPropertyName(FederatedLoadRelationFixture.EquipmentTypeFieldName)]
    public string Type { get; init; } = string.Empty;
}

sealed record FederatedLoadSearchRow
{
    [JsonPropertyName(FederatedLoadRelationFixture.SearchIdFieldName)]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName(FederatedLoadRelationFixture.SearchCustomerNameFieldName)]
    public string? CustomerName { get; init; }

    [JsonPropertyName(FederatedLoadRelationFixture.SearchEquipmentNumberFieldName)]
    public string? EquipmentNumber { get; init; }
}
