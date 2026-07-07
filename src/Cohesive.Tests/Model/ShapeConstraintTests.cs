using System.Text.Json;

namespace Cohesive.Tests.Model;

public sealed class ShapeConstraintTests
{
    [Fact]
    public void MinLengthConstraint_RejectsNegativeValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MinLengthConstraint(-1));
    }

    [Fact]
    public void ShapeConstraint_JsonRoundTripsMinLengthConstraint()
    {
        ShapeConstraint constraint = new MinLengthConstraint(
            value: 2,
            field: FieldPath.Parse("Customer.Name"),
            message: "Name must be at least two characters.");

        var json = JsonSerializer.Serialize(constraint);
        var roundTripped = Assert.IsType<MinLengthConstraint>(JsonSerializer.Deserialize<ShapeConstraint>(json));

        Assert.Contains("\"$constraint\":\"minLength\"", json, StringComparison.Ordinal);
        Assert.Equal(constraint, roundTripped);
    }
}
