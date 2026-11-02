using System.Linq.Expressions;
using Cohesive.Machines;

namespace Cohesive.Relations.Authoring;

/// <summary>Compiles typed predicates to portable machine guards for authoring and binding adapters.</summary>
/// <remarks>Provided by Cohesive.Relations. This lowering boundary consumes expression source and returns
/// derived IR; ordinary machine authors use the typed When extension.</remarks>
public static class MachineGuardCompiler
{
    /// <summary>Lowers a typed Boolean expression without executing it.</summary>
    /// <typeparam name="TFacts">Explicit facts record type.</typeparam>
    /// <typeparam name="TInput">Action input record type.</typeparam>
    /// <param name="predicate">Pure expression over facts and input.</param>
    /// <returns>Portable predicate with structural input contracts.</returns>
    /// <exception cref="ArgumentNullException">The predicate is null.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">An expression is outside the portable language.</exception>
    public static MachineGuard Compile<TFacts, TInput>(Expression<Func<TFacts, TInput, bool>> predicate)
        where TFacts : notnull where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var author = new RelationQueryExpressionAuthoring();
        var facts = author.Source<TFacts>("machine/facts").Binding;
        var input = author.Source<TInput>("machine/input").Binding;
        var lowerer = new RelationQueryExpressionLowerer(author.Clr.ResolveMemberPath);
        var lowered = lowerer.LowerValue(predicate, [facts, input], "machine/guard").RequireValue();
        return new(lowered.Value, facts.Id, input.Id, new(author.Clr.GetTypeRef(typeof(TFacts))), new(author.Clr.GetTypeRef(typeof(TInput))));
    }

}
