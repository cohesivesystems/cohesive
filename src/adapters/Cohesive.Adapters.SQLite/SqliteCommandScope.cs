using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite;

/// <summary>Reuses private native commands within one caller-owned connection and transaction lifetime.</summary>
/// <remarks>
/// Use one scope per operation, with a finite set of shared template instances. Each template instance acquires
/// one command lazily; subsequent executions reuse its parameters and native preparation. The scope has one owner,
/// is not thread-safe, and permits only one active reader. Dispose it before disposing the borrowed transaction or
/// connection. It never commits, rolls back, retries, opens a connection, or changes the database profile.
/// Runtime arrays remain borrowed until their command is rebound or the scope is disposed. Failed binding does
/// not execute SQL or partially change cached values; every subsequent execution must supply a complete binding.
/// </remarks>
public sealed class SqliteCommandScope : IDisposable
{
    readonly SqliteDatabase database;
    readonly SqliteConnection connection;
    readonly SqliteTransaction transaction;
    readonly Dictionary<SqliteCommandTemplate, SqliteCommand> commands = [];
    SqliteDataReader? activeReader;
    bool disposed;

    /// <summary>Creates an empty operation scope without preparing or executing SQL.</summary>
    /// <param name="database">Runtime supplying command policy and the configured timeout.</param>
    /// <param name="connection">Open connection borrowed until the scope is disposed.</param>
    /// <param name="transaction">Active caller-owned transaction on that connection, delimiting the reuse lifetime.</param>
    /// <exception cref="ArgumentNullException">The database, connection or transaction is null.</exception>
    /// <exception cref="ArgumentException">The transaction does not belong to the supplied open connection.</exception>
    /// <exception cref="InvalidOperationException">The connection is not open.</exception>
    public SqliteCommandScope(SqliteDatabase database, SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(transaction);
        SqliteDatabase.RequireConnection(connection, transaction);
        this.database = database;
        this.connection = connection;
        this.transaction = transaction;
    }

    /// <summary>Binds and synchronously executes a mutation using its retained native command.</summary>
    /// <param name="template">Reusable SQLite template instance identifying the command in this scope.</param>
    /// <param name="cancellationToken">Cancellation observed before binding and execution, not during native I/O.</param>
    /// <param name="values">Exactly one encoded value for each runtime binding, in any order.</param>
    /// <returns>The provider's number of affected rows.</returns>
    /// <exception cref="ArgumentNullException">The template is null.</exception>
    /// <exception cref="ArgumentException">Bindings are incomplete, duplicate, unknown or invalid, or the borrowed transaction has ended.</exception>
    /// <exception cref="InvalidOperationException">A reader is still open or the borrowed connection is closed.</exception>
    /// <exception cref="ObjectDisposedException">The scope has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before execution.</exception>
    /// <exception cref="SqliteException">Native preparation, binding, locking or execution fails.</exception>
    public int ExecuteNonQuery(SqliteCommandTemplate template, CancellationToken cancellationToken,
        params ReadOnlySpan<(string Binding, object? Value)> values) =>
        Bind(template, cancellationToken, values).ExecuteNonQuery();

    /// <summary>Binds and synchronously executes a query returning its first scalar value.</summary>
    /// <param name="template">Reusable SQLite template instance identifying the command in this scope.</param>
    /// <param name="cancellationToken">Cancellation observed before binding and execution, not during native I/O.</param>
    /// <param name="values">Exactly one encoded value for each runtime binding, in any order.</param>
    /// <returns>The first column of the first row, DBNull for SQL null, or null when no row matches.</returns>
    /// <exception cref="ArgumentNullException">The template is null.</exception>
    /// <exception cref="ArgumentException">Bindings are incomplete, duplicate, unknown or invalid, or the borrowed transaction has ended.</exception>
    /// <exception cref="InvalidOperationException">A reader is still open or the borrowed connection is closed.</exception>
    /// <exception cref="ObjectDisposedException">The scope has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before execution.</exception>
    /// <exception cref="SqliteException">Native preparation, binding, locking or execution fails.</exception>
    public object? ExecuteScalar(SqliteCommandTemplate template, CancellationToken cancellationToken,
        params ReadOnlySpan<(string Binding, object? Value)> values) =>
        Bind(template, cancellationToken, values).ExecuteScalar();

    /// <summary>Binds and starts a synchronous query, retaining its command while the caller reads.</summary>
    /// <param name="template">Reusable SQLite template instance identifying the command in this scope.</param>
    /// <param name="cancellationToken">Cancellation observed before binding and execution, not during native I/O.</param>
    /// <param name="values">Exactly one encoded value for each runtime binding, in any order.</param>
    /// <returns>A native reader the caller must dispose before another scope operation. Scope disposal also closes it.</returns>
    /// <exception cref="ArgumentNullException">The template is null.</exception>
    /// <exception cref="ArgumentException">Bindings are incomplete, duplicate, unknown or invalid, or the borrowed transaction has ended.</exception>
    /// <exception cref="InvalidOperationException">A reader is still open or the borrowed connection is closed.</exception>
    /// <exception cref="ObjectDisposedException">The scope has been disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before execution.</exception>
    /// <exception cref="SqliteException">Native preparation, binding, locking or execution fails.</exception>
    public SqliteDataReader ExecuteReader(SqliteCommandTemplate template, CancellationToken cancellationToken,
        params ReadOnlySpan<(string Binding, object? Value)> values)
    {
        activeReader = Bind(template, cancellationToken, values).ExecuteReader();
        return activeReader;
    }

    SqliteCommand Bind(SqliteCommandTemplate template, CancellationToken cancellationToken,
        ReadOnlySpan<(string Binding, object? Value)> values)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(template);
        SqliteDatabase.RequireConnection(connection, transaction);
        if (activeReader is { IsClosed: false })
            throw new InvalidOperationException("Dispose the active SQLite reader before reusing its operation scope.");
        activeReader = null;
        if (commands.TryGetValue(template, out var command))
            template.Rebind(command, values);
        else
        {
            command = database.CreateCommand(connection, transaction, template, values);
            commands.Add(template, command);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return command;
    }

    /// <summary>Closes the active reader and owned commands without disposing or completing borrowed resources.</summary>
    /// <exception cref="SqliteException">Closing a native reader fails while finishing its statement.</exception>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { activeReader?.Dispose(); }
        finally
        {
            activeReader = null;
            foreach (var command in commands.Values) command.Dispose();
            commands.Clear();
        }
    }
}
