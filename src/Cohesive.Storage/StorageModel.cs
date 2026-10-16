using Cohesive.Prelude;

namespace Cohesive.Storage;

/// <summary>Optional model lifetime for offline bindings produced by an existing storage adapter builder.</summary>
/// <typeparam name="TBindings">Immutable native storage mapping definition, not a live database registration.</typeparam>
/// <remarks>The factory runs once, including cached failures. It must validate/freeze its result and perform no IO.
/// Runtime data sources remain explicit caller-owned attachments. Different providers retain their native builders.</remarks>
public abstract class StorageModel<TBindings> : IAuthoringModel where TBindings : class
{
    readonly AuthoringState<TBindings> state;
    /// <summary>Retains the existing adapter authoring recipe without executing it from the base constructor.</summary>
    /// <param name="define">Offline native builder recipe.</param>
    protected StorageModel(Func<TBindings> define)
    {
        ArgumentNullException.ThrowIfNull(define);
        state = new(() => define() ?? throw new InvalidOperationException("Storage authoring returned no bindings."));
    }
    /// <summary>Validated native mapping definitions, prepared once for this model instance.</summary>
    public TBindings Bindings => state.Value;
    void IAuthoringModel.Complete() => _ = Bindings;
}
