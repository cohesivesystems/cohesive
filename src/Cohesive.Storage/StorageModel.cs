using System.Collections.Immutable;
using Cohesive.Prelude;

namespace Cohesive.Storage;

/// <summary>Optional constructor-authored collection of native offline storage bindings.</summary>
/// <remarks>A model can contribute several databases or providers. Each adapter remains responsible for
/// validating and freezing its bindings. Runtime data sources are separate caller-owned attachments.</remarks>
public abstract class StorageModel : IAuthoringModel
{
    /// <summary>Underlying builder, also usable directly without a model subclass.</summary>
    protected StorageModelBuilder Storage { get; } = new();

    /// <summary>Frozen native binding contributions in declaration order; retains their original instances.</summary>
    public ImmutableArray<object> Definition => Storage.Build();

    /// <summary>Exact adapter preparation owned by this model; null for a model without composed placement.</summary>
    public IStorageComposition? Composition { get { _ = Definition; return Storage.Composition; } }

    /// <summary>Gets the selected offline composition with its native adapter API.</summary>
    /// <typeparam name="T">Expected adapter preparation.</typeparam>
    /// <returns>The shared prepared instance.</returns>
    /// <exception cref="InvalidOperationException">The model has no composition of the requested kind.</exception>
    public T RequireComposition<T>() where T : class, IStorageComposition => Composition as T
        ?? throw new InvalidOperationException($"Storage model requires composition {typeof(T).Name}.");

    void IAuthoringModel.Complete() => _ = Definition;
}
