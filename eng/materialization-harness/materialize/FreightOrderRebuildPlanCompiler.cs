using Cohesive.Execution;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Realization;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>Harness evidence combines portable rebuild composition with the separately compiled storage interpretation.</summary>
public sealed class FreightOrderRebuildPlanCompilation(MaterializationPartitionedRebuild rebuild, StorageRealizationDocument storageRealization)
{
    /// <summary>Provider interpretation identity.</summary>
    public string Provider => rebuild.Provider;
    /// <summary>Separate canonical storage realization for harness evidence.</summary>
    public StorageRealizationDocument StorageRealization { get; } = storageRealization;
    /// <summary>Canonical rebuild request.</summary>
    public MaterializationRebuildRequestDocument Request => rebuild.Request;
    /// <summary>Frozen logical partition membership.</summary>
    public MaterializationRebuildMembershipEvidence Membership => rebuild.Membership;
    /// <summary>Admitted partition placement.</summary>
    public MaterializationTargetPlacementPlan Placement => rebuild.Placement;
    /// <summary>Compiled provider leaf plan.</summary>
    public MaterializationRebuildPlan Plan => rebuild.Plan;
    /// <summary>Canonical plan set.</summary>
    public MaterializationRebuildPlanSet PlanSet => rebuild.PlanSet;
    /// <summary>Binds native runtime interpretations to the admitted plan.</summary>
    /// <param name="target">Admitted target.</param>
    /// <param name="progressStore">Durable progress authority.</param>
    /// <param name="controlRuntimeProvider">Optional safe-point control runtime.</param>
    /// <returns>Executable resolved plan.</returns>
    public ResolvedMaterializationRebuildPlan Resolve(IMaterializationTarget target, IMaterializationProgressStore progressStore,
        MaterializationIndexSyncControlRuntimeProvider? controlRuntimeProvider = null) => rebuild.Resolve(target, progressStore, controlRuntimeProvider);
}

/// <summary>Supplies the freight harness's explicit bounds and membership to the shared composition.</summary>
public static class FreightOrderRebuildPlanCompiler
{
    const int MaximumPageItems = 2;
    const long MaximumPageBytes = 1024 * 1024;
    /// <summary>Compiles the shared partitioned recipe with freight-specific limits.</summary>
    /// <param name="semantics">Canonical freight semantics.</param>
    /// <param name="provider">Provider interpretation identity.</param>
    /// <param name="storageRealization">Matching storage evidence.</param>
    /// <param name="target">Native target descriptor.</param>
    /// <param name="tenantBindings">Exact per-partition native bindings.</param>
    /// <param name="impactPlan">Compiled impact routes.</param>
    /// <returns>Harness evidence and executable shared compilation.</returns>
    public static FreightOrderRebuildPlanCompilation Compile(FreightOrderMaterializationSemantics semantics,
        string provider, StorageRealizationDocument storageRealization, MaterializationTargetDescriptor target,
        IEnumerable<MaterializationPartitionBinding> tenantBindings, MaterializationImpactPlan impactPlan)
    {
        if (storageRealization.Structure.Id != semantics.Structure.Id)
            throw new ArgumentException("Storage realization belongs to another structure.", nameof(storageRealization));
        return new(MaterializationPartitionedRebuildCompiler.Compile(semantics.Document, semantics.Plan,
            new(MaximumPageItems, MaximumPageBytes, 16, MaximumPageBytes, 128,
                MaterializationRebuildProcessFactory.MaximumStartsPerActivation, MaterializationRebuildProcessFactory.MaximumParallelism,
                maximumChangeFeedsPerConvergenceActivation: 64),
            provider, target, tenantBindings, impactPlan), storageRealization);
    }
    /// <summary>Returns the canonical logical partition identity for one freight tenant.</summary>
    /// <param name="tenant">Stable tenant identity.</param>
    /// <returns>Provider-neutral tenant partition identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="tenant"/> is empty.</exception>
    public static RelationQueryLogicalPartitionIdentity LogicalPartition(string tenant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        return new($"materialization-harness/freight/tenant/{tenant}");
    }

    internal static MaterializationImpactPlan CompileImpactPlan(
        FreightOrderMaterializationSemantics semantics,
        string provider)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var compilation = MaterializationImpactPlanCompiler.Compile(
            document: semantics.Document,
            policy: new(
                id: new($"materialization-harness/{provider}/freight-impact/v1"),
                strategyPreference:
                [
                    MaterializationImpactStrategyKind.InverseTraversal
                ],
                maximumAffectedRoots: 64,
                maximumReadBytes: MaximumPageBytes));
        return Require(
            artifact: compilation.Plan,
            diagnostics: compilation.Diagnostics.Select(static diagnostic => diagnostic.Message));
    }

    static T Require<T>(T? artifact, IEnumerable<string> diagnostics) where T : class =>
        artifact ?? throw new InvalidOperationException(string.Join(Environment.NewLine, diagnostics));
}
