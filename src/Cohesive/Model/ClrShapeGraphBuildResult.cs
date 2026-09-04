using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;

namespace Cohesive.Model;

/// <summary>
/// Identifies how CLR shape inference selected a portable semantic identity.
/// </summary>
public enum ClrShapeIdentityOrigin
{
    /// <summary>
    /// The identity was selected by the deterministic built-in CLR convention.
    /// </summary>
    Convention = 0,

    /// <summary>
    /// The identity was supplied by an attribute or configured metadata provider.
    /// </summary>
    Metadata = 1
}

/// <summary>
/// Immutable result of CLR shape inference, including the effective semantic identities selected
/// by the builder's metadata providers.
/// </summary>
/// <remarks>
/// The reflection maps are authoring metadata. They support deterministic lowering from CLR
/// expressions but are not part of the portable <see cref="ShapeGraph"/>.
/// </remarks>
public sealed class ClrShapeGraphBuildResult
{
    internal ClrShapeGraphBuildResult(
        ShapeGraph graph,
        ImmutableDictionary<Type, TypeId> typeIds,
        ImmutableDictionary<Type, ShapeId> shapeIds,
        ImmutableDictionary<PropertyInfo, FieldName> fieldNames,
        ImmutableDictionary<Type, ClrShapeIdentityOrigin> shapeIdentityOrigins,
        ImmutableDictionary<PropertyInfo, ClrShapeIdentityOrigin> fieldIdentityOrigins)
    {
        Graph = Guard.RequireNotNull(graph);
        TypeIds = Guard.RequireNotNull(typeIds);
        ShapeIds = Guard.RequireNotNull(shapeIds);
        FieldNames = Guard.RequireNotNull(fieldNames);
        ShapeIdentityOrigins = Guard.RequireNotNull(shapeIdentityOrigins);
        FieldIdentityOrigins = Guard.RequireNotNull(fieldIdentityOrigins);
    }

    /// <summary>
    /// Gets the immutable semantic graph derived from the registered CLR roots.
    /// </summary>
    public ShapeGraph Graph { get; }

    /// <summary>
    /// Gets the effective named-type identity for every CLR type discovered by the build.
    /// </summary>
    public ImmutableDictionary<Type, TypeId> TypeIds { get; }

    /// <summary>
    /// Gets the effective local shape identity for every CLR type registered as a root.
    /// </summary>
    public ImmutableDictionary<Type, ShapeId> ShapeIds { get; }

    /// <summary>
    /// Gets the effective semantic field name for every readable CLR property discovered by the build.
    /// </summary>
    public ImmutableDictionary<PropertyInfo, FieldName> FieldNames { get; }

    /// <summary>
    /// Gets whether each registered root shape identity came from convention or configured metadata.
    /// </summary>
    public ImmutableDictionary<Type, ClrShapeIdentityOrigin> ShapeIdentityOrigins { get; }

    /// <summary>
    /// Gets whether each readable CLR property's field identity came from convention or configured metadata.
    /// </summary>
    public ImmutableDictionary<PropertyInfo, ClrShapeIdentityOrigin> FieldIdentityOrigins { get; }

    /// <summary>
    /// Resolves a CLR type to the portable semantic type selected during this build.
    /// </summary>
    /// <param name="clrType">
    /// CLR type to resolve. Scalar and scalar-collection types need not have been registered; named
    /// types and enums must have been discovered by this build.
    /// </param>
    /// <returns>The portable semantic type reference for <paramref name="clrType"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clrType"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="clrType"/> requires a named type that was not discovered by this build, or the
    /// CLR type is not supported by CLR shape inference.
    /// </exception>
    public TypeRef GetTypeRef(Type clrType) => ClrShapeGraphBuilder.ResolveTypeRef(clrType, TypeIds);

    /// <summary>
    /// Gets the exact graph-scoped root shape inferred for a CLR type.
    /// </summary>
    /// <typeparam name="T">CLR root type registered when this result was built.</typeparam>
    /// <returns>The graph object and effective shape identity associated with <typeparamref name="T"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> was not registered as a root shape in this build.
    /// </exception>
    public GraphShapeId GetShape<T>()
    {
        var clrType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (!ShapeIds.TryGetValue(clrType, out var shapeId))
        {
            throw new InvalidOperationException(
                $"CLR type '{clrType.FullName}' was not registered as a root shape in graph '{Graph.Id.Value}'.");
        }

        return new(Graph, shapeId);
    }

    /// <summary>
    /// Resolves a typed CLR member selector through the effective metadata captured by this build.
    /// </summary>
    /// <typeparam name="TRoot">CLR root type from which the selector starts.</typeparam>
    /// <typeparam name="TValue">CLR value type returned by the selected member path.</typeparam>
    /// <param name="selector">Direct or nested readable property selector rooted at its lambda parameter.</param>
    /// <returns>The effective canonical field path selected by this build.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="selector"/> is not a readable property chain rooted at its lambda parameter.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A selected property was not discovered by this CLR shape build.
    /// </exception>
    public FieldPath ResolveMemberPath<TRoot, TValue>(Expression<Func<TRoot, TValue>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        Expression current = selector.Body;
        while (current is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
            } conversion)
        {
            current = conversion.Operand;
        }

        List<PropertyInfo> reversed = [];
        while (current is MemberExpression member)
        {
            if (member.Member is not PropertyInfo property)
            {
                throw new ArgumentException(
                    "A semantic field selector must use readable CLR properties.",
                    nameof(selector));
            }

            reversed.Add(property);
            current = member.Expression
                ?? throw new ArgumentException(
                    "A semantic field selector cannot use a static member.",
                    nameof(selector));
        }

        if (!ReferenceEquals(current, selector.Parameters[0]) || reversed.Count == 0)
        {
            throw new ArgumentException(
                "A semantic field selector must be a property chain rooted at its lambda parameter.",
                nameof(selector));
        }

        reversed.Reverse();
        return ResolveMemberPath(typeof(TRoot), reversed);
    }

    /// <summary>
    /// Resolves an ordered CLR property chain to the effective semantic field path selected by this build.
    /// </summary>
    /// <param name="rootType">CLR type from which the member chain starts.</param>
    /// <param name="members">Ordered properties from the root toward the terminal value.</param>
    /// <returns>A field path containing the effective metadata-derived name of each property.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="rootType"/> or <paramref name="members"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="members"/> is empty, contains a null property, or is not a valid property chain
    /// rooted at <paramref name="rootType"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A member was not discovered by this build and therefore has no effective semantic field name.
    /// </exception>
    public FieldPath ResolveMemberPath(Type rootType, IReadOnlyList<PropertyInfo> members)
    {
        ArgumentNullException.ThrowIfNull(rootType);
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0)
            throw new ArgumentException("A member path requires at least one CLR property.", nameof(members));

        var currentType = UnwrapNullable(rootType);
        var segments = ImmutableArray.CreateBuilder<FieldPathSegment>(members.Count);
        for (var index = 0; index < members.Count; index++)
        {
            var property = members[index]
                ?? throw new ArgumentException("A member path cannot contain a null CLR property.", nameof(members));
            var declaringType = property.DeclaringType;
            if (declaringType is null || !declaringType.IsAssignableFrom(currentType))
            {
                throw new ArgumentException(
                    $"Property '{property.Name}' is not reachable from CLR type '{currentType}'.",
                    nameof(members));
            }

            if (!TryGetFieldName(property, out var fieldName))
            {
                throw new InvalidOperationException(
                    $"CLR property '{declaringType.FullName}.{property.Name}' was not discovered by shape inference.");
            }

            segments.Add(FieldPathSegment.ForField(fieldName.Value));
            currentType = UnwrapNullable(property.PropertyType);
        }

        return new(segments.MoveToImmutable());
    }

    bool TryGetFieldName(PropertyInfo property, out FieldName fieldName)
    {
        if (FieldNames.TryGetValue(property, out fieldName))
            return true;

        foreach (var pair in FieldNames)
        {
            if (ShapeTypeInspector.IsSameProperty(pair.Key, property))
            {
                fieldName = pair.Value;
                return true;
            }
        }

        fieldName = default;
        return false;
    }

    static Type UnwrapNullable(Type clrType) => Nullable.GetUnderlyingType(clrType) ?? clrType;
}
