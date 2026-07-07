using System.Globalization;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class ObservationValueBytesTests
{
    [Fact]
    public void FromObject_ByteArray_ProducesBytes_AndCopiesBuffer()
    {
        var source = new byte[] { 1, 2, 3 };

        var observed = ObservationValue.FromObject(source);
        source[0] = 9;

        Assert.Equal(ObservationValueKind.Bytes, observed.Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, observed.GetBytes().ToArray());
    }

    [Fact]
    public void Serialize_DefaultPolicy_ThrowsForBytes()
    {
        var observed = ObservationValue.FromBytes(new byte[] { 1, 2, 3 });

        var ex = Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(observed));
        Assert.Contains("cannot be encoded as JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_Base64Policy_EncodesBytesAsString()
    {
        var observed = ObservationValue.FromBytes(new byte[] { 1, 2, 3 });
        JsonSerializerOptions options = new();
        options.Converters.Add(new ObservationValueJsonConverter(ObservationBytesJsonEncoding.Base64String));

        var json = JsonSerializer.Serialize(observed, options);

        Assert.Equal("\"AQID\"", json);
    }

    [Fact]
    public void Serialize_Base64Policy_EncodesNestedBytesAsString()
    {
        var observed = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["payload"] = ObservationValue.FromBytes(new byte[] { 0, 255 })
        });
        JsonSerializerOptions options = new();
        options.Converters.Add(new ObservationValueJsonConverter(ObservationBytesJsonEncoding.Base64String));

        var json = JsonSerializer.Serialize(observed, options);

        Assert.Equal("{\"payload\":\"AP8=\"}", json);
    }

    [Fact]
    public void Deserialize_Base64String_DoesNotInferBytesWithoutMetadata()
    {
        var observed = JsonSerializer.Deserialize<ObservationValue>("\"AQID\"");

        Assert.Equal(ObservationValueKind.String, observed.Kind);
        Assert.Equal("AQID", observed.GetString());
    }

    [Fact]
    public void GetRawText_Bytes_RespectsEncodingPolicy()
    {
        var observed = ObservationValue.FromBytes(new byte[] { 0, 255 });

        Assert.Throws<InvalidOperationException>(() => observed.ToScalarString());
        Assert.Equal("AP8=", observed.ToScalarString(bytesEncoding: ObservationBytesJsonEncoding.Base64String));
    }

    [Fact]
    public void DeepEquals_Bytes_ComparesContent()
    {
        var left = ObservationValue.FromBytes(new byte[] { 1, 2, 3 });
        var equal = ObservationValue.FromBytes(new byte[] { 1, 2, 3 });
        var different = ObservationValue.FromBytes(new byte[] { 1, 2, 4 });

        Assert.True(ObservationValue.DeepEquals(left, equal));
        Assert.False(ObservationValue.DeepEquals(left, different));
        Assert.Equal(left.GetHashCode(), equal.GetHashCode());
    }

    [Fact]
    public void DeepEquals_Int64_AndIntegralDouble_AreEqual_AndHashMatches()
    {
        var left = ObservationValue.FromInt64(42);
        var right = ObservationValue.FromDouble(42d);

        Assert.True(ObservationValue.DeepEquals(left, right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void DeepEquals_Int64_AndFractionalDouble_AreNotEqual()
    {
        var left = ObservationValue.FromInt64(42);
        var right = ObservationValue.FromDouble(42.25d);

        Assert.False(ObservationValue.DeepEquals(left, right));
    }

    [Fact]
    public void DeepEquals_StringAndNumeric_AreNotEqual()
    {
        var left = ObservationValue.FromString("42");
        var right = ObservationValue.FromInt64(42);

        Assert.False(ObservationValue.DeepEquals(left, right));
    }

    [Fact]
    public void GetHashCode_Object_DoesNotDependOnPropertyInsertionOrder()
    {
        var first = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["a"] = ObservationValue.FromInt64(1),
            ["b"] = ObservationValue.FromString("x")
        });
        var second = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["b"] = ObservationValue.FromString("x"),
            ["a"] = ObservationValue.FromInt64(1)
        });

        Assert.True(ObservationValue.DeepEquals(first, second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void ToScalarString_UsesProvidedFormatProvider()
    {
        var observed = ObservationValue.FromDouble(1.5d);

        var formatted = observed.ToScalarString(new CultureInfo("fr-FR"));

        Assert.Equal("1,5", formatted);
    }

    [Fact]
    public void ToScalarString_Bytes_RespectsEncodingPolicy()
    {
        var observed = ObservationValue.FromBytes(new byte[] { 0, 255 });

        Assert.Throws<InvalidOperationException>(() => observed.ToScalarString());
        Assert.Equal("AP8=", observed.ToScalarString(bytesEncoding: ObservationBytesJsonEncoding.Base64String));
    }

    [Fact]
    public void ToScalarString_Object_Throws()
    {
        var observed = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["k"] = ObservationValue.FromString("v")
        });

        Assert.Throws<InvalidOperationException>(() => observed.ToScalarString());
    }
}
