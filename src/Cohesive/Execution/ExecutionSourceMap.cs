using System.Collections.Immutable;
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
}
