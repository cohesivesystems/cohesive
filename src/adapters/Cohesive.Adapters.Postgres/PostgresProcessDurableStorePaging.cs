using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Processes;

namespace Cohesive.Adapters.Postgres;

/// <summary>Deterministic canonical aggregate paging owned by the PostgreSQL Process-store adapter.</summary>
internal static class PostgresProcessDurableStorePaging
{
    internal const string Format = "cohesive-postgres-process-pages/content-defined-sha256-v1";
    const string FingerprintPrefix = "sha256-v1:";
    static readonly ulong[] Gear = CreateGear();
    static readonly JsonSerializerOptions JsonOptions = ProcessDurableCheckpointJsonSerializer.CreateOptions();

    internal static PostgresProcessDurablePagedAggregate Page(
        ProcessDurableAggregateDocument aggregate,
        PostgresProcessDurableStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentNullException.ThrowIfNull(options);
        var content = StrictDocumentJson.GetCanonicalBytes(aggregate, JsonOptions);
        RequireAggregateBound(content.Length, options);
        var pages = ImmutableArray.CreateBuilder<PostgresProcessDurablePage>();
        var offset = 0;
        while (offset < content.Length)
        {
            var length = NextPageLength(content, offset, options);
            var pageContent = ImmutableArray.Create(content.AsSpan(offset, length).ToArray());
            pages.Add(new(
                Fingerprint: Fingerprint(pageContent.AsSpan()),
                Content: pageContent));
            offset = checked(offset + length);
        }

        var retained = pages.ToImmutable();
        return new(
            AggregateFingerprint: Fingerprint(content),
            AggregateBytes: content.Length,
            Manifest: string.Join('\n', retained.Select(static page => page.Fingerprint)),
            Pages: retained);
    }

    internal static ProcessDurableAggregateDocument Reconstruct(
        string aggregateFingerprint,
        long aggregateBytes,
        string manifest,
        IReadOnlyDictionary<string, ImmutableArray<byte>> pages,
        PostgresProcessDurableStoreOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateFingerprint);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);
        if (aggregateBytes <= 0 || aggregateBytes > int.MaxValue)
        {
            throw new InvalidDataException(
                $"The PostgreSQL Process aggregate declares unsupported byte length {aggregateBytes}.");
        }
        RequireAggregateBound(aggregateBytes, options);
        var fingerprints = ParseManifest(manifest);
        var content = GC.AllocateUninitializedArray<byte>((int)aggregateBytes);
        var offset = 0;
        foreach (var fingerprint in fingerprints)
        {
            if (!pages.TryGetValue(fingerprint, out var page) || page.IsDefaultOrEmpty)
            {
                throw new InvalidDataException(
                    $"The PostgreSQL Process aggregate page '{fingerprint}' is absent or empty.");
            }
            if (page.Length > options.MaximumPageBytes)
            {
                throw new InvalidDataException(
                    $"The PostgreSQL Process aggregate page '{fingerprint}' exceeds the configured page bound.");
            }
            if (!string.Equals(Fingerprint(page.AsSpan()), fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The PostgreSQL Process aggregate page '{fingerprint}' does not match its content.");
            }
            if (page.Length > content.Length - offset)
            {
                throw new InvalidDataException(
                    "The PostgreSQL Process aggregate page manifest exceeds its declared byte length.");
            }
            page.AsSpan().CopyTo(content.AsSpan(offset));
            offset += page.Length;
        }
        if (offset != content.Length)
        {
            throw new InvalidDataException(
                "The PostgreSQL Process aggregate pages do not fill the declared byte length.");
        }
        if (!string.Equals(Fingerprint(content), aggregateFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The PostgreSQL Process aggregate fingerprint does not match its reconstructed canonical content.");
        }

        ProcessDurableAggregateDocument aggregate;
        try
        {
            aggregate = JsonSerializer.Deserialize<ProcessDurableAggregateDocument>(content, JsonOptions)
                ?? throw new JsonException("A PostgreSQL Process aggregate cannot deserialize to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The PostgreSQL Process aggregate is not a valid canonical durable-store aggregate.",
                exception);
        }
        var canonical = StrictDocumentJson.GetCanonicalBytes(aggregate, JsonOptions);
        if (!canonical.AsSpan().SequenceEqual(content))
        {
            throw new InvalidDataException(
                "The PostgreSQL Process aggregate differs from its unique canonical JSON encoding.");
        }
        return aggregate;
    }

    internal static ImmutableArray<string> ParseManifest(string manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Length == 0)
            throw new InvalidDataException("A PostgreSQL Process aggregate page manifest cannot be empty.");
        var values = manifest.Split('\n', StringSplitOptions.None);
        var fingerprints = ImmutableArray.CreateBuilder<string>(values.Length);
        foreach (var value in values)
        {
            if (!IsFingerprint(value))
            {
                throw new InvalidDataException(
                    $"The PostgreSQL Process aggregate page manifest contains invalid fingerprint '{value}'.");
            }
            fingerprints.Add(value);
        }
        return fingerprints.MoveToImmutable();
    }

    internal static string Fingerprint(ReadOnlySpan<byte> content) =>
        $"{FingerprintPrefix}{Convert.ToHexStringLower(SHA256.HashData(content))}";

    static int NextPageLength(
        ReadOnlySpan<byte> content,
        int offset,
        PostgresProcessDurableStoreOptions options)
    {
        var remaining = content.Length - offset;
        if (remaining <= options.MaximumPageBytes)
            return remaining;

        var limit = offset + options.MaximumPageBytes;
        var cursor = offset;
        ulong hash = 0;
        while (cursor < offset + options.MinimumPageBytes)
        {
            hash = Roll(hash, content[cursor]);
            cursor++;
        }
        var mask = (ulong)(options.TargetPageBytes - 1);
        while (cursor < limit)
        {
            hash = Roll(hash, content[cursor]);
            cursor++;
            if ((hash & mask) == 0)
                break;
        }
        return cursor - offset;
    }

    static ulong Roll(ulong hash, byte value) => unchecked((hash << 1) + Gear[value]);

    static bool IsFingerprint(string value)
    {
        if (!value.StartsWith(FingerprintPrefix, StringComparison.Ordinal)
            || value.Length != FingerprintPrefix.Length + 64)
        {
            return false;
        }
        foreach (var character in value.AsSpan(FingerprintPrefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    static void RequireAggregateBound(long byteCount, PostgresProcessDurableStoreOptions options)
    {
        if (options.MaximumAggregateBytes is { } maximum && byteCount > maximum)
        {
            throw new InvalidOperationException(
                $"The Process durable-store aggregate requires {byteCount} UTF-8 bytes, exceeding the configured reconstruction maximum of {maximum} bytes.");
        }
    }

    static ulong[] CreateGear()
    {
        var values = new ulong[256];
        for (var index = 0; index < values.Length; index++)
        {
            var value = unchecked((ulong)index + 0x9e3779b97f4a7c15UL);
            value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111ebUL);
            values[index] = value ^ (value >> 31);
        }
        return values;
    }
}

internal sealed record PostgresProcessDurablePage(
    string Fingerprint,
    ImmutableArray<byte> Content);

internal sealed record PostgresProcessDurablePagedAggregate(
    string AggregateFingerprint,
    int AggregateBytes,
    string Manifest,
    ImmutableArray<PostgresProcessDurablePage> Pages);
