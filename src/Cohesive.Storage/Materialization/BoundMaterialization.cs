namespace Cohesive.Storage.Materialization;

/// <summary>Admitted runtime ports for a partitioned materialization; all native resources remain caller-owned.</summary>
/// <remarks>This is an execution binding, not an authoring model. It neither initializes resources nor promotes generations.</remarks>
public sealed class BoundMaterialization
{
    /// <summary>Resolves a compiled plan against its native target and durable stores.</summary>
    /// <param name="compilation">Admitted partitioned plan and source bindings.</param>
    /// <param name="target">Exact native target interpretation.</param>
    /// <param name="progressStore">Durable progress authority.</param>
    /// <param name="workStore">Durable synchronization work authority for this deployment.</param>
    /// <exception cref="ArgumentNullException">A required port is null.</exception>
    /// <exception cref="ArgumentException">Ports do not satisfy the compiled plan.</exception>
    public BoundMaterialization(MaterializationPartitionedRebuild compilation, IMaterializationTarget target,
        IMaterializationProgressStore progressStore, IMaterializationSynchronizationWorkStore workStore)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(workStore);
        Compilation = compilation;
        Resolved = compilation.Resolve(target, progressStore);
        WorkStore = workStore;
    }

    /// <summary>Canonical compilation and partition evidence.</summary>
    public MaterializationPartitionedRebuild Compilation { get; }
    /// <summary>Resolved source, target, hydration and progress interpretations.</summary>
    public ResolvedMaterializationRebuildPlan Resolved { get; }
    /// <summary>Borrowed durable synchronization work store.</summary>
    public IMaterializationSynchronizationWorkStore WorkStore { get; }
}
