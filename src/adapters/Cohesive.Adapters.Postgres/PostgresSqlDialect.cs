using Cohesive.Adapters.Sql;

namespace Cohesive.Adapters.Postgres;

/// <summary>PostgreSQL identifier, parameter, and grammar policy for shared SQL construction.</summary>
public sealed class PostgresSqlDialect : SqlDialect
{
    /// <summary>Shared immutable PostgreSQL policy.</summary>
    public static PostgresSqlDialect Instance { get; } = new();
    /// <summary>Standard PostgreSQL identifier limit in UTF-8 bytes.</summary>
    public const int StandardMaxUtf8ByteLength = 63;
    private PostgresSqlDialect() { }
    /// <summary>Creates an identifier validated against PostgreSQL's exact name domain.</summary>
    /// <param name="value">Unquoted physical identifier.</param>
    /// <returns>A validated identifier usable by the shared construction layer.</returns>
    /// <exception cref="ArgumentException">The identifier is invalid or exceeds 63 UTF-8 bytes.</exception>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public static SqlIdentifier Identifier(string value)
    {
        var identifier = new SqlIdentifier(value);
        Instance.ValidateIdentifier(identifier);
        return identifier;
    }
    /// <inheritdoc />
    public override string Name => "postgres/v1";
    /// <inheritdoc />
    public override void ValidateIdentifier(SqlIdentifier identifier)
    {
        if (SqlUtf8.GetByteCount(identifier.Value, nameof(identifier)) > StandardMaxUtf8ByteLength)
            throw new ArgumentException("A PostgreSQL identifier cannot exceed 63 UTF-8 bytes.", nameof(identifier));
    }
    /// <inheritdoc />
    public override void ValidateParameter(object? value)
    {
        if (value is DateTime timestamp && (timestamp.Kind != DateTimeKind.Unspecified || timestamp.Ticks % 10 != 0))
            throw new ArgumentException("A PostgreSQL civil timestamp must be unspecified-kind and microsecond-aligned.", nameof(value));
        if (value is DateTimeOffset instant && (instant.Offset != TimeSpan.Zero || instant.Ticks % 10 != 0))
            throw new ArgumentException("A PostgreSQL instant must be UTC and microsecond-aligned.", nameof(value));
    }
    /// <inheritdoc />
    public override void Require(SqlFeature feature)
    {
        if (!Enum.IsDefined(feature)) throw new SqlConstructionException(Name, feature.ToString(), "Use a supported PostgreSQL construct.");
    }
    /// <inheritdoc />
    public override string FunctionName(SqlFunction function) => function switch
    {
        SqlFunction.ClockTimestamp => "CLOCK_TIMESTAMP",
        SqlFunction.Length => "LENGTH",
        SqlFunction.Right => "RIGHT",
        SqlFunction.Lower => "LOWER",
        SqlFunction.Upper => "UPPER",
        SqlFunction.Left => "LEFT",
        SqlFunction.StringPosition => "STRPOS",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported SQL scalar function.")
    };

    /// <inheritdoc />
    public override string FunctionName(SqlAggregateFunction function) => function switch
    {
        SqlAggregateFunction.Count => "COUNT",
        SqlAggregateFunction.Sum => "SUM",
        SqlAggregateFunction.Minimum => "MIN",
        SqlAggregateFunction.Maximum => "MAX",
        SqlAggregateFunction.Average => "AVG",
        SqlAggregateFunction.BooleanOr => "BOOL_OR",
        SqlAggregateFunction.BooleanAnd => "BOOL_AND",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported SQL aggregate function.")
    };

}
