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

    sealed record SerializedEnvelope(
        [property: JsonPropertyName("zeta")] long Sequence,
        [property: JsonPropertyName("alpha")] DateTimeOffset ObservedAt);

    sealed record AmbiguousSerializedEnvelope(
        [property: JsonPropertyName("same")] string First,
        [property: JsonPropertyName("same")] string Second);
}
