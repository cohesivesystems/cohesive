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
    readonly PostgresCatalogSchema catalog;
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
        catalog = new([.. tables.Select(table => new PostgresCatalogSchema.Table(table.Sql,
            [.. table.Columns.Select(column => new PostgresCatalogSchema.Column(column.Name, column.Type, column.Required, column.Collation))],
            [new([table.Partition, table.Identity])],
            [.. references.Where(reference => reference.Source == table).Select(reference => new PostgresCatalogSchema.Reference(
                [table.Partition, reference.Field], reference.Target.Sql, [reference.Target.Partition, reference.Target.Identity]))],
            [.. checks.Where(check => check.Table == table).Select(check => new PostgresCatalogSchema.Check(check.Field, check.Operator, check.DeparsedLiteral))]))]);
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

    internal async Task<int> ValidateAsync(NpgsqlDataSource database, PostgresSchemaInitialization initialization, CancellationToken cancellationToken) =>
        (await catalog.ValidateAsync(database, initialization, cancellationToken).ConfigureAwait(false)).MissingCount;

    static string Quote(string value) => PostgresSqlDialect.Identifier(value).ToSql(PostgresSqlDialect.Instance);
}
