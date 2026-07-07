using System.Text.Json;
using Cohesive.Adapters.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class ShapeGraphDocumentValidationTests
{
    [Fact]
    public void ShapeGraphDocumentJsonSchemaArtifact_IsCurrent()
    {
        var provider = ShapeGraphDocumentJsonSchemaProvider.Instance;
        var schemaPath = Path.Combine(
            FindRepoRoot(),
            "src",
            "adapters",
            "Cohesive.Adapters.Json",
            "Schemas",
            provider.FileName);

        AssertSchemaArtifactCurrent(provider, schemaPath);
    }

    [Fact]
    public void ShapeGraphDocumentStructuralValidator_RejectsMissingGraph()
    {
        var result = ShapeGraphDocumentStructuralValidator.ValidateJson(
            """{"schemaVersion":"shape-graph/v1"}""");

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Code.Contains("required", StringComparison.Ordinal));
    }

    [Fact]
    public void ShapeGraphDocumentValidator_RejectsMissingNamedTypeReference()
    {
        var graph = new ShapeGraph(
            id: new("graph.invalid"),
            shapes:
            [
                new(
                    id: new("shape.invalid"),
                    fields:
                    [
                        new(
                            name: new("missing"),
                            type: new NamedTypeRef(new TypeId("type.missing")))
                    ])
            ]);

        var document = ShapeGraphDocument.FromGraph(graph);
        var json = JsonSerializer.Serialize(document, CreateJsonOptions());

        var result = ShapeGraphDocumentValidator.ValidateJson(json, CreateJsonOptions());

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, x => x.Code == "shapeGraph.type.ref.missing");
    }

    [Fact]
    public void ShapeGraphDocumentValidator_AcceptsValidShapeGraphDocument()
    {
        var graph = new ShapeGraph(
            id: new("graph.order"),
            shapes:
            [
                new(
                    id: new("shape.order"),
                    fields:
                    [
                        new(
                            name: new("orderNumber"),
                            type: new ScalarTypeRef(ScalarTypeKind.String))
                    ])
            ]);

        var document = ShapeGraphDocument.FromGraph(graph);
        var json = JsonSerializer.Serialize(document, CreateJsonOptions());

        var result = ShapeGraphDocumentValidator.ValidateJson(json, CreateJsonOptions());

        Assert.True(result.IsValid);
        Assert.Empty(result.Diagnostics);
    }

    static JsonSerializerOptions CreateJsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    static void AssertSchemaArtifactCurrent(IJsonSchemaProvider provider, string schemaPath)
    {
        var expected = JsonSchemaExporter.ToJson(provider.Schema);

        if (Environment.GetEnvironmentVariable("COHESIVE_UPDATE_JSON_SCHEMAS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(schemaPath)!);
            File.WriteAllText(schemaPath, expected);
        }

        Assert.True(File.Exists(schemaPath), $"Missing schema artifact '{schemaPath}'. Run with COHESIVE_UPDATE_JSON_SCHEMAS=1 to create it.");
        Assert.Equal(expected, File.ReadAllText(schemaPath));
    }

    static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cohesive.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
