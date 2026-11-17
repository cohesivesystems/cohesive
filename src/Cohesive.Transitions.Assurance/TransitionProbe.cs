using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Assurance;

/// <summary>A proposed input with every admission node that motivated it; never an expected answer.</summary>
/// <param name="Input">Typed candidate command.</param>
/// <param name="MotivatingNodes">Nodes contributing this candidate; empty for a seed with no contributing guard.</param>
public sealed record TransitionInputCandidate<TInput>(TInput Input, ImmutableArray<ExecutionNodeId> MotivatingNodes);

/// <summary>Finite proposals and explicit inspection scope tied to one exact model.</summary>
/// <param name="Reference">Exact definition, revision and fingerprint.</param>
/// <param name="Candidates">Deterministically deduplicated inputs with per-input attribution.</param>
/// <param name="InspectedNodes">All inspected guards, not a claim of behavioral coverage.</param>
/// <param name="Minimum">Inclusive configured Int32 lower bound.</param>
/// <param name="Maximum">Inclusive configured Int32 upper bound.</param>
/// <param name="MaxInputs">Configured candidate budget.</param>
public sealed record TransitionInputCases<TInput>(ExecutionDefinitionReference Reference,
    ImmutableArray<TransitionInputCandidate<TInput>> Candidates, ImmutableArray<ExecutionNodeId> InspectedNodes,
    int Minimum, int Maximum, int MaxInputs)
{
    /// <summary>Typed inputs in stable candidate order.</summary>
    public IEnumerable<TInput> Inputs => Candidates.Select(candidate => candidate.Input);
    /// <summary>Versioned interpretation profile, independent of host-language API spelling.</summary>
    public string Profile => "cohesive.transition-boundaries/int32-bool/v1";
    /// <summary>Explicit residuals; successful inspection is not proof or exhaustive search.</summary>
    public ImmutableArray<string> CoverageLimits => ["caller-supplied states", "one input field at a time", "admission guards only", "no expected outcomes or completeness proof"];
}

/// <summary>Structured unsupported-generation diagnostic; no partial candidate set is returned.</summary>
public sealed class TransitionInputGenerationException : NotSupportedException
{
    /// <summary>Creates a diagnostic attributed to an admission node.</summary>
    /// <param name="node">Unsupported node identity within the exact source model.</param>
    /// <param name="detail">Unsupported construct and actionable profile limitation.</param>
    public TransitionInputGenerationException(ExecutionNodeId node, string detail)
        : base($"Boundary generation at '{node.Value}': {detail}. No complete case set was generated.") => Node = node;
    /// <summary>Stable diagnostic code for language-independent reporting.</summary>
    public string Code => "assurance.inputs.unsupported";
    /// <summary>Admission node that could not be inspected.</summary>
    public ExecutionNodeId Node { get; }
}

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
        return new(OfflineTransition.Prepare(transition));
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
    readonly OfflineTransition<TEntity, TInput, TOutcome> execution;
    CompiledTransitionPlan plan => execution.Plan;
    internal TransitionProbe(OfflineTransition<TEntity, TInput, TOutcome> execution) => this.execution = execution;

    /// <summary>Exact source authority used for both generation and execution.</summary>
    public ExecutionDefinitionReference Reference => plan.DefinitionReference;

    /// <summary>Delegates to the public offline execution API without changing its semantics.</summary>
    /// <param name="before">Complete pre-state.</param>
    /// <param name="input">Typed command.</param>
    /// <param name="invocation">Stable invocation identity.</param>
    /// <returns>Typed candidate state, outcome and full decision evidence.</returns>
    /// <exception cref="InvalidOperationException">Execution cannot produce a typed domain decision.</exception>
    /// <exception cref="NotSupportedException">Typed conversion is unsupported.</exception>
    public OfflineTransitionResult<TEntity, TOutcome> Execute(TEntity before, TInput input, ActivationId invocation) =>
        execution.Execute(before, input, invocation);

    /// <summary>Proposes seed-preserving Int32 neighbors and extremes from every supported admission guard.</summary>
    /// <param name="before">Supplied pre-state used to resolve field-dependent thresholds.</param>
    /// <param name="seed">Input template; fields not being varied retain this value.</param>
    /// <param name="maxInputs">Positive bound on distinct proposals, including the seed; overflow fails without partial success.</param>
    /// <param name="minimum">Inclusive lower bound for generated Int32 members.</param>
    /// <param name="maximum">Inclusive upper bound for generated Int32 members.</param>
    /// <returns>Seed plus unique candidates with exact model and admission-node provenance; never expected answers.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The budget/bounds are invalid or an inspected seed member lies outside them.</exception>
    /// <exception cref="NotSupportedException">Any guard, binding, operand or input member is outside this profile.</exception>
    /// <exception cref="InvalidOperationException">The case budget is exceeded or typed conversion fails.</exception>
    public TransitionInputCases<TInput> GenerateInputs(TEntity before, TInput seed, int maxInputs = 1024, int minimum = int.MinValue, int maximum = int.MaxValue)
    {
        if (maxInputs < 1) throw new ArgumentOutOfRangeException(nameof(maxInputs));
        if (minimum > maximum) throw new ArgumentOutOfRangeException(nameof(minimum));
        var state = ObservationValue.FromObject(before);
        var template = ObservationValue.FromObject(seed);
        var proposals = new List<ObservationValue> { template };
        var sources = new Dictionary<ObservationValue, List<ExecutionNodeId>> { [template] = [] };
        void Add(ObservationValue candidate, ExecutionNodeId node)
        {
            if (!sources.TryGetValue(candidate, out var motivations))
            {
                if (proposals.Count == maxInputs) throw new InvalidOperationException($"Boundary input budget {maxInputs} exceeded at '{node.Value}'.");
                proposals.Add(candidate);
                sources.Add(candidate, motivations = []);
            }
            if (!motivations.Contains(node)) motivations.Add(node);
        }
        var nodes = ImmutableArray.CreateBuilder<ExecutionNodeId>();
        foreach (var rule in plan.Definition.Preconditions)
        {
            // No short-circuit: even an unreachable or currently false guard must be supported.
            var expression = rule.Predicate;
            var booleanExpression = expression is UnaryExpr { Operator: UnaryOperator.Not } negation ? negation.Operand : expression;
            if (booleanExpression is ParameterExpr booleanInput && IsInput(booleanInput.Parameter, ScalarTypeKind.Bool)
                && template.TryGetField(FieldPath.FromField(booleanInput.Parameter), out var inputFlag) && inputFlag.TryGetBoolean(out _))
            {
                foreach (var value in new[] { false, true }) Add(template.WithField(FieldPath.FromField(booleanInput.Parameter), ObservationValue.FromBool(value)), rule.Id);
                nodes.Add(rule.Id);
                continue;
            }
            if (booleanExpression is FieldExpr boolean && IsState(boolean) && state.TryGetField(boolean.Path, out var flag) && flag.TryGetBoolean(out _))
            {
                nodes.Add(rule.Id);
                continue;
            }
            if (expression is not BinaryExpr binary || binary.Operator is not
                (BinaryOperator.Eq or BinaryOperator.Ne or BinaryOperator.Gt or BinaryOperator.Ge or BinaryOperator.Lt or BinaryOperator.Le))
                throw Unsupported(rule.Id, "expected an Int32 comparison or direct/negated boolean");
            var (left, right) = (binary.Left, binary.Right);
            if (left is not ParameterExpr) (left, right) = (right, left);
            if (left is not ParameterExpr parameter)
                throw Unsupported(rule.Id, "expected one direct input parameter");
            if (!IsInput(parameter.Parameter, ScalarTypeKind.Int32))
                throw Unsupported(rule.Id, "input contract must declare a nonnullable Int32 member");
            var path = FieldPath.FromField(parameter.Parameter);
            if (!template.TryGetField(path, out var original) || !original.TryGetInt32(out var seedValue))
                throw Unsupported(rule.Id, "input member must be a present Int32");
            if (seedValue < minimum || seedValue > maximum)
                throw new ArgumentOutOfRangeException(nameof(seed), "Seed member is outside the supplied Int32 bounds.");
            ObservationValue boundary;
            if (right is ConstantExpr constant) boundary = constant.Value;
            else if (right is FieldExpr field && IsState(field) && state.TryGetField(field.Path, out var observed)) boundary = observed;
            else throw Unsupported(rule.Id, "threshold must be a constant or supplied state field");
            if (!boundary.TryGetInt32(out var number)) throw Unsupported(rule.Id, "threshold must be Int32");
            // Use Int64 for neighbor arithmetic, then clip to the declared Int32 profile.
            long[] values = [minimum, -1, 0, 1, maximum, (long)number - 1, number, (long)number + 1];
            foreach (var value in values)
            {
                if (value < minimum || value > maximum) continue;
                var candidate = template.WithField(path, ObservationValue.FromInt64(value));
                Add(candidate, rule.Id);
            }
            nodes.Add(rule.Id);
        }
        return new(Reference, [.. proposals.Select(p => new TransitionInputCandidate<TInput>(
            p.Deserialize<TInput>() ?? throw new InvalidOperationException("No typed generated input."), [.. sources[p]]))], nodes.ToImmutable(), minimum, maximum, maxInputs);
    }

    bool IsInput(string parameter, ScalarTypeKind kind) => plan.Definition.Input.Type is ObjectTypeRef input
        && input.Fields.Any(f => f.Name == parameter && f.Type is ScalarTypeRef scalar && scalar.Kind == kind
            && f.Cardinality == FieldCardinality.Single && f.Nullability == FieldNullability.NonNullable);

    static bool IsState(FieldExpr field) => field.Binding is null || field.Binding == TransitionBindingIds.Observation;
    static TransitionInputGenerationException Unsupported(ExecutionNodeId node, string detail) => new(node, detail);
}
