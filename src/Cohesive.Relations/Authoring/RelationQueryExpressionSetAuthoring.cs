using System.Collections.Immutable;
using System.Linq.Expressions;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Authoring;

/// <summary>Transient typed ordering declaration consumed by expression authoring.</summary>
/// <remarks>
/// A successful order operation retains only the lowered canonical key. This declaration and its
/// expression tree are never retained by canonical relation/query IR.
/// </remarks>
public sealed class RelationQueryExpressionOrdering
{
    internal RelationQueryExpressionOrdering(
        LambdaExpression key,
        ImmutableArray<RelationQueryExpressionValueBinding> bindings,
        QuerySortDirection direction,
        QueryNullPlacement nullPlacement,
        string? sourceReference)
    {
        Key = key;
        Bindings = bindings;
        Direction = direction;
        NullPlacement = nullPlacement;
        SourceReference = sourceReference;
    }

    internal LambdaExpression Key { get; }

    internal ImmutableArray<RelationQueryExpressionValueBinding> Bindings { get; }

    internal QuerySortDirection Direction { get; }

    internal QueryNullPlacement NullPlacement { get; }

    internal string? SourceReference { get; }
}

public sealed partial class RelationQueryExpressionAuthoring
{
    /// <summary>Creates a typed transient ordering declaration.</summary>
    /// <typeparam name="TBinding">CLR type represented by the ordering binding.</typeparam>
    /// <typeparam name="TKey">CLR type of the ordering key.</typeparam>
    /// <param name="key">Ordering-key expression.</param>
    /// <param name="binding">Binding corresponding to the key parameter.</param>
    /// <param name="direction">Sort direction.</param>
    /// <param name="nullPlacement">Placement of null or missing values.</param>
    /// <param name="sourceReference">Optional stable producer reference for this ordering.</param>
    /// <returns>A transient ordering declaration accepted by <see cref="Order{TInput}(RelationQueryNodeHandle{TInput}, IEnumerable{RelationQueryExpressionOrdering}, string?)"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="key"/> or <paramref name="binding"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="binding"/> belongs to another session.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="direction"/> or <paramref name="nullPlacement"/> is unsupported.
    /// </exception>
    public RelationQueryExpressionOrdering Ordering<TBinding, TKey>(
        Expression<Func<TBinding, TKey>> key,
        RelationQueryExpressionValueBinding<TBinding> binding,
        QuerySortDirection direction = QuerySortDirection.Ascending,
        QueryNullPlacement nullPlacement = QueryNullPlacement.Last,
        string? sourceReference = null)
        where TBinding : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(binding);
        RequireOwner(binding);
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported query sort direction.");
        if (!Enum.IsDefined(nullPlacement))
            throw new ArgumentOutOfRangeException(nameof(nullPlacement), nullPlacement, "Unsupported query null placement.");
        return new(key, [binding], direction, nullPlacement, sourceReference);
    }

    /// <summary>Orders a logical branch by one or more typed ordering declarations.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Logical branch to order.</param>
    /// <param name="orderings">Non-empty ordering declarations from primary key through final tie-breaker.</param>
    /// <param name="sourceReference">Optional stable producer reference for the order node.</param>
    /// <returns>A structural handle for the canonical order node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="orderings"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="orderings"/> is empty or contains a declaration whose binding belongs to another
    /// session or is not visible in <paramref name="input"/>.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// An ordering key cannot be lowered exactly or returns a raw CLR temporal carrier instead of an explicitly
    /// normalized canonical scalar key.
    /// </exception>
    public RelationQueryNodeHandle<OrderQueryNode> Order<TInput>(
        RelationQueryNodeHandle<TInput> input,
        IEnumerable<RelationQueryExpressionOrdering> orderings,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
    {
        ArgumentNullException.ThrowIfNull(orderings);
        var materialized = orderings.ToImmutableArray();
        if (materialized.IsDefaultOrEmpty)
            throw new ArgumentException("At least one ordering is required.", nameof(orderings));
        if (materialized.Any(static ordering => ordering is null))
            throw new ArgumentException("Orderings cannot contain null entries.", nameof(orderings));

        var reference = sourceReference ?? "order";
        var lowerer = ExpressionLowerer;
        var lowered = new RelationQueryOrderingInput[materialized.Length];
        for (var index = 0; index < materialized.Length; index++)
        {
            var ordering = materialized[index];
            var site = ordering.SourceReference ?? $"{reference}/orderings/{index}";
            RequireCarrierIndependentKey(ordering.Key, "ordering", site + "/key");
            var handles = RequireBindings(ordering.Key, ordering.Bindings);
            RequireBindingsVisible(input, handles, nameof(orderings));
            var key = lowerer.LowerValue(ordering.Key, handles, site + "/key").RequireValue();
            lowered[index] = new(key.Value, ordering.Direction, ordering.NullPlacement, key.Source);
        }

        return structural.Order(
            input,
            [.. lowered],
            source: Source(reference, "Expression-authored ordering."));
    }

    /// <summary>Orders a logical branch by one or more typed ordering declarations.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Logical branch to order.</param>
    /// <param name="orderings">Non-empty ordering declarations from primary key through final tie-breaker.</param>
    /// <returns>A structural handle for the canonical order node.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="orderings"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="orderings"/> is empty, contains an invalid declaration, or references a binding
    /// that is not visible in <paramref name="input"/>.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// An ordering key cannot be lowered exactly or returns a raw CLR temporal carrier instead of an explicitly
    /// normalized canonical scalar key.
    /// </exception>
    public RelationQueryNodeHandle<OrderQueryNode> Order<TInput>(
        RelationQueryNodeHandle<TInput> input,
        params RelationQueryExpressionOrdering[] orderings)
        where TInput : LogicalQueryNode =>
        Order(input, (IEnumerable<RelationQueryExpressionOrdering>)orderings);

    /// <summary>Removes duplicates using one or more arbitrary-width typed key expressions.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <param name="input">Logical branch to de-duplicate.</param>
    /// <param name="keys">Non-empty distinct-key lambdas.</param>
    /// <param name="bindings">Visible bindings shared by the key lambdas in positional parameter order.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A structural handle for the canonical distinct node.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="keys"/> or <paramref name="bindings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="keys"/> is empty or contains null, or a binding belongs to another session or has a
    /// mismatched CLR type, or a binding is not visible in <paramref name="input"/>.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// A distinct key cannot be lowered exactly or returns a raw CLR temporal carrier instead of an explicitly
    /// normalized canonical scalar key.
    /// </exception>
    public RelationQueryNodeHandle<DistinctQueryNode> Distinct<TInput>(
        RelationQueryNodeHandle<TInput> input,
        IEnumerable<LambdaExpression> keys,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
    {
        ArgumentNullException.ThrowIfNull(keys);
        var materialized = keys.ToImmutableArray();
        if (materialized.IsDefaultOrEmpty)
            throw new ArgumentException("At least one distinct key is required.", nameof(keys));
        if (materialized.Any(static key => key is null))
            throw new ArgumentException("Distinct keys cannot contain null entries.", nameof(keys));

        var reference = sourceReference ?? "distinct";
        var lowerer = ExpressionLowerer;
        var lowered = new RelationQueryExpressionInput[materialized.Length];
        for (var index = 0; index < materialized.Length; index++)
        {
            var site = $"{reference}/keys/{index}";
            RequireCarrierIndependentKey(materialized[index], "distinct", site);
            var handles = RequireBindings(materialized[index], bindings);
            RequireBindingsVisible(input, handles, nameof(bindings));
            var key = lowerer.LowerValue(
                materialized[index],
                handles,
                site).RequireValue();
            lowered[index] = new(key.Value, key.Source);
        }

        return structural.Distinct(
            input,
            [.. lowered],
            source: Source(reference, "Expression-authored keyed distinctness."));
    }

    /// <summary>Removes duplicates using one typed key expression.</summary>
    /// <typeparam name="TInput">Canonical type of the input node.</typeparam>
    /// <typeparam name="TBinding">CLR type represented by the key binding.</typeparam>
    /// <typeparam name="TKey">CLR type of the distinct key.</typeparam>
    /// <param name="input">Logical branch to de-duplicate.</param>
    /// <param name="key">Distinct-key expression.</param>
    /// <param name="binding">Binding corresponding to the key lambda parameter.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A structural handle for the canonical distinct node.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is <see langword="null"/>, or the binding belongs to another session or has
    /// a mismatched CLR type, or the binding is not visible in <paramref name="input"/>.
    /// </exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">
    /// The distinct key cannot be lowered exactly or returns a raw CLR temporal carrier instead of an explicitly
    /// normalized canonical scalar key.
    /// </exception>
    public RelationQueryNodeHandle<DistinctQueryNode> Distinct<TInput, TBinding, TKey>(
        RelationQueryNodeHandle<TInput> input,
        Expression<Func<TBinding, TKey>> key,
        RelationQueryExpressionValueBinding<TBinding> binding,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TBinding : notnull =>
        Distinct(input, [key], [binding], sourceReference);

    internal static void RequireCarrierIndependentKey(
        LambdaExpression key,
        string keyRole,
        string sourceReference)
    {
        ArgumentNullException.ThrowIfNull(key);
        var visitor = new CarrierDependentKeyVisitor();
        visitor.Visit(key.Body);
        if (visitor.CarrierType is not { } carrierType)
            return;

        throw new RelationQueryExpressionAuthoringException(
        [
            new RelationQueryExpressionDiagnostic(
                RelationQueryExpressionDiagnosticCodes.KeyDomainUnsupported,
                DiagnosticSeverity.Error,
                $"The {keyRole} key contains carrier-dependent CLR type '{StableTypeName(carrierType)}'. "
                + "Raw temporal and dynamic JSON observation carriers can vary by source, so equality, hashing, or ordering is not portable.",
                expressionPath: "body",
                sourceReference,
                symbol: StableTypeName(carrierType),
                suggestion: "Project the value into an explicitly normalized canonical String or Int64 field (or another fixed scalar domain), then use that field as the key.")
        ]);
    }

    sealed class CarrierDependentKeyVisitor : ExpressionVisitor
    {
        public Type? CarrierType { get; private set; }

        public override Expression? Visit(Expression? node)
        {
            if (node is null || CarrierType is not null)
                return node;

            if (node is ParameterExpression)
                return node;

            if (node is MemberExpression member)
            {
                CarrierType = FindCarrierDependentType(member.Type, inspectComposite: true, []);
                return member;
            }

            if (node is not NewExpression and not MemberInitExpression and not LambdaExpression)
                CarrierType = FindCarrierDependentType(node.Type, inspectComposite: false, []);

            return CarrierType is null ? base.Visit(node) : node;
        }

        static Type? FindCarrierDependentType(
            Type type,
            bool inspectComposite,
            HashSet<Type> visited)
        {
            var normalized = Nullable.GetUnderlyingType(type) ?? type;
            if (normalized == typeof(DateOnly)
                || normalized == typeof(DateTime)
                || normalized == typeof(DateTimeOffset)
                || normalized == typeof(ObservationValue)
                || normalized == typeof(object)
                || normalized == typeof(System.Text.Json.JsonElement)
                || normalized == typeof(System.Text.Json.JsonDocument)
                || typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(normalized))
            {
                return type;
            }

            if (!inspectComposite || !visited.Add(normalized))
                return null;

            if (normalized.IsArray)
            {
                return FindCarrierDependentType(
                    normalized.GetElementType()!,
                    inspectComposite: true,
                    visited);
            }

            foreach (var argument in normalized.IsGenericType ? normalized.GetGenericArguments() : [])
            {
                var carrier = FindCarrierDependentType(argument, inspectComposite: true, visited);
                if (carrier is not null)
                    return carrier;
            }

            if (normalized.IsPrimitive
                || normalized.IsEnum
                || normalized == typeof(string)
                || normalized == typeof(decimal)
                || normalized == typeof(Guid))
            {
                return null;
            }

            foreach (var property in normalized.GetProperties(
                         System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (property.GetMethod is null || property.GetIndexParameters().Length != 0)
                    continue;

                var carrier = FindCarrierDependentType(property.PropertyType, inspectComposite: true, visited);
                if (carrier is not null)
                    return carrier;
            }

            return null;
        }
    }
}
