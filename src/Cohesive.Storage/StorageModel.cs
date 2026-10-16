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

    void IAuthoringModel.Complete() => _ = Definition;
}
