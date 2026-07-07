using System.Numerics;

namespace Cohesive.Domain;

/// <summary>
/// Dimension for linear measurements (distance and length).
/// </summary>
public readonly record struct LengthDimension : IQuantityDimension;

public readonly record struct Centimeter<TRep> : IQuantityUnit<LengthDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    public static string Symbol => "cm";
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(0.01m);
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(0.01m);
}

public readonly record struct Millimeter<TRep> : IQuantityUnit<LengthDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    public static string Symbol => "mm";
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(0.001m);
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(0.001m);
}

public readonly record struct Foot<TRep> : IQuantityUnit<LengthDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    public static string Symbol => "ft";
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(0.3048m);
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(0.3048m);
}

public readonly record struct Inch<TRep> : IQuantityUnit<LengthDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    public static string Symbol => "in";
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(0.0254m);
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(0.0254m);
}

/// <summary>
/// Base unit for <see cref="LengthDimension"/>.
/// </summary>
public readonly record struct Meter<TRep> : IQuantityUnit<LengthDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    public static string Symbol => "m";
    public static TRep ToBase(TRep value) => value;
    public static TRep FromBase(TRep baseValue) => baseValue;
}

public readonly record struct Kilometer<TRep> : IQuantityUnit<LengthDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    public static string Symbol => "km";
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(1_000m);
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(1_000m);
}

public readonly record struct Mile<TRep> : IQuantityUnit<LengthDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    public static string Symbol => "mi";
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(1_609.344m);
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(1_609.344m);
}

/// <summary>
/// Transportation-friendly travel distance.
/// </summary>
/// <param name="Value">The distance quantity.</param>
[Quantity(defaultUnitType: typeof(Mile<decimal>), defaultFormat: "0.###")]
[QuantityUnitMember(unitType: typeof(Kilometer<decimal>), memberName: "Kilometers")]
[QuantityUnitMember(unitType: typeof(Mile<decimal>), memberName: "Miles")]
public readonly partial record struct Distance(Quantity<LengthDimension, decimal> Value)
    : IStructuredQuantity<Distance, LengthDimension, decimal>,
        IComparable<Distance>,
        IAdditionOperators<Distance, Distance, Distance>,
        ISubtractionOperators<Distance, Distance, Distance>,
        IAdditiveIdentity<Distance, Distance>
{
    /// <summary>
    /// The zero value for distance (aka: <see cref="AdditiveIdentity"/>).
    /// </summary>
    public static Distance Zero => AdditiveIdentity;
}
        
/// <summary>
/// Generic linear size or extent type.
/// </summary>
[Quantity(defaultUnitType: typeof(Meter<decimal>), defaultFormat: "0.###")]
[QuantityUnitMember(unitType: typeof(Meter<decimal>), memberName: "Meters")]
[QuantityUnitMember(unitType: typeof(Centimeter<decimal>), memberName: "Centimeters")]
[QuantityUnitMember(unitType: typeof(Millimeter<decimal>), memberName: "Millimeters")]
[QuantityUnitMember(unitType: typeof(Foot<decimal>), memberName: "Feet")]
[QuantityUnitMember(unitType: typeof(Inch<decimal>), memberName: "Inches")]
public readonly partial record struct Length(Quantity<LengthDimension, decimal> Value)
    : IStructuredQuantity<Length, LengthDimension, decimal>,
        IComparable<Length>,
        IAdditionOperators<Length, Length, Length>,
        ISubtractionOperators<Length, Length, Length>,
        IAdditiveIdentity<Length, Length>;