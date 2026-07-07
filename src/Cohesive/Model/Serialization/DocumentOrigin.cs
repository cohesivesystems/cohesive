using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Coarse origin category for a serialized semantic document.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentOrigin
{
    /// <summary>
    /// Origin is unknown or has not been assigned.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Authored or changed directly by a user.
    /// </summary>
    User = 1,

    /// <summary>
    /// Produced by trusted system code.
    /// </summary>
    System = 2,

    /// <summary>
    /// Imported from an external document.
    /// </summary>
    Imported = 3,

    /// <summary>
    /// Generated from another semantic source.
    /// </summary>
    Generated = 4,

    /// <summary>
    /// Compiled from a higher-level semantic spec.
    /// </summary>
    Compiled = 5,

    /// <summary>
    /// Extracted from an unstructured or semi-structured source.
    /// </summary>
    Extracted = 6
}
