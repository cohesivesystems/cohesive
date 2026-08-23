using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Model;

public sealed class ObjectEntityDefinitionTests
{
    [Fact]
    public void ObjectEntityDefinition_ForPoco_InfersCanonicalEntityShape()
    {
        var definition = ObjectEntityDefinition.For<SamplePoco>();

        Assert.Equal("SamplePoco", definition.Name.Value);
        Assert.Equal("shape.entity.SamplePoco", definition.Shape.Id.Value);

        var id = Assert.Single(definition.Fields, static field => field.Name.Value == "entity_id");
        Assert.Equal(FieldPresence.Required, id.Presence);
        Assert.Equal(FieldCardinality.Single, id.Cardinality);

        var optionalName = Assert.Single(definition.Fields, static field => field.Name.Value == nameof(SamplePoco.OptionalName));
        Assert.Equal(FieldPresence.Optional, optionalName.Presence);

        var scores = Assert.Single(definition.Fields, static field => field.Name.Value == nameof(SamplePoco.Scores));
        Assert.Equal(FieldCardinality.Many, scores.Cardinality);
        Assert.Equal(ScalarTypeKind.Int32, Assert.IsType<ScalarTypeRef>(scores.Type).Kind);

        var payload = Assert.Single(definition.Fields, static field => field.Name.Value == nameof(SamplePoco.Payload));
        var payloadType = Assert.IsType<ObjectTypeRef>(payload.Type);
        Assert.Contains(payloadType.Fields, static field => field.Name == nameof(SampleNestedPayload.Code));
    }

    [Fact]
    public void ObjectEntityDefinition_ForPoco_IsCachedPerClrType()
    {
        Assert.Same(
            ObjectEntityDefinition.For<SamplePoco>(),
            ObjectEntityDefinition.For<SamplePoco>());
    }

    [Fact]
    public void ObjectEntityDefinition_ForJsonPayloads_InfersJsonTypeRefs()
    {
        var definition = ObjectEntityDefinition.For<SampleJsonPayloadPoco>();

        var payload = Assert.Single(definition.Fields, static field => field.Name.Value == nameof(SampleJsonPayloadPoco.Payload));
        Assert.Equal(JsonTypeKind.Any, Assert.IsType<JsonTypeRef>(payload.Type).Kind);

        var objectPayload = Assert.Single(definition.Fields, static field => field.Name.Value == nameof(SampleJsonPayloadPoco.ObjectPayload));
        Assert.Equal(JsonTypeKind.Object, Assert.IsType<JsonTypeRef>(objectPayload.Type).Kind);

        var annotation = Assert.Single(definition.Fields, static field => field.Name.Value == nameof(SampleJsonPayloadPoco.Annotation));
        Assert.Equal(JsonTypeKind.Any, Assert.IsType<JsonTypeRef>(annotation.Type).Kind);
    }

    [Fact]
    public void ObjectEntityDefinition_ForRecursivePayload_FallsBackToOpaqueNestedType()
    {
        var definition = ObjectEntityDefinition.For<SampleRecursivePayloadPoco>();

        var payload = Assert.Single(definition.Fields, static field => field.Name.Value == nameof(SampleRecursivePayloadPoco.Payload));
        var payloadType = Assert.IsType<ObjectTypeRef>(payload.Type);
        var child = Assert.Single(payloadType.Fields, static field => field.Name == nameof(SampleRecursivePayload.Child));
        var childType = Assert.IsType<OpaqueRuntimeTypeRef>(child.Type);

        Assert.Equal(typeof(SampleRecursivePayload).FullName, childType.RuntimeType);
        Assert.Equal(TypeInferenceDiagnosticReasons.RecursiveType, childType.InferenceDiagnostic?.Reason);
    }

    [Fact]
    public void ObjectEntityDefinition_ForDocumentEnvelope_InfersStructuralTypeRef()
    {
        var definition = ObjectEntityDefinition.For<SampleDocumentEnvelopePoco>();

        var document = Assert.Single(definition.Fields, static field => field.Name.Value == nameof(SampleDocumentEnvelopePoco.Document));
        var documentType = Assert.IsType<ObjectTypeRef>(document.Type);
        var payload = Assert.Single(documentType.Fields, static field => field.Name == nameof(SamplePortableDocument.Payload));
        var schemaVersion = Assert.Single(documentType.Fields, static field => field.Name == nameof(SamplePortableDocument.SchemaVersion));

        Assert.Equal(JsonTypeKind.Any, Assert.IsType<JsonTypeRef>(payload.Type).Kind);
        Assert.Equal(ScalarTypeKind.String, Assert.IsType<ScalarTypeRef>(schemaVersion.Type).Kind);
    }

    [Fact]
    public void ObjectEntityDefinition_CanonicalScalarFieldsValidatePortableStateValues()
    {
        var definition = ObjectEntityDefinition.For<CanonicalScalarPoco>();

        Assert.Equal(
            ScalarTypeKind.Int64,
            Assert.IsType<ScalarTypeRef>(
                Assert.Single(definition.Fields, field => field.Name.Value == nameof(CanonicalScalarPoco.Count)).Type).Kind);
        Assert.Equal(
            ScalarTypeKind.Date,
            Assert.IsType<ScalarTypeRef>(
                Assert.Single(definition.Fields, field => field.Name.Value == nameof(CanonicalScalarPoco.Date)).Type).Kind);
        Assert.Equal(
            ScalarTypeKind.Instant,
            Assert.IsType<ScalarTypeRef>(
                Assert.Single(definition.Fields, field => field.Name.Value == nameof(CanonicalScalarPoco.Instant)).Type).Kind);
        Assert.Equal(
            ScalarTypeKind.Bytes,
            Assert.IsType<ScalarTypeRef>(
                Assert.Single(definition.Fields, field => field.Name.Value == nameof(CanonicalScalarPoco.Payload)).Type).Kind);

        _ = definition.CreateState(
            entityId: "canonical-scalars/1",
            stateObject: new CanonicalScalarPoco(
                Count: 42,
                Date: new DateOnly(2026, 7, 29),
                Instant: new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
                Payload: [1, 2, 3]),
            version: 1);
    }

    [Fact]
    public void DefaultClrTypeRefMapper_ForUnsupportedDictionary_FallsBackToOpaqueWithDiagnostic()
    {
        var type = new DefaultClrTypeRefMapper().Map(typeof(Dictionary<string, int>), null);
        var opaque = Assert.IsType<OpaqueRuntimeTypeRef>(type);

        Assert.Equal(typeof(Dictionary<string, int>).FullName, opaque.RuntimeType);
        Assert.Equal(TypeInferenceDiagnosticReasons.UnsupportedDictionary, opaque.InferenceDiagnostic?.Reason);
    }

    [Fact]
    public void DefaultClrTypeRefMapper_ForShapeGraphDocument_UsesDeclaredPortableJsonContract()
    {
        var type = new DefaultClrTypeRefMapper().Map(typeof(ShapeGraphDocument), null);
        var documentType = Assert.IsType<JsonTypeRef>(type);

        Assert.Equal(JsonTypeKind.Object, documentType.Kind);
    }

    [Fact]
    public void DefaultClrTypeRefMapper_ForPolymorphicType_FallsBackToOpaqueWithDiagnostic()
    {
        var type = new DefaultClrTypeRefMapper().Map(typeof(TypeRef), null);
        var opaque = Assert.IsType<OpaqueRuntimeTypeRef>(type);

        Assert.Equal(typeof(TypeRef).FullName, opaque.RuntimeType);
        Assert.Equal(TypeInferenceDiagnosticReasons.PolymorphicType, opaque.InferenceDiagnostic?.Reason);
    }

    [Fact]
    public void OpaqueRuntimeTypeRef_SerializesInferenceDiagnosticWhenPresent()
    {
        TypeRef type = new OpaqueRuntimeTypeRef(
            runtimeType: "sample.Type",
            inferenceDiagnostic: new TypeInferenceDiagnostic(
                reason: TypeInferenceDiagnosticReasons.RecursiveType,
                message: "Recursive type."));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(type, options);
        var roundTrip = Assert.IsType<OpaqueRuntimeTypeRef>(JsonSerializer.Deserialize<TypeRef>(json, options));

        Assert.Contains("\"$type\":\"opaque\"", json, StringComparison.Ordinal);
        Assert.Contains("\"inferenceDiagnostic\":", json, StringComparison.Ordinal);
        Assert.Equal(type, roundTrip);
        Assert.Equal(TypeInferenceDiagnosticReasons.RecursiveType, roundTrip.InferenceDiagnostic?.Reason);
        Assert.Equal("Recursive type.", roundTrip.InferenceDiagnostic?.Message);
    }

    [Fact]
    public void OpaqueRuntimeTypeRef_OmitsInferenceDiagnosticWhenAbsent()
    {
        TypeRef type = new OpaqueRuntimeTypeRef(runtimeType: "sample.Type");
        var json = JsonSerializer.Serialize(type, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("inferenceDiagnostic", json, StringComparison.Ordinal);
    }

    public sealed record SamplePoco
    {
        [JsonPropertyName("entity_id")]
        public required string Id { get; init; }

        public string? OptionalName { get; init; }

        public required SampleNestedPayload Payload { get; init; }

        public required int[] Scores { get; init; }
    }

    public sealed record SampleNestedPayload
    {
        public required string Code { get; init; }
    }

    public sealed record SampleJsonPayloadPoco
    {
        public required JsonNode Payload { get; init; }

        public required JsonObject ObjectPayload { get; init; }

        public required AnnotationValue Annotation { get; init; }
    }

    public sealed record SampleRecursivePayloadPoco
    {
        public required SampleRecursivePayload Payload { get; init; }
    }

    public sealed record SampleRecursivePayload
    {
        public SampleRecursivePayload? Child { get; init; }

        public required string Name { get; init; }
    }

    public sealed record SampleDocumentEnvelopePoco
    {
        public required SamplePortableDocument Document { get; init; }
    }

    public sealed record SamplePortableDocument
    {
        public required string SchemaVersion { get; init; }

        public required JsonNode Payload { get; init; }
    }

    public sealed record CanonicalScalarPoco(
        long Count,
        DateOnly Date,
        DateTimeOffset Instant,
        byte[] Payload);
}
