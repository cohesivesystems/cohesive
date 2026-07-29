using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Processes.IR;

/// <summary>Stable built-in binding identities owned by canonical Process IR.</summary>
public static class ProcessBindingIds
{
    /// <summary>The complete typed Process invocation input.</summary>
    public static ValueBindingId Input { get; } = new("process.input");
}

/// <summary>Stable identity of one inbound Request obligation retained by Process coordination state.</summary>
/// <remarks>
/// Request-obligation identity is intentionally distinct from <see cref="ValueBindingId"/>. It denotes the
/// admitted logical Request envelope that a Reply must discharge, not application payload data.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RequestObligationBindingId
{
    /// <summary>Creates an inbound Request-obligation binding identity.</summary>
    /// <param name="value">Stable producer-assigned binding identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public RequestObligationBindingId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw Request-obligation binding identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw Request-obligation binding identity.</summary>
    /// <returns>The stable value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Coordination-local binding that retains one admitted inbound Request obligation.</summary>
public sealed record ProcessRequestObligationBinding
{
    /// <summary>Creates a Request-obligation binding.</summary>
    /// <param name="binding">Stable obligation-binding identity.</param>
    [JsonConstructor]
    public ProcessRequestObligationBinding(RequestObligationBindingId binding) => Binding = binding;

    /// <summary>Stable identity used by a later Reply to discharge the same logical Request.</summary>
    public RequestObligationBindingId Binding { get; }
}

/// <summary>Stable identity of one Process control-flow edge.</summary>
/// <remarks>
/// Edge identity is distinct from node identity because durable tokens and continuation evidence advance across
/// edges. Reusing a node identity as an edge identity would make branch continuity ambiguous.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ProcessEdgeId
{
    /// <summary>Creates a Process edge identity.</summary>
    /// <param name="value">Stable producer-assigned edge identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ProcessEdgeId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw Process edge identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw Process edge identity.</summary>
    /// <returns>The stable value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>One stable directed Process control-flow edge.</summary>
public sealed record ProcessEdge
{
    /// <summary>Creates a directed Process edge.</summary>
    /// <param name="id">Stable edge identity retained by tokens and continuation state.</param>
    /// <param name="target">Stable target Process-node identity.</param>
    [JsonConstructor]
    public ProcessEdge(ProcessEdgeId id, ExecutionNodeId target)
    {
        Id = id;
        Target = target;
    }

    /// <summary>Stable edge identity retained by tokens and continuation state.</summary>
    public ProcessEdgeId Id { get; }

    /// <summary>Stable target Process-node identity.</summary>
    public ExecutionNodeId Target { get; }
}

/// <summary>Typed coordination-local binding produced when a Process continuation is selected.</summary>
public sealed record ProcessOutputBinding
{
    /// <summary>Creates a typed Process output binding.</summary>
    /// <param name="binding">Stable value-binding identity.</param>
    /// <param name="contract">Portable contract of the produced value.</param>
    [JsonConstructor]
    public ProcessOutputBinding(ValueBindingId binding, ValueContract contract)
    {
        Binding = binding;
        Contract = contract;
    }

    /// <summary>Stable coordination-local value-binding identity.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Portable contract of the produced value.</summary>
    public ValueContract Contract { get; }
}

/// <summary>Typed continuation selected after one Process operation or branch completes.</summary>
public sealed record ProcessContinuation
{
    /// <summary>Creates a Process continuation.</summary>
    /// <param name="edge">Stable edge along which the owning token advances.</param>
    /// <param name="output">Optional typed binding receiving the operation or interaction result.</param>
    [JsonConstructor]
    public ProcessContinuation(ProcessEdge edge, ProcessOutputBinding? output = null)
    {
        Edge = edge;
        Output = output;
    }

    /// <summary>Stable edge along which the owning token advances.</summary>
    public ProcessEdge Edge { get; }

    /// <summary>Optional typed binding receiving the selected result.</summary>
    public ProcessOutputBinding? Output { get; }
}

/// <summary>Recovery behavior after an interruption that cannot transparently replay the current activation.</summary>
public enum ProcessRecoveryPolicy
{
    /// <summary>No recovery behavior was declared; invalid in canonical Process IR.</summary>
    Unspecified = 0,

    /// <summary>Restore durable continuation state and retain the current Process attempt identity.</summary>
    ContinueAttempt = 1,

    /// <summary>Abandon the interrupted attempt and restart under a new Process attempt identity.</summary>
    RestartAttempt = 2
}
