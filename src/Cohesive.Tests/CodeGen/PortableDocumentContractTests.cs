using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Adapters.TypeScript;
using Cohesive.CodeGen;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Tests.CodeGen;

public sealed class PortableDocumentContractTests
{
    [Fact]
    public void NativeDocumentContracts_PreserveOpaqueRelationCasesAndTypedExpressions()
    {
        var graph = new ClrShapeGraphBuilder()
            .AddMetadataProvider(new SystemTextJsonClrShapeMetadataProvider(StrictDocumentJson.CreateOptions()))
            .AddShape<RelationDraftDocument>()
            .AddShape<RelationQueryDocument>()
            .Build(new("native-document-contracts"));
        var emission = new TypeScriptShapeEmitter().Emit(new ShapeCodeGenerationRequest(graph));
        var text = Assert.Single(emission.Documents).Text;
        Assert.Contains("export interface RelationDraftDocument", text, StringComparison.Ordinal);
        Assert.Contains("export interface RelationQueryDocument", text, StringComparison.Ordinal);
        Assert.Contains("readonly $definition: 'relation';\n} & Record<string, unknown>", text, StringComparison.Ordinal);
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

    sealed record Envelope(Payload Payload);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$payload")]
    [JsonDerivedType(typeof(DocumentPayload), "document")]
    abstract record Payload;

    [PortableJsonValue(JsonTypeKind.Object)]
    sealed record DocumentPayload(string Name) : Payload;
}
