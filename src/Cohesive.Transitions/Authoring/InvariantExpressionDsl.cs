using System.Linq.Expressions;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Compiles typed invariant expressions into declarative invariant definitions.
/// </summary>
public static class InvariantExpressionDsl
{
    sealed record EmptyTransitionParameters;

    static readonly ITransitionExpressionCompiler DefaultCompiler = new TransitionExpressionCompiler();

    /// <summary>
    /// Compiles an invariant definition from a typed expression.
    /// </summary>
    public static InvariantDefinition Compile<TEntity>(
        EntityDefinition entityDefinition,
        string name,
        Expression<Func<TEntity, bool>> predicate,
        string? message = null,
        ITransitionExpressionCompiler? compiler = null
        ) where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var entityParameter = Expression.Parameter(typeof(TEntity), predicate.Parameters[0].Name ?? "entity");
        var transitionParameters = Expression.Parameter(typeof(EmptyTransitionParameters), "parameters");
        var body = new ParameterReplacementVisitor(predicate.Parameters[0], entityParameter).Visit(predicate.Body)
                   ?? throw new TransitionExpressionTranslationException("Invariant expression body could not be translated.");
        var adapted = Expression.Lambda<Func<TEntity, EmptyTransitionParameters, bool>>(body, entityParameter, transitionParameters);

        var resolvedCompiler = compiler ?? DefaultCompiler;
        var transition = resolvedCompiler.Compile<TEntity, EmptyTransitionParameters>(
            entityDefinition,
            "__invariant__",
            t => t.Requires(name: name, predicate: adapted, message)
            );

        var precondition = transition.Preconditions.Single();
        return new InvariantDefinition(name, precondition.Expression, message);
    }

    sealed class ParameterReplacementVisitor(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (ReferenceEquals(node, from))
                return to;

            return base.VisitParameter(node);
        }
    }
}
