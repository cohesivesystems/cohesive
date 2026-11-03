using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Owned PostgreSQL session advisory lease for cooperating operator commands.</summary>
/// <remarks>This does not fence external writes after network partition or server failover.</remarks>
public sealed class PostgresSessionLease : IAsyncDisposable
{
    readonly NpgsqlConnection connection;
    readonly long key;
    bool disposed;
    PostgresSessionLease(NpgsqlConnection connection, long key) { this.connection = connection; this.key = key; }

    /// <summary>Acquires an exclusive session lock without waiting.</summary>
    /// <param name="database">Borrowed connection pool.</param>
    /// <param name="key">Application-owned lock identity.</param>
    /// <param name="cancellationToken">Cancels acquisition.</param>
    /// <returns>An owned lease; dispose before the pool.</returns>
    /// <exception cref="InvalidOperationException">Another session owns the lock.</exception>
    public static async Task<PostgresSessionLease> AcquireAsync(NpgsqlDataSource database, long key,
        CancellationToken cancellationToken = default)
    {
        var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
            command.Parameters.AddWithValue(key);
            if (!(bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                throw new InvalidOperationException("Another PostgreSQL session owns the operator lease.");
            return new(connection, key);
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Releases the lease. Failed unlocks evict pooled connections without masking an operation failure.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
            command.Parameters.AddWithValue(key);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException)
        {
            NpgsqlConnection.ClearPool(connection);
        }
        finally { await connection.DisposeAsync().ConfigureAwait(false); }
    }
}
