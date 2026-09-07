using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Commits;

/// <summary>One strict tagged portable-value wire profile for storage commit declarations and receipts.</summary>
public static class StorageCommitJson
{
    static readonly JsonSerializerOptions Options = EntityStorageJson.CreateOptions();
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Creates extensible serializer options sharing the canonical tagged value codec.</summary>
    /// <returns>Fresh caller-owned options; configure before first use.</returns>
    public static JsonSerializerOptions CreateOptions() => EntityStorageJson.CreateOptions();

    /// <summary>Serializes a declaration into deterministic portable JSON.</summary>
    /// <param name="intent">Immutable declaration to persist or inspect.</param>
    /// <returns>Canonical JSON whose computed fingerprint is derived, not duplicated.</returns>
    /// <exception cref="ArgumentNullException">The intent is null.</exception>
    public static string Serialize(StorageCommitIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(intent, Options));
    }

    /// <summary>Reads a closed declaration, revalidates its invariants and computes its fingerprint.</summary>
    /// <param name="json">Portable declaration JSON.</param>
    /// <returns>A validated immutable declaration.</returns>
    /// <exception cref="JsonException">The document has duplicate/unknown fields or invalid structure.</exception>
    /// <exception cref="ArgumentException">A declaration invariant is violated.</exception>
    public static StorageCommitIntent Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (StrictDocumentJson.TryFindDuplicateProperty(document.RootElement, "", out var location))
            throw new JsonException($"Duplicate property at {location}.");
        return JsonSerializer.Deserialize<StorageCommitIntent>(json, Options)
            ?? throw new JsonException("A commit declaration cannot be null.");
    }

    internal static string ComputeFingerprint(StorageCommitIntent intent) =>
        "storage-commit/v1/sha256/" + Convert.ToHexStringLower(SHA256.HashData(
            StrictDocumentJson.GetCanonicalBytes(intent, Options)));

    internal static string RequireIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try { StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException error) { throw new ArgumentException("Identity contains invalid Unicode.", nameof(value), error); }
        return value;
    }

    internal static PortableValue RequireValue(PortableValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.State is not (PortableValueState.Concrete or PortableValueState.Null))
            throw new ArgumentException("A storage commit requires a concrete or null portable value.", nameof(value));
        return value;
    }
}
