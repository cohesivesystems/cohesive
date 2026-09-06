using System.Collections.Immutable;
using System.Linq.Expressions;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Authoring;

public sealed partial class RelationQueryAuthoringCore
{
    /// <summary>Selects the uniquely best ordered row per partition, retaining only the winner's provenance.</summary>
    /// <typeparam name="TInput">Canonical input-node type.</typeparam>
    /// <param name="input">Input node owned by this authoring session.</param>
    /// <param name="keys">Partition keys; an empty/default array selects globally per root.</param>
    /// <param name="orderings">Nonempty ordering from primary preference through final tie-breaker.</param>
    /// <param name="nodeId">Optional explicit node identity.</param>
    /// <param name="source">Optional producer attribution.</param>
    /// <returns>A handle to the canonical representative-selection node.</returns>
    /// <exception cref="ArgumentException">A handle, key, ordering or node identity is invalid, or ordering is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An ordering direction or null placement is unsupported.</exception>
    public RelationQueryNodeHandle<SelectRepresentativeQueryNode> SelectRepresentative<TInput>(
        RelationQueryNodeHandle<TInput> input,
        ImmutableArray<RelationQueryExpressionInput> keys,
        ImmutableArray<RelationQueryOrderingInput> orderings,
        QueryNodeId? nodeId = null,
        RelationQueryAuthoringSource? source = null)
        where TInput : LogicalQueryNode
    {
        RequireNode(input, nameof(input));
        keys = NormalizeEntries(keys, nameof(keys), "Representative keys");
        RequireEntries(orderings, nameof(orderings), "Orderings");
        var selected = SelectNodeId(RelationQueryWireNames.SelectRepresentativeNode, nodeId, source);
        var node = new SelectRepresentativeQueryNode(selected.Id, input.Id,
            [.. keys.Select(static key => key.Value)],
            [.. orderings.Select(static ordering => new QueryOrdering(ordering.Key, ordering.Direction, ordering.NullPlacement))]);
        AddNode(node, selected, source);
        for (var index = 0; index < keys.Length; index++)
            Trace(RelationQueryAuthoringDecisionKind.Expression, node.Id.Value,
                keys[index].Source ?? source, $"keys/{index}");
        for (var index = 0; index < orderings.Length; index++)
            Trace(RelationQueryAuthoringDecisionKind.Expression, node.Id.Value,
                orderings[index].KeySource ?? source, $"orderings/{index}/key");
        return Handle(node);
    }
}

public sealed partial class RelationQueryExpressionAuthoring
{
    /// <summary>Authors ordered representative selection using portable typed partition and ordering expressions.</summary>
    /// <typeparam name="TInput">Canonical input-node type.</typeparam>
    /// <param name="input">Input branch.</param>
    /// <param name="keys">Partition expressions, or an empty collection for a global partition per root.</param>
    /// <param name="bindings">Bindings corresponding to each key lambda's parameters.</param>
    /// <param name="orderings">Nonempty ordering declarations defining the unique winner.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A handle to materialized canonical selection with source attribution and no retained host-language expressions.</returns>
    /// <exception cref="ArgumentNullException">The keys, bindings or orderings collection is null.</exception>
    /// <exception cref="ArgumentException">A key, ordering, handle or binding is invalid or out of scope.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">An expression cannot be lowered to a portable canonical key.</exception>
    public RelationQueryNodeHandle<SelectRepresentativeQueryNode> SelectRepresentative<TInput>(
        RelationQueryNodeHandle<TInput> input,
        IEnumerable<LambdaExpression> keys,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        IEnumerable<RelationQueryExpressionOrdering> orderings,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var reference = sourceReference ?? RelationQueryWireNames.SelectRepresentativeNode;
        return structural.SelectRepresentative(input,
            LowerSetKeys(input, keys, bindings, reference, "representative partition"),
            LowerOrderings(input, orderings, reference),
            source: Source(reference, "Expression-authored ordered representative selection."));
    }

    /// <summary>Authors representative selection with one typed partition key.</summary>
    /// <typeparam name="TInput">Canonical input-node type.</typeparam>
    /// <typeparam name="TBinding">CLR type of the partition binding.</typeparam>
    /// <typeparam name="TKey">CLR type of the partition key.</typeparam>
    /// <param name="input">Input branch.</param>
    /// <param name="key">Partition-key expression.</param>
    /// <param name="binding">Binding corresponding to the key parameter.</param>
    /// <param name="orderings">Nonempty ordered preferences.</param>
    /// <param name="sourceReference">Optional stable producer reference.</param>
    /// <returns>A handle to the canonical representative selection.</returns>
    /// <exception cref="ArgumentException">An expression, handle or binding is invalid or out of scope.</exception>
    /// <exception cref="ArgumentNullException">The orderings collection is null.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">An expression cannot be lowered to a portable canonical key.</exception>
    public RelationQueryNodeHandle<SelectRepresentativeQueryNode> SelectRepresentative<TInput, TBinding, TKey>(
        RelationQueryNodeHandle<TInput> input,
        Expression<Func<TBinding, TKey>> key,
        RelationQueryExpressionValueBinding<TBinding> binding,
        IEnumerable<RelationQueryExpressionOrdering> orderings,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TBinding : notnull =>
        SelectRepresentative(input, [key], [binding], orderings, sourceReference);
}
