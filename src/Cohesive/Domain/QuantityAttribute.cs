namespace Cohesive.Domain;

/// <summary>
/// Marks a quantity struct for boilerplate generation.
/// </summary>
[AttributeUsage(validOn: AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class QuantityAttribute : Attribute
{
    /// <summary>
    /// Creates wrapper generation metadata.
    /// </summary>
    /// <param name="defaultUnitType">
    /// Unit used by generated <c>ToString()</c>.
    /// </param>
    /// <param name="defaultFormat">
    /// Numeric format used by generated <c>ToString()</c>.
    /// </param>
    public QuantityAttribute(Type defaultUnitType, string defaultFormat = "0.###")
    {
        DefaultUnitType = defaultUnitType;
        DefaultFormat = string.IsNullOrWhiteSpace(value: defaultFormat) ? "0.###" : defaultFormat;
    }

    /// <summary>
    /// Gets the default unit used by generated formatting.
    /// </summary>
    public Type DefaultUnitType { get; }

    /// <summary>
    /// Gets the numeric format used by generated formatting.
    /// </summary>
    public string DefaultFormat { get; }
}

/// <summary>
/// Declares a generated unit member for a quantity wrapper.
/// </summary>
[AttributeUsage(validOn: AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class QuantityUnitMemberAttribute : Attribute
{
    /// <summary>
    /// Declares one generated unit member.
    /// </summary>
    /// <param name="unitType">Concrete unit type (for example <c>typeof(Kilometer&lt;decimal&gt;)</c>).</param>
    /// <param name="memberName">
    /// Suffix used for generated APIs:
    /// <c>From{memberName}(...)</c> and property <c>{memberName}</c>.
    /// </param>
    public QuantityUnitMemberAttribute(Type unitType, string memberName)
    {
        UnitType = unitType;
        MemberName = string.IsNullOrWhiteSpace(value: memberName) ? throw new ArgumentException(message: "Member name is required.", paramName: nameof(memberName)) : memberName;
    }

    /// <summary>
    /// Gets the unit type for generated conversion members.
    /// </summary>
    public Type UnitType { get; }

    /// <summary>
    /// Gets the generated member name suffix.
    /// </summary>
    public string MemberName { get; }
}
