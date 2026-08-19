using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Physical;

/// <summary>Closed source-occurrence acquisition strategy for a physical relationship traversal.</summary>
internal enum RelationQueryPhysicalTraversalReachabilityMode
{
    /// <summary>Acquire only owners proven to survive the intervening left, at-most-one traversal chain.</summary>
    ExactOccurrenceChain = 0,

    /// <summary>
    /// Acquire for every bounded occurrence of the declared source binding and let the canonical interpreter discard
    /// evidence for occurrences removed by intervening semantic operators.
    /// </summary>
    ConservativeBindingOverAcquisition = 1
}

/// <summary>Resolved physical source-occurrence acquisition strategy for one traversal.</summary>
/// <param name="Mode">Exact or conservative bounded acquisition strategy.</param>
/// <param name="InterveningTraversals">
/// Exact left, at-most-one traversal chain used by <see cref="RelationQueryPhysicalTraversalReachabilityMode.ExactOccurrenceChain"/>.
/// </param>
internal sealed record RelationQueryPhysicalTraversalReachability(
    RelationQueryPhysicalTraversalReachabilityMode Mode,
    ImmutableArray<RelationQueryTraversalInputContract> InterveningTraversals);

/// <summary>Resolves the bounded source-occurrence acquisition strategy supported by federated acquisition.</summary>
internal static class RelationQueryPhysicalReachability
{
    /// <summary>
    /// Resolves either an exact left-row-preserving chain or conservative bounded over-acquisition from the declared
    /// source binding. Conservative acquisition changes physical work, not logical results: the canonical interpreter
    /// remains authoritative for filters, ordering, distinctness, cardinality, and downstream reachability.
    /// </summary>
    /// <param name="plan">Exact compiled semantic plan.</param>
    /// <param name="contract">Traversal whose source-binding reachability is being resolved.</param>
    /// <param name="reachability">Resolved acquisition strategy.</param>
    /// <returns>
    /// <see langword="true"/> when the traversal and its declared source binding each have one exact producer;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    internal static bool TryResolve(
        CompiledRelationQueryPlan plan,
        RelationQueryTraversalInputContract contract,
        out RelationQueryPhysicalTraversalReachability reachability)
    {
        var nodes = plan.ExecutionSlice.Nodes
            .Select(static execution => execution.CanonicalNode)
            .ToDictionary(static node => node.Id);
        if (!nodes.TryGetValue(contract.Input.Traversal, out var canonical)
            || canonical is not TraverseRelationshipQueryNode traversalNode)
        {
            reachability = null!;
            return false;
        }

        QueryNodeId[] producers =
        [
            .. plan.InputContract.Sources
                .Where(source => source.Binding == contract.From)
                .Select(static source => source.Node)
                .Concat(plan.InputContract.Traversals
                    .Where(traversal => traversal.Result == contract.From)
                    .Select(static traversal => traversal.Input.Traversal))
        ];
        if (producers.Length != 1)
        {
            reachability = null!;
            return false;
        }

        List<RelationQueryTraversalInputContract> reversed = [];
        HashSet<QueryNodeId> visited = [];
        var cursor = traversalNode.Input;
        while (cursor != producers[0])
        {
            if (!visited.Add(cursor) || !nodes.TryGetValue(cursor, out var intervening))
            {
                reachability = null!;
                return false;
            }

            if (intervening is not TraverseRelationshipQueryNode prior
                || prior.JoinKind != JoinKind.Left
                || prior.From != contract.From)
            {
                reachability = new(
                    RelationQueryPhysicalTraversalReachabilityMode.ConservativeBindingOverAcquisition,
                    []);
                return true;
            }

            var priorContracts = plan.InputContract.Traversals
                .Where(candidate => candidate.Input.Traversal == prior.Id)
                .Take(2)
                .ToArray();
            if (priorContracts.Length != 1
                || priorContracts[0].From != contract.From
                || priorContracts[0].Cardinality != RelationshipTraversalCardinality.AtMostOne)
            {
                reachability = new(
                    RelationQueryPhysicalTraversalReachabilityMode.ConservativeBindingOverAcquisition,
                    []);
                return true;
            }

            reversed.Add(priorContracts[0]);
            cursor = prior.Input;
        }

        reversed.Reverse();
        reachability = new(
            RelationQueryPhysicalTraversalReachabilityMode.ExactOccurrenceChain,
            [.. reversed]);
        return true;
    }
}
