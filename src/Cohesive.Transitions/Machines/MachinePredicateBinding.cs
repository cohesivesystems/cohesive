using Cohesive.Model.Authoring;
using Cohesive.Machines;
using Cohesive.Relations.Authoring;
using System.Linq.Expressions;
using Cohesive.Model;
using Cohesive.Transitions.Compilation;

namespace Cohesive.Transitions.Machines;

/// <summary>Predicate-backed entity binding. Transitions establish fields; the machine checks the final candidate.</summary>
/// <typeparam name="TEntity">Entity facts type.</typeparam>
/// <typeparam name="TInput">Transition input type.</typeparam>
public sealed class MachinePredicateBinding<TEntity, TInput> where TEntity : notnull where TInput : notnull
{
    readonly MachineDefinition machine;
    readonly IClrTypeRefMapper? typeRefMapper;
    readonly Dictionary<string, Expr> predicates = new(StringComparer.Ordinal);
    /// <summary>Creates a binding for an exact machine revision.</summary>
    /// <param name="machine">Canonical lifecycle authority.</param>
    /// <param name="typeRefMapper">Configured CLR contracts shared with entity and Transition authoring.</param>
    public MachinePredicateBinding(MachineDefinition machine, IClrTypeRefMapper? typeRefMapper = null)
    { ArgumentNullException.ThrowIfNull(machine); this.machine = machine; this.typeRefMapper = typeRefMapper; }
    /// <summary>Recognizes a state using pure entity facts.</summary>
    /// <param name="state">Declared state identity.</param>
    /// <param name="predicate">Entity configuration predicate.</param>
    /// <returns>This binding.</returns>
    /// <exception cref="ArgumentException">State is unknown or duplicated.</exception>
    public MachinePredicateBinding<TEntity, TInput> State(string state, Expression<Func<TEntity, TInput, bool>> predicate)
    {
        if (!machine.States.Contains(state)) throw new ArgumentException("Unknown machine state.", nameof(state));
        var guard = MachineGuardCompiler.Compile(predicate, typeRefMapper);
        if (guard.Analyze().Requirements.Bindings.Contains(guard.InputBinding))
            throw new ArgumentException("A state predicate cannot depend on an action payload.", nameof(predicate));
        predicates.Add(state, MachineTransitionLinker.Rebind(guard.Predicate, guard)); return this;
    }
    /// <summary>Recognizes a configuration independently of action payloads.</summary>
    /// <param name="state">Owner-scoped state.</param>
    /// <param name="predicate">Pure entity-state predicate.</param>
    /// <returns>This binding.</returns>
    /// <exception cref="ArgumentException">The state belongs to another machine.</exception>
    public MachinePredicateBinding<TEntity, TInput> State(MachineState state, Expression<Func<TEntity, bool>> predicate)
    {
        if (state.Resolve().Machine.Document.Reference != machine.Document.Reference)
            throw new ArgumentException("State belongs to another exact machine.", nameof(state));
        return State(state.Id, Expression.Lambda<Func<TEntity, TInput, bool>>(predicate.Body,
            predicate.Parameters[0], Expression.Parameter(typeof(TInput), "input")));
    }
    /// <summary>Derives machine links with no edge-owned field assignments.</summary>
    /// <returns>Source admission and final-candidate target obligations.</returns>
    /// <exception cref="InvalidOperationException">State coverage is incomplete.</exception>
    public TransitionMachineLinkCatalog Build()
    {
        if (predicates.Count != machine.States.Length) throw new InvalidOperationException("Every machine state needs a predicate.");
        Expr Exclusive(string state)
        {
            var predicate = predicates[state];
            foreach (var other in machine.States.Where(s => s != state)) predicate = Expr.And(predicate, Expr.Not(predicates[other]));
            return predicate;
        }
        return MachineTransitionLinker.Link(machine, Exclusive, _ => []);
    }
}
