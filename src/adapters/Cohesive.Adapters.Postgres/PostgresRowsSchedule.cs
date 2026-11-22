using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Public freshness watermarks. Applied advances only after all feeds for the captured request have published.</summary>
public sealed record PostgresRowsCheckpoint(long Requested, long Applied);

/// <summary>A durable wake-up counter for one local target. The logical replication checkpoints
/// identify actual changes. A truncate or an operator request schedules a full repair.</summary>
public sealed class PostgresRowsSchedule
{
    readonly NpgsqlDataSource database;
    readonly string[] tables;
    readonly string request;
    readonly string progress;
    readonly string trigger;

    internal PostgresRowsSchedule(NpgsqlDataSource database, IEnumerable<string> tables, string name)
    {
        this.database = database;
        this.tables = tables.ToArray();
        string Identifier(string suffix)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(name + suffix) > 63)
                throw new ArgumentException("Generated PostgreSQL scheduling identifiers must fit in 63 bytes.", nameof(name));
            return PostgresSqlDialect.Identifier(name + suffix).ToSql(PostgresSqlDialect.Instance);
        }
        request = Identifier("_request");
        progress = Identifier("_progress");
        trigger = Identifier("_changed");
    }

    /// <summary>Install transaction-coupled signals on the physical tables in the impact plan.</summary>
    public async Task InitializeAsync(CancellationToken token = default)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {request} (singleton boolean PRIMARY KEY CHECK(singleton), revision bigint NOT NULL, repair bigint NOT NULL);
            CREATE TABLE IF NOT EXISTS {progress} (singleton boolean PRIMARY KEY CHECK(singleton), revision bigint NOT NULL);
            INSERT INTO {request} VALUES(true,1,1) ON CONFLICT DO NOTHING;
            INSERT INTO {progress} VALUES(true,0) ON CONFLICT DO NOTHING;
            CREATE OR REPLACE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $fn$
            BEGIN UPDATE {request} SET revision=revision+1, repair=CASE WHEN TG_OP='TRUNCATE' THEN revision+1 ELSE repair END;
            RETURN NULL; END $fn$;
            """;
        await cmd.ExecuteNonQueryAsync(token);
        foreach (var table in tables)
        {
            cmd.CommandText = $"CREATE OR REPLACE TRIGGER {trigger} AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON {table} FOR EACH STATEMENT EXECUTE FUNCTION {trigger}()";
            await cmd.ExecuteNonQueryAsync(token);
        }
        await tx.CommitAsync(token);
    }

    /// <summary>Queue an explicit full repair and return its durable revision.</summary>
    public async Task<long> RequestResyncAsync(CancellationToken token = default)
    {
        await using var cmd = database.CreateCommand($"UPDATE {request} SET revision=revision+1, repair=revision+1 RETURNING revision");
        return (long)(await cmd.ExecuteScalarAsync(token))!;
    }

    /// <summary>Read requested and applied from one database snapshot.</summary>
    public async Task<PostgresRowsCheckpoint> CheckpointAsync(CancellationToken token = default) =>
        (await CaptureAsync(token)).Cut;

    internal async Task<(PostgresRowsCheckpoint Cut, bool Repair)> CaptureAsync(CancellationToken token)
    {
        await using var cmd = database.CreateCommand($"SELECT r.revision,p.revision,r.repair>p.revision FROM {request} r CROSS JOIN {progress} p");
        await using var reader = await cmd.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidOperationException("Missing materialization scheduling state.");
        return (new(reader.GetInt64(0), reader.GetInt64(1)), reader.GetBoolean(2));
    }

    internal async Task AcknowledgeAsync(long revision, CancellationToken token)
    {
        // A disconnected client's blocked UPDATE may still execute after the lock is released.
        // Require a subsequent commit from the live worker before the watermark is visible.
        await using var connection = await database.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await using var cmd = new NpgsqlCommand($"UPDATE {progress} SET revision=$1 WHERE singleton", connection, tx);
        cmd.Parameters.AddWithValue(revision);
        await cmd.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
    }
}
