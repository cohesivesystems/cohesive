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

/// <summary>The first admission guard outside the supported generation profile.</summary>
/// <param name="Node">Guard identity in the probe's exact model revision.</param>
/// <param name="Detail">Reason this guard cannot be inspected.</param>
public sealed record TransitionGenerationDiagnostic(ExecutionNodeId Node, string Detail)
{
    /// <summary>Stable diagnostic code for reporting.</summary>
    public string Code => "assurance.inputs.unsupported";
}

/// <summary>Structured unsupported-generation diagnostic; no partial candidate set is returned.</summary>
public sealed class TransitionInputGenerationException : NotSupportedException
{
    /// <summary>Creates a diagnostic attributed to an admission node.</summary>
    /// <param name="node">Unsupported node identity within the exact source model.</param>
    /// <param name="detail">Unsupported construct and actionable profile limitation.</param>
    public TransitionInputGenerationException(ExecutionNodeId node, string detail)
        : base($"Boundary generation at '{node.Value}': {detail}. No complete case set was generated.")
    {
        Diagnostic = new(node, detail);
    }
    /// <summary>Structured diagnostic shared with inspection tools.</summary>
    public TransitionGenerationDiagnostic Diagnostic { get; }
    /// <summary>Stable diagnostic code for language-independent reporting.</summary>
    public string Code => Diagnostic.Code;
    /// <summary>Admission node that could not be inspected.</summary>
    public ExecutionNodeId Node => Diagnostic.Node;
}

public static partial class TransitionAssurance
{
    /// <summary>Compiles once for typed, offline probing of an existing entity transition.</summary>
    /// <param name="transition">Canonical production transition to reuse without rewriting its rules.</param>
    /// <returns>A reusable, non-committing typed runner and boundary-input proposer.</returns>
    /// <exception cref="ArgumentNullException">Transition is null.</exception>
    /// <exception cref="ArgumentException">Compilation fails; the message includes its diagnostics.</exception>
    /// <exception cref="NotSupportedException">The transition creates a subject.</exception>
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
public sealed partial class TransitionProbe<TEntity, TInput, TOutcome> where TEntity : notnull
{
    readonly OfflineTransition<TEntity, TInput, TOutcome> execution;
    CompiledTransitionPlan plan => execution.Plan;
    readonly Lazy<GuardInspection> generationGuards;
    internal TransitionProbe(OfflineTransition<TEntity, TInput, TOutcome> execution)
    {
        this.execution = execution;
        // Execution supports more expressions than bounded generation. Inspect on first generation.
        generationGuards = new(InspectGuards);
    }

    /// <summary>Inspect the prepared model for bounded generation support before supplying any cases.</summary>
    /// <exception cref="TransitionInputGenerationException">A guard falls outside the generation profile.</exception>
    public void EnsureGenerationSupported() => _ = GenerationGuards;

    /// <summary>Inspect once and return the first unsupported guard, or null when all guards are supported.</summary>
    public TransitionGenerationDiagnostic? GenerationDiagnostic => generationGuards.Value.Diagnostic;

    ImmutableArray<Guard> GenerationGuards
    {
        get
        {
            var inspection = generationGuards.Value;
            if (inspection.Diagnostic is { } diagnostic)
                throw new TransitionInputGenerationException(diagnostic.Node, diagnostic.Detail);
            return inspection.Guards;
        }
    }

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
    /// <exception cref="TransitionInputGenerationException">Any guard, binding, operand or input member is outside this profile.</exception>
    /// <exception cref="InvalidOperationException">The case budget is exceeded or typed conversion fails.</exception>
    public TransitionInputCases<TInput> GenerateInputs(TEntity before, TInput seed, int maxInputs = 1024, int minimum = int.MinValue, int maximum = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputs);
        return GenerateInputs(before, seed, minimum, maximum, new CaseBudget(maxInputs, maxInputs));
    }

    readonly record struct CaseBudget(int Total, int Remaining)
    {
        public void RequireRoom(int used)
        {
            if (used >= Remaining) throw new InvalidOperationException($"Case budget {Total} exceeded.");
        }
    }

    TransitionInputCases<TInput> GenerateInputs(TEntity before, TInput seed, int minimum, int maximum, CaseBudget budget)
    {
        budget.RequireRoom(0);
        if (minimum > maximum) throw new ArgumentOutOfRangeException(nameof(minimum));
        var state = ObservationValue.FromObject(before);
        var template = ObservationValue.FromObject(seed);
        var proposals = new List<ObservationValue> { template };
        var sources = new Dictionary<ObservationValue, List<ExecutionNodeId>> { [template] = [] };
        void Add(ObservationValue candidate, ExecutionNodeId node)
        {
            if (!sources.TryGetValue(candidate, out var motivations))
            {
                budget.RequireRoom(proposals.Count);
                proposals.Add(candidate);
                sources.Add(candidate, motivations = []);
            }
            if (!motivations.Contains(node)) motivations.Add(node);
        }
        var nodes = ImmutableArray.CreateBuilder<ExecutionNodeId>();
        foreach (var rule in GenerationGuards)
        {
            if (!nodes.Contains(rule.Id)) nodes.Add(rule.Id);
            if (rule.Input is not { } path) continue;
            if (!template.TryGetField(path, out var original))
                throw new ArgumentException($"Missing command field {path}.", nameof(seed));
            if (rule.Boolean)
            {
                if (!original.TryGetBoolean(out _)) throw new ArgumentException($"Invalid Boolean command field {path}.", nameof(seed));
                foreach (var value in new[] { false, true })
                    Add(template.WithField(path, ObservationValue.FromBool(value)), rule.Id);
                continue;
            }
            if (!original.TryGetInt32(out var seedValue) || seedValue < minimum || seedValue > maximum)
                throw new ArgumentOutOfRangeException(nameof(seed), "Invalid Int32 seed or outside supplied bounds.");
            var number = rule.Constant ?? ReadInteger(state, rule.Fields.Single());
            // Use Int64 for neighbor arithmetic, then clip to the declared Int32 profile.
            long[] values = [minimum, -1, 0, 1, maximum, (long)number - 1, number, (long)number + 1];
            foreach (var value in values)
            {
                if (value < minimum || value > maximum) continue;
                var candidate = template.WithField(path, ObservationValue.FromInt64(value));
                Add(candidate, rule.Id);
            }
        }
        return new(Reference, [.. proposals.Select(p => new TransitionInputCandidate<TInput>(
            p.Deserialize<TInput>() ?? throw new InvalidOperationException("No typed generated input."), [.. sources[p]]))], nodes.ToImmutable(), minimum, maximum, budget.Remaining);
    }

    sealed record Guard(ExecutionNodeId Id, bool Boolean, FieldPath? Input, FieldPath[] Fields, int? Constant);

    sealed record GuardInspection(ImmutableArray<Guard> Guards, TransitionGenerationDiagnostic? Diagnostic = null);

    GuardInspection InspectGuards()
    {
        try
        {
            var guards = plan.Definition.Preconditions.Select(r => Resolve(r.Id, r.Predicate)).ToList();
            if (plan.Definition.MachineBinding is { } machine)
            {
                FieldPath? discriminator = null;
                foreach (var (name, state) in machine.States)
                {
                    if (state.Assignments is not [var assignment] ||
                        assignment.Value.Value is not { } value ||
                        state.Predicate != Expr.Eq(Expr.Field(TransitionBindingIds.Observation, assignment.Path), Expr.Const(value)) ||
                        discriminator is { } other && assignment.Path != other)
                        throw Unsupported(plan.Definition.Body.Id, $"State '{name}' needs a single mapped discriminator field.");
                    discriminator = assignment.Path;
                }
                foreach (var edge in plan.MachineEdges)
                    foreach (var predicate in edge.AdmissionGuards)
                    {
                        if (predicate is ConstantExpr { Value.Kind: ObservationValueKind.Bool }
                            or LiteralExpr { Value.Kind: ObservationValueKind.Bool }) continue;
                        guards.Add(Resolve(edge.Edge, predicate));
                    }
            }
            return new([.. guards]);
        }
        catch (TransitionInputGenerationException error)
        {
            return new([], error.Diagnostic);
        }
    }

    Guard Resolve(ExecutionNodeId id, Expr predicate)
    {
        List<FieldPath> fields = [];
        FieldPath? input = null;
        int? constant = null;
        void Operand(Expr expression, ScalarTypeKind kind)
        {
            FieldPath path;
            TypeRef contract;
            switch (expression)
            {
                case ParameterExpr parameter when input is null:
                    path = FieldPath.FromField(parameter.Parameter);
                    input = path;
                    contract = plan.Definition.Input.Type!;
                    break;
                case FieldExpr field when field.Binding == TransitionBindingIds.Input && input is null:
                    path = field.Path;
                    input = path;
                    contract = plan.Definition.Input.Type!;
                    break;
                case FieldExpr field when IsState(field):
                    path = field.Path;
                    fields.Add(path);
                    contract = plan.Definition.Observation.Type!;
                    break;
                case LiteralExpr typed when kind == ScalarTypeKind.Int32 && typed.Value.TryGetInt32(out var threshold):
                    constant = threshold;
                    return;
                case ConstantExpr literal when kind == ScalarTypeKind.Int32 && literal.Value.TryGetInt32(out var number):
                    constant = number;
                    return;
                default:
                    throw Unsupported(id, "expected a declared member or Int32 threshold");
            }
            foreach (var segment in path.Segments)
            {
                var member = (contract as ObjectTypeRef)?.Fields.FirstOrDefault(f => FieldPathSegment.ForField(f.Name) == segment);
                if (member is null || member.Cardinality != FieldCardinality.Single || member.Nullability != FieldNullability.NonNullable)
                    throw Unsupported(id, $"expected a nonnullable scalar member at {path}");
                contract = member.Type;
            }
            if (contract is not ScalarTypeRef scalar || scalar.Kind != kind)
                throw Unsupported(id, $"expected {kind} member at {path}");
        }
        if (predicate is BinaryExpr binary)
        {
            if (binary.Operator is not (BinaryOperator.Eq or BinaryOperator.Ne or BinaryOperator.Gt or BinaryOperator.Ge or BinaryOperator.Lt or BinaryOperator.Le))
                throw Unsupported(id, "expected an Int32 comparison");
            Operand(binary.Left, ScalarTypeKind.Int32);
            Operand(binary.Right, ScalarTypeKind.Int32);
            if (input is null && fields.Count == 0) throw Unsupported(id, "comparison has no variable operand");
            return new(id, false, input, [.. fields], constant);
        }
        Operand(predicate is UnaryExpr { Operator: UnaryOperator.Not } negation ? negation.Operand : predicate, ScalarTypeKind.Bool);
        return new(id, true, input, [.. fields], null);
    }

    static int ReadInteger(ObservationValue state, FieldPath path) =>
        state.TryGetField(path, out var value) && value.TryGetInt32(out var number)
            ? number : throw new ArgumentException($"Missing or invalid Int32 state field {path}.");

    static bool IsState(FieldExpr field) => field.Binding is null || field.Binding == TransitionBindingIds.Observation;
    static TransitionInputGenerationException Unsupported(ExecutionNodeId node, string detail) => new(node, detail);
}
