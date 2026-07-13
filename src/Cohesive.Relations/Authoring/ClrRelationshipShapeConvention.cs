using System.Collections.Concurrent;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Deterministically qualifies CLR types for convention-authored relationships.
/// </summary>
/// <remarks>
/// The shape portion follows the <c>clr:shape:</c> identity emitted by
/// <see cref="ClrShapeGraphBuilder"/>. The graph portion is scoped by the CLR type's
/// stable assembly name under a versioned convention, so authoring never depends on a
/// process-random graph identifier. This convenience convention treats each assembly name
/// as a graph; use explicit identifiers for independently versioned or composed graph snapshots.
/// Use explicit <see cref="QualifiedShapeId"/> builder overloads when a persisted or imported
/// shape graph supplies the canonical identifiers.
/// </remarks>
public static class ClrRelationshipShapeConvention
{
    /// <summary>Prefix for graph identifiers derived from CLR assembly identities.</summary>
    public const string GraphIdPrefix = "clr:graph:assembly/v1:";

    /// <summary>Prefix used by <see cref="ClrShapeGraphBuilder"/> for CLR-derived shape identifiers.</summary>
    public const string ShapeIdPrefix = ClrShapeIdentityConvention.ShapeIdPrefix;

    static readonly ConcurrentDictionary<Type, QualifiedShapeId> QualifiedShapes = [];

    /// <summary>Gets the deterministic convention identifier for a CLR shape.</summary>
    /// <typeparam name="T">CLR type represented by the shape.</typeparam>
    /// <returns>The assembly-scoped, graph-qualified shape identifier for <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The CLR type's assembly does not expose a stable name.
    /// </exception>
    public static QualifiedShapeId GetQualifiedShapeId<T>() where T : notnull =>
        GetQualifiedShapeId(typeof(T));

    internal static QualifiedShapeId GetQualifiedShapeId(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        var normalizedType = Nullable.GetUnderlyingType(clrType) ?? clrType;
        return QualifiedShapes.GetOrAdd(normalizedType, static type => CreateQualifiedShapeId(type));
    }

    static QualifiedShapeId CreateQualifiedShapeId(Type clrType)
    {
        var assemblyIdentity = clrType.Assembly.GetName().Name;
        if (string.IsNullOrWhiteSpace(assemblyIdentity))
        {
            throw new InvalidOperationException(
                $"CLR type '{clrType}' belongs to an assembly without a stable name.");
        }

        return new(
            new GraphId(GraphIdPrefix + assemblyIdentity),
            ClrShapeIdentityConvention.GetShapeId(clrType));
    }
}
