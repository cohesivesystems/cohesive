using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Model;

/// <summary>
/// Stable identifier for a semantic relationship.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationshipId
{
    /// <summary>
    /// Creates a relationship identifier.
    /// </summary>
    /// <param name="value">Raw relationship identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of white-space characters.</exception>
    [JsonConstructor]
    public RelationshipId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw relationship identifier.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
