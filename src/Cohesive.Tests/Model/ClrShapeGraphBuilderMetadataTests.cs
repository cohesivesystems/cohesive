using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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
    public void Build_DistinguishesCivilDateTimeFromAbsoluteInstant()
    {
        var graph = new ClrShapeGraphBuilder()
            .AddShape<TemporalEnvelope>()
            .Build(new("graph.temporal.test"));

        var shape = Assert.Single(graph.Shapes);

        Assert.Equal(
            ScalarTypeKind.DateTime,
            Assert.IsType<ScalarTypeRef>(shape.GetField(nameof(TemporalEnvelope.Civil)).Type).Kind);
        Assert.Equal(
            ScalarTypeKind.Instant,
            Assert.IsType<ScalarTypeRef>(shape.GetField(nameof(TemporalEnvelope.Instant)).Type).Kind);
    }

    [Fact]
    public void Build_UsesTheSharedCanonicalClrScalarMapping()
    {
        var graph = new ClrShapeGraphBuilder()
            .AddShape<CanonicalScalarEnvelope>()
            .Build(new("graph.canonical-scalars.test"));

        var shape = Assert.Single(graph.Shapes);

        Assert.Equal(
            ScalarTypeKind.Int64,
            Assert.IsType<ScalarTypeRef>(shape.GetField(nameof(CanonicalScalarEnvelope.Count)).Type).Kind);
        Assert.Equal(
            ScalarTypeKind.Date,
            Assert.IsType<ScalarTypeRef>(shape.GetField(nameof(CanonicalScalarEnvelope.Date)).Type).Kind);
        Assert.Equal(
            ScalarTypeKind.Bytes,
            Assert.IsType<ScalarTypeRef>(shape.GetField(nameof(CanonicalScalarEnvelope.Payload)).Type).Kind);
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

    [Fact]
    public void Build_IncludesConditionallyIgnoredJsonPropertiesAndExcludesAlwaysIgnoredProperties()
    {
        var graph = new ClrShapeGraphBuilder()
            .AddShape<JsonIgnoreEnvelope>()
            .Build(new("graph.json-ignore.test"));

        var shape = Assert.Single(graph.Shapes);

        Assert.True(shape.TryGetField(nameof(JsonIgnoreEnvelope.Conditional), out _));
        Assert.True(shape.TryGetField(nameof(JsonIgnoreEnvelope.Visible), out _));
        Assert.False(shape.TryGetField(nameof(JsonIgnoreEnvelope.AlwaysIgnored), out _));
    }

    [Fact]
    public void Build_EntityReferenceDeclarationProjectsTypedReferenceAndIndependentPresenceMetadata()
    {
        var result = new ClrShapeGraphBuilder()
            .AddShape<ReferenceCarrier>(ShapeRoles.Entity)
            .AddShape<ReferenceLoad>(ShapeRoles.Entity)
            .AddEntityReference<ReferenceLoad, ReferenceCarrier>(
                load => load.CarrierId,
                presence: FieldPresence.Optional,
                nullability: FieldNullability.NonNullable)
            .BuildResult(new("graph.entity-reference.test"));

        var load = result.GetShape<ReferenceLoad>().Graph.GetShape(
            result.GetShape<ReferenceLoad>().ShapeId);
        var carrier = result.GetShape<ReferenceCarrier>().Graph.GetShape(
            result.GetShape<ReferenceCarrier>().ShapeId);
        var reference = load.GetField(nameof(ReferenceLoad.CarrierId));

        Assert.Equal(FieldRole.Reference, reference.Role);
        Assert.Equal(FieldPresence.Optional, reference.Presence);
        Assert.Equal(FieldNullability.NonNullable, reference.Nullability);
        Assert.Equal(
            EntityTypeName.From<ReferenceCarrier>(),
            Assert.IsType<EntityReferenceTypeRef>(reference.Type).Entity);
        Assert.Equal(ShapeRoles.Entity, carrier.Role);
        Assert.Equal(EntityTypeName.From<ReferenceCarrier>(), carrier.EntityType);
    }

    [Fact]
    public void Build_EntityReferenceRequiresBothClrEndpointsAsRootShapes()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ClrShapeGraphBuilder()
            .AddShape<ReferenceLoad>(ShapeRoles.Entity)
            .AddEntityReference<ReferenceLoad, ReferenceCarrier>(load => load.CarrierId)
            .Build());

        Assert.Contains(nameof(ReferenceCarrier), exception.Message, StringComparison.Ordinal);
        Assert.Contains("root shape", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsMultipleDistinctEnumerableElementContracts()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new ClrShapeGraphBuilder()
            .AddShape<AmbiguousEnumerableEnvelope>()
            .Build(new("graph.ambiguous-enumerable.test")));

        Assert.Contains("multiple distinct element types", exception.Message, StringComparison.Ordinal);
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

    sealed record TemporalEnvelope(DateTime Civil, DateTimeOffset Instant);

    sealed record CanonicalScalarEnvelope(long Count, DateOnly Date, byte[] Payload);

    sealed record JsonEnvelope(JsonElement Element, JsonNode? Node, JsonObject Object, JsonArray Array);

    sealed record JsonIgnoreEnvelope(
        [property: JsonIgnore] string AlwaysIgnored,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Conditional,
        string Visible);

    sealed record ReferenceCarrier(string Name);

    sealed record ReferenceLoad(int Number, string CarrierId);

    sealed record AmbiguousEnumerableEnvelope(AmbiguousEnumerable Items);

    sealed class AmbiguousEnumerable : IEnumerable<int>, IEnumerable<string>
    {
        public string Description => "ambiguous";

        IEnumerator<int> IEnumerable<int>.GetEnumerator() => Enumerable.Empty<int>().GetEnumerator();

        IEnumerator<string> IEnumerable<string>.GetEnumerator() => Enumerable.Empty<string>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => Enumerable.Empty<int>().GetEnumerator();
    }
}
