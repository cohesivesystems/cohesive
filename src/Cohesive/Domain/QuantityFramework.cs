using System.Globalization;
using System.Numerics;

namespace Cohesive.Domain;

/// <summary>
/// Marker interface for a quantity dimension (for example, length or mass).
/// </summary>
public interface IQuantityDimension;

/// <summary>
/// Defines conversion behavior for a unit inside a dimension.
/// </summary>
/// <typeparam name="TDimension">The dimension/unit type</typeparam>
/// <typeparam name="TRep">The underlying numeric representation type</typeparam>
// ReSharper disable once UnusedTypeParameter
public interface IQuantityUnit<TDimension, TRep>
    where TDimension : struct, IQuantityDimension
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>
    /// Unit symbol used for display (for example, "km").
    /// </summary>
    static abstract string Symbol { get; }

    /// <summary>
    /// Converts a unit value into the canonical base unit for the dimension.
    /// </summary>
    static abstract TRep ToBase(TRep value);

    /// <summary>
    /// Converts a canonical base value into this unit.
    /// </summary>
    static abstract TRep FromBase(TRep baseValue);
}

/// <summary>
/// Canonical quantity value for a specific dimension, stored in base units.
/// </summary>
/// <typeparam name="TDimension">The dimension/unit type</typeparam>
/// <typeparam name="TRep">The underlying numeric representation type</typeparam>
public readonly record struct Quantity<TDimension, TRep>
    : IComparable<Quantity<TDimension, TRep>>,
        IAdditionOperators<Quantity<TDimension, TRep>, Quantity<TDimension, TRep>, Quantity<TDimension, TRep>>,
        ISubtractionOperators<Quantity<TDimension, TRep>, Quantity<TDimension, TRep>, Quantity<TDimension, TRep>>,
        IAdditiveIdentity<Quantity<TDimension, TRep>, Quantity<TDimension, TRep>>
    where TDimension : struct, IQuantityDimension
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>
    /// Creates a quantity from a canonical base-unit value.
    /// </summary>
    public Quantity(TRep baseValue)
    {
        BaseValue = baseValue;
    }

    /// <summary>
    /// Value in canonical base units for <typeparamref name="TDimension"/>.
    /// </summary>
    public TRep BaseValue { get; }

    /// <summary>
    /// Zero quantity.
    /// </summary>
    public static Quantity<TDimension, TRep> Zero => new(TRep.Zero);

    /// <inheritdoc />
    public static Quantity<TDimension, TRep> AdditiveIdentity => Zero;

    /// <summary>
    /// Creates a quantity from a canonical base-unit value.
    /// </summary>
    public static Quantity<TDimension, TRep> FromBase(TRep baseValue) => new(baseValue);

    /// <summary>
    /// Creates a quantity from a concrete unit.
    /// </summary>
    public static Quantity<TDimension, TRep> From<TUnit>(TRep value)
        where TUnit : struct, IQuantityUnit<TDimension, TRep>
        => new(TUnit.ToBase(value));

    /// <summary>
    /// Converts this quantity into a concrete unit.
    /// </summary>
    public TRep As<TUnit>()
        where TUnit : struct, IQuantityUnit<TDimension, TRep>
        => TUnit.FromBase(BaseValue);

    /// <summary>
    /// Formats this quantity in a concrete unit.
    /// </summary>
    public string Format<TUnit>(string? format = null, IFormatProvider? formatProvider = null)
        where TUnit : struct, IQuantityUnit<TDimension, TRep>
    {
        var provider = formatProvider ?? CultureInfo.InvariantCulture;
        var numeric = As<TUnit>().ToString(format, provider);
        return $"{numeric} {TUnit.Symbol}";
    }

    /// <inheritdoc />
    public int CompareTo(Quantity<TDimension, TRep> other) => BaseValue.CompareTo(other.BaseValue);

    /// <inheritdoc />
    public override string ToString() => BaseValue.ToString(format: null, formatProvider: CultureInfo.InvariantCulture);

    public static Quantity<TDimension, TRep> operator +(Quantity<TDimension, TRep> left, Quantity<TDimension, TRep> right) => new(left.BaseValue + right.BaseValue);

    public static Quantity<TDimension, TRep> operator -(Quantity<TDimension, TRep> left, Quantity<TDimension, TRep> right) => new(left.BaseValue - right.BaseValue);

    public static Quantity<TDimension, TRep> operator -(Quantity<TDimension, TRep> value) => new(-value.BaseValue);

    public static Quantity<TDimension, TRep> operator *(Quantity<TDimension, TRep> value, TRep scalar) => new(value.BaseValue * scalar);

    public static Quantity<TDimension, TRep> operator *(TRep scalar, Quantity<TDimension, TRep> value) => value * scalar;

    /// <exception cref="DivideByZeroException"></exception>
    public static Quantity<TDimension, TRep> operator /(Quantity<TDimension, TRep> value, TRep scalar)
    {
        if (scalar == TRep.Zero)
            throw new DivideByZeroException(message: "Cannot divide a quantity by zero.");

        return new(value.BaseValue / scalar);
    }

    /// <exception cref="DivideByZeroException"></exception>
    public static TRep operator /(Quantity<TDimension, TRep> left, Quantity<TDimension, TRep> right)
    {
        if (right.BaseValue == TRep.Zero)
            throw new DivideByZeroException(message: "Cannot compute a ratio against a zero quantity.");

        return left.BaseValue / right.BaseValue;
    }
}

/// <summary>
/// Contract for user-defined quantity structs that wrap a canonical quantity value.
/// </summary>
public interface IStructuredQuantity<out TSelf, TDimension, TRep>
    where TSelf : struct, IStructuredQuantity<TSelf, TDimension, TRep>
    where TDimension : struct, IQuantityDimension
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>
    /// Wrapped canonical quantity.
    /// </summary>
    Quantity<TDimension, TRep> Value { get; }

    /// <summary>
    /// Factory used by generic helper operations.
    /// </summary>
    static abstract TSelf FromValue(Quantity<TDimension, TRep> value);
}

/// <summary>
/// Reusable operations for quantity structs implementing <see cref="IStructuredQuantity{TSelf,TDimension,TRep}"/>.
/// </summary>
public static class QuantityMath
{
    /// <summary>
    /// Converts a structured quantity to a concrete unit.
    /// </summary>
    public static TRep As<TQuantity, TDimension, TRep, TUnit>(TQuantity quantity)
        where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
        where TDimension : struct, IQuantityDimension
        where TRep : IFloatingPoint<TRep>
        where TUnit : struct, IQuantityUnit<TDimension, TRep>
        => quantity.Value.As<TUnit>();

    /// <summary>
    /// Formats a structured quantity using a string.
    /// </summary>
    public static string Format<TQuantity, TDimension, TRep, TUnit>(
        TQuantity quantity,
        string? format = null,
        IFormatProvider? formatProvider = null)
        where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
        where TDimension : struct, IQuantityDimension
        where TRep : IFloatingPoint<TRep>
        where TUnit : struct, IQuantityUnit<TDimension, TRep>
        => quantity.Value.Format<TUnit>(format: format, formatProvider: formatProvider);

    /// <summary>
    /// Adds two structured quantities.
    /// </summary>
    public static TQuantity Add<TQuantity, TDimension, TRep>(TQuantity left, TQuantity right)
        where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
        where TDimension : struct, IQuantityDimension
        where TRep : IFloatingPoint<TRep>
        => TQuantity.FromValue(left.Value + right.Value);

    /// <summary>
    /// Subtracts two structured quantities.
    /// </summary>
    public static TQuantity Subtract<TQuantity, TDimension, TRep>(TQuantity left, TQuantity right)
        where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
        where TDimension : struct, IQuantityDimension
        where TRep : IFloatingPoint<TRep>
        => TQuantity.FromValue(left.Value - right.Value);

    /// <summary>
    /// Negates a structured quantity.
    /// </summary>
    public static TQuantity Negate<TQuantity, TDimension, TRep>(TQuantity value)
        where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
        where TDimension : struct, IQuantityDimension
        where TRep : IFloatingPoint<TRep>
        => TQuantity.FromValue(-value.Value);

    /// <summary>
    /// Multiplies a structured quantity by a scalar.
    /// </summary>
    public static TQuantity Scale<TQuantity, TDimension, TRep>(TQuantity value, TRep scalar)
        where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
        where TDimension : struct, IQuantityDimension
        where TRep : IFloatingPoint<TRep>
        => TQuantity.FromValue(value.Value * scalar);

    /// <summary>
    /// Divides a structured quantity by a scalar.
    /// </summary>
    /// <exception cref="DivideByZeroException"></exception>
    public static TQuantity Divide<TQuantity, TDimension, TRep>(TQuantity value, TRep scalar)
        where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
        where TDimension : struct, IQuantityDimension
        where TRep : IFloatingPoint<TRep>
        => TQuantity.FromValue(value.Value / scalar);

    /// <summary>
    /// Computes the ratio between two structured quantities.
    /// </summary>
    /// <exception cref="DivideByZeroException"></exception>
    public static TRep Ratio<TQuantity, TDimension, TRep>(TQuantity left, TQuantity right)
        where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
        where TDimension : struct, IQuantityDimension
        where TRep : IFloatingPoint<TRep>
        => left.Value / right.Value;

    /// <summary>
    /// Compares two structured quantities.
    /// </summary>
    public static int Compare<TQuantity, TDimension, TRep>(TQuantity left, TQuantity right)
        where TQuantity : struct, IStructuredQuantity<TQuantity, TDimension, TRep>
        where TDimension : struct, IQuantityDimension
        where TRep : IFloatingPoint<TRep>
        => left.Value.CompareTo(right.Value);
}
