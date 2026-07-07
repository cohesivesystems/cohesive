using Cohesive.Relations.Model;

namespace Cohesive.Relations.Execution;

sealed record RelationScopedObservation
{
    public RelationScopedObservation(
        Observation observation,
        string rootId,
        string? logicalEntityId = null
        )
    {
        Observation = Guard.RequireNotNull(observation);
        RootId = Guard.RequireNotNullOrWhiteSpace(rootId);
        LogicalEntityId = string.IsNullOrWhiteSpace(logicalEntityId) ? observation.Id : logicalEntityId;
    }

    public Observation Observation { get; init; }

    public string RootId { get; init; }

    public string LogicalEntityId { get; init; }

    public ShapeId ShapeId => Observation.ShapeId;

    public string Id => Observation.Id;

    public long Version => Observation.Version;
}
