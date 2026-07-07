using System.Text.Json;
using Cohesive.Relations.Model;
using Cohesive.Relations.Execution;

namespace Cohesive.Relations.Tests;

public sealed class RelationHydrationTests
{
    [Fact]
    public async Task HydrateAsync_OrderToSearchIndex_SelectsOnlyRequiredRootAndRelatedFields()
    {
        var definition = CreateSearchRelation();
        var rootOrder = new Observation(
            shapeId: new(nameof(OrderDto)),
            id: "order-1",
            version: 7,
            fields: Fields(new
            {
                OrderNumber = "ORD-1001",
                Stops = new[] { "PICKUP", "DELIVERY" },
                CustomerId = "cust-99",
                Unused = "ignore"
            }));
        var relatedCustomer = new Observation(
            shapeId: new(nameof(CustomerDto)),
            id: "cust-99",
            version: 2,
            fields: Fields(new
            {
                Name = "Northwind Foods",
                Segment = "Enterprise",
                Unused = "ignore"
            }));
        var store = new InMemoryHydrationStore(new Dictionary<ShapeId, IReadOnlyList<Observation>>
        {
            [new(nameof(OrderDto))] = [rootOrder],
            [new(nameof(CustomerDto))] = [relatedCustomer]
        });
        var hydrator = new RelationHydrator(store);

        var inputs = await hydrator.HydrateAsync(definition, rootIds: ["order-1"]);

        Assert.Equal(2, inputs.Count);
        var root = Assert.Single(inputs, x => x.ShapeId == new ShapeId(nameof(OrderDto)));
        var customer = Assert.Single(inputs, x => x.ShapeId == new ShapeId(nameof(CustomerDto)));
        Assert.Equal("order-1", customer.RootId);
        Assert.False(root.Observation.TryGetField("Unused", out _));
        Assert.False(customer.Observation.TryGetField("Unused", out _));

        Assert.Equal(2, store.Queries.Count);
        Assert.Equal(new ShapeId(nameof(OrderDto)), store.Queries[0].Schema);
        Assert.Equal(
            ["CustomerId", "OrderNumber", "Stops"],
            store.Queries[0].Fields.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(new ShapeId(nameof(CustomerDto)), store.Queries[1].Schema);
        Assert.Equal(
            ["Name", "Segment"],
            store.Queries[1].Fields.OrderBy(x => x, StringComparer.Ordinal).ToArray());

        var outputs = await new RelationExecutor().ExecuteAsync(definition, inputs);
        var searchOrder = Assert.Single(outputs);
        Assert.Equal("ORD-1001", searchOrder.GetField("OrderNumber").GetString());
        Assert.Equal(2, searchOrder.GetField("StopCount").GetInt32());
        var customerObject = searchOrder.GetField("Customer");
        Assert.Equal("cust-99", customerObject.GetProperty("id").GetString());
        Assert.Equal("Northwind Foods", customerObject.GetProperty("name").GetString());
    }

    [Fact]
    public async Task HydrateAsync_WithNoRelatedFields_OnlyQueriesRootSchema()
    {
        var definition = new RelationDefinition(
            id: new RelationId($"{nameof(OrderDto)}->SearchLite"),
            name: new RelationName("SearchLite"),
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
                    id: new MappingId("search_lite"),
                    name: new MappingName("SearchLite"),
                    targetShapeId: new ShapeId("SearchLite"),
                    assignments: [new("OrderNumber", Expr.Field("OrderNumber"))])
            ]);
        var root = new Observation(
            shapeId: new(nameof(OrderDto)),
            id: "order-1",
            version: 1,
            fields: Fields(new
            {
                OrderNumber = "ORD-1001",
                Extra = "ignored"
            }));
        var store = new InMemoryHydrationStore(new Dictionary<ShapeId, IReadOnlyList<Observation>>
        {
            [new(nameof(OrderDto))] = [root]
        });
        var hydrator = new RelationHydrator(store);

        var inputs = await hydrator.HydrateAsync(definition, rootIds: ["order-1"]);

        var hydratedRoot = Assert.Single(inputs);
        Assert.Equal(new ShapeId(nameof(OrderDto)), hydratedRoot.ShapeId);
        Assert.False(hydratedRoot.Observation.TryGetField("Extra", out _));
        Assert.Single(store.Queries);
        Assert.Equal(new ShapeId(nameof(OrderDto)), store.Queries[0].Schema);
        Assert.Equal(["OrderNumber"], store.Queries[0].Fields.ToArray());
    }

    static RelationDefinition CreateSearchRelation()
    {
        return new RelationDefinition(
            id: new RelationId($"{nameof(OrderDto)}->{nameof(SearchOrderDto)}"),
            name: new RelationName("SearchOrder"),
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
                    id: new MappingId("search_order"),
                    name: new MappingName("SearchOrder"),
                    targetShapeId: new ShapeId(nameof(SearchOrderDto)),
                    assignments:
                    [
                        new FieldAssignment("OrderNumber", expr: Expr.Field("OrderNumber")),
                        new FieldAssignment("StopCount", Expr.Call("count", Expr.Field("Stops"))),
                        new FieldAssignment(
                            "Customer",
                            Expr.Call(
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
                                    Expr.Const("Segment"))))
                    ])
            ]);
    }

    static IReadOnlyDictionary<string, ObservationValue> Fields(object expression)
        => ObservationValue.ToFieldDictionary(expression);

    sealed class InMemoryHydrationStore(IReadOnlyDictionary<ShapeId, IReadOnlyList<Observation>> bySchema)
        : IObservationHydrationStore
    {
        public List<ObservationHydrationOptions> Queries { get; } = [];

        public Task<IReadOnlyList<Observation>> QueryAsync(ObservationHydrationOptions options, CancellationToken token = default)
        {
            Queries.Add(options);

            if (!bySchema.TryGetValue(options.Schema, out var candidates))
                return Task.FromResult<IReadOnlyList<Observation>>([]);

            var keys = options.Keys is null ? null : new HashSet<string>(options.Keys, StringComparer.Ordinal);
            var selected = candidates
                .Where(x => keys is null || keys.Contains(x.Id))
                .Select(x => SelectFields(x, options.Fields))
                .ToArray();
            return Task.FromResult<IReadOnlyList<Observation>>(selected);
        }

        static Observation SelectFields(Observation source, IReadOnlyList<string> fields)
        {
            var layout = new ObservationLayout(source.ShapeId, fields);
            var values = new ObservationValue[layout.Count];
            var has = new bool[layout.Count];

            for (var i = 0; i < layout.Count; i++)
            {
                if (source.TryGetField(layout.FieldNames[i], out var value))
                {
                    values[i] = value;
                    has[i] = true;
                }
            }

            return new(
                layout: layout,
                id: source.Id,
                valuesByOrdinal: values,
                hasValueByOrdinal: has,
                version: source.Version,
                lineage: source.Lineage);
        }
    }

    sealed record OrderDto;
    sealed record CustomerDto;
    sealed record SearchOrderDto;
}
