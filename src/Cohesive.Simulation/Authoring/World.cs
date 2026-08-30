using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation;

public static partial class Simulation
{
    /// <summary>Defines a static initial world from canonical generation definitions.</summary>
    /// <param name="id">Stable logical world identity.</param>
    /// <param name="revision">Exact authored world revision.</param>
    /// <param name="configure">Authoring callback that declares named bounded populations.</param>
    /// <returns>A canonical provider-neutral world definition.</returns>
    /// <remarks>
    /// The callback executes immediately and does not survive into canonical IR. Typed POCO definitions contribute
    /// only their canonical generation semantics; CLR materializers remain local interpretations.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="revision"/> is empty or white-space.
    /// </exception>
    public static WorldDefinition DefineWorld(
        string id,
        string revision,
        Action<WorldBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        WorldBuilder builder = new();
        configure(builder);
        return builder.Build(id, revision);
    }
}

/// <summary>Fluent producer of canonical static world populations.</summary>
/// <remarks>The builder is mutable and intended for one single-threaded authoring callback.</remarks>
public sealed class WorldBuilder
{
    readonly List<WorldPopulationDefinition> populations = [];

    /// <summary>Creates an empty world builder.</summary>
    public WorldBuilder()
    {
    }

    /// <summary>Adds a population backed by direct canonical generation IR.</summary>
    /// <param name="id">Stable population identity within the world.</param>
    /// <param name="count">Number of initial population members.</param>
    /// <param name="generation">Canonical generation semantics for one population member.</param>
    /// <returns>This builder for continued authoring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public WorldBuilder Population(
        string id,
        int count,
        GenerationDefinition generation)
    {
        populations.Add(new(id, count, generation));
        return this;
    }

    /// <summary>Adds a population backed by typed POCO generation authoring.</summary>
    /// <typeparam name="T">CLR target type of the local authoring projection.</typeparam>
    /// <param name="id">Stable population identity within the world.</param>
    /// <param name="count">Number of initial population members.</param>
    /// <param name="generation">Typed producer whose canonical generation IR enters the world.</param>
    /// <returns>This builder for continued authoring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public WorldBuilder Population<T>(
        string id,
        int count,
        PocoGenerationDefinition<T> generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return Population(id, count, generation.Definition);
    }

    internal WorldDefinition Build(string id, string revision) =>
        new(id, revision, [.. populations]);
}
