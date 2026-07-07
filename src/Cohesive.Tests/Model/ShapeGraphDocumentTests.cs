using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class ShapeGraphDocumentTests
{
    [Fact]
    public void ShapeGraphDocument_JsonRoundTrip_PreservesGraphAndMetadata()
    {
        var addressTypeId = new TypeId("type.order.address");
        var graph = new ShapeGraph(
            id: new("graph.order"),
            shapes:
            [
                new(id: new("shape.order"),
                    role: ShapeRoles.Entity,
                    fields:
                    [
                        new(name: new("orderNumber"),
                            type: new ScalarTypeRef(ScalarTypeKind.String)
                            ),
                        new(name: new("address"),
                            type: new NamedTypeRef(addressTypeId),
                            presence: FieldPresence.Optional,
                            nullability: FieldNullability.Nullable
                            )
                    ])
            ],
            namedTypes:
            [
                new TypeDefinition.Structural(
                    id: addressTypeId,
                    fields:
                    [
                        new(name: new("city"),
                            type: new ScalarTypeRef(ScalarTypeKind.String)
                            )
                    ])
            ],
            annotations: AnnotationMap.Create("graph.kind", "order"));

        var metadata = new ShapeGraphDocumentMetadata(
            origin: DocumentOrigin.User,
            name: "Order Graph",
            description: "Graph used by shape graph document tests.",
            sourceUri: "memory://shape-graphs/order");

        var document = ShapeGraphDocument.FromGraph(graph, metadata);
        var options = CreateJsonOptions();
        var json = JsonSerializer.Serialize(document, options);
        var parsed = JsonSerializer.Deserialize<ShapeGraphDocument>(json, options) ?? throw new InvalidOperationException("Failed to deserialize shape graph document JSON.");
        var reparsedJson = JsonSerializer.Serialize(parsed, options);

        Assert.Contains("\"kind\": \"String\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"address\"", json, StringComparison.Ordinal);
        Assert.Contains("\"cardinality\": \"Single\"", json, StringComparison.Ordinal);
        Assert.Contains("\"presence\": \"Optional\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\": 4", json, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(reparsedJson)));
        Assert.Equal(ShapeGraphDocument.CurrentSchemaVersion, parsed.SchemaVersion);
        Assert.Equal(DocumentOrigin.User, parsed.Metadata.Origin);
        Assert.Equal("Order Graph", parsed.Metadata.Name);
        Assert.Equal("graph.order", parsed.Graph.Id.Value);
        Assert.Equal("order", parsed.Graph.Annotations[new("graph.kind")].Value!.GetValue<string>());
        Assert.True(parsed.Graph.TryGetShape(new ShapeId("shape.order"), out var shape));
        Assert.IsType<NamedTypeRef>(shape.GetField("address").Type);
        Assert.True(parsed.Graph.TryGetType(addressTypeId, out var addressType));
        var structural = Assert.IsType<TypeDefinition.Structural>(addressType);
        Assert.Equal("address", structural.Name);
        Assert.Equal("city", Assert.Single(structural.Fields).Name.Value);
    }

    [Fact]
    public void TypeDefinition_JsonRoundTrip_InfersMissingNameFromTypeId()
    {
        const string json = """
            {
              "$typeDef": "structural",
              "id": "clr:type:Sample.Training.Scenarios.Transportation.Model.Edi990TenderResponse",
              "fields": []
            }
            """;

        var parsed = JsonSerializer.Deserialize<TypeDefinition>(json, CreateJsonOptions())
                     ?? throw new InvalidOperationException("Failed to deserialize type definition JSON.");
        var structural = Assert.IsType<TypeDefinition.Structural>(parsed);

        Assert.Equal("Edi990TenderResponse", structural.Name);
        Assert.Equal("clr:type:Sample.Training.Scenarios.Transportation.Model.Edi990TenderResponse", structural.Id.Value);
    }

    [Fact]
    public void ShapeGraphDocument_JsonRoundTrip_AcceptsLegacyNumericEnumValues()
    {
        const string json = """
            {
              "schemaVersion": "shape-graph/v1",
              "graph": {
                "id": "graph.legacy",
                "shapes": [
                  {
                    "id": "shape.legacy",
                    "fields": [
                      {
                        "name": "orderNumber",
                        "type": { "$type": "scalar", "kind": 4, "format": 0 },
                        "cardinality": 0,
                        "presence": 0,
                        "nullability": 0,
                        "mutability": 0
                      }
                    ]
                  }
                ]
              }
            }
            """;

        var parsed = JsonSerializer.Deserialize<ShapeGraphDocument>(json, CreateJsonOptions())
                     ?? throw new InvalidOperationException("Failed to deserialize shape graph document JSON.");

        var field = Assert.Single(Assert.Single(parsed.Graph.Shapes).Fields);
        var type = Assert.IsType<ScalarTypeRef>(field.Type);
        Assert.Equal(ScalarTypeKind.String, type.Kind);
        Assert.Equal(PrimitiveFormat.None, type.Format);
        Assert.Equal(FieldCardinality.Single, field.Cardinality);
        Assert.Equal(FieldPresence.Required, field.Presence);
        Assert.Equal(FieldNullability.NonNullable, field.Nullability);
        Assert.Equal(FieldMutability.Mutable, field.Mutability);
    }

    static JsonSerializerOptions CreateJsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
}
