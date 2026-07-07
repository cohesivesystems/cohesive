using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cohesive.Tests.Model;

public sealed class ClrShapeGraphBuilderMetadataTests
{
    [Fact]
    public void Build_AppliesShapeAttributesAndMetadataProviderContributions()
    {
        var graph = new ClrShapeGraphBuilder()
            .AddMetadataProvider(new TestMetadataProvider())
            .AddShape<AttributedShipment>()
            .Build(new("graph.metadata.test"));

        var shape = Assert.Single(graph.Shapes);
        Assert.Equal("shape.shipment", shape.Id.Value);
        Assert.Equal("transport", GetAnnotation(shape.Annotations, ShapeAnnotationKeys.Role));
        Assert.True(shape.HasRole(ShapeRoles.Transport));
        Assert.Equal(nameof(AttributedShipment), GetAnnotation(shape.Annotations, "test.shape"));

        var stopsField = shape.GetField(nameof(AttributedShipment.Stops));
        Assert.Equal(nameof(AttributedShipment.Stops), GetAnnotation(stopsField.Annotations, "test.field"));

        var stopType = Assert.IsType<TypeDefinition.Structural>(Assert.Single(graph.NamedTypes, x => x.Id.Value == "type.stop"));
        Assert.Equal("type.stop", stopType.Id.Value);
        Assert.Equal(nameof(AttributedStop), stopType.Name);
        Assert.Equal(nameof(AttributedStop), GetAnnotation(stopType.Annotations, "test.type"));

        var codeField = Assert.Single(stopType.Fields, x => x.Name.Value == nameof(AttributedStop.Code));
        Assert.Equal(nameof(AttributedStop.Code), GetAnnotation(codeField.Annotations, "test.field"));
    }

    [Fact]
    public void Build_MapsEitherPropertiesToNamedUnionTypes()
    {
        var graph = new ClrShapeGraphBuilder()
            .AddShape<EitherEnvelope>()
            .Build(new("graph.either.test"));

        var envelope = Assert.Single(graph.Shapes);
        var items = envelope.GetField(nameof(EitherEnvelope.Items));
        Assert.Equal(FieldCardinality.Many, items.Cardinality);

        var unionRef = Assert.IsType<NamedTypeRef>(items.Type);
        var union = Assert.IsType<TypeDefinition.Union>(
            Assert.Single(graph.NamedTypes, x => x.Id == unionRef.TypeId));

        Assert.Equal("Type", union.Discriminator.FieldName);
        Assert.Equal("EitherOfEitherCaseAAndEitherCaseB", union.Name);
        Assert.Equal(
            [nameof(EitherCaseA), nameof(EitherCaseB)],
            union.Cases.Select(x => x.Name));
        Assert.All(union.Cases, unionCase => Assert.IsType<NamedTypeRef>(unionCase.Type));
    }

    [Fact]
    public void Build_MapsTimeOnlyPropertiesToOpaqueRuntimeType()
    {
        var graph = new ClrShapeGraphBuilder()
            .AddShape<TimeEnvelope>()
            .Build(new("graph.time.test"));

        var shape = Assert.Single(graph.Shapes);
        var time = shape.GetField(nameof(TimeEnvelope.Time));
        var type = Assert.IsType<OpaqueRuntimeTypeRef>(time.Type);
        Assert.Equal("TimeOnly", type.RuntimeType);
        Assert.DoesNotContain(graph.NamedTypes, x => x.Id.Value.EndsWith(nameof(TimeOnly), StringComparison.Ordinal));
    }

    [Fact]
    public void Build_MapsJsonRuntimePropertiesToJsonTypeRefs()
    {
        var graph = new ClrShapeGraphBuilder()
            .AddShape<JsonEnvelope>()
            .Build(new("graph.json.test"));

        var shape = Assert.Single(graph.Shapes);

        var element = Assert.IsType<JsonTypeRef>(shape.GetField(nameof(JsonEnvelope.Element)).Type);
        Assert.Equal(JsonTypeKind.Any, element.Kind);

        var node = Assert.IsType<JsonTypeRef>(shape.GetField(nameof(JsonEnvelope.Node)).Type);
        Assert.Equal(JsonTypeKind.Any, node.Kind);

        var obj = Assert.IsType<JsonTypeRef>(shape.GetField(nameof(JsonEnvelope.Object)).Type);
        Assert.Equal(JsonTypeKind.Object, obj.Kind);

        var array = Assert.IsType<JsonTypeRef>(shape.GetField(nameof(JsonEnvelope.Array)).Type);
        Assert.Equal(JsonTypeKind.Array, array.Kind);

        Assert.DoesNotContain(graph.NamedTypes, x => x.Id.Value.Contains(nameof(JsonElement), StringComparison.Ordinal));
        Assert.DoesNotContain(graph.NamedTypes, x => x.Id.Value.Contains(nameof(JsonValueKind), StringComparison.Ordinal));
        Assert.DoesNotContain(graph.NamedTypes, x => x.Id.Value.Contains(nameof(JsonNode), StringComparison.Ordinal));
    }

    static string? GetAnnotation(
        IReadOnlyDictionary<AnnotationKey, AnnotationValue> annotations,
        string key
        ) =>
        annotations[new(key)].Value?.GetValue<string>();

    sealed class TestMetadataProvider : IClrShapeMetadataProvider
    {
        public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context)
        {
            return context.Target switch
            {
                ClrShapeMetadataTarget.Shape => new()
                {
                    Annotations = AnnotationMap.Create("test.shape", context.ClrType.Name)
                },
                ClrShapeMetadataTarget.Type => new()
                {
                    Annotations = AnnotationMap.Create("test.type", context.ClrType.Name)
                },
                ClrShapeMetadataTarget.Field => new()
                {
                    Annotations = AnnotationMap.Create("test.field", context.Property?.Name)
                },
                _ => ClrShapeMetadata.Empty
            };
        }
    }

    [ShapeDefinition("shape.shipment", ShapeRoles.Transport)]
    sealed record AttributedShipment(IReadOnlyList<AttributedStop> Stops);

    [ShapeType("type.stop")]
    sealed record AttributedStop(string Code);

    sealed record EitherEnvelope(IReadOnlyList<Either<EitherCaseA, EitherCaseB>> Items);

    sealed record EitherCaseA(string Id);

    sealed record EitherCaseB(string Code);

    sealed record TimeEnvelope(TimeOnly Time);

    sealed record JsonEnvelope(JsonElement Element, JsonNode? Node, JsonObject Object, JsonArray Array);
}
