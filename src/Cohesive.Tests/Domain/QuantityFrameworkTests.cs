using System.Numerics;

namespace Cohesive.Tests.Domain;

/// <summary>
/// Tests for the reusable quantity framework and built-in quantity types.
/// </summary>
public sealed class QuantityFrameworkTests
{
    [Fact]
    public void Length_Addition_UsesCanonicalBaseConversion()
    {
        var length = Length.FromMeters(value: 2.5m) + Length.FromCentimeters(value: 75m);

        Assert.Equal(expected: 3.25m, actual: length.Meters);
        Assert.Equal(expected: 325m, actual: length.Centimeters);
    }

    [Fact]
    public void Mass_ConvertsPoundsToKilograms()
    {
        var mass = Mass.FromPounds(value: 220.4622622m);

        Assert.InRange(actual: mass.Kilograms, low: 99.99999m, high: 100.00001m);
        Assert.InRange(actual: mass.Grams, low: 99_999.99m, high: 100_000.01m);
    }

    [Fact]
    public void Weight_ImplicitlyConvertsToAndFromMass()
    {
        var weight = Weight.FromPounds(value: 100m);
        Mass mass = weight;
        Weight roundTrip = mass;

        Assert.Equal(expected: weight.Kilograms, actual: roundTrip.Kilograms);
    }

    [Fact]
    public void Volume_ConvertsUsGallonsToLiters()
    {
        var volume = Volume.FromUsGallons(value: 10m);

        Assert.InRange(actual: volume.Liters, low: 37.8541m, high: 37.8542m);
        Assert.InRange(actual: volume.CubicMeters, low: 0.03785m, high: 0.03786m);
    }

    [Fact]
    public void Temperature_ConvertsAcrossScales()
    {
        var boiling = Temperature.FromCelsius(value: 100m);

        Assert.InRange(actual: boiling.Kelvin, low: 373.1499m, high: 373.1501m);
        Assert.InRange(actual: boiling.Fahrenheit, low: 211.9999m, high: 212.0001m);
    }

    [Fact]
    public void Temperature_Difference_ReturnsKelvinDelta()
    {
        var morning = Temperature.FromCelsius(value: 12m);
        var afternoon = Temperature.FromFahrenheit(value: 77m);

        var delta = afternoon - morning;

        Assert.InRange(actual: delta, low: 12.9999m, high: 13.0001m);
    }

    [Fact]
    public void StructuredQuantity_CanReuseGenericHelpers()
    {
        var leg1 = RouteDistance.FromKilometers(value: 120m);
        var leg2 = RouteDistance.FromMiles(value: 10m);
        var total = leg1 + leg2;

        Assert.InRange(actual: total.Kilometers, low: 136.0934m, high: 136.0935m);
        Assert.True(condition: total > leg1);
    }

    [Fact]
    public void Quantity_CanUseDoubleRepresentation()
    {
        var distance = Quantity<LengthDimension, double>.From<Kilometer<double>>(100d);
        var withExtraMeters = distance + Quantity<LengthDimension, double>.From<Meter<double>>(500d);

        Assert.InRange(actual: distance.As<Mile<double>>(), low: 62.1371d, high: 62.1372d);
        Assert.Equal(expected: 100_500d, actual: withExtraMeters.BaseValue, precision: 8);
        Assert.Equal(expected: Quantity<LengthDimension, double>.Zero, actual: Quantity<LengthDimension, double>.AdditiveIdentity);
    }

    [Fact]
    public void Distance_ImplementsAdditiveIdentityAndAdditionOperators()
    {
        var leg1 = Distance.FromKilometers(value: 2m);
        var leg2 = Distance.FromMiles(value: 1m);
        var total = Sum(left: leg1, right: leg2);

        Assert.InRange(actual: total.Kilometers, low: 3.6093m, high: 3.6094m);
    }

    [Fact]
    public void Quantity_DividingByZeroScalar_Throws()
    {
        Assert.Throws<DivideByZeroException>(
            testCode: () => _ = Length.FromMeters(value: 10m) / 0m);
    }

    static TQuantity Sum<TQuantity>(TQuantity left, TQuantity right)
        where TQuantity : IAdditionOperators<TQuantity, TQuantity, TQuantity>, IAdditiveIdentity<TQuantity, TQuantity>
        => TQuantity.AdditiveIdentity + left + right;

    readonly record struct RouteDistance(Quantity<LengthDimension, decimal> Value)
        : IStructuredQuantity<RouteDistance, LengthDimension, decimal>, IComparable<RouteDistance>
    {
        public static RouteDistance FromValue(Quantity<LengthDimension, decimal> value) => new(value);

        public static RouteDistance FromKilometers(decimal value) => new(Quantity<LengthDimension, decimal>.From<Kilometer<decimal>>(value));

        public static RouteDistance FromMiles(decimal value) => new(Quantity<LengthDimension, decimal>.From<Mile<decimal>>(value));

        public decimal Kilometers => QuantityMath.As<RouteDistance, LengthDimension, decimal, Kilometer<decimal>>(quantity: this);

        public int CompareTo(RouteDistance other) => QuantityMath.Compare<RouteDistance, LengthDimension, decimal>(left: this, right: other);

        public static RouteDistance operator +(RouteDistance left, RouteDistance right) => QuantityMath.Add<RouteDistance, LengthDimension, decimal>(left: left, right: right);

        public static bool operator >(RouteDistance left, RouteDistance right) => left.CompareTo(other: right) > 0;

        public static bool operator <(RouteDistance left, RouteDistance right) => left.CompareTo(other: right) < 0;
    }
}
