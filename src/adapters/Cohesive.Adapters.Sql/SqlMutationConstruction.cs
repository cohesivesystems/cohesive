using System.Collections.Immutable;
using System.Text;

namespace Cohesive.Adapters.Sql;

/// <summary>Mutable, single-threaded builder for an injection-safe SQL <c>INSERT</c> command.</summary>
public sealed class SqlInsertBuilder
{
    readonly SqlQualifiedTable table;
    readonly List<SqlColumnValue> values = [];
    readonly HashSet<SqlIdentifier> valueColumns = [];
    readonly List<SqlReturningItem> returning = [];
    readonly HashSet<SqlIdentifier> returningAliases = [];
    ImmutableArray<SqlIdentifier> conflictColumns;
    ImmutableArray<SqlIdentifier> excludedUpdateColumns;
    bool conflictDoNothing;
    SqlExpression? conflictPredicate;

    /// <summary>Creates an insert builder for one physical table.</summary>
    /// <param name="table">Injection-safe physical table name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is <see langword="null"/>.</exception>
    public SqlInsertBuilder(SqlQualifiedTable table)
    {
        this.table = Guard.RequireNotNull(table);
    }

    /// <summary>Adds one inserted column and its value expression.</summary>
    /// <param name="columnName">Physical target-column identifier.</param>
    /// <param name="value">Value written to the column.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is invalid or already has an inserted value.</exception>
    public SqlInsertBuilder Value(string columnName, SqlExpression value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var column = new SqlIdentifier(columnName);
        if (!valueColumns.Add(column))
            throw new ArgumentException($"SQL insert column '{columnName}' is already present.", nameof(columnName));
        values.Add(new(column, value));
        return this;
    }

    /// <summary>
    /// Configures an upsert that replaces selected columns with their corresponding <c>EXCLUDED</c> values when the
    /// specified conflict key already exists.
    /// </summary>
    /// <param name="conflictColumns">One or more physical columns forming the conflict target.</param>
    /// <param name="excludedUpdateColumns">One or more inserted columns replaced from <c>EXCLUDED</c>.</param>
    /// <param name="predicate">Optional condition on the existing conflicting row; false preserves it without returning a row.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">A collection or column name is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A collection is empty or contains an invalid or repeated column.</exception>
    /// <exception cref="InvalidOperationException">Conflict behavior has already been configured.</exception>
    public SqlInsertBuilder OnConflictDoUpdate(
        IEnumerable<string> conflictColumns,
        IEnumerable<string> excludedUpdateColumns,
        SqlExpression? predicate = null)
    {
        if (!this.conflictColumns.IsDefault)
            throw new InvalidOperationException("SQL insert conflict behavior has already been configured.");
        this.conflictColumns = CaptureIdentifiers(conflictColumns, nameof(conflictColumns));
        this.excludedUpdateColumns = CaptureIdentifiers(excludedUpdateColumns, nameof(excludedUpdateColumns));
        conflictPredicate = predicate;
        return this;
    }

    /// <summary>Configures an idempotent insert that retains the existing row on a key conflict.</summary>
    /// <param name="conflictColumns">One or more physical columns forming the conflict target.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="conflictColumns"/> or a column name is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="conflictColumns"/> is empty or contains an invalid or repeated column.</exception>
    /// <exception cref="InvalidOperationException">Conflict behavior has already been configured.</exception>
    public SqlInsertBuilder OnConflictDoNothing(IEnumerable<string> conflictColumns)
    {
        if (!this.conflictColumns.IsDefault)
            throw new InvalidOperationException("SQL insert conflict behavior has already been configured.");
        this.conflictColumns = CaptureIdentifiers(conflictColumns, nameof(conflictColumns));
        excludedUpdateColumns = [];
        conflictDoNothing = true;
        return this;
    }

    /// <summary>Adds one expression returned from the inserted or updated row.</summary>
    /// <param name="expression">Expression evaluated by the <c>RETURNING</c> clause.</param>
    /// <param name="alias">Unique result-column alias.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is invalid or repeated.</exception>
    public SqlInsertBuilder Returning(SqlExpression expression, string alias)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var identifier = new SqlIdentifier(alias);
        if (!returningAliases.Add(identifier))
            throw new ArgumentException($"SQL returning alias '{alias}' is already present.", nameof(alias));
        returning.Add(new(expression, identifier));
        return this;
    }

    /// <summary>Builds an immutable reusable SQL command template.</summary>
    /// <param name="dialect">Explicit adapter-owned construction and parameter policy.</param>
    /// <exception cref="SqlConstructionException">A requested construct is unsupported by the dialect.</exception>
    /// <returns>Normalized SQL and deterministic positional-parameter slots.</returns>
    /// <exception cref="InvalidOperationException">
    /// No values are configured, or conflict and update columns do not refer to configured insert values.
    /// </exception>
    public SqlCommandTemplate BuildTemplate(SqlDialect dialect)
    {
        if (values.Count == 0)
            throw new InvalidOperationException("A SQL INSERT requires at least one column value.");
        ValidateConflictColumns();

        SqlRenderContext context = new(dialect);
        StringBuilder builder = new("INSERT INTO ");
        table.WriteTo(context, builder);
        builder.Append(" (");
        WriteIdentifiers(builder, context, values.Select(static value => value.Column));
        builder.Append(") VALUES (");
        WriteExpressions(builder, context, values.Select(static value => value.Value));
        builder.Append(')');

        if (!conflictColumns.IsDefault)
        {
            context.Dialect.Require(SqlFeature.OnConflict);
            builder.Append(" ON CONFLICT (");
            WriteIdentifiers(builder, context, conflictColumns);
            if (conflictDoNothing)
            {
                builder.Append(") DO NOTHING");
            }
            else
            {
                builder.Append(") DO UPDATE SET ");
                for (var index = 0; index < excludedUpdateColumns.Length; index++)
                {
                    if (index != 0)
                        builder.Append(", ");
                    var column = excludedUpdateColumns[index];
                    column.WriteQuoted(context, builder);
                    builder.Append(" = EXCLUDED.");
                    column.WriteQuoted(context, builder);
                }
            }
        }

        if (conflictPredicate is not null)
        {
            builder.Append(" WHERE ");
            conflictPredicate.WriteTo(context, builder);
        }
        SqlMutationWriter.WriteReturning(builder, context, returning);
        return new(builder.ToString(), context.Parameters, dialect.Name);
    }

    void ValidateConflictColumns()
    {
        if (conflictColumns.IsDefault)
            return;
        foreach (var column in conflictColumns)
        {
            if (!valueColumns.Contains(column))
                throw new InvalidOperationException($"SQL conflict column '{column.Value}' has no configured insert value.");
        }
        foreach (var column in excludedUpdateColumns)
        {
            if (!valueColumns.Contains(column))
                throw new InvalidOperationException($"SQL excluded update column '{column.Value}' has no configured insert value.");
        }
    }

    static ImmutableArray<SqlIdentifier> CaptureIdentifiers(
        IEnumerable<string> columns,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(columns, parameterName);
        ImmutableArray<SqlIdentifier>.Builder captured = ImmutableArray.CreateBuilder<SqlIdentifier>();
        HashSet<SqlIdentifier> unique = [];
        foreach (var columnName in columns)
        {
            var column = new SqlIdentifier(columnName);
            if (!unique.Add(column))
                throw new ArgumentException($"SQL column '{columnName}' is repeated.", parameterName);
            captured.Add(column);
        }
        if (captured.Count == 0)
            throw new ArgumentException("At least one SQL column is required.", parameterName);
        return captured.ToImmutable();
    }

    static void WriteIdentifiers(StringBuilder builder, SqlRenderContext context, IEnumerable<SqlIdentifier> identifiers)
    {
        var index = 0;
        foreach (var identifier in identifiers)
        {
            if (index++ != 0)
                builder.Append(", ");
            identifier.WriteQuoted(context, builder);
        }
    }

    static void WriteExpressions(
        StringBuilder builder,
        SqlRenderContext context,
        IEnumerable<SqlExpression> expressions)
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

/// <summary>Mutable, single-threaded builder for an injection-safe, predicate-guarded SQL <c>UPDATE</c>.</summary>
public sealed class SqlUpdateBuilder
{
    readonly SqlQualifiedTable table;
    readonly List<SqlColumnValue> assignments = [];
    readonly HashSet<SqlIdentifier> assignmentColumns = [];
    readonly List<SqlExpression> predicates = [];
    readonly List<SqlReturningItem> returning = [];
    readonly HashSet<SqlIdentifier> returningAliases = [];

    /// <summary>Creates an update builder for one physical table.</summary>
    /// <param name="table">Injection-safe physical table name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is <see langword="null"/>.</exception>
    public SqlUpdateBuilder(SqlQualifiedTable table)
    {
        this.table = Guard.RequireNotNull(table);
    }

    /// <summary>Adds one target-column assignment.</summary>
    /// <param name="columnName">Physical target-column identifier.</param>
    /// <param name="value">Value assigned to the column.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is invalid or already assigned.</exception>
    public SqlUpdateBuilder Set(string columnName, SqlExpression value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var column = new SqlIdentifier(columnName);
        if (!assignmentColumns.Add(column))
            throw new ArgumentException($"SQL update column '{columnName}' is already assigned.", nameof(columnName));
        assignments.Add(new(column, value));
        return this;
    }

    /// <summary>Adds a required predicate combined with prior predicates using conjunction.</summary>
    /// <param name="predicate">Boolean predicate restricting updated rows.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public SqlUpdateBuilder Where(SqlExpression predicate)
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
    public SqlUpdateBuilder Returning(SqlExpression expression, string alias)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var identifier = new SqlIdentifier(alias);
        if (!returningAliases.Add(identifier))
            throw new ArgumentException($"SQL returning alias '{alias}' is already present.", nameof(alias));
        returning.Add(new(expression, identifier));
        return this;
    }

    /// <summary>Builds an immutable reusable SQL command template.</summary>
    /// <param name="dialect">Explicit adapter-owned construction and parameter policy.</param>
    /// <exception cref="SqlConstructionException">A requested construct is unsupported by the dialect.</exception>
    /// <returns>Normalized SQL and deterministic positional-parameter slots.</returns>
    /// <exception cref="InvalidOperationException">No assignment or row-restricting predicate is configured.</exception>
    public SqlCommandTemplate BuildTemplate(SqlDialect dialect)
    {
        if (assignments.Count == 0)
            throw new InvalidOperationException("A SQL UPDATE requires at least one assignment.");
        if (predicates.Count == 0)
            throw new InvalidOperationException("A SQL UPDATE requires at least one predicate; unrestricted updates are not implicit.");

        SqlRenderContext context = new(dialect);
        StringBuilder builder = new("UPDATE ");
        table.WriteTo(context, builder);
        builder.Append(" SET ");
        for (var index = 0; index < assignments.Count; index++)
        {
            if (index != 0)
                builder.Append(", ");
            var assignment = assignments[index];
            assignment.Column.WriteQuoted(context, builder);
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

        SqlMutationWriter.WriteReturning(builder, context, returning);
        return new(builder.ToString(), context.Parameters, dialect.Name);
    }
}

/// <summary>Mutable, single-threaded builder for an injection-safe, predicate-guarded SQL <c>DELETE</c>.</summary>
public sealed class SqlDeleteBuilder
{
    readonly SqlQualifiedTable table;
    readonly List<SqlExpression> predicates = [];
    readonly List<SqlReturningItem> returning = [];
    readonly HashSet<SqlIdentifier> returningAliases = [];

    /// <summary>Creates a delete builder for one physical table.</summary>
    /// <param name="table">Injection-safe physical table name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is <see langword="null"/>.</exception>
    public SqlDeleteBuilder(SqlQualifiedTable table)
    {
        this.table = Guard.RequireNotNull(table);
    }

    /// <summary>Adds a required predicate combined with prior predicates using conjunction.</summary>
    /// <param name="predicate">Boolean predicate restricting deleted rows.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public SqlDeleteBuilder Where(SqlExpression predicate)
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
    public SqlDeleteBuilder Returning(SqlExpression expression, string alias)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var identifier = new SqlIdentifier(alias);
        if (!returningAliases.Add(identifier))
            throw new ArgumentException($"SQL returning alias '{alias}' is already present.", nameof(alias));
        returning.Add(new(expression, identifier));
        return this;
    }

    /// <summary>Builds an immutable reusable SQL command template.</summary>
    /// <param name="dialect">Explicit adapter-owned construction and parameter policy.</param>
    /// <exception cref="SqlConstructionException">A requested construct is unsupported by the dialect.</exception>
    /// <returns>Normalized SQL and deterministic positional-parameter slots.</returns>
    /// <exception cref="InvalidOperationException">No row-restricting predicate is configured.</exception>
    public SqlCommandTemplate BuildTemplate(SqlDialect dialect)
    {
        if (predicates.Count == 0)
            throw new InvalidOperationException("A SQL DELETE requires at least one predicate; unrestricted deletes are not implicit.");

        SqlRenderContext context = new(dialect);
        StringBuilder builder = new("DELETE FROM ");
        table.WriteTo(context, builder);
        builder.Append(" WHERE ");
        for (var index = 0; index < predicates.Count; index++)
        {
            if (index != 0)
                builder.Append(" AND ");
            predicates[index].WriteTo(context, builder);
        }

        SqlMutationWriter.WriteReturning(builder, context, returning);
        return new(builder.ToString(), context.Parameters, dialect.Name);
    }
}

internal sealed record SqlColumnValue(
    SqlIdentifier Column,
    SqlExpression Value);

internal sealed record SqlReturningItem(
    SqlExpression Expression,
    SqlIdentifier Alias);

internal static class SqlMutationWriter
{
    internal static void WriteReturning(
        StringBuilder builder,
        SqlRenderContext context,
        IReadOnlyList<SqlReturningItem> returning)
    {
        if (returning.Count == 0)
            return;
        context.Dialect.Require(SqlFeature.Returning);
        builder.Append(" RETURNING ");
        for (var index = 0; index < returning.Count; index++)
        {
            if (index != 0)
                builder.Append(", ");
            var item = returning[index];
            item.Expression.WriteTo(context, builder);
            builder.Append(" AS ");
            item.Alias.WriteQuoted(context, builder);
        }
    }
}
