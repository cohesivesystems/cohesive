using System.Text;

namespace Cohesive.Model;

/// <summary>
/// Defines the deterministic identifiers used for shapes and named types derived from CLR types.
/// </summary>
/// <remarks>
/// This convention is intentionally stateless and does not prescribe a caching lifetime. Consumers
/// that repeatedly resolve CLR metadata may cache the returned immutable identifiers at their own boundary.
/// </remarks>
public static class ClrShapeIdentityConvention
{
    /// <summary>Prefix for CLR-derived semantic shape identifiers.</summary>
    public const string ShapeIdPrefix = "clr:shape:";

    /// <summary>Prefix for CLR-derived named type identifiers.</summary>
    public const string TypeIdPrefix = "clr:type:";

    /// <summary>Gets the deterministic semantic shape identifier for a CLR type.</summary>
    /// <param name="clrType">CLR type represented by the semantic shape.</param>
    /// <returns>A shape identifier compatible with <see cref="ClrShapeGraphBuilder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clrType"/> is <see langword="null"/>.</exception>
    public static ShapeId GetShapeId(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return new(ShapeIdPrefix + GetClrTypeIdentity(clrType));
    }

    /// <summary>Gets the deterministic named type identifier for a CLR type.</summary>
    /// <param name="clrType">CLR type represented by the named type definition.</param>
    /// <returns>A named type identifier compatible with <see cref="ClrShapeGraphBuilder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clrType"/> is <see langword="null"/>.</exception>
    public static TypeId GetTypeId(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return new(TypeIdPrefix + GetClrTypeIdentity(clrType));
    }

    static string GetClrTypeIdentity(Type clrType)
    {
        if (!clrType.IsGenericType)
            return clrType.FullName ?? clrType.Name;

        var definition = clrType.GetGenericTypeDefinition();
        var definitionName = definition.FullName ?? definition.Name;
        var tickIndex = definitionName.IndexOf('`');
        if (tickIndex >= 0)
            definitionName = definitionName[..tickIndex];

        var arguments = clrType.GetGenericArguments();
        StringBuilder builder = new(definitionName);
        builder.Append('<');
        for (var i = 0; i < arguments.Length; i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append(GetClrTypeIdentity(arguments[i]));
        }

        builder.Append('>');
        return builder.ToString();
    }
}
