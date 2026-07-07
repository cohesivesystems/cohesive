namespace Cohesive.Relations.Tests;

/// <summary>
/// Tests relation runtime execution, incremental cache behavior, and explainability hooks.
/// </summary>
public sealed class RelationExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_Edi204ToDomainProjection_EmitsTenderAndStops()
    {
        var definition = CreateEdiTenderMappingDefinition();

        var input = new Observation(
            shapeId: new ShapeId("edi.204"),
            id: "edi-msg-1",
            version: 1,
            fields: Fields(new
            {
                EdiTransactionSet = "204",
                EdiTenderId = "TN-900",
                EdiShipper = "ACME",
                EdiStops = new[]
                {
                    new { code = "S1" },
                    new { code = "S2" }
                }
            }));

        var executor = new RelationExecutor();
        var outputs = await executor.ExecuteAsync(definition, [input], CancellationToken.None);

        Assert.Equal(3, outputs.Count);
        Assert.Single(outputs, x => x.ShapeId == new ShapeId("domain.inboundTender"));
        Assert.Equal(2, outputs.Count(x => x.ShapeId == new ShapeId("domain.stop")));

        var tender = outputs.Single(x => x.ShapeId == new ShapeId("domain.inboundTender"));
        Assert.Equal("TN-900", tender.Fields["TenderId"].GetString());
        Assert.Equal("ACME", tender.Fields["Shipper"].GetString());
        Assert.Equal(2, tender.Fields["StopCount"].GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_DomainToIndexProjection_EmitsAssignedOrdersOnly()
    {
        var definition = CreateAssignedOrderIndexMappingDefinition();

        var assigned = CreateOrderObservation(id: "order-1", status: "Assigned", stops: ["A", "B"], version: 1);
        var draft = CreateOrderObservation(id: "order-2", status: "Draft", stops: ["A"], version: 1);
        var outputs = await new RelationExecutor().ExecuteAsync(definition, [assigned, draft], CancellationToken.None);

        var indexDoc = Assert.Single(outputs);
        Assert.Equal(new ShapeId("index.orderDocument"), indexDoc.ShapeId);
        Assert.Equal("order-1", indexDoc.Fields["OrderId"].GetString());
        Assert.Equal("Assigned", indexDoc.Fields["Status"].GetString());
        Assert.Equal(2, indexDoc.Fields["StopCount"].GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_IncrementalCache_RecomputesChangedRootOnly()
    {
        var trace = new CapturingExecutionListener();
        var executor = new RelationExecutor(options: new RelationExecutionOptions { Listener = trace });
        var definition = CreateOrderIdOnlyIndexMappingDefinition();

        var root1v1 = CreateOrderObservation(id: "order-1", status: "Assigned", stops: ["A"], version: 1);
        var root2v1 = CreateOrderObservation(id: "order-2", status: "Assigned", stops: ["A"], version: 1);

        _ = await executor.ExecuteAsync(definition, [root1v1, root2v1], CancellationToken.None);
        Assert.Equal(2, trace.Traces.Count);

        _ = await executor.ExecuteAsync(definition, [root1v1, root2v1], CancellationToken.None);
        Assert.Equal(2, trace.Traces.Count);

        var root2v2 = root2v1 with { Version = 2 };
        _ = await executor.ExecuteAsync(definition, [root1v1, root2v2], CancellationToken.None);
        Assert.Equal(3, trace.Traces.Count);
    }

    [Fact]
    public async Task ExecuteAsync_ProducesLineageAndExplainabilityMetadata()
    {
        var trace = new CapturingExecutionListener();
        var executor = new RelationExecutor(options: new RelationExecutionOptions { Listener = trace });
        var definition = CreateLineageIndexMappingDefinition();

        var input = CreateOrderObservation(id: "order-1", status: "Assigned", stops: [], version: 1);
        var output = Assert.Single(await executor.ExecuteAsync(definition, [input], CancellationToken.None));

        Assert.Single(trace.Traces);
        var statusLineage = RelationLineage.Contributors(output, "Status");
        var contribution = Assert.Single(statusLineage);
        Assert.Equal("assign_status", contribution.NodeId);
        Assert.Contains(contribution.SourcePaths, x => x.ToString() == "Status");

        var references = RelationLineage.ReferencingNodes(definition, "Status");
        Assert.Contains("assign_status", references);
    }

    static RelationDefinition CreateEdiTenderMappingDefinition()
    {
        var filter = Expr.Eq(Expr.Field("EdiTransactionSet"), Expr.Const("204"));
        var tenderProjection = new MappingDefinition(
            id: new MappingId("map_tender"),
            name: new MappingName("InboundTender"),
            targetShapeId: new ShapeId("domain.inboundTender"),
            assignments:
            [
                new FieldAssignment("TenderId", Expr.Field("EdiTenderId")),
                new FieldAssignment("Shipper", Expr.Field("EdiShipper")),
                new FieldAssignment("StopCount", Expr.Call("count", Expr.Field("EdiStops")))
            ]);
        var stopProjection = new MappingDefinition(
            id: new MappingId("map_stop"),
            name: new MappingName("InboundStops"),
            targetShapeId: new ShapeId("domain.stop"),
            assignments:
            [
                new FieldAssignment("TenderId", Expr.Field("EdiTenderId")),
                new FieldAssignment("Shipper", Expr.Field("EdiShipper"))
            ],
            forEach: Expr.Field("EdiStops"));

        return CreateRelation(
            from: new ShapeId("edi.204"),
            mappings: [tenderProjection, stopProjection],
            filter: filter);
    }

    static RelationDefinition CreateAssignedOrderIndexMappingDefinition()
    {
        return CreateRelation(
            from: new ShapeId("domain.order"),
            mappings:
            [
                new MappingDefinition(
                    id: new MappingId("order_index"),
                    name: new MappingName("OrderIndex"),
                    targetShapeId: new ShapeId("index.orderDocument"),
                    assignments:
                    [
                        new FieldAssignment("OrderId", Expr.Field("OrderId")),
                        new FieldAssignment("Status", Expr.Field("Status")),
                        new FieldAssignment("StopCount", Expr.Call("count", Expr.Field("Stops")))
                    ],
                    predicate: Expr.Eq(Expr.Field("Status"), Expr.Const("Assigned")))
            ]);
    }

    static RelationDefinition CreateOrderIdOnlyIndexMappingDefinition()
    {
        return CreateRelation(
            from: new ShapeId("domain.order"),
            mappings:
            [
                new MappingDefinition(
                    id: new MappingId("order_index"),
                    name: new MappingName("OrderIndex"),
                    targetShapeId: new ShapeId("index.orderDocument"),
                    assignments:
                    [
                        new FieldAssignment("OrderId", Expr.Field("OrderId"))
                    ])
            ]);
    }

    static RelationDefinition CreateLineageIndexMappingDefinition()
    {
        return CreateRelation(
            from: new ShapeId("domain.order"),
            mappings:
            [
                new MappingDefinition(
                    id: new MappingId("lineage_rule"),
                    name: new MappingName("LineageIndex"),
                    targetShapeId: new ShapeId("index.orderDocument"),
                    assignments:
                    [
                        new FieldAssignment(
                            targetField: "Status",
                            expr: Expr.Field("Status"),
                            id: "assign_status")
                    ])
            ]);
    }

    static RelationDefinition CreateRelation(
        ShapeId from,
        IReadOnlyList<MappingDefinition> mappings,
        Expr? filter = null)
    {
        var firstTarget = mappings[0].TargetShapeId;
        return new RelationDefinition(
            id: new RelationId($"{from.Value}->{firstTarget.Value}"),
            name: new RelationName($"{from.Value}To{firstTarget.Value}"),
            sources:
            [
                new RelationSource(
                    alias: new SourceAlias("src"),
                    shapeId: from,
                    cardinality: SourceCardinality.Many)
            ],
            filter: filter,
            mappings: [.. mappings]);
    }

    static Observation CreateOrderObservation(string id, string status, IReadOnlyList<string> stops, long version)
    {
        return new Observation(
            shapeId: new ShapeId("domain.order"),
            id: id,
            version: version,
            fields: Fields(new
            {
                OrderId = id,
                Status = status,
                Stops = stops
            }));
    }

    static IReadOnlyDictionary<string, ObservationValue> Fields(object expression)
        => ObservationValue.ToFieldDictionary(expression);

    sealed class CapturingExecutionListener : IRelationExecutionListener
    {
        public List<RelationAssignmentTrace> Traces { get; } = [];

        public void OnAssignment(RelationAssignmentTrace trace) => Traces.Add(trace);
    }
}
