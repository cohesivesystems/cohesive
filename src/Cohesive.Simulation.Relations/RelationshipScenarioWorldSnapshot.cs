using Cohesive.Simulation.Scenarios;

namespace Cohesive.Simulation.Relations;

/// <summary>Materializes scenario actors governed by retained relationship-world artifacts.</summary>
public static class RelationshipScenarioWorldSnapshot
{
    /// <summary>Materializes every actor through the relationship-world reference interpreter.</summary>
    /// <param name="scenario">Exact scenario whose relationship-aware initial world will be interpreted.</param>
    /// <returns>A complete relationship-aware initial-world actor snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scenario"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// The initial artifact selects another world schema, interpreter, or entropy algorithm.
    /// </exception>
    /// <exception cref="ArgumentException">The artifact contains inconsistent retained world projections.</exception>
    /// <exception cref="System.Text.Json.JsonException">The retained relationship-world document is invalid.</exception>
    /// <exception cref="RelationshipWorldCompilationException">
    /// The retained relationship world fails semantic compilation.
    /// </exception>
    /// <exception cref="Cohesive.Simulation.Worlds.WorldGenerationException">
    /// A selected exemplar cannot be generated with valid identity.
    /// </exception>
    public static ScenarioWorldSnapshot Materialize(ScenarioDefinitionDocument scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var artifact = scenario.Definition.InitialWorld;
        var world = RelationshipWorldArtifact.GetWorld(artifact).Compile();
        return ScenarioWorldSnapshot.Materialize(
            scenario,
            (actor, exemplar) =>
            {
                var generated = world.GenerateExemplar(exemplar.Id, artifact.RootSeed);
                return new(
                    actor,
                    exemplar,
                    generated.EntityId,
                    generated.Observation,
                    generated.Replay.ToToken());
            });
    }
}
