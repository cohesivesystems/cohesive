using System.Collections.Immutable;
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
        var field = Expr.Field(TransitionBindingIds.Observation, path);
        return Link(machine,
            state => Expr.Eq(field, Expr.Const(values[state].Value!.Value)),
            edge => [new(path, values[edge.To])]);
    }

    // Group and lower guards once. Every linked edge shares its group's ambiguity evidence.
    internal static TransitionMachineLinkCatalog Link(MachineDefinition machine,
        Func<string, Expr> configuration,
        Func<MachineEdge, ImmutableArray<TransitionMachineConfigurationAssignment>> assignments)
    {
        var links = ImmutableArray.CreateBuilder<TransitionMachineEdgeLink>(machine.Edges.Length);
        foreach (var group in machine.Edges.GroupBy(edge => (edge.From, edge.Action)))
        {
            var edges = group.ToArray();
            var guards = edges.Select(edge => edge.Guard is { } guard
                ? Rebind(guard.Predicate, guard) : Expr.Const(true)).ToArray();
            var sourceState = configuration(group.Key.From);
            // Balanced counting keeps expression depth logarithmic in the sibling count.
            Expr Count(int start, int length) => length == 1
                ? Expr.If(guards[start], Expr.Const(1), Expr.Const(0))
                : Expr.Add(Count(start, length / 2), Count(start + length / 2, length - length / 2));
            var ambiguity = edges.Length > 1
                ? Expr.And(sourceState, Expr.Gt(Count(0, edges.Length), Expr.Const(1))) : null;
            for (var index = 0; index < edges.Length; index++)
            {
                var edge = edges[index];
                var source = edge.Guard is null ? sourceState : Expr.And(sourceState, guards[index]);
                links.Add(new(machine.Document.Reference, new(edge.Id), source,
                    configuration(edge.To), assignments(edge), ambiguity));
            }
        }
        return new(links.MoveToImmutable());
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
