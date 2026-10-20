using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

// Executable rules for representation changes; see docs/decisions/observation-value-optimization.md.
public sealed class ObservationValueSemanticContractTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(9007199254740992L)]
    public void NumericEquality_IgnoresCarrierKindAndKeepsHashCompatibility(long number)
    {
        ObservationValue[] values = [ObservationValue.FromInt64(number), ObservationValue.FromDecimal(number), ObservationValue.FromDouble(number)];
        foreach (var left in values)
        foreach (var right in values)
        {
            Assert.Equal(left, right);
            Assert.Equal(left.GetHashCode(), right.GetHashCode());
            Assert.Equal(0, ObservationValueSemantics.Compare(left, right));
        }
    }

    [Fact]
    public void SignedZeroAndDecimalScale_DoNotChangeNumericIdentity()
    {
        var zero = ObservationValue.FromInt64(0);
        var negativeZero = ObservationValue.FromDouble(-0d);
        Assert.Equal(zero, negativeZero);
        Assert.Equal(zero.GetHashCode(), negativeZero.GetHashCode());
        Assert.Equal(ObservationValue.FromDecimal(1m), ObservationValue.FromDecimal(1.000m));
        Assert.Equal(Canonical(zero), Canonical(negativeZero));
        Assert.Equal(Canonical(ObservationValue.FromDecimal(1m)), Canonical(ObservationValue.FromDecimal(1.000m)));
    }

    [Fact]
    public void TemporalEquality_PreservesOffsetRepresentationWhileOrderingComparesInstants()
    {
        var instant = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var utc = ObservationValue.FromDateTimeOffset(instant);
        var offset = ObservationValue.FromDateTimeOffset(instant.ToOffset(TimeSpan.FromHours(-7)));
        Assert.NotEqual(utc, offset);
        Assert.Equal(0, ObservationValueSemantics.Compare(utc, offset));
        Assert.NotEqual(utc, ObservationValue.FromString(utc.String));
    }

    [Fact]
    public void MissingUndefinedAndNull_AreDistinctUntilAnExplicitProjectionCollapsesThem()
    {
        var missing = ObservationValue.EmptyObject;
        var undefined = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = ObservationValue.Undefined });
        var presentNull = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = ObservationValue.Null });
        Assert.False(missing.TryGetProperty("value", out _));
        Assert.True(undefined.TryGetProperty("value", out var field));
        Assert.Equal(ObservationValueKind.Undefined, field.Kind);
        Assert.NotEqual(missing, undefined);
        Assert.NotEqual(undefined, presentNull);
        Assert.Equal(missing, presentNull.WithField(FieldPath.FromField("value"), ObservationValue.Undefined));
        // Ordinary JSON is a lossy projection, not an injective encoding of value identity.
        Assert.Equal(Canonical(undefined), Canonical(presentNull));
    }

    [Fact]
    public void ObjectOrderIsPhysical_ArrayOrderIsSemantic_AndCompositionSharesSubtrees()
    {
        var child = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["leaf"] = ObservationValue.FromInt64(1) });
        var left = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["a"] = child, ["b"] = ObservationValue.Null });
        var right = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["b"] = ObservationValue.Null, ["a"] = child });
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Equal(Canonical(left), Canonical(right));
        Assert.Same(child.Fields, left.WithField(FieldPath.FromField("b"), ObservationValue.FromInt64(2)).GetProperty("a").Fields);
        Assert.NotEqual(ObservationValue.FromArray([child, ObservationValue.Null]), ObservationValue.FromArray([ObservationValue.Null, child]));
    }

    [Fact]
    public void LosslessTaggedCodec_PreservesKindsThatPlainJsonCannotDistinguish()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(PortableValueJsonConverter.TaggedObservationValues);
        ObservationValue[] values = [ObservationValue.Undefined, ObservationValue.Null, ObservationValue.FromInt64(1),
            ObservationValue.FromDecimal(1m), ObservationValue.FromDouble(1d), ObservationValue.FromBytes(new byte[] { 1, 2 }),
            ObservationValue.FromDateOnly(new DateOnly(2026, 10, 18)), ObservationValue.FromString("2026-10-18")];
        foreach (var value in values)
        {
            var decoded = JsonSerializer.Deserialize<ObservationValue>(JsonSerializer.SerializeToUtf8Bytes(value, options), options);
            Assert.Equal(value.Kind, decoded.Kind);
            Assert.Equal(value, decoded);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteDoubles_HaveNoCanonicalJsonEncoding(double number) =>
        Assert.Throws<InvalidOperationException>(() => Canonical(ObservationValue.FromDouble(number)));

    static byte[] Canonical(ObservationValue value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        CanonicalJsonWriter.WriteCanonicalObservationValue(writer, value);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
