using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite;

/// <summary>One immutable, numbered migration composed of transaction-safe SQLite statements.</summary>
public sealed class SqliteMigration
{
    /// <summary>Creates an inspectable migration revision without executing SQL.</summary>
    /// <param name="version">Positive contiguous version within its owning module.</param>
    /// <param name="statements">Nonempty ordered SQL statements; each is one CREATE, ALTER, DROP, INSERT, UPDATE, or DELETE statement.</param>
    /// <exception cref="ArgumentOutOfRangeException">The version is not positive.</exception>
    /// <exception cref="ArgumentException">Statements are empty or contain transaction control, batching, or unsupported SQL.</exception>
    public SqliteMigration(int version, ImmutableArray<string> statements)
    {
        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        if (statements.IsDefaultOrEmpty)
            throw new ArgumentException("A migration must contain at least one statement.", nameof(statements));
        foreach (var statement in statements)
            SqliteMigrationSql.Validate(statement);
        Version = version;
        Statements = statements;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("cohesive.sqlite.migration/v1");
            writer.WriteNumberValue(version);
            foreach (var statement in statements)
                writer.WriteStringValue(statement);
            writer.WriteEndArray();
        }
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>Version scoped to the owning schema module.</summary>
    public int Version { get; }
    /// <summary>Exact ordered SQL source; whitespace and comments are part of the revision fingerprint.</summary>
    public ImmutableArray<string> Statements { get; }
    /// <summary>SHA-256 of the versioned deterministic SQL migration representation.</summary>
    public string Fingerprint { get; }
}

/// <summary>Classifies a mismatch between durable migration history and the supplied module plan.</summary>
public enum SqliteSchemaFailure
{
    /// <summary>The database has a later migration than this application understands.</summary>
    AheadOfPlan,
    /// <summary>An applied migration differs from the supplied immutable revision.</summary>
    ChangedMigration,
    /// <summary>The durable history is not a contiguous prefix starting at version one.</summary>
    InvalidHistory
}

/// <summary>Structured evidence that a module's durable migration history cannot be reconciled safely.</summary>
public sealed class SqliteSchemaException : InvalidOperationException
{
    /// <summary>Creates a schema mismatch diagnostic.</summary>
    /// <param name="failure">Mismatch classification.</param>
    /// <param name="module">Owning schema module.</param>
    /// <param name="version">Durable version where the mismatch was discovered.</param>
    public SqliteSchemaException(SqliteSchemaFailure failure, string module, long version)
        : base($"SQLite schema module '{module}' has {failure} at version {version}; no migrations were committed.")
    {
        Failure = failure;
        Module = module;
        Version = version;
    }
    /// <summary>Mismatch classification.</summary>
    public SqliteSchemaFailure Failure { get; }
    /// <summary>Module whose history failed validation.</summary>
    public string Module { get; }
    /// <summary>First durable version that failed validation.</summary>
    public long Version { get; }
}

/// <summary>Immutable ordered migration plan owned by one application or adapter module.</summary>
/// <remarks>Module ownership scopes the history, not SQL access permissions. Trusted migrations must only modify
/// tables owned by that module and must not modify the reserved history table. PRAGMA user_version is untouched.</remarks>
public sealed class SqliteSchema
{
    internal const string HistoryTable = "__cohesive_schema_migrations_v1";

    /// <summary>Creates a module plan whose versions form a complete history from one.</summary>
    /// <param name="module">Nonempty stable ownership identity, at most 200 characters and without NUL.</param>
    /// <param name="migrations">Ordered immutable history, with contiguous versions starting at one.</param>
    /// <exception cref="ArgumentException">The module or ordered version sequence is invalid.</exception>
    public SqliteSchema(string module, ImmutableArray<SqliteMigration> migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        if (module.Length > 200 || module.Contains('\0'))
            throw new ArgumentException("A schema module must be a bounded identity without NUL.", nameof(module));
        Module = SqliteScalarCodec.RequireText(module);
        Migrations = migrations.IsDefault ? [] : migrations;
        for (var index = 0; index < Migrations.Length; index++)
        {
            if (Migrations[index] is null || Migrations[index].Version != index + 1)
                throw new ArgumentException("Migrations must be non-null and numbered contiguously from one in application order.", nameof(migrations));
        }
    }

    /// <summary>Stable ownership identity in the shared database history.</summary>
    public string Module { get; }
    /// <summary>Complete immutable migration history supplied by this application version.</summary>
    public ImmutableArray<SqliteMigration> Migrations { get; }
    /// <summary>Latest supported schema version, or zero for an empty module.</summary>
    public int Version => Migrations.Length;

    /// <summary>Validates history and atomically applies the unapplied suffix under one immediate transaction.</summary>
    /// <param name="database">Runtime used to open an owned connection and bind every migration command.</param>
    /// <param name="cancellationToken">Cancellation checked before acquisition, between statements, and before commit.</param>
    /// <returns>The module version after successful commit; an exact repeat returns the same version.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    /// <exception cref="SqliteSchemaException">Durable history differs from or exceeds the supplied plan.</exception>
    /// <exception cref="SqliteException">SQL, locking, storage, or commit fails; uncommitted changes roll back on disposal.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before commit; the migration transaction rolls back.</exception>
    /// <exception cref="InvalidOperationException">The database cannot establish its configured operating profile.</exception>
    public int Apply(SqliteDatabase database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        using var connection = database.OpenConnection(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        cancellationToken.ThrowIfCancellationRequested();
        using (var create = database.CreateCommand(connection, transaction, $"""
            CREATE TABLE IF NOT EXISTS {HistoryTable} (
                module TEXT NOT NULL,
                version INTEGER NOT NULL CHECK (version > 0),
                fingerprint TEXT NOT NULL,
                PRIMARY KEY (module, version)
            ) STRICT;
            """))
            create.ExecuteNonQuery();

        var applied = 0;
        using (var read = database.CreateCommand(connection, transaction,
            $"SELECT version, fingerprint FROM {HistoryTable} WHERE module = $module ORDER BY version;",
            new SqliteParameter("$module", Module)))
        using (var rows = read.ExecuteReader())
        {
            while (rows.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = rows.GetInt64(0);
                if (version != applied + 1L)
                    throw new SqliteSchemaException(SqliteSchemaFailure.InvalidHistory, Module, version);
                if (version > Migrations.Length)
                    throw new SqliteSchemaException(SqliteSchemaFailure.AheadOfPlan, Module, version);
                if (!string.Equals(rows.GetString(1), Migrations[applied].Fingerprint, StringComparison.Ordinal))
                    throw new SqliteSchemaException(SqliteSchemaFailure.ChangedMigration, Module, version);
                applied++;
            }
        }

        for (var index = applied; index < Migrations.Length; index++)
        {
            var migration = Migrations[index];
            foreach (var statement in migration.Statements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var command = database.CreateCommand(connection, transaction, statement);
                command.ExecuteNonQuery();
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var record = database.CreateCommand(connection, transaction,
                $"INSERT INTO {HistoryTable} (module, version, fingerprint) VALUES ($module, $version, $fingerprint);",
                new SqliteParameter("$module", Module),
                new SqliteParameter("$version", migration.Version),
                new SqliteParameter("$fingerprint", migration.Fingerprint));
            record.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return Version;
    }
}

// This deliberately small migration subset excludes transaction boundaries, PRAGMAs, ATTACH, and scripts.
// Values may contain semicolons/quotes and comments; SQL grammar remains SQLite's responsibility.
internal static class SqliteMigrationSql
{
    internal static void Validate(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        SqliteScalarCodec.RequireText(sql);
        if (sql.Contains('\0'))
            throw new ArgumentException("Migration SQL cannot contain NUL.", nameof(sql));
        string? keyword = null;
        var terminated = false;
        for (var index = 0; index < sql.Length;)
        {
            var ch = sql[index];
            if (char.IsWhiteSpace(ch)) { index++; continue; }
            if (ch == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not '\r' and not '\n') index++;
                continue;
            }
            if (ch == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0) throw new ArgumentException("Unterminated SQL comment.", nameof(sql));
                index = end + 2;
                continue;
            }
            if (terminated) throw new ArgumentException("Each migration entry must contain exactly one SQL statement.", nameof(sql));
            if (keyword is null)
            {
                var start = index;
                while (index < sql.Length && char.IsAsciiLetter(sql[index])) index++;
                keyword = sql[start..index].ToUpperInvariant();
                if (keyword is not ("CREATE" or "ALTER" or "DROP" or "INSERT" or "UPDATE" or "DELETE"))
                    throw new ArgumentException("Migration SQL must begin with CREATE, ALTER, DROP, INSERT, UPDATE, or DELETE.", nameof(sql));
                continue;
            }
            if (ch == ';') { terminated = true; index++; continue; }
            if (ch is '\'' or '"' or '`' or '[')
            {
                var close = ch == '[' ? ']' : ch;
                var closed = false;
                index++;
                while (index < sql.Length)
                {
                    if (sql[index++] != close) continue;
                    if (ch != '[' && index < sql.Length && sql[index] == close) { index++; continue; }
                    closed = true;
                    break;
                }
                if (!closed) throw new ArgumentException("Unterminated SQL literal or identifier.", nameof(sql));
                continue;
            }
            index++;
        }
        if (keyword is null) throw new ArgumentException("A migration cannot consist only of comments.", nameof(sql));
    }
}
