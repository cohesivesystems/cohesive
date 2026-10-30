using System.Linq.Expressions;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Authoring;

namespace Cohesive.Machines;

/// <summary>Portable guard lowered by the shared expression frontend; contains no host callback.</summary>
/// <param name="Predicate">Boolean semantic expression.</param>
/// <param name="FactsBinding">Explicit immutable facts binding.</param>
/// <param name="InputBinding">Typed action input binding.</param>
/// <param name="FactsContract">Required facts contract.</param>
/// <param name="InputContract">Required action payload contract.</param>
public sealed record MachineGuard(Expr Predicate, ValueBindingId FactsBinding, ValueBindingId InputBinding,
    ValueContract FactsContract, ValueContract InputContract)
{
    /// <summary>Lowers a typed Boolean expression without executing it.</summary>
    /// <typeparam name="TFacts">Explicit facts record type.</typeparam>
    /// <typeparam name="TInput">Action input record type.</typeparam>
    /// <param name="predicate">Pure expression over facts and input.</param>
    /// <returns>Portable predicate with structural input contracts.</returns>
    /// <exception cref="RelationQueryExpressionAuthoringException">An expression is outside the portable language.</exception>
    public static MachineGuard From<TFacts, TInput>(Expression<Func<TFacts, TInput, bool>> predicate)
        where TFacts : notnull where TInput : notnull
    {
        var author = new RelationQueryExpressionAuthoring();
        var facts = author.Source<TFacts>("machine/facts").Binding;
        var input = author.Source<TInput>("machine/input").Binding;
        var lowerer = new RelationQueryExpressionLowerer(author.Clr.ResolveMemberPath);
        var lowered = lowerer.LowerValue(predicate, [facts, input], "machine/guard").RequireValue();
        return new(lowered.Value, facts.Id, input.Id, new(author.Clr.GetTypeRef(typeof(TFacts))), new(author.Clr.GetTypeRef(typeof(TInput))));
    }

    /// <summary>Inspects predicate types, capabilities and dependencies through the shared analyzer.</summary>
    /// <returns>Structured expression analysis, including input dependencies.</returns>
    public ExprAnalysisResult Analyze() => ExprAnalyzer.Analyze(new(
        new("machine.guard"), Predicate,
        new ExprScope(bindings: [new(FactsBinding, FactsContract), new(InputBinding, InputContract)], implicitBinding: FactsBinding),
        ExprExpectation.Boolean));

    internal bool Evaluate(ObservationValue facts, ObservationValue input)
    {
        if (!FactsContract.IsSatisfiedByConstant(facts) || !InputContract.IsSatisfiedByConstant(input))
            throw new ArgumentException("Facts or input violates the declared machine guard contract.");
        PortableExpressionValue Resolve(ValueBindingId binding) => binding == FactsBinding
            ? PortableExpressionValue.FromObservation(facts)
            : binding == InputBinding ? PortableExpressionValue.FromObservation(input)
            : throw new InvalidOperationException("Unknown machine expression binding.");
        var evaluator = new PortableExpressionReferenceEvaluator(new(ExprSemanticsCatalog.Default.Capabilities), "machines.reference/v1");
        var value = evaluator.Evaluate(Predicate, new()
        {
            ResolveBinding = Resolve,
            ResolveField = (binding, path) => Resolve(binding ?? FactsBinding).Project(path),
            ResolveParameter = _ => throw new InvalidOperationException("Machine guards require explicit bindings.")
        });
        return value.RequireObservation("Machine guard").GetBoolean();
    }
}

/// <summary>Outcome of pure admission.</summary>
public enum MachineDecisionKind
{
    /// <summary>Exactly one legal edge was selected.</summary>
    Accepted,
    /// <summary>The action is absent from the protocol.</summary>
    UnknownAction,
    /// <summary>The action has no edge from this state.</summary>
    Illegal,
    /// <summary>All candidate guards are false.</summary>
    Disabled,
    /// <summary>Multiple candidate guards are true.</summary>
    Ambiguous,
    /// <summary>Available evidence cannot evaluate a predicate.</summary>
    Indeterminate
}

/// <summary>A pure decision; commitment remains with the host.</summary>
/// <param name="Kind">Admission classification.</param>
/// <param name="Edge">Selected movement for accepted decisions.</param>
/// <param name="Diagnostic">Explanation for indeterminate decisions.</param>
public sealed record MachineDecision(MachineDecisionKind Kind, MachineEdge? Edge = null, string? Diagnostic = null);

public sealed partial class MachineDefinition
{
    /// <summary>Evaluates all candidate edges under explicit facts and action input.</summary>
    /// <param name="state">Current configuration.</param>
    /// <param name="action">Protocol action.</param>
    /// <param name="facts">Explicit snapshot and environmental facts.</param>
    /// <param name="input">Action payload.</param>
    /// <returns>A decision without state mutation or external effects.</returns>
    /// <exception cref="ArgumentException">State is unknown.</exception>
    public MachineDecision Evaluate(string state, string action, ObservationValue facts, ObservationValue input)
    {
        RequireState(state);
        if (!Edges.Any(e => e.Action == action)) return new(MachineDecisionKind.UnknownAction);
        var candidates = Edges.Where(e => e.From == state && e.Action == action).ToArray();
        if (candidates.Length == 0) return new(MachineDecisionKind.Illegal);
        MachineEdge? selected = null;
        try
        {
            foreach (var edge in candidates)
            {
                if (edge.Guard is not null && !edge.Guard.Evaluate(facts, input)) continue;
                if (selected is not null) return new(MachineDecisionKind.Ambiguous);
                selected = edge;
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return new(MachineDecisionKind.Indeterminate, Diagnostic: error.Message);
        }
        return selected is null ? new(MachineDecisionKind.Disabled) : new(MachineDecisionKind.Accepted, selected);
    }
}
