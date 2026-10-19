using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Text;
using Cohesive.Adapters.Sql;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Model;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Native configuration for the schema projected from exact entity mappings.</summary>
/// <remarks>Foreign keys are opt-in storage enforcement, not inferred from query correlation.
/// This builder is used only during definition completion; retained instances reject late edits.</remarks>
public sealed class PostgresEntitySchemaBuilder
{
    internal readonly List<RelationshipDefinition> References = [];
    internal readonly Dictionary<(QualifiedShapeId, string), string> Collations = [];
    internal sealed record CheckDeclaration(QualifiedShapeId Shape, string Field, SqlBinaryOperator Comparison, object Value);
    internal readonly List<CheckDeclaration> Checks = [];
    bool frozen;
    internal PostgresEntitySchemaBuilder() { }
    internal void Freeze() => frozen = true;
    void RequireMutable() { if (frozen) throw new InvalidOperationException("Schema configuration is frozen."); }

    /// <summary>Enforces a canonical reference using partition plus entity identity in this database.</summary>
    /// <typeparam name="TSource">Source entity.</typeparam>
    /// <typeparam name="TTarget">Target entity.</typeparam>
    /// <param name="relationship">Exact relationship; both endpoints must be mapped in this database.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">Relationship is null.</exception>
    /// <exception cref="InvalidOperationException">The builder is frozen.</exception>
    public PostgresEntitySchemaBuilder ForeignKey<TSource, TTarget>(RelationQueryExpressionRelationship<TSource, TTarget> relationship)
        where TSource : notnull where TTarget : notnull
    {
        RequireMutable();
        ArgumentNullException.ThrowIfNull(relationship);
        References.Add(relationship.Definition);
        return this;
    }

    /// <summary>Declares a native CHECK comparing a mapped text or Int32 field with a fixed value.</summary>
    /// <typeparam name="T">Entity type.</typeparam>
    /// <typeparam name="TValue">String or Int32 field/value type.</typeparam>
    /// <param name="entity">Exact mapped entity.</param>
    /// <param name="field">Direct canonical field selector.</param>
    /// <param name="comparison">Equality, inequality or ordered comparison.</param>
    /// <param name="value">Non-null constant captured at definition time; never executed as application code.</param>
    /// <returns>This builder.</returns>
    /// <remarks>SQL CHECK permits NULL; semantic field nullability remains separately enforced.
    /// Other scalar types and compound predicates require an explicit native migration.</remarks>
    /// <exception cref="ArgumentException">Selector, value type or comparison is unsupported.</exception>
    /// <exception cref="InvalidOperationException">The builder is frozen.</exception>
    public PostgresEntitySchemaBuilder Check<T, TValue>(DomainEntity<T> entity, Expression<Func<T, TValue>> field,
        SqlBinaryOperator comparison, TValue value) where T : notnull
    {
        RequireMutable();
        ArgumentNullException.ThrowIfNull(entity);
        if (value is not string and not int || comparison is not (SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterThanOrEqual or SqlBinaryOperator.LessThan or SqlBinaryOperator.LessThanOrEqual))
            throw new ArgumentException("Checks require a text or Int32 value and a scalar comparison.");
        ArgumentNullException.ThrowIfNull(field);
        var boxed = Expression.Lambda<Func<T, object?>>(Expression.Convert(field.Body, typeof(object)), field.Parameters);
        if (!FieldPath.Capture(boxed).TryGetDirectFieldName(out var name)) throw new ArgumentException("A direct field is required.", nameof(field));
        if (value is string text) SqlUtf8.RequireText(text, nameof(value));
        Checks.Add(new(entity.Definition.StateShape.QualifiedId, name, comparison, value));
        return this;
    }

    /// <summary>Attaches native collation to a mapped text column without repeating its physical name.</summary>
    /// <typeparam name="T">Entity type.</typeparam>
    /// <param name="entity">Exact mapped entity.</param>
    /// <param name="field">Direct canonical field selector.</param>
    /// <param name="collation">Native collation identifier in pg_catalog.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">Field is not direct, duplicated, or collation is invalid.</exception>
    /// <exception cref="InvalidOperationException">The builder is frozen.</exception>
    public PostgresEntitySchemaBuilder Collation<T>(DomainEntity<T> entity, Expression<Func<T, object?>> field, string collation) where T : notnull
    {
        RequireMutable();
        ArgumentNullException.ThrowIfNull(entity);
        if (!FieldPath.Capture(field).TryGetDirectFieldName(out var name)) throw new ArgumentException("A direct field is required.", nameof(field));
        _ = PostgresSqlDialect.Identifier(collation);
        Collations.Add((entity.Definition.StateShape.QualifiedId, name), collation);
        return this;
    }
}

/// <summary>Inspectable desired product schema projected from canonical fields and native mappings.</summary>
/// <remarks>Validates mapped columns, explicit collations, immediate partition/identity uniqueness and selected
/// foreign keys and scalar checks. Additional native indexes/defaults are outside this contract. Creation never alters
/// existing tables; incompatible schemas require a reviewed migration. Cyclic FK graphs require native migration.</remarks>
public sealed class PostgresEntitySchema
{
    sealed record Column(string Name, string Type, bool Required, string? Collation);
    sealed record Table(QualifiedShapeId Shape, string Sql, ImmutableArray<Column> Columns, string Partition, string Identity);
    sealed record Reference(Table Source, string Field, Table Target);
    sealed record Check(Table Table, string Field, string Operator, string Value, bool Text)
    {
        public string Literal => Text ? QuoteLiteral(Value, escape: true) + "::text" : Value;
        public string DeparsedLiteral => Text ? QuoteLiteral(Value, escape: false) + "::text"
            : Value.StartsWith('-') ? QuoteLiteral(Value, escape: false) + "::integer" : Value;
        static string QuoteLiteral(string value, bool escape) => (escape ? "E'" : "'")
            + (escape ? value.Replace("\\", "\\\\", StringComparison.Ordinal) : value).Replace("'", "''", StringComparison.Ordinal) + "'";
    }
    readonly ImmutableArray<Check> checks;
    readonly ImmutableArray<Table> tables;
    readonly ImmutableArray<Reference> references;
    internal PostgresEntitySchema(Dictionary<QualifiedShapeId, (EntityDefinition Entity, PostgresEntityRepositoryMapping Mapping)> mappings,
        Action<PostgresEntitySchemaBuilder>? configure)
    {
        if (mappings.Count == 0) throw new ArgumentException("Projected product schema requires at least one mapped entity.");
        var builder = new PostgresEntitySchemaBuilder();
        try { configure?.Invoke(builder); } finally { builder.Freeze(); }
        var selected = new Dictionary<QualifiedShapeId, Table>();
        var physical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (shape, (entity, mapping)) in mappings.OrderBy(x => x.Value.Mapping.Table.ToSql(PostgresSqlDialect.Instance), StringComparer.Ordinal))
        {
            var sql = mapping.Table.ToSql(PostgresSqlDialect.Instance);
            if (!physical.Add(sql)) throw new ArgumentException($"Multiple entities map to table {sql}.");
            var columns = ImmutableArray.CreateBuilder<Column>();
            foreach (var field in mapping.Fields.OrderBy(x => x.Column.Value, StringComparer.Ordinal))
            {
                var semantic = entity.Fields.Single(x => x.Name.Value == field.FieldName);
                builder.Collations.TryGetValue((shape, field.FieldName), out var collation);
                if (collation is not null && field.ScalarType != PostgresRelationQueryScalarType.Text)
                    throw new ArgumentException($"Collation requires a text field: {field.FieldName}.");
                columns.Add(new(field.Column.Value, PostgresRelationQueryScalarCatalog.SchemaTypeName(field.ScalarType), semantic.Nullability == FieldNullability.NonNullable, collation));
            }
            columns.Add(new(mapping.VersionColumn.Value, "bigint", true, null));
            if (mapping.PartitionField == mapping.IdentityField)
                throw new ArgumentException($"Projected schema {sql} requires distinct partition and identity fields.");
            selected.Add(shape, new(shape, sql, columns.ToImmutable(), mapping.FieldByName[mapping.PartitionField].Column.Value,
                mapping.FieldByName[mapping.IdentityField].Column.Value));
        }
        foreach (var ((shape, field), _) in builder.Collations)
            if (!mappings.TryGetValue(shape, out var mapping) || !mapping.Mapping.FieldByName.ContainsKey(field))
                throw new ArgumentException($"Collation addresses an unmapped entity field: {field}.");
        var refs = ImmutableArray.CreateBuilder<Reference>();
        var identities = new HashSet<RelationshipId>();
        foreach (var relationship in builder.References.OrderBy(x => x.Id.Value, StringComparer.Ordinal))
        {
            if (!identities.Add(relationship.Id)) throw new ArgumentException($"Foreign key selected twice: {relationship.Id.Value}.");
            if (!selected.TryGetValue(relationship.SourceShape, out var source) || !selected.TryGetValue(relationship.TargetShape, out var target))
                throw new ArgumentException($"Foreign key {relationship.Id.Value} crosses an unmapped database boundary.");
            if (relationship.TargetKey is not ObservationIdentityRelationshipTargetKey
                || !relationship.SourceReference.TryGetDirectFieldName(out var name)
                || !mappings[relationship.SourceShape].Mapping.FieldByName.TryGetValue(name, out var field)
                || field.ScalarType != PostgresRelationQueryScalarType.Text)
                throw new ArgumentException($"Foreign key {relationship.Id.Value} requires a direct text observation-identity reference.");
            if (field.Column.Value == source.Partition)
                throw new ArgumentException($"Foreign key {relationship.Id.Value} cannot use the partition as its entity reference.");
            RequireCompatible(source, field.Column.Value, target, target.Identity);
            RequireCompatible(source, source.Partition, target, target.Partition);
            refs.Add(new(source, field.Column.Value, target));
            void RequireCompatible(Table from, string fromName, Table to, string toName)
            {
                var left = from.Columns.Single(x => x.Name == fromName);
                var right = to.Columns.Single(x => x.Name == toName);
                if (left.Type != right.Type || (left.Collation ?? "default") != (right.Collation ?? "default"))
                    throw new ArgumentException($"Foreign key {relationship.Id.Value}: {from.Sql}.{fromName} and {to.Sql}.{toName} must have matching types and collations.");
            }
        }
        references = refs.ToImmutable();
        checks = builder.Checks.Select(check =>
        {
            if (!mappings.TryGetValue(check.Shape, out var mapping) || !mapping.Mapping.FieldByName.TryGetValue(check.Field, out var field))
                throw new ArgumentException($"Check addresses an unmapped entity field: {check.Field}.");
            if (field.ScalarType != (check.Value is string ? PostgresRelationQueryScalarType.Text : PostgresRelationQueryScalarType.Int32))
                throw new ArgumentException($"Check value does not match field {check.Field}.");
            var op = SqlOperators.Text(check.Comparison);
            return new Check(selected[check.Shape], field.Column.Value, op,
                Convert.ToString(check.Value, System.Globalization.CultureInfo.InvariantCulture)!, check.Value is string);
        }).Distinct().OrderBy(x => x.Table.Sql, StringComparer.Ordinal).ThenBy(x => x.Field, StringComparer.Ordinal)
            .ThenBy(x => x.Operator, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal).ToImmutableArray();
        var ordered = ImmutableArray.CreateBuilder<Table>();
        var visited = new HashSet<QualifiedShapeId>();
        var active = new HashSet<QualifiedShapeId>();
        foreach (var table in selected.Values.OrderBy(x => x.Sql, StringComparer.Ordinal)) Visit(table);
        tables = ordered.ToImmutable();
        void Visit(Table table)
        {
            if (visited.Contains(table.Shape)) return;
            if (!active.Add(table.Shape)) throw new ArgumentException("Cyclic product foreign keys require an explicit native migration.");
            foreach (var reference in references.Where(x => x.Source == table && x.Target != table)) Visit(reference.Target);
            active.Remove(table.Shape); visited.Add(table.Shape); ordered.Add(table);
        }
        var ddl = new StringBuilder();
        foreach (var table in tables)
        {
            var clauses = table.Columns.Select(column => $"{Quote(column.Name)} {column.Type}" +
                (column.Collation is { } collation ? $" COLLATE pg_catalog.{Quote(collation)}" : "") + (column.Required ? " NOT NULL" : "")).ToList();
            clauses.Add($"PRIMARY KEY ({Quote(table.Partition)}, {Quote(table.Identity)})");
            foreach (var reference in references.Where(x => x.Source == table))
                clauses.Add($"FOREIGN KEY ({Quote(table.Partition)}, {Quote(reference.Field)}) REFERENCES {reference.Target.Sql} ({Quote(reference.Target.Partition)}, {Quote(reference.Target.Identity)})");
            foreach (var check in checks.Where(x => x.Table == table))
                clauses.Add($"CHECK ({Quote(check.Field)} {check.Operator} {check.Literal})");
            ddl.Append("CREATE TABLE IF NOT EXISTS ").Append(table.Sql).Append(" (\n  ").AppendJoin(",\n  ", clauses).AppendLine("\n);");
        }
        CreateSql = ddl.ToString();
    }
    /// <summary>Deterministic native initial DDL; contains no ALTER, backfill or destructive migration.</summary>
    public string CreateSql { get; }

    /// <summary>Validates the projected contract against native catalog metadata without schema changes.</summary>
    /// <param name="database">Borrowed data source.</param>
    /// <param name="cancellationToken">Cancellation of metadata reads.</param>
    /// <returns>Completion after all declared tables, references and checks validate in one native batch.</returns>
    /// <exception cref="PostgresSchemaValidationException">A table, column, key, foreign key or declared check requires migration.</exception>
    /// <exception cref="NpgsqlException">Metadata access fails.</exception>
    public Task ValidateAsync(NpgsqlDataSource database, CancellationToken cancellationToken = default) =>
        ValidateAsync(database, PostgresSchemaInitialization.ValidateOnly, cancellationToken);

    internal async Task<HashSet<string>> ValidateAsync(NpgsqlDataSource database, PostgresSchemaInitialization initialization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await using var batch = database.CreateBatch();
        // Normalize deparsing locally to this implicit batch transaction, never the pooled session.
        batch.BatchCommands.Add(new("SELECT set_config('standard_conforming_strings','on',true)"));
        foreach (var table in tables)
        {
            Add("SELECT to_regclass($1) IS NOT NULL", table.Sql);
            Add("""
                SELECT a.attname,format_type(a.atttypid,a.atttypmod),a.attnotnull,c.collname,n.nspname
                FROM pg_attribute a LEFT JOIN pg_collation c ON c.oid=a.attcollation
                LEFT JOIN pg_namespace n ON n.oid=c.collnamespace
                WHERE a.attrelid=to_regclass($1) AND a.attnum>0 AND NOT a.attisdropped
                """, table.Sql);
            Add("""
                SELECT array_agg(a.attname::text ORDER BY k.ordinality)
                FROM pg_index i CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum,ordinality)
                JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
                WHERE i.indrelid=to_regclass($1) AND i.indisunique AND i.indisvalid AND i.indisready AND i.indimmediate
                  AND i.indpred IS NULL AND i.indexprs IS NULL AND k.ordinality<=i.indnkeyatts
                GROUP BY i.indexrelid
                """, table.Sql);
        }
        foreach (var reference in references)
            Add("""
                SELECT EXISTS(SELECT 1 FROM pg_constraint c WHERE c.contype='f' AND c.conrelid=to_regclass($1)
                AND c.confrelid=to_regclass($2) AND c.convalidated AND NOT c.condeferrable
                AND c.confupdtype='a' AND c.confdeltype='a' AND c.confmatchtype='s'
                AND (SELECT array_agg(a.attname::text ORDER BY k.n) FROM unnest(c.conkey) WITH ORDINALITY k(id,n)
                  JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.id)=$3
                AND (SELECT array_agg(a.attname::text ORDER BY k.n) FROM unnest(c.confkey) WITH ORDINALITY k(id,n)
                  JOIN pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.id)=$4)
                """, reference.Source.Sql, reference.Target.Sql,
                new[] { reference.Source.Partition, reference.Field }, new[] { reference.Target.Partition, reference.Target.Identity });
        foreach (var check in checks)
            Add("""
                SELECT EXISTS(SELECT 1 FROM pg_constraint c WHERE c.contype='c'
                  AND c.conrelid=to_regclass($1) AND c.convalidated AND NOT c.connoinherit
                  AND pg_get_expr(c.conbin,c.conrelid) = format('(%I %s %s)', $2::text, $3::text,
                    $4::text))
                """, check.Table.Sql, check.Field, check.Operator, check.DeparsedLiteral);
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.GetBoolean(0))
            {
                if (initialization == PostgresSchemaInitialization.ValidateOnly)
                    throw new PostgresSchemaValidationException("postgres.schema.tableMissing", table.Sql, $"Product table {table.Sql} is missing; create it or apply a reviewed migration.");
                missing.Add(table.Sql);
                // Consume this absent table's column and key results, then continue checking existing tables.
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            var found = new Dictionary<string, (string Type, bool Required, string? Collation, string? Schema)>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                found.Add(reader.GetString(0), (reader.GetString(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            foreach (var column in table.Columns)
                if (!found.TryGetValue(column.Name, out var actual) || actual.Type != column.Type || actual.Required != column.Required
                    || column.Collation is { } collation && (actual.Collation != collation || actual.Schema != "pg_catalog"))
                    throw new PostgresSchemaValidationException("postgres.schema.columnMismatch", table.Sql, $"Product schema {table.Sql} column {column.Name} requires migration.", column.Name);
            if (found.Count != table.Columns.Length) throw new PostgresSchemaValidationException("postgres.schema.undeclaredColumns", table.Sql, $"Product schema {table.Sql} has undeclared columns.");
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            var matched = false;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                matched |= reader.GetFieldValue<string[]>(0).ToHashSet(StringComparer.Ordinal).SetEquals([table.Partition, table.Identity]);
            if (!matched) throw new PostgresSchemaValidationException("postgres.schema.keyMismatch", table.Sql, $"Product schema {table.Sql} requires immediate partition/identity uniqueness.");
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var reference in references)
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.GetBoolean(0) && !missing.Contains(reference.Source.Sql))
                throw new PostgresSchemaValidationException("postgres.schema.referenceMismatch", reference.Source.Sql, $"Product schema {reference.Source.Sql} reference {reference.Field} requires a validated partition-scoped foreign key.", reference.Field);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var check in checks)
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.GetBoolean(0) && !missing.Contains(check.Table.Sql))
                throw new PostgresSchemaValidationException("postgres.schema.checkMismatch", check.Table.Sql,
                    $"Product schema {check.Table.Sql} check on {check.Field} requires migration.", check.Field);
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        }
        return missing;

        void Add(string sql, params object[] parameters)
        {
            var command = new NpgsqlBatchCommand(sql);
            foreach (var value in parameters) command.Parameters.AddWithValue(value);
            batch.BatchCommands.Add(command);
        }
    }
    static string Quote(string value) => PostgresSqlDialect.Identifier(value).ToSql(PostgresSqlDialect.Instance);
}
