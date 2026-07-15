using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Canonical three-placement Load, Customer, and Equipment semantics used by federated physical-planning tests.
/// </summary>
static class FederatedLoadRelationFixture
{
    public const string LoadIdFieldName = "Id";
    public const string LoadCustomerIdFieldName = "CustomerId";
    public const string LoadEquipmentIdFieldName = "EquipmentId";
    public const string LoadStatusFieldName = "Status";
    public const string LoadAmountFieldName = "Amount";
    public const string CustomerIdFieldName = "Id";
    public const string CustomerNameFieldName = "Name";
    public const string CustomerTypeFieldName = "Type";
    public const string EquipmentIdFieldName = "Id";
    public const string EquipmentNumberFieldName = "Number";
    public const string EquipmentTypeFieldName = "Type";
    public const string SearchIdFieldName = "Id";
    public const string SearchCustomerNameFieldName = "CustomerName";
    public const string SearchEquipmentNumberFieldName = "EquipmentNumber";
    public const string AggregateLoadCountFieldName = "LoadCount";

    public static readonly GraphId DomainGraphId = new("federated-domain/v1");
    public static readonly GraphId DtoGraphId = new("federated-dto/v1");

    public static readonly EntityTypeName LoadEntityType = new("FederatedLoad");
    public static readonly EntityTypeName CustomerEntityType = new("FederatedCustomer");
    public static readonly EntityTypeName EquipmentEntityType = new("FederatedEquipment");

    public static readonly ShapeId LoadShapeLocalId = new("Load");
    public static readonly ShapeId CustomerShapeLocalId = new("Customer");
    public static readonly ShapeId EquipmentShapeLocalId = new("Equipment");
    public static readonly ShapeId LoadSearchShapeLocalId = new("LoadSearchDto");
    public static readonly ShapeId LoadAggregateShapeLocalId = new("LoadAggregate");

    public static readonly QualifiedShapeId LoadShapeId = new(DomainGraphId, LoadShapeLocalId);
    public static readonly QualifiedShapeId CustomerShapeId = new(DomainGraphId, CustomerShapeLocalId);
    public static readonly QualifiedShapeId EquipmentShapeId = new(DomainGraphId, EquipmentShapeLocalId);
    public static readonly QualifiedShapeId LoadSearchShapeId = new(DtoGraphId, LoadSearchShapeLocalId);
    public static readonly QualifiedShapeId LoadAggregateShapeId = new(DtoGraphId, LoadAggregateShapeLocalId);

    public static readonly FieldPath LoadIdPath = FieldPath.FromField(LoadIdFieldName);
    public static readonly FieldPath LoadCustomerIdPath = FieldPath.FromField(LoadCustomerIdFieldName);
    public static readonly FieldPath LoadEquipmentIdPath = FieldPath.FromField(LoadEquipmentIdFieldName);
    public static readonly FieldPath LoadStatusPath = FieldPath.FromField(LoadStatusFieldName);
    public static readonly FieldPath LoadAmountPath = FieldPath.FromField(LoadAmountFieldName);
    public static readonly FieldPath CustomerIdPath = FieldPath.FromField(CustomerIdFieldName);
    public static readonly FieldPath CustomerNamePath = FieldPath.FromField(CustomerNameFieldName);
    public static readonly FieldPath CustomerTypePath = FieldPath.FromField(CustomerTypeFieldName);
    public static readonly FieldPath EquipmentIdPath = FieldPath.FromField(EquipmentIdFieldName);
    public static readonly FieldPath EquipmentNumberPath = FieldPath.FromField(EquipmentNumberFieldName);
    public static readonly FieldPath EquipmentTypePath = FieldPath.FromField(EquipmentTypeFieldName);
    public static readonly FieldPath SearchIdPath = FieldPath.FromField(SearchIdFieldName);
    public static readonly FieldPath SearchCustomerNamePath = FieldPath.FromField(SearchCustomerNameFieldName);
    public static readonly FieldPath SearchEquipmentNumberPath = FieldPath.FromField(SearchEquipmentNumberFieldName);
    public static readonly FieldPath AggregateLoadCountPath = FieldPath.FromField(AggregateLoadCountFieldName);

    public static readonly RelationshipId LoadCustomerRelationshipId = new("FederatedLoad.Customer");
    public static readonly RelationshipId LoadEquipmentRelationshipId = new("FederatedLoad.Equipment");

    public static readonly QueryId LoadSearchQueryId = new("federated-load-search-query");
    public static readonly QueryName LoadSearchQueryName = new("FederatedLoadSearchQuery");
    public static readonly RelationId LoadSearchRelationId = new("federated-load-search");
    public static readonly RelationName LoadSearchRelationName = new("FederatedLoadSearch");

    public static readonly ValueBindingId LoadBinding = new("load");
    public static readonly ValueBindingId CustomerBinding = new("customer");
    public static readonly ValueBindingId EquipmentBinding = new("equipment");
    public static readonly ValueBindingId SearchBinding = new("loadSearch");
    public static readonly ValueBindingId AggregateBinding = new("loadAggregate");

    public static readonly QueryNodeId LoadSourceNodeId = new("loads");
    public static readonly QueryNodeId CustomerTraversalNodeId = new("load-customer");
    public static readonly QueryNodeId EquipmentTraversalNodeId = new("load-equipment");
    public static readonly QueryNodeId ProjectionNodeId = new("project-load-search");
    public static readonly QueryNodeId AggregateNodeId = new("aggregate-loads");

    public static readonly QueryAssignmentId SearchIdAssignmentId = new("assign-load-id");
    public static readonly QueryAssignmentId SearchCustomerNameAssignmentId = new("assign-customer-name");
    public static readonly QueryAssignmentId SearchEquipmentNumberAssignmentId = new("assign-equipment-number");
    public static readonly QueryAssignmentId AggregateLoadCountAssignmentId = new("count-loads");
    public static readonly QueryResultId RowsResultId = new("rows");
    public static readonly QueryResultId AggregationResultId = new("load-count");

    public static ShapeGraphDocument DomainShapeGraphDocument { get; } = CreateDomainShapeGraphDocument();

    public static ShapeGraphDocument DtoShapeGraphDocument { get; } = CreateDtoShapeGraphDocument();

    public static ImmutableArray<ShapeGraphDocument> ShapeGraphDocuments { get; } =
        [DomainShapeGraphDocument, DtoShapeGraphDocument];

    public static ImmutableArray<ShapeGraph> ShapeGraphs { get; } =
        [DomainShapeGraphDocument.Graph, DtoShapeGraphDocument.Graph];

    public static RelationshipDefinition LoadCustomerRelationship { get; } = new(
        LoadCustomerRelationshipId,
        LoadShapeId,
        LoadCustomerIdPath,
        CustomerShapeId,
        ObservationIdentityRelationshipTargetKey.Instance);

    public static RelationshipDefinition LoadEquipmentRelationship { get; } = new(
        LoadEquipmentRelationshipId,
        LoadShapeId,
        LoadEquipmentIdPath,
        EquipmentShapeId,
        ObservationIdentityRelationshipTargetKey.Instance);

    public static RelationshipCatalogDocument RelationshipCatalogDocument { get; } =
        Cohesive.Relations.Serialization.RelationshipCatalogDocument.FromCatalog(
            new RelationshipCatalog([LoadCustomerRelationship, LoadEquipmentRelationship]));

    public static RelationQueryDocument QueryDocument { get; } = CreateQueryDocument();

    public static RelationQueryDocument RelationDocument { get; } = CreateRelationDocument();

    public static RelationQueryDocument AggregationDocument { get; } = CreateAggregationDocument();

    static RelationQueryDocument CreateQueryDocument()
    {
        IRQueryDefinition definition = new(
            LoadSearchQueryId,
            LoadSearchQueryName,
            CreateBody(),
            [new RowsQueryResultDefinition(RowsResultId, ProjectionNodeId)]);

        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateRelationDocument()
    {
        IRRelationDefinition definition = new(
            LoadSearchRelationId,
            LoadSearchRelationName,
            CreateBody(),
            LoadBinding,
            new(
                ProjectionNodeId,
                LoadSearchShapeId,
                RelationOutputMode.OnePerRoot,
                Expr.Field(SearchBinding, SearchIdPath)));

        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateAggregationDocument()
    {
        IRQueryDefinition definition = new(
            new("federated-load-aggregation"),
            new("FederatedLoadAggregation"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(LoadSourceNodeId, LoadBinding, LoadShapeId),
                new AggregateQueryNode(
                    AggregateNodeId,
                    LoadSourceNodeId,
                    AggregateBinding,
                    LoadAggregateShapeId,
                    aggregates:
                    [
                        new QueryAggregateAssignment(
                            AggregateLoadCountAssignmentId,
                            AggregateLoadCountPath,
                            AggregateOperator.Count)
                    ])
            ]),
            [new AggregationQueryResultDefinition(AggregationResultId, AggregateNodeId)]);

        return RelationQueryDocument.FromDefinition(definition);
    }

    static LogicalQueryDefinition CreateBody() => new(
    [
        new SourceQueryNode(LoadSourceNodeId, LoadBinding, LoadShapeId),
        new TraverseRelationshipQueryNode(
            CustomerTraversalNodeId,
            LoadSourceNodeId,
            LoadBinding,
            LoadCustomerRelationshipId,
            RelationshipTraversalDirection.Forward,
            CustomerBinding,
            JoinKind.Left,
            QueryInputRequirement.Optional),
        new TraverseRelationshipQueryNode(
            EquipmentTraversalNodeId,
            CustomerTraversalNodeId,
            LoadBinding,
            LoadEquipmentRelationshipId,
            RelationshipTraversalDirection.Forward,
            EquipmentBinding,
            JoinKind.Left,
            QueryInputRequirement.Optional),
        new ProjectQueryNode(
            ProjectionNodeId,
            EquipmentTraversalNodeId,
            SearchBinding,
            LoadSearchShapeId,
            [
                new ProjectionAssignment(
                    SearchIdAssignmentId,
                    SearchIdPath,
                    Expr.Field(LoadBinding, LoadIdPath)),
                new ProjectionAssignment(
                    SearchCustomerNameAssignmentId,
                    SearchCustomerNamePath,
                    Expr.Field(CustomerBinding, CustomerNamePath)),
                new ProjectionAssignment(
                    SearchEquipmentNumberAssignmentId,
                    SearchEquipmentNumberPath,
                    Expr.Field(EquipmentBinding, EquipmentNumberPath))
            ])
    ]);

    static ShapeGraphDocument CreateDomainShapeGraphDocument()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var load = new Shape(
            LoadShapeLocalId,
            [
                new FieldDefinition(
                    new FieldName(LoadIdFieldName),
                    stringType,
                    role: FieldRole.Identity),
                new FieldDefinition(
                    new FieldName(LoadCustomerIdFieldName),
                    new EntityReferenceTypeRef(CustomerEntityType),
                    role: FieldRole.Reference),
                new FieldDefinition(
                    new FieldName(LoadEquipmentIdFieldName),
                    new EntityReferenceTypeRef(EquipmentEntityType),
                    role: FieldRole.Reference),
                new FieldDefinition(new FieldName(LoadStatusFieldName), stringType),
                new FieldDefinition(
                    new FieldName(LoadAmountFieldName),
                    new ScalarTypeRef(ScalarTypeKind.Decimal))
            ],
            role: ShapeRoles.Entity).WithEntityType(LoadEntityType);
        var customer = new Shape(
            CustomerShapeLocalId,
            [
                new FieldDefinition(
                    new FieldName(CustomerIdFieldName),
                    stringType,
                    role: FieldRole.Identity),
                new FieldDefinition(new FieldName(CustomerNameFieldName), stringType),
                new FieldDefinition(new FieldName(CustomerTypeFieldName), stringType)
            ],
            role: ShapeRoles.Entity).WithEntityType(CustomerEntityType);
        var equipment = new Shape(
            EquipmentShapeLocalId,
            [
                new FieldDefinition(
                    new FieldName(EquipmentIdFieldName),
                    stringType,
                    role: FieldRole.Identity),
                new FieldDefinition(new FieldName(EquipmentNumberFieldName), stringType),
                new FieldDefinition(new FieldName(EquipmentTypeFieldName), stringType)
            ],
            role: ShapeRoles.Entity).WithEntityType(EquipmentEntityType);

        return ShapeGraphDocument.FromGraph(new ShapeGraph(DomainGraphId, [load, customer, equipment]));
    }

    static ShapeGraphDocument CreateDtoShapeGraphDocument()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var search = new Shape(
            LoadSearchShapeLocalId,
            [
                new FieldDefinition(
                    new FieldName(SearchIdFieldName),
                    stringType,
                    role: FieldRole.Identity),
                new FieldDefinition(
                    new FieldName(SearchCustomerNameFieldName),
                    stringType,
                    presence: FieldPresence.Optional),
                new FieldDefinition(
                    new FieldName(SearchEquipmentNumberFieldName),
                    stringType,
                    presence: FieldPresence.Optional)
            ],
            role: ShapeRoles.Dto);
        var aggregate = new Shape(
            LoadAggregateShapeLocalId,
            [
                new FieldDefinition(
                    new FieldName(AggregateLoadCountFieldName),
                    new ScalarTypeRef(ScalarTypeKind.Int64))
            ],
            role: ShapeRoles.Projection);

        return ShapeGraphDocument.FromGraph(new ShapeGraph(DtoGraphId, [search, aggregate]));
    }
}
