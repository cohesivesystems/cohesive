using System.Collections.Immutable;
using Cohesive.Prelude;

namespace Cohesive.Storage;

/// <summary>Collects native offline binding definitions without replacing their provider-specific builders.</summary>
/// <remarks>Recipes execute during declaration, once per contribution, and must return validated immutable
/// bindings without performing IO. Build freezes membership; it does not repeat adapter preparation.</remarks>
public sealed class StorageModelBuilder
{
    readonly List<object> bindings = [];
    readonly AuthoringState<ImmutableArray<object>> state;

    /// <summary>Starts an independent storage declaration lifetime.</summary>
    public StorageModelBuilder() => state = new(() => [.. bindings]);

    /// <summary>Runs an existing adapter recipe and retains its typed result as a contribution.</summary>
    /// <typeparam name="TBindings">Native immutable binding type, not a connection or runtime registration.</typeparam>
    /// <param name="define">Offline recipe that validates and freezes its native bindings.</param>
    /// <returns>The exact native result for assignment to a strongly typed model property.</returns>
    /// <exception cref="ArgumentNullException">The recipe is null.</exception>
    /// <exception cref="InvalidOperationException">Membership is frozen or the recipe returned null.</exception>
    /// <remarks>Adapter failures propagate during declaration and do not add a contribution.</remarks>
    public TBindings Define<TBindings>(Func<TBindings> define) where TBindings : class
    {
        ArgumentNullException.ThrowIfNull(define);
        return state.Edit(() =>
        {
            var result = define() ?? throw new InvalidOperationException("Storage authoring returned no bindings.");
            bindings.Add(result);
            return result;
        });
    }

    /// <summary>Freezes contributions in declaration order and returns the cached collection.</summary>
    /// <returns>Native immutable binding instances; no provider-neutral mapping conversion is performed.</returns>
    public ImmutableArray<object> Build() => state.Value;
}
