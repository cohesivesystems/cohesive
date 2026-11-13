using System.Collections.Immutable;
using Cohesive.Assurance;
using Cohesive.Model;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Scenarios;

namespace Cohesive.Simulation.Assurance;

/// <summary>Explicit single-actor reference-realization binding; not evidence of storage isolation.</summary>
/// <param name="ObservationContract">Exact accepted mapping contract referenced by the claim.</param>
/// <param name="AtomicScope">Exact assumption declaring sequential actor replacements atomic and the finite schedule closed.</param>
/// <param name="Target">Exact candidate/model identity, including the implementation revision.</param>
/// <param name="Interpreter">Exact interpreter identity required by this binding.</param>
/// <param name="ActorId">One retained scenario actor; never an inferred population.</param>
/// <param name="OperationId">Operation selecting transition obligations; null for state invariants.</param>
public sealed record ScenarioAssuranceBinding(ClaimReference ObservationContract, ClaimReference AtomicScope,
    ClaimReference Target, string Interpreter, string ActorId, string? OperationId = null);

/// <summary>How a scenario trace was obtained for an Assurance evaluation.</summary>
public enum ScenarioAssuranceMethod
{
    /// <summary>Only retained observations were evaluated; no candidate operation was invoked.</summary>
    TraceReevaluation,
    /// <summary>Candidate operations ran through ScenarioRunner and produced fresh observations.</summary>
    CandidateExecution
}

/// <summary>Existing Simulation and Assurance artifacts retained together without introducing a second trace model.</summary>
public sealed class ScenarioAssuranceResult
{
    internal ScenarioAssuranceResult(ScenarioAssuranceMethod method, ScenarioAssuranceBinding binding,
        ScenarioExecutionTraceDocument trace, TraceEvaluation evaluation)
    {
        Method = method;
        Binding = binding;
        Trace = trace;
        Evaluation = evaluation;
    }

    /// <summary>Whether this attempt executed candidate operations or only evaluated retained observations.</summary>
    public ScenarioAssuranceMethod Method { get; }
    /// <summary>Exact mapping, scope and candidate attribution.</summary>
    public ScenarioAssuranceBinding Binding { get; }
    /// <summary>Complete Simulation trace retaining source action identities.</summary>
    public ScenarioExecutionTraceDocument Trace { get; }
    /// <summary>Derived evaluation with exact projected inputs, witnesses, gaps and claim source maps.</summary>
    public TraceEvaluation Evaluation { get; }

    /// <summary>Projects a supported verdict to Simulation's existing property-case protocol.</summary>
    /// <returns>Failure for a supported violation, pass for non-vacuous satisfaction, otherwise discard.</returns>
    /// <remarks>Discard counts against Simulation's discard budget; uncertainty never becomes a successful case.</remarks>
    public PropertyCaseEvaluation ToPropertyEvaluation() => Evaluation.Status != TraceEvaluationStatus.Completed
        || Evaluation.Verdict == ClaimVerdict.Inconclusive
        || Evaluation.Alternatives.Any(x => x.Vacuous)
        ? PropertyCaseEvaluation.Discard("assurance.inconclusive-or-vacuous")
        : Evaluation.Verdict == ClaimVerdict.Violated
            ? PropertyCaseEvaluation.Fail("assurance.violation")
            : PropertyCaseEvaluation.Pass("assurance.satisfied");
}

/// <summary>Single-actor atomic-reference interpretation of existing Simulation traces.</summary>
/// <remarks>
/// Supports state invariants and transition contracts. Event correlation/closure requires a future enriched
/// trace profile and is rejected. State replacement is an atomic reference-model commit under the binding's
/// explicit scope assumption; it does not establish external storage semantics. Unchanged transition outcomes
/// remain unknown because current traces carry no authoritative no-op commit or rejection receipt.
/// </remarks>
public static class ScenarioAssurance
{
    /// <summary>Independently evaluates retained observations without running candidate code.</summary>
    /// <param name="claim">Derived accepted claim document.</param>
    /// <param name="trace">Complete retained Simulation trace.</param>
    /// <param name="binding">Explicit atomic-reference mapping and target attribution.</param>
    /// <param name="limits">Optional Assurance evaluation bounds.</param>
    /// <param name="cancellationToken">Cancellation signal.</param>
    /// <returns>Trace re-evaluation evidence, never implementation-reproduction evidence.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Binding identity, actor, operation, interpreter or observation contract mismatches.</exception>
    /// <exception cref="NotSupportedException">The claim requires event semantics unavailable in this profile.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An evaluation limit is nonpositive.</exception>
    /// <exception cref="OperationCanceledException">Evaluation is cancelled.</exception>
    public static ScenarioAssuranceResult Reevaluate(ClaimDocument claim, ScenarioExecutionTraceDocument trace,
        ScenarioAssuranceBinding binding, TraceEvaluationLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(trace);
        Validate(claim, trace.Scenario, binding);
        if (!string.Equals(trace.Interpreter, binding.Interpreter, StringComparison.Ordinal))
            throw new ArgumentException("Trace interpreter does not match the exact candidate binding.", nameof(binding));
        var definition = trace.Scenario.Definition;
        var initial = trace.InitialActors.Single(x => x.ActorId == binding.ActorId);
        var selector = claim.Definition.Selection;
        var state = initial.Observation.Value;
        List<SemanticObservation> observations = [];
        if (claim.Definition.Form == ClaimForm.StateInvariant)
            observations.Add(new("initial/" + binding.ActorId, binding.ActorId, 0, 0, selector,
                SemanticObservationKind.InitialState, Values(ClaimInputRole.State, state)));
        long ticks = 0;
        for (var index = 0; index < trace.Outcomes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = definition.Actions[index];
            var outcome = trace.Outcomes[index];
            ticks = (action.ScheduledAtUtc - definition.StartsAtUtc).Ticks;
            var change = outcome.StateChanges.FirstOrDefault(x => x.ActorId == binding.ActorId);
            var before = state;
            if (change is not null) state = change.After.Value;
            var occurrence = "action/" + action.Id + "/actor/" + binding.ActorId;
            if (claim.Definition.Form == ClaimForm.StateInvariant)
            {
                // One observation at every action boundary, including unchanged states.
                observations.Add(new(occurrence, binding.ActorId, index + 1, ticks, selector,
                    SemanticObservationKind.CommittedState, Values(ClaimInputRole.State, state)));
            }
            else if (action.OperationId == binding.OperationId
                && (change is not null || action.ActorId == binding.ActorId || action.TargetActorId == binding.ActorId))
            {
                observations.Add(new(occurrence, binding.ActorId, index + 1, ticks, selector,
                    change is null ? SemanticObservationKind.UnknownOutcome : SemanticObservationKind.Commit,
                    Values(ClaimInputRole.Before, before).Add(ClaimInputRole.Command, action.Input).Add(ClaimInputRole.After, state)));
            }
        }
        var fingerprint = trace.Fingerprint;
        var traceReference = new ClaimReference(SourceReference.Create("simulation-trace", Uri.EscapeDataString(definition.Id)),
            definition.Revision, FingerprintText.Format(fingerprint.Algorithm, fingerprint.Canonicalization, fingerprint.Value));
        var batch = new TraceObservationBatch(new(binding.ObservationContract, binding.AtomicScope,
                [new(selector, [binding.ActorId])]), binding.Target, traceReference,
            [new(traceReference, [.. observations], [new(selector, binding.ActorId, trace.Outcomes.Length, ticks,
                ObservationClosure.Closed, traceReference)])], EnumerationCompleteness.Complete, traceReference);
        return new(ScenarioAssuranceMethod.TraceReevaluation, binding, trace,
            TraceClaimEvaluator.Evaluate(claim, batch, limits, cancellationToken));
    }

    /// <summary>Executes candidate operations against a world, then evaluates their fresh trace.</summary>
    /// <param name="claim">Derived claim, unchanged across faulty and repaired candidate runs.</param>
    /// <param name="world">Exact starting world and scheduled inputs.</param>
    /// <param name="interpreter">Versioned candidate operation interpreter.</param>
    /// <param name="binding">Explicit mapping including the interpreter's exact identity.</param>
    /// <param name="limits">Optional Assurance bounds.</param>
    /// <param name="cancellationToken">Cancellation signal forwarded to candidate operations.</param>
    /// <returns>Candidate-execution evidence; no receipt or isolation guarantee is inferred for external systems.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Mapping, actor, operation or interpreter mismatches.</exception>
    /// <exception cref="NotSupportedException">The claim is outside this profile.</exception>
    /// <exception cref="ScenarioExecutionException">The candidate returns invalid execution evidence.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An evaluation limit is nonpositive.</exception>
    /// <exception cref="OperationCanceledException">Execution or evaluation is cancelled.</exception>
    public static async Task<ScenarioAssuranceResult> ExecuteAsync(ClaimDocument claim, ScenarioWorldSnapshot world,
        IScenarioActionInterpreter interpreter, ScenarioAssuranceBinding binding,
        TraceEvaluationLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(interpreter);
        Validate(claim, world.Scenario, binding);
        if (interpreter.Identity != binding.Interpreter)
            throw new ArgumentException("Candidate interpreter does not match the binding.", nameof(interpreter));
        var trace = await ScenarioRunner.ExecuteAsync(world, interpreter, cancellationToken).ConfigureAwait(false);
        var evaluated = Reevaluate(claim, trace, binding, limits, cancellationToken);
        return new(ScenarioAssuranceMethod.CandidateExecution, binding, trace, evaluated.Evaluation);
    }

    /// <summary>Reruns retained controllable inputs against a supplied candidate; never injects expected after-state.</summary>
    /// <param name="claim">Unchanged claim to evaluate.</param>
    /// <param name="retained">Retained trace whose initial observations and scheduled inputs are reused.</param>
    /// <param name="interpreter">Faulty or repaired candidate to execute.</param>
    /// <param name="binding">Exact new candidate attribution and mapping.</param>
    /// <param name="limits">Optional Assurance bounds.</param>
    /// <param name="cancellationToken">Cancellation signal.</param>
    /// <returns>Fresh execution evidence; repaired outcomes may legitimately diverge from retained outcomes.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Retained initial world or binding is inconsistent.</exception>
    /// <exception cref="NotSupportedException">The claim is outside this profile.</exception>
    /// <exception cref="ScenarioExecutionException">Candidate execution evidence is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An evaluation limit is nonpositive.</exception>
    /// <exception cref="OperationCanceledException">Execution is cancelled.</exception>
    public static Task<ScenarioAssuranceResult> ReplayAsync(ClaimDocument claim, ScenarioExecutionTraceDocument retained,
        IScenarioActionInterpreter interpreter, ScenarioAssuranceBinding binding,
        TraceEvaluationLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retained);
        var definitions = retained.Scenario.Definition.Actors.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var actors = retained.InitialActors.Select(initial =>
        {
            var actor = definitions[initial.ActorId];
            return new ScenarioActorSnapshot(actor, retained.Scenario.Definition.InitialWorld.GetExemplar(actor.ExemplarId),
                initial.EntityId, initial.Observation, initial.OriginReplayToken);
        });
        return ExecuteAsync(claim, ScenarioWorldSnapshot.Create(retained.Scenario, [.. actors]), interpreter, binding, limits, cancellationToken);
    }

    static ImmutableDictionary<ClaimInputRole, ObservationValue> Values(ClaimInputRole role, ObservationValue value) =>
        ImmutableDictionary<ClaimInputRole, ObservationValue>.Empty.Add(role, value);

    static void Validate(ClaimDocument claim, ScenarioDefinitionDocument scenario, ScenarioAssuranceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(binding);
        if (claim.Definition.Form is not (ClaimForm.StateInvariant or ClaimForm.TransitionContract))
            throw new NotSupportedException("This atomic actor profile supports state and transition claims; event history and deadline mapping require enriched trace contracts.");
        if (binding.ObservationContract != claim.Definition.Observation || binding.AtomicScope is null || binding.Target is null
            || string.IsNullOrWhiteSpace(binding.Interpreter) || !scenario.Definition.Actors.Any(x => x.Id == binding.ActorId))
            throw new ArgumentException("An exact observation contract, scope, target, interpreter and existing actor are required.", nameof(binding));
        if (claim.Definition.Form == ClaimForm.TransitionContract
            ? !scenario.Definition.Operations.Any(x => x.Id == binding.OperationId)
            : binding.OperationId is not null)
            throw new ArgumentException("Transition claims require an existing operation; invariant bindings must not filter operations.", nameof(binding));
    }
}
