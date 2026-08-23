using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Authoring;

namespace Cohesive.Tests.Model;

public sealed class DefaultClrTypeRefMapperTests
{
    readonly DefaultClrTypeRefMapper mapper = new();

    [Fact]
    public void Map_UsesCanonicalScalarKindsSharedWithClrShapeAuthoring()
    {
        (Type ClrType, ScalarTypeKind Expected)[] cases =
        [
            (typeof(long), ScalarTypeKind.Int64),
            (typeof(long?), ScalarTypeKind.Int64),
            (typeof(DateOnly), ScalarTypeKind.Date),
            (typeof(DateTime), ScalarTypeKind.DateTime),
            (typeof(DateTimeOffset), ScalarTypeKind.Instant),
            (typeof(byte[]), ScalarTypeKind.Bytes)
        ];

        foreach (var (clrType, expected) in cases)
        {
            var scalar = Assert.IsType<ScalarTypeRef>(mapper.Map(clrType, nullability: null));
            Assert.Equal(expected, scalar.Kind);
        }
    }

    [Fact]
    public void Map_StructuralObjectUsesSerializedMemberNamesInOrdinalOrder()
    {
        var type = Assert.IsType<ObjectTypeRef>(mapper.Map(typeof(SerializedEnvelope), nullability: null));

        Assert.Collection(
            type.Fields,
            field =>
            {
                Assert.Equal("alpha", field.Name);
                Assert.Equal(ScalarTypeKind.Instant, Assert.IsType<ScalarTypeRef>(field.Type).Kind);
            },
            field =>
            {
                Assert.Equal("zeta", field.Name);
                Assert.Equal(ScalarTypeKind.Int64, Assert.IsType<ScalarTypeRef>(field.Type).Kind);
            });
    }

    [Fact]
    public void Map_CollidingSerializedMemberNamesProduceDiagnosticOpaqueType()
    {
        var type = Assert.IsType<OpaqueRuntimeTypeRef>(
            mapper.Map(typeof(AmbiguousSerializedEnvelope), nullability: null));

        Assert.Equal(
            TypeInferenceDiagnosticReasons.AmbiguousSerializedProperty,
            type.InferenceDiagnostic?.Reason);
    }

    [Fact]
    public void Map_DeclaredPortableJsonValueUsesItsJsonContractAtEveryOccurrence()
    {
        var document = Assert.IsType<JsonTypeRef>(
            mapper.Map(typeof(PortableDocument), nullability: null));
        var envelope = Assert.IsType<ObjectTypeRef>(
            mapper.Map(typeof(PortableDocumentEnvelope), nullability: null));

        Assert.Equal(JsonTypeKind.Object, document.Kind);
        Assert.Equal(
            JsonTypeKind.Object,
            Assert.IsType<JsonTypeRef>(Assert.Single(envelope.Fields).Type).Kind);
    }

    [Fact]
    public void Map_JsonStringEnumUsesExactCanonicalWireMembersAcceptedByObservationValues()
    {
        var type = Assert.IsType<EnumTypeRef>(mapper.Map(typeof(WireDisposition), nullability: null));
        var contract = new ValueContract(type);
        var observed = ObservationValue.FromObject(WireDisposition.PartnerOverlay);

        Assert.Equal(["standard", "partner-overlay"], type.Members.ToArray());
        Assert.Equal("partner-overlay", observed.GetString());
        Assert.True(contract.IsSatisfiedByConstant(observed));
    }

    [Fact]
    public void Map_PlainEnumRetainsClrMemberNames()
    {
        var type = Assert.IsType<EnumTypeRef>(mapper.Map(typeof(PlainDisposition), nullability: null));

        Assert.Equal(
            [nameof(PlainDisposition.Standard), nameof(PlainDisposition.PartnerOverlay)],
            type.Members.ToArray());
        Assert.True(new ValueContract(type).IsSatisfiedByConstant(
            ObservationValue.FromObject(PlainDisposition.PartnerOverlay)));
    }

    [Fact]
    public void Map_CustomEnumConverterDoesNotClaimAnExactMemberCatalog()
    {
        var type = Assert.IsType<OpaqueRuntimeTypeRef>(
            mapper.Map(typeof(CustomConvertedDisposition), nullability: null));

        Assert.Equal(TypeInferenceDiagnosticReasons.UnsupportedEnumConverter, type.InferenceDiagnostic?.Reason);
    }

    sealed record SerializedEnvelope(
        [property: JsonPropertyName("zeta")] long Sequence,
        [property: JsonPropertyName("alpha")] DateTimeOffset ObservedAt);

    sealed record AmbiguousSerializedEnvelope(
        [property: JsonPropertyName("same")] string First,
        [property: JsonPropertyName("same")] string Second);

    [PortableJsonValue(JsonTypeKind.Object)]
    sealed record PortableDocument(IReadOnlyDictionary<string, object?> Content);

    sealed record PortableDocumentEnvelope(PortableDocument Document);

    [JsonConverter(typeof(JsonStringEnumConverter))]
    enum WireDisposition
    {
        [JsonStringEnumMemberName("standard")]
        Standard,

        [JsonStringEnumMemberName("partner-overlay")]
        PartnerOverlay
    }

    enum PlainDisposition
    {
        Standard,
        PartnerOverlay
    }

    [JsonConverter(typeof(CustomConvertedDispositionConverter))]
    enum CustomConvertedDisposition
    {
        Standard
    }

    sealed class CustomConvertedDispositionConverter : JsonConverter<CustomConvertedDisposition>
    {
        public override CustomConvertedDisposition Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => CustomConvertedDisposition.Standard;

        public override void Write(
            Utf8JsonWriter writer,
            CustomConvertedDisposition value,
            JsonSerializerOptions options) => writer.WriteNumberValue((int)value);
    }
}
