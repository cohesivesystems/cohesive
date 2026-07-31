using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Portable canonical ordering for materialized item identities.</summary>
/// <remarks>
/// Unicode scalar-value order is equivalent to lexicographic UTF-8 byte order and therefore matches keyword sorting
/// in targets such as Elasticsearch, including across the basic multilingual and supplementary planes.
/// </remarks>
public static class MaterializationSealContentOrder
{
    /// <summary>Comparer implementing canonical Unicode scalar-value order.</summary>
    public static IComparer<MaterializationItemId> Comparer { get; } = new ItemIdComparer();

    /// <summary>Compares two item identities in canonical Unicode scalar-value order.</summary>
    /// <param name="left">First defined item identity.</param>
    /// <param name="right">Second defined item identity.</param>
    /// <returns>A negative, zero, or positive value when <paramref name="left"/> precedes, equals, or follows <paramref name="right"/>.</returns>
    /// <exception cref="ArgumentException">Either identity is default.</exception>
    public static int Compare(MaterializationItemId left, MaterializationItemId right)
    {
        MaterializationContract.RequireDefinedIdentity(left.Value, nameof(left));
        MaterializationContract.RequireDefinedIdentity(right.Value, nameof(right));
        var leftRemaining = left.Value.AsSpan();
        var rightRemaining = right.Value.AsSpan();
        while (!leftRemaining.IsEmpty && !rightRemaining.IsEmpty)
        {
            _ = Rune.DecodeFromUtf16(leftRemaining, out var leftRune, out var leftConsumed);
            _ = Rune.DecodeFromUtf16(rightRemaining, out var rightRune, out var rightConsumed);
            var comparison = leftRune.Value.CompareTo(rightRune.Value);
            if (comparison != 0)
            {
                return comparison;
            }
            leftRemaining = leftRemaining[leftConsumed..];
            rightRemaining = rightRemaining[rightConsumed..];
        }
        return leftRemaining.IsEmpty ? (rightRemaining.IsEmpty ? 0 : -1) : 1;
    }

    sealed class ItemIdComparer : IComparer<MaterializationItemId>
    {
        public int Compare(MaterializationItemId left, MaterializationItemId right) =>
            MaterializationSealContentOrder.Compare(left, right);
    }
}

/// <summary>Canonical immutable item evidence included in a generation seal fingerprint.</summary>
public sealed record MaterializationSealContentEntry
{
    /// <summary>Creates one canonical seal-content entry.</summary>
    /// <param name="itemId">Stable logical item identity.</param>
    /// <param name="version">Retained monotonic item version.</param>
    /// <param name="mutationId">Mutation identity that established the retained version.</param>
    /// <param name="kind">Whether the retained version is an upsert or delete tombstone.</param>
    /// <param name="value">Retained upsert value, or <see langword="null"/> for a delete tombstone.</param>
    /// <exception cref="ArgumentException">
    /// An identity or version is default, an upsert has no value or has an undefined value, or a delete carries a
    /// value.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public MaterializationSealContentEntry(
        MaterializationItemId itemId,
        MaterializationItemVersion version,
        MaterializationItemMutationId mutationId,
        MaterializationItemMutationKind kind,
        ObservationValue? value)
    {
        MaterializationContract.RequireDefinedIdentity(itemId.Value, nameof(itemId));
        MaterializationContract.RequireDefinedIdentity(version.Value, nameof(version));
        MaterializationContract.RequireDefinedIdentity(mutationId.Value, nameof(mutationId));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported seal-content mutation kind.");
        }

        if (kind == MaterializationItemMutationKind.Upsert
            && (value is not { } upsert || upsert.Kind == ObservationValueKind.Undefined))
        {
            throw new ArgumentException("A sealed upsert requires one defined portable value.", nameof(value));
        }

        if (kind == MaterializationItemMutationKind.Delete && value is not null)
        {
            throw new ArgumentException("A sealed delete tombstone cannot carry a value.", nameof(value));
        }

        ItemId = itemId;
        Version = version;
        MutationId = mutationId;
        Kind = kind;
        Value = value;
    }

    /// <summary>Gets the stable logical item identity.</summary>
    public MaterializationItemId ItemId { get; }

    /// <summary>Gets the retained monotonic item version.</summary>
    public MaterializationItemVersion Version { get; }

    /// <summary>Gets the mutation identity that established the retained version.</summary>
    public MaterializationItemMutationId MutationId { get; }

    /// <summary>Gets whether the retained version is an upsert or delete tombstone.</summary>
    public MaterializationItemMutationKind Kind { get; }

    /// <summary>Gets the retained upsert value, or <see langword="null"/> for a delete tombstone.</summary>
    public ObservationValue? Value { get; }

    /// <summary>Projects one canonical item mutation into seal-content evidence.</summary>
    /// <param name="mutation">Mutation whose complete retained evidence should be represented.</param>
    /// <returns>A canonical seal-content entry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mutation"/> is <see langword="null"/>.</exception>
    public static MaterializationSealContentEntry From(MaterializationItemMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return new(
            mutation.ItemId,
            mutation.Version,
            mutation.MutationId,
            mutation.Kind,
            mutation is MaterializationUpsert upsert ? upsert.Value : null);
    }
}

/// <summary>Computes deterministic immutable generation-content fingerprints shared by target adapters.</summary>
public static class MaterializationSealFingerprinter
{
    /// <summary>Digest algorithm used by generation seal fingerprints.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile used by generation seal fingerprints.</summary>
    public const string Canonicalization = "cohesive-materialization-seal-content/v1-c14n/v1";

    /// <summary>Computes a fingerprint over the complete retained item set in canonical Unicode scalar-value order.</summary>
    /// <param name="entries">Complete retained upsert and tombstone evidence; declaration order is not significant.</param>
    /// <returns>A deterministic generation seal fingerprint.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entries"/> contains a <see langword="null"/> entry or repeats an item identity.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">The content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">A content value has no configured JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The content has no canonical JSON representation.</exception>
    public static MaterializationSealFingerprint Compute(
        ImmutableArray<MaterializationSealContentEntry> entries)
    {
        var normalized = entries.IsDefault ? [] : entries;
        var canonicalOrder = true;
        for (var index = 0; index < normalized.Length; index++)
        {
            if (normalized[index] is null)
            {
                throw new ArgumentException("Seal content cannot contain a null entry.", nameof(entries));
            }
            if (index > 0)
            {
                var comparison = MaterializationSealContentOrder.Compare(
                    normalized[index - 1].ItemId,
                    normalized[index].ItemId);
                if (comparison == 0)
                {
                    throw new ArgumentException("Seal content cannot repeat a logical item identity.", nameof(entries));
                }
                canonicalOrder &= comparison < 0;
            }
        }

        if (!canonicalOrder)
        {
            normalized = [.. normalized.OrderBy(
                static entry => entry.ItemId,
                MaterializationSealContentOrder.Comparer)];
            for (var index = 1; index < normalized.Length; index++)
            {
                if (normalized[index - 1].ItemId == normalized[index].ItemId)
                {
                    throw new ArgumentException("Seal content cannot repeat a logical item identity.", nameof(entries));
                }
            }
        }

        using MaterializationSealFingerprintAccumulator accumulator = new();
        foreach (var entry in normalized)
        {
            accumulator.Append(entry);
        }
        return accumulator.Complete();
    }
}

/// <summary>Incrementally computes one canonical generation seal without retaining the complete item set.</summary>
/// <remarks>
/// Entries must be appended in strictly increasing Unicode scalar-value <see cref="MaterializationSealContentEntry.ItemId"/>
/// order. This is the streaming counterpart of <see cref="MaterializationSealFingerprinter.Compute"/> and emits the
/// exact same canonical fingerprint while keeping memory bounded by one serialized entry.
/// </remarks>
public sealed class MaterializationSealFingerprintAccumulator : IDisposable
{
    static readonly System.Text.Json.JsonSerializerOptions FingerprintOptions = StrictDocumentJson.CreateOptions();
    static readonly byte[] Prefix = "{\"items\":["u8.ToArray();
    static readonly byte[] Suffix = "]}"u8.ToArray();
    static readonly byte[] Separator = ","u8.ToArray();

    readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    MaterializationItemId? lastItemId;
    bool hasEntries;
    bool completed;
    bool disposed;

    /// <summary>Creates an empty canonical seal fingerprint accumulator.</summary>
    public MaterializationSealFingerprintAccumulator() => hash.AppendData(Prefix);

    /// <summary>Appends one canonical item in strictly increasing Unicode scalar-value item-identity order.</summary>
    /// <param name="entry">Next complete retained item or tombstone evidence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="entry"/> repeats or precedes the previously appended item identity.
    /// </exception>
    /// <exception cref="InvalidOperationException"><see cref="Complete"/> has already been called.</exception>
    /// <exception cref="ObjectDisposedException">The accumulator has been disposed.</exception>
    /// <exception cref="System.Text.Json.JsonException">The entry cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">An entry value has no configured JSON representation.</exception>
    public void Append(MaterializationSealContentEntry entry)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        if (completed)
        {
            throw new InvalidOperationException("A completed materialization seal fingerprint cannot accept more entries.");
        }
        if (lastItemId is { } last
            && MaterializationSealContentOrder.Compare(last, entry.ItemId) >= 0)
        {
            throw new ArgumentException(
                "Streaming seal content must use strictly increasing Unicode scalar-value item identities.",
                nameof(entry));
        }

        if (hasEntries)
        {
            hash.AppendData(Separator);
        }
        var canonicalEntry = StrictDocumentJson.GetCanonicalBytes(entry, FingerprintOptions);
        hash.AppendData(canonicalEntry);
        lastItemId = entry.ItemId;
        hasEntries = true;
    }

    /// <summary>Completes the canonical document and returns its deterministic seal fingerprint.</summary>
    /// <returns>The same fingerprint produced by the batch fingerprinter for the appended ordered entries.</returns>
    /// <exception cref="InvalidOperationException">This method has already been called.</exception>
    /// <exception cref="ObjectDisposedException">The accumulator has been disposed.</exception>
    public MaterializationSealFingerprint Complete()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed)
        {
            throw new InvalidOperationException("A materialization seal fingerprint can be completed only once.");
        }

        hash.AppendData(Suffix);
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        completed = true;
        return new($"sha256-v1:{digest}");
    }

    /// <summary>Releases the incremental hashing state.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        hash.Dispose();
        disposed = true;
    }
}
