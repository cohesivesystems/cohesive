using System.Collections.Immutable;
using System.Text;

namespace Cohesive.Adapters.Postgres;

/// <summary>Mutable, single-threaded builder for an injection-safe PostgreSQL <c>INSERT</c> command.</summary>
public sealed class PostgresSqlInsertBuilder
{
    readonly PostgresSqlQualifiedTable table;
    readonly List<PostgresSqlColumnValue> values = [];
    readonly HashSet<PostgresSqlIdentifier> valueColumns = [];
    readonly List<PostgresSqlReturningItem> returning = [];
    readonly HashSet<PostgresSqlIdentifier> returningAliases = [];
    ImmutableArray<PostgresSqlIdentifier> conflictColumns;
    ImmutableArray<PostgresSqlIdentifier> excludedUpdateColumns;

    /// <summary>Creates an insert builder for one physical table.</summary>
    /// <param name="table">Injection-safe physical table name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is <see langword="null"/>.</exception>
    public PostgresSqlInsertBuilder(PostgresSqlQualifiedTable table)
    {
        this.table = Guard.RequireNotNull(table);
    }

    /// <summary>Adds one inserted column and its value expression.</summary>
    /// <param name="columnName">Physical target-column identifier.</param>
    /// <param name="value">Value written to the column.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is invalid or already has an inserted value.</exception>
    public PostgresSqlInsertBuilder Value(string columnName, PostgresSqlExpression value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var column = new PostgresSqlIdentifier(columnName);
        if (!valueColumns.Add(column))
            throw new ArgumentException($"PostgreSQL insert column '{columnName}' is already present.", nameof(columnName));
        values.Add(new(column, value));
        return this;
    }

    /// <summary>
    /// Configures an upsert that replaces selected columns with their corresponding <c>EXCLUDED</c> values when the
    /// specified conflict key already exists.
    /// </summary>
    /// <param name="conflictColumns">One or more physical columns forming the conflict target.</param>
    /// <param name="excludedUpdateColumns">One or more inserted columns replaced from <c>EXCLUDED</c>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">A collection or column name is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A collection is empty or contains an invalid or repeated column.</exception>
    /// <exception cref="InvalidOperationException">Conflict behavior has already been configured.</exception>
    public PostgresSqlInsertBuilder OnConflictDoUpdate(
        IEnumerable<string> conflictColumns,
        IEnumerable<string> excludedUpdateColumns)
    {
        if (!this.conflictColumns.IsDefault)
            throw new InvalidOperationException("PostgreSQL insert conflict behavior has already been configured.");
        this.conflictColumns = CaptureIdentifiers(conflictColumns, nameof(conflictColumns));
        this.excludedUpdateColumns = CaptureIdentifiers(excludedUpdateColumns, nameof(excludedUpdateColumns));
        return this;
    }

    /// <summary>Adds one expression returned from the inserted or updated row.</summary>
    /// <param name="expression">Expression evaluated by the <c>RETURNING</c> clause.</param>
    /// <param name="alias">Unique result-column alias.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is invalid or repeated.</exception>
    public PostgresSqlInsertBuilder Returning(PostgresSqlExpression expression, string alias)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var identifier = new PostgresSqlIdentifier(alias);
        if (!returningAliases.Add(identifier))
            throw new ArgumentException($"PostgreSQL returning alias '{alias}' is already present.", nameof(alias));
        returning.Add(new(expression, identifier));
        return this;
    }

    /// <summary>Builds an immutable reusable PostgreSQL command template.</summary>
    /// <returns>Normalized SQL and deterministic positional-parameter slots.</returns>
    /// <exception cref="InvalidOperationException">
    /// No values are configured, or conflict and update columns do not refer to configured insert values.
    /// </exception>
    public PostgresSqlCommandTemplate BuildTemplate()
    {
        if (values.Count == 0)
            throw new InvalidOperationException("A PostgreSQL INSERT requires at least one column value.");
        ValidateConflictColumns();

        PostgresSqlRenderContext context = new();
        StringBuilder builder = new("INSERT INTO ");
        table.WriteTo(builder);
        builder.Append(" (");
        WriteIdentifiers(builder, values.Select(static value => value.Column));
        builder.Append(") VALUES (");
        WriteExpressions(builder, context, values.Select(static value => value.Value));
        builder.Append(')');

        if (!conflictColumns.IsDefault)
        {
            builder.Append(" ON CONFLICT (");
            WriteIdentifiers(builder, conflictColumns);
            builder.Append(") DO UPDATE SET ");
            for (var index = 0; index < excludedUpdateColumns.Length; index++)
            {
                if (index != 0)
                    builder.Append(", ");
                var column = excludedUpdateColumns[index];
                column.WriteQuoted(builder);
                builder.Append(" = EXCLUDED.");
                column.WriteQuoted(builder);
            }
        }

        PostgresSqlMutationWriter.WriteReturning(builder, context, returning);
        return new(builder.ToString(), context.Parameters);
    }

    void ValidateConflictColumns()
    {
        if (conflictColumns.IsDefault)
            return;
        foreach (var column in conflictColumns)
        {
            if (!valueColumns.Contains(column))
                throw new InvalidOperationException($"PostgreSQL conflict column '{column.Value}' has no configured insert value.");
        }
        foreach (var column in excludedUpdateColumns)
        {
            if (!valueColumns.Contains(column))
                throw new InvalidOperationException($"PostgreSQL excluded update column '{column.Value}' has no configured insert value.");
        }
    }

    static ImmutableArray<PostgresSqlIdentifier> CaptureIdentifiers(
        IEnumerable<string> columns,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(columns, parameterName);
        ImmutableArray<PostgresSqlIdentifier>.Builder captured = ImmutableArray.CreateBuilder<PostgresSqlIdentifier>();
        HashSet<PostgresSqlIdentifier> unique = [];
        foreach (var columnName in columns)
        {
            var column = new PostgresSqlIdentifier(columnName);
            if (!unique.Add(column))
                throw new ArgumentException($"PostgreSQL column '{columnName}' is repeated.", parameterName);
            captured.Add(column);
        }
        if (captured.Count == 0)
            throw new ArgumentException("At least one PostgreSQL column is required.", parameterName);
        return captured.ToImmutable();
    }

    static void WriteIdentifiers(StringBuilder builder, IEnumerable<PostgresSqlIdentifier> identifiers)
    {
        var index = 0;
        foreach (var identifier in identifiers)
        {
            if (index++ != 0)
                builder.Append(", ");
            identifier.WriteQuoted(builder);
        }
    }

    static void WriteExpressions(
        StringBuilder builder,
        PostgresSqlRenderContext context,
        IEnumerable<PostgresSqlExpression> expressions)
    {
        var index = 0;
        foreach (var expression in expressions)
        {
            if (index++ != 0)
                builder.Append(", ");
            expression.WriteTo(context, builder);
        }
    }
}

/// <summary>Mutable, single-threaded builder for an injection-safe, predicate-guarded PostgreSQL <c>UPDATE</c>.</summary>
public sealed class PostgresSqlUpdateBuilder
{
    readonly PostgresSqlQualifiedTable table;
    readonly List<PostgresSqlColumnValue> assignments = [];
    readonly HashSet<PostgresSqlIdentifier> assignmentColumns = [];
    readonly List<PostgresSqlExpression> predicates = [];
    readonly List<PostgresSqlReturningItem> returning = [];
    readonly HashSet<PostgresSqlIdentifier> returningAliases = [];

    /// <summary>Creates an update builder for one physical table.</summary>
    /// <param name="table">Injection-safe physical table name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is <see langword="null"/>.</exception>
    public PostgresSqlUpdateBuilder(PostgresSqlQualifiedTable table)
    {
        this.table = Guard.RequireNotNull(table);
    }

    /// <summary>Adds one target-column assignment.</summary>
    /// <param name="columnName">Physical target-column identifier.</param>
    /// <param name="value">Value assigned to the column.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is invalid or already assigned.</exception>
    public PostgresSqlUpdateBuilder Set(string columnName, PostgresSqlExpression value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var column = new PostgresSqlIdentifier(columnName);
        if (!assignmentColumns.Add(column))
            throw new ArgumentException($"PostgreSQL update column '{columnName}' is already assigned.", nameof(columnName));
        assignments.Add(new(column, value));
        return this;
    }

    /// <summary>Adds a required predicate combined with prior predicates using conjunction.</summary>
    /// <param name="predicate">Boolean predicate restricting updated rows.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public PostgresSqlUpdateBuilder Where(PostgresSqlExpression predicate)
    {
        predicates.Add(Guard.RequireNotNull(predicate));
        return this;
    }

    /// <summary>Adds one expression returned from each updated row.</summary>
    /// <param name="expression">Expression evaluated by the <c>RETURNING</c> clause.</param>
    /// <param name="alias">Unique result-column alias.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is invalid or repeated.</exception>
    public PostgresSqlUpdateBuilder Returning(PostgresSqlExpression expression, string alias)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var identifier = new PostgresSqlIdentifier(alias);
        if (!returningAliases.Add(identifier))
            throw new ArgumentException($"PostgreSQL returning alias '{alias}' is already present.", nameof(alias));
        returning.Add(new(expression, identifier));
        return this;
    }

    /// <summary>Builds an immutable reusable PostgreSQL command template.</summary>
    /// <returns>Normalized SQL and deterministic positional-parameter slots.</returns>
    /// <exception cref="InvalidOperationException">No assignment or row-restricting predicate is configured.</exception>
    public PostgresSqlCommandTemplate BuildTemplate()
    {
        if (assignments.Count == 0)
            throw new InvalidOperationException("A PostgreSQL UPDATE requires at least one assignment.");
        if (predicates.Count == 0)
            throw new InvalidOperationException("A PostgreSQL UPDATE requires at least one predicate; unrestricted updates are not implicit.");

        PostgresSqlRenderContext context = new();
        StringBuilder builder = new("UPDATE ");
        table.WriteTo(builder);
        builder.Append(" SET ");
        for (var index = 0; index < assignments.Count; index++)
        {
            if (index != 0)
                builder.Append(", ");
            var assignment = assignments[index];
            assignment.Column.WriteQuoted(builder);
            builder.Append(" = ");
            assignment.Value.WriteTo(context, builder);
        }

        builder.Append(" WHERE ");
        for (var index = 0; index < predicates.Count; index++)
        {
            if (index != 0)
                builder.Append(" AND ");
            predicates[index].WriteTo(context, builder);
        }

        PostgresSqlMutationWriter.WriteReturning(builder, context, returning);
        return new(builder.ToString(), context.Parameters);
    }
}

/// <summary>Mutable, single-threaded builder for an injection-safe, predicate-guarded PostgreSQL <c>DELETE</c>.</summary>
public sealed class PostgresSqlDeleteBuilder
{
    readonly PostgresSqlQualifiedTable table;
    readonly List<PostgresSqlExpression> predicates = [];
    readonly List<PostgresSqlReturningItem> returning = [];
    readonly HashSet<PostgresSqlIdentifier> returningAliases = [];

    /// <summary>Creates a delete builder for one physical table.</summary>
    /// <param name="table">Injection-safe physical table name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is <see langword="null"/>.</exception>
    public PostgresSqlDeleteBuilder(PostgresSqlQualifiedTable table)
    {
        this.table = Guard.RequireNotNull(table);
    }

    /// <summary>Adds a required predicate combined with prior predicates using conjunction.</summary>
    /// <param name="predicate">Boolean predicate restricting deleted rows.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public PostgresSqlDeleteBuilder Where(PostgresSqlExpression predicate)
    {
        predicates.Add(Guard.RequireNotNull(predicate));
        return this;
    }

    /// <summary>Adds one expression returned from each deleted row.</summary>
    /// <param name="expression">Expression evaluated by the <c>RETURNING</c> clause.</param>
    /// <param name="alias">Unique result-column alias.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is invalid or repeated.</exception>
    public PostgresSqlDeleteBuilder Returning(PostgresSqlExpression expression, string alias)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var identifier = new PostgresSqlIdentifier(alias);
        if (!returningAliases.Add(identifier))
            throw new ArgumentException($"PostgreSQL returning alias '{alias}' is already present.", nameof(alias));
        returning.Add(new(expression, identifier));
        return this;
    }

    /// <summary>Builds an immutable reusable PostgreSQL command template.</summary>
    /// <returns>Normalized SQL and deterministic positional-parameter slots.</returns>
    /// <exception cref="InvalidOperationException">No row-restricting predicate is configured.</exception>
    public PostgresSqlCommandTemplate BuildTemplate()
    {
        if (predicates.Count == 0)
            throw new InvalidOperationException("A PostgreSQL DELETE requires at least one predicate; unrestricted deletes are not implicit.");

        PostgresSqlRenderContext context = new();
        StringBuilder builder = new("DELETE FROM ");
        table.WriteTo(builder);
        builder.Append(" WHERE ");
        for (var index = 0; index < predicates.Count; index++)
        {
            if (index != 0)
                builder.Append(" AND ");
            predicates[index].WriteTo(context, builder);
        }

        PostgresSqlMutationWriter.WriteReturning(builder, context, returning);
        return new(builder.ToString(), context.Parameters);
    }
}

internal sealed record PostgresSqlColumnValue(
    PostgresSqlIdentifier Column,
    PostgresSqlExpression Value);

internal sealed record PostgresSqlReturningItem(
    PostgresSqlExpression Expression,
    PostgresSqlIdentifier Alias);

internal static class PostgresSqlMutationWriter
{
    internal static void WriteReturning(
        StringBuilder builder,
        PostgresSqlRenderContext context,
        IReadOnlyList<PostgresSqlReturningItem> returning)
    {
        if (returning.Count == 0)
            return;
        builder.Append(" RETURNING ");
        for (var index = 0; index < returning.Count; index++)
        {
            if (index != 0)
                builder.Append(", ");
            var item = returning[index];
            item.Expression.WriteTo(context, builder);
            builder.Append(" AS ");
            item.Alias.WriteQuoted(builder);
        }
    }
}
