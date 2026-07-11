using Cohesive.Relations.Model;

namespace Cohesive.Relations.Hydration;

/// <summary>
/// An adapter for reading observations from storage.
/// </summary>
public interface IObservationHydrationStore
{
    /// <summary>
    /// Queries observations by schema, identity filters, and selected fields.
    /// </summary>
    Task<IReadOnlyList<Observation>> QueryAsync(ObservationHydrationOptions options, CancellationToken ct = default);
}