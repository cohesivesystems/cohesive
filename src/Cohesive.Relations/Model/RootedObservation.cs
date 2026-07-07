namespace Cohesive.Relations.Model;

/// <summary>
/// Associates a root identity with an observation for rooted relation execution.
/// </summary>
public sealed record RootedObservation
{
    /// <summary>
    /// Creates a rooted observation.
    /// </summary>
    public RootedObservation(Observation observation, string rootId)
    {
        Observation = Guard.RequireNotNull(observation);
        RootId = Guard.RequireNotNullOrWhiteSpace(rootId);
    }

    /// <summary>
    /// Wrapped observation.
    /// </summary>
    public Observation Observation { get; init; }

    /// <summary>
    /// Root identity used to scope rooted relation execution.
    /// </summary>
    public string RootId { get; init; }

    /// <summary>
    /// Observation shape.
    /// </summary>
    public ShapeId ShapeId => Observation.ShapeId;

    /// <summary>
    /// Observation identity.
    /// </summary>
    public string Id => Observation.Id;

    /// <summary>
    /// Observation version.
    /// </summary>
    public long Version => Observation.Version;
}
