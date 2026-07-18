using Cohesive.Model;

namespace Cohesive.Presentation;

/// <summary>
/// Declares an aggregate query over a source data source result.
/// </summary>
/// <param name="SourceDataSourceId">Data source that supplies the records being aggregated.</param>
/// <param name="Materialization">How much of the source must be materialized before aggregation.</param>
/// <param name="Measures">Measures projected into the aggregate result.</param>
public sealed record DataSourceAggregateQuery(
    string SourceDataSourceId,
    DataSourceAggregateMaterialization Materialization,
    DataSourceAggregateMeasure[] Measures
);

/// <summary>
/// Declares how an aggregate query consumes its source.
/// </summary>
/// <param name="Kind">Materialization strategy.</param>
/// <param name="PageSize">Optional page size used when materializing a paged source.</param>
public sealed record DataSourceAggregateMaterialization(
    DataSourceAggregateMaterializationKind Kind,
    int? PageSize = null
);

/// <summary>
/// Classifies aggregate source materialization strategies.
/// </summary>
public enum DataSourceAggregateMaterializationKind
{
    /// <summary>
    /// Aggregate the source data already available to the caller.
    /// </summary>
    CurrentPage = 0,

    /// <summary>
    /// Materialize every source page before aggregating.
    /// </summary>
    AllPages = 1
}

/// <summary>
/// Declares one aggregate measure in an aggregate query result.
/// </summary>
/// <param name="Id">Stable measure identifier.</param>
/// <param name="TargetPath">Path written in the aggregate result object.</param>
/// <param name="Operator">Aggregate operator.</param>
/// <param name="SourceField">Optional source field used by value aggregates such as sum, average, or min.</param>
/// <param name="Predicate">Optional record predicate applied before this measure is computed.</param>
public sealed record DataSourceAggregateMeasure(
    string Id,
    string TargetPath,
    AggregateOperator Operator,
    FieldPath? SourceField = null,
    DataSourceAggregatePredicate? Predicate = null
);

/// <summary>
/// Simple boolean predicate language used by aggregate measures.
/// </summary>
/// <param name="Kind">Predicate kind.</param>
/// <param name="Field">Field path used by field predicates.</param>
/// <param name="Value">String literal used by equality predicates.</param>
/// <param name="Terms">Child predicates used by boolean combinators.</param>
public sealed record DataSourceAggregatePredicate(
    DataSourceAggregatePredicateKind Kind,
    FieldPath? Field = null,
    string? Value = null,
    DataSourceAggregatePredicate[]? Terms = null
)
{
    /// <summary>
    /// Matches when the field value is equal to the supplied string.
    /// </summary>
    public static DataSourceAggregatePredicate Equal(string field, string value) =>
        new(DataSourceAggregatePredicateKind.FieldEquals, FieldPath.Parse(field), value);

    /// <summary>
    /// Matches when the field value is not equal to the supplied string.
    /// </summary>
    public static DataSourceAggregatePredicate NotEqual(string field, string value) =>
        new(DataSourceAggregatePredicateKind.FieldNotEquals, FieldPath.Parse(field), value);

    /// <summary>
    /// Matches when the field is present and non-empty.
    /// </summary>
    public static DataSourceAggregatePredicate HasValue(string field) =>
        new(DataSourceAggregatePredicateKind.FieldHasValue, FieldPath.Parse(field));

    /// <summary>
    /// Matches when every child predicate matches.
    /// </summary>
    public static DataSourceAggregatePredicate And(params DataSourceAggregatePredicate[] terms) =>
        new(DataSourceAggregatePredicateKind.And, Terms: terms);

    /// <summary>
    /// Matches when at least one child predicate matches.
    /// </summary>
    public static DataSourceAggregatePredicate Or(params DataSourceAggregatePredicate[] terms) =>
        new(DataSourceAggregatePredicateKind.Or, Terms: terms);

    /// <summary>
    /// Matches when the child predicate does not match.
    /// </summary>
    public static DataSourceAggregatePredicate Not(DataSourceAggregatePredicate term) =>
        new(DataSourceAggregatePredicateKind.Not, Terms: [term]);
}

/// <summary>
/// Classifies aggregate measure predicates.
/// </summary>
public enum DataSourceAggregatePredicateKind
{
    /// <summary>Represents the field equals option.</summary>
    FieldEquals = 0,
    /// <summary>Represents the field not equals option.</summary>
    FieldNotEquals = 1,
    /// <summary>Represents the field has value option.</summary>
    FieldHasValue = 2,
    /// <summary>Represents the and option.</summary>
    And = 3,
    /// <summary>Represents the or option.</summary>
    Or = 4,
    /// <summary>Represents the not option.</summary>
    Not = 5
}
