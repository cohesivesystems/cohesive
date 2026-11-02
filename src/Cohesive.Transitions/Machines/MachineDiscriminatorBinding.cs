using System.Linq.Expressions;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;

namespace Cohesive.Machines.Transitions;

/// <summary>Typed discriminator authoring projected into the existing Transition linker.</summary>
/// <typeparam name="TEntity">Entity observation type.</typeparam>
/// <typeparam name="TValue">Discriminator value type.</typeparam>
public sealed class MachineDiscriminatorBinding<TEntity, TValue> where TEntity : notnull
{
    readonly MachineDefinition machine;
    readonly FieldPath path;
    readonly ValueContract contract;
    readonly Dictionary<string, PortableValue> values = new(StringComparer.Ordinal);
    /// <summary>Binds a lifecycle to a direct or nested entity field.</summary>
    /// <param name="machine">Canonical lifecycle.</param>
    /// <param name="selector">Typed discriminator selector.</param>
    /// <exception cref="ArgumentException">Selector does not lower to a field.</exception>
    public MachineDiscriminatorBinding(MachineDefinition machine, Expression<Func<TEntity, TValue>> selector)
    {
        ArgumentNullException.ThrowIfNull(machine);
        this.machine = machine;
        var author = new RelationQueryExpressionAuthoring();
        var source = author.Source<TEntity>().Binding;
        var lowered = new RelationQueryExpressionLowerer(author.Clr.ResolveMemberPath)
            .LowerValue(selector, [source], "machine/discriminator").RequireValue().Value;
        path = lowered is FieldExpr field ? field.Path : throw new ArgumentException("Select one entity field.", nameof(selector));
        contract = new(author.Clr.GetTypeRef(typeof(TValue)));
    }
    /// <summary>Maps a state to its representation value.</summary>
    /// <param name="state">State identity.</param>
    /// <param name="value">Concrete discriminator value.</param>
    /// <returns>This binding.</returns>
    /// <exception cref="ArgumentException">State is unknown or duplicated.</exception>
    public MachineDiscriminatorBinding<TEntity, TValue> Map(string state, TValue value)
    {
        if (!machine.States.Contains(state)) throw new ArgumentException("Unknown machine state.", nameof(state));
        values.Add(state, PortableValue.Concrete(contract, ObservationValue.FromObject(value))); return this;
    }
    /// <summary>Maps an owner-scoped state without repeating its identity.</summary>
    /// <param name="state">State handle belonging to the bound definition.</param>
    /// <param name="value">Discriminator value.</param>
    /// <returns>This binding.</returns>
    /// <exception cref="ArgumentException">The state belongs to another exact machine.</exception>
    public MachineDiscriminatorBinding<TEntity, TValue> Map(MachineState state, TValue value)
    {
        if (state.Resolve().Machine.Document.Reference != machine.Document.Reference)
            throw new ArgumentException("State belongs to another exact machine.", nameof(state));
        return Map(state.Id, value);
    }
    /// <summary>Derives immutable links; ownership and shape checks remain in Transition compilation.</summary>
    /// <returns>Exact machine edge catalog.</returns>
    /// <exception cref="ArgumentException">Mapping is incomplete or invalid.</exception>
    public TransitionMachineLinkCatalog Build() => MachineTransitionLinker.LinkDiscriminator(machine, path, values);
}

/// <summary>Typed movement conveniences over canonical Transition authoring.</summary>
public static class MachineTransitionAuthoring
{
    /// <summary>References a frozen movement without repeating machine or edge identifiers.</summary>
    /// <typeparam name="TEntity">Entity type.</typeparam>
    /// <typeparam name="TInput">Input type.</typeparam>
    /// <typeparam name="TOutcome">Outcome type.</typeparam>
    /// <param name="transition">Owning Transition sequence.</param>
    /// <param name="movement">Owner-scoped movement handle.</param>
    /// <param name="rejection">Typed rejection expression.</param>
    /// <returns>The same Transition sequence.</returns>
    public static TransitionSequenceBuilder<TEntity, TInput, TOutcome> Move<TEntity, TInput, TOutcome>(
        this TransitionSequenceBuilder<TEntity, TInput, TOutcome> transition,
        MachineMovement movement, Expression<Func<TEntity, TInput, TOutcome>> rejection)
        where TEntity : notnull
    {
        var resolved = movement.Resolve();
        return transition.MoveMachine(new("machine/" + resolved.Machine.Id + "/" + movement.Id),
            resolved.Machine.Document.Reference, new(movement.Id), rejection);
    }
}
