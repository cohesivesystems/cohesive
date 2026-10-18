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
/// foreign keys. Additional native indexes/checks/defaults are outside this contract. Creation never alters
/// existing tables; incompatible schemas require a reviewed migration. Cyclic FK graphs require native migration.</remarks>
public sealed class PostgresEntitySchema
{
    sealed record Column(string Name, string Type, bool Required, string? Collation);
    sealed record Table(QualifiedShapeId Shape, string Sql, ImmutableArray<Column> Columns, string Partition, string Identity);
    sealed record Reference(Table Source, string Field, Table Target);
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
            refs.Add(new(source, field.Column.Value, target));
        }
        references = refs.ToImmutable();
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
            ddl.Append("CREATE TABLE IF NOT EXISTS ").Append(table.Sql).Append(" (\n  ").AppendJoin(",\n  ", clauses).AppendLine("\n);");
        }
        CreateSql = ddl.ToString();
    }
    /// <summary>Deterministic native initial DDL; contains no ALTER, backfill or destructive migration.</summary>
    public string CreateSql { get; }

    /// <summary>Validates the projected contract against native catalog metadata without schema changes.</summary>
    /// <param name="database">Borrowed data source.</param>
    /// <param name="cancellationToken">Cancellation of metadata reads.</param>
    /// <returns>Completion after all declared tables and references validate.</returns>
    /// <exception cref="PostgresSchemaValidationException">A table, column, key or foreign key requires migration.</exception>
    /// <exception cref="NpgsqlException">Metadata access fails.</exception>
    public async Task ValidateAsync(NpgsqlDataSource database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        foreach (var table in tables)
        {
            await using var columns = database.CreateCommand("""
                SELECT a.attname,format_type(a.atttypid,a.atttypmod),a.attnotnull,c.collname,n.nspname
                FROM pg_attribute a LEFT JOIN pg_collation c ON c.oid=a.attcollation
                LEFT JOIN pg_namespace n ON n.oid=c.collnamespace
                WHERE a.attrelid=to_regclass($1) AND a.attnum>0 AND NOT a.attisdropped
                """);
            columns.Parameters.AddWithValue(table.Sql);
            var found = new Dictionary<string, (string Type, bool Required, string? Collation, string? Schema)>();
            await using (var reader = await columns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    found.Add(reader.GetString(0), (reader.GetString(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            foreach (var column in table.Columns)
                if (!found.TryGetValue(column.Name, out var actual) || actual.Type != column.Type || actual.Required != column.Required
                    || column.Collation is { } collation && (actual.Collation != collation || actual.Schema != "pg_catalog"))
                    throw new PostgresSchemaValidationException("postgres.schema.columnMismatch", table.Sql, $"Product schema {table.Sql} column {column.Name} requires migration.", column.Name);
            if (found.Count != table.Columns.Length) throw new PostgresSchemaValidationException("postgres.schema.undeclaredColumns", table.Sql, $"Product schema {table.Sql} has undeclared columns.");
            await using var keys = database.CreateCommand("""
                SELECT array_agg(a.attname::text ORDER BY k.ordinality)
                FROM pg_index i CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum,ordinality)
                JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
                WHERE i.indrelid=to_regclass($1) AND i.indisunique AND i.indisvalid AND i.indisready AND i.indimmediate
                  AND i.indpred IS NULL AND i.indexprs IS NULL AND k.ordinality<=i.indnkeyatts
                GROUP BY i.indexrelid
                """);
            keys.Parameters.AddWithValue(table.Sql);
            var matched = false;
            await using (var reader = await keys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    matched |= reader.GetFieldValue<string[]>(0).ToHashSet(StringComparer.Ordinal).SetEquals([table.Partition, table.Identity]);
            if (!matched) throw new PostgresSchemaValidationException("postgres.schema.keyMismatch", table.Sql, $"Product schema {table.Sql} requires immediate partition/identity uniqueness.");
        }
        foreach (var reference in references)
        {
            await using var fk = database.CreateCommand("""
                SELECT EXISTS(SELECT 1 FROM pg_constraint c WHERE c.contype='f' AND c.conrelid=to_regclass($1)
                AND c.confrelid=to_regclass($2) AND c.convalidated AND NOT c.condeferrable
                AND c.confupdtype='a' AND c.confdeltype='a' AND c.confmatchtype='s'
                AND (SELECT array_agg(a.attname::text ORDER BY k.n) FROM unnest(c.conkey) WITH ORDINALITY k(id,n)
                  JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.id)=$3
                AND (SELECT array_agg(a.attname::text ORDER BY k.n) FROM unnest(c.confkey) WITH ORDINALITY k(id,n)
                  JOIN pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.id)=$4)
                """);
            fk.Parameters.AddWithValue(reference.Source.Sql); fk.Parameters.AddWithValue(reference.Target.Sql);
            fk.Parameters.AddWithValue(new[] { reference.Source.Partition, reference.Field });
            fk.Parameters.AddWithValue(new[] { reference.Target.Partition, reference.Target.Identity });
            if (await fk.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                throw new PostgresSchemaValidationException("postgres.schema.referenceMismatch", reference.Source.Sql, $"Product schema {reference.Source.Sql} reference {reference.Field} requires a validated partition-scoped foreign key.", reference.Field);
        }
    }
    static string Quote(string value) => PostgresSqlDialect.Identifier(value).ToSql(PostgresSqlDialect.Instance);
}
