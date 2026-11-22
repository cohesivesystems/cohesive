using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Cohesive.Adapters.Sql;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

public sealed partial class PostgresPersistenceRegistration
{
    /// <summary>Bind a flat relational row projection and a fixed input to a physical PostgreSQL table.
    /// Column names are the snake_case names of the canonical projected fields. Optional join fields use SQL NULL.</summary>
    public PostgresMaterializedRows MaterializeRows<TInput, TRow>(RelationQuery<TInput, TRow[]> query,
        TInput input, string table, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(query);
        var artifact = bindings.Prepare(query.CompilationRequest);
        var statement = artifact.Bind(new Dictionary<Cohesive.Relations.IR.QueryParameterId, ObservationValue>
            { [query.Parameter] = ObservationValue.FromObject(input) });
        return new(runtime.DataSource, schema, table, artifact, statement);
    }
}

/// <summary>A synchronous, full-snapshot PostgreSQL target. Resync is repeatable, creates a missing
/// target, and atomically replaces its rows using the compiled source SELECT on the database.</summary>
public sealed class PostgresMaterializedRows
{
    readonly NpgsqlDataSource database;
    readonly string table;
    readonly string owner;
    readonly string columns;
    readonly string select;
    readonly ImmutableArray<PostgresNpgsqlParameter> parameters;

    internal PostgresMaterializedRows(NpgsqlDataSource database, string schema, string name,
        PostgresRelationQueryCompiledArtifact artifact, SqlStatement statement)
    {
        this.database = database;
        table = Quote(schema) + "." + Quote(name);
        var fields = artifact.ResultFields;
        var names = fields.Select(field => field.Field.Path.TryGetDirectFieldName(out var member)
            ? Quote(JsonNamingPolicy.SnakeCaseLower.ConvertName(member))
            : throw new NotSupportedException("Materialized rows require direct scalar output fields.")).ToArray();
        if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new NotSupportedException("Materialized rows require distinct physical output columns.");
        columns = string.Join(", ", names);
        select = "SELECT " + string.Join(", ", fields.Select((field, i) => $"{Quote(field.Alias)} AS {names[i]}"))
            + $" FROM ({statement.Text}) AS source_rows";
        var types = PostgresQueryRowsReader.ParameterTypes(artifact);
        parameters = [.. statement.Parameters.Select((parameter, i) =>
            new PostgresNpgsqlParameter(parameter.Value ?? DBNull.Value, types[i], false))];
        var revision = JsonSerializer.SerializeToUtf8Bytes(new { artifact.Fingerprint.Value,
            Input = statement.Parameters.Select(p => p.Value) });
        owner = "cohesive.materializedRows/v1/" + Convert.ToHexString(SHA256.HashData(revision));
    }

    static string Quote(string name) => PostgresSqlDialect.Identifier(name).ToSql(PostgresSqlDialect.Instance);

    /// <summary>Rebuild from one source statement snapshot and commit before returning. Failed writes roll back.
    /// A table owned by another declaration or input is rejected before any of its rows can be replaced.</summary>
    public async Task ResyncAsync(CancellationToken cancellationToken = default)
    {
        if (System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Materialized row refresh owns its publication transaction.");
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Serialize first creation as well as refresh. Acquire the lock before taking the source snapshot.
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))";
        command.Parameters.AddWithValue(table);
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "SELECT to_regclass($1)::oid";
        var exists = await command.ExecuteScalarAsync(cancellationToken) is not DBNull;
        command.Parameters.Clear();
        if (!exists)
        {
            command.CommandText = $"CREATE TABLE {table} AS {select} WITH DATA";
            PostgresNpgsqlExecution.BindParameters(command, parameters, [], true);
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.Parameters.Clear();
            // The ownership marker contains only a fixed prefix and a hexadecimal digest.
            command.CommandText = $"COMMENT ON TABLE {table} IS '{owner}'";
        }
        else
        {
            command.CommandText = $"LOCK TABLE {table} IN EXCLUSIVE MODE";
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "SELECT obj_description(to_regclass($1), 'pg_class')";
            command.Parameters.AddWithValue(table);
            var existing = await command.ExecuteScalarAsync(cancellationToken);
            command.Parameters.Clear();
            if (!Equals(existing, owner))
                throw new InvalidOperationException($"Materialization binding differs for {table}.");
            command.CommandText = $"DELETE FROM {table}";
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = $"INSERT INTO {table} ({columns}) {select}";
            PostgresNpgsqlExecution.BindParameters(command, parameters, [], true);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
