using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Definition-only transition that applies to an explicit entity state.
/// </summary>
/// <remarks>
/// Compatibility handle retained through ARI-218. New consumers should compile and interpret the canonical
/// three-parameter <c>Transition&lt;TEntity, TInput, TOutcome&gt;</c> authoring result.
/// </remarks>
public sealed class Transition<TEntity, TInput>
    where TEntity : Entity
{
    readonly Func<EntityState, TInput, TransitionResult> apply;

    internal Transition(
        TEntity entity,
        TransitionDefinition definition,
        Func<EntityState, TInput, TransitionResult> apply
        )
    {
        Entity = Guard.RequireNotNull(entity);
        Definition = Guard.RequireNotNull(definition);
        this.apply = Guard.RequireNotNull(apply);
    }

    /// <summary>
    /// Entity definition that owns this transition.
    /// </summary>
    public TEntity Entity { get; }

    /// <summary>
    /// Declarative transition definition compiled from host DSL.
    /// </summary>
    public TransitionDefinition Definition { get; }

    /// <summary>
    /// Transition name.
    /// </summary>
    public string Name => Definition.Name;

    /// <summary>
    /// Applies this transition to the supplied state.
    /// </summary>
    public TransitionResult Apply(EntityState state, TInput input) => apply(state, input);
}
