using Cohesive.Model.Serialization;
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
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>();
        var links = ImmutableArray.CreateBuilder<TransitionMachineEdgeLink>(machine.Edges.Length);
        foreach (var group in machine.Edges.GroupBy(edge => (edge.From, edge.Action)))
        {
            var edges = group.ToArray();
            var guards = edges.Select(edge => edge.Guard is { } guard
                ? Rebind(guard.Predicate, guard) : Expr.Const(true)).ToImmutableArray();
            if (edges.Length > 1)
            {
                // Structural unguarded overlap is also rejected by MachineBuilder. Keep this
                // boundary explicit for independently materialized linker inputs.
                if (edges.Count(edge => edge.Guard is null) > 1)
                    throw new ArgumentException("Multiple unguarded sibling edges are always ambiguous.", nameof(machine));
                diagnostics.Add(new("machines.linking.disjointnessUnknown", DiagnosticSeverity.Warning,
                    $"Guard disjointness for '{machine.Id}/{group.Key.From}/{group.Key.Action}' is unproven; runtime checks remain required.",
                    $"/machines/{Uri.EscapeDataString(machine.Id)}/states/{Uri.EscapeDataString(group.Key.From)}/actions/{Uri.EscapeDataString(group.Key.Action)}"));
            }
            var sourceState = configuration(group.Key.From);
            for (var index = 0; index < edges.Length; index++)
            {
                var edge = edges[index];
                var hasGuards = edges.Length > 1 || edge.Guard is not null;
                links.Add(new(machine.Document.Reference, new(edge.Id), sourceState,
                    configuration(edge.To), assignments(edge), hasGuards ? guards : [], hasGuards ? index : -1));
            }
        }
        return new(links.MoveToImmutable(), diagnostics.ToImmutable());
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
