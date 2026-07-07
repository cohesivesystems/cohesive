using System.Text.Json.Nodes;
using Cohesive.Transitions.Model;
using Cohesive.Host.Configuration;

namespace Cohesive.Tests.Modeling;

/// <summary>
/// Tests for external JSON/YAML domain model authoring.
/// </summary>
public sealed class DomainModelExternalDslTests
{
    [Fact]
    public void ParseJson_AndParseYaml_RoundTripDomainModel()
    {
        var model = BuildModel();

        var json = DomainModelExternalDsl.ToJson(model: model);
        var fromJson = DomainModelExternalDsl.Parse(text: json, format: DomainModelExternalDslFormat.Json);
        var yaml = DomainModelExternalDsl.ToYaml(model: model);
        var fromYaml = DomainModelExternalDsl.Parse(text: yaml, format: DomainModelExternalDslFormat.Yaml);

        Assert.Single(collection: fromJson.Entities);
        Assert.Single(collection: fromYaml.Entities);

        var jsonTransition = Assert.Single(fromJson.Entities[0].Transitions);
        var yamlTransition = Assert.Single(fromYaml.Entities[0].Transitions);

        Assert.Equal(expected: "AssignCarrier", actual: jsonTransition.Name);
        Assert.Equal(expected: "AssignCarrier", actual: yamlTransition.Name);
        Assert.Equal(expected: "carrierId", actual: Assert.Single(jsonTransition.Inputs).Name);
        Assert.Equal(expected: "carrierId", actual: Assert.Single(yamlTransition.Inputs).Name);

        AssertJsonEqual("""{"source":"external-dsl"}""", fromJson.Annotations[new AnnotationKey("model.meta")].Value);
        AssertJsonEqual("""{"source":"external-dsl"}""", fromYaml.Annotations[new AnnotationKey("model.meta")].Value);
        AssertJsonEqual("""{"enabled":true}""", jsonTransition.Annotations[new AnnotationKey("transition.audit")].Value);
        AssertJsonEqual("""{"enabled":true}""", yamlTransition.Annotations[new AnnotationKey("transition.audit")].Value);

        var jsonDistanceField = Assert.Single(fromJson.Entities[0].Fields.Where(x => x.Name.Value == "PlannedDistance"));
        var yamlDistanceField = Assert.Single(fromYaml.Entities[0].Fields.Where(x => x.Name.Value == "PlannedDistance"));
        Assert.Equal("Distance", Assert.IsType<QuantityTypeRef>(jsonDistanceField.Type).Quantity);
        Assert.Equal("Distance", Assert.IsType<QuantityTypeRef>(yamlDistanceField.Type).Quantity);
    }

    [Fact]
    public void Parse_AutoDetectsJsonAndYaml()
    {
        var model = BuildModel();
        var json = DomainModelExternalDsl.ToJson(model: model, indented: false);
        var yaml = DomainModelExternalDsl.ToYaml(model: model);

        var fromJson = DomainModelExternalDsl.Parse(text: json);
        var fromYaml = DomainModelExternalDsl.Parse(text: yaml);

        Assert.Equal(expected: model.Entities[0].Name, actual: fromJson.Entities[0].Name);
        Assert.Equal(expected: model.Entities[0].Name, actual: fromYaml.Entities[0].Name);
    }

    [Fact]
    public void ParseJson_AcceptsLegacyFieldWrapperObjects_AndWritesCanonicalStrings()
    {
        var json = """
            {
              "entities": [
                {
                  "name": { "value": "Order" },
                  "fields": [
                    {
                      "id": { "value": "fld_order_status" },
                      "name": { "value": "Status" },
                      "type": { "$type": "scalar", "kind": "String", "format": "None" },
                      "cardinality": "Single",
                      "presence": "Required",
                      "role": "Data",
                      "nullability": "NonNullable",
                      "mutability": "Mutable",
                      "constraints": [ { "$constraint": "required" } ],
                      "annotations": {}
                    },
                    {
                      "id": { "value": "fld_order_carrier_id" },
                      "name": { "value": "CarrierId" },
                      "type": { "$type": "scalar", "kind": "String", "format": "None" },
                      "cardinality": "Single",
                      "presence": "Optional",
                      "role": "Data",
                      "nullability": "Nullable",
                      "mutability": "Mutable",
                      "constraints": [],
                      "annotations": {}
                    }
                  ],
                  "transitions": [
                    {
                      "name": "AssignCarrier",
                      "updates": [
                        {
                          "field": { "value": "fld_order_carrier_id" },
                          "valueExpression": { "$expr": "constant", "value": "carrier-1" }
                        }
                      ],
                      "readSet": [ { "value": "fld_order_status" } ],
                      "writeSet": [ { "value": "fld_order_carrier_id" } ]
                    }
                  ]
                }
              ]
            }
            """;

        var model = DomainModelExternalDsl.ParseJson(json);
        var transition = Assert.Single(Assert.Single(model.Entities).Transitions);
        Assert.Equal("fld_order_carrier_id", Assert.Single(transition.Updates).Field);
        Assert.Equal(["fld_order_carrier_id", "fld_order_status"], transition.ReadSet.ToArray());
        Assert.Equal(["fld_order_carrier_id"], transition.WriteSet.ToArray());

        var canonicalJson = DomainModelExternalDsl.ToJson(model, indented: false);
        Assert.Contains("\"field\":\"fld_order_carrier_id\"", canonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"readSet\":[\"fld_order_carrier_id\",\"fld_order_status\"]", canonicalJson, StringComparison.Ordinal);
        Assert.Contains("\"writeSet\":[\"fld_order_carrier_id\"]", canonicalJson, StringComparison.Ordinal);
    }

    static DomainModelDefinition BuildModel()
    {
        return DomainModelDsl.Define(domain => domain
            .Version(version: "2026-02-09")
            .Annotation("model.meta", new
            {
                source = "external-dsl"
            })
            .Entity(name: "Order", order => order
                .Field(name: "Id", type: DomainTypes.String(), f => f.WriteOnce())
                .Field(name: "Status", type: DomainTypes.Enum(name: "OrderStatus", members: ["Draft", "Assigned"]))
                .Field(name: "CarrierId", type: DomainTypes.String(), f => f.Optional())
                .Field(name: "PlannedDistance", type: DomainTypes.Quantity("Distance"))
                .Transition(
                    name: "AssignCarrier",
                    t => t
                        .Annotation("transition.audit", new
                        {
                            enabled = true
                        })
                        .Parameter(name: "carrierId", type: DomainTypes.String(), isRequired: true)
                        .Requires(name: "StatusMustBeDraft", expression: Expr.Eq(left: Expr.Field("Status"), right: Expr.Const(value: "Draft")))
                        .Set("CarrierId", Expr.Param(name: "carrierId"))
                        .Set("Status", Expr.Const(value: "Assigned")))));
    }

    static void AssertJsonEqual(string expectedJson, JsonNode? actualNode) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expectedJson), actualNode),
            $"Expected JSON-equivalent values.{Environment.NewLine}Expected: {expectedJson}{Environment.NewLine}Actual: {actualNode?.ToJsonString() ?? "null"}");
}
