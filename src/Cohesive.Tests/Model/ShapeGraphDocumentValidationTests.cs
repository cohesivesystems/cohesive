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
    public void ShapeGraphDocumentSemanticValidator_RejectsDefaultGraphIdentity()
    {
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            id: default,
            shapes: [new Shape(new("shape"), [])]));

        var result = ShapeGraphDocumentSemanticValidator.Validate(document);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "shapeGraph.id.missing");
    }

    [Fact]
    public void Shape_RejectsBypassedEmptyFieldIdentityAtConstructionBoundary()
    {
        var malformed = new FieldDefinition(
            new("value"),
            new ScalarTypeRef(ScalarTypeKind.String)) with
        {
            Name = default
        };

        var exception = Assert.Throws<ArgumentException>(() => new Shape(
            new("shape.invalid-field-name"),
            [malformed]));

        Assert.Equal("fields", exception.ParamName);
    }

    [Fact]
    public void ShapeGraphDocumentSemanticValidator_RejectsInvalidFieldValueMetadata()
    {
        var field = new FieldDefinition(
            new("value"),
            new ScalarTypeRef(ScalarTypeKind.String)) with
        {
            Type = null!,
            Cardinality = (FieldCardinality)997,
            Presence = (FieldPresence)998,
            Nullability = (FieldNullability)999
        };
        var graph = new ShapeGraph(
            new("graph.invalid-fields"),
            [new Shape(new("shape.invalid-fields"), [field])]);

        var result = ShapeGraphDocumentSemanticValidator.Validate(
            ShapeGraphDocument.FromGraph(graph));

        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "shapeGraph.field.type.missing");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "shapeGraph.field.cardinality.invalid");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "shapeGraph.field.presence.invalid");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "shapeGraph.field.nullability.invalid");
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

    [Fact]
    public void FieldMetadataFailuresKeepExactMessagesEscapedLocationsAndDeclarationOrder()
    {
        var shapeField = new FieldDefinition(new("field /é"), new ScalarTypeRef(ScalarTypeKind.String)) with
        { Type = null!, Cardinality = (FieldCardinality)997, Presence = (FieldPresence)998, Nullability = (FieldNullability)999 };
        var namedField = new StructuralField(new("field /é"), new ScalarTypeRef(ScalarTypeKind.String)) with
        { Type = null!, Cardinality = (FieldCardinality)997, Presence = (FieldPresence)998, Nullability = (FieldNullability)999 };
        var graph = new ShapeGraph(new("graph"), [new(new("shape /é"), [shapeField])],
            [new TypeDefinition.Structural(new("type /é"), [namedField])]);
        var result = ShapeGraphDocumentSemanticValidator.Validate(ShapeGraphDocument.FromGraph(graph));
        var expected = new List<DocumentValidationDiagnostic>();
        foreach (var (subject, location) in new[]
        {
            ("Shape 'shape /é' field 'field /é'", "/graph/shapes/shape%20%2F%C3%A9/fields/field%20%2F%C3%A9"),
            ("Structural type 'type /é' field 'field /é'", "/graph/namedTypes/type%20%2F%C3%A9/fields/field%20%2F%C3%A9")
        })
        {
            expected.Add(new("shapeGraph.field.type.missing", DiagnosticSeverity.Error, $"{subject} must declare a semantic type.", location + "/type"));
            expected.Add(new("shapeGraph.field.cardinality.invalid", DiagnosticSeverity.Error, $"{subject} has unsupported cardinality '997'.", location + "/cardinality"));
            expected.Add(new("shapeGraph.field.presence.invalid", DiagnosticSeverity.Error, $"{subject} has unsupported presence '998'.", location + "/presence"));
            expected.Add(new("shapeGraph.field.nullability.invalid", DiagnosticSeverity.Error, $"{subject} has unsupported nullability '999'.", location + "/nullability"));
        }
        Assert.Equal(expected, result.Diagnostics.Where(d => d.Code.StartsWith("shapeGraph.field.", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(128, true)]
    [InlineData(4096, false)]
    [InlineData(4096, true)]
    public void SuccessfulFieldMetadataValidationHasConstantAllocation(int fields, bool named)
    {
        var document = CreateMetadataDocument(fields, named);
        for (var i = 0; i < 8; i++) Assert.True(ShapeGraphDocumentSemanticValidator.Validate(document).IsValid);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            var result = ShapeGraphDocumentSemanticValidator.Validate(document);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.True(result.IsValid);
        }
        Array.Sort(samples);
        // Includes the validation result boundary; no per-field strings, paths or cached admission.
        Assert.InRange(samples[3], 0, 1024);
    }

    static ShapeGraphDocument CreateMetadataDocument(int fields, bool named)
    {
        var scalar = new ScalarTypeRef(ScalarTypeKind.String);
        return named
            ? ShapeGraphDocument.FromGraph(new(new("graph"), [new(new("shape"), [new(new("value"), new NamedTypeRef(new("type /é")))])],
                [new TypeDefinition.Structural(new("type /é"), [..Enumerable.Range(0, fields).Select(i => new StructuralField(new("field /é" + i), scalar))])]))
            : ShapeGraphDocument.FromGraph(new(new("graph"), [new(new("shape /é"),
                [..Enumerable.Range(0, fields).Select(i => new FieldDefinition(new("field /é" + i), scalar))]) ]));
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
