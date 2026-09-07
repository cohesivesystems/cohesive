using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Scenarios;

/// <summary>One materialized scenario actor at the initial-world boundary.</summary>
/// <remarks>
/// The actor and exemplar definitions remain projections of the owning scenario and artifact. The observation,
/// entity identity, and replay token are runtime interpretation results and do not replace either semantic authority.
/// </remarks>
public sealed class ScenarioActorSnapshot
{
    /// <summary>Creates one materialized actor snapshot.</summary>
    /// <param name="actor">Exact scenario actor definition represented by this snapshot.</param>
    /// <param name="exemplar">Exact initial-world exemplar selected by <paramref name="actor"/>.</param>
    /// <param name="entityId">Canonical entity identity assigned by the world interpreter.</param>
    /// <param name="observation">Complete actor observation produced by the world interpreter.</param>
    /// <param name="replayToken">Opaque interpreter-specific evidence for replaying the exact observation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="actor"/>, <paramref name="exemplar"/>, or <paramref name="observation"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The entity identity or replay token is empty, or the actor does not select the supplied exemplar.
    /// </exception>
    public ScenarioActorSnapshot(
        ScenarioActorDefinition actor,
        WorldExemplarDefinition exemplar,
        EntityId entityId,
        Observation observation,
        string replayToken)
    {
        Actor = Guard.RequireNotNull(actor);
        Exemplar = Guard.RequireNotNull(exemplar);
        if (!string.Equals(actor.ExemplarId, exemplar.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Actor '{actor.Id}' selects exemplar '{actor.ExemplarId}', not '{exemplar.Id}'.",
                nameof(exemplar));
        }
        if (string.IsNullOrWhiteSpace(entityId.Value))
            throw new ArgumentException("A scenario actor snapshot requires an entity identity.", nameof(entityId));

        EntityId = entityId;
        Observation = Guard.RequireNotNull(observation);
        ReplayToken = Guard.RequireNotNullOrWhiteSpace(replayToken);
    }

    /// <summary>Gets the exact scenario actor definition represented by this snapshot.</summary>
    public ScenarioActorDefinition Actor { get; }

    /// <summary>Gets the exact initial-world exemplar selected by <see cref="Actor"/>.</summary>
    public WorldExemplarDefinition Exemplar { get; }

    /// <summary>Gets the canonical world entity identity represented by this actor.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the complete initial observation produced by the world interpreter.</summary>
    public Observation Observation { get; }

    /// <summary>Gets opaque canonical interpreter-specific replay evidence for the initial observation.</summary>
    public string ReplayToken { get; }
}

/// <summary>Immutable materialization of every actor in one exact scenario's initial world.</summary>
/// <remarks>
/// The retained scenario and its world artifact remain the semantic and replay authorities. This snapshot is the
/// concrete runtime projection supplied to action interpreters. Actors are retained in canonical actor-identity order.
/// </remarks>
public sealed class ScenarioWorldSnapshot
{
    readonly IReadOnlyDictionary<string, ScenarioActorSnapshot> actorsById;

    ScenarioWorldSnapshot(
        ScenarioDefinitionDocument scenario,
        ImmutableArray<ScenarioActorSnapshot> actors,
        IReadOnlyDictionary<string, ScenarioActorSnapshot> actorsById)
    {
        Scenario = scenario;
        Actors = actors;
        this.actorsById = actorsById;
    }

    /// <summary>Gets the exact fingerprint-verified scenario whose initial actors were materialized.</summary>
    public ScenarioDefinitionDocument Scenario { get; }

    /// <summary>Gets every materialized actor in canonical actor-identity order.</summary>
    public ImmutableArray<ScenarioActorSnapshot> Actors { get; }

    /// <summary>Creates a validated snapshot from interpreter-produced actor materializations.</summary>
    /// <param name="scenario">Exact fingerprint-verified scenario represented by the snapshot.</param>
    /// <param name="actors">One materialization for every actor declared by <paramref name="scenario"/>.</param>
    /// <returns>A normalized immutable snapshot in canonical actor-identity order.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scenario"/> or an element of <paramref name="actors"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Actor materializations are missing, duplicated, unexpected, or disagree with the scenario's exact actor and
    /// exemplar definitions.
    /// </exception>
    /// <remarks>
    /// This is the provider-neutral adapter seam. The package owning the artifact interpreter is responsible for
    /// validating its retained world, generated observations, entity identities, and replay tokens before calling it.
    /// </remarks>
    public static ScenarioWorldSnapshot Create(
        ScenarioDefinitionDocument scenario,
        ImmutableArray<ScenarioActorSnapshot> actors)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (actors.IsDefault)
            throw new ArgumentException("Scenario actor snapshots must be initialized.", nameof(actors));

        var definition = scenario.Definition;
        Dictionary<string, ScenarioActorSnapshot> supplied = new(actors.Length, StringComparer.Ordinal);
        foreach (var actor in actors)
        {
            ArgumentNullException.ThrowIfNull(actor);
            if (!supplied.TryAdd(actor.Actor.Id, actor))
            {
                throw new ArgumentException(
                    $"Actor '{actor.Actor.Id}' is materialized more than once.",
                    nameof(actors));
            }
        }

        var normalized = ImmutableArray.CreateBuilder<ScenarioActorSnapshot>(definition.Actors.Length);
        foreach (var expectedActor in definition.Actors)
        {
            if (!supplied.Remove(expectedActor.Id, out var actor))
            {
                throw new ArgumentException(
                    $"Scenario actor '{expectedActor.Id}' has no materialized snapshot.",
                    nameof(actors));
            }
            if (actor.Actor != expectedActor)
            {
                throw new ArgumentException(
                    $"Snapshot for actor '{expectedActor.Id}' does not retain its exact scenario definition.",
                    nameof(actors));
            }

            var expectedExemplar = definition.InitialWorld.GetExemplar(expectedActor.ExemplarId);
            if (actor.Exemplar != expectedExemplar)
            {
                throw new ArgumentException(
                    $"Snapshot for actor '{expectedActor.Id}' does not retain its exact artifact exemplar.",
                    nameof(actors));
            }

            normalized.Add(actor);
        }

        if (supplied.Count > 0)
        {
            var unexpected = supplied.Keys.Order(StringComparer.Ordinal).First();
            throw new ArgumentException(
                $"Actor snapshot '{unexpected}' is not declared by scenario '{definition.Id}'.",
                nameof(actors));
        }

        var canonicalActors = normalized.MoveToImmutable();
        Dictionary<string, ScenarioActorSnapshot> actorsById = new(canonicalActors.Length, StringComparer.Ordinal);
        foreach (var actor in canonicalActors)
            actorsById.Add(actor.Actor.Id, actor);
        return new(scenario, canonicalActors, actorsById);
    }

    /// <summary>Materializes every actor through the core reference world interpreter.</summary>
    /// <param name="scenario">Exact scenario whose core initial-world artifact will be interpreted.</param>
    /// <returns>A complete initial-world actor snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scenario"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// The scenario's artifact selects another world schema, interpreter, or entropy algorithm.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">The retained core world document is invalid.</exception>
    /// <exception cref="WorldCompilationException">The retained core world fails semantic compilation.</exception>
    /// <exception cref="WorldGenerationException">A selected exemplar cannot be generated with valid identity.</exception>
    public static ScenarioWorldSnapshot FromCoreWorld(ScenarioDefinitionDocument scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var artifact = scenario.Definition.InitialWorld;
        artifact.RequireCoreReferenceCompatibility();
        var world = artifact.GetCoreWorld().Compile();
        return Materialize(
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

    /// <summary>Attempts to find one materialized actor by stable scenario identity.</summary>
    /// <param name="id">Stable scenario actor identity.</param>
    /// <param name="actor">Receives the materialized actor when found.</param>
    /// <returns><see langword="true"/> when the actor exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public bool TryGetActor(string id, out ScenarioActorSnapshot? actor) =>
        actorsById.TryGetValue(Guard.RequireNotNullOrWhiteSpace(id), out actor);

    /// <summary>Gets one materialized actor by stable scenario identity.</summary>
    /// <param name="id">Stable scenario actor identity.</param>
    /// <returns>The materialized actor named by <paramref name="id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    /// <exception cref="KeyNotFoundException">No actor has the supplied identity.</exception>
    public ScenarioActorSnapshot GetActor(string id) =>
        TryGetActor(id, out var actor)
            ? actor!
            : throw new KeyNotFoundException(
                $"Scenario '{Scenario.Definition.Id}' contains no materialized actor with identity '{id}'.");

    internal static ScenarioWorldSnapshot Materialize(
        ScenarioDefinitionDocument scenario,
        Func<ScenarioActorDefinition, WorldExemplarDefinition, ScenarioActorSnapshot> materialize)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(materialize);
        var definition = scenario.Definition;
        var actors = ImmutableArray.CreateBuilder<ScenarioActorSnapshot>(definition.Actors.Length);
        foreach (var actor in definition.Actors)
        {
            var exemplar = definition.InitialWorld.GetExemplar(actor.ExemplarId);
            actors.Add(Guard.RequireNotNull(materialize(actor, exemplar)));
        }

        return Create(scenario, actors.MoveToImmutable());
    }
}
