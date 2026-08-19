using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;

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

/// <summary>Logical identity and physical selector for one partition selected by a PostgreSQL source reader.</summary>
public sealed record PostgresRelationQueryPartitionScope
{
    /// <summary>Creates an exact fixed-partition scope.</summary>
    /// <param name="logicalPartition">Provider-neutral identity shared by all sources in this logical partition.</param>
    /// <param name="sourceSelector">Canonical placement selector identifying the partition coordinate.</param>
    /// <param name="canonicalValue">
    /// Canonical scalar text interpreted according to each table's partition binding. The value is retained only by
    /// the runtime policy; diagnostics and capability evidence expose a digest instead.
    /// </param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    public PostgresRelationQueryPartitionScope(
        RelationQueryLogicalPartitionIdentity logicalPartition,
        string sourceSelector,
        string canonicalValue)
    {
        LogicalPartition = Guard.RequireNotNull(logicalPartition);
        SourceSelector = Guard.RequireNotNullOrWhiteSpace(sourceSelector);
        CanonicalValue = Guard.RequireNotNullOrWhiteSpace(canonicalValue);
    }

    /// <summary>Provider-neutral identity shared by all sources in this logical partition.</summary>
    public RelationQueryLogicalPartitionIdentity LogicalPartition { get; }

    /// <summary>Canonical placement selector identifying the partition coordinate.</summary>
    public string SourceSelector { get; }

    /// <summary>Canonical scalar value supplied as the PostgreSQL equality predicate parameter.</summary>
    public string CanonicalValue { get; }

    internal string ComputeDigest(PostgresRelationQueryPartitionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var canonical = string.Concat(
            "cohesive.adapters.postgres/partition-scope/v1\0",
            SourceSelector, "\0",
            binding.SemanticPath.ToString(), "\0",
            binding.ColumnName, "\0",
            ((int)binding.ScalarType).ToString(System.Globalization.CultureInfo.InvariantCulture), "\0",
            CanonicalValue);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

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
        maximumKeyBytes: 256,
        partitionScope: null);

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
    /// <param name="partitionScope">
    /// Optional provider-neutral logical partition and fixed physical selector applied by parameterized equality to
    /// every acquired table. When supplied, every placement served by the reader must declare the same selector and
    /// every table must bind it physically.
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
        int maximumKeyBytes = 256,
        PostgresRelationQueryPartitionScope? partitionScope = null)
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
        PartitionScope = partitionScope;
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

    /// <summary>Exact fixed logical partition applied to every source read, or <see langword="null"/>.</summary>
    public PostgresRelationQueryPartitionScope? PartitionScope { get; }
}
