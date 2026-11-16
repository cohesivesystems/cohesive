using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Storage;

/// <summary>Durable identity of a direct activation or a fully specified Process operation.</summary>
/// <remarks>Process coordinates are present together or absent together. Existing Process identities retain
/// their wire representation; direct identities have null Process coordinates. The validated structural encoding
/// also permits receipt locators to remain portable typed values.</remarks>
public sealed record EntityTransitionOperationIdentity
{
    /// <summary>Rehydrates a direct or Process identity, rejecting partial Process coordinates.</summary>
    /// <param name="activation">Stable activation; also the complete identity of a direct operation.</param>
    /// <param name="continuation">Process instance and attempt, or null for direct execution.</param>
    /// <param name="token">Process token, present only with the other Process coordinates.</param>
    /// <param name="node">Process invocation node, present only with the other Process coordinates.</param>
    /// <param name="occurrence">Nonnegative occurrence, present only with the other Process coordinates.</param>
    /// <exception cref="ArgumentException">The activation is empty or Process coordinates are incomplete.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The occurrence is negative.</exception>
    [JsonConstructor]
    public EntityTransitionOperationIdentity(ActivationId activation, ProcessContinuationIdentity? continuation = null,
        TokenId? token = null, ExecutionNodeId? node = null, long? occurrence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activation.Value);
        if (continuation is null)
        {
            if (token is not null || node is not null || occurrence is not null)
                throw new ArgumentException("Process coordinates must be present together or absent together.");
        }
        else
        {
            if (token is null || node is null || occurrence is null)
                throw new ArgumentException("Process coordinates must be present together or absent together.");
            ArgumentException.ThrowIfNullOrWhiteSpace(token.Value.Value);
            ArgumentException.ThrowIfNullOrWhiteSpace(node.Value.Value);
            if (occurrence.Value < 0) throw new ArgumentOutOfRangeException(nameof(occurrence));
        }
        Activation = activation;
        Continuation = continuation;
        Token = token;
        Node = node;
        Occurrence = occurrence;
    }

    /// <summary>Decision activation and stable direct operation identity.</summary>
    public ActivationId Activation { get; }
    /// <summary>Process instance and attempt; absent for direct execution.</summary>
    public ProcessContinuationIdentity? Continuation { get; }
    /// <summary>Process token; absent for direct execution.</summary>
    public TokenId? Token { get; }
    /// <summary>Process invocation node; absent for direct execution.</summary>
    public ExecutionNodeId? Node { get; }
    /// <summary>Process occurrence; absent for direct execution.</summary>
    public long? Occurrence { get; }

    /// <summary>Projects a real Process occurrence without fabricating provenance.</summary>
    /// <param name="operation">Complete Process occurrence.</param>
    /// <returns>The corresponding receipt identity with unchanged canonical Process coordinates.</returns>
    /// <exception cref="ArgumentNullException">The occurrence is null.</exception>
    public static implicit operator EntityTransitionOperationIdentity(ProcessOperationOccurrence operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new(operation.Activation, operation.Continuation, operation.Token, operation.Node, operation.Occurrence);
    }

    /// <summary>Projects a direct operation without Process coordinates.</summary>
    /// <param name="operation">Stable direct operation identity.</param>
    /// <returns>A direct receipt identity.</returns>
    /// <exception cref="ArgumentNullException">The operation is null.</exception>
    public static implicit operator EntityTransitionOperationIdentity(DirectTransitionOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new(operation.Activation);
    }
}

/// <summary>A caller-assigned direct identity, reused unchanged on retries and unique within the bound repository.</summary>
public sealed record DirectTransitionOperation
{
    /// <summary>Creates a direct operation without Process provenance.</summary>
    /// <param name="activation">Stable identity; never generate a new value for a retry.</param>
    /// <exception cref="ArgumentException">The activation is empty.</exception>
    [JsonConstructor]
    public DirectTransitionOperation(ActivationId activation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activation.Value);
        Activation = activation;
    }
    /// <summary>Stable direct invocation identity.</summary>
    public ActivationId Activation { get; }
}
