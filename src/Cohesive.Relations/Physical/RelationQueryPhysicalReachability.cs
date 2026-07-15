using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Physical;

/// <summary>Proves the narrow row-preserving traversal chains supported by v1 federated acquisition.</summary>
internal static class RelationQueryPhysicalReachability
{
    /// <summary>
    /// Tries to identify the left, at-most-one sibling traversals that must complete before
    /// <paramref name="contract"/> can acquire the same source-binding occurrences.
    /// </summary>
    /// <param name="plan">Exact compiled semantic plan.</param>
    /// <param name="contract">Traversal whose source-binding reachability is being proven.</param>
    /// <param name="interveningTraversals">
    /// Proven intervening traversals in semantic evaluation order, or an empty array for a direct traversal.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when every intervening node is a left, at-most-one traversal from the same
    /// source binding; otherwise, <see langword="false"/>.
    /// </returns>
    internal static bool TryGetPreservingInterveningTraversals(
        CompiledRelationQueryPlan plan,
        RelationQueryTraversalInputContract contract,
        out ImmutableArray<RelationQueryTraversalInputContract> interveningTraversals)
    {
        var nodes = plan.ExecutionSlice.Nodes
            .Select(static execution => execution.CanonicalNode)
            .ToDictionary(static node => node.Id);
        if (!nodes.TryGetValue(contract.Input.Traversal, out var canonical)
            || canonical is not TraverseRelationshipQueryNode traversalNode)
        {
            interveningTraversals = [];
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
            interveningTraversals = [];
            return false;
        }

        List<RelationQueryTraversalInputContract> reversed = [];
        HashSet<QueryNodeId> visited = [];
        var cursor = traversalNode.Input;
        while (cursor != producers[0])
        {
            if (!visited.Add(cursor)
                || !nodes.TryGetValue(cursor, out var intervening)
                || intervening is not TraverseRelationshipQueryNode prior
                || prior.JoinKind != JoinKind.Left
                || prior.From != contract.From)
            {
                interveningTraversals = [];
                return false;
            }

            var priorContracts = plan.InputContract.Traversals
                .Where(candidate => candidate.Input.Traversal == prior.Id)
                .Take(2)
                .ToArray();
            if (priorContracts.Length != 1
                || priorContracts[0].From != contract.From
                || priorContracts[0].Cardinality != RelationshipTraversalCardinality.AtMostOne)
            {
                interveningTraversals = [];
                return false;
            }

            reversed.Add(priorContracts[0]);
            cursor = prior.Input;
        }

        reversed.Reverse();
        interveningTraversals = [.. reversed];
        return true;
    }
}
