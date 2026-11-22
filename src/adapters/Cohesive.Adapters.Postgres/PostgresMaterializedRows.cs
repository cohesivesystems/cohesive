using System.Collections.Immutable;
using System.Linq.Expressions;
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
    public PostgresMaterializedRows<TRow> MaterializeRows<TInput, TRow>(RelationQuery<TInput, TRow[]> query,
        TInput input, string table, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsDirectRowArray)
            throw new NotSupportedException("Materialized lookups require a direct row array. Use BuildArrayQuery or Queries.Array.");
        var artifact = bindings.Prepare(query.CompilationRequest);
        var statement = artifact.Bind(new Dictionary<Cohesive.Relations.IR.QueryParameterId, ObservationValue>
            { [query.Parameter] = ObservationValue.FromObject(input) });
        return new(runtime.DataSource, schema, table, artifact, statement, query.AssembleResult);
    }
}

/// <summary>A synchronous, full-snapshot PostgreSQL target. Resync is repeatable, creates a missing
/// target, and atomically replaces its rows using the compiled source SELECT on the database.</summary>
public sealed class PostgresMaterializedRows<TRow>
{
    readonly NpgsqlDataSource database;
    readonly string table;
    readonly string owner;
    readonly string columns;
    readonly string select;
    readonly ImmutableArray<PostgresNpgsqlParameter> parameters;

    readonly ImmutableArray<PostgresRelationQueryResultFieldBinding> fields;
    readonly ImmutableArray<PostgresRelationQueryScalarType> resultTypes;
    readonly Func<ImmutableArray<ObservationValue>, TRow[]> assemble;
    readonly string[] names;

    internal PostgresMaterializedRows(NpgsqlDataSource database, string schema, string name,
        PostgresRelationQueryCompiledArtifact artifact, SqlStatement statement, Func<ImmutableArray<ObservationValue>, TRow[]> assemble)
    {
        this.database = database;
        table = Quote(schema) + "." + Quote(name);
        this.assemble = assemble;
        fields = artifact.ResultFields;
        resultTypes = [.. fields.Select(f => PostgresQueryRowsReader.RequireScalar(f.ValueContract))];
        names = fields.Select(field => field.Field.Path.TryGetDirectFieldName(out var member)
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

    /// <summary>Prepare an equality lookup on one projected field. Reads use the physical target and
    /// the original query's typed row assembly. Nullable keys match stored SQL nulls.</summary>
    public PostgresMaterializedLookup<TKey, TRow> Lookup<TKey>(Expression<Func<TRow, TKey>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var path = FieldPath.Capture(Expression.Lambda<Func<TRow, object?>>(
            Expression.Convert(selector.Body, typeof(object)), selector.Parameters));
        var index = Array.FindIndex(fields.ToArray(), field => field.Field.Path.Equals(path));
        if (index < 0) throw new ArgumentException($"Field '{path}' is not projected by this target.", nameof(selector));
        var sql = $"SELECT {columns} FROM {table} WHERE {names[index]}";
        return new(async (key, token) =>
        {
            var value = PostgresRelationQueryValueConverter.Convert(ObservationValue.FromObject(key),
                fields[index].Encoding, names[index]);
            PostgresNpgsqlCommand read = value is null
                ? new(sql + " IS NULL", [], resultTypes, long.MaxValue)
                : new(sql + " = $1", [new(value, resultTypes[index], false)], resultTypes, long.MaxValue);
            await using var connection = await database.OpenConnectionAsync(token);
            await using var tx = await connection.BeginTransactionAsync(token);
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            await RequireOwnerAsync(command, "ACCESS SHARE", token);
            var result = await PostgresNpgsqlExecution.CreateTransactionExecutor(connection, tx)(read, token);
            var rows = result.Rows.Select(row => ObservationValue.FromFieldAssignments(fields.Select((field, i) =>
                new KeyValuePair<FieldPath, ObservationValue>(field.Field.Path, row[i] is { } raw
                    ? PostgresRelationQueryScalarCatalog.ToObservationValue(raw, resultTypes[i]) : ObservationValue.Null))));
            await tx.CommitAsync(token);
            return assemble([.. rows]);
        });
    }

    async Task RequireOwnerAsync(NpgsqlCommand command, string lockMode, CancellationToken token)
    {
        command.CommandText = $"LOCK TABLE {table} IN {lockMode} MODE";
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT obj_description(to_regclass($1), 'pg_class')";
        command.Parameters.AddWithValue(table);
        var existing = await command.ExecuteScalarAsync(token);
        command.Parameters.Clear();
        if (!Equals(existing, owner))
            throw new InvalidOperationException($"Materialization binding differs for {table}.");
    }

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
            await RequireOwnerAsync(command, "EXCLUSIVE", cancellationToken);
            command.CommandText = $"DELETE FROM {table}";
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = $"INSERT INTO {table} ({columns}) {select}";
            PostgresNpgsqlExecution.BindParameters(command, parameters, [], true);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>A prepared typed equality lookup over a persisted PostgreSQL projection.</summary>
public sealed class PostgresMaterializedLookup<TKey, TRow>
{
    readonly Func<TKey, CancellationToken, Task<TRow[]>> read;
    internal PostgresMaterializedLookup(Func<TKey, CancellationToken, Task<TRow[]>> read) => this.read = read;

    /// <summary>Read the matching typed rows from one committed publication; ordering is unspecified.</summary>
    public Task<TRow[]> ReadAsync(TKey key, CancellationToken cancellationToken = default) => read(key, cancellationToken);
}
