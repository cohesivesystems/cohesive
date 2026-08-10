using System.Security.Cryptography;
using System.Text;

namespace Cohesive.Execution;

/// <summary>Canonical deterministic physical identities shared by durable-operation runtimes.</summary>
/// <remarks>
/// Request <see cref="InteractionEnvelopeContext.EmissionId"/> remains the logical operation identity. These
/// derived identities name attempt and Reply evidence without introducing a second logical operation key. The
/// derivation is versioned and deliberately shared so moving an operation between faithful runtimes does not
/// change its target idempotency or Reply identity.
/// </remarks>
public static class DurableOperationIdentities
{
    const string Version = "cohesive.processes.runtime/v1";

    /// <summary>Derives one stable physical attempt identity from a logical operation and one-based ordinal.</summary>
    /// <param name="operationId">Canonical Request emission identity.</param>
    /// <param name="ordinal">One-based physical attempt ordinal.</param>
    /// <returns>Stable attempt identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="operationId"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is not positive.</exception>
    public static OperationAttemptId Attempt(EmissionId operationId, int ordinal)
    {
        RequireOperationId(operationId);
        if (ordinal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "An operation attempt ordinal must be positive.");
        }
        return new(Derive(
            "operation-attempt",
            operationId.Value,
            ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>Derives the single stable Reply emission identity for a logical operation.</summary>
    /// <param name="operationId">Canonical Request emission identity.</param>
    /// <returns>Stable Reply emission identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="operationId"/> is default.</exception>
    public static EmissionId Reply(EmissionId operationId)
    {
        RequireOperationId(operationId);
        return new(Derive("operation-reply", operationId.Value));
    }

    /// <summary>Derives the single stable Reply idempotency key for a logical operation.</summary>
    /// <param name="operationId">Canonical Request emission identity.</param>
    /// <returns>Stable Reply idempotency key.</returns>
    /// <exception cref="ArgumentException"><paramref name="operationId"/> is default.</exception>
    public static InteractionIdempotencyKey ReplyIdempotency(EmissionId operationId)
    {
        RequireOperationId(operationId);
        return new(Derive("operation-reply-idempotency", operationId.Value));
    }

    static string Derive(string purpose, params string[] components)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Version);
        Append(hash, purpose);
        foreach (var component in components)
        {
            Append(hash, component);
        }
        return $"{purpose}/sha256-v1:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    static void Append(IncrementalHash hash, string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthBytes, length);
        hash.AppendData(lengthBytes);
        hash.AppendData(Encoding.UTF8.GetBytes(value));
    }

    static void RequireOperationId(EmissionId operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId.Value))
        {
            throw new ArgumentException("A durable operation identity cannot be default.", nameof(operationId));
        }
    }
}
