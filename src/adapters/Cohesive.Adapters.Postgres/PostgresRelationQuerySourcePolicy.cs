namespace Cohesive.Adapters.Postgres;

/// <summary>Caller-attested Npgsql process configuration for exact PostgreSQL temporal acquisition.</summary>
public enum PostgresNpgsqlTemporalSemantics
{
    /// <summary>Temporal source acquisition is not authorized.</summary>
    Unsupported = 0,

    /// <summary>
    /// The caller set <c>Npgsql.DisableDateTimeInfinityConversions</c> before every Npgsql operation in the process.
    /// </summary>
    InfinityConversionsDisabledBeforeInitialization = 1
}

/// <summary>Explicit physical operating bounds for PostgreSQL Relations acquisition and materialization paging.</summary>
public sealed record PostgresRelationQuerySourcePolicy
{
    /// <summary>Conventional bounded PostgreSQL source policy.</summary>
    public static PostgresRelationQuerySourcePolicy Default { get; } = new(
        maximumBatchKeys: 1_000,
        maximumRowsPerRead: 10_000,
        maximumPageItems: PostgresRelationQueryTargetProfile.MaximumPageSize,
        maximumPageBytes: 16 * 1024 * 1024,
        temporalSemantics: PostgresNpgsqlTemporalSemantics.Unsupported,
        maximumKeyBytes: 256);

    /// <summary>Creates explicit source operating bounds.</summary>
    /// <param name="maximumBatchKeys">Maximum typed keys accepted by one set-oriented command.</param>
    /// <param name="maximumRowsPerRead">Maximum observations retained by one Relations read.</param>
    /// <param name="maximumPageItems">Maximum observations returned by one materialization page.</param>
    /// <param name="maximumPageBytes">
    /// Maximum provider payload bytes retained by one Npgsql command and maximum canonical encoded bytes returned by
    /// one materialization page.
    /// </param>
    /// <param name="temporalSemantics">
    /// Explicit caller evidence for the process-wide Npgsql temporal mode. The default does not authorize temporal
    /// source acquisition.
    /// </param>
    /// <param name="maximumKeyBytes">
    /// Maximum canonical UTF-8 bytes in one identity or relationship key. The conventional 256-byte bound keeps
    /// batched requests and durable continuation state physically bounded.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A bound is not positive, exceeds the supported CLR range, page items exceed rows per read or the canonical
    /// PostgreSQL page-size limit, or
    /// <paramref name="temporalSemantics"/> is unsupported.
    /// </exception>
    public PostgresRelationQuerySourcePolicy(
        int maximumBatchKeys,
        int maximumRowsPerRead,
        int maximumPageItems,
        long maximumPageBytes,
        PostgresNpgsqlTemporalSemantics temporalSemantics = PostgresNpgsqlTemporalSemantics.Unsupported,
        int maximumKeyBytes = 256)
    {
        if (maximumBatchKeys <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBatchKeys), maximumBatchKeys, "A PostgreSQL key batch must be positive.");
        if (maximumRowsPerRead <= 0 || maximumRowsPerRead >= Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRowsPerRead),
                maximumRowsPerRead,
                $"A PostgreSQL source read must be from 1 through {Array.MaxLength - 1} rows so one probe row remains representable.");
        }
        if (maximumPageItems <= 0
            || maximumPageItems > maximumRowsPerRead
            || maximumPageItems > PostgresRelationQueryTargetProfile.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPageItems),
                maximumPageItems,
                $"A materialization page must be positive and no larger than both the source-read row bound and the canonical PostgreSQL limit of {PostgresRelationQueryTargetProfile.MaximumPageSize} items.");
        }
        if (maximumPageBytes <= 0 || maximumPageBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPageBytes),
                maximumPageBytes,
                $"A provider result/materialization page byte bound must be from 1 through {Array.MaxLength} bytes.");
        }
        if (maximumKeyBytes <= 0 || maximumKeyBytes > maximumPageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumKeyBytes),
                maximumKeyBytes,
                "A PostgreSQL key byte bound must be positive and no larger than the provider result/page byte bound.");
        }
        if (!Enum.IsDefined(temporalSemantics))
        {
            throw new ArgumentOutOfRangeException(
                nameof(temporalSemantics),
                temporalSemantics,
                "The Npgsql temporal semantics are unsupported.");
        }

        MaximumBatchKeys = maximumBatchKeys;
        MaximumRowsPerRead = maximumRowsPerRead;
        MaximumPageItems = maximumPageItems;
        MaximumPageBytes = maximumPageBytes;
        MaximumKeyBytes = maximumKeyBytes;
        TemporalSemantics = temporalSemantics;
    }

    /// <summary>Maximum typed keys accepted by one set-oriented command.</summary>
    public int MaximumBatchKeys { get; }

    /// <summary>Maximum observations retained by one Relations read.</summary>
    public int MaximumRowsPerRead { get; }

    /// <summary>Maximum observations returned by one materialization page.</summary>
    public int MaximumPageItems { get; }

    /// <summary>
    /// Maximum provider payload bytes retained by one Npgsql command and canonical encoded bytes returned by one
    /// materialization page.
    /// </summary>
    public long MaximumPageBytes { get; }

    /// <summary>Maximum canonical UTF-8 bytes in one identity or relationship key.</summary>
    public int MaximumKeyBytes { get; }

    /// <summary>Caller-attested process-wide Npgsql temporal mode.</summary>
    public PostgresNpgsqlTemporalSemantics TemporalSemantics { get; }
}
