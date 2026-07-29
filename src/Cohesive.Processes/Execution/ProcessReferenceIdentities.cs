using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Execution;

namespace Cohesive.Processes.Execution;

/// <summary>Derives replay-stable identities for the canonical Process reference interpreter.</summary>
/// <remarks>
/// Every derivation is domain-separated and includes <see cref="Version"/> in a sequence of length-prefixed
/// UTF-8 fields. SHA-256 digests are rendered as lowercase hexadecimal after a purpose-specific prefix. Changing
/// the field order, encoding, or meaning requires a new convention version so persisted continuation identities
/// never silently change interpretation.
/// </remarks>
internal static class ProcessReferenceIdentities
{
    internal const string Version = "cohesive.processes.reference-identities/v1";

    const string RootTokenPurpose = "root-token";
    const string ForkTokenPurpose = "fork-token";
    const string ForkRegistrationPurpose = "fork-registration";
    const string EmissionPurpose = "emission";
    const string IdempotencyPurpose = "interaction-idempotency";
    const string WaitRegistrationPurpose = "wait-registration";

    const string TokenPrefix = "process-token:v1:sha256:";
    const string ForkRegistrationPrefix = "process-fork:v1:sha256:";
    const string EmissionPrefix = "process-emission:v1:sha256:";
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
