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
/// validates transport integrity; only projection from authoritative parent state, or from that state together with
/// its exact canonical attempt-restart intent, proves that cancellation was semantically requested.
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

/// <summary>Exact runtime observation that one propagated child cancellation reached terminal closure.</summary>
/// <remarks>
/// The owning parent continuation remains authority for whether cancellation was requested. This observation is
/// admissible only when its intent identity and child continuation match that retained state exactly.
/// </remarks>
public sealed record ProcessChildCancellationClosure
{
    /// <summary>Creates one attributable propagated-child closure observation.</summary>
    /// <param name="intentId">Exact projected child-cancellation intent identity.</param>
    /// <param name="childContinuation">Exact child Process instance and attempt that closed.</param>
    /// <param name="outcome">Observed terminal child outcome.</param>
    /// <param name="observedAtUtc">Explicit UTC closure observation time.</param>
    /// <exception cref="ArgumentException">Identity or time evidence is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is nonterminal or unsupported.</exception>
    [JsonConstructor]
    public ProcessChildCancellationClosure(
        string intentId,
        ProcessContinuationIdentity childContinuation,
        ExecutionTerminalOutcomeKind outcome,
        DateTimeOffset observedAtUtc)
    {
        IntentId = Guard.RequireNotNullOrWhiteSpace(intentId);
        ChildContinuation = childContinuation ?? throw new ArgumentNullException(nameof(childContinuation));
        if (string.IsNullOrWhiteSpace(childContinuation.ProcessInstanceId.Value)
            || string.IsNullOrWhiteSpace(childContinuation.ProcessAttemptId.Value))
        {
            throw new ArgumentException("A child cancellation closure requires exact child continuation identity.", nameof(childContinuation));
        }
        if (outcome is not (ExecutionTerminalOutcomeKind.Completed
            or ExecutionTerminalOutcomeKind.Failed
            or ExecutionTerminalOutcomeKind.Cancelled
            or ExecutionTerminalOutcomeKind.Terminated))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A child cancellation closure must be terminal.");
        }
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("A child cancellation closure time must use the UTC offset.", nameof(observedAtUtc));

        Outcome = outcome;
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Exact projected child-cancellation intent identity.</summary>
    public string IntentId { get; }

    /// <summary>Exact child Process instance and attempt that closed.</summary>
    public ProcessContinuationIdentity ChildContinuation { get; }

    /// <summary>Observed terminal child outcome.</summary>
    public ExecutionTerminalOutcomeKind Outcome { get; }

    /// <summary>Explicit UTC closure observation time.</summary>
    public DateTimeOffset ObservedAtUtc { get; }
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
    public static ImmutableArray<ProcessChildCancellationIntent> Project(ProcessContinuationState state) =>
        ProjectCore(
            state,
            static child => child.Disposition == ProcessChildDisposition.CancellationRequested);

    /// <summary>
    /// Projects the propagated child cancellations required to close an abandoned attempt before its replacement
    /// may start child work.
    /// </summary>
    /// <param name="state">Exact continuation of the attempt being abandoned.</param>
    /// <param name="restart">Canonical lifecycle intent authorizing the attempt replacement.</param>
    /// <returns>
    /// Replay-stable intents for active or already cancellation-requested children whose authored policy is
    /// <see cref="ProcessChildCancellationPolicy.Propagate"/>. Detached, pending, and terminal children contribute
    /// no intent.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="restart"/> does not close the exact Process instance and attempt retained by
    /// <paramref name="state"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A selected child lacks its exact propagated policy, Request emission, or identity evidence.
    /// </exception>
    public static ImmutableArray<ProcessChildCancellationIntent> ProjectAttemptRestart(
        ProcessContinuationState state,
        ProcessAttemptRestartIntent restart)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(restart);
        if (restart.ProcessInstanceId != state.Continuation.ProcessInstanceId
            || restart.AbandonedAttemptId != state.Continuation.ProcessAttemptId)
        {
            throw new ArgumentException(
                "A child restart-closure projection requires the canonical intent for the retained parent attempt.",
                nameof(restart));
        }

        return ProjectCore(
            state,
            static child => child.Cancellation == ProcessChildCancellationPolicy.Propagate
                && child.Disposition is
                    ProcessChildDisposition.Active or ProcessChildDisposition.CancellationRequested);
    }

    static ImmutableArray<ProcessChildCancellationIntent> ProjectCore(
        ProcessContinuationState state,
        Func<ProcessChildState, bool> include)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(include);
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

            if (!include(child))
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
