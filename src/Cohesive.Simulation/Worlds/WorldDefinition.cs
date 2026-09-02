using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Worlds;

/// <summary>One stable bounded population within a simulation world.</summary>
public sealed record WorldPopulationDefinition
{
    /// <summary>Creates a world population definition.</summary>
    /// <param name="id">Stable population identity within the owning world.</param>
    /// <param name="count">Number of initial observations in the population.</param>
    /// <param name="generation">Canonical generation semantics for one population member.</param>
    /// <exception cref="ArgumentNullException"><paramref name="generation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    /// <remarks>A negative <paramref name="count"/> is retained for structured compiler diagnostics.</remarks>
    [JsonConstructor]
    public WorldPopulationDefinition(string id, int count, GenerationDefinition generation)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Count = count;
        Generation = Guard.RequireNotNull(generation);
    }

    /// <summary>Gets the stable population identity within the owning world.</summary>
    public string Id { get; }

    /// <summary>Gets the declared number of initial observations.</summary>
    public int Count { get; }

    /// <summary>Gets the canonical generation semantics for one population member.</summary>
    public GenerationDefinition Generation { get; }
}

/// <summary>Stable semantic alias for one exact generated member of a world population.</summary>
public sealed record WorldExemplarDefinition
{
    /// <summary>Creates a named world exemplar.</summary>
    /// <param name="id">Stable world-wide exemplar identity.</param>
    /// <param name="populationId">Stable identity of the containing population.</param>
    /// <param name="sequenceIndex">Zero-based sequence index within the population.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> or <paramref name="populationId"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <paramref name="populationId"/> is empty.</exception>
    /// <remarks>
    /// Sequence bounds are validated by the world compiler so malformed direct IR produces structured diagnostics.
    /// </remarks>
    [JsonConstructor]
    public WorldExemplarDefinition(string id, string populationId, int sequenceIndex)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        PopulationId = Guard.RequireNotNullOrWhiteSpace(populationId);
        SequenceIndex = sequenceIndex;
    }

    /// <summary>Gets the stable world-wide exemplar identity.</summary>
    public string Id { get; }

    /// <summary>Gets the stable identity of the containing population.</summary>
    public string PopulationId { get; }

    /// <summary>Gets the zero-based sequence index within the population.</summary>
    public int SequenceIndex { get; }
}

/// <summary>Portable semantic authority for a static initial simulation world.</summary>
/// <remarks>
/// A world currently defines initial populations and stable aliases to exact population members. Temporal activity,
/// transitions, causality, and scheduling will belong to a scenario layer once those semantics are explicit.
/// </remarks>
public sealed record WorldDefinition
{
    /// <summary>Creates a simulation world definition without named exemplars.</summary>
    /// <param name="id">Stable logical world identity.</param>
    /// <param name="revision">Exact authored world revision.</param>
    /// <param name="populations">Initial population declarations. Declaration order is non-semantic.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="revision"/> is empty or white-space.
    /// </exception>
    public WorldDefinition(
        string id,
        string revision,
        ImmutableArray<WorldPopulationDefinition> populations)
        : this(id, revision, populations, [])
    {
    }

    /// <summary>Creates a simulation world definition.</summary>
    /// <param name="id">Stable logical world identity.</param>
    /// <param name="revision">Exact authored world revision.</param>
    /// <param name="populations">Initial population declarations. Declaration order is non-semantic.</param>
    /// <param name="exemplars">Named population members. Declaration order is non-semantic.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="revision"/> is empty or white-space.
    /// </exception>
    [JsonConstructor]
    public WorldDefinition(
        string id,
        string revision,
        ImmutableArray<WorldPopulationDefinition> populations,
        ImmutableArray<WorldExemplarDefinition> exemplars)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        Populations = populations.IsDefault ? [] : populations;
        Exemplars = exemplars.IsDefault ? [] : exemplars;
    }

    /// <summary>Gets the stable logical world identity.</summary>
    public string Id { get; }

    /// <summary>Gets the exact authored world revision.</summary>
    public string Revision { get; }

    /// <summary>Gets initial population declarations.</summary>
    public ImmutableArray<WorldPopulationDefinition> Populations { get; }

    /// <summary>Gets stable world-wide aliases for exact generated population members.</summary>
    public ImmutableArray<WorldExemplarDefinition> Exemplars { get; }

    /// <summary>Attempts provider-neutral world compilation and retains structured diagnostics.</summary>
    /// <returns>A result containing a compiled world only when every population is valid.</returns>
    public WorldCompilationResult CompileResult() => WorldCompiler.Compile(this);

    /// <summary>Compiles this world for deterministic reference generation.</summary>
    /// <returns>An immutable reusable world plan.</returns>
    /// <exception cref="WorldCompilationException">World or nested generation validation fails.</exception>
    public CompiledWorldPlan Compile()
    {
        var result = CompileResult();
        return result.Plan ?? throw new WorldCompilationException(result.Validation);
    }
}

/// <summary>Deterministic convention deriving isolated generation scopes for world populations.</summary>
public static class WorldPopulationScopeConvention
{
    /// <summary>Stable identity of the current world-population scope convention.</summary>
    public const string Identity = "cohesive-simulation-world-population-scope/v1";

    /// <summary>Derives an unambiguous scope from exact world and population identities.</summary>
    /// <param name="worldId">Stable logical world identity.</param>
    /// <param name="populationId">Stable population identity within the world.</param>
    /// <returns>A scope containing reversible URL-safe UTF-8 encodings of both identities.</returns>
    /// <exception cref="ArgumentNullException">An identity is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is empty or white-space.</exception>
    public static GenerationScope Create(string worldId, string populationId)
    {
        worldId = Guard.RequireNotNullOrWhiteSpace(worldId);
        populationId = Guard.RequireNotNullOrWhiteSpace(populationId);
        return new($"{Identity}/{Encode(worldId)}/{Encode(populationId)}");
    }

    static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
