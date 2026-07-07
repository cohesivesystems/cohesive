using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Stable identifier for an observation/entity instance.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct EntityId
{
    /// <summary>
    /// Creates an entity identifier.
    /// </summary>
    [JsonConstructor]
    public EntityId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw entity identifier.
    /// </summary>
    public string Value { get; }

    public override string ToString() => Value;
}
