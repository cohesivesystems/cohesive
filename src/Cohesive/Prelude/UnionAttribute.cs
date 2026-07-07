namespace Cohesive.Prelude;

/// <summary>
/// Marks a type for discriminated-union code generation.
/// </summary>
[AttributeUsage(validOn: AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class UnionAttribute : Attribute
{
    /// <summary>
    /// Creates a union attribute with an optional discriminator-property override.
    /// </summary>
    public UnionAttribute(string discriminatorPropertyName = "Type")
    {
        DiscriminatorPropertyName = string.IsNullOrWhiteSpace(value: discriminatorPropertyName) ? "Type" : discriminatorPropertyName;
    }

    /// <summary>
    /// Gets the tagged-union discriminator property name.
    /// </summary>
    public string DiscriminatorPropertyName { get; }
}
