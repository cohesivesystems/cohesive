namespace Cohesive.Domain;

/// <summary>
/// Marks a static code-set type, enum code set, or one code-set member for catalog generation.
/// </summary>
/// <remarks>
/// When applied to a static partial type, all public static constant or readonly fields are
/// included in the generated catalog. When applied to a field, the attribute supplies
/// member-specific metadata and can be used without a type-level attribute. When applied to
/// an enum, generated extension methods expose enum cases as code definitions.
/// </remarks>
[AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Enum | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class CodeSetAttribute : Attribute
{
    /// <summary>
    /// Marks a code-set type or code member using a generated label.
    /// </summary>
    public CodeSetAttribute()
    {
    }

    /// <summary>
    /// Marks a code member with an explicit human-readable label.
    /// </summary>
    public CodeSetAttribute(string label)
    {
        Label = NormalizeOptional(value: label);
    }

    /// <summary>
    /// Preferred human-readable label. When omitted, generators may derive a label from the field name.
    /// </summary>
    public string? Label { get; }

    /// <summary>
    /// Optional free-form description.
    /// </summary>
    public string? Description { get; set; }

    static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(value: normalized) ? null : normalized;
    }
}
