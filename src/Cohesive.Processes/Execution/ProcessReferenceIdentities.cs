using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Execution;

/// <summary>Derives replay-stable identities for the canonical Process reference interpreter.</summary>
/// <remarks>
/// Every derivation is domain-separated and includes <see cref="Version"/> in a sequence of length-prefixed
/// UTF-8 fields. SHA-256 digests are rendered as lowercase hexadecimal after a purpose-specific prefix. Changing
/// the field order, encoding, or meaning requires a new convention version so persisted continuation identities
/// never silently change interpretation.
/// </remarks>
public static class ProcessReferenceIdentities
{
    internal const string Version = "cohesive.processes.reference-identities/v1";

    const string RootTokenPurpose = "root-token";
    const string ForkTokenPurpose = "fork-token";
    const string ForkRegistrationPurpose = "fork-registration";
    const string ChildRegistrationPurpose = "child-registration";
    const string ChildInstancePurpose = "child-instance";
    const string ChildAttemptPurpose = "child-attempt";
    const string ChildCancellationIntentPurpose = "child-cancellation-intent";
    const string PartitionRegistrationPurpose = "partition-registration";
    const string PartitionTokenPurpose = "partition-token";
    const string RecurrenceRegistrationPurpose = "recurrence-registration";
    const string EmissionPurpose = "emission";
    const string TransitionEmissionPurpose = "transition-emission";
    const string IdempotencyPurpose = "interaction-idempotency";
    const string WaitRegistrationPurpose = "wait-registration";

    const string TokenPrefix = "process-token:v1:sha256:";
    const string ForkRegistrationPrefix = "process-fork:v1:sha256:";
    const string ChildRegistrationPrefix = "process-child:v1:sha256:";
    const string ChildInstancePrefix = "process-child-instance:v1:sha256:";
    const string ChildAttemptPrefix = "process-child-attempt:v1:sha256:";
    const string ChildCancellationIntentPrefix = "process-child-cancel:v1:sha256:";
    const string PartitionRegistrationPrefix = "process-partition:v1:sha256:";
    const string RecurrenceRegistrationPrefix = "process-recurrence:v1:sha256:";
    const string EmissionPrefix = "process-emission:v1:sha256:";
    const string TransitionEmissionPrefix = "process-transition-emission:v1:sha256:";
    const string IdempotencyPrefix = "process-idempotency:v1:sha256:";
    const string WaitRegistrationPrefix = "process-wait:v1:sha256:";

    /// <summary>Derives the sole root token for one exact Process continuation attempt.</summary>
    /// <param name="continuation">Logical Process instance and attempt that own the token.</param>
    /// <returns>The same token identity for every replay of the same Process attempt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="continuation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="continuation"/> contains a default identity.</exception>
    internal static TokenId RootToken(ProcessContinuationIdentity continuation)
    {
        RequireContinuation(continuation);
        return new(Derive(
            TokenPrefix,
            RootTokenPurpose,
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value));
    }

    /// <summary>Derives one child token from its exact Fork occurrence, owner, and semantic branch.</summary>
    /// <param name="continuation">Logical Process instance and attempt that own the Fork.</param>
    /// <param name="owner">Token whose current occurrence executed the Fork.</param>
    /// <param name="fork">Canonical Fork node identity.</param>
    /// <param name="forkOccurrence">Zero-based occurrence of the Fork in the owner token's durable history.</param>
    /// <param name="branch">Canonical identity of the selected Fork branch.</param>
    /// <returns>A replay-stable token identity unique to the branch occurrence within the Process attempt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="continuation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="continuation"/>, <paramref name="owner"/>, <paramref name="fork"/>, or
    /// <paramref name="branch"/> contains a default identity.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="forkOccurrence"/> is negative.</exception>
    internal static TokenId ForkToken(
        ProcessContinuationIdentity continuation,
        TokenId owner,
        ExecutionNodeId fork,
        long forkOccurrence,
        ExecutionNodeId branch)
    {
        RequireContinuation(continuation);
        RequireIdentity(owner.Value, nameof(owner));
        RequireIdentity(fork.Value, nameof(fork));
        RequireNonNegative(forkOccurrence, nameof(forkOccurrence));
        RequireIdentity(branch.Value, nameof(branch));

        return new(Derive(
            TokenPrefix,
            ForkTokenPurpose,
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            owner.Value,
            fork.Value,
            forkOccurrence.ToString(CultureInfo.InvariantCulture),
            branch.Value));
    }

    /// <summary>Derives the durable identity of one Fork occurrence owned by one token.</summary>
    /// <param name="continuation">Logical Process instance and attempt that own the Fork.</param>
    /// <param name="owner">Token executing the Fork.</param>
    /// <param name="fork">Canonical Fork node identity.</param>
    /// <param name="forkOccurrence">Zero-based occurrence in the owner-token history.</param>
    /// <returns>A replay-stable opaque Fork registration identity.</returns>
    internal static string ForkRegistration(
        ProcessContinuationIdentity continuation,
        TokenId owner,
        ExecutionNodeId fork,
        long forkOccurrence)
    {
        RequireContinuation(continuation);
        RequireIdentity(owner.Value, nameof(owner));
        RequireIdentity(fork.Value, nameof(fork));
        RequireNonNegative(forkOccurrence, nameof(forkOccurrence));
        return Derive(
            ForkRegistrationPrefix,
            ForkRegistrationPurpose,
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            owner.Value,
            fork.Value,
            forkOccurrence.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Derives one exact child occurrence registration.</summary>
    /// <param name="continuation">Parent Process continuation that owns the child.</param>
    /// <param name="owner">Parent coordination token.</param>
    /// <param name="node">Canonical child-bearing node.</param>
    /// <param name="occurrence">Zero-based occurrence in the owner-token history.</param>
    /// <param name="progressIdentity">Partition progress identity, or null for a direct child.</param>
    /// <returns>A replay-stable child registration identity.</returns>
    internal static string ChildRegistration(
        ProcessContinuationIdentity continuation,
        TokenId owner,
        ExecutionNodeId node,
        long occurrence,
        string? progressIdentity)
    {
        RequireContinuation(continuation);
        RequireIdentity(owner.Value, nameof(owner));
        RequireIdentity(node.Value, nameof(node));
        RequireNonNegative(occurrence, nameof(occurrence));
        if (progressIdentity is not null)
        {
            RequireIdentity(progressIdentity, nameof(progressIdentity));
        }

        return Derive(
            ChildRegistrationPrefix,
            ChildRegistrationPurpose,
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            owner.Value,
            node.Value,
            occurrence.ToString(CultureInfo.InvariantCulture),
            progressIdentity ?? string.Empty);
    }

    /// <summary>Derives the first exact continuation identity of one child occurrence.</summary>
    /// <param name="continuation">Parent Process continuation that owns the child.</param>
    /// <param name="owner">Parent coordination token.</param>
    /// <param name="node">Canonical child-bearing node.</param>
    /// <param name="occurrence">Zero-based occurrence in the owner-token history.</param>
    /// <param name="progressIdentity">Partition progress identity, or null for a direct child.</param>
    /// <param name="process">Exact child Process definition.</param>
    /// <returns>Replay-stable child instance and first-attempt identities.</returns>
    internal static ProcessContinuationIdentity ChildContinuation(
        ProcessContinuationIdentity continuation,
        TokenId owner,
        ExecutionNodeId node,
        long occurrence,
        string? progressIdentity,
        ExecutionDefinitionReference process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var registration = ChildRegistration(continuation, owner, node, occurrence, progressIdentity);
        var fields = new[]
        {
            registration,
            process.DefinitionId.Value,
            process.RevisionId.Value,
            process.Fingerprint.Algorithm,
            process.Fingerprint.Canonicalization,
            process.Fingerprint.Value
        };
        return new(
            new(Derive(ChildInstancePrefix, ChildInstancePurpose, fields)),
            new(Derive(ChildAttemptPrefix, ChildAttemptPurpose, fields)));
    }

    /// <summary>
    /// Attempts to verify a child Request's unique reference-interpreter identity evidence and derive its child
    /// registration.
    /// </summary>
    /// <param name="parent">Exact parent Process definition that authored the Request.</param>
    /// <param name="node">Canonical direct or partitioned child-bearing Process node.</param>
    /// <param name="request">Canonical child Request envelope to verify.</param>
    /// <param name="registration">
    /// Receives the replay-stable child registration when every identity rederives; otherwise null.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the parent, child definition, outcome mapping, continuation, emission,
    /// idempotency key, response wait, owner occurrence, and optional partition progress all rederive exactly.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="parent"/>, <paramref name="node"/>, or <paramref name="request"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static bool TryGetCanonicalChildRegistration(
        ExecutionDefinitionReference parent,
        ProcessNode node,
        RequestEnvelope request,
        out string? registration)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(request);
        registration = null;
        if (!ProcessRequestSemantics.TryProjectChild(node, out var child)
            || request.Contract != child.Contract
            || request.ChildTarget is not { } target
            || request.Context.Origin is not ProcessInteractionOrigin origin
            || origin.Definition != parent
            || origin.Node != node.Id
            || origin.Entity is not null
            || origin.Transition is not null
            || origin.TransitionNode is not null
            || origin.Outcome is not null
            || request.ResponseTarget is not ProcessTokenInteractionTarget responseTarget)
        {
            return false;
        }

        var partitioned = child.Multiplicity == ProcessChildRequestMultiplicity.Partitioned;
        if (partitioned != (target.ProgressIdentity is not null))
            return false;

        var requestToken = partitioned
            ? PartitionToken(
                origin.Continuation,
                target.OwnerToken,
                node.Id,
                target.Occurrence,
                target.ProgressIdentity!)
            : target.OwnerToken;
        var tokenStep = partitioned ? 0 : target.Occurrence;
        var emission = Emission(
            origin.Continuation,
            origin.Activation,
            requestToken,
            node.Id,
            tokenStep);
        var continuation = ChildContinuation(
            origin.Continuation,
            target.OwnerToken,
            node.Id,
            target.Occurrence,
            target.ProgressIdentity,
            child.Process);
        var valid = origin.Token == requestToken
            && target.Definition == child.Process
            && target.OutcomeMapping == child.OutcomeMapping
            && target.Continuation == continuation
            && request.Context.EmissionId == emission
            && request.Context.IdempotencyKey == Idempotency(emission)
            && responseTarget.Continuation == origin.Continuation
            && responseTarget.Token == requestToken
            && responseTarget.WaitRegistrationId == WaitRegistration(
                origin.Continuation,
                requestToken,
                node.Id,
                tokenStep);
        if (valid)
        {
            registration = ChildRegistration(
                origin.Continuation,
                target.OwnerToken,
                node.Id,
                target.Occurrence,
                target.ProgressIdentity);
        }
        return valid;
    }

    /// <summary>Derives one replay-stable child cancellation-intent identity.</summary>
    /// <param name="continuation">Parent Process continuation that owns the intent.</param>
    /// <param name="registration">Exact child occurrence registration.</param>
    /// <param name="request">Canonical child Request emission being controlled.</param>
    /// <returns>A purpose-separated opaque cancellation-intent identity.</returns>
    internal static string ChildCancellationIntent(
        ProcessContinuationIdentity continuation,
        string registration,
        EmissionId request)
    {
        RequireContinuation(continuation);
        RequireIdentity(registration, nameof(registration));
        RequireIdentity(request.Value, nameof(request));
        return Derive(
            ChildCancellationIntentPrefix,
            ChildCancellationIntentPurpose,
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            registration,
            request.Value);
    }

    /// <summary>Derives the coordinator registration of one bounded partition occurrence.</summary>
    /// <param name="continuation">Parent Process continuation.</param>
    /// <param name="owner">Token executing bounded partition work.</param>
    /// <param name="node">Canonical bounded-work node.</param>
    /// <param name="occurrence">Zero-based occurrence in the owner-token history.</param>
    /// <returns>A replay-stable bounded-work registration identity.</returns>
    internal static string PartitionRegistration(
        ProcessContinuationIdentity continuation,
        TokenId owner,
        ExecutionNodeId node,
        long occurrence)
    {
        RequireContinuation(continuation);
        RequireIdentity(owner.Value, nameof(owner));
        RequireIdentity(node.Value, nameof(node));
        RequireNonNegative(occurrence, nameof(occurrence));
        return Derive(
            PartitionRegistrationPrefix,
            PartitionRegistrationPurpose,
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            owner.Value,
            node.Value,
            occurrence.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Derives the Request-owning token for one partition child.</summary>
    /// <param name="continuation">Parent Process continuation.</param>
    /// <param name="owner">Bounded-work coordinator token.</param>
    /// <param name="node">Canonical bounded-work node.</param>
    /// <param name="occurrence">Zero-based occurrence in the owner-token history.</param>
    /// <param name="progressIdentity">Authored stable partition progress identity.</param>
    /// <returns>A replay-stable child Request token.</returns>
    internal static TokenId PartitionToken(
        ProcessContinuationIdentity continuation,
        TokenId owner,
        ExecutionNodeId node,
        long occurrence,
        string progressIdentity)
    {
        RequireContinuation(continuation);
        RequireIdentity(owner.Value, nameof(owner));
        RequireIdentity(node.Value, nameof(node));
        RequireNonNegative(occurrence, nameof(occurrence));
        RequireIdentity(progressIdentity, nameof(progressIdentity));
        return new(Derive(
            TokenPrefix,
            PartitionTokenPurpose,
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            owner.Value,
            node.Value,
            occurrence.ToString(CultureInfo.InvariantCulture),
            progressIdentity));
    }

    /// <summary>Derives one explicit recurrence occurrence registration.</summary>
    /// <param name="continuation">Parent Process continuation.</param>
    /// <param name="token">Token executing the recurrence.</param>
    /// <param name="node">Canonical recurrence node.</param>
    /// <param name="occurrence">Zero-based originating occurrence in the token history.</param>
    /// <returns>A replay-stable recurrence registration identity.</returns>
    internal static string RecurrenceRegistration(
        ProcessContinuationIdentity continuation,
        TokenId token,
        ExecutionNodeId node,
        long occurrence)
    {
        RequireContinuation(continuation);
        RequireIdentity(token.Value, nameof(token));
        RequireIdentity(node.Value, nameof(node));
        RequireNonNegative(occurrence, nameof(occurrence));
        return Derive(
            RecurrenceRegistrationPrefix,
            RecurrenceRegistrationPurpose,
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            token.Value,
            node.Value,
            occurrence.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Derives the logical identity of one Process interaction emission.</summary>
    /// <param name="continuation">Logical Process instance and attempt that own the emission.</param>
    /// <param name="activation">Finite activation that first materialized the emission.</param>
    /// <param name="token">Token that executed the emitting node.</param>
    /// <param name="node">Canonical emitting-node identity.</param>
    /// <param name="tokenStep">Zero-based durable execution step of the node in the token history.</param>
    /// <returns>The stable emission identity retained across dispatch retries and activation replay.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="continuation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="continuation"/>, <paramref name="activation"/>, <paramref name="token"/>, or
    /// <paramref name="node"/> contains a default identity.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokenStep"/> is negative.</exception>
    internal static EmissionId Emission(
        ProcessContinuationIdentity continuation,
        ActivationId activation,
        TokenId token,
        ExecutionNodeId node,
        long tokenStep)
    {
        RequireContinuation(continuation);
        RequireIdentity(activation.Value, nameof(activation));
        RequireIdentity(token.Value, nameof(token));
        RequireIdentity(node.Value, nameof(node));
        RequireNonNegative(tokenStep, nameof(tokenStep));

        return new(Derive(
            EmissionPrefix,
            EmissionPurpose,
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            activation.Value,
            token.Value,
            node.Value,
            tokenStep.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Derives one logical emission from an exact Process Transition occurrence and canonical Transition node.
    /// </summary>
    /// <param name="invocation">Exact Process Transition operation occurrence.</param>
    /// <param name="transitionNode">Canonical emitting node in the invoked Transition.</param>
    /// <returns>A replay-stable identity unique to the Transition emission occurrence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="invocation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="transitionNode"/> is a default identity.</exception>
    internal static EmissionId TransitionEmission(
        ProcessTransitionInvocation invocation,
        ExecutionNodeId transitionNode)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        RequireIdentity(transitionNode.Value, nameof(transitionNode));
        return new(Derive(
            TransitionEmissionPrefix,
            TransitionEmissionPurpose,
            invocation.Continuation.ProcessInstanceId.Value,
            invocation.Continuation.ProcessAttemptId.Value,
            invocation.Activation.Value,
            invocation.Token.Value,
            invocation.Node.Value,
            invocation.Occurrence.ToString(CultureInfo.InvariantCulture),
            transitionNode.Value));
    }

    /// <summary>Derives the interaction deduplication key owned by one logical Process emission.</summary>
    /// <param name="emission">Stable logical emission identity.</param>
    /// <returns>A purpose-separated idempotency key that is stable across delivery retries.</returns>
    /// <exception cref="ArgumentException"><paramref name="emission"/> is a default identity.</exception>
    internal static InteractionIdempotencyKey Idempotency(EmissionId emission)
    {
        RequireIdentity(emission.Value, nameof(emission));
        return new(Derive(IdempotencyPrefix, IdempotencyPurpose, emission.Value));
    }

    /// <summary>Derives the durable identity of a wait registered by one token-node occurrence.</summary>
    /// <param name="continuation">Logical Process instance and attempt that own the wait.</param>
    /// <param name="token">Token registering the wait.</param>
    /// <param name="node">Canonical node whose semantics define the wait.</param>
    /// <param name="tokenStep">Zero-based durable execution step of the node in the token history.</param>
    /// <returns>A stable opaque registration identity shared by every clause in the wait occurrence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="continuation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="continuation"/>, <paramref name="token"/>, or <paramref name="node"/> contains a default
    /// identity.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tokenStep"/> is negative.</exception>
    internal static ProcessWaitRegistrationId WaitRegistration(
        ProcessContinuationIdentity continuation,
        TokenId token,
        ExecutionNodeId node,
        long tokenStep)
    {
        RequireContinuation(continuation);
        RequireIdentity(token.Value, nameof(token));
        RequireIdentity(node.Value, nameof(node));
        RequireNonNegative(tokenStep, nameof(tokenStep));

        return new(Derive(
            WaitRegistrationPrefix,
            WaitRegistrationPurpose,
            continuation.ProcessInstanceId.Value,
            continuation.ProcessAttemptId.Value,
            token.Value,
            node.Value,
            tokenStep.ToString(CultureInfo.InvariantCulture)));
    }

    static string Derive(string prefix, string purpose, params ReadOnlySpan<string> fields)
    {
        var canonical = new ArrayBufferWriter<byte>();
        Append(canonical, Version);
        Append(canonical, purpose);
        foreach (var field in fields)
        {
            Append(canonical, field);
        }

        return prefix + Convert.ToHexStringLower(SHA256.HashData(canonical.WrittenSpan));
    }

    static void Append(ArrayBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteAscii(writer, byteCount.ToString(CultureInfo.InvariantCulture));
        WriteByte(writer, (byte)':');

        var destination = writer.GetSpan(byteCount);
        writer.Advance(Encoding.UTF8.GetBytes(value, destination));
        WriteByte(writer, (byte)';');
    }

    static void WriteAscii(ArrayBufferWriter<byte> writer, string value)
    {
        var destination = writer.GetSpan(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            destination[index] = (byte)value[index];
        }

        writer.Advance(value.Length);
    }

    static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    static void RequireContinuation(ProcessContinuationIdentity continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        RequireIdentity(continuation.ProcessInstanceId.Value, nameof(continuation));
        RequireIdentity(continuation.ProcessAttemptId.Value, nameof(continuation));
    }

    static void RequireIdentity(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-default execution identity is required.", parameterName);
        }
    }

    static void RequireNonNegative(long value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "An occurrence or step must not be negative.");
        }
    }
}
