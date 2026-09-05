using Cohesive.Adapters.Sql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Physical PostgreSQL binding and deterministic paging policy for one Process durability authority.</summary>
public sealed record PostgresProcessDurableStoreOptions
{
    /// <summary>Default PostgreSQL schema for Process durable-store documents.</summary>
    public const string DefaultSchema = "cohesive";

    /// <summary>Default legacy table containing first-generation atomic Process durable-store documents.</summary>
    public const string DefaultTable = "process_durable_stores";

    /// <summary>Default minimum UTF-8 bytes of one content-defined aggregate page.</summary>
    public const int DefaultMinimumPageBytes = 16 * 1024;

    /// <summary>Default target UTF-8 bytes of one content-defined aggregate page.</summary>
    public const int DefaultTargetPageBytes = 32 * 1024;

    /// <summary>Default maximum UTF-8 bytes of one content-defined aggregate page.</summary>
    public const int DefaultMaximumPageBytes = 64 * 1024;

    /// <summary>Creates a physical PostgreSQL Process durability binding.</summary>
    /// <param name="authorityId">Stable row identity of one independent Process durability authority.</param>
    /// <param name="schema">Validated PostgreSQL schema identifier.</param>
    /// <param name="table">Validated legacy PostgreSQL table identifier used for compatible migration.</param>
    /// <param name="minimumPageBytes">Positive minimum content-defined page size.</param>
    /// <param name="targetPageBytes">Power-of-two target content-defined page size.</param>
    /// <param name="maximumPageBytes">Maximum content-defined page size, no smaller than the target.</param>
    /// <param name="maximumAggregateBytes">
    /// Optional positive bound on one reconstructed canonical aggregate; <see langword="null"/> leaves the
    /// provider without an artificial aggregate-size limit while every physical page remains bounded.
    /// </param>
    /// <exception cref="ArgumentNullException">A string argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authorityId"/> is empty or a SQL identifier is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive or the page-size ordering is invalid.</exception>
    /// <exception cref="ArgumentException"><paramref name="targetPageBytes"/> is not a power of two.</exception>
    public PostgresProcessDurableStoreOptions(
        string authorityId,
        string schema = DefaultSchema,
        string table = DefaultTable,
        int minimumPageBytes = DefaultMinimumPageBytes,
        int targetPageBytes = DefaultTargetPageBytes,
        int maximumPageBytes = DefaultMaximumPageBytes,
        long? maximumAggregateBytes = null)
    {
        if (minimumPageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumPageBytes),
                minimumPageBytes,
                "Minimum Process durable-store page bytes must be positive.");
        }
        if (targetPageBytes < minimumPageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetPageBytes),
                targetPageBytes,
                "Target Process durable-store page bytes cannot be smaller than the minimum.");
        }
        if (!System.Numerics.BitOperations.IsPow2(targetPageBytes))
        {
            throw new ArgumentException(
                "Target Process durable-store page bytes must be a power of two.",
                nameof(targetPageBytes));
        }
        if (maximumPageBytes < targetPageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPageBytes),
                maximumPageBytes,
                "Maximum Process durable-store page bytes cannot be smaller than the target.");
        }
        if (maximumAggregateBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAggregateBytes),
                maximumAggregateBytes,
                "Maximum reconstructed Process aggregate bytes must be positive when configured.");
        }

        AuthorityId = Guard.RequireNotNullOrWhiteSpace(authorityId);
        var qualifiedTable = new SqlQualifiedTable(schema, table);
        var instanceTable = new SqlQualifiedTable(schema, $"{table}_instances");
        var pageTable = new SqlQualifiedTable(schema, $"{table}_pages");
        Schema = qualifiedTable.SchemaName!.Value.Value;
        Table = qualifiedTable.TableName.Value;
        InstanceTable = instanceTable.TableName.Value;
        PageTable = pageTable.TableName.Value;
        QualifiedSchema = qualifiedTable.SchemaName.Value.ToSql(PostgresSqlDialect.Instance);
        QualifiedTable = qualifiedTable.ToSql(PostgresSqlDialect.Instance);
        QualifiedInstanceTable = instanceTable.ToSql(PostgresSqlDialect.Instance);
        QualifiedPageTable = pageTable.ToSql(PostgresSqlDialect.Instance);
        Instances = instanceTable;
        Pages = pageTable;
        MinimumPageBytes = minimumPageBytes;
        TargetPageBytes = targetPageBytes;
        MaximumPageBytes = maximumPageBytes;
        MaximumAggregateBytes = maximumAggregateBytes;
    }

    /// <summary>Stable row identity of one independent Process durability authority.</summary>
    public string AuthorityId { get; }

    /// <summary>Validated PostgreSQL schema identifier.</summary>
    public string Schema { get; }

    /// <summary>Validated legacy PostgreSQL table identifier.</summary>
    public string Table { get; }

    /// <summary>Validated per-instance root-table identifier.</summary>
    public string InstanceTable { get; }

    /// <summary>Validated content-addressed page-table identifier.</summary>
    public string PageTable { get; }

    /// <summary>Positive minimum content-defined page size.</summary>
    public int MinimumPageBytes { get; }

    /// <summary>Power-of-two target content-defined page size.</summary>
    public int TargetPageBytes { get; }

    /// <summary>Maximum content-defined page size.</summary>
    public int MaximumPageBytes { get; }

    /// <summary>Optional maximum reconstructed canonical aggregate bytes.</summary>
    public long? MaximumAggregateBytes { get; }

    internal string QualifiedTable { get; }

    internal string QualifiedSchema { get; }

    internal string QualifiedInstanceTable { get; }

    internal string QualifiedPageTable { get; }

    internal SqlQualifiedTable Instances { get; }

    internal SqlQualifiedTable Pages { get; }
}
