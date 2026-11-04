using Cohesive.Machines;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Machines;

/// <summary>Projects machine admission and discriminator assignments into existing Transition linking evidence.</summary>
public static class MachineTransitionLinker
{
    /// <summary>Derives exact source/target predicates and edge-owned discriminator assignments.</summary>
    /// <param name="machine">Authoritatively lowered machine.</param>
    /// <param name="path">Aggregate discriminator field.</param>
    /// <param name="values">A total, injective mapping of states to concrete discriminator values.</param>
    /// <returns>Immutable exact-reference links consumed by the Transition compiler.</returns>
    /// <exception cref="ArgumentException">The mapping is incomplete, nonconcrete, or noninjective.</exception>
    /// <exception cref="ArgumentNullException">Machine or values is null.</exception>
    /// <exception cref="NotSupportedException">A guard uses an expression outside the supported binding fragment.</exception>
    public static TransitionMachineLinkCatalog LinkDiscriminator(
        MachineDefinition machine, FieldPath path, IReadOnlyDictionary<string, PortableValue> values)
    {
        ArgumentNullException.ThrowIfNull(machine); ArgumentNullException.ThrowIfNull(values);
        if (values.Count != machine.States.Length || machine.States.Any(s => !values.TryGetValue(s, out var v)
            || v is null || v.State != PortableValueState.Concrete))
            throw new ArgumentException("Supply exactly one concrete value per state.", nameof(values));
        var mapped = machine.States.Select(s => values[s]).ToArray();
        if (mapped.Select(v => v.Value).Distinct().Count() != mapped.Length)
            throw new ArgumentException("State discriminator values must be distinct.", nameof(values));
        var reference = machine.Document.Reference;
        var field = Expr.Field(TransitionBindingIds.Observation, path);
        return new([.. machine.Edges.Select(edge => new TransitionMachineEdgeLink(reference, new(edge.Id),
            SourceAdmission(machine, edge, Expr.Eq(field, Expr.Const(values[edge.From].Value!.Value))),
            Expr.Eq(field, Expr.Const(values[edge.To].Value!.Value)),
            [new(path, values[edge.To])]))]);
    }

    // Both binding projections preserve the same guard and unique-selection obligations.
    internal static Expr SourceAdmission(MachineDefinition machine, MachineEdge edge, Expr configuration)
    {
        var source = configuration;
        if (edge.Guard is { } guard) source = Expr.And(source, Rebind(guard.Predicate, guard));
        foreach (var other in machine.Edges.Where(candidate => candidate.Id != edge.Id
                     && candidate.From == edge.From && candidate.Action == edge.Action))
            source = Expr.And(source, other.Guard is null ? Expr.Const(false) : Expr.Not(Rebind(other.Guard.Predicate, other.Guard)));
        return source;
    }

    internal static Expr Rebind(Expr expression, MachineGuard guard)
    {
        ValueBindingId Bind(ValueBindingId id) => id == guard.FactsBinding ? TransitionBindingIds.Observation
            : id == guard.InputBinding ? TransitionBindingIds.Input : throw new ArgumentException("Unbound machine expression.");
        return expression switch
        {
            BindingExpr e => new BindingExpr(Bind(e.Binding)),
            FieldExpr e => new FieldExpr(e.Path, e.Binding is { } id ? Bind(id) : TransitionBindingIds.Observation),
            ConstantExpr or LiteralExpr => expression,
            UnaryExpr e => e with { Operand = Rebind(e.Operand, guard) },
            BinaryExpr e => e with { Left = Rebind(e.Left, guard), Right = Rebind(e.Right, guard) },
            _ => throw new NotSupportedException("Machine bindings currently accept field, literal, unary and binary expressions.")
        };
    }
}
