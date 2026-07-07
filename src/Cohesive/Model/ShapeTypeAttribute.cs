namespace Cohesive.Model;

/// <summary>
/// Declares stable named type metadata for a CLR type used in shape inference.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class ShapeTypeAttribute : Attribute
{
    /// <summary>
    /// Creates named type metadata.
    /// </summary>
    public ShapeTypeAttribute(string id)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
    }

    /// <summary>
    /// Stable named type identifier.
    /// </summary>
    public string Id { get; }

}
