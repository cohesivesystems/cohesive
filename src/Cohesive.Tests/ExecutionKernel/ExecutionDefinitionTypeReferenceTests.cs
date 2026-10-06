using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.ExecutionKernel.TestFixtures.MotionDq;
using Cohesive.Processes.IR;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionDefinitionTypeReferenceTests
{
    [Fact]
    public void SharedNestedTypesAreEncodedOnceAndDecodedAsSharedInstances()
    {
        var nested = new ObjectTypeRef([new("value", new ArrayTypeRef(new ScalarTypeRef(ScalarTypeKind.String)))]);
        var document = Create(new Types([nested, nested, new ObjectTypeRef(nested.Fields)]));
        Assert.Equal(3, document.Definition.GetProperty("$types").GetArrayLength());
        Assert.All(document.Definition.GetProperty("values").EnumerateArray(), value => Assert.Equal(JsonValueKind.Number, value.ValueKind));
        var decoded = document.GetDefinition<Types>();
        Assert.Same(decoded.Values[0], decoded.Values[1]);
        Assert.Same(decoded.Values[0], decoded.Values[2]);
        Assert.Equal(nested, decoded.Values[0]);
        Assert.Equal(document.Metadata.Fingerprint, Create(decoded).Metadata.Fingerprint);
    }

    [Fact]
    public void TypeNumberingDoesNotDependOnDictionaryInsertionOrder()
    {
        TypeRef text = new ScalarTypeRef(ScalarTypeKind.String);
        TypeRef number = new ScalarTypeRef(ScalarTypeKind.Int32);
        var first = Create(new TypeMap(new() { ["z"] = text, ["a"] = number }));
        var second = Create(new TypeMap(new() { ["a"] = number, ["z"] = text }));
        Assert.Equal(first.Definition.GetRawText(), second.Definition.GetRawText());
        Assert.Equal(first.Metadata.Fingerprint, second.Metadata.Fingerprint);
    }

    [Theory]
    [InlineData("{\"values\":[0]}", "requires")]
    [InlineData("{\"$types\":[],\"values\":[0]}", "out of range")]
    [InlineData("{\"$types\":[],\"values\":[-1]}", "out of range")]
    [InlineData("{\"$types\":[],\"values\":[0.5]}", "integer")]
    [InlineData("{\"$types\":[{\"$type\":\"array\",\"elementType\":0}],\"values\":[0]}", "cycle")]
    [InlineData("{\"$types\":[{\"$type\":\"missing\"}],\"values\":[]}", "unknown type")]
    [InlineData("{\"$types\":[{\"$type\":\"scalar\",\"kind\":\"String\",\"format\":\"None\",\"unexpected\":1}],\"values\":[0]}", "unexpected")]
    public void InvalidReferencesAndUnusedInvalidEntriesAreRejected(string json, string expected)
    {
        var original = Create(new Types([]));
        using var parsed = JsonDocument.Parse(json);
        var document = new ExecutionDefinitionDocument(original.Kind, original.Metadata, parsed.RootElement);
        Assert.Contains(expected, Assert.Throws<JsonException>(() => document.GetDefinition<Types>()).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdmissionRejectsUnusedTableEntriesEvenWithAValidFingerprint()
    {
        var authored = MotionDqMonitoringProcess.AuthorVersion1().Document;
        var body = JsonNode.Parse(authored.Definition.GetRawText())!.AsObject();
        var table = body["$types"]!.AsArray();
        table.Add(table[0]!.DeepClone());
        using var parsed = JsonDocument.Parse(body.ToJsonString());
        var fingerprint = ExecutionDefinitionFingerprinter.Compute(authored.Metadata.SchemaVersion, authored.Kind, parsed.RootElement, authored.Extensions);
        var metadata = new ExecutionDefinitionMetadata(authored.Metadata.DefinitionId, authored.Metadata.RevisionId,
            authored.Metadata.SchemaVersion, fingerprint, authored.Metadata.Provenance, authored.Metadata.DisplayName,
            authored.Metadata.Description, authored.Metadata.SourceMap, authored.Metadata.Diagnostics);
        var changed = new ExecutionDefinitionDocument(authored.Kind, metadata, parsed.RootElement, authored.Extensions);
        var validation = ProcessDefinitionDocuments.Validate(changed);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == ProcessDefinitionDocumentDiagnosticCodes.DefinitionWireNonCanonical);
    }

    [Fact]
    public void LargeRepeatedTypesHaveCompactWireGrowth()
    {
        var nested = new ObjectTypeRef([.. Enumerable.Range(0, 128).Select(index => new ObjectFieldTypeDef($"field{index}", new ScalarTypeRef(ScalarTypeKind.String)))]);
        var single = Create(new Types([nested])).Definition.GetRawText().Length;
        var repeated = Create(new Types([.. Enumerable.Repeat<TypeRef>(nested, 1000)])).Definition.GetRawText().Length;
        Assert.True(repeated < single + 3000, $"Repeated wire size {repeated}, single {single}.");
    }

    static ExecutionDefinitionDocument Create<T>(T value) => ExecutionDefinitionDocument.Create(
        new("test"), new("test/types"), new("revision/1"), value,
        new(new("tests", "1"), new("tests/types", new(["test"])), DocumentOrigin.Compiled));

    public sealed record Types(TypeRef[] Values);
    public sealed record TypeMap(Dictionary<string, TypeRef> Values);
}
