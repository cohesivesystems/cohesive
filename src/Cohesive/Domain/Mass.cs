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
    public static string Symbol => "kg";
    public static TRep ToBase(TRep value) => value;
    public static TRep FromBase(TRep baseValue) => baseValue;
}

public readonly record struct Gram<TRep> : IQuantityUnit<MassDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    public static string Symbol => "g";
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(0.001m);
    public static TRep FromBase(TRep baseValue) => baseValue / TRep.CreateChecked(0.001m);
}

public readonly record struct Pound<TRep> : IQuantityUnit<MassDimension, TRep>
    where TRep : IFloatingPoint<TRep>
{
    public static string Symbol => "lb";
    public static TRep ToBase(TRep value) => value * TRep.CreateChecked(0.45359237m);
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
    public static implicit operator Mass(Weight value) => Mass.FromValue(value: value.Value);

    public static implicit operator Weight(Mass value) => FromValue(value: value.Value);
}
