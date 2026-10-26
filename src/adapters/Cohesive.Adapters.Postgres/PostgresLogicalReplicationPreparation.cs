using Cohesive.Adapters.Sql;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Explicit opt-in preparation of FULL-identity pgoutput inputs; never part of normal reads.</summary>
public static class PostgresLogicalReplicationPreparation
{
    /// <summary>Creates missing publication/slots and enables full before images on supplied tables.</summary>
    /// <param name="database">Borrowed native pool.</param>
    /// <param name="tables">Exact declared source tables.</param>
    /// <param name="publication">Dedicated publication name.</param>
    /// <param name="slots">Dedicated stable slot names.</param>
    /// <param name="state">Configured progress authority protecting against lost-WAL recreation.</param>
    /// <param name="cancellationToken">Cancels native preparation.</param>
    /// <returns>Completion after explicit preparation.</returns>
    /// <exception cref="InvalidOperationException">A slot is missing while durable state exists.</exception>
    public static async Task InitializeAsync(NpgsqlDataSource database, IEnumerable<SqlQualifiedTable> tables,
        string publication, IEnumerable<string> slots, PostgresMaterializationStateStore state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(publication);
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            using var identifiers = new NpgsqlCommandBuilder();
            var names = tables.Select(table => table.ToSql(PostgresSqlDialect.Instance)).ToArray();
            foreach (var name in names)
            {
                await using var identity = new NpgsqlCommand("ALTER TABLE " + name + " REPLICA IDENTITY FULL", connection, transaction);
                await identity.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = $1)", connection, transaction);
            exists.Parameters.AddWithValue(publication);
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
            {
                await using var createPublication = new NpgsqlCommand("CREATE PUBLICATION " + identifiers.QuoteIdentifier(publication) +
                    " FOR TABLE " + string.Join(", ", names) + " WITH (publish = 'insert, update, delete')", connection, transaction);
                await createPublication.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            foreach (var input in slots)
            {
                await using var slot = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = $1)", connection);
                slot.Parameters.AddWithValue(input);
                if (!(bool)(await slot.ExecuteScalarAsync(cancellationToken))!)
                {
                    // An absent slot cannot be reconstructed from an old durable cursor.
                    if (await state.HasStoredStateAsync(cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("A replication slot is missing while durable materialization state exists. Recover retained WAL or explicitly reset the shadow state; initialization cannot reconstruct lost changes.");
                    await using var create = new NpgsqlCommand("SELECT pg_create_logical_replication_slot($1, 'pgoutput')", connection);
                    create.Parameters.AddWithValue(input);
                    await create.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
    }
}
