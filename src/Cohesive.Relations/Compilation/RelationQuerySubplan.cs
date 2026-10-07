using System.Collections.Immutable;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Compilation;

/// <summary>A closed projected rowset and the remaining query, derived from one canonical query.</summary>
/// <remarks>
/// This is a preparation artifact, not a second authored query. The original snapshot remains authoritative.
/// The initial cut supports source/filter/join/traversal/projection ancestors, one row terminal and full demand.
/// Hidden bindings may not escape the projection. Ordering, paging, aggregation and correlated invocation cuts
/// are rejected rather than assigned weaker semantics. Native capability admission remains the adapter's job.
/// </remarks>
public sealed class RelationQuerySubplan
{
    RelationQuerySubplan(RelationQueryCompilationResult original, ProjectQueryNode cut,
        ImmutableArray<QueryNodeId> coveredNodes, RelationQueryCompilationResult prefix,
        RelationQueryCompilationResult remainder)
    { Original = original; Cut = cut; CoveredNodes = coveredNodes; Prefix = prefix; Remainder = remainder; }

    /// <summary>Original semantic compilation and exact authored request.</summary>
    public RelationQueryCompilationResult Original { get; }
    /// <summary>Projection whose complete rowset forms the execution boundary.</summary>
    public ProjectQueryNode Cut { get; }
    /// <summary>Original nodes executed exclusively by the prefix.</summary>
    public ImmutableArray<QueryNodeId> CoveredNodes { get; }
    /// <summary>Derived closed prefix, retaining original node and binding identities.</summary>
    public RelationQueryCompilationResult Prefix { get; }
    /// <summary>Derived remaining query; the cut is its only replacement source.</summary>
    public RelationQueryCompilationResult Remainder { get; }

    /// <summary>Derives and validates a closed projection cut without selecting a provider or performing IO.</summary>
    /// <param name="request">Original immutable query snapshot, shapes, relationships and full output demand.</param>
    /// <param name="projection">Nonterminal projection defining the rowset interface.</param>
    /// <returns>Prepared semantic plans with exact original-to-derived provenance.</returns>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="RelationQueryPreparationException">The query/cut is unsupported, invalid or leaks an interior branch; evidence and a stable code are retained.</exception>
    public static RelationQuerySubplan Compile(RelationQueryCompilationRequest request, QueryNodeId projection)
    {
        ArgumentNullException.ThrowIfNull(request);
        var original = RelationQueryStaticCompiler.Compile(request);
        Require(original, "Original query");
        if (request.DefinitionDocument.Definition is not QueryDefinition query
            || query.Results.Length != 1 || query.Results[0] is not RowsQueryResultDefinition
            || request.Demand.Kind != RelationQueryCompilationDemandKind.AllDeclaredOutputs)
            throw new RelationQueryPreparationException("subplan cut", original,
                code: "relationQuery.subplan.resultUnsupported", detail: "Subplans require one row-result query with full output demand.");
        var nodes = query.Body.Nodes.ToDictionary(node => node.Id);
        if (!nodes.TryGetValue(projection, out var selected) || selected is not ProjectQueryNode cut
            || query.Results[0].Input == projection)
            throw new RelationQueryPreparationException("subplan cut", original,
                code: "relationQuery.subplan.cutInvalid", detail: "Select a nonterminal projection as the subplan boundary.");
        HashSet<QueryNodeId> covered = [];
        Visit(cut.Id);
        foreach (var node in query.Body.Nodes.Where(node => !covered.Contains(node.Id)))
            if (node.Inputs.Any(input => covered.Contains(input) && input != cut.Id))
                throw new RelationQueryPreparationException("subplan cut", original,
                code: "relationQuery.subplan.interiorEscapes", detail: "A subplan interior cannot feed another branch outside its projection.");
        var prefixDefinition = new QueryDefinition(new(query.Id.Value + "/subplan/" + projection.Value),
            new(query.Name.Value + "Subplan"), new([.. query.Body.Nodes.Where(node => covered.Contains(node.Id))], query.Body.Parameters),
            [new RowsQueryResultDefinition(new("subplan-rows"), cut.Id)]);
        var remainderDefinition = query with
        {
            Id = new(query.Id.Value + "/remainder/" + projection.Value),
            Name = new(query.Name.Value + "Remainder"),
            Body = new([new SourceQueryNode(cut.Id, cut.ResultBinding, cut.ResultShape),
                .. query.Body.Nodes.Where(node => !covered.Contains(node.Id))], query.Body.Parameters)
        };
        var prefix = CompileDerived(prefixDefinition);
        Require(prefix, "Native subplan");
        var remainder = CompileDerived(remainderDefinition);
        Require(remainder, "Remaining query (only projected bindings may cross the boundary)");
        return new(original, cut, [.. covered.OrderBy(id => id.Value, StringComparer.Ordinal)], prefix, remainder);

        void Visit(QueryNodeId id)
        {
            if (!covered.Add(id)) return;
            var node = nodes[id];
            if (node is not (SourceQueryNode or FilterQueryNode or JoinQueryNode or TraverseRelationshipQueryNode or ProjectQueryNode))
                throw new RelationQueryPreparationException("subplan cut", original,
                code: "relationQuery.subplan.ancestorUnsupported", detail: "This subplan boundary supports only sources, filters, joins, traversals and projections.");
            foreach (var input in node.Inputs) Visit(input);
        }
        RelationQueryCompilationResult CompileDerived(QueryDefinition definition) => RelationQueryStaticCompiler.Compile(
            new(RelationQueryDocument.FromDefinition(definition), request.ShapeDocuments, request.RelationshipCatalogDocument));
    }

    static void Require(RelationQueryCompilationResult compilation, string phase)
    {
        if (!compilation.IsSuccessful)
            throw new RelationQueryPreparationException(phase, compilation);
    }
}
