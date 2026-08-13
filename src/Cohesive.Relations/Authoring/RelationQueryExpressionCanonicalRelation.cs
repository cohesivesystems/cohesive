using Cohesive.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Authoring;

public sealed partial class RelationQueryExpressionAuthoring
{
    /// <summary>Projects one successfully authored exactly-one Relation into a typed canonical handle.</summary>
    /// <typeparam name="TInput">CLR type represented by the Relation root.</typeparam>
    /// <typeparam name="TOutputNode">Canonical logical-node type producing the Relation output.</typeparam>
    /// <typeparam name="TResult">CLR type represented by the Relation output.</typeparam>
    /// <param name="authored">Exact Relation terminal produced by this authoring session.</param>
    /// <param name="input">Typed source handle that must own the canonical Relation root.</param>
    /// <param name="result">Typed output handle that must own the canonical Relation output.</param>
    /// <param name="revisionId">Exact semantic revision used by execution-definition links.</param>
    /// <param name="metadata">Optional persisted Relation document metadata.</param>
    /// <returns>
    /// A typed immutable handle containing the canonical document, exact reference, contracts, and captured
    /// semantic dependency evidence.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authored"/>, <paramref name="input"/>, or <paramref name="result"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The terminal is invalid or belongs to another session; a handle belongs to another session; the declared
    /// root or output differs from the supplied typed handle; the Relation does not produce exactly one result per
    /// root; or <paramref name="revisionId"/> is default.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Canonical Relation, shape, or relationship content has no stable serialized representation.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Canonical Relation, shape, or relationship content contains an unsupported runtime value.
    /// </exception>
    public Relation<TInput, TResult> CreateRelation<TInput, TOutputNode, TResult>(
        RelationQueryAuthoringResult<RelationDefinition> authored,
        RelationQueryExpressionBoundNode<SourceQueryNode, TInput> input,
        RelationQueryExpressionBoundNode<TOutputNode, TResult> result,
        ExecutionRevisionId revisionId,
        RelationQueryDocumentMetadata? metadata = null)
        where TInput : notnull
        where TOutputNode : LogicalQueryNode
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(revisionId.Value))
            throw new ArgumentException("A typed Relation requires an exact revision identity.", nameof(revisionId));

        RequireOwner(input.Binding);
        RequireOwner(result.Binding);
        if (!authored.Validation.IsValid
            || !authoredEvaluationSnapshots.TryGetValue(authored, out var snapshot))
        {
            throw new ArgumentException(
                "The Relation terminal must be valid and must have been produced by this expression-authoring session.",
                nameof(authored));
        }

        var definition = authored.Definition;
        if (definition.Output.Mode != RelationOutputMode.OnePerRoot)
        {
            throw new ArgumentException(
                $"Typed singular Relation handles require output mode {RelationOutputMode.OnePerRoot}, but "
                + $"'{definition.Id.Value}' declares {definition.Output.Mode}.",
                nameof(authored));
        }

        var root = definition.Body.Nodes
            .OfType<SourceQueryNode>()
            .SingleOrDefault(source => source.Binding == definition.RootBinding);
        if (root is null
            || root.Id != input.Node.Id
            || root.Binding != input.Binding.Id
            || input.Binding.Shape is not { } inputShape
            || root.Shape != inputShape)
        {
            throw new ArgumentException(
                $"Typed Relation input '{typeof(TInput)}' does not identify the canonical root of "
                + $"'{definition.Id.Value}'.",
                nameof(input));
        }

        if (definition.Output.Node != result.Node.Id
            || result.Binding.Shape is not { } resultShape
            || definition.Output.Shape != resultShape)
        {
            throw new ArgumentException(
                $"Typed Relation result '{typeof(TResult)}' does not identify the canonical output of "
                + $"'{definition.Id.Value}'.",
                nameof(result));
        }

        return new(
            authored.CreateDocument(metadata),
            revisionId,
            authored.Validation,
            snapshot.ShapeDocuments,
            RelationshipCatalogDocument.FromCatalog(snapshot.RelationshipCatalog),
            new(input.Binding.Type),
            new(result.Binding.Type));
    }
}
