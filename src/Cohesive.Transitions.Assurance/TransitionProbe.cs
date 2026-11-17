using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Assurance;

/// <summary>Typed observations from one non-committing reference execution; not proof or commit evidence.</summary>
/// <param name="State">Projected candidate state, or preserved state on rejection.</param>
/// <param name="Outcome">Authored typed outcome.</param>
/// <param name="Decision">Complete interpreter decision, including diagnostics, traces and emissions.</param>
public sealed record TransitionProbeResult<TEntity, TOutcome>(TEntity State, TOutcome Outcome, TransitionDecision Decision);

/// <summary>Bounded input proposals tied to an exact transition; contains no expected outcomes.</summary>
/// <param name="Reference">Exact source definition, revision and fingerprint.</param>
/// <param name="Inputs">Distinct typed proposals in deterministic discovery order.</param>
/// <param name="InspectedNodes">Every inspected admission node, including state-only boolean guards.</param>
public sealed record TransitionInputCases<TInput>(ExecutionDefinitionReference Reference,
    ImmutableArray<TInput> Inputs, ImmutableArray<ExecutionNodeId> InspectedNodes);

public static partial class TransitionAssurance
{
    /// <summary>Compiles once for typed, offline probing of an existing entity transition.</summary>
    /// <param name="transition">Canonical production transition to reuse without rewriting its rules.</param>
    /// <returns>A reusable, non-committing typed runner and boundary-input proposer.</returns>
    /// <exception cref="ArgumentNullException">Transition is null.</exception>
    /// <exception cref="ArgumentException">Compilation fails; the message includes its diagnostics.</exception>
    /// <exception cref="NotSupportedException">The transition creates a subject or requires machine bindings.</exception>
    public static TransitionProbe<TEntity, TInput, TOutcome> Prepare<TEntity, TInput, TOutcome>(
        Transition<TEntity, TInput, TOutcome> transition) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (transition.Definition.SubjectCreation is not null || transition.Definition.MachineBinding is not null)
            throw new NotSupportedException("Offline probes require an existing entity without machine bindings.");
        var compiled = transition.Compile();
        if (!compiled.IsSuccessful)
            throw new ArgumentException(string.Join("; ", compiled.Validation.Diagnostics), nameof(transition));
        return new(compiled.Plan!);
    }
}

/// <summary>Typed adapter over the canonical reference interpreter and a bounded guard-inspection profile.</summary>
/// <remarks>Uses ordinary observation conversion for test workloads. No I/O or commits. Generation varies one
/// direct Int32 input member at a time around constants or supplied state fields; it does not solve predicates,
/// generate states, explore combinations, or establish coverage of all behaviors. Unsupported guards fail closed.</remarks>
/// <typeparam name="TEntity">Complete existing entity record.</typeparam>
/// <typeparam name="TInput">Typed input record.</typeparam>
/// <typeparam name="TOutcome">Typed authored outcome.</typeparam>
public sealed class TransitionProbe<TEntity, TInput, TOutcome> where TEntity : notnull
{
    readonly CompiledTransitionPlan plan;
    internal TransitionProbe(CompiledTransitionPlan plan) => this.plan = plan;

    /// <summary>Exact source authority used for both generation and execution.</summary>
    public ExecutionDefinitionReference Reference => plan.DefinitionReference;

    /// <summary>Executes actual production IR and returns typed state/outcome plus complete raw evidence.</summary>
    /// <param name="before">Complete pre-state, never mutated.</param>
    /// <param name="input">Typed command.</param>
    /// <param name="invocation">Caller-owned stable identity for this test invocation.</param>
    /// <returns>Applied, NoChange or rejected decision with projected state and typed outcome.</returns>
    /// <exception cref="InvalidOperationException">Execution fails outside domain semantics or has no typed outcome.</exception>
    /// <exception cref="NotSupportedException">A value cannot be converted to the typed observation contract.</exception>
    public TransitionProbeResult<TEntity, TOutcome> Execute(TEntity before, TInput input, ActivationId invocation)
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

    /// <summary>Proposes seed-preserving Int32 neighbors and extremes from every supported admission guard.</summary>
    /// <param name="before">Supplied pre-state used to resolve field-dependent thresholds.</param>
    /// <param name="seed">Input template; fields not being varied retain this value.</param>
    /// <param name="maxInputs">Positive bound on distinct proposals, including the seed; overflow fails without partial success.</param>
    /// <returns>Seed plus unique candidates with exact model and admission-node provenance; never expected answers.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The bound is not positive.</exception>
    /// <exception cref="NotSupportedException">Any guard, binding, operand or input member is outside this profile.</exception>
    /// <exception cref="InvalidOperationException">The case budget is exceeded or typed conversion fails.</exception>
    public TransitionInputCases<TInput> GenerateInputs(TEntity before, TInput seed, int maxInputs = 1024)
    {
        if (maxInputs < 1) throw new ArgumentOutOfRangeException(nameof(maxInputs));
        var state = ObservationValue.FromObject(before);
        var template = ObservationValue.FromObject(seed);
        var proposals = new List<ObservationValue> { template };
        var unique = new HashSet<ObservationValue> { template };
        var nodes = ImmutableArray.CreateBuilder<ExecutionNodeId>();
        foreach (var rule in plan.Definition.Preconditions)
        {
            // No short-circuit: even an unreachable or currently false guard must be supported.
            var expression = rule.Predicate;
            if (expression is UnaryExpr { Operator: UnaryOperator.Not, Operand: FieldExpr boolean }
                && IsState(boolean) && state.TryGetField(boolean.Path, out var flag) && flag.TryGetBoolean(out _))
            {
                nodes.Add(rule.Id);
                continue;
            }
            if (expression is not BinaryExpr binary || binary.Operator is not
                (BinaryOperator.Eq or BinaryOperator.Ne or BinaryOperator.Gt or BinaryOperator.Ge or BinaryOperator.Lt or BinaryOperator.Le))
                throw Unsupported(rule.Id, "expected an Int32 comparison or negated state boolean");
            var (left, right) = (binary.Left, binary.Right);
            if (left is not ParameterExpr) (left, right) = (right, left);
            if (left is not ParameterExpr parameter)
                throw Unsupported(rule.Id, "expected one direct input parameter");
            if (plan.Definition.Input.Type is not ObjectTypeRef inputType
                || !inputType.Fields.Any(f => f.Name == parameter.Parameter
                    && f.Type is ScalarTypeRef { Kind: ScalarTypeKind.Int32 }
                    && f.Cardinality == FieldCardinality.Single && f.Nullability == FieldNullability.NonNullable))
                throw Unsupported(rule.Id, "input contract must declare a nonnullable Int32 member");
            var path = FieldPath.FromField(parameter.Parameter);
            if (!template.TryGetField(path, out var original) || !original.TryGetInt32(out _))
                throw Unsupported(rule.Id, "input member must be a present Int32");
            ObservationValue boundary;
            if (right is ConstantExpr constant) boundary = constant.Value;
            else if (right is FieldExpr field && IsState(field) && state.TryGetField(field.Path, out var observed)) boundary = observed;
            else throw Unsupported(rule.Id, "threshold must be a constant or supplied state field");
            if (!boundary.TryGetInt32(out var number)) throw Unsupported(rule.Id, "threshold must be Int32");
            // Use Int64 for neighbor arithmetic, then clip to the declared Int32 profile.
            long[] values = [int.MinValue, -1, 0, 1, int.MaxValue, (long)number - 1, number, (long)number + 1];
            foreach (var value in values)
            {
                if (value < int.MinValue || value > int.MaxValue) continue;
                var candidate = template.WithField(path, ObservationValue.FromInt64(value));
                if (!unique.Add(candidate)) continue;
                if (proposals.Count == maxInputs) throw new InvalidOperationException($"Boundary input budget {maxInputs} exceeded at '{rule.Id.Value}'.");
                proposals.Add(candidate);
            }
            nodes.Add(rule.Id);
        }
        return new(Reference, [.. proposals.Select(p => p.Deserialize<TInput>()
            ?? throw new InvalidOperationException("No typed generated input."))], nodes.ToImmutable());
    }

    static bool IsState(FieldExpr field) => field.Binding is null || field.Binding == TransitionBindingIds.Observation;
    static NotSupportedException Unsupported(ExecutionNodeId node, string detail) =>
        new($"Boundary generation at '{node.Value}': {detail}. No complete case set was generated.");
}
