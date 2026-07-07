using Cohesive.Relations.Model;

namespace Cohesive.Relations.Execution;

/// <summary>
/// Executes projection definitions over source observations.
/// </summary>
public interface IRelationExecutor
{
    /// <summary>
    /// Executes <paramref name="relation"/> over input observations and returns emitted observations.
    /// </summary>
    ValueTask<IReadOnlyList<Observation>> ExecuteAsync(RelationDefinition relation, IReadOnlyList<Observation> inputs, CancellationToken ct);

    /// <summary>
    /// Executes <paramref name="relation"/> over root-scoped input observations and returns emitted observations.
    /// </summary>
    ValueTask<IReadOnlyList<Observation>> ExecuteAsync(RelationDefinition relation, IReadOnlyList<RootedObservation> inputs, CancellationToken ct);
}
