using System.Collections.Immutable;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

// Native catalog requirements shared by product projection and storage-owned attachments.
internal sealed class PostgresCatalogSchema(ImmutableArray<PostgresCatalogSchema.Table> tables)
{
    internal const string PrimaryKey = "p";
    internal const string UniqueConstraint = "u";

    internal sealed record Column(string Name, string Type, bool Required, string? Collation = null, string? Identity = null);
    internal sealed record Key(ImmutableArray<string> Columns, string Kind = "", bool NullsDistinct = false);
    internal sealed record Reference(ImmutableArray<string> Columns, string Target, ImmutableArray<string> TargetColumns, string DiagnosticColumn);
    internal sealed record Check(string Field, string Operator, string Literal);
    internal sealed record Table(string Sql, ImmutableArray<Column> Columns, ImmutableArray<Key> Keys,
        ImmutableArray<Reference> References = default, ImmutableArray<Check> Checks = default);
    internal sealed record Admission(int MissingCount, Dictionary<string, HashSet<string>> FenceNames);

    internal async Task<Admission> ValidateAsync(NpgsqlDataSource database, PostgresSchemaInitialization policy, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(database);
        await using var batch = database.CreateBatch();
        Add("SELECT set_config('standard_conforming_strings','on',true)");
        var resultCounts = new int[tables.Length];
        var tableIndex = 0;
        foreach (var table in tables)
        {
            var start = batch.BatchCommands.Count;
            Add("SELECT to_regclass($1) IS NOT NULL", table.Sql);
            Add("""
                SELECT a.attname,format_type(a.atttypid,a.atttypmod),a.attnotnull,c.collname,n.nspname,a.attidentity::text
                FROM pg_attribute a LEFT JOIN pg_collation c ON c.oid=a.attcollation
                LEFT JOIN pg_namespace n ON n.oid=c.collnamespace
                WHERE a.attrelid=to_regclass($1) AND a.attnum>0 AND NOT a.attisdropped
                """, table.Sql);
            Add($"""
                SELECT array_agg(a.attname::text ORDER BY k.ordinality),coalesce(c.contype::text,''),index_class.relname,
                    NOT COALESCE((to_jsonb(i)->>'indnullsnotdistinct')::boolean,false)
                FROM pg_index i JOIN pg_class index_class ON index_class.oid=i.indexrelid
                CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum,ordinality)
                JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
                LEFT JOIN pg_constraint c ON c.conindid=i.indexrelid AND c.contype IN ('{PrimaryKey}','{UniqueConstraint}')
                WHERE i.indrelid=to_regclass($1) AND i.indisunique AND i.indisvalid AND i.indisready AND i.indimmediate
                  AND i.indpred IS NULL AND i.indexprs IS NULL AND k.ordinality<=i.indnkeyatts
                GROUP BY i.indexrelid,c.contype,index_class.relname,NOT COALESCE((to_jsonb(i)->>'indnullsnotdistinct')::boolean,false)
                """, table.Sql);
            if (!table.References.IsDefaultOrEmpty)
                foreach (var reference in table.References)
                    Add("""
                        SELECT EXISTS(SELECT 1 FROM pg_constraint c WHERE c.contype='f' AND c.conrelid=to_regclass($1)
                        AND c.confrelid=to_regclass($2) AND c.convalidated AND NOT c.condeferrable
                        AND c.confupdtype='a' AND c.confdeltype='a' AND c.confmatchtype='s'
                        AND (SELECT array_agg(a.attname::text ORDER BY k.n) FROM unnest(c.conkey) WITH ORDINALITY k(id,n)
                          JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.id)=$3
                        AND (SELECT array_agg(a.attname::text ORDER BY k.n) FROM unnest(c.confkey) WITH ORDINALITY k(id,n)
                          JOIN pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.id)=$4)
                        """, table.Sql, reference.Target, reference.Columns.ToArray(), reference.TargetColumns.ToArray());
            if (!table.Checks.IsDefaultOrEmpty)
                foreach (var check in table.Checks)
                    Add("""
                        SELECT EXISTS(SELECT 1 FROM pg_constraint c WHERE c.contype='c'
                          AND c.conrelid=to_regclass($1) AND c.convalidated AND NOT c.connoinherit
                          AND pg_get_expr(c.conbin,c.conrelid) = format('(%I %s %s)', $2::text, $3::text, $4::text))
                        """, table.Sql, check.Field, check.Operator, check.Literal);
            resultCounts[tableIndex++] = batch.BatchCommands.Count - start;
        }
        await using var reader = await batch.ExecuteReaderAsync(token).ConfigureAwait(false);
        await reader.NextResultAsync(token).ConfigureAwait(false);
        var missing = 0;
        var fences = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        tableIndex = 0;
        foreach (var table in tables)
        {
            var resultCount = resultCounts[tableIndex++];
            await reader.ReadAsync(token).ConfigureAwait(false);
            var exists = reader.GetBoolean(0);
            if (!exists)
            {
                if (policy == PostgresSchemaInitialization.ValidateOnly) throw Failure(Diagnostic.TableMissing, table.Sql);
                missing++;
                for (var i = 0; i < resultCount; i++) await reader.NextResultAsync(token).ConfigureAwait(false);
                continue;
            }
            await reader.NextResultAsync(token).ConfigureAwait(false);
            var found = new Dictionary<string, (string Type, bool Required, string? Collation, string? Schema, string Identity)>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                found.Add(reader.GetString(0), (reader.GetString(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
            foreach (var column in table.Columns)
            {
                if (!found.TryGetValue(column.Name, out var actual) || actual.Type != column.Type || actual.Required != column.Required
                    || column.Collation is { } collation && (actual.Collation != collation || actual.Schema != "pg_catalog"))
                    throw Failure(Diagnostic.ColumnMismatch, table.Sql, column.Name);
                if (column.Identity is { } identity && actual.Identity != identity) throw Failure(Diagnostic.IdentityMismatch, table.Sql, column.Name);
            }
            if (found.Count != table.Columns.Length) throw Failure(Diagnostic.UndeclaredColumns, table.Sql);
            await reader.NextResultAsync(token).ConfigureAwait(false);
            var matched = new bool[table.Keys.Length];
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var columns = reader.GetFieldValue<string[]>(0);
                for (var i = 0; i < table.Keys.Length; i++)
                {
                    var key = table.Keys[i];
                    if (columns.Length != key.Columns.Length || !key.Columns.All(columns.Contains)
                        || key.NullsDistinct && !reader.GetBoolean(3)) continue;
                    // Admission requires the declared constraint kind. Conflict recognition also includes
                    // redundant immediate unique indexes over exactly the same columns/null semantics.
                    matched[i] |= key.Kind.Length == 0 || key.Kind == reader.GetString(1);
                    names.Add(reader.GetString(2));
                }
            }
            if (matched.Any(value => !value)) throw Failure(Diagnostic.KeyMismatch, table.Sql);
            fences.Add(table.Sql, names);
            await reader.NextResultAsync(token).ConfigureAwait(false);
            if (!table.References.IsDefaultOrEmpty)
                foreach (var reference in table.References)
                {
                    await reader.ReadAsync(token).ConfigureAwait(false);
                    if (!reader.GetBoolean(0)) throw Failure(Diagnostic.ReferenceMismatch, table.Sql, reference.DiagnosticColumn);
                    await reader.NextResultAsync(token).ConfigureAwait(false);
                }
            if (!table.Checks.IsDefaultOrEmpty)
                foreach (var check in table.Checks)
                {
                    await reader.ReadAsync(token).ConfigureAwait(false);
                    if (!reader.GetBoolean(0)) throw Failure(Diagnostic.CheckMismatch, table.Sql, check.Field);
                    await reader.NextResultAsync(token).ConfigureAwait(false);
                }
        }
        return new(missing, fences);

        void Add(string sql, params object[] parameters)
        {
            var command = new NpgsqlBatchCommand(sql);
            foreach (var value in parameters) command.Parameters.AddWithValue(value);
            batch.BatchCommands.Add(command);
        }
    }
    sealed class Diagnostic
    {
        Diagnostic(string code, string remedy) { Code = code; Remedy = remedy; }
        internal string Code { get; }
        internal string Remedy { get; }
        internal static readonly Diagnostic TableMissing = new("postgres.schema.tableMissing", "Create the table with a reviewed migration or explicitly select CreateIfMissing for initial bootstrap.");
        internal static readonly Diagnostic ColumnMismatch = new("postgres.schema.columnMismatch", "Migrate the column to its declared type, nullability and collation; creation does not repair existing columns.");
        internal static readonly Diagnostic UndeclaredColumns = new("postgres.schema.undeclaredColumns", "Reconcile extra columns with the declared mapping using a reviewed migration.");
        internal static readonly Diagnostic KeyMismatch = new("postgres.schema.keyMismatch", "Restore the declared immediate primary/unique key and required NULL semantics; partial or deferred keys do not qualify.");
        internal static readonly Diagnostic IdentityMismatch = new("postgres.schema.identityMismatch", "Restore the declared generated-always identity column using a reviewed migration.");
        internal static readonly Diagnostic ReferenceMismatch = new("postgres.schema.referenceMismatch", "Restore the validated, non-deferrable foreign key with the declared target columns and NO ACTION behavior.");
        internal static readonly Diagnostic CheckMismatch = new("postgres.schema.checkMismatch", "Restore and validate the declared CHECK predicate using a reviewed migration.");
    }

    static PostgresSchemaValidationException Failure(Diagnostic diagnostic, string table, string? column = null) =>
        new(diagnostic.Code, table,
            $"Schema {table}{(column is null ? "" : " column " + column)} does not match the declaration ({diagnostic.Code}). {diagnostic.Remedy}", column);
}
