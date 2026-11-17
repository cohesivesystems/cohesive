using System.Collections.Immutable;
using Cohesive.Assurance;
using Cohesive.Authoring;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Assurance;

/// <summary>Binds the actual admission IR of a transition to a finite pre-state claim check.</summary>
/// <remarks>This checks explicit admission soundness, not body paths, reachability, or storage commits.
/// The first profile requires an existing entity, no machine binding, and state-only scalar predicates.</remarks>
public static partial class TransitionAssurance
{
    /// <summary>Exact state-only admission-binding profile retained alongside the transition sources.</summary>
    public static ClaimReference Profile { get; } = new(new("assurance-binding://transition-admission"),
        "1", "cohesive.transitions.assurance.admission/v1");

    /// <summary>Derives an ordered conjunction of every admission rule; no rules means unconditional admission.</summary>
    /// <typeparam name="TEntity">Typed authoring projection of the complete entity observation.</typeparam>
    /// <typeparam name="TInput">Transition command type; admission predicates must not depend on it.</typeparam>
    /// <typeparam name="TOutcome">Transition outcome type.</typeparam>
    /// <param name="transition">Authoritatively lowered transition whose admission rules are checked.</param>
    /// <param name="claim">One-input pre-state transition claim pinned to this exact transition as subject and selector.</param>
    /// <param name="before">Exact claim binding with the transition's full observation type and value constraints.</param>
    /// <param name="members">Complete declared catalog of pre-states, defensively snapshotted.</param>
    /// <param name="source">Exact source of the catalog and optional assumptions.</param>
    /// <param name="configuration">Exact configuration identifying the binding and finite-check profile.</param>
    /// <returns>Finite-check source with derived admission predicates and node-level provenance; call Build to validate and materialize.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Transition validation fails, the claim targets another revision, or input contracts differ.</exception>
    /// <exception cref="NotSupportedException">The transition or an admission expression is outside the state-only existing-entity profile.</exception>
    public static FiniteGuardCheck.Source<TEntity> CheckAdmission<TEntity, TInput, TOutcome>(
        Transition<TEntity, TInput, TOutcome> transition, ClaimDocument claim, IExpressionInput<TEntity> before,
        IReadOnlyList<TEntity> members, ClaimReference source, ClaimReference configuration) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!transition.IsValid)
            throw new ArgumentException("Only validated transition definitions can be bound.", nameof(transition));
        var definition = transition.Definition;
        if (definition.SubjectCreation is not null || definition.MachineBinding is not null)
            throw new NotSupportedException("Admission checking requires an existing entity without lifecycle classification or creation.");
        var reference = ClaimReference.FromExecution(transition.Reference);
        var contract = claim.Definition;
        if (!contract.Attachments.Any(x => x.Role == "subject" && x.Reference == reference) || contract.Selection != reference)
            throw new ArgumentException("Claim subject and selector must reference this exact transition revision.", nameof(claim));
        if (contract.Form != ClaimForm.TransitionContract || contract.Inputs.Length != 1
            || contract.Inputs[0].Role != ClaimInputRole.Before)
            throw new NotSupportedException("Admission checking requires a single pre-state transition claim.");
        var input = contract.Inputs[0];
        var observation = definition.Observation;
        if (input.Id != before.Binding.Id || input.Contract.Type != before.Binding.Type
            || !input.Contract.Equals(observation))
            throw new ArgumentException("Claim pre-state must match the transition's complete observation contract.", nameof(before));

        var rules = definition.Preconditions;
        var predicates = new Expr[rules.Length];
        var sources = ImmutableArray.CreateBuilder<ClaimSourceLocation>(rules.Length + 2);
        sources.Add(new("/guard", reference));
        sources.Add(new("/guard", Profile));
        var path = "/guard";
        for (var i = 0; i < rules.Length; i++)
        {
            predicates[i] = Rebind(rules[i].Predicate, before.Binding.Id, rules[i].Id);
            sources.Add(new(i == rules.Length - 1 ? path : path + "/left",
                ClaimReference.FromExecution(transition.Reference, rules[i].Id)));
            path += "/right";
        }
        Expr guard = predicates.Length == 0 ? Expr.Const(true) : predicates[^1];
        for (var i = predicates.Length - 2; i >= 0; i--)
            guard = Expr.And(predicates[i], guard);
        return FiniteGuardCheck.Define(claim, before, members, source, configuration)
            .When(guard, sources.MoveToImmutable());
    }

    // A deliberately narrow binding profile, not a second expression interpreter.
    // Preserve source order and never simplify away an unsupported dependency.
    static Expr Rebind(Expr expression, ValueBindingId before, ExecutionNodeId rule)
    {
        ValueBindingId Bind(ValueBindingId? binding) => binding is null || binding == TransitionBindingIds.Observation
            ? before : throw new NotSupportedException($"Admission '{rule.Value}' reads unsupported binding '{binding}'; only pre-state observations are supported.");
        return expression switch
        {
            BindingExpr e => new BindingExpr(Bind(e.Binding)),
            FieldExpr e => new FieldExpr(e.Path, Bind(e.Binding)),
            ConstantExpr or LiteralExpr => expression,
            UnaryExpr e => e with { Operand = Rebind(e.Operand, before, rule) },
            BinaryExpr e => e with { Left = Rebind(e.Left, before, rule), Right = Rebind(e.Right, before, rule) },
            ConditionalExpr e => e with { Test = Rebind(e.Test, before, rule),
                IfTrue = Rebind(e.IfTrue, before, rule), IfFalse = Rebind(e.IfFalse, before, rule) },
            _ => throw new NotSupportedException($"Admission '{rule.Value}' uses unsupported expression '{expression.GetType().Name}'.")
        };
    }
}
