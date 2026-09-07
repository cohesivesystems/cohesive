using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Scenarios;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Simulation.Transitions;

/// <summary>Selects which actor supplies and receives one scenario-bound Transition's aggregate state.</summary>
public enum ScenarioTransitionSubject
{
    /// <summary>The action's actor is the Transition subject.</summary>
    Actor = 0,

    /// <summary>The action's explicitly selected target actor is the Transition subject.</summary>
    TargetActor = 1
}

/// <summary>Binds one scenario operation to an exact compiled Transition and subject-selection policy.</summary>
public sealed record ScenarioTransitionBinding
{
    /// <summary>Creates one scenario-to-Transition binding.</summary>
    /// <param name="operationId">Stable scenario operation identity interpreted by this binding.</param>
    /// <param name="transition">Exact compiled Transition plan.</param>
    /// <param name="subject">Actor role whose current observation is the Transition aggregate state.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="operationId"/> or <paramref name="transition"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="operationId"/> is empty or white-space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="subject"/> is unsupported.</exception>
    public ScenarioTransitionBinding(
        string operationId,
        CompiledTransitionPlan transition,
        ScenarioTransitionSubject subject = ScenarioTransitionSubject.Actor)
    {
        OperationId = Guard.RequireNotNullOrWhiteSpace(operationId);
        Transition = Guard.RequireNotNull(transition);
        if (!Enum.IsDefined(subject))
            throw new ArgumentOutOfRangeException(nameof(subject), subject, "Unsupported scenario Transition subject.");
        Subject = subject;
    }

    /// <summary>Gets the stable scenario operation identity interpreted by this binding.</summary>
    public string OperationId { get; }

    /// <summary>Gets the exact compiled Transition plan.</summary>
    public CompiledTransitionPlan Transition { get; }

    /// <summary>Gets the actor role whose current observation is the Transition aggregate state.</summary>
    public ScenarioTransitionSubject Subject { get; }
}

/// <summary>Stable diagnostics produced while projecting Transition decisions into scenario outcomes.</summary>
public static class TransitionScenarioDiagnosticCodes
{
    /// <summary>A Transition decision did not produce a usable authored outcome.</summary>
    public const string DecisionFailed = "simulation.transitions.decision.failed";
}

/// <summary>
/// Interprets scenario actions through exact canonical Cohesive Transition plans and projects accepted patches into
/// explicit scenario actor state changes.
/// </summary>
/// <remarks>
/// The reference interpreter is pure and non-committing. This adapter supplies the actor's complete current
/// observation as both evaluation and fresh commit evidence, then projects its sparse patch into a validated complete
/// replacement observation. Transition documents remain the semantic authorities and are pinned into
/// <see cref="Identity"/> through their exact coordinates and fingerprints.
///
/// The current profile supports existing single-actor aggregate state. It fails closed for emission intents and
/// Machine movements because the scenario result model does not yet retain or commit those effects atomically.
/// </remarks>
public sealed class TransitionScenarioActionInterpreter : IScenarioActionInterpreter
{
    /// <summary>Stable identity of this Transition-to-scenario interpretation profile.</summary>
    public const string ProfileIdentity = "cohesive-simulation-transitions-reference/v1";

    readonly IReadOnlyDictionary<string, ScenarioTransitionBinding> bindingsByOperation;

    /// <summary>Creates an interpreter from exact scenario-operation bindings.</summary>
    /// <param name="bindings">Non-empty operation bindings; declaration order is non-semantic.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="bindings"/> or one of its elements is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// No bindings are supplied or an operation identity is bound more than once.
    /// </exception>
    public TransitionScenarioActionInterpreter(IEnumerable<ScenarioTransitionBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var normalized = bindings.OrderBy(static binding => binding?.OperationId, StringComparer.Ordinal)
            .ToImmutableArray();
        if (normalized.IsEmpty)
            throw new ArgumentException("A Transition scenario interpreter requires at least one binding.", nameof(bindings));
        if (normalized.Any(static binding => binding is null))
            throw new ArgumentException("Transition scenario bindings cannot contain null.", nameof(bindings));

        Dictionary<string, ScenarioTransitionBinding> indexed = new(normalized.Length, StringComparer.Ordinal);
        foreach (var binding in normalized)
        {
            if (!indexed.TryAdd(binding.OperationId, binding))
            {
                throw new ArgumentException(
                    $"Scenario operation '{binding.OperationId}' is bound more than once.",
                    nameof(bindings));
            }
        }

        Bindings = normalized;
        bindingsByOperation = indexed;
        Identity = CreateIdentity(normalized);
    }

    /// <summary>Gets exact operation bindings in canonical operation-identity order.</summary>
    public ImmutableArray<ScenarioTransitionBinding> Bindings { get; }

    /// <summary>Gets a deterministic identity pinning this profile and every exact Transition binding.</summary>
    public string Identity { get; }

    /// <summary>Interprets one scenario action through its bound Transition plan.</summary>
    /// <param name="context">Canonical action, current world, operation, and input context.</param>
    /// <param name="cancellationToken">Token that cancels interpretation before the pure decision is evaluated.</param>
    /// <returns>A Transition outcome plus an explicit subject replacement when aggregate state changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The operation is unbound, contracts disagree, a target subject is absent, or an accepted decision has no
    /// authored outcome.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A decision contains emission or Machine effects, or a changed state cannot be validated without its Shape
    /// graph.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public ValueTask<ScenarioActionResult> ExecuteAsync(
        ScenarioActionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!bindingsByOperation.TryGetValue(context.Operation.Id, out var binding))
        {
            throw new InvalidOperationException(
                $"Scenario operation '{context.Operation.Id}' has no Transition binding.");
        }

        ValidateContracts(context.Operation, binding);
        var subject = ResolveSubject(context, binding.Subject);
        var plan = binding.Transition;
        if (plan.Definition.Observation.Shape is { } shape && shape != subject.Observation.ShapeId)
        {
            throw new InvalidOperationException(
                $"Scenario actor '{subject.Actor.Id}' observation shape '{subject.Observation.ShapeId}' does not "
                + $"match Transition observation shape '{shape}'.");
        }

        var state = PortableValue.Concrete(plan.Definition.Observation, subject.Observation.Value);
        var decision = TransitionReferenceInterpreter.DecideFullState(
            plan,
            CreateActivationId(context),
            context.Input,
            state,
            commitState: state);
        RejectUnsupportedEffects(decision);

        if (decision.Kind is TransitionDecisionKind.Conflict
            or TransitionDecisionKind.InvalidDefinition
            or TransitionDecisionKind.InfrastructureFailure
            or TransitionDecisionKind.Unspecified)
        {
            return ValueTask.FromResult(ScenarioActionResult.Unchanged(Failure(context, decision)));
        }

        var output = decision.Outcome
            ?? throw new InvalidOperationException(
                $"Transition '{plan.DefinitionReference.DefinitionId}' decision '{decision.Kind}' has no outcome.");
        if (decision.Kind != TransitionDecisionKind.Applied || decision.Patch.IsEmpty)
            return ValueTask.FromResult(ScenarioActionResult.Unchanged(output));

        var graph = plan.ShapeGraph
            ?? throw new NotSupportedException(
                $"Transition '{plan.DefinitionReference.DefinitionId}' changed aggregate state but its compiled plan "
                + "does not retain the Shape graph required to validate a complete replacement observation.");
        var candidate = TransitionStateProjector.Apply(subject.Observation.Value, decision);
        if (candidate.Equals(subject.Observation.Value))
            return ValueTask.FromResult(ScenarioActionResult.Unchanged(output));

        var after = Observation.Create(graph, subject.Observation.ShapeId, candidate);
        return ValueTask.FromResult(new ScenarioActionResult(
            output,
            [ScenarioActorStateChange.Replace(subject, after)]));
    }

    static void ValidateContracts(
        ScenarioOperationDefinition operation,
        ScenarioTransitionBinding binding)
    {
        var definition = binding.Transition.Definition;
        if (operation.Input != definition.Input || operation.Output != definition.Outcome)
        {
            throw new InvalidOperationException(
                $"Scenario operation '{operation.Id}' contracts do not exactly match bound Transition "
                + $"'{binding.Transition.DefinitionReference.DefinitionId}'.");
        }
    }

    static ScenarioActorSnapshot ResolveSubject(
        ScenarioActionContext context,
        ScenarioTransitionSubject subject) => subject switch
    {
        ScenarioTransitionSubject.Actor => context.ActorSnapshot,
        ScenarioTransitionSubject.TargetActor => context.TargetActorSnapshot
            ?? throw new InvalidOperationException(
                $"Action '{context.Action.Id}' must select a target actor for its Transition binding."),
        _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, "Unsupported scenario Transition subject.")
    };

    static ActivationId CreateActivationId(ScenarioActionContext context) => new(
        $"cohesive-simulation-scenario-activation/v1/{context.Scenario.Fingerprint.Value}/"
        + Uri.EscapeDataString(context.Action.Id));

    static void RejectUnsupportedEffects(TransitionDecision decision)
    {
        if (!decision.Emissions.IsEmpty || !decision.MachineMovements.IsEmpty)
        {
            throw new NotSupportedException(
                "The current Transition scenario interpreter cannot retain or atomically commit Transition emission "
                + "intents or Machine movements.");
        }
    }

    static PortableValue Failure(ScenarioActionContext context, TransitionDecision decision)
    {
        var diagnostic = decision.Diagnostics.FirstOrDefault(
            static candidate => candidate.Severity == DiagnosticSeverity.Error)
            ?? new(
                Code: TransitionScenarioDiagnosticCodes.DecisionFailed,
                Severity: DiagnosticSeverity.Error,
                Message: $"Transition decision ended as '{decision.Kind}' without an authored outcome.",
                Location: $"/actions/{context.SequenceIndex}",
                Evidence: new(stage: "scenario-transition", subject: context.Action.Id));
        return PortableValue.Failed(context.Operation.Output, diagnostic);
    }

    static string CreateIdentity(ImmutableArray<ScenarioTransitionBinding> bindings)
    {
        StringBuilder content = new();
        Append(content, ProfileIdentity);
        foreach (var binding in bindings)
        {
            var reference = binding.Transition.DefinitionReference;
            Append(content, binding.OperationId);
            Append(content, ((int)binding.Subject).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(content, reference.DefinitionId.Value);
            Append(content, reference.RevisionId.Value);
            Append(content, reference.Fingerprint.Algorithm);
            Append(content, reference.Fingerprint.Canonicalization);
            Append(content, reference.Fingerprint.Value);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString()));
        return $"{ProfileIdentity}/sha256/{Convert.ToHexStringLower(digest)}";
    }

    static void Append(StringBuilder destination, string value) =>
        destination.Append(value.Length).Append(':').Append(value);
}
