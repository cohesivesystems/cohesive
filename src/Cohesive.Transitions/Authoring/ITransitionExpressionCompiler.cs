using Cohesive.Transitions.Model;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Compiles restricted C# expression trees into declarative transition definitions.
/// </summary>
public interface ITransitionExpressionCompiler
{
    /// <summary>
    /// Compiles a typed transition expression configuration into a <see cref="TransitionDefinition"/>.
    /// </summary>
    TransitionDefinition Compile<TEntity, TParameters>(
        EntityDefinition entityDefinition,
        string transitionName,
        Action<TransitionExpressionBuilder<TEntity, TParameters>> configure
        ) where TEntity : Entity;
}
