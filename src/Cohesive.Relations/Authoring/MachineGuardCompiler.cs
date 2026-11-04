using Cohesive.Model.Authoring;
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
    /// <param name="typeRefMapper">Configured CLR contracts shared with entity and Transition authoring; defaults to the framework mapper.</param>
    /// <returns>Portable predicate with structural input contracts.</returns>
    /// <exception cref="ArgumentNullException">The predicate is null.</exception>
    /// <exception cref="RelationQueryExpressionAuthoringException">An expression is outside the portable language.</exception>
    public static MachineGuard Compile<TFacts, TInput>(Expression<Func<TFacts, TInput, bool>> predicate, IClrTypeRefMapper? typeRefMapper = null)
        where TFacts : notnull where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var author = new RelationQueryExpressionAuthoring();
        var facts = author.Source<TFacts>("machine/facts").Binding;
        var input = author.Source<TInput>("machine/input").Binding;
        var lowerer = new RelationQueryExpressionLowerer(author.Clr.ResolveMemberPath);
        var lowered = lowerer.LowerValue(predicate, [facts, input], "machine/guard").RequireValue();
        // Guards are standalone portable contracts, so resolve CLR enums and nested values using
        // the same mapper as Transition authoring rather than retaining graph-local named references.
        var mapper = typeRefMapper ?? new DefaultClrTypeRefMapper();
        return new(lowered.Value, facts.Id, input.Id,
            new(mapper.Map(typeof(TFacts), nullability: null)), new(mapper.Map(typeof(TInput), nullability: null)));
    }

}
