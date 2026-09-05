using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using Cohesive.Simulation;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Relations;

/// <summary>Portable deterministic selection policy for one inter-population relationship.</summary>
/// <remarks>
/// The current profile selects uniformly from the complete named target population. Presence probability controls
/// whether an optional or nullable source reference receives a target; field presence, nullability, cardinality,
/// endpoints, and target keys remain owned by the canonical relationship and shape authorities.
/// </remarks>
public sealed record WorldRelationshipSelectionPolicy
{
    /// <summary>Creates a uniform population selection policy.</summary>
    /// <param name="presenceProbability">
    /// Finite probability in the inclusive range zero through one that a target is selected.
    /// </param>
    /// <remarks>Validity is retained for structured relationship-world compiler diagnostics.</remarks>
    [JsonConstructor]
    public WorldRelationshipSelectionPolicy(double presenceProbability = 1d) =>
        PresenceProbability = presenceProbability;

    /// <summary>Gets the probability that a target population member is selected.</summary>
    public double PresenceProbability { get; }

    /// <summary>Gets the conventional always-present uniform selection policy.</summary>
    public static WorldRelationshipSelectionPolicy Uniform { get; } = new();
}

/// <summary>Binds one canonical relationship to source and target populations in a simulation world.</summary>
/// <remarks>
/// This binding declares only population placement and selection. The linked relationship catalog remains the sole
/// authority for source fields, endpoint shapes, target keys, cardinality, and uniqueness.
/// </remarks>
public sealed record WorldPopulationRelationshipBinding
{
    /// <summary>Creates an inter-population relationship binding.</summary>
    /// <param name="sourcePopulationId">Stable identity of the reference-bearing population.</param>
    /// <param name="relationshipId">Stable relationship identity resolved in the exact linked catalog.</param>
    /// <param name="targetPopulationId">Stable identity of the selected target population.</param>
    /// <param name="selection">Portable deterministic target selection.</param>
    /// <exception cref="ArgumentNullException"><paramref name="selection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A population identity is empty or white-space.</exception>
    [JsonConstructor]
    public WorldPopulationRelationshipBinding(
        string sourcePopulationId,
        RelationshipId relationshipId,
        string targetPopulationId,
        WorldRelationshipSelectionPolicy selection)
    {
        SourcePopulationId = Guard.RequireNotNullOrWhiteSpace(sourcePopulationId);
        RelationshipId = relationshipId;
        TargetPopulationId = Guard.RequireNotNullOrWhiteSpace(targetPopulationId);
        Selection = Guard.RequireNotNull(selection);
    }

    /// <summary>Gets the stable source-population identity.</summary>
    public string SourcePopulationId { get; }

    /// <summary>Gets the canonical relationship identity.</summary>
    public RelationshipId RelationshipId { get; }

    /// <summary>Gets the stable target-population identity.</summary>
    public string TargetPopulationId { get; }

    /// <summary>Gets the deterministic target-selection policy.</summary>
    public WorldRelationshipSelectionPolicy Selection { get; }
}

/// <summary>Portable semantic authority for one relationship-linked static simulation world.</summary>
public sealed record RelationshipWorldDefinition
{
    /// <summary>Creates a relationship-linked world definition.</summary>
    /// <param name="world">Canonical static simulation-world authority.</param>
    /// <param name="relationshipCatalog">Exact fingerprint-pinned canonical relationship authority.</param>
    /// <param name="relationshipBindings">World-local population bindings; declaration order is non-semantic.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="world"/> or <paramref name="relationshipCatalog"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public RelationshipWorldDefinition(
        WorldDefinition world,
        RelationshipCatalogDocument relationshipCatalog,
        ImmutableArray<WorldPopulationRelationshipBinding> relationshipBindings)
    {
        World = Guard.RequireNotNull(world);
        RelationshipCatalog = Guard.RequireNotNull(relationshipCatalog);
        RelationshipBindings = relationshipBindings.IsDefault ? [] : relationshipBindings;
    }

    /// <summary>Gets the canonical static simulation-world authority.</summary>
    public WorldDefinition World { get; }

    /// <summary>Gets the exact linked canonical relationship authority.</summary>
    public RelationshipCatalogDocument RelationshipCatalog { get; }

    /// <summary>Gets world-local relationship population bindings.</summary>
    public ImmutableArray<WorldPopulationRelationshipBinding> RelationshipBindings { get; }

    /// <summary>Attempts relationship-aware compilation and retains structured diagnostics.</summary>
    /// <returns>A result containing a plan only when all world and relationship invariants are satisfied.</returns>
    public RelationshipWorldCompilationResult CompileResult() => RelationshipWorldCompiler.Compile(this);

    /// <summary>Compiles this definition into a deterministic relationship-aware world plan.</summary>
    /// <returns>An immutable reusable plan.</returns>
    /// <exception cref="RelationshipWorldCompilationException">Compilation produces an error diagnostic.</exception>
    public CompiledRelationshipWorldPlan Compile()
    {
        var result = CompileResult();
        return result.Plan ?? throw new RelationshipWorldCompilationException(result.Validation);
    }
}

/// <summary>Human-reviewable fluent producer for canonical relationship-world IR.</summary>
/// <remarks>The builder is mutable and intended for one single-threaded authoring callback.</remarks>
public sealed class RelationshipWorldBuilder
{
    readonly WorldBuilder world = new();
    readonly List<WorldPopulationRelationshipBinding> relationships = [];

    /// <summary>Creates an empty relationship-world builder.</summary>
    public RelationshipWorldBuilder()
    {
    }

    /// <summary>Adds a population with conventional sequence-derived entity identity.</summary>
    /// <param name="id">Stable population identity.</param>
    /// <param name="count">Bounded population size.</param>
    /// <param name="generation">Local field-generation semantics.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public RelationshipWorldBuilder Population(string id, int count, GenerationDefinition generation) =>
        Population(id, count, WorldEntityIdentityPolicy.PopulationSequence, generation);

    /// <summary>Adds a population with explicit portable entity identity.</summary>
    /// <param name="id">Stable population identity.</param>
    /// <param name="count">Bounded population size.</param>
    /// <param name="entityIdentity">Population entity-identity policy.</param>
    /// <param name="generation">Local field-generation semantics.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entityIdentity"/> or <paramref name="generation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public RelationshipWorldBuilder Population(
        string id,
        int count,
        WorldEntityIdentityPolicy entityIdentity,
        GenerationDefinition generation)
    {
        world.Population(id, count, entityIdentity, generation);
        return this;
    }

    /// <summary>Adds a population from typed POCO generation authoring.</summary>
    /// <typeparam name="T">CLR authoring type.</typeparam>
    /// <param name="id">Stable population identity.</param>
    /// <param name="count">Bounded population size.</param>
    /// <param name="generation">Typed producer whose canonical generation IR enters the world.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public RelationshipWorldBuilder Population<T>(
        string id,
        int count,
        PocoGenerationDefinition<T> generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return Population(id, count, generation.Definition);
    }

    /// <summary>Adds a typed POCO population with an explicit portable entity identity policy.</summary>
    /// <typeparam name="T">CLR authoring type.</typeparam>
    /// <param name="id">Stable population identity.</param>
    /// <param name="count">Bounded population size.</param>
    /// <param name="entityIdentity">Population entity-identity policy.</param>
    /// <param name="generation">Typed producer whose canonical generation IR enters the world.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entityIdentity"/> or <paramref name="generation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public RelationshipWorldBuilder Population<T>(
        string id,
        int count,
        WorldEntityIdentityPolicy entityIdentity,
        PocoGenerationDefinition<T> generation)
    {
        world.Population(id, count, entityIdentity, generation);
        return this;
    }

    /// <summary>Binds one canonical relationship to source and target populations.</summary>
    /// <param name="sourcePopulationId">Stable source population identity.</param>
    /// <param name="relationshipId">Canonical relationship identity.</param>
    /// <param name="targetPopulationId">Stable target population identity.</param>
    /// <param name="selection">Selection policy, or the always-present uniform convention when omitted.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourcePopulationId"/> or <paramref name="targetPopulationId"/> is empty or white-space.
    /// </exception>
    public RelationshipWorldBuilder Relationship(
        string sourcePopulationId,
        RelationshipId relationshipId,
        string targetPopulationId,
        WorldRelationshipSelectionPolicy? selection = null)
    {
        relationships.Add(new(
            sourcePopulationId,
            relationshipId,
            targetPopulationId,
            selection ?? WorldRelationshipSelectionPolicy.Uniform));
        return this;
    }

    /// <summary>Names one exact population sequence member.</summary>
    /// <param name="id">Stable world-wide exemplar identity.</param>
    /// <param name="populationId">Stable containing population identity.</param>
    /// <param name="sequenceIndex">Zero-based sequence index.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="populationId"/> is empty or white-space.
    /// </exception>
    public RelationshipWorldBuilder Exemplar(string id, string populationId, int sequenceIndex)
    {
        world.Exemplar(id, populationId, sequenceIndex);
        return this;
    }

    internal RelationshipWorldDefinition Build(
        string id,
        string revision,
        RelationshipCatalogDocument relationshipCatalog) =>
        new(
            world.Build(id, revision),
            relationshipCatalog,
            [.. relationships]);
}

/// <summary>Entry point for fluent relationship-world authoring.</summary>
public static class SimulationRelations
{
    /// <summary>Defines a relationship-linked static simulation world.</summary>
    /// <param name="id">Stable logical world identity.</param>
    /// <param name="revision">Exact authored revision.</param>
    /// <param name="relationshipCatalog">Exact fingerprint-pinned canonical relationship authority.</param>
    /// <param name="configure">Immediate authoring callback.</param>
    /// <returns>Canonical provider-neutral relationship-world IR.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relationshipCatalog"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <paramref name="revision"/> is empty.</exception>
    public static RelationshipWorldDefinition DefineWorld(
        string id,
        string revision,
        RelationshipCatalogDocument relationshipCatalog,
        Action<RelationshipWorldBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(relationshipCatalog);
        ArgumentNullException.ThrowIfNull(configure);
        RelationshipWorldBuilder builder = new();
        configure(builder);
        return builder.Build(id, revision, relationshipCatalog);
    }
}
