using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Tests JSON-IR round-tripping for relation documents.
/// </summary>
public sealed class RelationDefinitionJsonRoundTripTests
{
    [Fact]
    public void JsonRoundTrip_PreservesRelationShape()
    {
        var definition = new RelationDefinition(
            id: new RelationId("rel_edi_tender"),
            name: new RelationName("EdiTender"),
            sources:
            [
                new RelationSource(
                    alias: new SourceAlias("src"),
                    shapeId: new ShapeId("edi.204"),
                    cardinality: SourceCardinality.Many)
            ],
            filter: Expr.Eq(Expr.Field("EdiTransactionSet"), Expr.Const("204")),
            mappings:
            [
                new MappingDefinition(
                    id: new MappingId("map_tender"),
                    name: new MappingName("InboundTender"),
                    targetShapeId: new ShapeId("domain.inboundTender"),
                    key: Expr.Call("key"),
                    entity: Expr.Call("entityId"),
                    assignments:
                    [
                        new FieldAssignment("TenderId", Expr.Field("EdiTenderId")),
                        new FieldAssignment("Shipper", Expr.Field("EdiShipper")),
                        new FieldAssignment("StopCount", Expr.Call("count", Expr.Field("EdiStops")))
                    ])
            ],
            metadata: new RelationMetadata(
                allowCodegen: true,
                deterministic: true,
                hints: ImmutableDictionary<string, string>.Empty));

        var json = RelationJsonMapper.ToJson(definition, indented: true);
        var parsed = RelationJsonMapper.ParseJson(json);
        var reparsedJson = RelationJsonMapper.ToJson(parsed, indented: true);

        var originalNode = JsonNode.Parse(json);
        var reparsedNode = JsonNode.Parse(reparsedJson);
        Assert.True(JsonNode.DeepEquals(originalNode, reparsedNode));

        var relationNode = originalNode?["relation"];
        Assert.NotNull(relationNode);
        Assert.Equal("rel_edi_tender", relationNode!["id"]!["value"]!.GetValue<string>());
        Assert.Equal("EdiTender", relationNode["name"]!["value"]!.GetValue<string>());
        _ = Assert.Single(relationNode["sources"]!.AsArray());
        _ = Assert.Single(relationNode["mappings"]!.AsArray());
        Assert.NotNull(relationNode["mappings"]![0]!["assignments"]);
    }

    [Fact]
    public void ParseJson_AcceptsLegacyTargetFieldWrapper_AndWritesCanonicalString()
    {
        var json = """
            {
              "relation": {
                "id": { "value": "rel_edi_tender" },
                "name": { "value": "EdiTender" },
                "sources": [
                  {
                    "alias": { "value": "src" },
                    "shapeId": { "value": "edi.204" },
                    "cardinality": "Many"
                  }
                ],
                "mappings": [
                  {
                    "id": { "value": "map_tender" },
                    "name": { "value": "InboundTender" },
                    "kind": "Relation",
                    "sourceShapeId": null,
                    "targetShapeId": { "value": "domain.inboundTender" },
                    "assignments": [
                      {
                        "targetField": { "value": "TenderId" },
                        "expr": { "$expr": "field", "path": { "segments": [ { "kind": "Field", "segment": "EdiTenderId" } ] } }
                      }
                    ],
                    "scope": "Rooted",
                    "direction": "SourceToTarget",
                    "nestedMappings": [],
                    "collectionMappings": [],
                    "metadata": { "allowCodegen": true, "deterministic": true, "hints": {} }
                  }
                ],
                "metadata": { "allowCodegen": true, "deterministic": true, "hints": {} }
              }
            }
            """;

        var parsed = RelationJsonMapper.ParseJson(json);
        var mapping = Assert.Single(parsed.Mappings);
        var assignment = Assert.Single(mapping.Assignments);
        Assert.Equal("TenderId", assignment.TargetField);

        var canonicalJson = RelationJsonMapper.ToJson(parsed, indented: false);
        Assert.Contains("\"targetField\":\"TenderId\"", canonicalJson, StringComparison.Ordinal);
        var canonicalMapping = JsonNode.Parse(canonicalJson)?["relation"]?["mappings"]?[0];
        var canonicalMappingObject = Assert.IsType<JsonObject>(canonicalMapping);
        Assert.False(canonicalMappingObject.ContainsKey("kind"));
        Assert.False(canonicalMappingObject.ContainsKey("sourceShapeId"));
        Assert.False(canonicalMappingObject.ContainsKey("direction"));
        Assert.False(canonicalMappingObject.ContainsKey("nestedMappings"));
        Assert.False(canonicalMappingObject.ContainsKey("collectionMappings"));
    }
}
