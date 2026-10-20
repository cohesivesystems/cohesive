using System.Collections.Immutable;

namespace Cohesive.Storage;

/// <summary>Offline preparation over exact native contributions to a storage model.</summary>
/// <remarks>Contains no clients or credentials. Provider adapters retain their own planning and evidence types.</remarks>
public interface IStorageComposition
{
    /// <summary>The native binding instances whose placement and guarantees were prepared.</summary>
    ImmutableArray<object> Bindings { get; }
}
