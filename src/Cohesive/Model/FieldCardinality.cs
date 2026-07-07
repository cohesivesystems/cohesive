using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Field multiplicity in semantic state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldCardinality
{
    Single = 0,
    Many = 1
}
