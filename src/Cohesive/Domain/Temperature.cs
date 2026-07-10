using System.Numerics;

namespace Cohesive.Domain;

/// <summary>
/// Dimension for absolute thermodynamic temperature values.
/// </summary>
public readonly record struct TemperatureDimension : IQuantityDimension;

/// <summary>
/// Base unit for <see cref="TemperatureDimension"/> (absolute Kelvin scale).
/// </summary>
public readonly record struct Kelvin<TRep> : IQuantityUnit<TemperatureDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>Gets the unit symbol.</summary>
    public static string Symbol => "K";
    /// <summary>Converts the value to base.</summary>
    public static TRep ToBase(TRep value) => value;
    /// <summary>Creates a value from base.</summary>
    public static TRep FromBase(TRep baseValue) => baseValue;
}

/// <summary>Represents a struct.</summary>
public readonly record struct Celsius<TRep> : IQuantityUnit<TemperatureDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>Gets the unit symbol.</summary>
    public static string Symbol => "degC";
    /// <summary>Converts the value to base.</summary>
    public static TRep ToBase(TRep value) => value + TRep.CreateChecked(273.15m);
    /// <summary>Creates a value from base.</summary>
    public static TRep FromBase(TRep baseValue) => baseValue - TRep.CreateChecked(273.15m);
}

/// <summary>Represents a struct.</summary>
public readonly record struct Fahrenheit<TRep> : IQuantityUnit<TemperatureDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>Gets the unit symbol.</summary>
    public static string Symbol => "degF";

    /// <summary>Converts the value to base.</summary>
    public static TRep ToBase(TRep value) =>
        ((value - TRep.CreateChecked(32m)) * TRep.CreateChecked(5m) / TRep.CreateChecked(9m))
        + TRep.CreateChecked(273.15m);

    /// <summary>Creates a value from base.</summary>
    public static TRep FromBase(TRep baseValue) =>
        ((baseValue - TRep.CreateChecked(273.15m)) * TRep.CreateChecked(9m) / TRep.CreateChecked(5m))
        + TRep.CreateChecked(32m);
}

/// <summary>
/// Absolute temperature quantity backed by Kelvin as base unit.
/// </summary>
public readonly record struct Temperature(Quantity<TemperatureDimension, decimal> Value)
    : IStructuredQuantity<Temperature, TemperatureDimension, decimal>,
        IComparable<Temperature>,
        IAdditionOperators<Temperature, decimal, Temperature>,
        ISubtractionOperators<Temperature, decimal, Temperature>,
        IAdditiveIdentity<Temperature, Temperature>
{
    /// <summary>Creates a value from value.</summary>
    public static Temperature FromValue(Quantity<TemperatureDimension, decimal> value) => new(value);

    /// <summary>Creates a value from kelvin.</summary>
    public static Temperature FromKelvin(decimal value) => new(Quantity<TemperatureDimension, decimal>.From<Kelvin<decimal>>(value));

    /// <summary>Creates a value from celsius.</summary>
    public static Temperature FromCelsius(decimal value) => new(Quantity<TemperatureDimension, decimal>.From<Celsius<decimal>>(value));

    /// <summary>Creates a value from fahrenheit.</summary>
    public static Temperature FromFahrenheit(decimal value) => new(Quantity<TemperatureDimension, decimal>.From<Fahrenheit<decimal>>(value));

    /// <summary>Gets the kelvin.</summary>
    public decimal Kelvin => QuantityMath.As<Temperature, TemperatureDimension, decimal, Kelvin<decimal>>(quantity: this);

    /// <summary>Gets the celsius.</summary>
    public decimal Celsius => QuantityMath.As<Temperature, TemperatureDimension, decimal, Celsius<decimal>>(quantity: this);

    /// <summary>Gets the fahrenheit.</summary>
    public decimal Fahrenheit => QuantityMath.As<Temperature, TemperatureDimension, decimal, Fahrenheit<decimal>>(quantity: this);

    /// <inheritdoc />
    public int CompareTo(Temperature other) => QuantityMath.Compare<Temperature, TemperatureDimension, decimal>(left: this, right: other);

    /// <summary>Gets the additive identity.</summary>
    public static Temperature AdditiveIdentity => FromKelvin(0m);

    /// <inheritdoc />
    public override string ToString() => QuantityMath.Format<Temperature, TemperatureDimension, decimal, Celsius<decimal>>(quantity: this, format: "0.###");

    /// <summary>
    /// Adds a temperature offset in Kelvin.
    /// </summary>
    public static Temperature operator +(Temperature value, decimal kelvinOffset) => Temperature.FromKelvin(value.Kelvin + kelvinOffset);

    /// <summary>
    /// Subtracts a temperature offset in Kelvin.
    /// </summary>
    public static Temperature operator -(Temperature value, decimal kelvinOffset) => Temperature.FromKelvin(value.Kelvin - kelvinOffset);

    /// <summary>
    /// Returns the signed temperature difference in Kelvin.
    /// </summary>
    public static decimal operator -(Temperature left, Temperature right) => left.Kelvin - right.Kelvin;
}
