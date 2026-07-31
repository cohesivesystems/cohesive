using System.Globalization;
using System.Text;

namespace Cohesive.Adapters.Elastic;

/// <summary>Explicit bounded operating policy for one Elasticsearch materialization target.</summary>
/// <remarks>
/// These limits are deployment policy, not claims inferred from Elasticsearch defaults. Capability projection
/// advertises the effective values, and every target operation enforces the same item, byte, and concurrency
/// boundaries. Batch item and canonical-byte limits are enforced before bulk mutation I/O, after bounded durable
/// identity lookup so an exact admitted operation remains replayable if deployment policy is later tightened. A
/// deployment may tighten the conventional values but must not configure values larger than its cluster, proxy, or
/// client can actually preserve.
/// </remarks>
public sealed record ElasticMaterializationTargetPolicy
{
    /// <summary>Conventional bounded Elasticsearch target policy.</summary>
    public static ElasticMaterializationTargetPolicy Default { get; } = new(
        maximumBatchItems: 1_000,
        maximumBatchBytes: 5 * 1024 * 1024,
        maximumParallelism: 8,
        maximumDiagnosticBytes: 64 * 1024);

    /// <summary>Creates explicit Elasticsearch target operating bounds.</summary>
    /// <param name="maximumBatchItems">Maximum mutations accepted by one bulk request.</param>
    /// <param name="maximumBatchBytes">
    /// Maximum canonical materialization request bytes accepted before provider-specific bulk framing.
    /// </param>
    /// <param name="maximumParallelism">Maximum independently admitted target operations.</param>
    /// <param name="maximumDiagnosticBytes">
    /// Maximum provider error bytes retained for sanitized diagnostics from one operation.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A bound is not positive or a byte bound exceeds the largest supported CLR array.
    /// </exception>
    public ElasticMaterializationTargetPolicy(
        int maximumBatchItems,
        long maximumBatchBytes,
        int maximumParallelism,
        int maximumDiagnosticBytes)
    {
        if (maximumBatchItems <= 0 || maximumBatchItems >= Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBatchItems),
                maximumBatchItems,
                $"An Elasticsearch materialization batch must contain from 1 through {Array.MaxLength - 1} items.");
        }

        if (maximumBatchBytes <= 0 || maximumBatchBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBatchBytes),
                maximumBatchBytes,
                $"An Elasticsearch materialization batch must contain from 1 through {Array.MaxLength} canonical bytes.");
        }

        if (maximumParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumParallelism),
                maximumParallelism,
                "Elasticsearch materialization parallelism must be positive.");
        }

        if (maximumDiagnosticBytes <= 0 || maximumDiagnosticBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDiagnosticBytes),
                maximumDiagnosticBytes,
                $"Retained Elasticsearch diagnostic bytes must be from 1 through {Array.MaxLength} bytes.");
        }

        MaximumBatchItems = maximumBatchItems;
        MaximumBatchBytes = maximumBatchBytes;
        MaximumParallelism = maximumParallelism;
        MaximumDiagnosticBytes = maximumDiagnosticBytes;
    }

    /// <summary>Gets the maximum mutations accepted by one bulk request.</summary>
    public int MaximumBatchItems { get; }

    /// <summary>Gets the maximum canonical materialization request bytes accepted by one bulk operation.</summary>
    public long MaximumBatchBytes { get; }

    /// <summary>Gets the maximum independently admitted target operations.</summary>
    public int MaximumParallelism { get; }

    /// <summary>Gets the maximum provider error bytes retained in sanitized diagnostics.</summary>
    public int MaximumDiagnosticBytes { get; }

    internal void AppendCanonical(StringBuilder canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        Append(canonical, MaximumBatchItems);
        Append(canonical, MaximumBatchBytes);
        Append(canonical, MaximumParallelism);
        Append(canonical, MaximumDiagnosticBytes);
    }

    static void Append(StringBuilder canonical, long value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        canonical.Append(text.Length.ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(text);
        canonical.Append(';');
    }
}
