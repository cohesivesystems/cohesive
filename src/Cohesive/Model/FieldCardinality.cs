using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Field multiplicity in semantic state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldCardinality
{
    /// <summary>A single field cardinality type.</summary>
    Single = 0,
    
    /// <summary>A many field cardinality type.</summary>
    Many = 1
}
