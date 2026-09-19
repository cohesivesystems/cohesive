using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Integrations;

/// <summary>Exact externally retained bytes. A locator is not evidence until its bytes have been verified.</summary>
/// <param name="Locator">Stable opaque adapter-owned locator; never a credential or expiring signed URL.</param>
/// <param name="Sha256">Lowercase SHA-256 digest of the exact bytes, prefixed with sha256:.</param>
/// <param name="Length">Exact byte length, including zero for an empty payload.</param>
/// <param name="MediaType">Explicit payload interpretation, including a version where the source contract requires one.</param>
public sealed record IngestionContentReference(string Locator, string Sha256, long Length, string MediaType)
{
    const string DigestPrefix = "sha256:";
    /// <summary>Describes caller-owned bytes without retaining or copying them.</summary>
    /// <param name="locator">Stable locator at which the caller will retain these bytes.</param>
    /// <param name="bytes">Exact immutable input for this calculation.</param>
    /// <param name="mediaType">Explicit payload media type.</param>
    /// <returns>A descriptor; constructing it does not persist the bytes.</returns>
    /// <exception cref="ArgumentException">Locator or media type is blank or exceeds the identity bound.</exception>
    public static IngestionContentReference Describe(string locator, ReadOnlySpan<byte> bytes, string mediaType)
    {
        var result = new IngestionContentReference(locator, DigestPrefix + Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, mediaType);
        result.Validate();
        return result;
    }

    /// <summary>Checks length and digest before interpreting retrieved bytes.</summary>
    /// <param name="bytes">Retrieved bytes, owned by the caller.</param>
    /// <returns>Whether length and SHA-256 match exactly; metadata must separately match the retained descriptor.</returns>
    /// <exception cref="ArgumentException">This descriptor is malformed.</exception>
    public bool Matches(ReadOnlySpan<byte> bytes)
    {
        Validate();
        if (Length != bytes.Length) return false;
        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, actual);
        Convert.FromHexString(Sha256.AsSpan(DigestPrefix.Length), expected, out _, out _);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    internal void Validate()
    {
        IngestionLedgerAddress.Require(Locator);
        IngestionLedgerAddress.Require(MediaType);
        if (Length < 0 || Sha256 is null || Sha256.Length != DigestPrefix.Length + SHA256.HashSizeInBytes * 2 || !Sha256.StartsWith(DigestPrefix, StringComparison.Ordinal)
            || Sha256.AsSpan(DigestPrefix.Length).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("Content requires a nonnegative byte length and a lowercase sha256: digest.");
    }
}

/// <summary>One immutable retained boundary of an ingestion operation, persisted in a canonical work document.</summary>
/// <remarks>These records describe recovery evidence, not mutable workflow state. Processes owns sequencing and dispatch.</remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(IngestionAcquisitionRequest), "request")]
[JsonDerivedType(typeof(IngestionAcquisitionReceipt), "acquired")]
[JsonDerivedType(typeof(IngestionPreparedPublication), "prepared")]
public abstract record IngestionWorkItem;

/// <summary>Frozen source selection and original progress expectation, retained before acquisition.</summary>
/// <param name="Address">Independent flow/source/destination/partition scope.</param>
/// <param name="Definition">Exact ingestion definition, including the chosen commit profile.</param>
/// <param name="Transformation">Exact transformation and dependency closure selected before acquisition; never refreshed during recovery.</param>
/// <param name="OperationId">Stable logical publication identity within Address, preserved through ambiguous outcomes.</param>
/// <param name="AttemptId">Stable acquisition identity; losing an acknowledgment never authorizes a replacement.</param>
/// <param name="Selection">Exact serialized source selection and dependency closure, including provider/normalizer configuration.</param>
/// <param name="ExpectedLedgerRevision">Frozen separate-ledger expectation; zero means absent, null means no separate ledger.</param>
/// <param name="SelectedAtUtc">UTC observation at which this selection was frozen.</param>
public sealed record IngestionAcquisitionRequest(IngestionLedgerAddress Address, ExecutionDefinitionReference Definition,
    ExecutionDefinitionReference Transformation, string OperationId, string AttemptId, IngestionContentReference Selection, long? ExpectedLedgerRevision,
    DateTimeOffset SelectedAtUtc) : IngestionWorkItem;

/// <summary>Acquired source evidence bound to the exact original request; retain before transformation.</summary>
/// <param name="Request">Exact request document reference, including fingerprint.</param>
/// <param name="Content">Exact source evidence or an immutable manifest of verified source payloads.</param>
/// <param name="ObservedAtUtc">UTC acquisition observation, distinct from source market/event time.</param>
public sealed record IngestionAcquisitionReceipt(ExecutionDefinitionReference Request, IngestionContentReference Content,
    DateTimeOffset ObservedAtUtc) : IngestionWorkItem;

/// <summary>Authoritative prepared sink input; retries resolve these bytes and never rerun transformation.</summary>
/// <param name="Acquisition">Exact retained acquisition document reference.</param>
/// <param name="Transformation">Exact transformation definition and dependency closure; no ambient executable callbacks.</param>
/// <param name="Content">Exact serialized publication intent, including sink guards and domain payload closure.</param>
/// <param name="NextPosition">Proposed source progress for a separate ledger, or null for an atomic sink-only profile.</param>
/// <param name="PreparedAtUtc">UTC observation at which publication was frozen.</param>
public sealed record IngestionPreparedPublication(ExecutionDefinitionReference Acquisition, ExecutionDefinitionReference Transformation,
    IngestionContentReference Content, IngestionPosition? NextPosition, DateTimeOffset PreparedAtUtc) : IngestionWorkItem;
