using System.Text;

namespace Cohesive.Adapters.Postgres;

/// <summary>Physical PostgreSQL binding for durable materialization runtime state authorities.</summary>
public sealed record PostgresMaterializationStateStoreOptions
{
    /// <summary>Default PostgreSQL schema for materialization runtime state.</summary>
    public const string DefaultSchema = "cohesive";

    /// <summary>Default table containing materialization reference-ledger documents.</summary>
    public const string DefaultTable = "materialization_state_ledgers";

    /// <summary>Default maximum UTF-8 bytes of one state-authority document.</summary>
    public const long DefaultMaximumDocumentBytes = 64L * 1024 * 1024;

    /// <summary>Creates a physical PostgreSQL materialization-state binding.</summary>
    /// <param name="authorityId">Stable identity prefix for one independent state authority.</param>
    /// <param name="schema">Validated PostgreSQL schema identifier.</param>
    /// <param name="table">Validated PostgreSQL table identifier.</param>
    /// <param name="maximumDocumentBytes">Strictly positive maximum serialized ledger bytes.</param>
    /// <exception cref="ArgumentNullException">A string argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authorityId"/> is empty or a SQL identifier is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumDocumentBytes"/> is not positive.</exception>
    public PostgresMaterializationStateStoreOptions(
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
                "Maximum materialization-state document bytes must be positive.");
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

    /// <summary>Stable identity prefix for one independent state authority.</summary>
    public string AuthorityId { get; }

    /// <summary>Validated PostgreSQL schema identifier.</summary>
    public string Schema { get; }

    /// <summary>Validated PostgreSQL table identifier.</summary>
    public string Table { get; }

    /// <summary>Strictly positive maximum serialized ledger bytes.</summary>
    public long MaximumDocumentBytes { get; }

    internal string QualifiedTable { get; }

    internal string QualifiedSchema { get; }
}
