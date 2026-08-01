using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

sealed class MaterializationTestImpactRuntime(MaterializationImpactPlanFingerprint impactPlan)
    : IMaterializationImpactRuntime
{
    public MaterializationImpactPlanFingerprint ImpactPlan { get; } = impactPlan;

    public ValueTask<ImmutableArray<MaterializationAffectedRoot>> ResolveRootsAsync(
        OperationContext context,
        MaterializationImpactRootResolutionRequest request) =>
        throw new InvalidOperationException("This baseline-only test runtime does not execute impact resolution.");

    public ValueTask<ImmutableArray<MaterializationRootProjection>> HydrateAsync(
        OperationContext context,
        MaterializationImpactHydrationRequest request) =>
        throw new InvalidOperationException("This baseline-only test runtime does not execute incremental hydration.");
}
