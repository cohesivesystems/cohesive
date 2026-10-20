using Npgsql;

namespace Cohesive.Adapters.Postgres;

// One admit/create/validate lifecycle for model-owned relational attachments. Native history providers retain
// their own migration protocol because their object set and transaction semantics differ.
internal static class PostgresSchemaPreparation
{
    // Stable lock protocol shared with reviewed standalone SQL migrations.
    internal const long AdvisoryLockKey = 71261591088300481;
    internal static async Task<TStorage?> PrepareAsync<TOptions, TStorage>(NpgsqlDataSource database,
        TOptions? options, PostgresSchemaInitialization initialization, Func<TOptions, string> schema,
        Func<TOptions, NpgsqlDataSource, PostgresSchemaInitialization, CancellationToken, Task<TStorage?>> admit, CancellationToken token)
        where TOptions : class where TStorage : class
    {
        token.ThrowIfCancellationRequested();
        if (options is null) return null;
        var existing = await admit(options, database, initialization, token).ConfigureAwait(false);
        if (existing is not null) return existing;
        await CreateAsync(database, schema(options), token).ConfigureAwait(false);
        return await admit(options, database, PostgresSchemaInitialization.ValidateOnly, token).ConfigureAwait(false);
    }

    internal static async Task CreateAsync(NpgsqlDataSource database, string sql, CancellationToken token)
    {
        // A database-scoped bootstrap lock covers overlapping product/receipt/outbox declarations.
        // Hold the lock through arbitrary multi-statement native DDL on this transaction.
        await using var connection = await database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(FormattableString.Invariant($"SELECT pg_advisory_xact_lock({AdvisoryLockKey});\n") + sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }
}
