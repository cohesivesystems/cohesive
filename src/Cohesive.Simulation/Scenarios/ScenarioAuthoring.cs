using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Scenarios;

namespace Cohesive.Simulation;

public static partial class Simulation
{
    /// <summary>Defines a deterministic scenario over one exact generated-world artifact.</summary>
    /// <param name="id">Stable logical scenario identity.</param>
    /// <param name="revision">Exact authored scenario revision.</param>
    /// <param name="initialWorld">Exact generated-world artifact from which execution begins.</param>
    /// <param name="startsAtUtc">Fixed UTC origin of the scenario's virtual timeline.</param>
    /// <param name="configure">Authoring callback that declares operations, actors, and scheduled actions.</param>
    /// <returns>Canonical provider-neutral scenario IR.</returns>
    /// <remarks>
    /// The callback executes immediately and does not survive into canonical IR. CLR operation types and input values
    /// are projected into portable core types and observations before the definition is returned.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/>, <paramref name="revision"/>, <paramref name="initialWorld"/>, or
    /// <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="revision"/> is empty or white-space.
    /// </exception>
    public static ScenarioDefinition DefineScenario(
        string id,
        string revision,
        WorldArtifactManifest initialWorld,
        DateTimeOffset startsAtUtc,
        Action<ScenarioBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(initialWorld);
        ArgumentNullException.ThrowIfNull(configure);
        ScenarioBuilder builder = new(startsAtUtc);
        configure(builder);
        return builder.Build(id, revision, initialWorld);
    }
}

/// <summary>Typed human-authoring projection for deterministic scenario IR.</summary>
public sealed class ScenarioBuilder
{
    static readonly DefaultClrTypeRefMapper TypeMapper = new();
    readonly DateTimeOffset startsAtUtc;
    readonly List<ScenarioOperationDefinition> operations = [];
    readonly List<ScenarioActorDefinition> actors = [];
    readonly List<ScenarioActionDefinition> actions = [];

    internal ScenarioBuilder(DateTimeOffset startsAtUtc) => this.startsAtUtc = startsAtUtc;

    /// <summary>Declares one portable operation contract.</summary>
    /// <param name="id">Stable operation identity interpreted by an execution target.</param>
    /// <param name="input">Portable contract required for every scheduled input.</param>
    /// <param name="output">Portable contract required for every retained outcome.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/>, <paramref name="input"/>, or <paramref name="output"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public ScenarioBuilder Operation(string id, ValueContract input, ValueContract output)
    {
        operations.Add(new(id, input, output));
        return this;
    }

    /// <summary>Declares one operation contract from CLR input and output types.</summary>
    /// <typeparam name="TInput">CLR input type lowered immediately to a portable type reference.</typeparam>
    /// <typeparam name="TOutput">CLR output type lowered immediately to a portable type reference.</typeparam>
    /// <param name="id">Stable operation identity interpreted by an execution target.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public ScenarioBuilder Operation<TInput, TOutput>(string id) =>
        Operation(
            id,
            new(TypeMapper.Map(typeof(TInput), nullability: null)),
            new(TypeMapper.Map(typeof(TOutput), nullability: null)));

    /// <summary>Binds one stable scenario actor to a named initial-world exemplar.</summary>
    /// <param name="id">Stable actor identity within the scenario.</param>
    /// <param name="exemplarId">Named exemplar identity in the initial world artifact.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="exemplarId"/> is empty or white-space.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/> or <paramref name="exemplarId"/> is <see langword="null"/>.
    /// </exception>
    public ScenarioBuilder Actor(string id, string exemplarId)
    {
        actors.Add(new(id, exemplarId));
        return this;
    }

    /// <summary>Schedules one portable action at an offset from the scenario's virtual-time origin.</summary>
    /// <param name="id">Stable action identity within the scenario.</param>
    /// <param name="afterStart">Exact offset from the fixed scenario start.</param>
    /// <param name="actorId">Stable identity of the actor invoking the operation.</param>
    /// <param name="operationId">Stable identity of the declared operation contract.</param>
    /// <param name="input">Exact portable operation input.</param>
    /// <param name="targetActorId">Optional stable identity of another actor targeted by the action.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// An action, actor, operation, or optional target identity is empty or white-space.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/>, <paramref name="actorId"/>, or <paramref name="operationId"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="afterStart"/> moves virtual time outside the supported <see cref="DateTimeOffset"/> range.
    /// </exception>
    public ScenarioBuilder Action(
        string id,
        TimeSpan afterStart,
        string actorId,
        string operationId,
        ObservationValue input,
        string? targetActorId = null)
    {
        actions.Add(new(
            id,
            startsAtUtc.Add(afterStart),
            actorId,
            operationId,
            input,
            targetActorId));
        return this;
    }

    /// <summary>Schedules one CLR input value after immediately lowering it to a portable observation.</summary>
    /// <typeparam name="TInput">CLR input type projected into the declared operation contract.</typeparam>
    /// <param name="id">Stable action identity within the scenario.</param>
    /// <param name="afterStart">Exact offset from the fixed scenario start.</param>
    /// <param name="actorId">Stable identity of the actor invoking the operation.</param>
    /// <param name="operationId">Stable identity of the declared operation contract.</param>
    /// <param name="input">CLR input value lowered immediately to a portable observation value.</param>
    /// <param name="targetActorId">Optional stable identity of another actor targeted by the action.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">
    /// An action, actor, operation, or optional target identity is empty or white-space.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/>, <paramref name="actorId"/>, or <paramref name="operationId"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="afterStart"/> moves virtual time outside the supported <see cref="DateTimeOffset"/> range.
    /// </exception>
    /// <exception cref="NotSupportedException"><paramref name="input"/> cannot be projected portably.</exception>
    public ScenarioBuilder Action<TInput>(
        string id,
        TimeSpan afterStart,
        string actorId,
        string operationId,
        TInput input,
        string? targetActorId = null) =>
        Action(
            id,
            afterStart,
            actorId,
            operationId,
            input is ObservationValue value ? value : ObservationValue.FromObject(input),
            targetActorId);

    internal ScenarioDefinition Build(
        string id,
        string revision,
        WorldArtifactManifest initialWorld) =>
        new(
            id,
            revision,
            initialWorld,
            startsAtUtc,
            [.. operations],
            [.. actors],
            [.. actions]);
}
