using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Tests search-index projection rooted on an order observation with related customer resolution.
/// </summary>
public sealed class SearchIndexProjectionTests
{
    static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task ExecuteAsync_OrderDtoRootsSearchOrderDto_WithResolvedSearchCustomDto()
    {
        var definition = new RelationDefinition(
            id: new RelationId($"{nameof(OrderDto)}->{nameof(SearchOrderDto)}"),
            name: new RelationName("SearchOrderIndex"),
            sources:
            [
                new RelationSource(
                    alias: new SourceAlias("src"),
                    shapeId: new ShapeId(nameof(OrderDto)),
                    cardinality: SourceCardinality.Many)
            ],
            mappings:
            [
                new MappingDefinition(
                    id: new MappingId("search_order_index"),
                    name: new MappingName("SearchOrderIndex"),
                    targetShapeId: new ShapeId(nameof(SearchOrderDto)),
                    assignments:
                    [
                        new FieldAssignment(
                            targetField: "OrderNumber",
                            expr: Expr.Field("OrderNumber"),
                            id: "assign_order_number"
                            ),
                        new FieldAssignment(
                            targetField: "StopCount",
                            expr: Expr.Call("count", Expr.Field("Stops")),
                            id: "assign_stop_count"
                            ),
                        new FieldAssignment(
                            targetField: "Customer",
                            expr: Expr.Call(
                                "object",
                                Expr.Const("id"),
                                Expr.Field("CustomerId"),
                                Expr.Const("name"),
                                Expr.RelatedField(
                                    Expr.Const(nameof(CustomerDto)),
                                    Expr.Field("CustomerId"),
                                    Expr.Const("Name")),
                                Expr.Const("segment"),
                                Expr.RelatedField(
                                    Expr.Const(nameof(CustomerDto)),
                                    Expr.Field("CustomerId"),
                                    Expr.Const("Segment"))),
                            id: "assign_search_customer"
                            )
                    ])
            ]);

        var order = new OrderDto(
            OrderNumber: "ORD-1001",
            Stops: ["PICKUP", "MID", "DELIVERY"],
            CustomerId: "cust-99");

        var customer = new CustomerDto(
            Id: "cust-99",
            Name: "Northwind Foods",
            Segment: "Enterprise");

        var orderMapper = ObjectObservationMapper
            .For<OrderDto>(new(nameof(OrderDto)))
            .Map(nameof(OrderDto.OrderNumber), nameof(OrderDto.OrderNumber))
            .Map(nameof(OrderDto.Stops), nameof(OrderDto.Stops))
            .Map(nameof(OrderDto.CustomerId), nameof(OrderDto.CustomerId))
            .Build();

        var customerMapper = ObjectObservationMapper
            .For<CustomerDto>(new(nameof(CustomerDto)))
            .MapAllFromJsonPropertyName(requireAttribute: true)
            .Build();

        var orderObservation = orderMapper.Map(
            order,
            new ObjectObservationMetadata
            {
                Id = "order-1",
                Version = 3
            });

        var customerObservation = customerMapper.Map(
            customer,
            new ObjectObservationMetadata
            {
                Id = "cust-99",
                Version = 11
            });

        var outputs = await new RelationExecutor().ExecuteAsync(
            relation: definition,
            inputs:
            [
                new RootedObservation(orderObservation, "order-1"),
                new RootedObservation(customerObservation, "order-1")
            ]
            );

        var searchOrderObservation = Assert.Single(outputs);
        Assert.Equal(new ShapeId(nameof(SearchOrderDto)), searchOrderObservation.ShapeId);
        var searchLayout = new ObservationLayout(
            new ShapeId(nameof(SearchOrderDto)),
            [nameof(SearchOrderDto.OrderNumber), nameof(SearchOrderDto.StopCount), nameof(SearchOrderDto.Customer)]
            );
        var explicitSearchOrderMapper = ObservationObjectMapper
            .For<SearchOrderDto>(searchLayout)
            .Map(nameof(SearchOrderDto.OrderNumber), x => x.OrderNumber)
            .Map(nameof(SearchOrderDto.StopCount), x => x.StopCount)
            .Map(
                nameof(SearchOrderDto.Customer),
                x => x.Customer,
                json => json.Deserialize<SearchCustomerDto>(CaseInsensitiveJson)
                        ?? throw new InvalidOperationException("Projected customer payload is missing."))
            .Build();

        var attributeSearchOrderMapper = ObservationObjectMapper
            .For<SearchOrderDto>(searchLayout)
            .WithSerializerOptions(CaseInsensitiveJson)
            .MapAllFromJsonPropertyName(requireAttribute: true)
            .Build();

        var projectedViaExplicitMapping = explicitSearchOrderMapper.Map(searchOrderObservation);
        var projectedViaAttributeMapping = attributeSearchOrderMapper.Map(searchOrderObservation);
        Assert.Equal(projectedViaExplicitMapping, projectedViaAttributeMapping);
        Assert.Equal(
            expected: new SearchOrderDto(
                OrderNumber: "ORD-1001",
                StopCount: 3,
                Customer: new SearchCustomerDto("cust-99", "Northwind Foods", "Enterprise")),
            actual: projectedViaExplicitMapping);

        var lineages = RelationLineage.Contributors(searchOrderObservation, nameof(SearchOrderDto.Customer));
        var customerLineage = Assert.Single(lineages);
        Assert.Equal("assign_search_customer", customerLineage.NodeId);
        Assert.Contains(customerLineage.SourcePaths, x => x.ToString().Contains($"{nameof(CustomerDto)}.cust-99.Name", StringComparison.Ordinal));
    }

    sealed record OrderDto(string OrderNumber, IReadOnlyList<string> Stops, string CustomerId);

    sealed record CustomerDto(
        [property: JsonPropertyName("CustomerId")] string Id,
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Segment")] string Segment);

    sealed record SearchOrderDto(
        [property: JsonPropertyName("OrderNumber")] string OrderNumber,
        [property: JsonPropertyName("StopCount")] int StopCount,
        [property: JsonPropertyName("Customer")] SearchCustomerDto Customer);

    sealed record SearchCustomerDto(string Id, string Name, string Segment);
}
