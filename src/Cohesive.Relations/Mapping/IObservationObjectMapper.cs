using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

/// <summary>
/// Maps observed shapes into typed CLR objects.
/// </summary>
public interface IObservationObjectMapper<out T>
{
    /// <summary>
    /// Maps one observed shape to a typed object.
    /// </summary>
    T Map(Observation observation);
}
