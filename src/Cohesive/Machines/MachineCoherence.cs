using System.Collections.Immutable;
using Cohesive.Execution;

namespace Cohesive.Machines;

/// <summary>Allowed simultaneous configuration.</summary>
/// <param name="Left">Left state identity.</param>
/// <param name="Right">Right state identity.</param>
public sealed record MachineStatePair(string Left, string Right);

/// <summary>Exact immediate-coherence declaration. Does not coordinate participants.</summary>
/// <param name="Left">Exact left definition.</param>
/// <param name="Right">Exact right definition.</param>
/// <param name="Allowed">Allowed configuration pairs.</param>
public sealed record MachineCoherencePayload(ExecutionDefinitionReference Left, ExecutionDefinitionReference Right,
    ImmutableArray<MachineStatePair> Allowed);

/// <summary>Authors immediate coherence under independent interleaving of two machines.</summary>
public sealed class MachineCoherenceBuilder
{
    readonly MachineDefinition left;
    readonly MachineDefinition right;
    readonly HashSet<MachineStatePair> allowed = [];
    /// <summary>Creates an immediate-coherence declaration.</summary>
    /// <param name="left">Left participant.</param>
    /// <param name="right">Right participant.</param>
    public MachineCoherenceBuilder(MachineDefinition left, MachineDefinition right)
    { ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right); this.left = left; this.right = right; }
    /// <summary>Allows one simultaneous configuration.</summary>
    /// <param name="leftState">Left state.</param>
    /// <param name="rightState">Right state.</param>
    /// <returns>This declaration.</returns>
    /// <exception cref="ArgumentException">A state is unknown.</exception>
    public MachineCoherenceBuilder Allow(string leftState, string rightState)
    {
        if (!left.States.Contains(leftState) || !right.States.Contains(rightState)) throw new ArgumentException("Unknown coherence state.");
        allowed.Add(new(leftState, rightState)); return this;
    }
    /// <summary>Explores reachable pairs on demand with a declared bound.</summary>
    /// <param name="maximumPairs">Maximum visited configurations, positive.</param>
    /// <returns>Proven closure, a counterexample trace, or Unknown for guards or an exhausted bound.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Bound is not positive.</exception>
    public MachineClaim Analyze(int maximumPairs = 10000)
    {
        if (maximumPairs <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPairs));
        if (left.Edges.Concat(right.Edges).Any(e => e.Guard is not null))
            return new(MachineProofState.Unknown, "Guarded reachability requires symbolic evidence.", []);
        var initial = new MachineStatePair(left.Initial, right.Initial);
        var queue = new Queue<(MachineStatePair Pair, ImmutableArray<string> Trace)>();
        queue.Enqueue((initial, []));
        HashSet<MachineStatePair> visited = [];
        while (queue.TryDequeue(out var current))
        {
            if (visited.Contains(current.Pair)) continue;
            if (visited.Count == maximumPairs) return new(MachineProofState.Unknown, "Reachability bound exhausted.", current.Trace);
            visited.Add(current.Pair);
            if (!allowed.Contains(current.Pair)) return new(MachineProofState.Disproven,
                $"Forbidden pair ({current.Pair.Left}, {current.Pair.Right}).", current.Trace);
            foreach (var edge in left.Edges.Where(e => e.From == current.Pair.Left))
                queue.Enqueue((new(edge.To, current.Pair.Right), current.Trace.Add("left/" + edge.Id)));
            foreach (var edge in right.Edges.Where(e => e.From == current.Pair.Right))
                queue.Enqueue((new(current.Pair.Left, edge.To), current.Trace.Add("right/" + edge.Id)));
        }
        return new(MachineProofState.Proven, "All reachable independent interleavings preserve coherence.", []);
    }
    /// <summary>Persists a declared constraint independently of whether a realization satisfies it.</summary>
    /// <param name="id">Relationship identity.</param>
    /// <param name="revision">Exact revision.</param>
    /// <returns>Canonical relationship document with exact participants.</returns>
    public ExecutionDefinitionDocument Build(string id, string revision) => ExecutionDefinitionDocument.Create(
        new("machine.coherence/v1"), new(id), new(revision),
        new MachineCoherencePayload(left.Document.Reference, right.Document.Reference,
            [.. allowed.OrderBy(p => p.Left, StringComparer.Ordinal).ThenBy(p => p.Right, StringComparer.Ordinal)]),
        new(new("cohesive.machines.csharp", "1"), new(id)));
}

/// <summary>Evidence state, separate from any acceptance or waiver policy.</summary>
public enum MachineProofState
{
    /// <summary>Established for the stated finite semantics.</summary>
    Proven,
    /// <summary>Established only under named assumptions.</summary>
    Conditional,
    /// <summary>A counterexample exists.</summary>
    Disproven,
    /// <summary>Evidence is insufficient.</summary>
    Unknown
}

/// <summary>An attributable analysis result for a selected machine property.</summary>
/// <param name="State">Proof state.</param>
/// <param name="Explanation">Scope, bound, or failure explanation.</param>
/// <param name="Trace">Counterexample movements when available.</param>
public sealed record MachineClaim(MachineProofState State, string Explanation, ImmutableArray<string> Trace);
