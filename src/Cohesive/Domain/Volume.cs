using System.Numerics;

namespace Cohesive.Domain;

/// <summary>
/// Dimension for volumetric values.
/// </summary>
public readonly record struct VolumeDimension : IQuantityDimension;

/// <summary>
/// Base unit for <see cref="VolumeDimension"/>.
/// </summary>
public readonly record struct Liter<TRep> : IQuantityUnit<VolumeDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>Gets the unit symbol.</summary>
    public static string Symbol => "L";
    /// <summary>Converts the value to base.</summary>
    public static TRep ToBase(TRep value) => value;
    /// <summary>Creates a value from base.</summary>
    public static TRep FromBase(TRep baseValue) => baseValue;
}

/// <summary>Represents a struct.</summary>
public readonly record struct Milliliter<TRep> : IQuantityUnit<VolumeDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>Gets the unit symbol.</summary>
    public static string Symbol => "mL";
    /// <summary>Converts the value to base.</summary>
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(0.001m);
    /// <summary>Creates a value from base.</summary>
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(0.001m);
}

/// <summary>Represents a struct.</summary>
public readonly record struct CubicMeter<TRep> : IQuantityUnit<VolumeDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>Gets the unit symbol.</summary>
    public static string Symbol => "m3";
    /// <summary>Converts the value to base.</summary>
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(1_000m);
    /// <summary>Creates a value from base.</summary>
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(1_000m);
}

/// <summary>Represents a struct.</summary>
public readonly record struct UsGallon<TRep> : IQuantityUnit<VolumeDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>Gets the unit symbol.</summary>
    public static string Symbol => "gal";
    /// <summary>Converts the value to base.</summary>
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(3.785411784m);
    /// <summary>Creates a value from base.</summary>
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(3.785411784m);
}

/// <summary>
/// Volume quantity.
/// </summary>
[Quantity(defaultUnitType: typeof(Liter<decimal>), defaultFormat: "0.###")]
[QuantityUnitMember(unitType: typeof(Liter<decimal>), memberName: "Liters")]
[QuantityUnitMember(unitType: typeof(Milliliter<decimal>), memberName: "Milliliters")]
[QuantityUnitMember(unitType: typeof(CubicMeter<decimal>), memberName: "CubicMeters")]
[QuantityUnitMember(unitType: typeof(UsGallon<decimal>), memberName: "UsGallons")]
public readonly partial record struct Volume(Quantity<VolumeDimension, decimal> Value)
    : IStructuredQuantity<Volume, VolumeDimension, decimal>,
        IComparable<Volume>,
        IAdditionOperators<Volume, Volume, Volume>,
        ISubtractionOperators<Volume, Volume, Volume>,
        IAdditiveIdentity<Volume, Volume>;
