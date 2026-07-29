using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Processes.IR;

/// <summary>Canonical, portable semantic definition of one finite-activation Process graph.</summary>
/// <remarks>
/// Definition identity, revision, fingerprint, provenance, and descriptive metadata belong to the surrounding
/// <see cref="ExecutionDefinitionDocument"/>. This payload contains only fingerprint-bearing Process semantics.
/// It contains no callbacks, suspended host-language frames, runtime services, storage state, or compiled plans.
/// </remarks>
public sealed record ProcessDefinition
{
    /// <summary>Creates a normalized canonical Process definition.</summary>
    /// <param name="input">Typed Process invocation input contract.</param>
    /// <param name="result">Typed contract shared by successful and failed terminal Process results.</param>
    /// <param name="entry">Stable identity of the first Process node.</param>
    /// <param name="nodes">Set-like Process node table.</param>
    /// <param name="recoveryPolicy">Explicit behavior after a recoverable interruption.</param>
    [JsonConstructor]
    public ProcessDefinition(
        ValueContract input,
        ValueContract result,
        ExecutionNodeId entry,
        ImmutableArray<ProcessNode> nodes,
        ProcessRecoveryPolicy recoveryPolicy)
    {
        Input = input;
        Result = result;
        Entry = entry;
        Nodes = ProcessIrCollections.NormalizeSet(nodes, CompareNodes);
        RecoveryPolicy = recoveryPolicy;
    }

    /// <summary>Typed Process invocation input contract.</summary>
    public ValueContract Input { get; }

    /// <summary>Typed contract shared by successful and failed terminal Process results.</summary>
    public ValueContract Result { get; }

    /// <summary>Stable identity of the first Process node.</summary>
    public ExecutionNodeId Entry { get; }

    /// <summary>Process node table in deterministic stable-identity order.</summary>
    public ImmutableArray<ProcessNode> Nodes { get; }

    /// <summary>Explicit behavior after a recoverable interruption.</summary>
    public ProcessRecoveryPolicy RecoveryPolicy { get; }

    /// <summary>Compares Process definitions by complete normalized semantic value.</summary>
    /// <param name="other">Definition to compare with this value.</param>
    /// <returns><see langword="true"/> when all typed contracts, graph semantics, and recovery policy are equal.</returns>
    public bool Equals(ProcessDefinition? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Input == other.Input
        && Result == other.Result
        && Entry == other.Entry
        && RecoveryPolicy == other.RecoveryPolicy
        && Nodes.SequenceEqual(other.Nodes);

    /// <summary>Returns a structural hash code for the complete normalized Process definition.</summary>
    /// <returns>A hash code derived from contracts, entry, nodes, and recovery policy.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Input);
        hash.Add(Result);
        hash.Add(Entry);
        hash.Add(RecoveryPolicy);
        foreach (var node in Nodes)
            hash.Add(node);
        return hash.ToHashCode();
    }

    static int CompareNodes(ProcessNode? left, ProcessNode? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;
        return StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);
    }
}
