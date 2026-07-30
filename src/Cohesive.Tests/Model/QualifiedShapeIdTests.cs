using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Authoring;

namespace Cohesive.Tests.Model;

public sealed class QualifiedShapeIdTests
{
    [Fact]
    public void QualifiedShapeId_SerializesGraphAndShapeIdsAsFlatValues()
    {
        var id = new QualifiedShapeId(
            graphId: new GraphId("graph.transportation.edi204.v4010"),
            shapeId: new ShapeId("shape.x12.204"));

        var json = JsonSerializer.Serialize(id, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("Failed to parse JSON.");

        Assert.Equal("graph.transportation.edi204.v4010", node["graphId"]!.GetValue<string>());
        Assert.Equal("shape.x12.204", node["shapeId"]!.GetValue<string>());

        var roundTrip = JsonSerializer.Deserialize<QualifiedShapeId>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(id, roundTrip);
    }

    [Fact]
    public void GraphShapeId_QualifiesShapeWithContainingGraph()
    {
        var graph = new ShapeGraph(
            id: new GraphId("graph.transportation.edi204.v4010"),
            shapes:
            [
                new(
                    id: new ShapeId("shape.x12.204"),
                    fields: [])
            ]);
        var graphShapeId = new GraphShapeId(
            graph: graph,
            shapeId: new ShapeId("shape.x12.204"));
        var json = JsonSerializer.Serialize(graphShapeId, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("Failed to parse JSON.");
        var roundTrip = JsonSerializer.Deserialize<GraphShapeId>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(
            new QualifiedShapeId(
                graphId: new GraphId("graph.transportation.edi204.v4010"),
                shapeId: new ShapeId("shape.x12.204")),
            graphShapeId.QualifiedId);
        Assert.Equal("graph.transportation.edi204.v4010", node["graph"]!["id"]!.GetValue<string>());
        Assert.Equal("shape.x12.204", node["shapeId"]!.GetValue<string>());
        Assert.Null(node["qualifiedId"]);
        Assert.Equal(graphShapeId.QualifiedId, roundTrip.QualifiedId);
        Assert.Equal("graph.transportation.edi204.v4010:shape.x12.204", graphShapeId.ToString());
        Assert.Throws<ArgumentException>(() => new GraphShapeId(graph, new ShapeId("shape.missing")));
    }

    [Fact]
    public void QualifiedShapeId_TypeRefTreatsGraphAndShapeIdsAsStrings()
    {
        var type = new DefaultClrTypeRefMapper().Map(typeof(QualifiedShapeId), null);
        var objectType = Assert.IsType<ObjectTypeRef>(type);
        var graphIdField = Assert.Single(objectType.Fields, field => field.Name == nameof(QualifiedShapeId.GraphId));
        var shapeIdField = Assert.Single(objectType.Fields, field => field.Name == nameof(QualifiedShapeId.ShapeId));

        Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(graphIdField.Type).Kind);
        Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(shapeIdField.Type).Kind);
    }

}
