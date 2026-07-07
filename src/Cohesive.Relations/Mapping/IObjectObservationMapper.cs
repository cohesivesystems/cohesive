using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

/// <summary>
/// Maps CLR objects into observed shapes.
/// </summary>
public interface IObjectObservationMapper<in T>
{
    /// <summary>
    /// Maps one object instance to an observed shape. Explicit metadata values override convention-based extraction.
    /// </summary>
    Observation Map(T source, ObjectObservationMetadata? metadata = null);
}
