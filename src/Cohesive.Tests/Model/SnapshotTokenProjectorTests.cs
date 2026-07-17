using System.Text.Json;
using System.Globalization;

namespace Cohesive.Tests.Model;

public sealed class SnapshotTokenProjectorTests
{
    [Fact]
    public void Compute_ObjectPropertyOrder_DoesNotAffectToken()
    {
        var stateA = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["payload"] = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["b"] = ObservationValue.FromInt64(2),
                ["a"] = ObservationValue.FromString("x")
            })
        };
        var stateB = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["payload"] = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["a"] = ObservationValue.FromString("x"),
                ["b"] = ObservationValue.FromInt64(2)
            })
        };

        var tokenA = SnapshotTokenProjector.Compute(stateA, ["payload"]);
        var tokenB = SnapshotTokenProjector.Compute(stateB, ["payload"]);

        Assert.Equal(tokenA, tokenB);
    }

    [Fact]
    public void Compute_ArrayOrder_AffectsToken()
    {
        var stateA = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["items"] = ObservationValue.FromArray([ObservationValue.FromInt64(1), ObservationValue.FromInt64(2)])
        };
        var stateB = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["items"] = ObservationValue.FromArray([ObservationValue.FromInt64(2), ObservationValue.FromInt64(1)])
        };

        var tokenA = SnapshotTokenProjector.Compute(stateA, ["items"]);
        var tokenB = SnapshotTokenProjector.Compute(stateB, ["items"]);

        Assert.NotEqual(tokenA, tokenB);
    }

    [Fact]
    public void Compute_MissingField_And_NullField_AreEquivalent()
    {
        var missing = new Dictionary<string, ObservationValue>(StringComparer.Ordinal);
        var withNull = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["status"] = ObservationValue.Null
        };

        var missingToken = SnapshotTokenProjector.Compute(missing, ["status"]);
        var nullToken = SnapshotTokenProjector.Compute(withNull, ["status"]);

        Assert.Equal(missingToken, nullToken);
    }

    [Fact]
    public void Compute_Int64_And_String_AreDifferent()
    {
        var intState = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["v"] = ObservationValue.FromInt64(42)
        };
        var stringState = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["v"] = ObservationValue.FromString("42")
        };

        var intToken = SnapshotTokenProjector.Compute(intState, ["v"]);
        var stringToken = SnapshotTokenProjector.Compute(stringState, ["v"]);

        Assert.NotEqual(intToken, stringToken);
    }

    [Fact]
    public void Compute_Int64_PreservesLegacyTokenEncoding()
    {
        var state = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["v"] = ObservationValue.FromInt64(42)
        };

        Assert.Equal(
            "5E343C53B62ADCF0F61D92D9DB5C0D1FB6F1B641DCDDA03B928C50862587CCD4",
            SnapshotTokenProjector.Compute(state, ["v"]));
    }

    [Theory]
    [InlineData(1.5d, "9584A22FCA24C3243C9F88B2E5317BCCAF6203AF254C3D5F4EBFC166BAAA6CB9")]
    [InlineData(0.1d, "6836E58E5812C6EA5F4496C3E987A33379E730C3EDD0AD9BB542F8BDD620B0FF")]
    public void Compute_RepresentableFraction_PreservesLegacyDoubleTokenEncoding(
        double value,
        string expected)
    {
        using var document = JsonDocument.Parse(value.ToString("R", CultureInfo.InvariantCulture));
        var parsed = ObservationValue.FromJsonElement(document.RootElement);
        var parsedState = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["v"] = parsed
        };
        var legacyState = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["v"] = ObservationValue.FromDouble(value)
        };

        Assert.Equal(ObservationValueKind.Decimal, parsed.Kind);
        Assert.Equal(expected, SnapshotTokenProjector.Compute(parsedState, ["v"]));
        Assert.Equal(expected, SnapshotTokenProjector.Compute(legacyState, ["v"]));
    }

    [Fact]
    public void Compute_BytesValue_ProducesDigest()
    {
        var state = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["blob"] = ObservationValue.FromBytes(new byte[] { 0, 255, 17, 23 })
        };

        var token = SnapshotTokenProjector.Compute(state, ["blob"]);

        Assert.Equal(64, token.Length);
        Assert.All(token, c =>
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            Assert.True(isHex);
        });
    }

    [Fact]
    public void Compute_HighPrecisionDecimal_DistinguishesExactPayload()
    {
        var first = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["amount"] = ObservationValue.FromDecimal(12345678901234567890.123456789m)
        };
        var second = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["amount"] = ObservationValue.FromDecimal(12345678901234567890.123456788m)
        };

        Assert.NotEqual(
            SnapshotTokenProjector.Compute(first, ["amount"]),
            SnapshotTokenProjector.Compute(second, ["amount"]));
    }

    [Fact]
    public void Compute_CanonicalDecimalAndDoubleRepresentations_ProduceSameToken()
    {
        var exact = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["amount"] = ObservationValue.FromDecimal(0.1m)
        };
        var floatingPoint = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["amount"] = ObservationValue.FromDouble(0.1d)
        };

        Assert.Equal(
            SnapshotTokenProjector.Compute(exact, ["amount"]),
            SnapshotTokenProjector.Compute(floatingPoint, ["amount"]));
    }

    [Fact]
    public void Compute_CanonicalIntegerDecimalAndDoubleRepresentations_ProduceSameToken()
    {
        var integer = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["amount"] = ObservationValue.FromInt64(42)
        };
        var exact = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["amount"] = new ObservationValue(ObservationValueKind.Decimal, dec: 42m)
        };
        var floatingPoint = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["amount"] = ObservationValue.FromDouble(42d)
        };

        var integerToken = SnapshotTokenProjector.Compute(integer, ["amount"]);

        Assert.Equal(integerToken, SnapshotTokenProjector.Compute(exact, ["amount"]));
        Assert.Equal(integerToken, SnapshotTokenProjector.Compute(floatingPoint, ["amount"]));
    }

    [Fact]
    public void Compute_CanonicalIntegralDoubleAndParsedInt64Spelling_ProduceSameToken()
    {
        var floatingPoint = ObservationValue.FromDouble(Math.BitIncrement(1e18));
        using var document = JsonDocument.Parse(floatingPoint.GetRawText());
        var parsed = ObservationValue.FromJsonElement(document.RootElement);
        var floatingPointState = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["amount"] = floatingPoint
        };
        var parsedState = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["amount"] = parsed
        };

        Assert.Equal(ObservationValueKind.Int64, parsed.Kind);
        Assert.Equal(
            SnapshotTokenProjector.Compute(floatingPointState, ["amount"]),
            SnapshotTokenProjector.Compute(parsedState, ["amount"]));
    }

    [Fact]
    public void Compute_DoublesOutsideCanonicalDecimalDomain_PreserveBitwiseIdentity()
    {
        var first = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["amount"] = ObservationValue.FromDouble(1e-29)
        };
        var second = new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["amount"] = ObservationValue.FromDouble(Math.BitIncrement(1e-29))
        };

        Assert.NotEqual(
            SnapshotTokenProjector.Compute(first, ["amount"]),
            SnapshotTokenProjector.Compute(second, ["amount"]));
    }
}
