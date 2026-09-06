using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Simulation.Artifacts;

namespace Cohesive.Simulation.Scenarios;

/// <summary>Portable input and output contracts for one scenario operation.</summary>
public sealed record ScenarioOperationDefinition
{
    /// <summary>Creates a scenario operation contract.</summary>
    /// <param name="id">Stable operation identity interpreted by an execution target.</param>
    /// <param name="input">Portable contract required for every scheduled input.</param>
    /// <param name="output">Portable contract required for every retained action outcome.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/>, <paramref name="input"/>, or <paramref name="output"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ScenarioOperationDefinition(string id, ValueContract input, ValueContract output)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Input = Guard.RequireNotNull(input);
        Output = Guard.RequireNotNull(output);
    }

    /// <summary>Gets the stable target-interpreted operation identity.</summary>
    public string Id { get; }

    /// <summary>Gets the portable contract required for scheduled inputs.</summary>
    public ValueContract Input { get; }

    /// <summary>Gets the portable contract required for retained outcomes.</summary>
    public ValueContract Output { get; }
}

/// <summary>One stable scenario actor bound to a named member of the initial world.</summary>
public sealed record ScenarioActorDefinition
{
    /// <summary>Creates a world-bound scenario actor.</summary>
    /// <param name="id">Stable actor identity within the scenario.</param>
    /// <param name="exemplarId">Named exemplar identity in the initial world artifact.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="exemplarId"/> is empty or white-space.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/> or <paramref name="exemplarId"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ScenarioActorDefinition(string id, string exemplarId)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        ExemplarId = Guard.RequireNotNullOrWhiteSpace(exemplarId);
    }

    /// <summary>Gets the stable actor identity within the scenario.</summary>
    public string Id { get; }

    /// <summary>Gets the named initial-world exemplar represented by this actor.</summary>
    public string ExemplarId { get; }
}

/// <summary>One exact action scheduled on a scenario's virtual UTC timeline.</summary>
public sealed record ScenarioActionDefinition
{
    /// <summary>Creates one scheduled scenario action.</summary>
    /// <param name="id">Stable action identity within the scenario.</param>
    /// <param name="scheduledAtUtc">Exact virtual UTC instant at which the action becomes eligible.</param>
    /// <param name="actorId">Stable identity of the actor invoking the operation.</param>
    /// <param name="operationId">Stable identity of the declared operation contract.</param>
    /// <param name="input">Exact portable operation input.</param>
    /// <param name="targetActorId">Optional stable identity of another actor targeted by the action.</param>
    /// <exception cref="ArgumentException">
    /// An action, actor, operation, or optional target identity is empty or white-space.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/>, <paramref name="actorId"/>, or <paramref name="operationId"/> is
    /// <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ScenarioActionDefinition(
        string id,
        DateTimeOffset scheduledAtUtc,
        string actorId,
        string operationId,
        ObservationValue input,
        string? targetActorId = null)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        ScheduledAtUtc = scheduledAtUtc;
        ActorId = Guard.RequireNotNullOrWhiteSpace(actorId);
        OperationId = Guard.RequireNotNullOrWhiteSpace(operationId);
        Input = input;
        TargetActorId = targetActorId is null
            ? null
            : Guard.RequireNotNullOrWhiteSpace(targetActorId);
    }

    /// <summary>Gets the stable action identity within the scenario.</summary>
    public string Id { get; }

    /// <summary>Gets the exact virtual UTC instant at which the action becomes eligible.</summary>
    public DateTimeOffset ScheduledAtUtc { get; }

    /// <summary>Gets the stable identity of the actor invoking the operation.</summary>
    public string ActorId { get; }

    /// <summary>Gets the stable identity of the declared operation contract.</summary>
    public string OperationId { get; }

    /// <summary>Gets the exact portable operation input.</summary>
    public ObservationValue Input { get; }

    /// <summary>Gets the optional identity of another actor targeted by this action.</summary>
    public string? TargetActorId { get; }
}

/// <summary>Portable semantic authority for one deterministic scheduled scenario.</summary>
/// <remarks>
/// The initial artifact owns generated world identity and replay. This definition owns actor aliases, operation
/// contracts, virtual time, and scheduled action intent. Execution targets and observed outcomes are interpretations
/// and do not become part of this source definition.
/// </remarks>
public sealed record ScenarioDefinition
{
    /// <summary>Creates a deterministic scheduled scenario.</summary>
    /// <param name="id">Stable logical scenario identity.</param>
    /// <param name="revision">Exact authored scenario revision.</param>
    /// <param name="initialWorld">Exact generated-world artifact from which execution begins.</param>
    /// <param name="startsAtUtc">Fixed UTC origin of the scenario's virtual timeline.</param>
    /// <param name="operations">Portable operation contracts. Declaration order is non-semantic.</param>
    /// <param name="actors">World-bound actors. Declaration order is non-semantic.</param>
    /// <param name="actions">Scheduled action intents. Declaration order is non-semantic.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/>, <paramref name="revision"/>, or <paramref name="initialWorld"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="revision"/> is empty or white-space.
    /// </exception>
    /// <remarks>
    /// Cross-reference, UTC, portability, type compatibility, uniqueness, and schedule bounds are retained for
    /// structured compiler diagnostics.
    /// </remarks>
    [JsonConstructor]
    public ScenarioDefinition(
        string id,
        string revision,
        WorldArtifactManifest initialWorld,
        DateTimeOffset startsAtUtc,
        ImmutableArray<ScenarioOperationDefinition> operations,
        ImmutableArray<ScenarioActorDefinition> actors,
        ImmutableArray<ScenarioActionDefinition> actions)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        InitialWorld = Guard.RequireNotNull(initialWorld);
        StartsAtUtc = startsAtUtc;
        Operations = operations.IsDefault ? [] : operations;
        Actors = actors.IsDefault ? [] : actors;
        Actions = actions.IsDefault ? [] : actions;
    }

    /// <summary>Gets the stable logical scenario identity.</summary>
    public string Id { get; }

    /// <summary>Gets the exact authored scenario revision.</summary>
    public string Revision { get; }

    /// <summary>Gets the exact generated-world artifact from which execution begins.</summary>
    public WorldArtifactManifest InitialWorld { get; }

    /// <summary>Gets the fixed UTC origin of the scenario's virtual timeline.</summary>
    public DateTimeOffset StartsAtUtc { get; }

    /// <summary>Gets portable operation contracts.</summary>
    public ImmutableArray<ScenarioOperationDefinition> Operations { get; }

    /// <summary>Gets actors bound to named initial-world exemplars.</summary>
    public ImmutableArray<ScenarioActorDefinition> Actors { get; }

    /// <summary>Gets scheduled action intents.</summary>
    public ImmutableArray<ScenarioActionDefinition> Actions { get; }

    /// <summary>Attempts provider-neutral scenario compilation and retains structured diagnostics.</summary>
    /// <returns>A result containing a compiled plan only when every scenario invariant is satisfied.</returns>
    public ScenarioCompilationResult CompileResult() => ScenarioCompiler.Compile(this);

    /// <summary>Compiles this scenario into an immutable normalized schedule.</summary>
    /// <returns>An immutable plan for the exact initial world, actors, operations, and scheduled actions.</returns>
    /// <exception cref="ScenarioCompilationException">Scenario validation fails.</exception>
    public CompiledScenarioPlan Compile()
    {
        var result = CompileResult();
        return result.Plan ?? throw new ScenarioCompilationException(result.Validation);
    }
}
