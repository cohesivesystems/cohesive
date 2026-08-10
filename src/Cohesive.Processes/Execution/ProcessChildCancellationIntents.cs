using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Execution;

/// <summary>Replay-stable semantic intent to request cancellation of one already-started child Process.</summary>
/// <remarks>
/// The authoritative durable source is the owning <see cref="ProcessChildState"/> in
/// <see cref="ProcessChildDisposition.CancellationRequested"/>. This projection gives runtimes and adapters a
/// deterministic, portable control surface after recovery without creating a second state authority. Construction
/// validates transport integrity; only projection from authoritative parent state proves that cancellation was
/// semantically requested.
/// </remarks>
public sealed record ProcessChildCancellationIntent
{
    /// <summary>Restores one exact portable cancellation intent projected from authoritative parent state.</summary>
    /// <exception cref="ArgumentException">Any required identity is empty or semantic identity evidence is incomplete.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="purpose"/> is unspecified or unsupported.</exception>
    [JsonConstructor]
    public ProcessChildCancellationIntent(
        string intentId,
        ExecutionDefinitionReference parentDefinition,
        ProcessContinuationIdentity parentContinuation,
        TokenId owner,
        TokenId token,
        ExecutionNodeId node,
        string childRegistrationId,
        EmissionId requestEmission,
        ExecutionDefinitionReference childDefinition,
        ProcessContinuationIdentity childContinuation,
        ProcessChildPurpose purpose)
    {
        if (string.IsNullOrWhiteSpace(intentId)
            || string.IsNullOrWhiteSpace(owner.Value)
            || string.IsNullOrWhiteSpace(token.Value)
            || string.IsNullOrWhiteSpace(node.Value)
            || string.IsNullOrWhiteSpace(childRegistrationId)
            || string.IsNullOrWhiteSpace(requestEmission.Value)
            || !IsValid(parentDefinition)
            || !IsValid(parentContinuation)
            || !IsValid(childDefinition)
            || !IsValid(childContinuation))
        {
            throw new ArgumentException(
                "A child cancellation intent requires complete parent, child, node, registration, and Request identity evidence.");
        }
        if (!Enum.IsDefined(purpose) || purpose == ProcessChildPurpose.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "A child cancellation purpose is required.");
        }
        IntentId = intentId;
        ParentDefinition = parentDefinition;
        ParentContinuation = parentContinuation;
        Owner = owner;
        Token = token;
        Node = node;
        ChildRegistrationId = childRegistrationId;
        RequestEmission = requestEmission;
        ChildDefinition = childDefinition;
        ChildContinuation = childContinuation;
        Purpose = purpose;
    }

    static bool IsValid(ExecutionDefinitionReference? definition) =>
        definition is not null
        && !string.IsNullOrWhiteSpace(definition.DefinitionId.Value)
        && !string.IsNullOrWhiteSpace(definition.RevisionId.Value)
        && definition.Fingerprint is not null
        && !string.IsNullOrWhiteSpace(definition.Fingerprint.Algorithm)
        && !string.IsNullOrWhiteSpace(definition.Fingerprint.Canonicalization)
        && !string.IsNullOrWhiteSpace(definition.Fingerprint.Value);

    static bool IsValid(ProcessContinuationIdentity? continuation) =>
        continuation is not null
        && !string.IsNullOrWhiteSpace(continuation.ProcessInstanceId.Value)
        && !string.IsNullOrWhiteSpace(continuation.ProcessAttemptId.Value);

    /// <summary>Opaque deterministic intent identity.</summary>
    public string IntentId { get; }

    /// <summary>Exact parent Process definition that authoritatively requested cancellation.</summary>
    public ExecutionDefinitionReference ParentDefinition { get; }

    /// <summary>Exact parent Process continuation retaining the cancellation disposition.</summary>
    public ProcessContinuationIdentity ParentContinuation { get; }

    /// <summary>Parent coordination token that owns the child occurrence.</summary>
    public TokenId Owner { get; }

    /// <summary>Parent-side token that emitted the child Request.</summary>
    public TokenId Token { get; }

    /// <summary>Canonical parent child-bearing node.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Replay-stable child occurrence registration.</summary>
    public string ChildRegistrationId { get; }

    /// <summary>Canonical child Request emission whose started work is being cancelled.</summary>
    public EmissionId RequestEmission { get; }

    /// <summary>Exact pinned child Process definition.</summary>
    public ExecutionDefinitionReference ChildDefinition { get; }

    /// <summary>Exact pinned child Process instance and attempt.</summary>
    public ProcessContinuationIdentity ChildContinuation { get; }

    /// <summary>Authored semantic purpose of the child work.</summary>
    public ProcessChildPurpose Purpose { get; }
}

/// <summary>Projects deterministic cancellation intents from authoritative durable child state.</summary>
public static class ProcessChildCancellationIntents
{
    /// <summary>Projects every currently requested propagated child cancellation in stable intent order.</summary>
    /// <param name="state">Exact parent Process continuation to inspect.</param>
    /// <returns>
    /// Replay-stable intents for children in <see cref="ProcessChildDisposition.CancellationRequested"/>; detached,
    /// pending, and terminal children contribute no intent.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A cancellation-requested child lacks its exact propagated policy, Request emission, or identity evidence.
    /// </exception>
    public static ImmutableArray<ProcessChildCancellationIntent> Project(ProcessContinuationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsValid(state.Definition) || !IsValid(state.Continuation))
        {
            throw new InvalidOperationException(
                "A child cancellation-intent projection requires exact parent definition and continuation evidence.");
        }

        var builder = ImmutableArray.CreateBuilder<ProcessChildCancellationIntent>();
        foreach (var child in state.Children)
        {
            if (child is null)
            {
                throw new InvalidOperationException(
                    "A child cancellation-intent projection cannot inspect a null restored child entry.");
            }

            if (child.Disposition != ProcessChildDisposition.CancellationRequested)
                continue;
            if (child.Cancellation != ProcessChildCancellationPolicy.Propagate
                || child.RequestEmission is not { } request
                || string.IsNullOrWhiteSpace(child.RegistrationId)
                || string.IsNullOrWhiteSpace(child.Owner.Value)
                || string.IsNullOrWhiteSpace(child.Token.Value)
                || string.IsNullOrWhiteSpace(child.Node.Value)
                || string.IsNullOrWhiteSpace(request.Value)
                || !IsValid(child.Process)
                || !IsValid(child.Continuation))
            {
                throw new InvalidOperationException(
                    "A child cancellation intent requires exact propagated Request and child identity evidence.");
            }

            builder.Add(new(
                ProcessReferenceIdentities.ChildCancellationIntent(
                    state.Continuation,
                    child.RegistrationId,
                    request),
                state.Definition,
                state.Continuation,
                child.Owner,
                child.Token,
                child.Node,
                child.RegistrationId,
                request,
                child.Process,
                child.Continuation,
                child.Purpose));
        }

        builder.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.IntentId, right.IntentId));
        return builder.Count == builder.Capacity ? builder.MoveToImmutable() : builder.ToImmutable();
    }

    static bool IsValid(ExecutionDefinitionReference? definition) =>
        definition is not null
        && !string.IsNullOrWhiteSpace(definition.DefinitionId.Value)
        && !string.IsNullOrWhiteSpace(definition.RevisionId.Value)
        && definition.Fingerprint is not null
        && !string.IsNullOrWhiteSpace(definition.Fingerprint.Algorithm)
        && !string.IsNullOrWhiteSpace(definition.Fingerprint.Canonicalization)
        && !string.IsNullOrWhiteSpace(definition.Fingerprint.Value);

    static bool IsValid(ProcessContinuationIdentity? continuation) =>
        continuation is not null
        && !string.IsNullOrWhiteSpace(continuation.ProcessInstanceId.Value)
        && !string.IsNullOrWhiteSpace(continuation.ProcessAttemptId.Value);
}
