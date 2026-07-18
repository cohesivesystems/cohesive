using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>How one PostgreSQL command-template parameter obtains its value.</summary>
public enum PostgresSqlParameterBindingKind
{
    /// <summary>The parameter value is captured by the immutable SQL expression tree.</summary>
    Constant = 0,

    /// <summary>The parameter value is supplied when the command template is bound.</summary>
    Runtime = 1
}

/// <summary>Supported PostgreSQL binary operators.</summary>
public enum PostgresSqlBinaryOperator
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

    /// <summary>PostgreSQL pattern matching with SQL <c>LIKE</c> semantics.</summary>
    Like = 12,

    /// <summary>Null-safe equality using <c>IS NOT DISTINCT FROM</c>.</summary>
    IsNotDistinctFrom = 13,

    /// <summary>Null-safe inequality using <c>IS DISTINCT FROM</c>.</summary>
    IsDistinctFrom = 14
}

/// <summary>Supported PostgreSQL unary operators.</summary>
public enum PostgresSqlUnaryOperator
{
    /// <summary>Boolean negation.</summary>
    Not = 0,

    /// <summary>Numeric negation.</summary>
    Negate = 1
}

/// <summary>Supported PostgreSQL scalar functions.</summary>
public enum PostgresSqlFunction
{
    /// <summary>Returns the number of characters in a text value.</summary>
    Length = 0,

    /// <summary>Returns a requested number of characters from the right side of a text value.</summary>
    Right = 1,

    /// <summary>Converts text to lower case according to the effective PostgreSQL collation.</summary>
    Lower = 2,

    /// <summary>Converts text to upper case according to the effective PostgreSQL collation.</summary>
    Upper = 3,

    /// <summary>Returns a requested number of characters from the left side of a text value.</summary>
    Left = 4,

    /// <summary>Returns the one-based position of a text substring, or zero when absent.</summary>
    StringPosition = 5
}

/// <summary>Supported PostgreSQL aggregate functions.</summary>
public enum PostgresSqlAggregateFunction
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

/// <summary>PostgreSQL join syntax emitted by <see cref="PostgresSqlSelectBuilder"/>.</summary>
public enum PostgresSqlJoinKind
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

/// <summary>Direction of one PostgreSQL ordering term.</summary>
public enum PostgresSqlSortDirection
{
    /// <summary>Orders lower values before higher values.</summary>
    Ascending = 0,

    /// <summary>Orders higher values before lower values.</summary>
    Descending = 1
}

/// <summary>Placement of SQL nulls within one PostgreSQL ordering term.</summary>
public enum PostgresSqlNullPlacement
{
    /// <summary>Places null values before non-null values.</summary>
    First = 0,

    /// <summary>Places null values after non-null values.</summary>
    Last = 1
}

static class PostgresSqlUtf8
{
    static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static int GetByteCount(string value, string parameterName)
    {
        try
        {
            return Strict.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("A PostgreSQL UTF-8 value must contain valid Unicode.", parameterName, exception);
        }
    }

    public static string RequireText(string value, string parameterName)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("A PostgreSQL text value cannot contain a zero character.", parameterName);
        _ = GetByteCount(value, parameterName);
        return value;
    }
}

/// <summary>An injection-safe PostgreSQL identifier rendered with double-quote escaping.</summary>
public readonly record struct PostgresSqlIdentifier
{
    /// <summary>Standard PostgreSQL identifier limit in UTF-8 bytes.</summary>
    public const int StandardMaxUtf8ByteLength = 63;

    /// <summary>Creates a PostgreSQL identifier.</summary>
    /// <param name="value">Unquoted identifier value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty, contains the zero character, or exceeds the standard PostgreSQL
    /// identifier limit of <see cref="StandardMaxUtf8ByteLength"/> UTF-8 bytes, or is not valid Unicode.
    /// </exception>
    public PostgresSqlIdentifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            throw new ArgumentException("A PostgreSQL identifier cannot be empty.", nameof(value));
        }

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A PostgreSQL identifier cannot contain a zero character.", nameof(value));
        }

        var utf8Length = PostgresSqlUtf8.GetByteCount(value, nameof(value));

        if (utf8Length > StandardMaxUtf8ByteLength)
        {
            throw new ArgumentException(
                $"A PostgreSQL identifier cannot exceed {StandardMaxUtf8ByteLength.ToString(CultureInfo.InvariantCulture)} UTF-8 bytes.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Unquoted identifier value.</summary>
    public string Value { get; }

    /// <summary>Returns the unquoted identifier value.</summary>
    /// <returns>The unquoted identifier value.</returns>
    public override string ToString() => Value;

    internal void WriteQuoted(StringBuilder builder) =>
        builder.Append('"').Append(Value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
}

/// <summary>An injection-safe optionally schema-qualified PostgreSQL table name.</summary>
public sealed record PostgresSqlQualifiedTable
{
    /// <summary>Creates an unqualified PostgreSQL table name.</summary>
    /// <param name="tableName">Physical table identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tableName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="tableName"/> is not a supported PostgreSQL identifier.</exception>
    public PostgresSqlQualifiedTable(string tableName)
        : this(schemaName: null, new PostgresSqlIdentifier(tableName))
    {
    }

    /// <summary>Creates a schema-qualified PostgreSQL table name.</summary>
    /// <param name="schemaName">Physical schema identifier.</param>
    /// <param name="tableName">Physical table identifier.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is not a supported PostgreSQL identifier.</exception>
    public PostgresSqlQualifiedTable(string schemaName, string tableName)
        : this(new PostgresSqlIdentifier(schemaName), new PostgresSqlIdentifier(tableName))
    {
    }

    PostgresSqlQualifiedTable(PostgresSqlIdentifier? schemaName, PostgresSqlIdentifier tableName)
    {
        SchemaName = schemaName;
        TableName = tableName;
    }

    /// <summary>Optional physical schema identifier.</summary>
    public PostgresSqlIdentifier? SchemaName { get; }

    /// <summary>Physical table identifier.</summary>
    public PostgresSqlIdentifier TableName { get; }

    internal void WriteTo(StringBuilder builder)
    {
        if (SchemaName is { } schema)
        {
            schema.WriteQuoted(builder);
            builder.Append('.');
        }

        TableName.WriteQuoted(builder);
    }
}

/// <summary>One key and continuation value participating in structural keyset pagination.</summary>
public sealed record PostgresSqlKeysetTerm
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
    public PostgresSqlKeysetTerm(
        PostgresSqlExpression key,
        PostgresSqlExpression continuation,
        PostgresSqlSortDirection direction = PostgresSqlSortDirection.Ascending,
        PostgresSqlNullPlacement nullPlacement = PostgresSqlNullPlacement.Last)
    {
        Key = Guard.RequireNotNull(key);
        Continuation = Guard.RequireNotNull(continuation);
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported PostgreSQL sort direction.");
        }

        if (!Enum.IsDefined(nullPlacement))
        {
            throw new ArgumentOutOfRangeException(nameof(nullPlacement), nullPlacement, "Unsupported PostgreSQL null placement.");
        }

        Direction = direction;
        NullPlacement = nullPlacement;
    }

    /// <summary>Expression evaluated for the candidate row.</summary>
    public PostgresSqlExpression Key { get; }

    /// <summary>Expression containing the preceding page's final key value.</summary>
    public PostgresSqlExpression Continuation { get; }

    /// <summary>Ordering direction applied to this key.</summary>
    public PostgresSqlSortDirection Direction { get; }

    /// <summary>Explicit null placement applied to this key.</summary>
    public PostgresSqlNullPlacement NullPlacement { get; }
}

/// <summary>Closed, injection-safe PostgreSQL scalar-expression tree.</summary>
public abstract record PostgresSqlExpression
{
    /// <summary>Initializes a PostgreSQL expression.</summary>
    private protected PostgresSqlExpression()
    {
    }

    /// <summary>Creates a column reference qualified by a source alias.</summary>
    /// <param name="sourceAlias">Table or derived-table alias.</param>
    /// <param name="columnName">Physical column identifier.</param>
    /// <returns>A qualified column expression.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is not a supported PostgreSQL identifier.</exception>
    public static PostgresSqlExpression Column(string sourceAlias, string columnName) =>
        new ColumnExpression(new(sourceAlias), new(columnName));

    /// <summary>Creates an unqualified column reference.</summary>
    /// <param name="columnName">Physical column identifier.</param>
    /// <returns>An unqualified column expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="columnName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="columnName"/> is not a supported PostgreSQL identifier.</exception>
    public static PostgresSqlExpression UnqualifiedColumn(string columnName) =>
        new ColumnExpression(SourceAlias: null, new(columnName));

    /// <summary>Creates a parameterized constant expression.</summary>
    /// <param name="value">
    /// Provider-neutral parameter value: <see langword="null"/>, Boolean, 32- or 64-bit integer, decimal, string,
    /// UUID, date, civil timestamp, instant, or bytes. Mutable byte arrays are captured immediately.
    /// </param>
    /// <returns>An expression whose value is retained by the command template.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> has an unsupported CLR type or timestamp kind, or a string is outside the exact
    /// PostgreSQL UTF-8 text domain.
    /// </exception>
    public static PostgresSqlExpression Constant(object? value) =>
        new ConstantExpression(PostgresSqlConstant.Capture(value));

    /// <summary>Creates a runtime-bound parameter expression.</summary>
    /// <param name="binding">Stable application binding used by <see cref="PostgresSqlCommandTemplate.Bind(IReadOnlyDictionary{string, object?})"/>.</param>
    /// <returns>An expression rendered as a deterministically allocated positional parameter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="binding"/> is empty or white space.</exception>
    public static PostgresSqlExpression RuntimeParameter(string binding) =>
        new RuntimeParameterExpression(Guard.RequireNotNullOrWhiteSpace(binding));

    /// <summary>Applies one explicitly selected PostgreSQL collation to an expression.</summary>
    /// <param name="operand">Text expression to collate.</param>
    /// <param name="collation">PostgreSQL collation identifier.</param>
    /// <returns>An expression rendered with a safely quoted <c>COLLATE</c> clause.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="collation"/> is not a supported identifier or is schema-qualified. This profile accepts
    /// one unqualified collation identifier so quoting cannot change qualification semantics.
    /// </exception>
    public static PostgresSqlExpression Collate(PostgresSqlExpression operand, string collation) =>
        new CollateExpression(Guard.RequireNotNull(operand), RequireUnqualifiedCollation(collation));

    static PostgresSqlIdentifier RequireUnqualifiedCollation(string collation)
    {
        var identifier = new PostgresSqlIdentifier(collation);
        if (collation.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A PostgreSQL collation must be an unqualified identifier in this SQL construction profile.",
                nameof(collation));
        }
        return identifier;
    }

    /// <summary>Creates a unary expression.</summary>
    /// <param name="operator">Closed PostgreSQL unary operator.</param>
    /// <param name="operand">Operand expression.</param>
    /// <returns>A unary PostgreSQL expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operand"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operator"/> is unsupported.</exception>
    public static PostgresSqlExpression Unary(
        PostgresSqlUnaryOperator @operator,
        PostgresSqlExpression operand)
    {
        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported PostgreSQL unary operator.");
        }

        return new UnaryExpression(@operator, Guard.RequireNotNull(operand));
    }

    /// <summary>Creates a binary expression.</summary>
    /// <param name="operator">Closed PostgreSQL binary operator.</param>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>A binary PostgreSQL expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="left"/> or <paramref name="right"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operator"/> is unsupported.</exception>
    public static PostgresSqlExpression Binary(
        PostgresSqlBinaryOperator @operator,
        PostgresSqlExpression left,
        PostgresSqlExpression right)
    {
        if (!Enum.IsDefined(@operator))
        {
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported PostgreSQL binary operator.");
        }

        return new BinaryExpression(@operator, Guard.RequireNotNull(left), Guard.RequireNotNull(right));
    }

    /// <summary>Creates an SQL <c>IS NULL</c> predicate.</summary>
    /// <param name="operand">Value tested for null.</param>
    /// <returns>A Boolean null-test expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operand"/> is <see langword="null"/>.</exception>
    public static PostgresSqlExpression IsNull(PostgresSqlExpression operand) =>
        new NullTestExpression(Guard.RequireNotNull(operand), NullExpected: true);

    /// <summary>Creates an SQL <c>IS NOT NULL</c> predicate.</summary>
    /// <param name="operand">Value tested for non-null presence.</param>
    /// <returns>A Boolean non-null-test expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operand"/> is <see langword="null"/>.</exception>
    public static PostgresSqlExpression IsNotNull(PostgresSqlExpression operand) =>
        new NullTestExpression(Guard.RequireNotNull(operand), NullExpected: false);

    /// <summary>Creates a closed PostgreSQL scalar-function call.</summary>
    /// <param name="function">Supported scalar function.</param>
    /// <param name="arguments">Function arguments.</param>
    /// <returns>A scalar-function expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> or an argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The argument count does not match the selected function.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="function"/> is unsupported.</exception>
    public static PostgresSqlExpression Function(
        PostgresSqlFunction function,
        params PostgresSqlExpression[] arguments)
    {
        if (!Enum.IsDefined(function))
        {
            throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported PostgreSQL scalar function.");
        }

        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(static argument => argument is null))
        {
            throw new ArgumentNullException(nameof(arguments), "PostgreSQL function arguments cannot contain null entries.");
        }

        PostgresSqlFunctions.ValidateArity(function, arguments.Length, nameof(arguments));
        return new FunctionExpression(function, [.. arguments]);
    }

    /// <summary>Creates a PostgreSQL aggregate expression with an optional aggregate-local filter.</summary>
    /// <param name="function">Supported aggregate function.</param>
    /// <param name="operand">
    /// Aggregate operand, or <see langword="null"/> only to request <c>COUNT(*)</c>.
    /// </param>
    /// <param name="filter">Optional predicate emitted through PostgreSQL's aggregate <c>FILTER</c> clause.</param>
    /// <param name="distinct">Whether duplicate operand values are removed before aggregation.</param>
    /// <returns>An aggregate expression.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="operand"/> is <see langword="null"/> for a function other than count, or
    /// <paramref name="distinct"/> is true for <c>COUNT(*)</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="function"/> is unsupported.</exception>
    public static PostgresSqlExpression Aggregate(
        PostgresSqlAggregateFunction function,
        PostgresSqlExpression? operand = null,
        PostgresSqlExpression? filter = null,
        bool distinct = false)
    {
        if (!Enum.IsDefined(function))
        {
            throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported PostgreSQL aggregate function.");
        }

        if (operand is null && function != PostgresSqlAggregateFunction.Count)
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
    public static PostgresSqlExpression Conditional(
        PostgresSqlExpression test,
        PostgresSqlExpression whenTrue,
        PostgresSqlExpression whenFalse) =>
        new ConditionalExpression(
            Guard.RequireNotNull(test),
            Guard.RequireNotNull(whenTrue),
            Guard.RequireNotNull(whenFalse));

    /// <summary>Creates a SQL <c>COALESCE</c> expression.</summary>
    /// <param name="values">Two or more values evaluated from left to right.</param>
    /// <returns>A coalescing expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> or an entry is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Fewer than two values are supplied.</exception>
    public static PostgresSqlExpression Coalesce(params PostgresSqlExpression[] values)
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
    public static PostgresSqlExpression KeysetAfter(ImmutableArray<PostgresSqlKeysetTerm> terms)
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

        PostgresSqlExpression? predicate = null;
        PostgresSqlExpression? equalPrefix = null;
        foreach (var term in normalized)
        {
            var after = AfterTerm(term);
            var branch = equalPrefix is null
                ? after
                : Binary(PostgresSqlBinaryOperator.And, equalPrefix, after);
            predicate = predicate is null
                ? branch
                : Binary(PostgresSqlBinaryOperator.Or, predicate, branch);

            var equal = Binary(
                PostgresSqlBinaryOperator.IsNotDistinctFrom,
                term.Key,
                term.Continuation);
            equalPrefix = equalPrefix is null
                ? equal
                : Binary(PostgresSqlBinaryOperator.And, equalPrefix, equal);
        }

        return predicate!;
    }

    static PostgresSqlExpression AfterTerm(PostgresSqlKeysetTerm term)
    {
        var concreteComparison = Binary(
            term.Direction == PostgresSqlSortDirection.Ascending
                ? PostgresSqlBinaryOperator.GreaterThan
                : PostgresSqlBinaryOperator.LessThan,
            term.Key,
            term.Continuation);
        var bothConcrete = Binary(
            PostgresSqlBinaryOperator.And,
            IsNotNull(term.Key),
            Binary(
                PostgresSqlBinaryOperator.And,
                IsNotNull(term.Continuation),
                concreteComparison));
        var crossesNullBoundary = term.NullPlacement == PostgresSqlNullPlacement.First
            ? Binary(
                PostgresSqlBinaryOperator.And,
                IsNull(term.Continuation),
                IsNotNull(term.Key))
            : Binary(
                PostgresSqlBinaryOperator.And,
                IsNotNull(term.Continuation),
                IsNull(term.Key));
        return Binary(PostgresSqlBinaryOperator.Or, crossesNullBoundary, bothConcrete);
    }

    internal abstract void WriteTo(PostgresSqlRenderContext context, StringBuilder builder);

    sealed record ColumnExpression(
        PostgresSqlIdentifier? SourceAlias,
        PostgresSqlIdentifier ColumnName) : PostgresSqlExpression
    {
        internal override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
        {
            if (SourceAlias is { } alias)
            {
                alias.WriteQuoted(builder);
                builder.Append('.');
            }

            ColumnName.WriteQuoted(builder);
        }
    }

    sealed record ConstantExpression(PostgresSqlConstant Value) : PostgresSqlExpression
    {
        internal override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder) =>
            builder.Append(context.AddConstant(Value));
    }

    sealed record RuntimeParameterExpression(string Binding) : PostgresSqlExpression
    {
        internal override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder) =>
            builder.Append(context.AddRuntime(Binding));
    }

    sealed record CollateExpression(
        PostgresSqlExpression Operand,
        PostgresSqlIdentifier Collation) : PostgresSqlExpression
    {
        internal override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
        {
            builder.Append('(');
            Operand.WriteTo(context, builder);
            builder.Append(" COLLATE ");
            Collation.WriteQuoted(builder);
            builder.Append(')');
        }
    }

    sealed record UnaryExpression(
        PostgresSqlUnaryOperator Operator,
        PostgresSqlExpression Operand) : PostgresSqlExpression
    {
        internal override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
        {
            builder.Append('(').Append(PostgresSqlOperators.Text(Operator));
            Operand.WriteTo(context, builder);
            builder.Append(')');
        }
    }

    sealed record BinaryExpression(
        PostgresSqlBinaryOperator Operator,
        PostgresSqlExpression Left,
        PostgresSqlExpression Right) : PostgresSqlExpression
    {
        internal override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
        {
            builder.Append('(');
            Left.WriteTo(context, builder);
            builder.Append(' ').Append(PostgresSqlOperators.Text(Operator)).Append(' ');
            Right.WriteTo(context, builder);
            builder.Append(')');
        }
    }

    sealed record NullTestExpression(
        PostgresSqlExpression Operand,
        bool NullExpected) : PostgresSqlExpression
    {
        internal override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
        {
            builder.Append('(');
            Operand.WriteTo(context, builder);
            builder.Append(NullExpected ? " IS NULL)" : " IS NOT NULL)");
        }
    }

    sealed record FunctionExpression(
        PostgresSqlFunction FunctionKind,
        ImmutableArray<PostgresSqlExpression> Arguments) : PostgresSqlExpression
    {
        internal override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
        {
            builder.Append(PostgresSqlFunctions.Name(FunctionKind)).Append('(');
            WriteExpressions(Arguments, context, builder);
            builder.Append(')');
        }
    }

    sealed record AggregateExpression(
        PostgresSqlAggregateFunction FunctionKind,
        PostgresSqlExpression? Operand,
        PostgresSqlExpression? Filter,
        bool Distinct) : PostgresSqlExpression
    {
        internal override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
        {
            builder.Append(PostgresSqlFunctions.Name(FunctionKind)).Append('(');
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
                builder.Append(" FILTER (WHERE ");
                Filter.WriteTo(context, builder);
                builder.Append(')');
            }
        }
    }

    sealed record ConditionalExpression(
        PostgresSqlExpression Test,
        PostgresSqlExpression WhenTrue,
        PostgresSqlExpression WhenFalse) : PostgresSqlExpression
    {
        internal override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
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
        ImmutableArray<PostgresSqlExpression> Values) : PostgresSqlExpression
    {
        internal override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
        {
            builder.Append("COALESCE(");
            WriteExpressions(Values, context, builder);
            builder.Append(')');
        }
    }

    static void WriteExpressions(
        ImmutableArray<PostgresSqlExpression> expressions,
        PostgresSqlRenderContext context,
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

/// <summary>Portable type tag for one captured PostgreSQL command-template constant.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostgresSqlConstantKind
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

    /// <summary>Timestamp with an explicit offset.</summary>
    TimestampWithTimeZone = 9,

    /// <summary>Byte sequence.</summary>
    Bytea = 10
}

/// <summary>Tagged portable representation of one captured PostgreSQL parameter value.</summary>
public sealed record PostgresSqlConstant
{
    /// <summary>Creates and validates a tagged portable constant.</summary>
    /// <param name="kind">Portable scalar kind.</param>
    /// <param name="value">Invariant string representation, or <see langword="null"/> only for SQL null.</param>
    /// <exception cref="ArgumentException">The value is absent, malformed, or conflicts with <paramref name="kind"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    [JsonConstructor]
    public PostgresSqlConstant(PostgresSqlConstantKind kind, string? value)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported PostgreSQL constant kind.");
        if (kind == PostgresSqlConstantKind.Null != (value is null))
            throw new ArgumentException("Only a tagged SQL null may omit its portable value.", nameof(value));
        Kind = kind;
        Value = value;
        _ = ToClrValue();
    }

    /// <summary>Portable scalar kind.</summary>
    public PostgresSqlConstantKind Kind { get; }

    /// <summary>Invariant string representation, or <see langword="null"/> for SQL null.</summary>
    public string? Value { get; }

    internal static PostgresSqlConstant Capture(object? value)
    {
        if (value is DateTime timestamp && (timestamp.Kind != DateTimeKind.Unspecified || timestamp.Ticks % 10 != 0))
        {
            throw new ArgumentException(
                "A PostgreSQL civil timestamp constant must be unspecified-kind and microsecond-aligned.",
                nameof(value));
        }
        if (value is DateTimeOffset instant && instant.Ticks % 10 != 0)
            throw new ArgumentException("A PostgreSQL instant constant must be microsecond-aligned.", nameof(value));

        return value switch
        {
            null => new(PostgresSqlConstantKind.Null, null),
            bool item => new(PostgresSqlConstantKind.Boolean, item ? "true" : "false"),
            int item => new(PostgresSqlConstantKind.Int32, item.ToString(CultureInfo.InvariantCulture)),
            long item => new(PostgresSqlConstantKind.Int64, item.ToString(CultureInfo.InvariantCulture)),
            decimal item => new(PostgresSqlConstantKind.Decimal, item.ToString(CultureInfo.InvariantCulture)),
            string item => new(PostgresSqlConstantKind.String, PostgresSqlUtf8.RequireText(item, nameof(value))),
            Guid item => new(PostgresSqlConstantKind.Uuid, item.ToString("D", CultureInfo.InvariantCulture)),
            DateOnly item => new(PostgresSqlConstantKind.Date, item.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            DateTime item => new(PostgresSqlConstantKind.Timestamp, item.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset item =>
                new(PostgresSqlConstantKind.TimestampWithTimeZone, item.ToString("O", CultureInfo.InvariantCulture)),
            byte[] item => new(PostgresSqlConstantKind.Bytea, Convert.ToBase64String(item)),
            _ => throw new ArgumentException(
                $"CLR value type '{value.GetType().FullName}' has no portable PostgreSQL command-template constant encoding.",
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
                "A PostgreSQL runtime parameter must use a supported, exact provider-neutral CLR value.",
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
                PostgresSqlConstantKind.Null when Value is null => null,
                PostgresSqlConstantKind.Boolean => bool.Parse(Value!),
                PostgresSqlConstantKind.Int32 => int.Parse(Value!, NumberStyles.Integer, CultureInfo.InvariantCulture),
                PostgresSqlConstantKind.Int64 => long.Parse(Value!, NumberStyles.Integer, CultureInfo.InvariantCulture),
                PostgresSqlConstantKind.Decimal => decimal.Parse(Value!, NumberStyles.Number, CultureInfo.InvariantCulture),
                PostgresSqlConstantKind.String => PostgresSqlUtf8.RequireText(Value!, nameof(Value)),
                PostgresSqlConstantKind.Uuid => Guid.ParseExact(Value!, "D"),
                PostgresSqlConstantKind.Date => DateOnly.ParseExact(Value!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                PostgresSqlConstantKind.Timestamp => RequireCivilTimestamp(Value!),
                PostgresSqlConstantKind.TimestampWithTimeZone => RequireInstantTimestamp(Value!),
                PostgresSqlConstantKind.Bytea => Convert.FromBase64String(Value!),
                _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unsupported PostgreSQL constant kind.")
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new ArgumentException(
                $"Portable PostgreSQL constant '{Kind}' has a malformed value.",
                nameof(Value),
                exception);
        }
    }

    static DateTime RequireCivilTimestamp(string value)
    {
        var parsed = DateTime.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (parsed.Kind != DateTimeKind.Unspecified || parsed.Ticks % 10 != 0)
        {
            throw new FormatException(
                "A PostgreSQL civil timestamp must have DateTimeKind.Unspecified and microsecond-aligned ticks.");
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
        if (parsed.Ticks % 10 != 0)
            throw new FormatException("A PostgreSQL instant must have microsecond-aligned ticks.");
        return parsed;
    }
}

/// <summary>One deterministically allocated parameter slot in a PostgreSQL command template.</summary>
public sealed record PostgresSqlParameterSlot
{
    internal PostgresSqlParameterSlot(
        int position,
        PostgresSqlParameterBindingKind kind,
        string? binding,
        object? constantValue)
        : this(
            position,
            kind,
            binding,
            kind == PostgresSqlParameterBindingKind.Constant
                ? PostgresSqlConstant.Capture(constantValue)
                : null)
    {
    }

    /// <summary>Creates and validates one persisted PostgreSQL parameter slot.</summary>
    /// <param name="position">One-based PostgreSQL parameter position.</param>
    /// <param name="kind">Captured-constant or runtime-binding kind.</param>
    /// <param name="binding">Runtime binding identity, or <see langword="null"/> for a constant.</param>
    /// <param name="constant">Tagged captured constant, or <see langword="null"/> for a runtime slot.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is not positive or <paramref name="kind"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Binding and constant metadata conflict with <paramref name="kind"/>.</exception>
    [JsonConstructor]
    public PostgresSqlParameterSlot(
        int position,
        PostgresSqlParameterBindingKind kind,
        string? binding,
        PostgresSqlConstant? constant)
    {
        if (position <= 0)
            throw new ArgumentOutOfRangeException(nameof(position), position, "A PostgreSQL parameter position must be positive.");
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported PostgreSQL parameter-binding kind.");
        if (kind == PostgresSqlParameterBindingKind.Runtime
            && string.IsNullOrWhiteSpace(binding))
            throw new ArgumentException("A runtime PostgreSQL parameter requires a binding identity.", nameof(binding));
        if (kind == PostgresSqlParameterBindingKind.Constant != (constant is not null)
            || kind == PostgresSqlParameterBindingKind.Constant && binding is not null)
        {
            throw new ArgumentException("PostgreSQL parameter constant metadata conflicts with its binding kind.", nameof(constant));
        }
        Position = position;
        Kind = kind;
        Binding = binding;
        Constant = constant;
    }

    /// <summary>One-based PostgreSQL positional-parameter position.</summary>
    public int Position { get; }

    /// <summary>Rendered positional placeholder such as <c>$1</c>.</summary>
    [JsonIgnore]
    public string Placeholder => $"${Position.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>How the slot obtains its value.</summary>
    public PostgresSqlParameterBindingKind Kind { get; }

    /// <summary>Runtime binding identity, or <see langword="null"/> for a captured constant.</summary>
    public string? Binding { get; }

    /// <summary>Tagged captured constant, or <see langword="null"/> for a runtime slot.</summary>
    public PostgresSqlConstant? Constant { get; }

    /// <summary>Provider-neutral captured CLR value; meaningful only for a constant slot.</summary>
    [JsonIgnore]
    public object? ConstantValue => Constant?.ToClrValue();
}

/// <summary>One concrete ordered value bound to a PostgreSQL positional parameter.</summary>
public sealed class PostgresSqlParameter
{
    readonly object? value;

    internal PostgresSqlParameter(int position, string? binding, object? value)
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
/// Immutable normalized PostgreSQL SQL and deterministic parameter-slot metadata. Templates produced by the closed
/// construction API are injection-safe. Rehydrated command text is executable code and must come from a trusted
/// artifact source.
/// </summary>
public sealed class PostgresSqlCommandTemplate
{
    /// <summary>Creates and validates a persisted immutable PostgreSQL command template.</summary>
    /// <param name="text">Trusted PostgreSQL text containing positional placeholders.</param>
    /// <param name="parameters">Parameter slots in exact one-based position order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Text is empty or parameter slots are null, repeated, or non-contiguous.</exception>
    [JsonConstructor]
    internal PostgresSqlCommandTemplate(string text, ImmutableArray<PostgresSqlParameterSlot> parameters)
    {
        Text = Guard.RequireNotNullOrWhiteSpace(text);
        var normalized = parameters.IsDefault ? [] : parameters;
        if (normalized.Any(static parameter => parameter is null))
            throw new ArgumentException("PostgreSQL command-template parameters cannot contain null entries.", nameof(parameters));
        if (normalized.Select(static parameter => parameter.Position)
            .SequenceEqual(Enumerable.Range(1, normalized.Length)) is false)
        {
            throw new ArgumentException(
                "PostgreSQL command-template parameters must occupy contiguous one-based positions.",
                nameof(parameters));
        }
        ValidatePlaceholders(Text, normalized.Length);
        Parameters = normalized;
    }

    /// <summary>PostgreSQL command text using positional placeholders.</summary>
    public string Text { get; }

    /// <summary>Parameter slots in one-based placeholder order.</summary>
    public ImmutableArray<PostgresSqlParameterSlot> Parameters { get; }

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
                throw new ArgumentException("PostgreSQL placeholders must use canonical positive positions.", nameof(text));
            var position = 0;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                try
                {
                    position = checked(position * 10 + text[index] - '0');
                }
                catch (OverflowException exception)
                {
                    throw new ArgumentException("A PostgreSQL placeholder position is too large.", nameof(text), exception);
                }
                index++;
            }
            index--;
            positions.Add(position);
        }

        if (inQuotedIdentifier)
            throw new ArgumentException("PostgreSQL command text contains an unterminated quoted identifier.", nameof(text));

        if (!positions.SetEquals(Enumerable.Range(1, slotCount)))
        {
            throw new ArgumentException(
                "PostgreSQL command text placeholders must correspond exactly to declared parameter slots.",
                nameof(text));
        }
    }

    /// <summary>Binds a command template that has no runtime parameters.</summary>
    /// <returns>A concrete immutable SQL statement containing captured constant values.</returns>
    /// <exception cref="ArgumentException">The template contains at least one runtime parameter.</exception>
    public PostgresSqlStatement Bind() => Bind(EmptyRuntimeParameters.Instance);

    /// <summary>Binds runtime values without rebuilding or reparsing the SQL tree.</summary>
    /// <param name="runtimeParameters">Values keyed by the bindings supplied to <see cref="PostgresSqlExpression.RuntimeParameter"/>.</param>
    /// <returns>A concrete immutable SQL statement with ordered positional values.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runtimeParameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A required binding is absent, an unknown binding is supplied, or a value is outside the exact provider-neutral
    /// PostgreSQL value domain.
    /// </exception>
    public PostgresSqlStatement Bind(IReadOnlyDictionary<string, object?> runtimeParameters)
    {
        ArgumentNullException.ThrowIfNull(runtimeParameters);
        var expected = Parameters
            .Where(static slot => slot.Kind == PostgresSqlParameterBindingKind.Runtime)
            .Select(static slot => slot.Binding!)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = runtimeParameters.Keys.Where(binding => !expected.Contains(binding)).ToArray();
        if (unknown.Length != 0)
        {
            throw new ArgumentException(
                $"The invocation contains unknown PostgreSQL parameter binding(s): {string.Join(", ", unknown.Order(StringComparer.Ordinal))}.",
                nameof(runtimeParameters));
        }

        var missing = expected.Where(binding => !runtimeParameters.ContainsKey(binding)).ToArray();
        if (missing.Length != 0)
        {
            throw new ArgumentException(
                $"The invocation is missing PostgreSQL parameter binding(s): {string.Join(", ", missing.Order(StringComparer.Ordinal))}.",
                nameof(runtimeParameters));
        }

        var values = ImmutableArray.CreateBuilder<PostgresSqlParameter>(Parameters.Length);
        foreach (var slot in Parameters)
        {
            var value = slot.Kind == PostgresSqlParameterBindingKind.Constant
                ? slot.ConstantValue
                : PostgresSqlConstant.NormalizeRuntimeValue(runtimeParameters[slot.Binding!]);
            values.Add(new(
                slot.Position,
                slot.Binding,
                value));
        }

        return new(Text, values.MoveToImmutable());
    }

    /// <summary>Binds runtime values from a strongly typed value dictionary.</summary>
    /// <typeparam name="TValue">Provider-neutral value type supplied by the caller.</typeparam>
    /// <param name="runtimeParameters">
    /// Values keyed by the bindings supplied to <see cref="PostgresSqlExpression.RuntimeParameter"/>.
    /// </param>
    /// <returns>A concrete immutable SQL statement with boxed values in positional order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runtimeParameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A required binding is absent, an unknown binding is supplied, or a value is outside the exact provider-neutral
    /// PostgreSQL value domain.
    /// </exception>
    public PostgresSqlStatement Bind<TValue>(IReadOnlyDictionary<string, TValue> runtimeParameters)
    {
        ArgumentNullException.ThrowIfNull(runtimeParameters);
        Dictionary<string, object?> boxed = new(runtimeParameters.Count, StringComparer.Ordinal);
        foreach (var pair in runtimeParameters)
        {
            boxed.Add(pair.Key, pair.Value);
        }

        return Bind(boxed);
    }

    sealed class EmptyRuntimeParameters : Dictionary<string, object?>
    {
        public static EmptyRuntimeParameters Instance { get; } = new();
    }
}

/// <summary>Immutable PostgreSQL SQL and concrete ordered positional values.</summary>
public sealed class PostgresSqlStatement
{
    internal PostgresSqlStatement(string text, ImmutableArray<PostgresSqlParameter> parameters)
    {
        Text = text;
        Parameters = parameters;
    }

    /// <summary>PostgreSQL SQL text using positional placeholders.</summary>
    public string Text { get; }

    /// <summary>Concrete parameter values in placeholder order.</summary>
    public ImmutableArray<PostgresSqlParameter> Parameters { get; }
}

/// <summary>Immutable PostgreSQL SELECT tree that may be rendered directly or used as a derived table.</summary>
public sealed class PostgresSqlSelectQuery
{
    readonly PostgresSqlFromItem? from;
    readonly ImmutableArray<PostgresSqlSelectItem> selections;
    readonly ImmutableArray<PostgresSqlJoinItem> joins;
    readonly ImmutableArray<PostgresSqlExpression> predicates;
    readonly ImmutableArray<PostgresSqlExpression> groupings;
    readonly ImmutableArray<PostgresSqlOrderItem> orderings;
    readonly bool distinct;
    readonly int? limit;
    readonly int? offset;

    internal PostgresSqlSelectQuery(
        PostgresSqlFromItem? from,
        ImmutableArray<PostgresSqlSelectItem> selections,
        ImmutableArray<PostgresSqlJoinItem> joins,
        ImmutableArray<PostgresSqlExpression> predicates,
        ImmutableArray<PostgresSqlExpression> groupings,
        ImmutableArray<PostgresSqlOrderItem> orderings,
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
    /// <returns>Normalized SQL and deterministic positional-parameter slots.</returns>
    public PostgresSqlCommandTemplate ToCommandTemplate()
    {
        PostgresSqlRenderContext context = new();
        StringBuilder builder = new();
        WriteTo(context, builder);
        return new(builder.ToString(), context.Parameters);
    }

    internal void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
    {
        builder.Append("SELECT ");
        if (distinct)
        {
            builder.Append("DISTINCT ");
        }

        for (var index = 0; index < selections.Length; index++)
        {
            if (index != 0)
            {
                builder.Append(", ");
            }

            selections[index].Expression.WriteTo(context, builder);
            builder.Append(" AS ");
            selections[index].Alias.WriteQuoted(builder);
        }

        if (from is not null)
        {
            builder.Append(" FROM ");
            from.WriteTo(context, builder);
        }

        foreach (var join in joins)
        {
            builder.Append(' ').Append(PostgresSqlOperators.Text(join.Kind)).Append(' ');
            join.Source.WriteTo(context, builder);
            if (join.Predicate is not null)
            {
                builder.Append(" ON ");
                join.Predicate.WriteTo(context, builder);
            }
        }

        if (!predicates.IsDefaultOrEmpty)
        {
            builder.Append(" WHERE ");
            WriteExpressionList(predicates, context, builder, " AND ");
        }

        if (!groupings.IsDefaultOrEmpty)
        {
            builder.Append(" GROUP BY ");
            WriteExpressionList(groupings, context, builder, ", ");
        }

        if (!orderings.IsDefaultOrEmpty)
        {
            builder.Append(" ORDER BY ");
            for (var index = 0; index < orderings.Length; index++)
            {
                if (index != 0)
                {
                    builder.Append(", ");
                }

                var ordering = orderings[index];
                ordering.Expression.WriteTo(context, builder);
                builder.Append(ordering.Direction == PostgresSqlSortDirection.Ascending ? " ASC" : " DESC");
                builder.Append(ordering.NullPlacement == PostgresSqlNullPlacement.First
                    ? " NULLS FIRST"
                    : " NULLS LAST");
            }
        }

        if (limit is { } pageLimit)
        {
            builder.Append(" LIMIT ").Append(pageLimit.ToString(CultureInfo.InvariantCulture));
        }

        if (offset is { } pageOffset)
        {
            builder.Append(" OFFSET ").Append(pageOffset.ToString(CultureInfo.InvariantCulture));
        }
    }

    static void WriteExpressionList(
        ImmutableArray<PostgresSqlExpression> expressions,
        PostgresSqlRenderContext context,
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

/// <summary>Mutable, single-threaded builder for an injection-safe PostgreSQL SELECT tree.</summary>
public sealed class PostgresSqlSelectBuilder
{
    readonly PostgresSqlFromItem? from;
    readonly List<PostgresSqlSelectItem> selections = [];
    readonly List<PostgresSqlJoinItem> joins = [];
    readonly List<PostgresSqlExpression> predicates = [];
    readonly List<PostgresSqlExpression> groupings = [];
    readonly List<PostgresSqlOrderItem> orderings = [];
    readonly HashSet<PostgresSqlIdentifier> aliases = [];
    readonly HashSet<PostgresSqlIdentifier> selectionAliases = [];
    bool distinct;
    int? limit;
    int? offset;

    /// <summary>
    /// Creates a SELECT builder without a <c>FROM</c> source, suitable for projecting supplied runtime inputs as one
    /// derived row.
    /// </summary>
    public PostgresSqlSelectBuilder()
    {
    }

    /// <summary>Creates a SELECT builder rooted at a physical table.</summary>
    /// <param name="table">Injection-safe physical table name.</param>
    /// <param name="alias">Alias used to qualify columns from the table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is not a supported PostgreSQL identifier.</exception>
    public PostgresSqlSelectBuilder(PostgresSqlQualifiedTable table, string alias)
    {
        ArgumentNullException.ThrowIfNull(table);
        var identifier = new PostgresSqlIdentifier(alias);
        from = new PostgresSqlTableFromItem(table, identifier);
        aliases.Add(identifier);
    }

    /// <summary>Creates a SELECT builder rooted at an immutable derived query.</summary>
    /// <param name="query">Inner query used as the derived table.</param>
    /// <param name="alias">Alias used to qualify columns projected by the derived table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is not a supported PostgreSQL identifier.</exception>
    public PostgresSqlSelectBuilder(PostgresSqlSelectQuery query, string alias)
    {
        ArgumentNullException.ThrowIfNull(query);
        var identifier = new PostgresSqlIdentifier(alias);
        from = new PostgresSqlDerivedFromItem(query, identifier);
        aliases.Add(identifier);
    }

    /// <summary>Adds one projected expression with a safe result alias.</summary>
    /// <param name="expression">Expression to project.</param>
    /// <param name="alias">Result-column alias.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> or <paramref name="alias"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is invalid or duplicates another projection alias.</exception>
    public PostgresSqlSelectBuilder Select(PostgresSqlExpression expression, string alias)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var identifier = new PostgresSqlIdentifier(alias);
        if (!selectionAliases.Add(identifier))
        {
            throw new ArgumentException($"PostgreSQL projection alias '{alias}' is already present.", nameof(alias));
        }

        selections.Add(new(expression, identifier));
        return this;
    }

    /// <summary>Requests whole-projection duplicate elimination.</summary>
    /// <returns>This builder.</returns>
    public PostgresSqlSelectBuilder Distinct()
    {
        distinct = true;
        return this;
    }

    /// <summary>Adds a predicate combined with prior predicates using conjunction.</summary>
    /// <param name="predicate">Boolean predicate.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public PostgresSqlSelectBuilder Where(PostgresSqlExpression predicate)
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
    public PostgresSqlSelectBuilder Join(
        PostgresSqlQualifiedTable table,
        string alias,
        PostgresSqlJoinKind kind,
        PostgresSqlExpression? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        RequireFromForJoin();
        ValidateJoin(kind, predicate);
        joins.Add(new(new PostgresSqlTableFromItem(table, RequireNewAlias(alias)), kind, predicate));
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
    public PostgresSqlSelectBuilder Join(
        PostgresSqlSelectQuery query,
        string alias,
        PostgresSqlJoinKind kind,
        PostgresSqlExpression? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        RequireFromForJoin();
        ValidateJoin(kind, predicate);
        joins.Add(new(new PostgresSqlDerivedFromItem(query, RequireNewAlias(alias)), kind, predicate));
        return this;
    }

    /// <summary>Adds one grouping expression.</summary>
    /// <param name="expression">Grouping expression.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    public PostgresSqlSelectBuilder GroupBy(PostgresSqlExpression expression)
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
    public PostgresSqlSelectBuilder OrderBy(
        PostgresSqlExpression expression,
        PostgresSqlSortDirection direction = PostgresSqlSortDirection.Ascending,
        PostgresSqlNullPlacement nullPlacement = PostgresSqlNullPlacement.Last)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported PostgreSQL sort direction.");
        }

        if (!Enum.IsDefined(nullPlacement))
        {
            throw new ArgumentOutOfRangeException(nameof(nullPlacement), nullPlacement, "Unsupported PostgreSQL null placement.");
        }

        orderings.Add(new(expression, direction, nullPlacement));
        return this;
    }

    /// <summary>Sets the maximum number of rows returned.</summary>
    /// <param name="value">Positive row limit.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not positive.</exception>
    public PostgresSqlSelectBuilder Limit(int value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A PostgreSQL row limit must be positive.");
        }

        limit = value;
        return this;
    }

    /// <summary>Sets the number of ordered rows skipped.</summary>
    /// <param name="value">Non-negative row offset.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public PostgresSqlSelectBuilder Offset(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A PostgreSQL row offset cannot be negative.");
        }

        offset = value;
        return this;
    }

    /// <summary>Sets offset and limit paging together.</summary>
    /// <param name="offset">Non-negative number of rows skipped.</param>
    /// <param name="limit">Positive maximum number of rows returned.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative or <paramref name="limit"/> is not positive.</exception>
    public PostgresSqlSelectBuilder OffsetLimit(int offset, int limit) =>
        Offset(offset).Limit(limit);

    /// <summary>Builds an immutable SELECT tree suitable for direct rendering or derived-table composition.</summary>
    /// <returns>An immutable snapshot of the builder.</returns>
    /// <exception cref="InvalidOperationException">No projection has been configured.</exception>
    public PostgresSqlSelectQuery BuildQuery()
    {
        if (selections.Count == 0)
        {
            throw new InvalidOperationException("A PostgreSQL SELECT query requires at least one projected expression.");
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

    /// <summary>Builds an immutable reusable PostgreSQL command template.</summary>
    /// <returns>Normalized SQL and deterministic positional-parameter slots.</returns>
    /// <exception cref="InvalidOperationException">No projection has been configured.</exception>
    public PostgresSqlCommandTemplate BuildTemplate() => BuildQuery().ToCommandTemplate();

    /// <summary>Builds a concrete statement when the query contains no runtime-bound parameters.</summary>
    /// <returns>Normalized SQL and ordered captured constant values.</returns>
    /// <exception cref="ArgumentException">The query contains a runtime-bound parameter.</exception>
    /// <exception cref="InvalidOperationException">No projection has been configured.</exception>
    public PostgresSqlStatement Build() => BuildTemplate().Bind();

    static void ValidateJoin(
        PostgresSqlJoinKind kind,
        PostgresSqlExpression? predicate)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported PostgreSQL join kind.");
        }

        if (kind == PostgresSqlJoinKind.Cross != (predicate is null))
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
                "A PostgreSQL SELECT without a FROM source cannot contain joins; wrap it as a derived source first.");
        }
    }

    PostgresSqlIdentifier RequireNewAlias(string alias)
    {
        var identifier = new PostgresSqlIdentifier(alias);
        if (!aliases.Add(identifier))
        {
            throw new ArgumentException($"PostgreSQL source alias '{alias}' is already present.", nameof(alias));
        }

        return identifier;
    }
}

internal abstract record PostgresSqlFromItem(PostgresSqlIdentifier Alias)
{
    public abstract void WriteTo(PostgresSqlRenderContext context, StringBuilder builder);
}

internal sealed record PostgresSqlTableFromItem(
    PostgresSqlQualifiedTable Table,
    PostgresSqlIdentifier SourceAlias) : PostgresSqlFromItem(SourceAlias)
{
    public override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
    {
        Table.WriteTo(builder);
        builder.Append(" AS ");
        Alias.WriteQuoted(builder);
    }
}

internal sealed record PostgresSqlDerivedFromItem(
    PostgresSqlSelectQuery Query,
    PostgresSqlIdentifier SourceAlias) : PostgresSqlFromItem(SourceAlias)
{
    public override void WriteTo(PostgresSqlRenderContext context, StringBuilder builder)
    {
        builder.Append('(');
        Query.WriteTo(context, builder);
        builder.Append(") AS ");
        Alias.WriteQuoted(builder);
    }
}

internal sealed record PostgresSqlSelectItem(
    PostgresSqlExpression Expression,
    PostgresSqlIdentifier Alias);

internal sealed record PostgresSqlJoinItem(
    PostgresSqlFromItem Source,
    PostgresSqlJoinKind Kind,
    PostgresSqlExpression? Predicate);

internal sealed record PostgresSqlOrderItem(
    PostgresSqlExpression Expression,
    PostgresSqlSortDirection Direction,
    PostgresSqlNullPlacement NullPlacement);

internal sealed class PostgresSqlRenderContext
{
    readonly ImmutableArray<PostgresSqlParameterSlot>.Builder parameters =
        ImmutableArray.CreateBuilder<PostgresSqlParameterSlot>();
    readonly Dictionary<string, int> runtimePositions = new(StringComparer.Ordinal);

    public ImmutableArray<PostgresSqlParameterSlot> Parameters => parameters.ToImmutable();

    public string AddConstant(PostgresSqlConstant value)
    {
        var position = parameters.Count + 1;
        parameters.Add(new(position, PostgresSqlParameterBindingKind.Constant, binding: null, value));
        return Placeholder(position);
    }

    public string AddRuntime(string binding)
    {
        if (runtimePositions.TryGetValue(binding, out var existing))
        {
            return Placeholder(existing);
        }

        var position = parameters.Count + 1;
        runtimePositions.Add(binding, position);
        parameters.Add(new(position, PostgresSqlParameterBindingKind.Runtime, binding, constantValue: null));
        return Placeholder(position);
    }

    static string Placeholder(int position) => $"${position.ToString(CultureInfo.InvariantCulture)}";
}

internal static class PostgresSqlOperators
{
    public static string Text(PostgresSqlBinaryOperator @operator) => @operator switch
    {
        PostgresSqlBinaryOperator.Equal => "=",
        PostgresSqlBinaryOperator.NotEqual => "<>",
        PostgresSqlBinaryOperator.GreaterThan => ">",
        PostgresSqlBinaryOperator.GreaterThanOrEqual => ">=",
        PostgresSqlBinaryOperator.LessThan => "<",
        PostgresSqlBinaryOperator.LessThanOrEqual => "<=",
        PostgresSqlBinaryOperator.And => "AND",
        PostgresSqlBinaryOperator.Or => "OR",
        PostgresSqlBinaryOperator.Add => "+",
        PostgresSqlBinaryOperator.Subtract => "-",
        PostgresSqlBinaryOperator.Multiply => "*",
        PostgresSqlBinaryOperator.Divide => "/",
        PostgresSqlBinaryOperator.Like => "LIKE",
        PostgresSqlBinaryOperator.IsNotDistinctFrom => "IS NOT DISTINCT FROM",
        PostgresSqlBinaryOperator.IsDistinctFrom => "IS DISTINCT FROM",
        _ => throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported PostgreSQL binary operator.")
    };

    public static string Text(PostgresSqlUnaryOperator @operator) => @operator switch
    {
        PostgresSqlUnaryOperator.Not => "NOT ",
        PostgresSqlUnaryOperator.Negate => "-",
        _ => throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported PostgreSQL unary operator.")
    };

    public static string Text(PostgresSqlJoinKind kind) => kind switch
    {
        PostgresSqlJoinKind.Inner => "INNER JOIN",
        PostgresSqlJoinKind.Left => "LEFT JOIN",
        PostgresSqlJoinKind.Right => "RIGHT JOIN",
        PostgresSqlJoinKind.Full => "FULL JOIN",
        PostgresSqlJoinKind.Cross => "CROSS JOIN",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported PostgreSQL join kind.")
    };
}

static class PostgresSqlFunctions
{
    public static string Name(PostgresSqlFunction function) => function switch
    {
        PostgresSqlFunction.Length => "LENGTH",
        PostgresSqlFunction.Right => "RIGHT",
        PostgresSqlFunction.Lower => "LOWER",
        PostgresSqlFunction.Upper => "UPPER",
        PostgresSqlFunction.Left => "LEFT",
        PostgresSqlFunction.StringPosition => "STRPOS",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported PostgreSQL scalar function.")
    };

    public static string Name(PostgresSqlAggregateFunction function) => function switch
    {
        PostgresSqlAggregateFunction.Count => "COUNT",
        PostgresSqlAggregateFunction.Sum => "SUM",
        PostgresSqlAggregateFunction.Minimum => "MIN",
        PostgresSqlAggregateFunction.Maximum => "MAX",
        PostgresSqlAggregateFunction.Average => "AVG",
        PostgresSqlAggregateFunction.BooleanOr => "BOOL_OR",
        PostgresSqlAggregateFunction.BooleanAnd => "BOOL_AND",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported PostgreSQL aggregate function.")
    };

    public static void ValidateArity(PostgresSqlFunction function, int count, string parameterName)
    {
        var valid = function switch
        {
            PostgresSqlFunction.Length or PostgresSqlFunction.Lower or PostgresSqlFunction.Upper => count == 1,
            PostgresSqlFunction.Right or PostgresSqlFunction.Left or PostgresSqlFunction.StringPosition => count == 2,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                $"PostgreSQL function '{function}' does not accept {count.ToString(CultureInfo.InvariantCulture)} argument(s).",
                parameterName);
        }
    }
}
