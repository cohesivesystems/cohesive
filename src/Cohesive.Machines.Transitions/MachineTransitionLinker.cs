using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Machines.Transitions;

/// <summary>Projects discriminator-backed machines into existing Transition linking evidence.</summary>
public static class MachineTransitionLinker
{
    /// <summary>Derives exact source/target predicates and edge-owned discriminator assignments.</summary>
    /// <param name="machine">Authoritatively lowered machine.</param>
    /// <param name="path">Aggregate discriminator field.</param>
    /// <param name="values">A total, injective mapping of states to concrete discriminator values.</param>
    /// <returns>Immutable exact-reference links consumed by the Transition compiler.</returns>
    /// <exception cref="ArgumentException">The mapping is incomplete, nonconcrete, or noninjective.</exception>
    /// <exception cref="ArgumentNullException">Machine or values is null.</exception>
    public static TransitionMachineLinkCatalog LinkDiscriminator(
        MachineDefinition machine, FieldPath path, IReadOnlyDictionary<string, PortableValue> values)
    {
        ArgumentNullException.ThrowIfNull(machine); ArgumentNullException.ThrowIfNull(values);
        if (values.Count != machine.States.Length || machine.States.Any(s => !values.TryGetValue(s, out var v)
            || v is null || v.State != PortableValueState.Concrete))
            throw new ArgumentException("Supply exactly one concrete value per state.", nameof(values));
        if (machine.Edges.Any(e => e.Guard is not null)) throw new ArgumentException("Use predicate links for guarded machines.", nameof(machine));
        var mapped = machine.States.Select(s => values[s]).ToArray();
        if (mapped.Select(v => v.Value).Distinct().Count() != mapped.Length)
            throw new ArgumentException("State discriminator values must be distinct.", nameof(values));
        var reference = machine.Document.Reference;
        var field = Expr.Field(TransitionBindingIds.Observation, path);
        return new([.. machine.Edges.Select(edge => new TransitionMachineEdgeLink(reference, new(edge.Id),
            Expr.Eq(field, Expr.Const(values[edge.From].Value!.Value)),
            Expr.Eq(field, Expr.Const(values[edge.To].Value!.Value)),
            [new(path, values[edge.To])]))]);
    }
}
