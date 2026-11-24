using Cohesive.Prelude;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Execution;

/// <summary>Typed observations from one non-committing reference execution; not proof or commit evidence.</summary>
/// <param name="State">Projected candidate state, or preserved state on rejection.</param>
/// <param name="Outcome">Authored typed outcome.</param>
/// <param name="Decision">Complete interpreter decision, including diagnostics, traces and emissions.</param>
public sealed record OfflineTransitionResult<TEntity, TOutcome>(TEntity State, TOutcome Outcome, TransitionDecision Decision);

/// <summary>Creates non-committing typed executions without an Assurance or storage dependency.</summary>
public static class OfflineTransition
{
    /// <summary>Executes a parameterless command with the caller's invocation identity.</summary>
    public static OfflineTransitionResult<TEntity, TOutcome> Execute<TEntity, TOutcome>(
        this OfflineTransition<TEntity, Unit, TOutcome> runner, TEntity before, ActivationId invocation)
        where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.Execute(before, default, invocation);
    }

    /// <summary>Compiles one existing-entity transition for repeated offline execution.</summary>
    /// <param name="transition">Canonical model to compile.</param>
    /// <returns>A reusable typed reference execution.</returns>
    /// <exception cref="ArgumentNullException">Transition is null.</exception>
    /// <exception cref="ArgumentException">Compilation fails with diagnostics.</exception>
    /// <exception cref="NotSupportedException">The transition creates a subject instead of operating on the supplied entity.</exception>
    public static OfflineTransition<TEntity, TInput, TOutcome> Prepare<TEntity, TInput, TOutcome>(
        Transition<TEntity, TInput, TOutcome> transition) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (transition.Definition.SubjectCreation is not null)
            throw new NotSupportedException("Offline probes require an existing entity.");
        var compiled = transition.Compile();
        if (!compiled.IsSuccessful)
            throw new ArgumentException(string.Join("; ", compiled.Validation.Diagnostics), nameof(transition));
        return new(compiled.Plan!);
    }
}

/// <summary>Typed full-state reference execution; never commits state or dispatches effects.</summary>
/// <typeparam name="TEntity">Existing entity type.</typeparam>
/// <typeparam name="TInput">Command type.</typeparam>
/// <typeparam name="TOutcome">Outcome type.</typeparam>
public sealed class OfflineTransition<TEntity, TInput, TOutcome> where TEntity : notnull
{
    readonly CompiledTransitionPlan plan;
    internal OfflineTransition(CompiledTransitionPlan plan) => this.plan = plan;
    /// <summary>Compiled exact model available for other non-executing interpretations.</summary>
    public CompiledTransitionPlan Plan => plan;
    /// <summary>Exact definition/revision/fingerprint authority.</summary>
    public ExecutionDefinitionReference Reference => plan.DefinitionReference;

    /// <summary>Executes actual production IR and returns typed state/outcome plus complete raw evidence.</summary>
    /// <param name="before">Complete pre-state, never mutated.</param>
    /// <param name="input">Typed command.</param>
    /// <param name="invocation">Caller-owned stable identity for this test invocation.</param>
    /// <returns>Applied, NoChange or rejected decision with projected state and typed outcome.</returns>
    /// <exception cref="InvalidOperationException">Execution fails outside domain semantics or has no typed outcome.</exception>
    /// <exception cref="NotSupportedException">A value cannot be converted to the typed observation contract.</exception>
    public OfflineTransitionResult<TEntity, TOutcome> Execute(TEntity before, TInput input, ActivationId invocation)
    {
        var observation = ObservationValue.FromObject(before);
        var decision = TransitionReferenceInterpreter.DecideFullState(plan, invocation,
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(input)),
            PortableValue.Concrete(plan.Definition.Observation, observation));
        if (decision.Kind is not (TransitionDecisionKind.Applied or TransitionDecisionKind.NoChange
            or TransitionDecisionKind.AdmissionRejected or TransitionDecisionKind.DomainRejected))
            throw new InvalidOperationException($"{decision.Kind}: {string.Join("; ", decision.Diagnostics)}");
        var state = TransitionStateProjector.Apply(observation, decision).Deserialize<TEntity>()
            ?? throw new InvalidOperationException("No typed projected state.");
        var outcome = (decision.Outcome?.Value ?? throw new InvalidOperationException("No transition outcome.")).Deserialize<TOutcome>()
            ?? throw new InvalidOperationException("No typed transition outcome.");
        return new(state, outcome, decision);
    }

}
