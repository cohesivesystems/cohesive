using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation;

public static partial class Simulation
{
    /// <summary>Defines a static initial world from canonical generation definitions.</summary>
    /// <param name="id">Stable logical world identity.</param>
    /// <param name="revision">Exact authored world revision.</param>
    /// <param name="configure">Authoring callback that declares named bounded populations and exemplars.</param>
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

/// <summary>Fluent producer of canonical static world populations and named exemplars.</summary>
/// <remarks>The builder is mutable and intended for one single-threaded authoring callback.</remarks>
public sealed class WorldBuilder
{
    readonly List<WorldPopulationDefinition> populations = [];
    readonly List<WorldExemplarDefinition> exemplars = [];

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
        => Population(id, count, WorldEntityIdentityPolicy.PopulationSequence, generation);

    /// <summary>Adds a population backed by direct canonical generation IR and an explicit identity policy.</summary>
    /// <param name="id">Stable population identity within the world.</param>
    /// <param name="count">Number of initial population members.</param>
    /// <param name="entityIdentity">Portable policy assigning identity to generated members.</param>
    /// <param name="generation">Canonical generation semantics for one population member.</param>
    /// <returns>This builder for continued authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entityIdentity"/> or <paramref name="generation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public WorldBuilder Population(
        string id,
        int count,
        WorldEntityIdentityPolicy entityIdentity,
        GenerationDefinition generation)
    {
        populations.Add(new(id, count, entityIdentity, generation));
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

    /// <summary>Adds a typed POCO population with an explicit portable entity identity policy.</summary>
    /// <typeparam name="T">CLR target type of the local authoring projection.</typeparam>
    /// <param name="id">Stable population identity within the world.</param>
    /// <param name="count">Number of initial population members.</param>
    /// <param name="entityIdentity">Portable policy assigning identity to generated members.</param>
    /// <param name="generation">Typed producer whose canonical generation IR enters the world.</param>
    /// <returns>This builder for continued authoring.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entityIdentity"/> or <paramref name="generation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public WorldBuilder Population<T>(
        string id,
        int count,
        WorldEntityIdentityPolicy entityIdentity,
        PocoGenerationDefinition<T> generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return Population(id, count, entityIdentity, generation.Definition);
    }

    /// <summary>Names one exact generated member of a declared population.</summary>
    /// <param name="id">Stable world-wide exemplar identity.</param>
    /// <param name="populationId">Stable identity of the containing population.</param>
    /// <param name="sequenceIndex">Zero-based sequence index within the population.</param>
    /// <returns>This builder for continued authoring.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> or <paramref name="populationId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <paramref name="populationId"/> is empty.</exception>
    /// <remarks>
    /// The population may be declared before or after this call. The world compiler validates the reference and its
    /// sequence bound after the authoring callback has completed.
    /// </remarks>
    public WorldBuilder Exemplar(string id, string populationId, int sequenceIndex)
    {
        exemplars.Add(new(id, populationId, sequenceIndex));
        return this;
    }

    internal WorldDefinition Build(string id, string revision) =>
        new(id, revision, [.. populations], [.. exemplars]);
}
