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
}
