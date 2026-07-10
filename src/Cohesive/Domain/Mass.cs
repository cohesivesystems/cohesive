using System.Numerics;

namespace Cohesive.Domain;

/// <summary>
/// Dimension for mass/weight values.
/// </summary>
public readonly record struct MassDimension : IQuantityDimension;

/// <summary>
/// Base unit for <see cref="MassDimension"/>.
/// </summary>
public readonly record struct Kilogram<TRep> : IQuantityUnit<MassDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>Gets the unit symbol.</summary>
    public static string Symbol => "kg";
    /// <summary>Converts the value to base.</summary>
    public static TRep ToBase(TRep value) => value;
    /// <summary>Creates a value from base.</summary>
    public static TRep FromBase(TRep baseValue) => baseValue;
}

/// <summary>Represents a struct.</summary>
public readonly record struct Gram<TRep> : IQuantityUnit<MassDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>Gets the unit symbol.</summary>
    public static string Symbol => "g";
    /// <summary>Converts the value to base.</summary>
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(0.001m);
    /// <summary>Creates a value from base.</summary>
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(0.001m);
}

/// <summary>Represents a struct.</summary>
public readonly record struct Pound<TRep> : IQuantityUnit<MassDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    /// <summary>Gets the unit symbol.</summary>
    public static string Symbol => "lb";
    /// <summary>Converts the value to base.</summary>
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(0.45359237m);
    /// <summary>Creates a value from base.</summary>
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(0.45359237m);
}

/// <summary>
/// Mass quantity (usable for weight in most transportation workflows).
/// </summary>
[Quantity(defaultUnitType: typeof(Kilogram<decimal>), defaultFormat: "0.###")]
[QuantityUnitMember(unitType: typeof(Kilogram<decimal>), memberName: "Kilograms")]
[QuantityUnitMember(unitType: typeof(Gram<decimal>), memberName: "Grams")]
[QuantityUnitMember(unitType: typeof(Pound<decimal>), memberName: "Pounds")]
public readonly partial record struct Mass(Quantity<MassDimension, decimal> Value)
    : IStructuredQuantity<Mass, MassDimension, decimal>,
        IComparable<Mass>,
        IAdditionOperators<Mass, Mass, Mass>,
        ISubtractionOperators<Mass, Mass, Mass>,
        IAdditiveIdentity<Mass, Mass>;
        
/// <summary>
/// Weight quantity alias over <see cref="MassDimension"/> for domain readability.
/// </summary>
[Quantity(defaultUnitType: typeof(Kilogram<decimal>), defaultFormat: "0.###")]
[QuantityUnitMember(unitType: typeof(Kilogram<decimal>), memberName: "Kilograms")]
[QuantityUnitMember(unitType: typeof(Gram<decimal>), memberName: "Grams")]
[QuantityUnitMember(unitType: typeof(Pound<decimal>), memberName: "Pounds")]
public readonly partial record struct Weight(Quantity<MassDimension, decimal> Value)
    : IStructuredQuantity<Weight, MassDimension, decimal>,
        IComparable<Weight>,
        IAdditionOperators<Weight, Weight, Weight>,
        ISubtractionOperators<Weight, Weight, Weight>,
        IAdditiveIdentity<Weight, Weight>
{
    /// <summary>Converts a weight to its equivalent mass.</summary>
    public static implicit operator Mass(Weight value) => Mass.FromValue(value: value.Value);

    /// <summary>Converts a mass to its equivalent weight.</summary>
    public static implicit operator Weight(Mass value) => FromValue(value: value.Value);
}
