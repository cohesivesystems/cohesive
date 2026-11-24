using Cohesive.Authoring;
using System.Collections.Immutable;
using Cohesive.Model.Authoring;
using Cohesive.Machines;
using System.Linq.Expressions;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;

namespace Cohesive.Transitions.Machines;

/// <summary>Typed discriminator authoring projected into the existing Transition linker.</summary>
/// <remarks>Edge guards read entity observation and transition input. The movement assigns the discriminator
/// only after source configuration, guard and unique-selection obligations succeed.</remarks>
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
    /// <param name="typeRefMapper">Configured CLR contracts shared with entity and Transition authoring; defaults to the framework mapper.</param>
    /// <exception cref="ArgumentException">Selector does not lower to a field.</exception>
    public MachineDiscriminatorBinding(MachineDefinition machine, Expression<Func<TEntity, TValue>> selector, IClrTypeRefMapper? typeRefMapper = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        this.machine = machine;
        var author = new RelationQueryExpressionAuthoring();
        var source = author.Source<TEntity>().Binding;
        var lowered = new ExpressionLowerer(author.Clr.ResolveMemberPath)
            .LowerValue(selector, [source], "machine/discriminator").RequireValue().Value;
        path = lowered is FieldExpr field ? field.Path : throw new ArgumentException("Select one entity field.", nameof(selector));
        contract = new((typeRefMapper ?? new DefaultClrTypeRefMapper()).Map(typeof(TValue), nullability: null));
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
    /// <summary>Binds this machine as the entity's sole lifecycle authority.</summary>
    /// <param name="entity">Unbound entity definition.</param>
    /// <returns>A new definition carrying classification and discriminator assignments.</returns>
    /// <exception cref="ArgumentException">The mapping is incomplete or noninjective.</exception>
    /// <exception cref="InvalidOperationException">The entity is already bound.</exception>
    /// <exception cref="NotSupportedException">The entity already has a published shape graph.</exception>
    /// <exception cref="ArgumentNullException">Entity is null.</exception>
    public Cohesive.Transitions.Model.EntityDefinition Bind(Cohesive.Transitions.Model.EntityDefinition entity)
    {
        _ = Build();
        var field = Expr.Field(Cohesive.Transitions.IR.TransitionBindingIds.Observation, path);
        return new EntityMachineBinding(machine.Document,
            values.ToImmutableSortedDictionary(pair => pair.Key,
                pair => new EntityMachineState(Expr.Eq(field, Expr.Const(pair.Value.Value!.Value)),
                    [new(path, pair.Value)]), StringComparer.Ordinal)).Attach(entity);
    }

    /// <summary>Derives immutable guarded links; ownership and shape checks remain in Transition compilation.</summary>
    /// <returns>Exact machine edge catalog.</returns>
    /// <exception cref="ArgumentException">Mapping is incomplete or invalid.</exception>
    /// <exception cref="NotSupportedException">A guard uses an unsupported binding expression.</exception>
    public TransitionMachineLinkCatalog Build() => MachineTransitionLinker.LinkDiscriminator(machine, path, values);
}

/// <summary>Typed movement conveniences over canonical Transition authoring.</summary>
public static class MachineTransitionAuthoring
{
    /// <summary>Dispatches an action using the entity's bound lifecycle and records the exact selected edge.</summary>
    /// <typeparam name="TEntity">Entity type.</typeparam>
    /// <typeparam name="TInput">Command type.</typeparam>
    /// <typeparam name="TOutcome">Outcome type.</typeparam>
    /// <param name="transition">Owning transition sequence.</param>
    /// <param name="action">Action from the entity's bound machine.</param>
    /// <param name="rejection">Outcome when no movement is enabled.</param>
    /// <returns>The same transition sequence.</returns>
    /// <exception cref="InvalidOperationException">The action is unknown or its exact machine is not bound.</exception>
    public static TransitionSequenceBuilder<TEntity, TInput, TOutcome> Move<TEntity, TInput, TOutcome>(
        this TransitionSequenceBuilder<TEntity, TInput, TOutcome> transition,
        MachineAction action, Expression<Func<TEntity, TInput, TOutcome>> rejection) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(rejection);
        var (machine, edges) = action.Resolve();
        var reference = machine.Document.Reference;
        var binding = transition.Context.MachineBinding;
        if (binding is null || binding.Machine.Reference != reference)
            throw new InvalidOperationException($"Action '{action.Id}' requires the entity binding for machine '{machine.Id}'.");

        ExecutionNodeId Id(string kind) => transition.Context.NextConventionId(kind);
        void MoveEdge(TransitionSequenceBuilder<TEntity, TInput, TOutcome> branch, MachineEdge edge) =>
            branch.MoveMachine(Id("movement"), reference, new(edge.Id), rejection);

        if (edges.Length == 1)
        {
            MoveEdge(transition, edges[0]);
            return transition;
        }

        return transition.Choose(Id("action"), choice =>
        {
            foreach (var edge in edges[..^1])
            {
                var source = binding.States[edge.From].Predicate;
                var enabled = edge.Guard is { } guard
                    ? Expr.And(source, MachineTransitionLinker.Rebind(guard.Predicate, guard))
                    : source;
                choice.CaseExpression(Id("candidate"), enabled, branch => MoveEdge(branch, edge));
            }
            // Every exact movement enforces source, guard and sibling uniqueness. The last
            // candidate therefore also supplies the normal admission rejection when none matches.
            choice.Fallback(Id("last"), branch => MoveEdge(branch, edges[^1]));
        });
    }

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
