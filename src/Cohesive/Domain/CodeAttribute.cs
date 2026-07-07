namespace Cohesive.Domain;

/// <summary>
/// Associates a code value and an optional label and description with an enum case.
/// </summary>
[AttributeUsage(validOn: AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class CodeAttribute : Attribute
{
    /// <summary>
    /// Creates code metadata for one enum case.
    /// </summary>
    public CodeAttribute(string code)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
    }

    /// <summary>
    /// External code value.
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Optional human-readable label. When omitted, generators use the enum case name.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Optional free-form description.
    /// </summary>
    public string? Description { get; set; }
}
