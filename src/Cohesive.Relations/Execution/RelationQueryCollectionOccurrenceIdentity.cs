using System.Globalization;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Execution;

/// <summary>Deterministic evaluation-local identity for one item produced by a canonical collection expansion.</summary>
internal static class RelationQueryCollectionOccurrenceIdentity
{
    /// <summary>Creates one item occurrence identity from its expansion, owner occurrence, and zero-based position.</summary>
    /// <param name="expansion">Canonical expansion node.</param>
    /// <param name="owner">Exact owner occurrence.</param>
    /// <param name="index">Zero-based item position in the expanded collection.</param>
    /// <returns>An identity unique to this logical collection occurrence within the evaluation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    internal static RelationQueryOccurrenceId Create(
        QueryNodeId expansion,
        RelationQueryOccurrenceId owner,
        int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), index, "A collection occurrence index cannot be negative.");
        return new(
            $"runtime/expansion/{Uri.EscapeDataString(expansion.Value)}/{Uri.EscapeDataString(owner.Value)}/{index.ToString(CultureInfo.InvariantCulture)}");
    }
}
