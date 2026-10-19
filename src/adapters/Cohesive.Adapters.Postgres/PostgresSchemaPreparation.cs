using Npgsql;

namespace Cohesive.Adapters.Postgres;

// One create/validate lifecycle for model-owned relational attachments. Native history providers retain
// their own migration protocol because their object set and transaction semantics differ.
internal static class PostgresSchemaPreparation
{
    internal static async Task<TStorage?> PrepareAsync<TOptions, TStorage>(NpgsqlDataSource database,
        TOptions? options, PostgresSchemaInitialization initialization, Func<TOptions, string> schema,
        Func<TOptions, NpgsqlDataSource, CancellationToken, Task<TStorage>> bind, CancellationToken token)
        where TOptions : class where TStorage : class
    {
        token.ThrowIfCancellationRequested();
        if (options is null) return null;
        if (initialization == PostgresSchemaInitialization.CreateIfMissing)
            await CreateAsync(database, schema(options), token).ConfigureAwait(false);
        return await bind(options, database, token).ConfigureAwait(false);
    }

    internal static async Task CreateAsync(NpgsqlDataSource database, string sql, CancellationToken token)
    {
        // A database-scoped bootstrap lock covers overlapping product/receipt/outbox declarations.
        // Hold the lock through arbitrary multi-statement native DDL on this transaction.
        await using var connection = await database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(71261591088300481);\n" + sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }
}
