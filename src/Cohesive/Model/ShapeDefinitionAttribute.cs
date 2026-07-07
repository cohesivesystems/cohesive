namespace Cohesive.Model;

/// <summary>
/// Declares stable shape identity metadata for a CLR root shape type.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ShapeDefinitionAttribute : Attribute
{
    /// <summary>
    /// Creates shape definition metadata.
    /// </summary>
    public ShapeDefinitionAttribute(string id)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
    }

    /// <summary>
    /// Creates shape definition metadata with an explicit shape role.
    /// </summary>
    public ShapeDefinitionAttribute(string id, string role) : this(id)
    {
        Role = Guard.RequireNotNullOrWhiteSpace(role);
        HasRole = true;
    }

    /// <summary>
    /// Stable shape identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Shape role declared by the attribute when <see cref="HasRole"/> is true.
    /// </summary>
    public string? Role { get; }

    /// <summary>
    /// True when this attribute explicitly declares <see cref="Role"/>.
    /// </summary>
    public bool HasRole { get; }
}
