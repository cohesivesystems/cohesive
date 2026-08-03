using System.Text.Json.Nodes;
using Cohesive.Host.Configuration;
using Cohesive.Transitions.Model;

namespace Cohesive.Tests.Modeling;

/// <summary>
/// Tests for external JSON/YAML entity-shape model authoring.
/// </summary>
public sealed class DomainModelExternalDslTests
{
    [Fact]
    public void ParseJson_AndParseYaml_RoundTripDomainModel()
    {
        var model = BuildModel();

        var json = DomainModelExternalDsl.ToJson(model);
        var fromJson = DomainModelExternalDsl.Parse(json, DomainModelExternalDslFormat.Json);
        var yaml = DomainModelExternalDsl.ToYaml(model);
        var fromYaml = DomainModelExternalDsl.Parse(yaml, DomainModelExternalDslFormat.Yaml);

        var jsonEntity = Assert.Single(fromJson.Entities);
        var yamlEntity = Assert.Single(fromYaml.Entities);

        AssertJsonEqual(
            """{"source":"external-dsl"}""",
            fromJson.Annotations[new AnnotationKey("model.meta")].Value);
        AssertJsonEqual(
            """{"source":"external-dsl"}""",
            fromYaml.Annotations[new AnnotationKey("model.meta")].Value);

        var jsonDistanceField = Assert.Single(jsonEntity.Fields.Where(
            static field => field.Name.Value == "PlannedDistance"));
        var yamlDistanceField = Assert.Single(yamlEntity.Fields.Where(
            static field => field.Name.Value == "PlannedDistance"));
        Assert.Equal("Distance", Assert.IsType<QuantityTypeRef>(jsonDistanceField.Type).Quantity);
        Assert.Equal("Distance", Assert.IsType<QuantityTypeRef>(yamlDistanceField.Type).Quantity);
    }

    [Fact]
    public void Parse_AutoDetectsJsonAndYaml()
    {
        var model = BuildModel();
        var json = DomainModelExternalDsl.ToJson(model, indented: false);
        var yaml = DomainModelExternalDsl.ToYaml(model);

        var fromJson = DomainModelExternalDsl.Parse(json);
        var fromYaml = DomainModelExternalDsl.Parse(yaml);

        Assert.Equal(model.Entities[0].Name, fromJson.Entities[0].Name);
        Assert.Equal(model.Entities[0].Name, fromYaml.Entities[0].Name);
    }

    static DomainModelDefinition BuildModel() =>
        DomainModelDsl.Define(domain => domain
            .Version("2026-02-09")
            .Annotation("model.meta", new
            {
                source = "external-dsl"
            })
            .Entity(name: "Order", order => order
                .Field(name: "Id", type: DomainTypes.String(), field => field.WriteOnce())
                .Field(name: "Status", type: DomainTypes.Enum(
                    name: "OrderStatus",
                    members: ["Draft", "Assigned"]))
                .Field(name: "CarrierId", type: DomainTypes.String(), field => field.Optional())
                .Field(name: "PlannedDistance", type: DomainTypes.Quantity("Distance"))));

    static void AssertJsonEqual(string expectedJson, JsonNode? actualNode) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expectedJson), actualNode),
            $"Expected JSON-equivalent values.{Environment.NewLine}Expected: {expectedJson}{Environment.NewLine}Actual: {actualNode?.ToJsonString() ?? "null"}");
}
