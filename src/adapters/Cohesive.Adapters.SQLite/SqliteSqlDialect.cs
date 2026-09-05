using Cohesive.Adapters.Sql;

namespace Cohesive.Adapters.SQLite;

/// <summary>SQLite construction policy for the adapter's required modern SQLite engine profile.</summary>
public sealed class SqliteSqlDialect : SqlDialect
{
    /// <summary>Shared immutable SQLite policy.</summary>
    public static SqliteSqlDialect Instance { get; } = new();
    private SqliteSqlDialect() { }
    /// <inheritdoc />
    public override string Name => "sqlite/v1";
    /// <inheritdoc />
    public override void ValidateIdentifier(SqlIdentifier identifier) { }
    /// <inheritdoc />
    public override void ValidateParameter(object? value)
    {
        if (value is not (null or string or long or int or byte[]))
            throw new ArgumentException("SQLite SQL parameters must be encoded through SqliteScalarCodec into INTEGER, TEXT, or BLOB values.", nameof(value));
    }
    /// <inheritdoc />
    public override string FunctionName(SqlFunction function) => function switch
    {
        SqlFunction.Length => "LENGTH",
        SqlFunction.Lower => "LOWER",
        SqlFunction.Upper => "UPPER",
        _ => throw Unsupported(function.ToString())
    };
    /// <inheritdoc />
    public override string FunctionName(SqlAggregateFunction function) => function switch
    {
        SqlAggregateFunction.Count => "COUNT",
        SqlAggregateFunction.Sum => "SUM",
        SqlAggregateFunction.Minimum => "MIN",
        SqlAggregateFunction.Maximum => "MAX",
        SqlAggregateFunction.Average => "AVG",
        _ => throw Unsupported(function.ToString())
    };
    /// <inheritdoc />
    public override void Require(SqlFeature feature)
    {
        if (feature is not (SqlFeature.Returning or SqlFeature.OnConflict or SqlFeature.AggregateFilter))
            throw Unsupported(feature.ToString());
    }
    SqlConstructionException Unsupported(string construct) => new(Name, construct,
        "Select an explicit SQLite-supported lowering or a target that supports this construct.");
}
