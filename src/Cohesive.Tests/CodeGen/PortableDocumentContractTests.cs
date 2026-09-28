using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cohesive.Adapters.TypeScript;
using Cohesive.Adapters.OpenApi;
using Cohesive.CodeGen;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Tests.CodeGen;

public sealed class PortableDocumentContractTests
{
    [Fact]
    public void NativeDocumentContracts_RequirePublicProjectionAndRetainTypedExpressions()
    {
        Assert.Throws<InvalidOperationException>(() => new ClrShapeGraphBuilder().AddShape<RelationDraftDocument>());
        var graph = new ClrShapeGraphBuilder()
            .UsePublicJsonContracts(StrictDocumentJson.CreateOptions())
            .AddShape<RelationDraftDocument>()
            .AddShape<RelationQueryDocument>()
            .Build(new("native-document-contracts"));
        var emission = new TypeScriptShapeEmitter().Emit(new ShapeCodeGenerationRequest(graph));
        var text = Assert.Single(emission.Documents).Text;
        Assert.Contains("export interface RelationDraftDocument", text, StringComparison.Ordinal);
        Assert.Contains("export interface RelationQueryDocument", text, StringComparison.Ordinal);
        Assert.Contains("readonly $definition: 'relation';\n} & RelationDefinition", text, StringComparison.Ordinal);
        Assert.Contains("export type Expr =", text, StringComparison.Ordinal);
        Assert.Contains("readonly $expr: 'field';", text, StringComparison.Ordinal);
        Assert.Equal(text, Assert.Single(new TypeScriptShapeEmitter()
            .Emit(new ShapeCodeGenerationRequest(graph)).Documents).Text);
    }

    [Fact]
    public void PortableObjectUnionCase_RetainsTheSerializedFlatDiscriminatorLayout()
    {
        var options = StrictDocumentJson.CreateOptions();
        var wire = JsonSerializer.SerializeToElement<Payload>(new DocumentPayload("example"), options);
        Assert.Equal("document", wire.GetProperty("$payload").GetString());
        Assert.Equal("example", wire.GetProperty("name").GetString());
        Assert.False(wire.TryGetProperty("value", out _));

        var graph = new ClrShapeGraphBuilder()
            .AddMetadataProvider(new SystemTextJsonClrShapeMetadataProvider(options))
            .AddShape<Envelope>()
            .Build();
        var text = Assert.Single(new TypeScriptShapeEmitter()
            .Emit(new ShapeCodeGenerationRequest(graph)).Documents).Text;
        Assert.Contains("readonly $payload: 'document';\n} & Record<string, unknown>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("readonly value:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("export interface DocumentPayload", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicProjection_ExpandsNativeDocumentsWithoutChangingAdmission()
    {
        var result = new ClrShapeGraphBuilder()
            .UsePublicJsonContracts(StrictDocumentJson.CreateOptions())
            .AddShape<Review>()
            .BuildResult();
        var text = Assert.Single(new TypeScriptShapeEmitter()
            .Emit(new ShapeCodeGenerationRequest(result.Graph)).Documents).Text;
        Assert.Contains("export interface Review", text, StringComparison.Ordinal);
        Assert.Contains("draft: RelationDraftDocument;", text, StringComparison.Ordinal);
        Assert.Contains("relation?: RelationQueryDocument | null;", text, StringComparison.Ordinal);
        Assert.Contains("export interface RelationDefinition", text, StringComparison.Ordinal);
        Assert.Contains("readonly $definition: 'relation';\n} & RelationDefinition", text, StringComparison.Ordinal);
        Assert.IsType<NamedTypeRef>(result.GetTypeRef(typeof(Review)));
        Assert.IsType<JsonTypeRef>(new Cohesive.Model.Authoring.DefaultClrTypeRefMapper().Map(typeof(Review), null));
    }

    [Fact]
    public void PublicProjection_PreservesRecursiveRecordsAndOpaqueConverterValues()
    {
        var graph = new ClrShapeGraphBuilder()
            .UsePublicJsonContracts(StrictDocumentJson.CreateOptions())
            .AddShape<RecursiveDocument>()
            .Build();
        var text = Assert.Single(new TypeScriptShapeEmitter()
            .Emit(new ShapeCodeGenerationRequest(graph)).Documents).Text;
        Assert.Contains("child?: RecursiveDocument | null;", text, StringComparison.Ordinal);
        Assert.Contains("converted: unknown;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("export interface ConvertedDocument", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicProjection_RejectsUnrepresentableSerializerMetadata()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(Review))
                info.Properties.RemoveAt(0);
        });
        var options = StrictDocumentJson.CreateOptions();
        options.TypeInfoResolver = resolver;
        var builder = new ClrShapeGraphBuilder().UsePublicJsonContracts(options);
        Assert.Throws<NotSupportedException>(() => builder.AddShape<Review>());
    }

    [Fact]
    public void PublicProjection_RejectsContradictoryRootKind()
    {
        var builder = new ClrShapeGraphBuilder().UsePublicJsonContracts(StrictDocumentJson.CreateOptions());
        Assert.Throws<InvalidOperationException>(() => builder.AddShape<InvalidDocument>());
    }

    [Fact]
    public void PublicOpenApi_UsesSerializerMembersAndValidRecursiveReferences()
    {
        var api = Cohesive.Api.Api.Define("Documents")
            .Query("Get").Route("GET", "/documents").Returns<Review>().Done().Build();
        var emitter = new OpenApiEmitter(new()
        {
            JsonSerializerOptions = StrictDocumentJson.CreateOptions()
        });
        var text = Assert.Single(emitter.Emit(api).Documents).Text;
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var schema = root.GetProperty("components").GetProperty("schemas").GetProperty(nameof(Review));
        Assert.True(schema.GetProperty("properties").TryGetProperty("draft", out _));
        var relation = schema.GetProperty("properties").GetProperty("relation");
        Assert.Contains(relation.GetProperty("anyOf").EnumerateArray(),
            item => item.TryGetProperty("type", out var type) && type.GetString() == "null");
        Assert.Contains("$definition", text, StringComparison.Ordinal);
        Assert.Contains("$expr", text, StringComparison.Ordinal);
        Assert.Contains("#/components/schemas/Expr", text, StringComparison.Ordinal);
        ValidateReferences(root, root);
        Assert.Equal(text, Assert.Single(emitter.Emit(api).Documents).Text);
    }

    [Fact]
    public void PublicOpenApi_RejectsContradictoryPortableRootKind()
    {
        var api = Cohesive.Api.Api.Define("Documents")
            .Query("Get").Route("GET", "/documents").Returns<InvalidDocument>().Done().Build();
        Assert.Throws<InvalidOperationException>(() => new OpenApiEmitter(new()
        {
            JsonSerializerOptions = StrictDocumentJson.CreateOptions()
        }).Emit(api));
    }

    [Fact]
    public void PublicTypeScript_IsolatesNestedConverterProfilesForTheSameRecursiveType()
    {
        var builder = new ClrShapeGraphBuilder()
            .UsePublicJsonContracts(new JsonSerializerOptions())
            .AddShape<MixedProfiles>();
        var graph = builder.Build();
        var root = Assert.Single(graph.Shapes);
        var plain = Assert.IsType<NamedTypeRef>(Assert.Single(root.Fields, field => field.Name.Value == "Plain").Type);
        var web = Assert.IsType<NamedTypeRef>(Assert.Single(root.Fields, field => field.Name.Value == "Web").Type);
        Assert.NotEqual(plain.TypeId, web.TypeId);
        var plainDefinition = Assert.IsType<TypeDefinition.Structural>(Assert.Single(graph.NamedTypes, type => type.Id == plain.TypeId));
        var webDefinition = Assert.IsType<TypeDefinition.Structural>(Assert.Single(graph.NamedTypes, type => type.Id == web.TypeId));
        Assert.Contains(plainDefinition.Fields, field => field.Name.Value == "Name");
        Assert.Contains(webDefinition.Fields, field => field.Name.Value == "name");
        Assert.DoesNotContain(webDefinition.Fields, field => field.Name.Value == "Name");
        var text = Assert.Single(new TypeScriptShapeEmitter()
            .Emit(new ShapeCodeGenerationRequest(graph)).Documents).Text;
        Assert.Contains("name: string;", text, StringComparison.Ordinal);
        var declarations = System.Text.RegularExpressions.Regex.Matches(text, @"export (?:interface|type) (\w+)")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(declarations.Length, declarations.Distinct(StringComparer.Ordinal).Count());
        var child = Assert.Single(webDefinition.Fields, field => field.Name.Value == "child");
        Assert.Equal(web, child.Type);
        var rebuilt = builder.Build();
        Assert.Same(webDefinition, Assert.Single(rebuilt.NamedTypes, type => type.Id == web.TypeId));
        Assert.Equal(text, Assert.Single(new TypeScriptShapeEmitter()
            .Emit(new ShapeCodeGenerationRequest(rebuilt)).Documents).Text);
    }

    [Fact]
    public void PublicOpenApi_IsolatesNestedConverterProfilesForTheSameRecursiveType()
    {
        var options = new JsonSerializerOptions();
        var value = new MixedProfiles(new("outer", null), new("inner", new("child", null)));
        var wire = JsonSerializer.SerializeToElement(value, options);
        Assert.True(wire.GetProperty("Plain").TryGetProperty("Name", out _));
        Assert.True(wire.GetProperty("Web").TryGetProperty("name", out _));
        var api = Cohesive.Api.Api.Define("Profiles")
            .Query("Get").Route("GET", "/profiles").Returns<MixedProfiles>().Done().Build();
        var emitter = new OpenApiEmitter(new() { JsonSerializerOptions = options });
        var text = Assert.Single(emitter.Emit(api).Documents).Text;
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        ValidateReferences(root, root);
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var properties = schemas.GetProperty(nameof(MixedProfiles)).GetProperty("properties");
        var plainRef = properties.GetProperty("Plain").GetProperty("$ref").GetString()!;
        var webRef = properties.GetProperty("Web").GetProperty("$ref").GetString()!;
        Assert.NotEqual(plainRef, webRef);
        var plain = schemas.GetProperty(plainRef.Split('/')[^1]).GetProperty("properties");
        var web = schemas.GetProperty(webRef.Split('/')[^1]).GetProperty("properties");
        Assert.True(plain.TryGetProperty("Name", out _));
        Assert.False(plain.TryGetProperty("name", out _));
        Assert.True(web.TryGetProperty("name", out _));
        Assert.False(web.TryGetProperty("Name", out _));
        Assert.Contains(web.GetProperty("child").GetProperty("anyOf").EnumerateArray(),
            item => item.TryGetProperty("$ref", out var reference) && reference.GetString() == webRef);
        Assert.Equal(text, Assert.Single(emitter.Emit(api).Documents).Text);
    }

    [Fact]
    public void PublicTypeScript_ProjectsScalarAndCollectionPropertyProfiles()
    {
        var graph = new ClrShapeGraphBuilder()
            .UsePublicJsonContracts(new JsonSerializerOptions())
            .AddShape<ProfileCollections>().Build();
        var text = Assert.Single(new TypeScriptShapeEmitter()
            .Emit(new ShapeCodeGenerationRequest(graph)).Documents).Text;
        Assert.Contains("Text: string;", text, StringComparison.Ordinal);
        Assert.Contains("name: string;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("export interface ProfileValue", text, StringComparison.Ordinal);
        var root = Assert.Single(graph.Shapes);
        Assert.Equal(FieldCardinality.Many, Assert.Single(root.Fields, field => field.Name.Value == "Items").Cardinality);
    }

    sealed record ProfileCollections(
        [property: JsonConverter(typeof(WebJsonPropertyConverter<string>))] string Text,
        [property: JsonConverter(typeof(WebJsonPropertyConverter<ProfileNode[]>))] ProfileNode[] Items);

    [Fact]
    public void PropertyCodeConverter_ProjectsOpenStringOutputIncludingUndefinedValues()
    {
        var options = new JsonSerializerOptions();
        Assert.Equal("M", JsonSerializer.SerializeToElement(new CodeEnvelope(Requirement.Required), options)
            .GetProperty("Value").GetString());
        Assert.Equal("99", JsonSerializer.SerializeToElement(new CodeEnvelope((Requirement)99), options)
            .GetProperty("Value").GetString());
        var graph = new ClrShapeGraphBuilder().UsePublicJsonContracts(options).AddShape<CodeEnvelope>().Build();
        var text = Assert.Single(new TypeScriptShapeEmitter().Emit(new ShapeCodeGenerationRequest(graph)).Documents).Text;
        Assert.Contains("Value: string;", text, StringComparison.Ordinal);
        var api = Cohesive.Api.Api.Define("Codes").Query("Get").Route("GET", "/codes").Returns<CodeEnvelope>().Done().Build();
        using var document = JsonDocument.Parse(Assert.Single(new OpenApiEmitter(new() { JsonSerializerOptions = options }).Emit(api).Documents).Text);
        var value = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(nameof(CodeEnvelope)).GetProperty("properties").GetProperty("Value");
        Assert.Equal("string", value.GetProperty("type").GetString());
        Assert.False(value.TryGetProperty("enum", out _));
    }

    public enum Requirement { [Cohesive.Domain.Code("M")] Required }
    sealed record CodeEnvelope([property: JsonConverter(typeof(Cohesive.Domain.CodeEnumJsonConverter<Requirement>))] Requirement Value);

    sealed record ProfileNode(string Name, ProfileNode? Child);
    sealed record MixedProfiles(ProfileNode Plain,
        [property: JsonConverter(typeof(WebJsonPropertyConverter<ProfileNode>))] ProfileNode Web,
        [property: JsonConverter(typeof(WebJsonPropertyConverter<ProfileNode>))] ProfileNode? Other = null);

    static void ValidateReferences(JsonElement node, JsonElement root)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("$ref", out var reference))
            {
                var target = root;
                var path = reference.GetString()!;
                Assert.StartsWith("#/components/schemas/", path, StringComparison.Ordinal);
                foreach (var segment in path[2..].Split('/'))
                {
                    var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                    target = target.ValueKind == JsonValueKind.Array
                        ? target[int.Parse(name, System.Globalization.CultureInfo.InvariantCulture)]
                        : target.GetProperty(name);
                }
            }
            foreach (var property in node.EnumerateObject())
                ValidateReferences(property.Value, root);
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                ValidateReferences(item, root);
        }
    }

    [PortableJsonValue(JsonTypeKind.Object)]
    sealed record Review(RelationDraftDocument Draft, RelationQueryDocument? Relation);

    [PortableJsonValue(JsonTypeKind.Object)]
    sealed record RecursiveDocument(RecursiveDocument? Child, ConvertedDocument Converted);

    [PortableJsonValue(JsonTypeKind.String)]
    sealed record InvalidDocument(string Name);

    [PortableJsonValue(JsonTypeKind.String), JsonConverter(typeof(ConvertedDocumentConverter))]
    sealed record ConvertedDocument(string Name);

    sealed class ConvertedDocumentConverter : JsonConverter<ConvertedDocument>
    {
        public override ConvertedDocument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, ConvertedDocument value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Name);
    }

    sealed record Envelope(Payload Payload);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$payload")]
    [JsonDerivedType(typeof(DocumentPayload), "document")]
    abstract record Payload;

    [PortableJsonValue(JsonTypeKind.Object)]
    sealed record DocumentPayload(string Name) : Payload;
}
