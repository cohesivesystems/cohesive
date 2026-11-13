using Cohesive.Authoring;
using Cohesive.Model.Authoring;
using System.Linq.Expressions;
using Cohesive.Relations.Authoring;

namespace Cohesive.Machines;

/// <summary>Typed machine guard extensions supplied by the Cohesive.Relations assembly.</summary>
/// <remarks>Reference Cohesive.Relations and import Cohesive.Machines to use these extensions.
/// The core Cohesive assembly supplies machine semantics but does not perform CLR expression lowering.</remarks>
public static class MachineGuardAuthoring
{
    /// <summary>Lowers and attaches a typed guard to a movement using the Cohesive.Relations assembly.</summary>
    /// <typeparam name="TFacts">Facts type.</typeparam>
    /// <typeparam name="TInput">Action payload type.</typeparam>
    /// <param name="movement">Owning movement declaration.</param>
    /// <param name="predicate">Pure admission predicate.</param>
    /// <param name="typeRefMapper">Configured CLR contracts shared with entity and Transition authoring; defaults to the framework mapper.</param>
    /// <returns>The same movement with its lowered guard.</returns>
    /// <exception cref="ArgumentNullException">The movement or predicate is null.</exception>
    /// <exception cref="InvalidOperationException">Machine source is frozen.</exception>
    /// <exception cref="ExpressionAuthoringException">The expression is unsupported.</exception>
    public static MachineMovement When<TFacts, TInput>(this MachineMovement movement,
        Expression<Func<TFacts, TInput, bool>> predicate, IClrTypeRefMapper? typeRefMapper = null)
        where TFacts : notnull where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(movement);
        return movement.When(MachineGuardCompiler.Compile(predicate, typeRefMapper));
    }
}
