using Cohesive.Transitions.Model;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Entry point for compiling typed transition expressions into declarative transition definitions.
/// </summary>
public static class TransitionExpressionDsl
{
    static readonly ITransitionExpressionCompiler DefaultCompiler = new TransitionExpressionCompiler();

    /// <summary>
    /// Compiles a transition definition from typed expression authoring.
    /// </summary>
    public static TransitionDefinition Compile<TEntity, TParameters>(
        EntityDefinition entityDefinition,
        string transitionName,
        Action<TransitionExpressionBuilder<TEntity, TParameters>> configure,
        ITransitionExpressionCompiler? compiler = null
        ) where TEntity : Entity
    {
        var resolvedCompiler = compiler ?? DefaultCompiler;
        return resolvedCompiler.Compile(entityDefinition, transitionName: transitionName, configure);
    }
}
