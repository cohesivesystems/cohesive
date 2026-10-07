using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.MotionDq;
using Cohesive.Model.Serialization;
using Cohesive.Model;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionDefinitionTypeReferenceTests(Xunit.Abstractions.ITestOutputHelper output)
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

    [Fact]
    public void EqualScalarInstancesReusePreparationWithoutConflatingFormats()
    {
        var values = Enumerable.Range(0, 512).Select(_ => (TypeRef)new ScalarTypeRef(ScalarTypeKind.String)).ToArray();
        var shared = new Types([.. Enumerable.Repeat(values[0], values.Length)]);
        var distinct = new Types(values);
        for (var iteration = 0; iteration < 16; iteration++)
        {
            _ = Create(shared);
            _ = Create(distinct);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sharedDocument = Create(shared);
        var sharedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var distinctDocument = Create(distinct);
        var distinctBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"512 scalar uses: shared {sharedBytes} B, distinct equal instances {distinctBytes} B.");
        Assert.Equal(sharedDocument.Definition.GetRawText(), distinctDocument.Definition.GetRawText());
        Assert.Equal(sharedDocument.Metadata.Fingerprint, distinctDocument.Metadata.Fingerprint);
        // New identities require dictionary slots, but must not each create a JSON entry/tree.
        Assert.InRange(distinctBytes - sharedBytes, 0, 100_000);
        var formatted = Create(new Types([new ScalarTypeRef(ScalarTypeKind.String),
            new ScalarTypeRef(ScalarTypeKind.String, PrimitiveFormat.Uuid)]));
        Assert.Equal(2, formatted.Definition.GetProperty("$types").GetArrayLength());
    }

    [Fact]
    public void ParentPreparationAvoidsProvisionalOwnedCanonicalDocuments()
    {
        var type = new ObjectTypeRef([.. Enumerable.Range(0, 128).Select(index =>
            new ObjectFieldTypeDef($"field{index}", new ScalarTypeRef(ScalarTypeKind.String)))]);
        var declaration = new Types([type, type]);
        for (var i = 0; i < 16; i++) _ = Create(declaration);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var document = Create(declaration);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"128-field parent preparation: {allocated} B.");
        Assert.Equal(2, document.Definition.GetProperty("$types").GetArrayLength());
        Assert.InRange(allocated, 1, 425_000);
    }

    [Fact]
    public void ParentKeysDeduplicateCanonicalNumericAnnotationsAndKeepFinalChildNumbers()
    {
        var firstAnnotation = JsonSerializer.Deserialize<AnnotationValue>("{\"z\":1.0000,\"a\":[0,2]}")!;
        var secondAnnotation = JsonSerializer.Deserialize<AnnotationValue>("{\"a\":[0.0,2e0],\"z\":1e0}")!;
        var first = new ObjectTypeRef([new("value", new ScalarTypeRef(ScalarTypeKind.String),
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(new("evidence"), firstAnnotation))]);
        var second = new ObjectTypeRef([new("value", new ScalarTypeRef(ScalarTypeKind.String),
            annotations: ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(new("evidence"), secondAnnotation))]);
        var document = Create(new Types([first, second, new ScalarTypeRef(ScalarTypeKind.Bool)]));
        // Bool sorts ahead of String, forcing the parent to use a final child index different
        // from its provisional one. Numeric annotation values are ordinary data, not references.
        Assert.Equal(3, document.Definition.GetProperty("$types").GetArrayLength());
        var decoded = document.GetDefinition<Types>();
        Assert.Same(decoded.Values[0], decoded.Values[1]);
        var field = Assert.Single(Assert.IsType<ObjectTypeRef>(decoded.Values[0]).Fields);
        Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(field.Type).Kind);
        Assert.Equal("{\"a\":[0,2],\"z\":1}", field.Annotations[new("evidence")].Value.GetRawText());
        Assert.Equal(document.Definition.GetRawText(), Create(decoded).Definition.GetRawText());
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

    [Fact]
    public void DirectMetadataPreservesCanonicalScalarWire()
    {
        var document = Create(new Types([new ScalarTypeRef(ScalarTypeKind.String)]));
        Assert.Equal("{\"$types\":[{\"$type\":\"scalar\",\"format\":\"None\",\"kind\":\"String\"}],\"values\":[0]}",
            document.Definition.GetRawText());
    }

    [Fact]
    public void ConcurrentCodecLeasesKeepDocumentTablesIsolated()
    {
        Parallel.For(0, 32, index =>
        {
            var kind = index % 2 == 0 ? ScalarTypeKind.String : ScalarTypeKind.Int32;
            var document = Create(new Payload(index.ToString(), [new ScalarTypeRef(kind)]));
            var decoded = document.GetDefinition<Payload>();
            Assert.Equal(index.ToString(), decoded.Text);
            Assert.Equal(kind, Assert.IsType<ScalarTypeRef>(Assert.Single(decoded.Values)).Kind);
        });
    }

    [Fact]
    public void SuccessfulProjectionDoesNotMaterializeFilteredJsonTrees()
    {
        var nested = new ObjectTypeRef([.. Enumerable.Range(0, 128).Select(index =>
            new ObjectFieldTypeDef($"field{index}", new ScalarTypeRef(ScalarTypeKind.String)))]);
        var document = Create(new Payload(new string('x', 100_000), [nested, nested]));
        // Warm every retained codec; another test may have populated the bounded lease pool.
        for (var iteration = 0; iteration < 16; iteration++)
            _ = document.GetDefinition<Payload>();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var decoded = document.GetDefinition<Payload>();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Direct projection allocated {allocated} bytes.");
        Assert.Equal(100_000, decoded.Text.Length);
        Assert.Same(decoded.Values[0], decoded.Values[1]);
        // Includes the retained 100 KB text and typed fields; the old filtered-tree path used 541 KB.
        Assert.InRange(allocated, 0, 400_000);
    }

    [Fact]
    public void MetadataAllowanceIsScopedToTheDefinitionRoot()
    {
        var original = Create(new Nested(new Leaf("value")));
        using var parsed = JsonDocument.Parse("{\"$types\":[],\"value\":{\"text\":\"value\",\"$types\":[]}}");
        var changed = new ExecutionDefinitionDocument(original.Kind, original.Metadata, parsed.RootElement);
        Assert.Throws<JsonException>(() => changed.GetDefinition<Nested>());
    }

    [Fact]
    public void AuthoredReservedTablePropertyIsRejected()
    {
        Assert.Throws<JsonException>(() => Create(new Reserved([])));
    }

    public sealed record Payload(string Text, TypeRef[] Values);
    public sealed record Nested(Leaf Value);
    public sealed record Leaf(string Text);
    public sealed record Reserved([property: System.Text.Json.Serialization.JsonPropertyName("$types")] int[] Values);

    [Theory]
    [InlineData("{\"$types\":[],\"$root\":\"unknown\"}", "Unknown")]
    [InlineData("{\"$types\":[]}", "requires")]
    [InlineData("{\"$types\":[],\"$root\":17}", "Unknown")]
    public void RootDispatchRejectsMissingUnknownAndWrongKindTags(string json, string expected)
    {
        var original = Create<DispatchRoot>(new KnownRoot("value"));
        using var parsed = JsonDocument.Parse(json);
        var document = new ExecutionDefinitionDocument(original.Kind, original.Metadata, parsed.RootElement);
        Assert.Contains(expected, Assert.Throws<JsonException>(() => document.GetDefinition<DispatchRoot>()).Message);
    }

    [Fact]
    public void RootDispatchRejectsUnregisteredConcreteTypes()
    {
        Assert.Contains("Unsupported", Assert.Throws<JsonException>(() => Create<DispatchRoot>(new OtherRoot("value"))).Message);
    }

    [System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$root",
        UnknownDerivedTypeHandling = System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(KnownRoot), "known")]
    public abstract record DispatchRoot;
    public sealed record KnownRoot(string Text) : DispatchRoot;
    public sealed record OtherRoot(string Text) : DispatchRoot;

    [Theory]
    [InlineData("{\"$types\":[]}", "requires")]
    [InlineData("{\"$types\":[],\"$definition\":\"unknown\"}", "Unknown")]
    public void RelationQueryRootRejectsMissingAndUnknownTags(string json, string expected)
    {
        var original = Create(new Types([]));
        using var parsed = JsonDocument.Parse(json);
        var document = new ExecutionDefinitionDocument(original.Kind, original.Metadata, parsed.RootElement);
        Assert.Contains(expected, Assert.Throws<JsonException>(() =>
            document.GetDefinition<Cohesive.Relations.IR.RelationQueryDefinition>()).Message);
    }

    [Fact]
    public void CachedRootDispatchAllocatesNothingForStringAndIntegerTags()
    {
        var executionTypes = typeof(ExecutionDefinitionDocument).Assembly.GetType("Cohesive.Execution.ExecutionDefinitionTypes")!;
        var codecType = executionTypes.GetNestedType("Codec", System.Reflection.BindingFlags.NonPublic)!;
        using var codec = (IDisposable)Activator.CreateInstance(codecType, nonPublic: true)!;
        var dispatchMethod = codecType.GetMethod("Dispatch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var (root, json) in new[] { (typeof(DispatchRoot), "{\"$root\":\"known\"}"), (typeof(IntegerDispatchRoot), "{\"$root\":17}") })
        {
            var dispatch = dispatchMethod.Invoke(codec, [root])!;
            var resolve = dispatch.GetType().GetMethod("Resolve", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.CreateDelegate<Func<JsonElement, Type>>(dispatch);
            using var parsed = JsonDocument.Parse(json);
            for (var index = 0; index < 16; index++) _ = resolve(parsed.RootElement);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 128; index++) _ = resolve(parsed.RootElement);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    [System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$root")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(IntegerKnownRoot), 17)]
    public abstract record IntegerDispatchRoot;
    public sealed record IntegerKnownRoot(string Text) : IntegerDispatchRoot;

    static ExecutionDefinitionDocument Create<T>(T value) => ExecutionDefinitionDocument.Create(
        new("test"), new("test/types"), new("revision/1"), value,
        new(new("tests", "1"), new("tests/types", new(["test"])), DocumentOrigin.Compiled));

    public sealed record Types(TypeRef[] Values);
    public sealed record TypeMap(Dictionary<string, TypeRef> Values);
}
