namespace Cohesive.Adapters.Postgres;

/// <summary>Physical PostgreSQL binding and limits for one portable Process distribution authority.</summary>
public sealed record PostgresProcessDistributionStoreOptions
{
    /// <summary>Default PostgreSQL schema for distribution ledger tables.</summary>
    public const string DefaultSchema = "cohesive";

    /// <summary>Default table containing atomic distribution ledger aggregates.</summary>
    public const string DefaultTable = "process_distribution_ledgers";

    /// <summary>Default maximum UTF-8 bytes of one aggregate document.</summary>
    public const long DefaultMaximumLedgerBytes = 16L * 1024 * 1024;

    /// <summary>Creates a physical PostgreSQL distribution binding.</summary>
    /// <param name="authorityId">Stable row identity of one independent distribution authority.</param>
    /// <param name="schema">Validated PostgreSQL schema identifier.</param>
    /// <param name="table">Validated PostgreSQL table identifier.</param>
    /// <param name="maximumLedgerBytes">Strictly positive maximum serialized aggregate bytes.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authorityId"/>, <paramref name="schema"/>, or <paramref name="table"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authorityId"/> is empty, or an SQL identifier contains unsupported characters.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumLedgerBytes"/> is not positive.</exception>
    public PostgresProcessDistributionStoreOptions(
        string authorityId,
        string schema = DefaultSchema,
        string table = DefaultTable,
        long maximumLedgerBytes = DefaultMaximumLedgerBytes)
    {
        if (maximumLedgerBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLedgerBytes),
                maximumLedgerBytes,
                "Maximum ledger bytes must be positive.");
        }

        AuthorityId = Guard.RequireNotNullOrWhiteSpace(authorityId);
        Schema = RequireSqlIdentifier(schema, nameof(schema));
        Table = RequireSqlIdentifier(table, nameof(table));
        MaximumLedgerBytes = maximumLedgerBytes;
    }

    /// <summary>Stable row identity of one independent distribution authority.</summary>
    public string AuthorityId { get; }

    /// <summary>Validated PostgreSQL schema identifier.</summary>
    public string Schema { get; }

    /// <summary>Validated PostgreSQL table identifier.</summary>
    public string Table { get; }

    /// <summary>Strictly positive maximum serialized aggregate bytes.</summary>
    public long MaximumLedgerBytes { get; }

    internal string QualifiedTable => $"\"{Schema}\".\"{Table}\"";

    static string RequireSqlIdentifier(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || !(value[0] == '_' || char.IsAsciiLetter(value[0])))
            throw new ArgumentException("A PostgreSQL identifier must start with an ASCII letter or underscore.", parameterName);
        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] != '_' && !char.IsAsciiLetterOrDigit(value[index]))
            {
                throw new ArgumentException(
                    "A PostgreSQL identifier may contain only ASCII letters, digits, and underscores.",
                    parameterName);
            }
        }
        return value;
    }
}
