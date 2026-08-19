using System.Text;

namespace Cohesive.Adapters.Postgres;

/// <summary>Physical PostgreSQL binding and limits for one portable Process durability authority.</summary>
public sealed record PostgresProcessDurableStoreOptions
{
    /// <summary>Default PostgreSQL schema for Process durable-store documents.</summary>
    public const string DefaultSchema = "cohesive";

    /// <summary>Default table containing atomic Process durable-store documents.</summary>
    public const string DefaultTable = "process_durable_stores";

    /// <summary>Default maximum UTF-8 bytes of one authority document.</summary>
    public const long DefaultMaximumDocumentBytes = 64L * 1024 * 1024;

    /// <summary>Creates a physical PostgreSQL Process durability binding.</summary>
    /// <param name="authorityId">Stable row identity of one independent Process durability authority.</param>
    /// <param name="schema">Validated PostgreSQL schema identifier.</param>
    /// <param name="table">Validated PostgreSQL table identifier.</param>
    /// <param name="maximumDocumentBytes">Strictly positive maximum serialized authority bytes.</param>
    /// <exception cref="ArgumentNullException">A string argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authorityId"/> is empty or a SQL identifier is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumDocumentBytes"/> is not positive.</exception>
    public PostgresProcessDurableStoreOptions(
        string authorityId,
        string schema = DefaultSchema,
        string table = DefaultTable,
        long maximumDocumentBytes = DefaultMaximumDocumentBytes)
    {
        if (maximumDocumentBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDocumentBytes),
                maximumDocumentBytes,
                "Maximum Process durable-store document bytes must be positive.");
        }

        AuthorityId = Guard.RequireNotNullOrWhiteSpace(authorityId);
        var qualifiedTable = new PostgresSqlQualifiedTable(schema, table);
        Schema = qualifiedTable.SchemaName!.Value.Value;
        Table = qualifiedTable.TableName.Value;
        StringBuilder schemaSql = new();
        qualifiedTable.SchemaName.Value.WriteQuoted(schemaSql);
        QualifiedSchema = schemaSql.ToString();
        StringBuilder sql = new();
        qualifiedTable.WriteTo(sql);
        QualifiedTable = sql.ToString();
        MaximumDocumentBytes = maximumDocumentBytes;
    }

    /// <summary>Stable row identity of one independent Process durability authority.</summary>
    public string AuthorityId { get; }

    /// <summary>Validated PostgreSQL schema identifier.</summary>
    public string Schema { get; }

    /// <summary>Validated PostgreSQL table identifier.</summary>
    public string Table { get; }

    /// <summary>Strictly positive maximum serialized authority bytes.</summary>
    public long MaximumDocumentBytes { get; }

    internal string QualifiedTable { get; }

    internal string QualifiedSchema { get; }
}
