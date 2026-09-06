using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Cohesive.Adapters.Sql;

/// <summary>How one SQL command-template parameter obtains its value.</summary>
public enum SqlParameterBindingKind
{
    /// <summary>The parameter value is captured by the immutable SQL expression tree.</summary>
    Constant = 0,

    /// <summary>The parameter value is supplied when the command template is bound.</summary>
    Runtime = 1
}

/// <summary>Supported SQL binary operators.</summary>
public enum SqlBinaryOperator
{
    /// <summary>SQL equality.</summary>
    Equal = 0,

    /// <summary>SQL inequality.</summary>
    NotEqual = 1,

    /// <summary>SQL greater-than comparison.</summary>
    GreaterThan = 2,

    /// <summary>SQL greater-than-or-equal comparison.</summary>
    GreaterThanOrEqual = 3,

    /// <summary>SQL less-than comparison.</summary>
    LessThan = 4,

    /// <summary>SQL less-than-or-equal comparison.</summary>
    LessThanOrEqual = 5,

    /// <summary>Boolean conjunction.</summary>
    And = 6,

    /// <summary>Boolean disjunction.</summary>
    Or = 7,

    /// <summary>Numeric addition.</summary>
    Add = 8,

    /// <summary>Numeric subtraction.</summary>
    Subtract = 9,

    /// <summary>Numeric multiplication.</summary>
    Multiply = 10,

    /// <summary>Numeric division.</summary>
    Divide = 11,

    /// <summary>SQL pattern matching with SQL <c>LIKE</c> semantics.</summary>
    Like = 12,

    /// <summary>Null-safe equality using <c>IS NOT DISTINCT FROM</c>.</summary>
    IsNotDistinctFrom = 13,

    /// <summary>Null-safe inequality using <c>IS DISTINCT FROM</c>.</summary>
    IsDistinctFrom = 14
}

/// <summary>Supported SQL unary operators.</summary>
public enum SqlUnaryOperator
{
    /// <summary>Boolean negation.</summary>
    Not = 0,

    /// <summary>Numeric negation.</summary>
    Negate = 1
}

/// <summary>Supported SQL scalar functions.</summary>
public enum SqlFunction
{
    /// <summary>Returns the number of characters in a text value.</summary>
    Length = 0,

    /// <summary>Returns a requested number of characters from the right side of a text value.</summary>
    Right = 1,

    /// <summary>Converts text to lower case according to the effective SQL collation.</summary>
    Lower = 2,

    /// <summary>Converts text to upper case according to the effective SQL collation.</summary>
    Upper = 3,

    /// <summary>Returns a requested number of characters from the left side of a text value.</summary>
    Left = 4,

    /// <summary>Returns the one-based position of a text substring, or zero when absent.</summary>
    StringPosition = 5
}

/// <summary>Supported SQL aggregate functions.</summary>
public enum SqlAggregateFunction
{
    /// <summary>Counts input rows or non-null operand values.</summary>
    Count = 0,

    /// <summary>Sums non-null input values.</summary>
    Sum = 1,

    /// <summary>Selects the minimum non-null input value.</summary>
    Minimum = 2,

    /// <summary>Selects the maximum non-null input value.</summary>
    Maximum = 3,

    /// <summary>Computes the average of non-null input values.</summary>
    Average = 4,

    /// <summary>Returns true when at least one non-null Boolean input is true.</summary>
    BooleanOr = 5,

    /// <summary>Returns true when every non-null Boolean input is true.</summary>
    BooleanAnd = 6
}

/// <summary>SQL join syntax emitted by <see cref="SqlSelectBuilder"/>.</summary>
public enum SqlJoinKind
{
    /// <summary>Retains only matching left and right rows.</summary>
    Inner = 0,

    /// <summary>Retains every left row and null-extends absent right rows.</summary>
    Left = 1,

    /// <summary>Retains every right row and null-extends absent left rows.</summary>
    Right = 2,

    /// <summary>Retains every row from both sides and null-extends absent counterparts.</summary>
    Full = 3,

    /// <summary>Produces the Cartesian product and therefore has no join predicate.</summary>
    Cross = 4
}

/// <summary>Direction of one SQL ordering term.</summary>
public enum SqlSortDirection
{
    /// <summary>Orders lower values before higher values.</summary>
    Ascending = 0,

    /// <summary>Orders higher values before lower values.</summary>
    Descending = 1
}

/// <summary>Placement of SQL nulls within one SQL ordering term.</summary>
public enum SqlNullPlacement
{
    /// <summary>Places null values before non-null values.</summary>
    First = 0,

    /// <summary>Places null values after non-null values.</summary>
    Last = 1
}

/// <summary>An injection-safe SQL identifier rendered with double-quote escaping.</summary>
public readonly record struct SqlIdentifier
{
    /// <summary>Creates a SQL identifier.</summary>
    /// <param name="value">Unquoted identifier value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty, contains the zero character, or is not valid Unicode. Target length limits are checked when rendering.
    /// </exception>
    public SqlIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            throw new ArgumentException("A SQL identifier cannot be empty.", nameof(value));
        }

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A SQL identifier cannot contain a zero character.", nameof(value));
        }

        _ = SqlUtf8.GetByteCount(value, nameof(value));

        Value = value;
    }

    /// <summary>Unquoted identifier value.</summary>
    public string Value { get; }

    /// <summary>Returns the unquoted identifier value.</summary>
    /// <returns>The unquoted identifier value.</returns>
    public override string ToString() => Value;

    /// <summary>Renders this identifier as one safely quoted target token.</summary>
    /// <param name="dialect">Target policy used to validate exact identifier representation.</param>
    /// <returns>One escaped double-quoted SQL identifier.</returns>
    /// <exception cref="ArgumentNullException">The dialect is null.</exception>
    /// <exception cref="ArgumentException">The identifier is default or outside the target domain.</exception>
    public string ToSql(SqlDialect dialect)
    {
        var builder = new StringBuilder();
        WriteQuoted(new SqlRenderContext(dialect), builder);
        return builder.ToString();
    }

    internal void WriteQuoted(SqlRenderContext context, StringBuilder builder)
    {
        if (Value is null) throw new ArgumentException("A default SQL identifier cannot be rendered.");
        context.Dialect.ValidateIdentifier(this);
        builder.Append('"').Append(Value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }
}

/// <summary>An injection-safe optionally schema-qualified SQL table name.</summary>
public sealed record SqlQualifiedTable
{
    /// <summary>Creates an unqualified SQL table name.</summary>
    /// <param name="tableName">Physical table identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tableName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is not a supported SQL identifier.</exception>
    public SqlQualifiedTable(string tableName)
        : this(schemaName: null, new SqlIdentifier(tableName))
    {
    }

    /// <summary>Creates a schema-qualified SQL table name.</summary>
    /// <param name="schemaName">Physical schema identifier.</param>
    /// <param name="tableName">Physical table identifier.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is not a supported SQL identifier.</exception>
    public SqlQualifiedTable(string schemaName, string tableName)
        : this(new SqlIdentifier(schemaName), new SqlIdentifier(tableName))
    {
    }

    SqlQualifiedTable(SqlIdentifier? schemaName, SqlIdentifier tableName)
    {
        SchemaName = schemaName;
        TableName = tableName;
    }

    /// <summary>Optional physical schema identifier.</summary>
    public SqlIdentifier? SchemaName { get; }

    /// <summary>Physical table identifier.</summary>
    public SqlIdentifier TableName { get; }

    /// <summary>Renders this optionally schema-qualified table name using target identifier policy.</summary>
    /// <param name="dialect">Target policy for exact identifier representation.</param>
    /// <returns>A name with each identifier individually escaped and double-quoted.</returns>
    /// <exception cref="ArgumentNullException">The dialect is null.</exception>
    /// <exception cref="ArgumentException">An identifier is outside the target domain.</exception>
    public string ToSql(SqlDialect dialect)
    {
        var builder = new StringBuilder();
        WriteTo(new SqlRenderContext(dialect), builder);
        return builder.ToString();
    }

    internal void WriteTo(SqlRenderContext context, StringBuilder builder)
    {
        if (SchemaName is { } schema)
        {
            schema.WriteQuoted(context, builder);
            builder.Append('.');
        }

        TableName.WriteQuoted(context, builder);
    }
}

/// <summary>One key and continuation value participating in structural keyset pagination.</summary>
public sealed record SqlKeysetTerm
{
    /// <summary>Creates one keyset comparison term.</summary>
    /// <param name="key">Expression evaluated for the candidate row.</param>
    /// <param name="continuation">Expression containing the preceding page's final key value.</param>
    /// <param name="direction">Ordering direction applied to this key.</param>
    /// <param name="nullPlacement">Explicit null placement applied to this key.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="key"/> or <paramref name="continuation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="direction"/> or <paramref name="nullPlacement"/> is unsupported.
    /// </exception>
    public SqlKeysetTerm(
        SqlExpression key,
        SqlExpression continuation,
        SqlSortDirection direction = SqlSortDirection.Ascending,
        SqlNullPlacement nullPlacement = SqlNullPlacement.Last)
    {
        Key = Guard.RequireNotNull(key);
        Continuation = Guard.RequireNotNull(continuation);
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported SQL sort direction.");
        }

        if (!Enum.IsDefined(nullPlacement))
        {
            throw new ArgumentOutOfRangeException(nameof(nullPlacement), nullPlacement, "Unsupported SQL null placement.");
        }

        Direction = direction;
        NullPlacement = nullPlacement;
    }

    /// <summary>Expression evaluated for the candidate row.</summary>
    public SqlExpression Key { get; }

    /// <summary>Expression containing the preceding page's final key value.</summary>
    public SqlExpression Continuation { get; }

    /// <summary>Ordering direction applied to this key.</summary>
    public SqlSortDirection Direction { get; }

    /// <summary>Explicit null placement applied to this key.</summary>
    public SqlNullPlacement NullPlacement { get; }
}

/// <summary>SQL scalar-expression tree with structured operands and dialect-owned intrinsic extensions.</summary>
public abstract partial record SqlExpression
{
    /// <summary>Initializes a SQL expression.</summary>
    private protected SqlExpression()
    {
    }

    /// <summary>Creates a column reference qualified by a source alias.</summary>
    /// <param name="sourceAlias">Table or derived-table alias.</param>
    /// <param name="columnName">Physical column identifier.</param>
    /// <returns>A qualified column expression.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is not a supported SQL identifier.</exception>
    public static SqlExpression Column(string sourceAlias, string columnName) =>
        new ColumnExpression(new(sourceAlias), new(columnName));

    /// <summary>Creates an unqualified column reference.</summary>
    /// <param name="columnName">Physical column identifier.</param>
    /// <returns>An unqualified column expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is not a supported SQL identifier.</exception>
    public static SqlExpression UnqualifiedColumn(string columnName) =>
        new ColumnExpression(SourceAlias: null, new(columnName));

    /// <summary>Creates a parameterized constant expression.</summary>
    /// <param name="value">
    /// Provider-neutral parameter value: <see langword="null"/>, Boolean, 32- or 64-bit integer, decimal, string,
    /// UUID, date, unspecified-kind civil timestamp, offset timestamp, or bytes.
    /// Mutable byte arrays are captured immediately.
    /// </param>
    /// <returns>An expression whose value is retained by the command template.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> has an unsupported CLR type or timestamp kind, alignment, or offset, or a string is
    /// outside the exact SQL UTF-8 text domain.
    /// </exception>
    public static SqlExpression Constant(object? value) =>
        new ConstantExpression(SqlConstant.Capture(value));

    /// <summary>Creates a runtime-bound parameter expression.</summary>
    /// <param name="binding">Stable application binding used by <see cref="SqlCommandTemplate.Bind(SqlDialect, IReadOnlyDictionary{string, object?})"/>.</param>
    /// <returns>An expression rendered as a deterministically allocated positional parameter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="binding"/> is empty or white space.</exception>
    public static SqlExpression RuntimeParameter(string binding) =>
        new RuntimeParameterExpression(Guard.RequireNotNullOrWhiteSpace(binding));

    /// <summary>Compares a scalar against a runtime-bound native SQL array using <c>= ANY</c>.</summary>
    /// <param name="operand">Scalar expression to compare.</param>
    /// <param name="arrayBinding">Nonempty runtime binding for the native array.</param>
    /// <returns>A predicate requiring <see cref="SqlFeature.ArrayAny"/> at rendering.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The binding is empty or white space.</exception>
    public static SqlExpression EqualAny(
        SqlExpression operand,
        string arrayBinding) =>
        new EqualAnyExpression(
            Guard.RequireNotNull(operand),
            Guard.RequireNotNullOrWhiteSpace(arrayBinding));

    /// <summary>Creates a correlated SQL <c>EXISTS</c> predicate.</summary>
    /// <param name="query">Subquery whose row existence determines the predicate value.</param>
    /// <returns>A parenthesized <c>EXISTS</c> expression sharing the containing statement's parameter bindings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    public static SqlExpression Exists(SqlSelectQuery query) =>
        new ExistsExpression(Guard.RequireNotNull(query));

    /// <summary>Projects a bounded subquery as one value, or SQL null when no row matches.</summary>
    /// <param name="query">Immutable query selecting exactly one column with an explicit limit of one.</param>
    /// <returns>A scalar subquery sharing its containing statement's parameters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
    /// <exception cref="ArgumentException">The query does not select one column with limit one.</exception>
    public static SqlExpression ScalarSubquery(SqlSelectQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.RequireScalarSubquery();
        return new ScalarSubqueryExpression(query);
    }

    /// <summary>Applies one explicitly selected SQL collation to an expression.</summary>
    /// <param name="operand">Text expression to collate.</param>
    /// <param name="collation">SQL collation identifier.</param>
    /// <returns>An expression rendered with a safely quoted <c>COLLATE</c> clause.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="collation"/> is not a supported identifier or is schema-qualified. This profile accepts
    /// one unqualified collation identifier so quoting cannot change qualification semantics.
    /// </exception>
    public static SqlExpression Collate(SqlExpression operand, string collation) =>
        new CollateExpression(Guard.RequireNotNull(operand), RequireUnqualifiedCollation(collation));

    static SqlIdentifier RequireUnqualifiedCollation(string collation)
    {
        var identifier = new SqlIdentifier(collation);
        if (collation.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A SQL collation must be an unqualified identifier in this SQL construction profile.",
                nameof(collation));
        }
        return identifier;
    }

    /// <summary>Creates a unary expression.</summary>
    /// <param name="operator">Closed SQL unary operator.</param>
    /// <param name="operand">Operand expression.</param>
    /// <returns>A unary SQL expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operand"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operator"/> is unsupported.</exception>
    public static SqlExpression Unary(
        SqlUnaryOperator @operator,
        SqlExpression operand)
    {
        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported SQL unary operator.");
        }

        return new UnaryExpression(@operator, Guard.RequireNotNull(operand));
    }

    /// <summary>Creates a binary expression.</summary>
    /// <param name="operator">Closed SQL binary operator.</param>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>A binary SQL expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="left"/> or <paramref name="right"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operator"/> is unsupported.</exception>
    public static SqlExpression Binary(
        SqlBinaryOperator @operator,
        SqlExpression left,
        SqlExpression right)
    {
        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported SQL binary operator.");
        }

        return new BinaryExpression(@operator, Guard.RequireNotNull(left), Guard.RequireNotNull(right));
    }

    /// <summary>Creates an SQL <c>IS NULL</c> predicate.</summary>
    /// <param name="operand">Value tested for null.</param>
    /// <returns>A Boolean null-test expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operand"/> is <see langword="null"/>.</exception>
    public static SqlExpression IsNull(SqlExpression operand) =>
        new NullTestExpression(Guard.RequireNotNull(operand), NullExpected: true);

    /// <summary>Creates an SQL <c>IS NOT NULL</c> predicate.</summary>
    /// <param name="operand">Value tested for non-null presence.</param>
    /// <returns>A Boolean non-null-test expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operand"/> is <see langword="null"/>.</exception>
    public static SqlExpression IsNotNull(SqlExpression operand) =>
        new NullTestExpression(Guard.RequireNotNull(operand), NullExpected: false);

    /// <summary>Creates a closed SQL scalar-function call.</summary>
    /// <param name="function">Supported scalar function.</param>
    /// <param name="arguments">Function arguments.</param>
    /// <returns>A scalar-function expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> or an argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The argument count does not match the selected function.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="function"/> is unsupported.</exception>
    public static SqlExpression Function(
        SqlFunction function,
        params SqlExpression[] arguments)
    {
        if (!Enum.IsDefined(function))
        {
            throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported SQL scalar function.");
        }

        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(static argument => argument is null))
        {
            throw new ArgumentNullException(nameof(arguments), "SQL function arguments cannot contain null entries.");
        }

        SqlFunctions.ValidateArity(function, arguments.Length, nameof(arguments));
        return new FunctionExpression(function, [.. arguments]);
    }

    /// <summary>Creates a named, dialect-owned expression without embedding executable policy or raw SQL.</summary>
    /// <param name="intrinsic">Stable adapter-owned construct identity, conventionally namespaced and versioned.</param>
    /// <param name="arguments">Ordered expression operands, copied into immutable storage.</param>
    /// <returns>An expression resolved by <see cref="SqlDialect.WriteIntrinsic"/> during rendering.</returns>
    /// <exception cref="ArgumentNullException">The identity, argument array, or an operand is null.</exception>
    /// <exception cref="ArgumentException">The identity is empty or white space.</exception>
    /// <remarks>
    /// The dialect validates identity, arity, and target support before emitting syntax. Unsupported identities
    /// fail with <see cref="SqlConstructionException"/> at rendering. The identity itself is never emitted as SQL.
    /// </remarks>
    public static SqlExpression Intrinsic(string intrinsic, params SqlExpression[] arguments)
    {
        Guard.RequireNotNullOrWhiteSpace(intrinsic);
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (var argument in arguments)
            ArgumentNullException.ThrowIfNull(argument, nameof(arguments));
        return new IntrinsicExpression(intrinsic, [.. arguments]);
    }

    /// <summary>Creates a SQL aggregate expression with an optional aggregate-local filter.</summary>
    /// <param name="function">Supported aggregate function.</param>
    /// <param name="operand">
    /// Aggregate operand, or <see langword="null"/> only to request <c>COUNT(*)</c>.
    /// </param>
    /// <param name="filter">Optional predicate emitted through SQL's aggregate <c>FILTER</c> clause.</param>
    /// <param name="distinct">Whether duplicate operand values are removed before aggregation.</param>
    /// <returns>An aggregate expression.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="operand"/> is <see langword="null"/> for a function other than count, or
    /// <paramref name="distinct"/> is true for <c>COUNT(*)</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="function"/> is unsupported.</exception>
    public static SqlExpression Aggregate(
        SqlAggregateFunction function,
        SqlExpression? operand = null,
        SqlExpression? filter = null,
        bool distinct = false)
    {
        if (!Enum.IsDefined(function))
        {
            throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported SQL aggregate function.");
        }

        if (operand is null && function != SqlAggregateFunction.Count)
        {
            throw new ArgumentException("Only COUNT may omit its aggregate operand.", nameof(operand));
        }

        if (operand is null && distinct)
        {
            throw new ArgumentException("COUNT(*) cannot be combined with DISTINCT.", nameof(distinct));
        }

        return new AggregateExpression(function, operand, filter, distinct);
    }

    /// <summary>Creates a searched SQL conditional expression.</summary>
    /// <param name="test">Boolean condition.</param>
    /// <param name="whenTrue">Value returned when <paramref name="test"/> is true.</param>
    /// <param name="whenFalse">Value returned otherwise.</param>
    /// <returns>A <c>CASE WHEN</c> expression.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    public static SqlExpression Conditional(
        SqlExpression test,
        SqlExpression whenTrue,
        SqlExpression whenFalse) =>
        new ConditionalExpression(
            Guard.RequireNotNull(test),
            Guard.RequireNotNull(whenTrue),
            Guard.RequireNotNull(whenFalse));

    /// <summary>Creates a SQL <c>COALESCE</c> expression.</summary>
    /// <param name="values">Two or more values evaluated from left to right.</param>
    /// <returns>A coalescing expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or an entry is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Fewer than two values are supplied.</exception>
    public static SqlExpression Coalesce(params SqlExpression[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length < 2)
        {
            throw new ArgumentException("COALESCE requires at least two values.", nameof(values));
        }

        if (values.Any(static value => value is null))
        {
            throw new ArgumentNullException(nameof(values), "COALESCE values cannot contain null entries.");
        }

        return new CoalesceExpression([.. values]);
    }

    /// <summary>
    /// Creates a null-aware lexicographic predicate selecting rows strictly after a continuation tuple.
    /// </summary>
    /// <param name="terms">Ordered key terms aligned with the query's <c>ORDER BY</c> clause.</param>
    /// <returns>
    /// A structural disjunction using each term's direction and null placement and null-safe equality for preceding keys.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="terms"/> is default or empty, contains a <see langword="null"/> entry, or contains an
    /// unsupported enum value.
    /// </exception>
    public static SqlExpression KeysetAfter(ImmutableArray<SqlKeysetTerm> terms)
    {
        var normalized = terms.IsDefault ? [] : terms;
        if (normalized.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A keyset predicate requires at least one ordered term.", nameof(terms));
        }

        if (normalized.Any(static term => term is null))
        {
            throw new ArgumentException("Keyset terms cannot contain null entries.", nameof(terms));
        }

        SqlExpression? predicate = null;
        SqlExpression? equalPrefix = null;
        foreach (var term in normalized)
        {
            var after = AfterTerm(term);
            var branch = equalPrefix is null
                ? after
                : Binary(SqlBinaryOperator.And, equalPrefix, after);
            predicate = predicate is null
                ? branch
                : Binary(SqlBinaryOperator.Or, predicate, branch);

            var equal = Binary(
                SqlBinaryOperator.IsNotDistinctFrom,
                term.Key,
                term.Continuation);
            equalPrefix = equalPrefix is null
                ? equal
                : Binary(SqlBinaryOperator.And, equalPrefix, equal);
        }

        return predicate!;
    }

    static SqlExpression AfterTerm(SqlKeysetTerm term)
    {
        var concreteComparison = Binary(
            term.Direction == SqlSortDirection.Ascending
                ? SqlBinaryOperator.GreaterThan
                : SqlBinaryOperator.LessThan,
            term.Key,
            term.Continuation);
        var bothConcrete = Binary(
            SqlBinaryOperator.And,
            IsNotNull(term.Key),
            Binary(
                SqlBinaryOperator.And,
                IsNotNull(term.Continuation),
                concreteComparison));
        var crossesNullBoundary = term.NullPlacement == SqlNullPlacement.First
            ? Binary(
                SqlBinaryOperator.And,
                IsNull(term.Continuation),
                IsNotNull(term.Key))
            : Binary(
                SqlBinaryOperator.And,
                IsNotNull(term.Continuation),
                IsNull(term.Key));
        return Binary(SqlBinaryOperator.Or, crossesNullBoundary, bothConcrete);
    }

    internal abstract void WriteTo(SqlRenderContext context, StringBuilder builder);

    sealed record ColumnExpression(
        SqlIdentifier? SourceAlias,
        SqlIdentifier ColumnName) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            if (SourceAlias is { } alias)
            {
                alias.WriteQuoted(context, builder);
                builder.Append('.');
            }

            ColumnName.WriteQuoted(context, builder);
        }
    }

    sealed record ConstantExpression(SqlConstant Value) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder) =>
            builder.Append(context.AddConstant(Value));
    }

    sealed record RuntimeParameterExpression(string Binding) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder) =>
            builder.Append(context.AddRuntime(Binding));
    }

    sealed record EqualAnyExpression(
        SqlExpression Operand,
        string ArrayBinding) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            builder.Append('(');
            Operand.WriteTo(context, builder);
            context.Dialect.Require(SqlFeature.ArrayAny);
            builder.Append(" = ANY(").Append(context.AddRuntime(ArrayBinding)).Append("))");
        }
    }

    sealed record ScalarSubqueryExpression(SqlSelectQuery Query) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            context.Dialect.Require(SqlFeature.ScalarSubquery);
            builder.Append('(');
            context.WriteNestedQuery(Query, builder);
            builder.Append(')');
        }
    }

    sealed record ExistsExpression(SqlSelectQuery Query) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            builder.Append("EXISTS (");
            context.WriteNestedQuery(Query, builder);
            builder.Append(')');
        }
    }

    sealed record CollateExpression(
        SqlExpression Operand,
        SqlIdentifier Collation) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            builder.Append('(');
            Operand.WriteTo(context, builder);
            builder.Append(" COLLATE ");
            Collation.WriteQuoted(context, builder);
            builder.Append(')');
        }
    }

    sealed record UnaryExpression(
        SqlUnaryOperator Operator,
        SqlExpression Operand) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            builder.Append('(').Append(SqlOperators.Text(Operator));
            Operand.WriteTo(context, builder);
            builder.Append(')');
        }
    }

    sealed record BinaryExpression(
        SqlBinaryOperator Operator,
        SqlExpression Left,
        SqlExpression Right) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            if (Operator is SqlBinaryOperator.IsDistinctFrom or SqlBinaryOperator.IsNotDistinctFrom)
                context.Dialect.Require(SqlFeature.DistinctComparison);
            builder.Append('(');
            Left.WriteTo(context, builder);
            builder.Append(' ').Append(SqlOperators.Text(Operator)).Append(' ');
            Right.WriteTo(context, builder);
            builder.Append(')');
        }
    }

    sealed record NullTestExpression(
        SqlExpression Operand,
        bool NullExpected) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            builder.Append('(');
            Operand.WriteTo(context, builder);
            builder.Append(NullExpected ? " IS NULL)" : " IS NOT NULL)");
        }
    }

    sealed record FunctionExpression(
        SqlFunction FunctionKind,
        ImmutableArray<SqlExpression> Arguments) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            builder.Append(context.Dialect.FunctionName(FunctionKind)).Append('(');
            WriteExpressions(Arguments, context, builder);
            builder.Append(')');
        }
    }

    sealed record IntrinsicExpression(
        string IntrinsicId,
        ImmutableArray<SqlExpression> Arguments) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder) =>
            context.Dialect.WriteIntrinsic(IntrinsicId, Arguments, new SqlExpressionWriter(context, builder));
    }

    sealed record AggregateExpression(
        SqlAggregateFunction FunctionKind,
        SqlExpression? Operand,
        SqlExpression? Filter,
        bool Distinct) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            builder.Append(context.Dialect.FunctionName(FunctionKind)).Append('(');
            if (Distinct)
            {
                builder.Append("DISTINCT ");
            }

            if (Operand is null)
            {
                builder.Append('*');
            }
            else
            {
                Operand.WriteTo(context, builder);
            }

            builder.Append(')');
            if (Filter is not null)
            {
                context.Dialect.Require(SqlFeature.AggregateFilter);
                builder.Append(" FILTER (WHERE ");
                Filter.WriteTo(context, builder);
                builder.Append(')');
            }
        }
    }

    sealed record ConditionalExpression(
        SqlExpression Test,
        SqlExpression WhenTrue,
        SqlExpression WhenFalse) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            builder.Append("(CASE WHEN ");
            Test.WriteTo(context, builder);
            builder.Append(" THEN ");
            WhenTrue.WriteTo(context, builder);
            builder.Append(" ELSE ");
            WhenFalse.WriteTo(context, builder);
            builder.Append(" END)");
        }
    }

    sealed record CoalesceExpression(
        ImmutableArray<SqlExpression> Values) : SqlExpression
    {
        internal override void WriteTo(SqlRenderContext context, StringBuilder builder)
        {
            builder.Append("COALESCE(");
            WriteExpressions(Values, context, builder);
            builder.Append(')');
        }
    }

    static void WriteExpressions(
        ImmutableArray<SqlExpression> expressions,
        SqlRenderContext context,
        StringBuilder builder)
    {
        for (var index = 0; index < expressions.Length; index++)
        {
            if (index != 0)
            {
                builder.Append(", ");
            }

            expressions[index].WriteTo(context, builder);
        }
    }
}

/// <summary>Portable type tag for one captured SQL command-template constant.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SqlConstantKind
{
    /// <summary>SQL null.</summary>
    Null = 0,

    /// <summary>Boolean value.</summary>
    Boolean = 1,

    /// <summary>32-bit signed integer.</summary>
    Int32 = 2,

    /// <summary>64-bit signed integer.</summary>
    Int64 = 3,

    /// <summary>Exact decimal value.</summary>
    Decimal = 4,

    /// <summary>Text value.</summary>
    String = 5,

    /// <summary>UUID value.</summary>
    Uuid = 6,

    /// <summary>Calendar date.</summary>
    Date = 7,

    /// <summary>Civil timestamp without a time zone.</summary>
    Timestamp = 8,

    /// <summary>Timestamp retaining its exact offset and tick precision.</summary>
    TimestampWithTimeZone = 9,

    /// <summary>Byte sequence.</summary>
    Bytes = 10
}

/// <summary>Tagged portable representation of one captured SQL parameter value.</summary>
public sealed record SqlConstant
{
    /// <summary>Creates and validates a tagged portable constant.</summary>
    /// <param name="kind">Portable scalar kind.</param>
    /// <param name="value">Invariant string representation, or <see langword="null"/> only for SQL null.</param>
    /// <exception cref="ArgumentException">The value is absent, malformed, or conflicts with <paramref name="kind"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    [JsonConstructor]
    public SqlConstant(SqlConstantKind kind, string? value)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported SQL constant kind.");
        if (kind == SqlConstantKind.Null != (value is null))
            throw new ArgumentException("Only a tagged SQL null may omit its portable value.", nameof(value));
        Kind = kind;
        Value = value;
        _ = ToClrValue();
    }

    /// <summary>Portable scalar kind.</summary>
    public SqlConstantKind Kind { get; }

    /// <summary>Invariant string representation, or <see langword="null"/> for SQL null.</summary>
    public string? Value { get; }

    internal static SqlConstant Capture(object? value)
    {
        return value switch
        {
            null => new(SqlConstantKind.Null, null),
            bool item => new(SqlConstantKind.Boolean, item ? "true" : "false"),
            int item => new(SqlConstantKind.Int32, item.ToString(CultureInfo.InvariantCulture)),
            long item => new(SqlConstantKind.Int64, item.ToString(CultureInfo.InvariantCulture)),
            decimal item => new(SqlConstantKind.Decimal, item.ToString(CultureInfo.InvariantCulture)),
            string item => new(SqlConstantKind.String, SqlUtf8.RequireText(item, nameof(value))),
            Guid item => new(SqlConstantKind.Uuid, item.ToString("D", CultureInfo.InvariantCulture)),
            DateOnly item => new(SqlConstantKind.Date, item.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            DateTime item => new(SqlConstantKind.Timestamp, item.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset item =>
                new(SqlConstantKind.TimestampWithTimeZone, item.ToString("O", CultureInfo.InvariantCulture)),
            byte[] item => new(SqlConstantKind.Bytes, Convert.ToBase64String(item)),
            _ => throw new ArgumentException(
                $"CLR value type '{value.GetType().FullName}' has no portable SQL command-template constant encoding.",
                nameof(value))
        };
    }

    internal static object? NormalizeRuntimeValue(object? value)
    {
        try
        {
            return Capture(value).ToClrValue();
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "A SQL runtime parameter must use a supported, exact provider-neutral CLR value.",
                nameof(value),
                exception);
        }
    }

    internal object? ToClrValue()
    {
        try
        {
            return Kind switch
            {
                SqlConstantKind.Null when Value is null => null,
                SqlConstantKind.Boolean => bool.Parse(Value!),
                SqlConstantKind.Int32 => int.Parse(Value!, NumberStyles.Integer, CultureInfo.InvariantCulture),
                SqlConstantKind.Int64 => long.Parse(Value!, NumberStyles.Integer, CultureInfo.InvariantCulture),
                SqlConstantKind.Decimal => decimal.Parse(Value!, NumberStyles.Number, CultureInfo.InvariantCulture),
                SqlConstantKind.String => SqlUtf8.RequireText(Value!, nameof(Value)),
                SqlConstantKind.Uuid => Guid.ParseExact(Value!, "D"),
                SqlConstantKind.Date => DateOnly.ParseExact(Value!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                SqlConstantKind.Timestamp => RequireCivilTimestamp(Value!),
                SqlConstantKind.TimestampWithTimeZone => RequireInstantTimestamp(Value!),
                SqlConstantKind.Bytes => Convert.FromBase64String(Value!),
                _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unsupported SQL constant kind.")
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new ArgumentException(
                $"Portable SQL constant '{Kind}' has a malformed value.",
                nameof(Value),
                exception);
        }
    }

    static DateTime RequireCivilTimestamp(string value)
    {
        var parsed = DateTime.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (parsed.Kind != DateTimeKind.Unspecified)
        {
            throw new FormatException(
                "A SQL civil timestamp must have DateTimeKind.Unspecified.");
        }
        return parsed;
    }

    static DateTimeOffset RequireInstantTimestamp(string value)
    {
        var parsed = DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        return parsed;
    }
}

/// <summary>One deterministically allocated parameter slot in a SQL command template.</summary>
public sealed record SqlParameterSlot
{
    internal SqlParameterSlot(
        int position,
        SqlParameterBindingKind kind,
        string? binding,
        object? constantValue)
        : this(
            position,
            kind,
            binding,
            kind == SqlParameterBindingKind.Constant
                ? SqlConstant.Capture(constantValue)
                : null)
    {
    }

    /// <summary>Creates and validates one persisted SQL parameter slot.</summary>
    /// <param name="position">One-based SQL parameter position.</param>
    /// <param name="kind">Captured-constant or runtime-binding kind.</param>
    /// <param name="binding">Runtime binding identity, or <see langword="null"/> for a constant.</param>
    /// <param name="constant">Tagged captured constant, or <see langword="null"/> for a runtime slot.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is not positive or <paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Binding and constant metadata conflict with <paramref name="kind"/>.</exception>
    [JsonConstructor]
    public SqlParameterSlot(
        int position,
        SqlParameterBindingKind kind,
        string? binding,
        SqlConstant? constant)
    {
        if (position <= 0)
            throw new ArgumentOutOfRangeException(nameof(position), position, "A SQL parameter position must be positive.");
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported SQL parameter-binding kind.");
        if (kind == SqlParameterBindingKind.Runtime
            && string.IsNullOrWhiteSpace(binding))
            throw new ArgumentException("A runtime SQL parameter requires a binding identity.", nameof(binding));
        if (kind == SqlParameterBindingKind.Constant != (constant is not null)
            || kind == SqlParameterBindingKind.Constant && binding is not null)
        {
            throw new ArgumentException("SQL parameter constant metadata conflicts with its binding kind.", nameof(constant));
        }
        Position = position;
        Kind = kind;
        Binding = binding;
        Constant = constant;
    }

    /// <summary>One-based SQL positional-parameter position.</summary>
    public int Position { get; }

    /// <summary>Rendered positional placeholder such as <c>$1</c>.</summary>
    [JsonIgnore]
    public string Placeholder => $"${Position.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>How the slot obtains its value.</summary>
    public SqlParameterBindingKind Kind { get; }

    /// <summary>Runtime binding identity, or <see langword="null"/> for a captured constant.</summary>
    public string? Binding { get; }

    /// <summary>Tagged captured constant, or <see langword="null"/> for a runtime slot.</summary>
    public SqlConstant? Constant { get; }

    /// <summary>Provider-neutral captured CLR value; meaningful only for a constant slot.</summary>
    [JsonIgnore]
    public object? ConstantValue => Constant?.ToClrValue();
}

/// <summary>One concrete ordered value bound to a SQL positional parameter.</summary>
public sealed class SqlParameter
{
    readonly object? value;

    internal SqlParameter(int position, string? binding, object? value)
    {
        Position = position;
        Binding = binding;
        this.value = value is byte[] bytes ? bytes.ToArray() : value;
    }

    /// <summary>One-based positional-parameter position.</summary>
    public int Position { get; }

    /// <summary>Rendered positional placeholder such as <c>$1</c>.</summary>
    public string Placeholder => $"${Position.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Runtime binding identity, or <see langword="null"/> when the value came from a captured constant.</summary>
    public string? Binding { get; }

    /// <summary>
    /// Provider-neutral parameter value. Mutable byte sequences are returned as defensive copies; every other
    /// supported value is immutable.
    /// </summary>
    public object? Value => value is byte[] bytes ? bytes.ToArray() : value;
}

/// <summary>
/// Immutable normalized SQL and deterministic parameter-slot metadata. Templates produced by the closed
/// construction API are injection-safe. Rehydrated command text is executable code and must come from a trusted
/// artifact source.
/// </summary>
public sealed class SqlCommandTemplate
{
    /// <summary>Creates and validates a persisted immutable SQL command template.</summary>
    /// <param name="text">Trusted SQL text containing positional placeholders.</param>
    /// <param name="parameters">Parameter slots in exact one-based position order.</param>
    /// <param name="dialect">Stable identity of the target construction profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Text is empty or parameter slots are null, repeated, or non-contiguous.</exception>
    [JsonConstructor]
    internal SqlCommandTemplate(string text, ImmutableArray<SqlParameterSlot> parameters, string dialect)
    {
        Text = Guard.RequireNotNullOrWhiteSpace(text);
        Dialect = Guard.RequireNotNullOrWhiteSpace(dialect);
        var normalized = parameters.IsDefault ? [] : parameters;
        if (normalized.Any(static parameter => parameter is null))
            throw new ArgumentException("SQL command-template parameters cannot contain null entries.", nameof(parameters));
        if (normalized.Where(static slot => slot.Kind == SqlParameterBindingKind.Runtime).GroupBy(static slot => slot.Binding, StringComparer.Ordinal).Any(static group => group.Count() != 1))
            throw new ArgumentException("A runtime binding must occupy exactly one parameter slot.", nameof(parameters));
        if (normalized.Select(static parameter => parameter.Position)
            .SequenceEqual(Enumerable.Range(1, normalized.Length)) is false)
        {
            throw new ArgumentException(
                "SQL command-template parameters must occupy contiguous one-based positions.",
                nameof(parameters));
        }
        ValidatePlaceholders(Text, normalized.Length);
        Parameters = normalized;
    }

    /// <summary>Identity of the dialect that produced this template.</summary>
    public string Dialect { get; }

    /// <summary>SQL command text using positional placeholders.</summary>
    public string Text { get; }

    /// <summary>Parameter slots in one-based placeholder order.</summary>
    public ImmutableArray<SqlParameterSlot> Parameters { get; }

    static void ValidatePlaceholders(string text, int slotCount)
    {
        HashSet<int> positions = [];
        var inQuotedIdentifier = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
            {
                if (inQuotedIdentifier && index + 1 < text.Length && text[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                inQuotedIdentifier = !inQuotedIdentifier;
                continue;
            }

            if (inQuotedIdentifier)
                continue;

            if (text[index] != '$' || index + 1 >= text.Length || !char.IsAsciiDigit(text[index + 1]))
                continue;

            var start = ++index;
            if (text[start] == '0')
                throw new ArgumentException("SQL placeholders must use canonical positive positions.", nameof(text));
            var position = 0;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                try
                {
                    position = checked(position * 10 + text[index] - '0');
                }
                catch (OverflowException exception)
                {
                    throw new ArgumentException("A SQL placeholder position is too large.", nameof(text), exception);
                }
                index++;
            }
            index--;
            positions.Add(position);
        }

        if (inQuotedIdentifier)
            throw new ArgumentException("SQL command text contains an unterminated quoted identifier.", nameof(text));

        if (!positions.SetEquals(Enumerable.Range(1, slotCount)))
        {
            throw new ArgumentException(
                "SQL command text placeholders must correspond exactly to declared parameter slots.",
                nameof(text));
        }
    }

    /// <summary>Binds a command template that has no runtime parameters.</summary>
    /// <param name="dialect">Explicit adapter-owned construction and parameter policy.</param>
    /// <exception cref="SqlConstructionException">A requested construct is unsupported by the dialect.</exception>
    /// <returns>A concrete immutable SQL statement containing captured constant values.</returns>
    /// <exception cref="ArgumentException">The template contains at least one runtime parameter.</exception>
    public SqlStatement Bind(SqlDialect dialect) => Bind(dialect, EmptyRuntimeParameters.Instance);

    /// <summary>Binds runtime values without rebuilding or reparsing the SQL tree.</summary>
    /// <param name="dialect">Exact target policy whose identity must match the compiled template.</param>
    /// <param name="runtimeParameters">Values keyed by the bindings supplied to <see cref="SqlExpression.RuntimeParameter"/>.</param>
    /// <returns>A concrete immutable SQL statement with ordered positional values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runtimeParameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A required binding is absent, an unknown binding is supplied, or a value is outside the exact provider-neutral
    /// SQL value domain.
    /// </exception>
    public SqlStatement Bind(SqlDialect dialect, IReadOnlyDictionary<string, object?> runtimeParameters)
    {
        ArgumentNullException.ThrowIfNull(runtimeParameters);
        ArgumentNullException.ThrowIfNull(dialect);
        if (dialect.Name != Dialect) throw new ArgumentException("The binding dialect differs from the compiled template.", nameof(dialect));
        var expected = Parameters
            .Where(static slot => slot.Kind == SqlParameterBindingKind.Runtime)
            .Select(static slot => slot.Binding!)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = runtimeParameters.Keys.Where(binding => !expected.Contains(binding)).ToArray();
        if (unknown.Length != 0)
        {
            throw new ArgumentException(
                $"The invocation contains unknown SQL parameter binding(s): {string.Join(", ", unknown.Order(StringComparer.Ordinal))}.",
                nameof(runtimeParameters));
        }

        var missing = expected.Where(binding => !runtimeParameters.ContainsKey(binding)).ToArray();
        if (missing.Length != 0)
        {
            throw new ArgumentException(
                $"The invocation is missing SQL parameter binding(s): {string.Join(", ", missing.Order(StringComparer.Ordinal))}.",
                nameof(runtimeParameters));
        }

        var values = ImmutableArray.CreateBuilder<SqlParameter>(Parameters.Length);
        foreach (var slot in Parameters)
        {
            var value = slot.Kind == SqlParameterBindingKind.Constant
                ? slot.ConstantValue
                : SqlConstant.NormalizeRuntimeValue(runtimeParameters[slot.Binding!]);
            dialect.ValidateParameter(value);
            values.Add(new(
                slot.Position,
                slot.Binding,
                value));
        }

        return new(Text, values.MoveToImmutable());
    }

    /// <summary>Binds runtime values from a strongly typed value dictionary.</summary>
    /// <typeparam name="TValue">Provider-neutral value type supplied by the caller.</typeparam>
    /// <param name="dialect">Exact target policy whose identity must match the compiled template.</param>
    /// <param name="runtimeParameters">
    /// Values keyed by the bindings supplied to <see cref="SqlExpression.RuntimeParameter"/>.
    /// </param>
    /// <returns>A concrete immutable SQL statement with boxed values in positional order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runtimeParameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A required binding is absent, an unknown binding is supplied, or a value is outside the exact provider-neutral
    /// SQL value domain.
    /// </exception>
    public SqlStatement Bind<TValue>(SqlDialect dialect, IReadOnlyDictionary<string, TValue> runtimeParameters)
    {
        ArgumentNullException.ThrowIfNull(runtimeParameters);
        ArgumentNullException.ThrowIfNull(dialect);
        if (dialect.Name != Dialect) throw new ArgumentException("The binding dialect differs from the compiled template.", nameof(dialect));
        Dictionary<string, object?> boxed = new(runtimeParameters.Count, StringComparer.Ordinal);
        foreach (var pair in runtimeParameters)
        {
            boxed.Add(pair.Key, pair.Value);
        }

        return Bind(dialect, boxed);
    }

    sealed class EmptyRuntimeParameters : Dictionary<string, object?>
    {
        public static EmptyRuntimeParameters Instance { get; } = new();
    }
}

/// <summary>Immutable SQL and concrete ordered positional values.</summary>
public sealed class SqlStatement
{
    internal SqlStatement(string text, ImmutableArray<SqlParameter> parameters)
    {
        Text = text;
        Parameters = parameters;
    }

    /// <summary>SQL text using positional placeholders.</summary>
    public string Text { get; }

    /// <summary>Concrete parameter values in placeholder order.</summary>
    public ImmutableArray<SqlParameter> Parameters { get; }
}

/// <summary>Immutable SQL SELECT tree that may be rendered directly or used as a derived table.</summary>
public sealed class SqlSelectQuery
{
    readonly SqlFromItem? from;
    readonly ImmutableArray<SqlSelectItem> selections;
    readonly ImmutableArray<SqlJoinItem> joins;
    readonly ImmutableArray<SqlExpression> predicates;
    readonly ImmutableArray<SqlExpression> groupings;
    readonly ImmutableArray<SqlOrdering> orderings;
    readonly bool distinct;
    readonly int? limit;
    readonly int? offset;

    internal void RequireScalarSubquery()
    {
        if (selections.Length != 1 || limit != 1)
            throw new ArgumentException("A scalar subquery must select exactly one column with an explicit limit of one.");
    }

    internal SqlSelectQuery(
        SqlFromItem? from,
        ImmutableArray<SqlSelectItem> selections,
        ImmutableArray<SqlJoinItem> joins,
        ImmutableArray<SqlExpression> predicates,
        ImmutableArray<SqlExpression> groupings,
        ImmutableArray<SqlOrdering> orderings,
        bool distinct,
        int? limit,
        int? offset)
    {
        this.from = from;
        this.selections = selections;
        this.joins = joins;
        this.predicates = predicates;
        this.groupings = groupings;
        this.orderings = orderings;
        this.distinct = distinct;
        this.limit = limit;
        this.offset = offset;
    }

    /// <summary>Renders this immutable query into a reusable command template.</summary>
    /// <param name="dialect">Explicit adapter-owned construction and parameter policy.</param>
    /// <exception cref="SqlConstructionException">A requested construct is unsupported by the dialect.</exception>
    /// <returns>Normalized SQL and deterministic positional-parameter slots.</returns>
    public SqlCommandTemplate ToCommandTemplate(SqlDialect dialect) => ToCommandTemplate(dialect, SqlFormatting.Compact);

    /// <summary>Renders this query with explicit, deterministic whitespace policy.</summary>
    /// <param name="dialect">Adapter-owned construction and parameter policy.</param>
    /// <param name="formatting">Whitespace layout; parameter order and query structure are preserved.</param>
    /// <returns>A reusable template containing the actual executable SQL in the selected layout.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dialect"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unknown.</exception>
    /// <exception cref="SqlConstructionException">The dialect does not support a requested construct.</exception>
    public SqlCommandTemplate ToCommandTemplate(SqlDialect dialect, SqlFormatting formatting)
    {
        SqlRenderContext context = new(dialect, formatting);
        StringBuilder builder = new();
        WriteTo(context, builder);
        return new(builder.ToString(), context.Parameters, dialect.Name);
    }

    internal void WriteTo(SqlRenderContext context, StringBuilder builder)
    {
        builder.Append(distinct ? "SELECT DISTINCT" : "SELECT");
        context.Indentation++;
        context.Separator(builder);
        for (var index = 0; index < selections.Length; index++)
        {
            if (index != 0)
            {
                builder.Append(',');
                context.Separator(builder);
            }
            selections[index].Expression.WriteTo(context, builder);
            builder.Append(" AS ");
            selections[index].Alias.WriteQuoted(context, builder);
        }
        context.Indentation--;

        if (from is not null)
        {
            context.Separator(builder).Append("FROM ");
            from.WriteTo(context, builder);
        }
        foreach (var join in joins)
        {
            if (join.Kind == SqlJoinKind.Right) context.Dialect.Require(SqlFeature.RightJoin);
            if (join.Kind == SqlJoinKind.Full) context.Dialect.Require(SqlFeature.FullJoin);
            context.Separator(builder).Append(SqlOperators.Text(join.Kind)).Append(' ');
            join.Source.WriteTo(context, builder);
            if (join.Predicate is not null)
            {
                builder.Append(" ON ");
                join.Predicate.WriteTo(context, builder);
            }
        }
        if (!predicates.IsDefaultOrEmpty)
        {
            context.Separator(builder).Append("WHERE ");
            WriteExpressionList(predicates, context, builder, " AND ");
        }
        if (!groupings.IsDefaultOrEmpty)
        {
            context.Separator(builder).Append("GROUP BY ");
            WriteExpressionList(groupings, context, builder, ", ");
        }
        if (!orderings.IsDefaultOrEmpty)
        {
            context.Separator(builder).Append("ORDER BY");
            context.Indentation++;
            context.Separator(builder);
            for (var index = 0; index < orderings.Length; index++)
            {
                if (index != 0)
                {
                    builder.Append(',');
                    context.Separator(builder);
                }
                orderings[index].WriteTo(context, builder);
            }
            context.Indentation--;
        }
        if (limit is { } pageLimit)
            context.Separator(builder).Append("LIMIT ").Append(pageLimit.ToString(CultureInfo.InvariantCulture));
        if (offset is { } pageOffset)
        {
            if (limit is null) context.Dialect.Require(SqlFeature.OffsetWithoutLimit);
            context.Separator(builder).Append("OFFSET ").Append(pageOffset.ToString(CultureInfo.InvariantCulture));
        }
    }

    static void WriteExpressionList(
        ImmutableArray<SqlExpression> expressions,
        SqlRenderContext context,
        StringBuilder builder,
        string separator)
    {
        for (var index = 0; index < expressions.Length; index++)
        {
            if (index != 0)
            {
                builder.Append(separator);
            }

            expressions[index].WriteTo(context, builder);
        }
    }
}

/// <summary>Mutable, single-threaded builder for an injection-safe SQL SELECT tree.</summary>
public sealed class SqlSelectBuilder
{
    readonly SqlFromItem? from;
    readonly List<SqlSelectItem> selections = [];
    readonly List<SqlJoinItem> joins = [];
    readonly List<SqlExpression> predicates = [];
    readonly List<SqlExpression> groupings = [];
    readonly List<SqlOrdering> orderings = [];
    readonly HashSet<SqlIdentifier> aliases = [];
    readonly HashSet<SqlIdentifier> selectionAliases = [];
    bool distinct;
    int? limit;
    int? offset;

    /// <summary>
    /// Creates a SELECT builder without a <c>FROM</c> source, suitable for projecting supplied runtime inputs as one
    /// derived row.
    /// </summary>
    public SqlSelectBuilder()
    {
    }

    /// <summary>Creates a SELECT builder rooted at a physical table.</summary>
    /// <param name="table">Injection-safe physical table name.</param>
    /// <param name="alias">Alias used to qualify columns from the table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is not a supported SQL identifier.</exception>
    public SqlSelectBuilder(SqlQualifiedTable table, string alias)
    {
        ArgumentNullException.ThrowIfNull(table);
        var identifier = new SqlIdentifier(alias);
        from = new SqlTableFromItem(table, identifier);
        aliases.Add(identifier);
    }

    /// <summary>Creates a SELECT builder rooted at an immutable derived query.</summary>
    /// <param name="query">Inner query used as the derived table.</param>
    /// <param name="alias">Alias used to qualify columns projected by the derived table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is not a supported SQL identifier.</exception>
    public SqlSelectBuilder(SqlSelectQuery query, string alias)
    {
        ArgumentNullException.ThrowIfNull(query);
        var identifier = new SqlIdentifier(alias);
        from = new SqlDerivedFromItem(query, identifier);
        aliases.Add(identifier);
    }

    /// <summary>Creates a SELECT source by expanding a runtime-bound native SQL array.</summary>
    /// <param name="arrayBinding">Nonempty runtime binding for the native array.</param>
    /// <param name="alias">Alias of the expanded source.</param>
    /// <param name="columnAlias">Alias of its single element column.</param>
    /// <returns>A mutable builder requiring <see cref="SqlFeature.ArrayUnnest"/> at rendering.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The binding is empty or white space, or an alias is invalid.</exception>
    public static SqlSelectBuilder FromArray(string arrayBinding, string alias, string columnAlias) =>
        new(arrayBinding, alias, columnAlias);

    SqlSelectBuilder(
        string arrayBinding,
        string alias,
        string columnAlias)
    {
        var identifier = new SqlIdentifier(alias);
        from = new SqlArrayUnnestFromItem(
            SqlExpression.RuntimeParameter(arrayBinding),
            identifier,
            new(columnAlias));
        aliases.Add(identifier);
    }

    /// <summary>Adds one projected expression with a safe result alias.</summary>
    /// <param name="expression">Expression to project.</param>
    /// <param name="alias">Result-column alias.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is invalid or duplicates another projection alias.</exception>
    public SqlSelectBuilder Select(SqlExpression expression, string alias)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var identifier = new SqlIdentifier(alias);
        if (!selectionAliases.Add(identifier))
        {
            throw new ArgumentException($"SQL projection alias '{alias}' is already present.", nameof(alias));
        }

        selections.Add(new(expression, identifier));
        return this;
    }

    /// <summary>Requests whole-projection duplicate elimination.</summary>
    /// <returns>This builder.</returns>
    public SqlSelectBuilder Distinct()
    {
        distinct = true;
        return this;
    }

    /// <summary>Adds a predicate combined with prior predicates using conjunction.</summary>
    /// <param name="predicate">Boolean predicate.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public SqlSelectBuilder Where(SqlExpression predicate)
    {
        predicates.Add(Guard.RequireNotNull(predicate));
        return this;
    }

    /// <summary>Adds a physical-table join.</summary>
    /// <param name="table">Physical right-side table.</param>
    /// <param name="alias">Unique right-side source alias.</param>
    /// <param name="kind">Join kind.</param>
    /// <param name="predicate">Join predicate, or <see langword="null"/> only for a cross join.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The alias is invalid or repeated, or predicate presence conflicts with <paramref name="kind"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">This builder was created without a <c>FROM</c> source.</exception>
    public SqlSelectBuilder Join(
        SqlQualifiedTable table,
        string alias,
        SqlJoinKind kind,
        SqlExpression? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        RequireFromForJoin();
        ValidateJoin(kind, predicate);
        joins.Add(new(new SqlTableFromItem(table, RequireNewAlias(alias)), kind, predicate));
        return this;
    }

    /// <summary>Adds a derived-query join.</summary>
    /// <param name="query">Immutable right-side query.</param>
    /// <param name="alias">Unique right-side source alias.</param>
    /// <param name="kind">Join kind.</param>
    /// <param name="predicate">Join predicate, or <see langword="null"/> only for a cross join.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The alias is invalid or repeated, or predicate presence conflicts with <paramref name="kind"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">This builder was created without a <c>FROM</c> source.</exception>
    public SqlSelectBuilder Join(
        SqlSelectQuery query,
        string alias,
        SqlJoinKind kind,
        SqlExpression? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireFromForJoin();
        ValidateJoin(kind, predicate);
        joins.Add(new(new SqlDerivedFromItem(query, RequireNewAlias(alias)), kind, predicate));
        return this;
    }

    /// <summary>Adds a correlated derived query as a lateral cross join.</summary>
    /// <param name="query">Right-side query, which may reference preceding source aliases.</param>
    /// <param name="alias">Unique right-side source alias.</param>
    /// <returns>This builder, requiring <see cref="SqlFeature.Lateral"/> at rendering.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The alias is invalid or repeated.</exception>
    /// <exception cref="InvalidOperationException">This builder has no FROM source.</exception>
    public SqlSelectBuilder CrossJoinLateral(
        SqlSelectQuery query,
        string alias)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireFromForJoin();
        joins.Add(new(
            new SqlLateralDerivedFromItem(query, RequireNewAlias(alias)),
            SqlJoinKind.Cross,
            Predicate: null));
        return this;
    }

    /// <summary>Adds one grouping expression.</summary>
    /// <param name="expression">Grouping expression.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    public SqlSelectBuilder GroupBy(SqlExpression expression)
    {
        groupings.Add(Guard.RequireNotNull(expression));
        return this;
    }

    /// <summary>Adds one deterministic ordering term.</summary>
    /// <param name="expression">Ordering key.</param>
    /// <param name="direction">Ordering direction.</param>
    /// <param name="nullPlacement">Explicit placement of null values.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="direction"/> or <paramref name="nullPlacement"/> is unsupported.
    /// </exception>
    public SqlSelectBuilder OrderBy(
        SqlExpression expression,
        SqlSortDirection direction = SqlSortDirection.Ascending,
        SqlNullPlacement nullPlacement = SqlNullPlacement.Last)
    {
        orderings.Add(new(expression, direction, nullPlacement));
        return this;
    }

    /// <summary>Sets the maximum number of rows returned.</summary>
    /// <param name="value">Positive row limit.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not positive.</exception>
    public SqlSelectBuilder Limit(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A SQL row limit must be positive.");
        }

        limit = value;
        return this;
    }

    /// <summary>Sets the number of ordered rows skipped.</summary>
    /// <param name="value">Non-negative row offset.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public SqlSelectBuilder Offset(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A SQL row offset cannot be negative.");
        }

        offset = value;
        return this;
    }

    /// <summary>Sets offset and limit paging together.</summary>
    /// <param name="offset">Non-negative number of rows skipped.</param>
    /// <param name="limit">Positive maximum number of rows returned.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative or <paramref name="limit"/> is not positive.</exception>
    public SqlSelectBuilder OffsetLimit(int offset, int limit) =>
        Offset(offset).Limit(limit);

    /// <summary>Builds an immutable SELECT tree suitable for direct rendering or derived-table composition.</summary>
    /// <returns>An immutable snapshot of the builder.</returns>
    /// <exception cref="InvalidOperationException">No projection has been configured.</exception>
    public SqlSelectQuery BuildQuery()
    {
        if (selections.Count == 0)
        {
            throw new InvalidOperationException("A SQL SELECT query requires at least one projected expression.");
        }

        return new(
            from,
            [.. selections],
            [.. joins],
            [.. predicates],
            [.. groupings],
            [.. orderings],
            distinct,
            limit,
            offset);
    }

    /// <summary>Builds an immutable reusable SQL command template.</summary>
    /// <param name="dialect">Explicit adapter-owned construction and parameter policy.</param>
    /// <exception cref="SqlConstructionException">A requested construct is unsupported by the dialect.</exception>
    /// <returns>Normalized SQL and deterministic positional-parameter slots.</returns>
    /// <exception cref="InvalidOperationException">No projection has been configured.</exception>
    public SqlCommandTemplate BuildTemplate(SqlDialect dialect) => BuildQuery().ToCommandTemplate(dialect);

    /// <summary>Builds a reusable template with explicit, deterministic whitespace policy.</summary>
    /// <param name="dialect">Adapter-owned construction and parameter policy.</param>
    /// <param name="formatting">Whitespace layout; query structure and parameter order are unchanged.</param>
    /// <returns>The actual executable SQL and deterministic parameter slots.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dialect"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unknown.</exception>
    /// <exception cref="SqlConstructionException">The dialect does not support a requested construct.</exception>
    /// <exception cref="InvalidOperationException">No projection has been configured.</exception>
    public SqlCommandTemplate BuildTemplate(SqlDialect dialect, SqlFormatting formatting) =>
        BuildQuery().ToCommandTemplate(dialect, formatting);

    /// <summary>Builds a concrete statement when the query contains no runtime-bound parameters.</summary>
    /// <param name="dialect">Explicit adapter-owned construction and parameter policy.</param>
    /// <exception cref="SqlConstructionException">A requested construct is unsupported by the dialect.</exception>
    /// <returns>Normalized SQL and ordered captured constant values.</returns>
    /// <exception cref="ArgumentException">The query contains a runtime-bound parameter.</exception>
    /// <exception cref="InvalidOperationException">No projection has been configured.</exception>
    public SqlStatement Build(SqlDialect dialect) => BuildTemplate(dialect).Bind(dialect);

    static void ValidateJoin(
        SqlJoinKind kind,
        SqlExpression? predicate)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported SQL join kind.");
        }

        if (kind == SqlJoinKind.Cross != (predicate is null))
        {
            throw new ArgumentException(
                "A cross join prohibits a predicate and every other join kind requires one.",
                nameof(predicate));
        }
    }

    void RequireFromForJoin()
    {
        if (from is null)
        {
            throw new InvalidOperationException(
                "A SQL SELECT without a FROM source cannot contain joins; wrap it as a derived source first.");
        }
    }

    SqlIdentifier RequireNewAlias(string alias)
    {
        var identifier = new SqlIdentifier(alias);
        if (!aliases.Add(identifier))
        {
            throw new ArgumentException($"SQL source alias '{alias}' is already present.", nameof(alias));
        }

        return identifier;
    }
}

internal abstract record SqlFromItem(SqlIdentifier Alias)
{
    public abstract void WriteTo(SqlRenderContext context, StringBuilder builder);
}

internal sealed record SqlTableFromItem(
    SqlQualifiedTable Table,
    SqlIdentifier SourceAlias) : SqlFromItem(SourceAlias)
{
    public override void WriteTo(SqlRenderContext context, StringBuilder builder)
    {
        Table.WriteTo(context, builder);
        builder.Append(" AS ");
        Alias.WriteQuoted(context, builder);
    }
}

internal sealed record SqlDerivedFromItem(
    SqlSelectQuery Query,
    SqlIdentifier SourceAlias) : SqlFromItem(SourceAlias)
{
    public override void WriteTo(SqlRenderContext context, StringBuilder builder)
    {
        builder.Append('(');
        context.WriteNestedQuery(Query, builder);
        builder.Append(") AS ");
        Alias.WriteQuoted(context, builder);
    }
}

internal sealed record SqlLateralDerivedFromItem(
    SqlSelectQuery Query,
    SqlIdentifier SourceAlias) : SqlFromItem(SourceAlias)
{
    public override void WriteTo(SqlRenderContext context, StringBuilder builder)
    {
        context.Dialect.Require(SqlFeature.Lateral);
        builder.Append("LATERAL (");
        context.WriteNestedQuery(Query, builder);
        builder.Append(") AS ");
        Alias.WriteQuoted(context, builder);
    }
}

internal sealed record SqlArrayUnnestFromItem(
    SqlExpression Array,
    SqlIdentifier SourceAlias,
    SqlIdentifier ColumnAlias) : SqlFromItem(SourceAlias)
{
    public override void WriteTo(SqlRenderContext context, StringBuilder builder)
    {
        context.Dialect.Require(SqlFeature.ArrayUnnest);
        builder.Append("unnest(");
        Array.WriteTo(context, builder);
        builder.Append(") AS ");
        Alias.WriteQuoted(context, builder);
        builder.Append('(');
        ColumnAlias.WriteQuoted(context, builder);
        builder.Append(')');
    }
}

internal sealed record SqlSelectItem(
    SqlExpression Expression,
    SqlIdentifier Alias);

internal sealed record SqlJoinItem(
    SqlFromItem Source,
    SqlJoinKind Kind,
    SqlExpression? Predicate);

internal sealed class SqlRenderContext
{
    internal SqlRenderContext(SqlDialect dialect, SqlFormatting formatting = SqlFormatting.Compact)
    {
        Dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        if (!Enum.IsDefined(formatting)) throw new ArgumentOutOfRangeException(nameof(formatting));
        Formatting = formatting;
    }
    internal SqlDialect Dialect { get; }
    internal SqlFormatting Formatting { get; }
    internal int Indentation { get; set; }
    internal StringBuilder Separator(StringBuilder builder) => Formatting == SqlFormatting.Compact
        ? builder.Append(' ') : LineBreak(builder);
    internal StringBuilder LineBreak(StringBuilder builder)
    {
        if (Formatting == SqlFormatting.Indented) builder.Append('\n').Append(' ', Indentation * 4);
        return builder;
    }
    internal void WriteNestedQuery(SqlSelectQuery query, StringBuilder builder)
    {
        Indentation++;
        LineBreak(builder);
        query.WriteTo(this, builder);
        Indentation--;
        LineBreak(builder);
    }
    readonly SqlParameterSlots<SqlParameterSlot> parameters = new();
    public ImmutableArray<SqlParameterSlot> Parameters => parameters.Snapshot();
    public string AddConstant(SqlConstant value)
    {
        Dialect.ValidateParameter(value.ToClrValue());
        return Placeholder(parameters.AddConstant(position =>
            new(position + 1, SqlParameterBindingKind.Constant, binding: null, value)) + 1);
    }
    public string AddRuntime(string binding) => Placeholder(parameters.GetOrAddRuntime(binding, position =>
        new(position + 1, SqlParameterBindingKind.Runtime, binding, constantValue: null)) + 1);

    static string Placeholder(int position) => $"${position.ToString(CultureInfo.InvariantCulture)}";
}

internal static class SqlOperators
{
    public static string Text(SqlBinaryOperator @operator) => @operator switch
    {
        SqlBinaryOperator.Equal => "=",
        SqlBinaryOperator.NotEqual => "<>",
        SqlBinaryOperator.GreaterThan => ">",
        SqlBinaryOperator.GreaterThanOrEqual => ">=",
        SqlBinaryOperator.LessThan => "<",
        SqlBinaryOperator.LessThanOrEqual => "<=",
        SqlBinaryOperator.And => "AND",
        SqlBinaryOperator.Or => "OR",
        SqlBinaryOperator.Add => "+",
        SqlBinaryOperator.Subtract => "-",
        SqlBinaryOperator.Multiply => "*",
        SqlBinaryOperator.Divide => "/",
        SqlBinaryOperator.Like => "LIKE",
        SqlBinaryOperator.IsNotDistinctFrom => "IS NOT DISTINCT FROM",
        SqlBinaryOperator.IsDistinctFrom => "IS DISTINCT FROM",
        _ => throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported SQL binary operator.")
    };

    public static string Text(SqlUnaryOperator @operator) => @operator switch
    {
        SqlUnaryOperator.Not => "NOT ",
        SqlUnaryOperator.Negate => "-",
        _ => throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported SQL unary operator.")
    };

    public static string Text(SqlJoinKind kind) => kind switch
    {
        SqlJoinKind.Inner => "INNER JOIN",
        SqlJoinKind.Left => "LEFT JOIN",
        SqlJoinKind.Right => "RIGHT JOIN",
        SqlJoinKind.Full => "FULL JOIN",
        SqlJoinKind.Cross => "CROSS JOIN",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported SQL join kind.")
    };
}

static class SqlFunctions
{
    public static void ValidateArity(SqlFunction function, int count, string parameterName)
    {
        var valid = function switch
        {
            SqlFunction.Length or SqlFunction.Lower or SqlFunction.Upper => count == 1,
            SqlFunction.Right or SqlFunction.Left or SqlFunction.StringPosition => count == 2,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                $"SQL function '{function}' does not accept {count.ToString(CultureInfo.InvariantCulture)} argument(s).",
                parameterName);
        }
    }
}
