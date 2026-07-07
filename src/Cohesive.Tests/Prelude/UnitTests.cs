using System.Text.Json;

namespace Cohesive.Tests.Prelude;

/// <summary>
/// Unit tests for the functional Unit type.
/// </summary>
public sealed class UnitTests
{
    [Fact]
    public void Unit_DefaultAndValue_AreEqual()
    {
        var @default = default(Unit);
        var value = Unit.Value;

        Assert.Equal(expected: @default, actual: value);
        Assert.Equal(expected: @default.GetHashCode(), actual: value.GetHashCode());
    }

    [Fact]
    public void Unit_ToString_ReturnsParenPair()
    {
        Assert.Equal(expected: "()", actual: Unit.Value.ToString());
    }

    [Fact]
    public void Unit_CanBeUsedAsExplicitEmptyReturnValue()
    {
        var counter = 0;
        var result = Run(action: () => counter++);

        Assert.Equal(expected: 1, actual: counter);
        Assert.Equal(expected: Unit.Value, actual: result);
    }

    [Fact]
    public void Unit_JsonSerialization_UsesUnitLiteral()
    {
        var json = JsonSerializer.Serialize(value: Unit.Value);
        Assert.Equal(expected: "\"()\"", actual: json);
    }

    [Fact]
    public void Unit_JsonDeserialization_FromUnitLiteral_ReturnsUnitValue()
    {
        var unit = JsonSerializer.Deserialize<Unit>(json: "\"()\"");
        Assert.Equal(expected: Unit.Value, actual: unit);
    }

    [Fact]
    public void Unit_JsonDeserialization_FromNull_ReturnsUnitValue()
    {
        var unit = JsonSerializer.Deserialize<Unit>(json: "null");
        Assert.Equal(expected: Unit.Value, actual: unit);
    }

    static Unit Run(Action action)
    {
        action();
        return Unit.Value;
    }
}
