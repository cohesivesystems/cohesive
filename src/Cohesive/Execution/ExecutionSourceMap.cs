using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Normalized, non-semantic mappings from canonical execution paths to producer source constructs.
/// </summary>
/// <remarks>
/// Source maps are durable attribution evidence, but they are not part of execution-definition
/// semantic fingerprints. Multiple source constructs may map to the same semantic path. Exact
/// duplicate mappings are rejected and entries are retained in deterministic ordinal order.
/// </remarks>
public sealed record ExecutionSourceMap
{
    /// <summary>Empty execution source map.</summary>
    public static ExecutionSourceMap Empty { get; } = new([]);

    /// <summary>Creates a normalized execution source map.</summary>
    /// <param name="entries">
    /// Source references whose semantic paths identify the canonical constructs they produced.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="entries"/> contains a <see langword="null"/> entry, an entry without a
    /// semantic path, or an exact duplicate mapping.
    /// </exception>
    [JsonConstructor]
    public ExecutionSourceMap(ImmutableArray<ExecutionSourceProvenance> entries)
    {
        if (entries.IsDefaultOrEmpty)
        {
            Entries = [];
            return;
        }

        HashSet<ExecutionSourceProvenance> observed = [];
        foreach (var entry in entries)
        {
            if (entry is null)
                throw new ArgumentException("An execution source map cannot contain null entries.", nameof(entries));
            if (entry.SemanticPath is null)
            {
                throw new ArgumentException(
                    "Every execution source-map entry requires a canonical semantic path.",
                    nameof(entries));
            }
            if (!observed.Add(entry))
            {
                throw new ArgumentException(
                    $"Execution source mapping '{entry.SemanticPath}' to '{entry.Reference}' is duplicated.",
                    nameof(entries));
            }

        }

        Entries = CanonicalDocumentCollections.SortIfNeeded(entries, static (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(
                left.SemanticPath!.Value.ToString(),
                right.SemanticPath!.Value.ToString());
            if (comparison != 0)
                return comparison;

            comparison = StringComparer.Ordinal.Compare(left.Reference, right.Reference);
            if (comparison != 0)
                return comparison;

            return StringComparer.Ordinal.Compare(left.Description, right.Description);
        });
    }

    /// <summary>Source mappings in deterministic semantic-path and source-reference order.</summary>
    public ImmutableArray<ExecutionSourceProvenance> Entries { get; }

    /// <summary>
    /// Resolves the deepest mapped semantic-path prefix of one canonical diagnostic location.
    /// </summary>
    /// <remarks>
    /// Source-map paths are relative to the canonical definition payload. A leading
    /// <c>/definition</c> envelope segment is therefore removed from <paramref name="location"/> before matching.
    /// JSON Pointer escapes are decoded before structural segment comparison, so source paths containing
    /// <c>/</c> or <c>~</c> remain distinct from paths containing additional segments. When multiple mappings
    /// share the deepest matching path, their references are returned once each in deterministic ordinal order.
    /// A null, root, malformed, or unmapped location resolves to the required fallback reference.
    /// </remarks>
    /// <param name="location">
    /// Canonical JSON Pointer location, either definition-relative or prefixed by the shared
    /// <c>/definition</c> envelope segment; or <see langword="null"/> when no location is available.
    /// </param>
    /// <param name="fallbackReference">Required source reference used when no source-map entry matches.</param>
    /// <returns>
    /// The distinct references at the deepest matching semantic path in ordinal order, or a singleton containing
    /// <paramref name="fallbackReference"/> when no mapping matches.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="fallbackReference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="fallbackReference"/> is empty or consists only of white-space characters.
    /// </exception>
    public ImmutableArray<string> ResolveReferences(string? location, string fallbackReference)
    {
        fallbackReference = Guard.RequireNotNullOrWhiteSpace(fallbackReference);
        if (Entries.IsDefaultOrEmpty)
            return [fallbackReference];

        var locationSegments = ParseLocation(location);
        if (locationSegments.IsDefaultOrEmpty)
            return [fallbackReference];

        var deepest = -1;
        HashSet<string>? references = null;
        foreach (var entry in Entries)
        {
            var mappedSegments = entry.SemanticPath!.Value.Segments;
            if (mappedSegments.Length < deepest || !IsPrefix(mappedSegments, locationSegments))
                continue;

            if (mappedSegments.Length > deepest)
            {
                deepest = mappedSegments.Length;
                references = new(StringComparer.Ordinal);
            }

            references!.Add(entry.Reference);
        }

        return references is null
            ? [fallbackReference]
            : [.. references.Order(StringComparer.Ordinal)];
    }

    /// <summary>Attaches deterministic source references for one canonical diagnostic location.</summary>
    /// <param name="diagnostic">Diagnostic whose existing evidence is preserved.</param>
    /// <param name="fallbackReference">Required source reference used when no source-map entry matches.</param>
    /// <param name="defaultStage">Stage used only when the diagnostic has no existing evidence stage.</param>
    /// <returns>
    /// A diagnostic with existing and resolved source references merged, de-duplicated, and sorted ordinally.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="diagnostic"/>, <paramref name="fallbackReference"/>, or
    /// <paramref name="defaultStage"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="fallbackReference"/> or <paramref name="defaultStage"/> is empty or white space.
    /// </exception>
    public DocumentValidationDiagnostic WithResolvedSourceReferences(
        DocumentValidationDiagnostic diagnostic,
        string fallbackReference,
        string defaultStage)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        defaultStage = Guard.RequireNotNullOrWhiteSpace(defaultStage);
        var evidence = diagnostic.Evidence;
        var resolved = ResolveReferences(diagnostic.Location, fallbackReference);
        var sourceReferences = evidence is null || evidence.SourceReferences.IsDefaultOrEmpty
            ? resolved
            : [
                .. evidence.SourceReferences
                    .Concat(resolved)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            ];
        return diagnostic with
        {
            Evidence = new(
                stage: evidence?.Stage ?? defaultStage,
                subject: evidence?.Subject,
                relatedLocations: evidence?.RelatedLocations ?? [],
                sourceReferences: sourceReferences,
                resolutionOptions: evidence?.ResolutionOptions ?? [],
                expected: evidence?.Expected,
                observed: evidence?.Observed)
        };
    }

    /// <summary>Compares source maps by their normalized mappings.</summary>
    /// <param name="other">Source map to compare with this value.</param>
    /// <returns>
    /// <see langword="true"/> when both maps contain the same normalized entries; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public bool Equals(ExecutionSourceMap? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || Entries.Length != other.Entries.Length)
            return false;

        for (var index = 0; index < Entries.Length; index++)
        {
            if (Entries[index] != other.Entries[index])
                return false;
        }

        return true;
    }

    /// <summary>Returns a structural hash code for the normalized source mappings.</summary>
    /// <returns>A hash code derived from every normalized mapping.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in Entries)
            hash.Add(entry);
        return hash.ToHashCode();
    }

    static ImmutableArray<string> ParseLocation(string? location)
    {
        if (string.IsNullOrEmpty(location) || location[0] != '/')
            return [];

        var encodedSegments = location.Split('/', StringSplitOptions.None);
        var decodedSegments = ImmutableArray.CreateBuilder<string>(encodedSegments.Length - 1);
        for (var index = 1; index < encodedSegments.Length; index++)
        {
            if (!TryDecodePointerSegment(encodedSegments[index], out var decoded))
                return [];

            decodedSegments.Add(decoded);
        }

        if (decodedSegments.Count > 0
            && string.Equals(decodedSegments[0], "definition", StringComparison.Ordinal))
        {
            decodedSegments.RemoveAt(0);
        }

        return decodedSegments.Count == decodedSegments.Capacity
            ? decodedSegments.MoveToImmutable()
            : decodedSegments.ToImmutable();
    }

    static bool TryDecodePointerSegment(string encoded, out string decoded)
    {
        var escapeIndex = encoded.IndexOf('~', StringComparison.Ordinal);
        if (escapeIndex < 0)
        {
            decoded = encoded;
            return true;
        }

        var builder = new StringBuilder(encoded.Length);
        builder.Append(encoded, startIndex: 0, count: escapeIndex);
        for (var index = escapeIndex; index < encoded.Length; index++)
        {
            var character = encoded[index];
            if (character != '~')
            {
                builder.Append(character);
                continue;
            }

            if (++index >= encoded.Length)
            {
                decoded = string.Empty;
                return false;
            }

            switch (encoded[index])
            {
                case '0':
                    builder.Append('~');
                    break;
                case '1':
                    builder.Append('/');
                    break;
                default:
                    decoded = string.Empty;
                    return false;
            }
        }

        decoded = builder.ToString();
        return true;
    }

    static bool IsPrefix(
        ImmutableArray<string> prefix,
        ImmutableArray<string> path)
    {
        if (prefix.Length > path.Length)
            return false;

        for (var index = 0; index < prefix.Length; index++)
        {
            if (!string.Equals(prefix[index], path[index], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
