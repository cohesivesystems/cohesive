using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Scenarios;

/// <summary>Stable diagnostic codes emitted by deterministic scenario execution.</summary>
public static class ScenarioExecutionDiagnosticCodes
{
    /// <summary>An action interpreter returned no portable outcome.</summary>
    public const string OutputMissing = "simulation.scenario.execution.outputMissing";

    /// <summary>An action outcome carries a contract other than the operation's declared output contract.</summary>
    public const string OutputContractMismatch = "simulation.scenario.execution.outputContractMismatch";
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

    /// <summary>Gets the materialized initial-world state of <see cref="Actor"/>.</summary>
    public ScenarioActorSnapshot ActorSnapshot { get; }

    /// <summary>Gets the optional target actor selected by the action.</summary>
    public ScenarioActorDefinition? TargetActor => TargetActorSnapshot?.Actor;

    /// <summary>Gets the materialized initial-world state of <see cref="TargetActor"/>, when selected.</summary>
    public ScenarioActorSnapshot? TargetActorSnapshot { get; }

    /// <summary>Gets the action input represented against the operation's exact input contract.</summary>
    public PortableValue Input { get; }
}

/// <summary>Interprets scheduled scenario actions against a test model, application, or external system.</summary>
/// <remarks>
/// Implementations are runtime policy and never enter canonical scenario IR. <see cref="Identity"/> must identify
/// the exact interpreter behavior and version used to produce retained outcomes. Throwing represents an operational
/// execution failure and does not produce a complete trace. Expected semantic inability to produce a value should be
/// returned as <see cref="PortableValue.Failed"/>.
/// </remarks>
public interface IScenarioActionInterpreter
{
    /// <summary>Gets the exact interpreter identity and version retained by resulting traces.</summary>
    string Identity { get; }

    /// <summary>Interprets one action at its declared virtual UTC instant.</summary>
    /// <param name="context">Canonical action, materialized actors, operation, input, and scenario context.</param>
    /// <param name="cancellationToken">Token that cancels physical interpretation.</param>
    /// <returns>An outcome carrying the action operation's exact output contract.</returns>
    /// <remarks>
    /// The runner invokes actions sequentially in canonical schedule order and does not wait for wall-clock time.
    /// A failed or unknown portable value is retained as an outcome and does not implicitly stop later actions.
    /// </remarks>
    ValueTask<PortableValue> ExecuteAsync(
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
    /// wall clock. Actions at one instant execute in ordinal action-identity order. A returned
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
            var output = await interpreter.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            ValidateOutput(output, operation, action, index);
            outcomes.Add(new(action.Id, output));
        }

        return ScenarioExecutionTraceDocument.FromOutcomes(
            scenario,
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
        PortableValue? output,
        ScenarioOperationDefinition operation,
        ScenarioActionDefinition action,
        int index)
    {
        var location = $"/outcomes/{index}/output";
        if (output is null)
        {
            throw Failure(new(
                Code: ScenarioExecutionDiagnosticCodes.OutputMissing,
                Severity: DiagnosticSeverity.Error,
                Message: $"Interpreter returned no output for action '{action.Id}'.",
                Location: location,
                Evidence: Evidence(action.Id)));
        }

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
