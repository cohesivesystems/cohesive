using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class SingleValueWrapperJsonConverterTests
{
    [Fact]
    public void Serialize_TopLevelWrapper_WritesFlatUnderlyingValue()
    {
        var json = JsonSerializer.Serialize(new ShapeId("shape-001"), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("\"shape-001\"", json);
    }

    [Fact]
    public void Serialize_WrapperEnvelope_WritesFlatUnderlyingValues()
    {
        var envelope = CreateEnvelope();

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var node = JsonNode.Parse(json);

        Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse("""
                    {
                      "shapeId": "shape-001",
                      "graphId": "graph-001",
                      "diagnosticId": "diag-001",
                      "fieldName": "ReferenceNumber",
                      "typeId": "type-001",
                      "entityId": "entity-001",
                      "entityTypeName": "ShipmentEntity"
                    }
                    """),
            node));
    }

    [Fact]
    public void Deserialize_WrapperEnvelope_AcceptsFlatValues()
    {
        var envelope = JsonSerializer.Deserialize<WrapperEnvelope>(
            """
                {
                  "shapeId": "shape-001",
                  "graphId": "graph-001",
                  "diagnosticId": "diag-001",
                  "fieldName": "ReferenceNumber",
                  "typeId": "type-001",
                  "entityId": "entity-001",
                  "entityTypeName": "ShipmentEntity"
                }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(CreateEnvelope(), envelope);
    }

    [Fact]
    public void Deserialize_WrapperEnvelope_AcceptsLegacyNestedValueObjects()
    {
        var envelope = JsonSerializer.Deserialize<WrapperEnvelope>(
            """
                {
                  "shapeId": { "value": "shape-001" },
                  "graphId": { "value": "graph-001" },
                  "diagnosticId": { "value": "diag-001" },
                  "fieldName": { "value": "ReferenceNumber" },
                  "typeId": { "value": "type-001" },
                  "entityId": { "value": "entity-001" },
                  "entityTypeName": { "value": "ShipmentEntity" }
                }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(CreateEnvelope(), envelope);
    }

    [Fact]
    public void Deserialize_TopLevelWrapper_AcceptsLegacyNestedValueObject()
    {
        var value = JsonSerializer.Deserialize<ShapeId>(
            """{ "value": "shape-001" }""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(new ShapeId("shape-001"), value);
    }

    [Fact]
    public void Deserialize_TopLevelWrapper_AcceptsLegacyNestedValueObjectWithAdditionalProperties()
    {
        var value = JsonSerializer.Deserialize<ShapeId>(
            """{ "ignored": 42, "value": "shape-001", "metadata": { "source": "legacy" } }""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(new ShapeId("shape-001"), value);
    }

    [Fact]
    public void Deserialize_TopLevelWrapper_AcceptsOverriddenNestedPropertyName()
    {
        var value = JsonSerializer.Deserialize<WrappedCode>(
            """{ "code": "wrapped-001" }""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(new WrappedCode("wrapped-001"), value);
    }

    [Fact]
    public void Deserialize_TopLevelWrapper_AcceptsOverriddenNestedPropertyNameCaseInsensitively()
    {
        var value = JsonSerializer.Deserialize<WrappedCode>(
            """{ "CoDe": "wrapped-001" }""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(new WrappedCode("wrapped-001"), value);
    }

    [Fact]
    public void Deserialize_TopLevelWrapper_AcceptsOverriddenNestedPropertyNameWithCaseSensitiveOptions()
    {
        var value = JsonSerializer.Deserialize<WrappedCode>(
            """{ "code": "wrapped-001" }""",
            new JsonSerializerOptions());

        Assert.Equal(new WrappedCode("wrapped-001"), value);
    }

    [Fact]
    public void Serialize_TopLevelWrapper_WithOverriddenValueProperty_WritesFlatUnderlyingValue()
    {
        var json = JsonSerializer.Serialize(new WrappedCode("wrapped-001"), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("\"wrapped-001\"", json);
    }

    static WrapperEnvelope CreateEnvelope() => new(
        ShapeId: new("shape-001"),
        GraphId: new("graph-001"),
        DiagnosticId: new("diag-001"),
        FieldName: new("ReferenceNumber"),
        TypeId: new("type-001"),
        EntityId: new("entity-001"),
        EntityTypeName: new("ShipmentEntity")
        );

    sealed record WrapperEnvelope(
            ShapeId ShapeId,
            GraphId GraphId,
            DiagnosticId DiagnosticId,
            FieldName FieldName,
            TypeId TypeId,
            EntityId EntityId,
            EntityTypeName EntityTypeName
            );

    [JsonConverter(typeof(SingleValueWrapperJsonConverter))]
    [SingleValueWrapperValueProperty(nameof(Code))]
    [method: JsonConstructor]
    readonly record struct WrappedCode([property: JsonPropertyName("code")] string Code);
}
