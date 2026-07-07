namespace Cohesive.Domain;

/// <summary>
/// Metadata for one named code-set value.
/// </summary>
/// <typeparam name="T">Code value type.</typeparam>
public readonly record struct CodeDefinition<T>
{
    /// <summary>
    /// Creates one code definition.
    /// </summary>
    public CodeDefinition(string name, T value, string label, string? description = null)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(value: name);
        Value = value;
        Label = NormalizeLabel(label: label, fallback: Name);
        Description = NormalizeOptional(value: description);
    }

    /// <summary>
    /// Static member name that defines the code.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Unique code value.
    /// </summary>
    public T Value { get; }

    /// <summary>
    /// Preferred human-readable label.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Optional free-form description.
    /// </summary>
    public string? Description { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Label} ({Value})";

    static string NormalizeLabel(string? label, string fallback)
    {
        var normalized = label?.Trim();
        return string.IsNullOrWhiteSpace(value: normalized) ? fallback : normalized;
    }

    static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(value: normalized) ? null : normalized;
    }
}
