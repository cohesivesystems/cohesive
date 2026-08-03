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

    /// <summary>
    /// Compiles an invariant definition from a typed expression.
    /// </summary>
    /// <typeparam name="TEntity">The authored entity type.</typeparam>
    /// <param name="entityDefinition">The canonical entity definition used to resolve field identities.</param>
    /// <param name="name">The stable invariant name.</param>
    /// <param name="predicate">The portable predicate that must hold for valid entity state.</param>
    /// <param name="message">An optional violation message.</param>
    /// <returns>The canonical invariant definition produced from <paramref name="predicate"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entityDefinition"/> or <paramref name="predicate"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="TransitionExpressionTranslationException">
    /// <paramref name="predicate"/> uses an expression outside the portable Transition expression subset.
    /// </exception>
    public static InvariantDefinition Compile<TEntity>(
        EntityDefinition entityDefinition,
        string name,
        Expression<Func<TEntity, bool>> predicate,
        string? message = null
        ) where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var entityParameter = Expression.Parameter(typeof(TEntity), predicate.Parameters[0].Name ?? "entity");
        var transitionParameters = Expression.Parameter(typeof(EmptyTransitionParameters), "parameters");
        var body = new ParameterReplacementVisitor(predicate.Parameters[0], entityParameter).Visit(predicate.Body)
                   ?? throw new TransitionExpressionTranslationException("Invariant expression body could not be translated.");
        var adapted = Expression.Lambda<Func<TEntity, EmptyTransitionParameters, bool>>(body, entityParameter, transitionParameters);

        var translator = new TransitionExpressionTranslator<TEntity, EmptyTransitionParameters>(
            entityDefinition,
            parameterNames: new HashSet<string>(StringComparer.Ordinal));
        return new InvariantDefinition(name, translator.Translate(adapted), message);
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
