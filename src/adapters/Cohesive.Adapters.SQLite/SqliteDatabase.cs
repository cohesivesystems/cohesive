using Cohesive.Adapters.Sql;
using System.Data;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite;

/// <summary>Opens configured connections and binds commands without acquiring hidden transaction ownership.</summary>
/// <remarks>
/// The database object is immutable and reusable across threads. Returned connections, transactions, commands,
/// and readers have one caller owner and must not be shared concurrently. Provider operations are synchronous.
/// Cancellation is observed between operations; it cannot interrupt SQLite I/O or its bounded lock retry loop.
/// </remarks>
public sealed class SqliteDatabase
{
    readonly string connectionString;

    /// <summary>Minimum qualified native engine version, including the upstream WAL-reset fix.</summary>
    public static Version MinimumEngineVersion { get; } = new(3, 51, 3);

    /// <summary>Creates a runtime for one resolved local database configuration.</summary>
    /// <param name="options">Immutable configuration; constructing the runtime does not open or migrate the database.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public SqliteDatabase(SqliteDatabaseOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = options.Pooling == SqliteConnectionPooling.Enabled,
            ForeignKeys = true,
            DefaultTimeout = options.BusyTimeoutSeconds
        }.ToString();
    }

    /// <summary>Inspectable effective settings and their convention origins.</summary>
    public SqliteDatabaseOptions Options { get; }

    /// <summary>Opens a caller-owned logical connection with verified WAL, foreign keys, and synchronization settings.</summary>
    /// <param name="cancellationToken">Cancellation checked before opening and between configuration operations.</param>
    /// <returns>An open connection which the caller must dispose. Native handle reuse follows <see cref="SqliteDatabaseOptions.Pooling"/>.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed before ownership transfers to the caller.</exception>
    /// <exception cref="SqliteException">Opening, locking, or configuring the database fails.</exception>
    /// <exception cref="InvalidOperationException">SQLite cannot establish the requested operating profile.</exception>
    public SqliteConnection OpenConnection(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            cancellationToken.ThrowIfCancellationRequested();
            // The provider applies connection-string PRAGMAs only when creating a native handle.
            // A preceding pool borrower may have changed them, so restore required state on every checkout.
            if (Options.Pooling == SqliteConnectionPooling.Enabled)
            {
                using var reset = CreateCommand(connection, null, "PRAGMA foreign_keys = ON; PRAGMA query_only = OFF;");
                reset.ExecuteNonQuery();
            }
            using (var engine = CreateCommand(connection, null, "SELECT sqlite_version();"))
            {
                var version = engine.ExecuteScalar() as string;
                if (!Version.TryParse(version, out var parsed) || parsed < MinimumEngineVersion)
                    throw new InvalidOperationException($"The SQLite WAL profile requires native engine {MinimumEngineVersion} or newer; found '{version}'.");
            }
            using (var journal = CreateCommand(connection, null, "PRAGMA journal_mode = WAL;"))
            {
                if (!string.Equals(journal.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The database did not accept the required WAL profile.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            using (var sync = CreateCommand(connection, null, Options.Durability == SqliteDurability.Full
                ? "PRAGMA synchronous = FULL;" : "PRAGMA synchronous = NORMAL;"))
                sync.ExecuteNonQuery();
            using (var sync = CreateCommand(connection, null, "PRAGMA synchronous;"))
            {
                var expected = Options.Durability == SqliteDurability.Full ? 2L : 1L;
                if (sync.ExecuteScalar() is not long actual || actual != expected)
                    throw new InvalidOperationException("The database did not accept the requested synchronization policy.");
            }
            using (var foreignKeys = CreateCommand(connection, null, "PRAGMA foreign_keys;"))
            {
                if (foreignKeys.ExecuteScalar() is not 1L)
                    throw new InvalidOperationException("Foreign-key enforcement is required by this SQLite profile.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            // Do not repeatedly hand out a pooled handle whose required profile could not be established.
            if (Options.Pooling == SqliteConnectionPooling.Enabled)
                SqliteConnection.ClearPool(connection);
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Releases this connection-string pool's idle native handles and retires active handles when returned.</summary>
    /// <remarks>Call after operations finish before deleting or replacing the database file. Matching database objects
    /// share the provider pool; this operation does not interrupt active borrowers or prevent later checkouts.</remarks>
    public void ClearPool()
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>Binds a precompiled SQLite template directly to fresh provider parameters.</summary>
    /// <param name="connection">Open caller-owned connection.</param>
    /// <param name="transaction">Borrowed transaction on the same connection, or null.</param>
    /// <param name="template">Reusable provider binding plan over shared SQL construction.</param>
    /// <param name="values">Exactly one encoded INTEGER/TEXT/BLOB/null value per runtime binding, in any order.
    /// Mutable byte arrays are borrowed for the command lifetime; callers retain ownership.</param>
    /// <returns>A caller-owned command; binding does not execute SQL or take transaction ownership.</returns>
    /// <exception cref="ArgumentNullException">The connection or template is null.</exception>
    /// <exception cref="ArgumentException">Bindings are missing, repeated, unknown or invalid, or the transaction has another owner.</exception>
    /// <exception cref="InvalidOperationException">The connection is not open.</exception>
    public SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction,
        SqliteCommandTemplate template, params ReadOnlySpan<(string Binding, object? Value)> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Bind(this, connection, transaction, values);
    }

    /// <summary>Binds a command to a caller-owned connection and optional transaction with the configured timeout.</summary>
    /// <param name="connection">Open connection borrowed for the command lifetime.</param>
    /// <param name="transaction">Borrowed transaction on that exact connection, or null for an operation outside a transaction.</param>
    /// <param name="sql">Trusted SQL template; use parameters for data and QuoteIdentifier for identifiers.</param>
    /// <param name="parameters">Fresh parameters whose ownership transfers to the returned command.</param>
    /// <returns>A caller-owned command; disposing it never commits or disposes the borrowed transaction.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    /// <exception cref="ArgumentException">SQL is empty, contains NUL/invalid Unicode, or the transaction belongs to another/closed connection.</exception>
    /// <exception cref="InvalidOperationException">The supplied connection is not open.</exception>
    public SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params ReadOnlySpan<SqliteParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        if (SqliteScalarCodec.RequireText(sql).Contains('\0'))
            throw new ArgumentException("SQLite command text cannot contain NUL; bind values as parameters.", nameof(sql));
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("A SQLite command requires an open borrowed connection.");
        if (transaction is not null && !ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The transaction must belong to the supplied open connection.", nameof(transaction));
        var command = connection.CreateCommand();
        try
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            command.CommandTimeout = Options.BusyTimeoutSeconds;
            foreach (var parameter in parameters)
                command.Parameters.Add(parameter);
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>Quotes one SQLite identifier component without interpreting dots as schema qualification.</summary>
    /// <param name="identifier">Nonempty identifier without NUL characters.</param>
    /// <returns>A double-quoted identifier with embedded quotes doubled.</returns>
    /// <exception cref="ArgumentException">The identifier is empty, whitespace, or contains NUL/invalid Unicode.</exception>
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return new SqlIdentifier(identifier).ToSql(SqliteSqlDialect.Instance);
    }
}
