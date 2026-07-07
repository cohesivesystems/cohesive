using System.Collections.Immutable;
using Cohesive.Prelude;

namespace Cohesive.CodeGen;

/// <summary>
/// A discovered set of literal constants that are part of a contracts assembly surface.
/// </summary>
public sealed record ContractConstantSet
{
    /// <summary>
    /// Creates a constant set.
    /// </summary>
    public ContractConstantSet(ImmutableArray<ContractConstantGroup> groups)
    {
        Groups = groups.IsDefault ? [] : groups;
    }

    /// <summary>
    /// Constant groups discovered from public static contract classes.
    /// </summary>
    public ImmutableArray<ContractConstantGroup> Groups { get; init; }
}

/// <summary>
/// A public static class containing literal contract constants.
/// </summary>
public sealed record ContractConstantGroup
{
    /// <summary>
    /// Creates a constant group.
    /// </summary>
    public ContractConstantGroup(string name, string? namespaceName, ImmutableArray<ContractConstant> constants)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        NamespaceName = string.IsNullOrWhiteSpace(namespaceName) ? null : namespaceName;
        Constants = constants.IsDefault ? [] : constants;
    }

    /// <summary>
    /// CLR type name that owns the constants.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// CLR namespace for the owning type, when available.
    /// </summary>
    public string? NamespaceName { get; init; }

    /// <summary>
    /// Constants in deterministic source order for target projection.
    /// </summary>
    public ImmutableArray<ContractConstant> Constants { get; init; }
}

/// <summary>
/// A literal contract constant field.
/// </summary>
public sealed record ContractConstant
{
    /// <summary>
    /// Creates a constant.
    /// </summary>
    public ContractConstant(string name, Type valueType, object value)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// CLR field name.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Literal CLR value type.
    /// </summary>
    public Type ValueType { get; init; }

    /// <summary>
    /// Literal value.
    /// </summary>
    public object Value { get; init; }
}
