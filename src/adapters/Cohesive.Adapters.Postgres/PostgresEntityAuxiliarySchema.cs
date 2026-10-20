using Npgsql;

namespace Cohesive.Adapters.Postgres;

internal static class PostgresEntityAuxiliarySchema
{
    internal static async Task ValidateTable(NpgsqlDataSource dataSource, string table, string[] columns, string[] key, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT attname || ':' || format_type(atttypid,atttypmod), attnotnull
            FROM pg_attribute WHERE attrelid=to_regclass($1) AND attnum>0 AND NOT attisdropped ORDER BY attnum
            """);
        command.Parameters.AddWithValue(table);
        var found = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                found.Add(reader.GetBoolean(1) ? reader.GetString(0) : "nullable");
        if (!found.Order(StringComparer.Ordinal).SequenceEqual(columns.Order(StringComparer.Ordinal))) throw new InvalidOperationException("Entity auxiliary columns require migration.");
        await using var constraint = dataSource.CreateCommand("""
            SELECT array_agg(a.attname::text ORDER BY k.ordinality)
            FROM pg_constraint c CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY k(attnum,ordinality)
            JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum
            WHERE c.conrelid=to_regclass($1) AND c.contype='p' AND NOT c.condeferrable GROUP BY c.oid
            """);
        constraint.Parameters.AddWithValue(table);
        if (await constraint.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string[] actual || !actual.SequenceEqual(key))
            throw new InvalidOperationException("Entity auxiliary storage requires its immediate primary key.");
    }
}
