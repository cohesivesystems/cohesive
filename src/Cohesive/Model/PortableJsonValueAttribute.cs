namespace Cohesive.Model;

using System.Collections.Concurrent;
using System.Reflection;

/// <summary>
/// Declares that a CLR type is canonically represented as one portable JSON value rather than as an inferred CLR
/// object shape.
/// </summary>
/// <remarks>
/// Apply this only when JSON serialization is the type's semantic representation and the type's own schema or
/// validator owns its internal structure. The declaration is part of canonical contract inference: serializer
/// settings that change the represented JSON must therefore remain deterministic.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class PortableJsonValueAttribute : Attribute
{
    static readonly ConcurrentDictionary<Type, JsonTypeKind?> KindByClrType = [];

    /// <summary>Creates a portable JSON value declaration.</summary>
    /// <param name="kind">The JSON value shape guaranteed by every serialized instance.</param>
    public PortableJsonValueAttribute(JsonTypeKind kind = JsonTypeKind.Any)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A portable JSON value requires a known JSON kind.");

        Kind = kind;
    }

    /// <summary>The canonical JSON value shape guaranteed by the declared CLR type.</summary>
    public JsonTypeKind Kind { get; }

    /// <summary>Finds the explicit portable JSON contract declared by a CLR type.</summary>
    /// <param name="clrType">The CLR type whose declaration is inspected.</param>
    /// <param name="kind">The declared JSON root kind when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the type declares a portable JSON value contract; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clrType"/> is <see langword="null"/>.</exception>
    public static bool TryGetKind(Type clrType, out JsonTypeKind kind)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        var declaredKind = KindByClrType.GetOrAdd(
            clrType,
            static type => type.GetCustomAttribute<PortableJsonValueAttribute>(inherit: false)?.Kind);
        kind = declaredKind.GetValueOrDefault();
        return declaredKind.HasValue;
    }
}
