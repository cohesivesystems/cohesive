using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Scenarios;

/// <summary>Stable diagnostic codes emitted by deterministic scenario execution.</summary>
public static class ScenarioExecutionDiagnosticCodes
{
    /// <summary>An action interpreter returned no action result.</summary>
    public const string ResultMissing = "simulation.scenario.execution.resultMissing";

    /// <summary>An action outcome carries a contract other than the operation's declared output contract.</summary>
    public const string OutputContractMismatch = "simulation.scenario.execution.outputContractMismatch";

    /// <summary>An action interpreter returned state changes that cannot advance the current world.</summary>
    public const string StateChangesInvalid = "simulation.scenario.execution.stateChangesInvalid";
}

/// <summary>One explicit replacement of a scenario actor's complete observation.</summary>
/// <remarks>
/// The before observation is optimistic evidence about the exact state interpreted by an action. The runner applies
/// the replacement only when that evidence equals the current actor observation. Actor identity and origin replay
/// evidence cannot change through this contract.
/// </remarks>
public sealed record ScenarioActorStateChange
{
    /// <summary>Creates one evidence-backed actor observation replacement.</summary>
    /// <param name="actorId">Stable identity of the actor whose observation changes.</param>
    /// <param name="before">Complete observation the interpreter read.</param>
    /// <param name="after">Complete replacement observation produced by the interpreter.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="actorId"/>, <paramref name="before"/>, or <paramref name="after"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="actorId"/> is empty, the observations use different shapes, or they are equal.
    /// </exception>
    [JsonConstructor]
    public ScenarioActorStateChange(string actorId, Observation before, Observation after)
    {
        ActorId = Guard.RequireNotNullOrWhiteSpace(actorId);
        Before = Guard.RequireNotNull(before);
        After = Guard.RequireNotNull(after);
        if (before.ShapeId != after.ShapeId)
        {
            throw new ArgumentException(
                $"Actor state cannot change shape from '{before.ShapeId}' to '{after.ShapeId}'.",
                nameof(after));
        }
        if (before.Equals(after))
            throw new ArgumentException("An actor state change must replace the observation.", nameof(after));
    }

    /// <summary>Gets the stable identity of the actor whose observation changes.</summary>
    public string ActorId { get; }

    /// <summary>Gets the complete observation the interpreter read.</summary>
    public Observation Before { get; }

    /// <summary>Gets the complete replacement observation produced by the interpreter.</summary>
    public Observation After { get; }

    /// <summary>Creates a replacement from an exact actor snapshot and its new observation.</summary>
    /// <param name="actor">Actor snapshot interpreted by the action.</param>
    /// <param name="after">Complete replacement observation produced by the interpreter.</param>
    /// <returns>A state change carrying the actor's exact current observation as before-state evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="actor"/> or <paramref name="after"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="after"/> uses another shape or equals the current observation.
    /// </exception>
    public static ScenarioActorStateChange Replace(ScenarioActorSnapshot actor, Observation after)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new(actor.Actor.Id, actor.Observation, after);
    }
}

/// <summary>Portable output and explicit world-state effects returned by one scenario action interpreter.</summary>
public sealed record ScenarioActionResult
{
    /// <summary>Creates one action interpretation result.</summary>
    /// <param name="output">Portable operation output or semantic failure evidence.</param>
    /// <param name="stateChanges">Actor observation replacements, in any order.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="output"/> or an element of <paramref name="stateChanges"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">State changes contain the same actor more than once.</exception>
    [JsonConstructor]
    public ScenarioActionResult(
        PortableValue output,
        ImmutableArray<ScenarioActorStateChange> stateChanges = default)
    {
        Output = Guard.RequireNotNull(output);
        StateChanges = NormalizeStateChanges(stateChanges);
    }

    /// <summary>Gets the portable operation output or semantic failure evidence.</summary>
    public PortableValue Output { get; }

    /// <summary>Gets actor replacements in canonical actor-identity order.</summary>
    public ImmutableArray<ScenarioActorStateChange> StateChanges { get; }

    /// <summary>Creates an action result that leaves every actor observation unchanged.</summary>
    /// <param name="output">Portable operation output or semantic failure evidence.</param>
    /// <returns>An action result with no state changes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    public static ScenarioActionResult Unchanged(PortableValue output) => new(output, []);

    internal static ImmutableArray<ScenarioActorStateChange> NormalizeStateChanges(
        ImmutableArray<ScenarioActorStateChange> stateChanges)
    {
        if (stateChanges.IsDefaultOrEmpty)
            return [];

        var normalized = ImmutableArray.CreateBuilder<ScenarioActorStateChange>(stateChanges.Length);
        foreach (var change in stateChanges)
            normalized.Add(Guard.RequireNotNull(change));
        normalized.Sort(static (left, right) => string.CompareOrdinal(left.ActorId, right.ActorId));

        for (var index = 1; index < normalized.Count; index++)
        {
            if (string.Equals(normalized[index - 1].ActorId, normalized[index].ActorId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Actor '{normalized[index].ActorId}' changes more than once in one action.",
                    nameof(stateChanges));
            }
        }

        return normalized.MoveToImmutable();
    }
}

/// <summary>Runtime context for interpreting one action from a canonical scenario schedule.</summary>
/// <remarks>
/// The context is a convenience projection over the retained scenario document and materialized world snapshot. It
/// introduces no second semantic authority: action and operation definitions are resolved from <see cref="Scenario"/>,
/// while actor definitions and observations are projected by <see cref="World"/>.
/// </remarks>
public sealed class ScenarioActionContext
{
    internal ScenarioActionContext(
        ScenarioWorldSnapshot world,
        int sequenceIndex,
        ScenarioActionDefinition action,
        ScenarioOperationDefinition operation,
        ScenarioActorSnapshot actorSnapshot,
        ScenarioActorSnapshot? targetActorSnapshot,
        PortableValue input)
    {
        World = world;
        SequenceIndex = sequenceIndex;
        Action = action;
        Operation = operation;
        ActorSnapshot = actorSnapshot;
        TargetActorSnapshot = targetActorSnapshot;
        Input = input;
    }

    /// <summary>Gets the complete materialized world snapshot used for this action.</summary>
    public ScenarioWorldSnapshot World { get; }

    /// <summary>Gets the exact fingerprint-verified scenario document being interpreted.</summary>
    public ScenarioDefinitionDocument Scenario => World.Scenario;

    /// <summary>Gets the zero-based position in canonical virtual-time and action-identity order.</summary>
    public int SequenceIndex { get; }

    /// <summary>Gets the exact scheduled action intent.</summary>
    public ScenarioActionDefinition Action { get; }

    /// <summary>Gets the operation contract selected by <see cref="ScenarioActionDefinition.OperationId"/>.</summary>
    public ScenarioOperationDefinition Operation { get; }

    /// <summary>Gets the actor selected by <see cref="ScenarioActionDefinition.ActorId"/>.</summary>
    public ScenarioActorDefinition Actor => ActorSnapshot.Actor;

    /// <summary>Gets the actor state visible immediately before this action.</summary>
    public ScenarioActorSnapshot ActorSnapshot { get; }

    /// <summary>Gets the optional target actor selected by the action.</summary>
    public ScenarioActorDefinition? TargetActor => TargetActorSnapshot?.Actor;

    /// <summary>Gets the target actor state visible immediately before this action, when selected.</summary>
    public ScenarioActorSnapshot? TargetActorSnapshot { get; }

    /// <summary>Gets the action input represented against the operation's exact input contract.</summary>
    public PortableValue Input { get; }
}

/// <summary>Interprets scheduled scenario actions against a test model, application, or external system.</summary>
/// <remarks>
/// Implementations are runtime policy and never enter canonical scenario IR. <see cref="Identity"/> must identify
/// the exact interpreter behavior and version used to produce retained outcomes. Throwing represents an operational
/// execution failure and does not produce a complete trace. Expected semantic inability to produce a value should be
/// returned through <see cref="ScenarioActionResult.Unchanged(PortableValue)"/> with a
/// <see cref="PortableValue.Failed(ValueContract, DocumentValidationDiagnostic)"/> output.
/// </remarks>
public interface IScenarioActionInterpreter
{
    /// <summary>Gets the exact interpreter identity and version retained by resulting traces.</summary>
    string Identity { get; }

    /// <summary>Interprets one action at its declared virtual UTC instant.</summary>
    /// <param name="context">Canonical action, materialized actors, operation, input, and scenario context.</param>
    /// <param name="cancellationToken">Token that cancels physical interpretation.</param>
    /// <returns>
    /// An output carrying the action operation's exact output contract plus explicit actor state replacements.
    /// </returns>
    /// <remarks>
    /// The runner invokes actions sequentially in canonical schedule order and does not wait for wall-clock time.
    /// A failed or unknown portable value is retained as an outcome and does not implicitly stop later actions.
    /// </remarks>
    ValueTask<ScenarioActionResult> ExecuteAsync(
        ScenarioActionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Failure raised when an interpreter violates the scenario execution boundary.</summary>
public sealed class ScenarioExecutionException : InvalidOperationException
{
    internal ScenarioExecutionException(DocumentValidationResult validation)
        : base(CreateMessage(validation)) => Validation = validation;

    /// <summary>Gets structured evidence describing the execution-boundary violation.</summary>
    public DocumentValidationResult Validation { get; }

    static string CreateMessage(DocumentValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        var errors = validation.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}");
        return $"Scenario execution could not retain an action outcome: {string.Join(" | ", errors)}";
    }
}

/// <summary>Executes canonical scenario schedules through an explicit runtime interpreter.</summary>
public static class ScenarioRunner
{
    /// <summary>Executes every scheduled action sequentially and returns one complete canonical trace.</summary>
    /// <param name="world">Complete materialization of the exact scenario and its initial actors.</param>
    /// <param name="interpreter">Runtime policy that interprets each declared operation.</param>
    /// <param name="cancellationToken">Token that cancels physical interpretation.</param>
    /// <returns>
    /// A complete fingerprint-verified trace retaining the exact scenario, interpreter identity, and action outcomes.
    /// </returns>
    /// <remarks>
    /// Virtual time advances by selecting actions in compiled schedule order; this method never delays against the
    /// wall clock. Actions at one instant execute in ordinal action-identity order. Valid state changes are applied
    /// atomically between interpreter calls, so each context sees all prior action effects. A returned
    /// <see cref="PortableValueState.Failed"/> or <see cref="PortableValueState.Unknown"/> value remains evidence and
    /// does not implicitly control later scheduling.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="world"/> or <paramref name="interpreter"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><see cref="IScenarioActionInterpreter.Identity"/> is empty.</exception>
    /// <exception cref="ScenarioExecutionException">
    /// The interpreter returns no value, a different output contract, or a value invalid for the declared contract.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static async Task<ScenarioExecutionTraceDocument> ExecuteAsync(
        ScenarioWorldSnapshot world,
        IScenarioActionInterpreter interpreter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(interpreter);
        var interpreterIdentity = Guard.RequireNotNullOrWhiteSpace(interpreter.Identity);
        var initialWorld = world;
        var scenario = world.Scenario;
        var plan = scenario.Compile();
        var actions = plan.Definition.Actions;
        var outcomes = ImmutableArray.CreateBuilder<ScenarioActionOutcome>(actions.Length);

        for (var index = 0; index < actions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = actions[index];
            var operation = plan.GetOperation(action.OperationId);
            var actorSnapshot = world.GetActor(action.ActorId);
            var targetActorSnapshot = action.TargetActorId is { } targetSnapshotId
                ? world.GetActor(targetSnapshotId)
                : null;
            var context = new ScenarioActionContext(
                world,
                index,
                action,
                operation,
                actorSnapshot,
                targetActorSnapshot,
                ToPortableInput(action.Input, operation.Input));
            var result = await interpreter.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                throw Failure(new(
                    Code: ScenarioExecutionDiagnosticCodes.ResultMissing,
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Interpreter returned no result for action '{action.Id}'.",
                    Location: $"/outcomes/{index}",
                    Evidence: Evidence(action.Id)));
            }

            ValidateOutput(result.Output, operation, action, index);
            try
            {
                world = world.Apply(result.StateChanges);
            }
            catch (ArgumentException exception)
            {
                throw Failure(new(
                    Code: ScenarioExecutionDiagnosticCodes.StateChangesInvalid,
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Action '{action.Id}' returned invalid state changes: {exception.Message}",
                    Location: $"/outcomes/{index}/stateChanges",
                    Evidence: Evidence(action.Id)));
            }
            outcomes.Add(new(action.Id, result.Output, result.StateChanges));
        }

        return ScenarioExecutionTraceDocument.FromOutcomes(
            initialWorld,
            interpreterIdentity,
            outcomes.MoveToImmutable());
    }

    static PortableValue ToPortableInput(ObservationValue input, ValueContract contract) => input.Kind switch
    {
        ObservationValueKind.Undefined => PortableValue.Missing(contract),
        ObservationValueKind.Null => PortableValue.Null(contract),
        _ => PortableValue.Concrete(contract, input)
    };

    static void ValidateOutput(
        PortableValue output,
        ScenarioOperationDefinition operation,
        ScenarioActionDefinition action,
        int index)
    {
        var location = $"/outcomes/{index}/output";
        if (output.Contract != operation.Output)
        {
            throw Failure(new(
                Code: ScenarioExecutionDiagnosticCodes.OutputContractMismatch,
                Severity: DiagnosticSeverity.Error,
                Message: $"Action '{action.Id}' output does not carry operation '{operation.Id}'s declared contract.",
                Location: $"{location}/contract",
                Evidence: Evidence(action.Id)));
        }

        var validation = PortableExecutionValidator.Validate(output);
        if (validation.IsValid)
            return;

        throw new ScenarioExecutionException(new(DocumentValidationDiagnostics.Normalize(
        [
            .. validation.Diagnostics.Select(diagnostic => diagnostic with
            {
                Location = PrefixLocation(location, diagnostic.Location),
                Evidence = Evidence(action.Id)
            })
        ])));
    }

    static ScenarioExecutionException Failure(DocumentValidationDiagnostic diagnostic) =>
        new(new([diagnostic]));

    static DocumentDiagnosticEvidence Evidence(string actionId) =>
        new(stage: "scenario-execution", subject: actionId);

    static string PrefixLocation(string prefix, string? location) =>
        string.IsNullOrEmpty(location) || location == "/"
            ? prefix
            : location[0] == '/'
                ? prefix + location
                : $"{prefix}/{location}";
}
