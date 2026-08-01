using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

/// <summary>How an interpretation target realizes one requested semantic capability.</summary>
/// <remarks>
/// This classification is shared across Cohesive compilers and adapters. Owning evidence and decision records
/// remain responsible for constraining which classifications are valid at their lifecycle boundary.
/// </remarks>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum CapabilityRealizationKind
{
    /// <summary>The target preserves the capability directly.</summary>
    Native = 0,

    /// <summary>Declared target facilities compose exact support for the capability.</summary>
    Composed = 1,

    /// <summary>Exact support holds only inside attributable validated operating boundaries.</summary>
    Constrained = 2,

    /// <summary>An explicit local override supplies exact attributable support.</summary>
    Override = 3,

    /// <summary>No supplied or permitted strategy preserves the requested capability.</summary>
    Unavailable = 4,

    /// <summary>No realization classification is known; invalid where an exact decision or evidence is required.</summary>
    Unknown = 5
}
