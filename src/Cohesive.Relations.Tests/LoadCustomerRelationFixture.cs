using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Relations.Tests;

enum LoadCustomerProjectionMode
{
    LoadOnly = 0,
    Enriched = 1
}

sealed record LoadCustomerTraversalOptions(
    JoinKind JoinKind,
    QueryInputRequirement Requirement,
    RelationOutputMode OutputMode,
    LoadCustomerProjectionMode ProjectionMode
    )
{
    public static LoadCustomerTraversalOptions Baseline { get; } = new(
        JoinKind.Left,
        QueryInputRequirement.Required,
        RelationOutputMode.OnePerRoot,
        LoadCustomerProjectionMode.Enriched);

    public static LoadCustomerTraversalOptions Optional { get; } = new(
        JoinKind.Left,
        QueryInputRequirement.Optional,
        RelationOutputMode.OnePerRoot,
        LoadCustomerProjectionMode.Enriched);

    public static LoadCustomerTraversalOptions Inner { get; } = new(
        JoinKind.Inner,
        QueryInputRequirement.Required,
        RelationOutputMode.ZeroOrOnePerRoot,
        LoadCustomerProjectionMode.Enriched);
}

static class LoadCustomerRelationFixture
{
    public const string LoadIdFieldName = "Id";
    public const string LoadCustomerIdFieldName = "CustomerId";
    public const string LoadStatusFieldName = "Status";
    public const string LoadAmountFieldName = "Amount";
    public const string LoadActiveFieldName = "Active";
    public const string LoadNotesFieldName = "Notes";
    public const string CustomerIdFieldName = "Id";
    public const string CustomerNameFieldName = "Name";
    public const string CustomerTypeFieldName = "Type";
    public const string SearchIdFieldName = "Id";
    public const string SearchCustomerNameFieldName = "CustomerName";
    public const string SearchCustomerTypeFieldName = "CustomerType";
    public const string AggregateCustomerNameFieldName = "CustomerName";
    public const string AggregateTotalAmountFieldName = "TotalAmount";
    public const string AggregateLoadCountFieldName = "LoadCount";

    public static readonly GraphId DomainGraphId = new("domain/v1");
    public static readonly GraphId DtoGraphId = new("dto/v1");

    public static readonly EntityTypeName LoadEntityType = new("Load");
    public static readonly EntityTypeName CustomerEntityType = new("Customer");

    public static readonly ShapeId LoadShapeLocalId = new("Load");
    public static readonly ShapeId CustomerShapeLocalId = new("Customer");
    public static readonly ShapeId LoadSearchShapeLocalId = new("LoadSearchDto");
    public static readonly ShapeId LoadAggregateShapeLocalId = new("LoadByCustomerAggregate");

    public static readonly QualifiedShapeId LoadShapeId = new(DomainGraphId, LoadShapeLocalId);
    public static readonly QualifiedShapeId CustomerShapeId = new(DomainGraphId, CustomerShapeLocalId);
    public static readonly QualifiedShapeId LoadSearchShapeId = new(DtoGraphId, LoadSearchShapeLocalId);
    public static readonly QualifiedShapeId LoadAggregateShapeId = new(DtoGraphId, LoadAggregateShapeLocalId);

    static readonly ImmutableDictionary<QualifiedShapeId, RelationQuerySourceInstanceId> PhysicalSourceAliases =
        new Dictionary<QualifiedShapeId, RelationQuerySourceInstanceId>
        {
            [LoadShapeId] = FederatedLoadPhysicalExecutionFixture.LoadsSource,
            [CustomerShapeId] = FederatedLoadPhysicalExecutionFixture.CustomersSource
        }.ToImmutableDictionary();

    public static readonly FieldPath LoadIdPath = FieldPath.FromField(LoadIdFieldName);
    public static readonly FieldPath LoadCustomerIdPath = FieldPath.FromField(LoadCustomerIdFieldName);
    public static readonly FieldPath LoadStatusPath = FieldPath.FromField(LoadStatusFieldName);
    public static readonly FieldPath LoadAmountPath = FieldPath.FromField(LoadAmountFieldName);
    public static readonly FieldPath LoadActivePath = FieldPath.FromField(LoadActiveFieldName);
    public static readonly FieldPath LoadNotesPath = FieldPath.FromField(LoadNotesFieldName);
    public static readonly FieldPath CustomerIdPath = FieldPath.FromField(CustomerIdFieldName);
    public static readonly FieldPath CustomerNamePath = FieldPath.FromField(CustomerNameFieldName);
    public static readonly FieldPath CustomerTypePath = FieldPath.FromField(CustomerTypeFieldName);
    public static readonly FieldPath SearchIdPath = FieldPath.FromField(SearchIdFieldName);
    public static readonly FieldPath SearchCustomerNamePath = FieldPath.FromField(SearchCustomerNameFieldName);
    public static readonly FieldPath SearchCustomerTypePath = FieldPath.FromField(SearchCustomerTypeFieldName);
    public static readonly FieldPath AggregateCustomerNamePath = FieldPath.FromField(AggregateCustomerNameFieldName);
    public static readonly FieldPath AggregateTotalAmountPath = FieldPath.FromField(AggregateTotalAmountFieldName);
    public static readonly FieldPath AggregateLoadCountPath = FieldPath.FromField(AggregateLoadCountFieldName);

    public static readonly RelationshipId LoadCustomerRelationshipId = new("Load.Customer");

    public static RelationQuerySourcePlacement CreatePhysicalPlacement(CompiledRelationQueryPlan plan) =>
        FederatedLoadPhysicalExecutionFixture.CreatePlacement(
            plan,
            sourceAliases: PhysicalSourceAliases);

    public static readonly RelationId LoadSearchRelationId = new("load-search");
    public static readonly RelationName LoadSearchRelationName = new("LoadSearch");
    public static readonly QueryId LoadSearchQueryId = new("load-search-query");
    public static readonly QueryName LoadSearchQueryName = new("LoadSearchQuery");
    public static readonly QueryId ExplicitJoinQueryId = new("load-customer-explicit-join");
    public static readonly QueryName ExplicitJoinQueryName = new("LoadCustomerExplicitJoin");

    public static readonly ValueBindingId LoadBinding = new("load");
    public static readonly ValueBindingId CustomerBinding = new("customer");
    public static readonly ValueBindingId SearchBinding = new("loadSearch");
    public static readonly ValueBindingId AggregateBinding = new("loadAggregate");

    public static readonly QueryNodeId LoadSourceNodeId = new("loads");
    public static readonly QueryNodeId CustomerSourceNodeId = new("customers");
    public static readonly QueryNodeId StatusFilterNodeId = new("status-filter");
    public static readonly QueryNodeId CustomerTraversalNodeId = new("customer-traversal");
    public static readonly QueryNodeId ExplicitJoinNodeId = new("load-customer-join");
    public static readonly QueryNodeId ProjectionNodeId = new("project-load-search");
    public static readonly QueryNodeId OrderNodeId = new("order-load-search");
    public static readonly QueryNodeId PageNodeId = new("page-load-search");
    public static readonly QueryNodeId AggregateNodeId = new("aggregate-by-customer");

    public static readonly QueryAssignmentId SearchIdAssignmentId = new("assign-id");
    public static readonly QueryAssignmentId SearchCustomerNameAssignmentId = new("assign-customer-name");
    public static readonly QueryAssignmentId AggregateCustomerNameGroupingId = new("group-customer-name");
    public static readonly QueryAssignmentId AggregateTotalAmountAssignmentId = new("sum-total-amount");
    public static readonly QueryAssignmentId AggregateLoadCountAssignmentId = new("count-loads");

    public static readonly QueryResultId RowsResultId = new("rows");
    public static readonly QueryResultId AggregationResultId = new("by-customer");
    public static readonly QueryParameterId StatusParameterId = new("status");
    public static readonly QueryParameterId CursorParameterId = new("cursor");

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

    public static RelationshipCatalogDocument RelationshipCatalogDocument { get; } =
        Cohesive.Relations.Serialization.RelationshipCatalogDocument.FromCatalog(
            new RelationshipCatalog([LoadCustomerRelationship]));

    public static RelationQueryDocument BaselineRelationDocument { get; } =
        CreateRelationDocument(LoadCustomerTraversalOptions.Baseline);

    public static RelationQueryDocument OptionalTraversalRelationDocument { get; } =
        CreateRelationDocument(LoadCustomerTraversalOptions.Optional);

    public static RelationQueryDocument InnerTraversalRelationDocument { get; } =
        CreateRelationDocument(LoadCustomerTraversalOptions.Inner);

    public static RelationQueryDocument RepresentativeQueryDocument { get; } =
        CreateRepresentativeQueryDocument();

    public static RelationQueryDocument ExplicitJoinQueryDocument { get; } =
        CreateExplicitJoinQueryDocument();

    public static RelationQueryDocument CreateRelationDocument(LoadCustomerTraversalOptions? options = null)
    {
        options ??= LoadCustomerTraversalOptions.Baseline;
        var assignments = CreateSearchAssignments(options.ProjectionMode);
        IRRelationDefinition definition = new(
            LoadSearchRelationId,
            LoadSearchRelationName,
            new(
            [
                new SourceQueryNode(LoadSourceNodeId, LoadBinding, LoadShapeId),
                new TraverseRelationshipQueryNode(
                    CustomerTraversalNodeId,
                    LoadSourceNodeId,
                    LoadBinding,
                    LoadCustomerRelationshipId,
                    RelationshipTraversalDirection.Forward,
                    CustomerBinding,
                    options.JoinKind,
                    options.Requirement
                    ),
                new ProjectQueryNode(
                    ProjectionNodeId,
                    CustomerTraversalNodeId,
                    SearchBinding,
                    LoadSearchShapeId,
                    assignments
                    )
            ]),
            LoadBinding,
            new(ProjectionNodeId,
                LoadSearchShapeId,
                options.OutputMode,
                Expr.Field(SearchBinding, SearchIdPath)
                )
            );

        return RelationQueryDocument.FromDefinition(definition);
    }

    public static RelationQueryDocument CreateRepresentativeQueryDocument(
        LoadCustomerTraversalOptions? options = null)
    {
        options ??= LoadCustomerTraversalOptions.Optional;
        IRQueryDefinition definition = new(
            LoadSearchQueryId,
            LoadSearchQueryName,
            new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(LoadSourceNodeId, LoadBinding, LoadShapeId),
                    new FilterQueryNode(
                        StatusFilterNodeId,
                        LoadSourceNodeId,
                        Expr.Eq(
                            Expr.Field(LoadBinding, LoadStatusPath),
                            Expr.Param(StatusParameterId.Value))),
                    new TraverseRelationshipQueryNode(
                        CustomerTraversalNodeId,
                        StatusFilterNodeId,
                        LoadBinding,
                        LoadCustomerRelationshipId,
                        RelationshipTraversalDirection.Forward,
                        CustomerBinding,
                        options.JoinKind,
                        options.Requirement),
                    new ProjectQueryNode(
                        ProjectionNodeId,
                        CustomerTraversalNodeId,
                        SearchBinding,
                        LoadSearchShapeId,
                        CreateSearchAssignments(options.ProjectionMode)),
                    new OrderQueryNode(
                        OrderNodeId,
                        ProjectionNodeId,
                        [new QueryOrdering(Expr.Field(SearchBinding, SearchIdPath))]),
                    new PageQueryNode(
                        PageNodeId,
                        OrderNodeId,
                        new KeysetPageDefinition(
                            limit: 25,
                            after: [Expr.Param(CursorParameterId.Value)])),
                    new AggregateQueryNode(
                        AggregateNodeId,
                        CustomerTraversalNodeId,
                        AggregateBinding,
                        LoadAggregateShapeId,
                        groupings:
                        [
                            new QueryGrouping(
                                AggregateCustomerNameGroupingId,
                                AggregateCustomerNamePath,
                                Expr.Field(CustomerBinding, CustomerNamePath))
                        ],
                        aggregates:
                        [
                            new QueryAggregateAssignment(
                                AggregateLoadCountAssignmentId,
                                AggregateLoadCountPath,
                                AggregateOperator.Count),
                            new QueryAggregateAssignment(
                                AggregateTotalAmountAssignmentId,
                                AggregateTotalAmountPath,
                                AggregateOperator.Sum,
                                Expr.Field(LoadBinding, LoadAmountPath),
                                Expr.Field(LoadBinding, LoadActivePath))
                        ])
                ],
                parameters:
                [
                    new QueryParameterDefinition(
                        CursorParameterId,
                        new ScalarTypeRef(ScalarTypeKind.String)),
                    new QueryParameterDefinition(
                        StatusParameterId,
                        new ScalarTypeRef(ScalarTypeKind.String))
                ]),
            results:
            [
                new AggregationQueryResultDefinition(AggregationResultId, AggregateNodeId),
                new RowsQueryResultDefinition(RowsResultId, PageNodeId)
            ]);

        return RelationQueryDocument.FromDefinition(definition);
    }

    public static RelationQueryDocument CreateExplicitJoinQueryDocument(
        JoinKind joinKind = JoinKind.Inner,
        LoadCustomerProjectionMode projectionMode = LoadCustomerProjectionMode.Enriched)
    {
        IRQueryDefinition definition = new(
            ExplicitJoinQueryId,
            ExplicitJoinQueryName,
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(LoadSourceNodeId, LoadBinding, LoadShapeId),
                new SourceQueryNode(CustomerSourceNodeId, CustomerBinding, CustomerShapeId),
                new JoinQueryNode(
                    ExplicitJoinNodeId,
                    LoadSourceNodeId,
                    CustomerSourceNodeId,
                    joinKind,
                    Expr.Eq(
                        Expr.Field(LoadBinding, LoadCustomerIdPath),
                        Expr.Field(CustomerBinding, CustomerIdPath))),
                new ProjectQueryNode(
                    ProjectionNodeId,
                    ExplicitJoinNodeId,
                    SearchBinding,
                    LoadSearchShapeId,
                    CreateSearchAssignments(projectionMode))
            ]),
            [new RowsQueryResultDefinition(RowsResultId, ProjectionNodeId)]);

        return RelationQueryDocument.FromDefinition(definition);
    }

    static ShapeGraphDocument CreateDomainShapeGraphDocument()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var customerReferenceType = new EntityReferenceTypeRef(CustomerEntityType);
        var load = new Shape(
            LoadShapeLocalId,
            [
                new FieldDefinition(
                    new FieldName(LoadIdFieldName),
                    stringType,
                    role: FieldRole.Identity),
                new FieldDefinition(
                    new FieldName(LoadCustomerIdFieldName),
                    customerReferenceType,
                    role: FieldRole.Reference),
                new FieldDefinition(new FieldName(LoadStatusFieldName), stringType),
                new FieldDefinition(
                    new FieldName(LoadAmountFieldName),
                    new ScalarTypeRef(ScalarTypeKind.Decimal)),
                new FieldDefinition(
                    new FieldName(LoadActiveFieldName),
                    new ScalarTypeRef(ScalarTypeKind.Bool)),
                new FieldDefinition(
                    new FieldName(LoadNotesFieldName),
                    stringType,
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable)
            ],
            role: ShapeRoles.Entity).WithEntityType(LoadEntityType);
        var customer = new Shape(
            CustomerShapeLocalId,
            [
                new FieldDefinition(
                    new FieldName(CustomerIdFieldName),
                    customerReferenceType,
                    role: FieldRole.Identity),
                new FieldDefinition(new FieldName(CustomerNameFieldName), stringType),
                new FieldDefinition(new FieldName(CustomerTypeFieldName), stringType)
            ],
            role: ShapeRoles.Entity).WithEntityType(CustomerEntityType);

        return ShapeGraphDocument.FromGraph(new ShapeGraph(DomainGraphId, [load, customer]));
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
                    new FieldName(SearchCustomerTypeFieldName),
                    stringType,
                    presence: FieldPresence.Optional)
            ],
            role: ShapeRoles.Dto);
        var aggregate = new Shape(
            LoadAggregateShapeLocalId,
            [
                new FieldDefinition(
                    new FieldName(AggregateCustomerNameFieldName),
                    stringType,
                    presence: FieldPresence.Optional),
                new FieldDefinition(
                    new FieldName(AggregateTotalAmountFieldName),
                    new ScalarTypeRef(ScalarTypeKind.Decimal)),
                new FieldDefinition(
                    new FieldName(AggregateLoadCountFieldName),
                    new ScalarTypeRef(ScalarTypeKind.Int64))
            ],
            role: ShapeRoles.Projection);

        return ShapeGraphDocument.FromGraph(new ShapeGraph(DtoGraphId, [search, aggregate]));
    }

    static ImmutableArray<ProjectionAssignment> CreateSearchAssignments(
        LoadCustomerProjectionMode projectionMode) => projectionMode switch
    {
        LoadCustomerProjectionMode.LoadOnly =>
        [
            new ProjectionAssignment(
                SearchIdAssignmentId,
                SearchIdPath,
                Expr.Field(LoadBinding, LoadIdPath))
        ],
        LoadCustomerProjectionMode.Enriched =>
        [
            new ProjectionAssignment(
                SearchIdAssignmentId,
                SearchIdPath,
                Expr.Field(LoadBinding, LoadIdPath)),
            new ProjectionAssignment(
                SearchCustomerNameAssignmentId,
                SearchCustomerNamePath,
                Expr.Field(CustomerBinding, CustomerNamePath))
        ],
        _ => throw new ArgumentOutOfRangeException(
            nameof(projectionMode),
            projectionMode,
            "Unsupported Load/Customer projection mode.")
    };
}
