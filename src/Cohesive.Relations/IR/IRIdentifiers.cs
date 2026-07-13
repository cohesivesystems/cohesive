using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.IR;

/// <summary>
/// Stable identifier for a persisted query definition.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct QueryId
{
    /// <summary>Creates a query identifier.</summary>
    /// <param name="value">Raw query identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of white-space characters.</exception>
    [JsonConstructor]
    public QueryId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw query identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable human-readable query name.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct QueryName
{
    /// <summary>Creates a query name.</summary>
    /// <param name="value">Raw query name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of white-space characters.</exception>
    [JsonConstructor]
    public QueryName(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw query name.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable identifier for a logical query node.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct QueryNodeId
{
    /// <summary>Creates a query node identifier.</summary>
    /// <param name="value">Raw query node identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of white-space characters.</exception>
    [JsonConstructor]
    public QueryNodeId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw node identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable identifier for a named query result.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct QueryResultId
{
    /// <summary>Creates a query result identifier.</summary>
    /// <param name="value">Raw query result identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of white-space characters.</exception>
    [JsonConstructor]
    public QueryResultId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw result identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable identifier for a query parameter declaration.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct QueryParameterId
{
    /// <summary>Creates a query parameter identifier.</summary>
    /// <param name="value">Raw query parameter identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of white-space characters.</exception>
    [JsonConstructor]
    public QueryParameterId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw parameter identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable identifier for a projection or aggregation assignment.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct QueryAssignmentId
{
    /// <summary>Creates an assignment identifier.</summary>
    /// <param name="value">Raw assignment identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of white-space characters.</exception>
    [JsonConstructor]
    public QueryAssignmentId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw assignment identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
