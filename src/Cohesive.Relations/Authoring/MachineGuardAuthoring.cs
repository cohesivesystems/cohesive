using System.Linq.Expressions;
using Cohesive.Machines;

namespace Cohesive.Relations.Authoring;

/// <summary>Typed machine guard authoring using the shared Relations expression frontend.</summary>
public static class MachineGuardAuthoring
{
    /// <summary>Lowers a typed Boolean expression without executing it.</summary>
    /// <typeparam name="TFacts">Explicit facts record type.</typeparam>
    /// <typeparam name="TInput">Action input record type.</typeparam>
    /// <param name="predicate">Pure expression over facts and input.</param>
    /// <returns>Portable predicate with structural input contracts.</returns>
    /// <exception cref="RelationQueryExpressionAuthoringException">An expression is outside the portable language.</exception>
    public static MachineGuard From<TFacts, TInput>(Expression<Func<TFacts, TInput, bool>> predicate)
        where TFacts : notnull where TInput : notnull
    {
        var author = new RelationQueryExpressionAuthoring();
        var facts = author.Source<TFacts>("machine/facts").Binding;
        var input = author.Source<TInput>("machine/input").Binding;
        var lowerer = new RelationQueryExpressionLowerer(author.Clr.ResolveMemberPath);
        var lowered = lowerer.LowerValue(predicate, [facts, input], "machine/guard").RequireValue();
        return new(lowered.Value, facts.Id, input.Id, new(author.Clr.GetTypeRef(typeof(TFacts))), new(author.Clr.GetTypeRef(typeof(TInput))));
    }

    /// <summary>Lowers and attaches a typed guard to a movement.</summary>
    /// <typeparam name="TFacts">Facts type.</typeparam>
    /// <typeparam name="TInput">Action payload type.</typeparam>
    /// <param name="movement">Owning movement declaration.</param>
    /// <param name="predicate">Pure admission predicate.</param>
    /// <returns>The same movement with its lowered guard.</returns>
    /// <exception cref="InvalidOperationException">Machine source is frozen.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">The expression is unsupported.</exception>
    public static MachineMovement When<TFacts, TInput>(this MachineMovement movement,
        Expression<Func<TFacts, TInput, bool>> predicate)
        where TFacts : notnull where TInput : notnull => movement.When(From(predicate));
}
