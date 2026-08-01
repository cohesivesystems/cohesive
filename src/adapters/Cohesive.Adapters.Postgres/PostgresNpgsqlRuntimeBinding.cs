using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Npgsql.Replication;

namespace Cohesive.Adapters.Postgres;

/// <summary>A sanitized deterministic fingerprint of one Npgsql data-source configuration.</summary>
/// <remarks>
/// Passwords, passfile locations, and SSL passwords are excluded before hashing. The fingerprint is diagnostic
/// evidence for the bound runtime instance; it is not a credential and does not make independently constructed data
/// sources interchangeable.
/// </remarks>
public sealed record PostgresNpgsqlDataSourceFingerprint
{
    internal PostgresNpgsqlDataSourceFingerprint(
        string algorithm,
        string canonicalization,
        string value)
    {
        Algorithm = algorithm;
        Canonicalization = canonicalization;
        Value = value;
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Sanitized Npgsql data-source canonicalization profile.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>Creates a fresh, unopened Npgsql logical-replication connection for one attested runtime.</summary>
/// <remarks>
/// Logical replication connections cannot be created by <see cref="NpgsqlDataSource"/> and therefore do not inherit
/// its password, certificate, or other runtime callbacks. The caller must configure equivalent callbacks explicitly
/// in every connection returned by this factory. The runtime binding verifies sanitized connection settings and
/// rejects a connection instance returned more than once.
/// </remarks>
/// <returns>
/// A fresh, unopened logical-replication connection. Ownership transfers to the adapter operation that requests it.
/// </returns>
public delegate LogicalReplicationConnection PostgresLogicalReplicationConnectionFactory();

/// <summary>
/// Runtime attestation binding a persisted PostgreSQL database identity to one exact single-host
/// <see cref="NpgsqlDataSource"/> instance.
/// </summary>
/// <remarks>
/// The data source is borrowed and must outlive every reader constructed with this binding. The binding retains the
/// exact object identity so another data source is rejected even when it has an equivalent connection string. Its
/// public fingerprint is computed from sanitized connection settings and never includes password, passfile, or SSL
/// password values. <see cref="Authority"/> is caller-supplied provenance and must be a stable, non-secret identity,
/// not a connection string or credential.
/// </remarks>
public sealed class PostgresNpgsqlRuntimeBinding
{
    readonly NpgsqlDataSource dataSource;
    readonly ConditionalWeakTable<LogicalReplicationConnection, IssuedLogicalReplicationConnection>
        issuedLogicalReplicationConnections = new();
    readonly PostgresLogicalReplicationConnectionFactory? logicalReplicationConnectionFactory;
    readonly PostgresNpgsqlDataSourceFingerprint logicalReplicationAffinityFingerprint;

    /// <summary>Creates an exact runtime binding for a PostgreSQL database identity and Npgsql data source.</summary>
    /// <param name="database">Persisted physical database identity attested by the runtime owner.</param>
    /// <param name="dataSource">Caller-owned, single-host Npgsql data source covered by the attestation.</param>
    /// <param name="authority">Stable, non-secret identity of the configuration or deployment authority making the attestation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> or <paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="database"/> is the default empty identity, <paramref name="dataSource"/> is multi-host, or
    /// <paramref name="authority"/> is empty, exceeds 256 characters, or is not an ASCII provenance identity composed
    /// of letters, digits, <c>/</c>, <c>.</c>, <c>_</c>, <c>-</c>, <c>:</c>, or <c>@</c>.
    /// </exception>
    public PostgresNpgsqlRuntimeBinding(
        PostgresRelationQueryDatabaseId database,
        NpgsqlDataSource dataSource,
        string authority)
    {
        if (string.IsNullOrWhiteSpace(database.Value))
        {
            throw new ArgumentException("A PostgreSQL runtime binding requires a persisted database identity.", nameof(database));
        }

        ArgumentNullException.ThrowIfNull(dataSource);
        if (dataSource is NpgsqlMultiHostDataSource)
        {
            throw new ArgumentException(
                "A PostgreSQL runtime binding requires one exact single-host Npgsql data source.",
                nameof(dataSource));
        }

        Database = database;
        this.dataSource = dataSource;
        Authority = RequireNonSecretAuthority(authority);
        DataSourceFingerprint = PostgresNpgsqlDataSourceFingerprinter.Compute(dataSource);
        logicalReplicationAffinityFingerprint =
            PostgresNpgsqlDataSourceFingerprinter.ComputeLogicalReplicationAffinity(
                dataSource.ConnectionString);
    }

    /// <summary>
    /// Creates an exact runtime binding with an explicit factory for fresh logical-replication connections.
    /// </summary>
    /// <param name="database">Persisted physical database identity attested by the runtime owner.</param>
    /// <param name="dataSource">Caller-owned, single-host Npgsql data source covered by the attestation.</param>
    /// <param name="authority">Stable, non-secret identity of the configuration or deployment authority.</param>
    /// <param name="logicalReplicationConnectionFactory">
    /// Factory returning a fresh, unopened logical-replication connection on every invocation. Its sanitized
    /// connection settings must equal those of <paramref name="dataSource"/>. Because Npgsql data-source callbacks
    /// are not inherited, the factory owner must configure equivalent credential and certificate behavior itself.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dataSource"/>, <paramref name="authority"/>, or
    /// <paramref name="logicalReplicationConnectionFactory"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="database"/> is the default empty identity, <paramref name="dataSource"/> is multi-host, or
    /// <paramref name="authority"/> is not a bounded canonical non-secret provenance identity.
    /// </exception>
    public PostgresNpgsqlRuntimeBinding(
        PostgresRelationQueryDatabaseId database,
        NpgsqlDataSource dataSource,
        string authority,
        PostgresLogicalReplicationConnectionFactory logicalReplicationConnectionFactory)
        : this(
            database: database,
            dataSource: dataSource,
            authority: authority)
    {
        this.logicalReplicationConnectionFactory = Guard.RequireNotNull(
            logicalReplicationConnectionFactory);
    }

    /// <summary>Persisted physical PostgreSQL database identity attested by this runtime binding.</summary>
    public PostgresRelationQueryDatabaseId Database { get; }

    /// <summary>Sanitized fingerprint of the exact bound data-source configuration.</summary>
    public PostgresNpgsqlDataSourceFingerprint DataSourceFingerprint { get; }

    /// <summary>Stable, non-secret identity of the authority that supplied the runtime binding.</summary>
    public string Authority { get; }

    internal bool Matches(NpgsqlDataSource candidate) =>
        ReferenceEquals(dataSource, candidate)
        && Equals(DataSourceFingerprint, PostgresNpgsqlDataSourceFingerprinter.Compute(candidate));

    internal NpgsqlDataSource DataSource => dataSource;

    internal bool SupportsLogicalReplication => logicalReplicationConnectionFactory is not null;

    internal async ValueTask<LogicalReplicationConnection> CreateLogicalReplicationConnectionAsync()
    {
        var factory = logicalReplicationConnectionFactory
            ?? throw new InvalidOperationException(
                "The PostgreSQL runtime binding has no logical-replication connection factory.");
        var connection = factory();
        if (connection is null)
        {
            throw new InvalidOperationException(
                "The PostgreSQL logical-replication connection factory returned null.");
        }
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
            if (string.IsNullOrWhiteSpace(settings.Host)
                || settings.Host.Contains(',', StringComparison.Ordinal)
                || !Equals(
                    logicalReplicationAffinityFingerprint,
                    PostgresNpgsqlDataSourceFingerprinter.ComputeLogicalReplicationAffinity(
                        connection.ConnectionString)))
            {
                throw new InvalidOperationException(
                    "The logical-replication connection does not match the runtime binding's attested single-host data source.");
            }

            lock (issuedLogicalReplicationConnections)
            {
                if (issuedLogicalReplicationConnections.TryGetValue(connection, out _))
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL logical-replication connection factory must return a fresh connection instance for every operation.");
                }

                issuedLogicalReplicationConnections.Add(
                    connection,
                    IssuedLogicalReplicationConnection.Instance);
            }

            return connection;
        }
        catch
        {
            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Affinity/freshness is the primary failure; cleanup must not replace it.
            }
            throw;
        }
    }

    static string RequireNonSecretAuthority(string authority)
    {
        var value = Guard.RequireNotNullOrWhiteSpace(authority);
        if (value.Length > 256 || value.Any(static character =>
                !char.IsAsciiLetterOrDigit(character)
                && character is not ('/' or '.' or '_' or '-' or ':' or '@')))
        {
            throw new ArgumentException(
                "A PostgreSQL runtime-binding authority must be a bounded ASCII provenance identity, not a connection string or credential.",
                nameof(authority));
        }
        return value;
    }

    sealed class IssuedLogicalReplicationConnection
    {
        internal static IssuedLogicalReplicationConnection Instance { get; } = new();
    }
}

static class PostgresNpgsqlDataSourceFingerprinter
{
    const string Algorithm = "sha256";
    const string Canonicalization = "cohesive.adapters.postgres/npgsql-data-source-sanitized/v1";
    const string LogicalReplicationAffinityCanonicalization =
        "cohesive.adapters.postgres/npgsql-logical-replication-affinity-sanitized/v1";

    internal static PostgresNpgsqlDataSourceFingerprint Compute(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        return Compute(dataSource.ConnectionString);
    }

    internal static PostgresNpgsqlDataSourceFingerprint Compute(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        return Compute(settings, Canonicalization);
    }

    internal static PostgresNpgsqlDataSourceFingerprint ComputeLogicalReplicationAffinity(
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        RemoveSetting(settings, "Pooling");
        RemoveSetting(settings, "Enlist");
        RemoveSetting(settings, "Multiplexing");
        RemoveSetting(settings, "Keepalive");
        return Compute(settings, LogicalReplicationAffinityCanonicalization);
    }

    static PostgresNpgsqlDataSourceFingerprint Compute(
        NpgsqlConnectionStringBuilder settings,
        string canonicalization)
    {
        RemoveSensitiveSetting(settings, "Password");
        RemoveSensitiveSetting(settings, "Passfile");
        RemoveSensitiveSetting(settings, "SSL Password");

        var canonical = new StringBuilder(canonicalization.Length + settings.ConnectionString.Length + 64);
        Append(canonical, canonicalization);
        foreach (var key in settings.Keys.Cast<string>().Order(StringComparer.Ordinal))
        {
            Append(canonical, key);
            Append(canonical, Format(settings[key]));
        }

        return new(
            Algorithm,
            canonicalization,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
                .ToLowerInvariant());
    }

    static void RemoveSensitiveSetting(NpgsqlConnectionStringBuilder settings, string key)
    {
        if (settings.ContainsKey(key))
        {
            settings.Remove(key);
        }
    }

    static void RemoveSetting(NpgsqlConnectionStringBuilder settings, string key)
    {
        if (settings.ContainsKey(key))
        {
            settings.Remove(key);
        }
    }

    static string Format(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    static void Append(StringBuilder canonical, string value)
    {
        canonical.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(value);
        canonical.Append(';');
    }
}
