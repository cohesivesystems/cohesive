using System.Collections.Immutable;
using Cohesive.Prelude;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Composes a positioned feed with the actual bounded read capabilities used to resolve affected roots.</summary>
/// <remarks>The evidence is the union of admitted reader capabilities, never a copy of requested guarantees.
/// This composition requires linked baseline/impact plans, one native source and one logical partition; it does not claim a snapshot across reads.</remarks>
public sealed class PostgresMaterializationImpactSource : IMaterializationRetainedChangeSource, IMaterializationSettlingSource, IMaterializationCurrentStateEnrichmentSource
{
    readonly PostgresLogicalReplicationMaterializationChangeSource source;
    readonly MaterializationCurrentStateEnricher? enricher;
    /// <summary>Attaches same-placement inverse-read evidence to a native feed.</summary>
    /// <param name="source">Preflighted feed.</param>
    /// <param name="plans">Exact baseline/impact lowering that relates both read scopes to one semantic plan.</param>
    /// <param name="reads">Materialization readers actually used by affected-root execution.</param>
    /// <param name="currentStateRequirement">Optional root feed whose partial WAL image requires current-state enrichment.</param>
    /// <param name="currentStateReader">Actual impact reader for the optional root enrichment.</param>
    /// <exception cref="ArgumentException">Read scopes differ from the feed's plan, source or partition.</exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public PostgresMaterializationImpactSource(PostgresLogicalReplicationMaterializationChangeSource source,
        MaterializationRelationReadPlans plans, IEnumerable<PostgresMaterializationSource> reads,
        MaterializationSourceRequirement? currentStateRequirement = null, MaterializationObservationReader? currentStateReader = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(plans);
        var candidates = reads.ToArray();
        if ((source.Scope.PhysicalPlan != plans.Baseline.Fingerprint && source.Scope.PhysicalPlan != plans.Impact.Fingerprint)
            || candidates.Length == 0 || candidates.Any(read => read is null || read.Scope.PhysicalPlan != plans.Impact.Fingerprint
            || read.Scope.Source != source.Scope.Source || read.Scope.LogicalPartition != source.Scope.LogicalPartition))
            throw new ArgumentException("Impact reads must belong to the feed's exact physical plan, source and partition.", nameof(reads));
        var native = source.Descriptor.CapabilityProfile;
        var evidence = native.Evidence.ToBuilder();
        foreach (var read in candidates.OrderBy(read => read.Scope.Input.Value, StringComparer.Ordinal))
            foreach (var capability in read.Descriptor.CapabilityProfile.Evidence.Where(item => item.Capability is
                         MaterializationCapabilityKind.SourceBatchedPointRead or MaterializationCapabilityKind.SourceParameterizedPredicateQuery))
                evidence.Add(new(new("impact/" + Uri.EscapeDataString(read.Scope.Input.Value) + "/" + capability.Id.Value),
                    capability.Capability, capability.Realization, capability.Guarantees, capability.OperatingLimits,
                    capability.SourceReferences, capability.Description));
        MaterializationCapabilityProfile profile = new(new(native.Id.Value + "/impact-closure/v1"),
            native.Role, native.Subject, evidence.ToImmutable());
        if ((currentStateRequirement is null) != (currentStateReader is null))
            throw new ArgumentException("Current-state enrichment requires both the exact requirement and reader.");
        if (currentStateRequirement is not null)
        {
            if (currentStateRequirement.Input != source.Scope.Input)
                throw new ArgumentException("Current-state requirement belongs to another input.", nameof(currentStateRequirement));
            var limits = candidates.Single(read => read.Scope.Input == source.Scope.Input).Descriptor.CapabilityProfile.Evidence
                .First(item => item.Capability == MaterializationCapabilityKind.SourceBatchedPointRead).OperatingLimits;
            var result = MaterializationCurrentStateEnrichmentCompiler.Compile(source.Scope.Input, source.Scope.Shape,
                source.Scope.Source, currentStateRequirement.Capabilities.Single(item => item.Capability == MaterializationCapabilityKind.SourceChangeDelivery),
                profile, new(limits.Single(limit => limit.Kind == MaterializationLimitKind.ReadItems).Maximum,
                    limits.Single(limit => limit.Kind == MaterializationLimitKind.ReadBytes).Maximum,
                    "relations-physical-plan/" + source.Scope.PhysicalPlan.Value));
            if (!result.IsSuccessful) throw new ArgumentException(string.Join("; ", result.Diagnostics), nameof(currentStateRequirement));
            CurrentStateEnrichment = result.Plan;
            profile = result.Profile!;
            if (result.Plan!.Strategy == MaterializationCurrentStateEnrichmentStrategyKind.BatchedIdentityRead)
                enricher = new(result.Plan, currentStateReader!);
        }
        Descriptor = new(source.Descriptor.RelationReader, profile);
    }
    /// <inheritdoc />
    public MaterializationCurrentStateEnrichmentPlan? CurrentStateEnrichment { get; }
    /// <inheritdoc />
    public MaterializationQuerySourceDescriptor Descriptor { get; }
    /// <inheritdoc />
    public ValueTask<MaterializationSourcePage> ReadPageAsync(OperationContext context, MaterializationSourcePageRequest request) => source.ReadPageAsync(context, request);
    /// <inheritdoc />
    public ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(OperationContext context, MaterializationSourceScope scope) => source.CaptureCurrentPositionAsync(context, scope);
    /// <inheritdoc />
    public ValueTask<MaterializationSourcePosition> CaptureRetainedStartPositionAsync(OperationContext context, MaterializationSourceScope scope) => source.CaptureRetainedStartPositionAsync(context, scope);
    /// <inheritdoc />
    public async ValueTask<MaterializationChangePage> ReadChangesAsync(OperationContext context, MaterializationChangeReadRequest request)
    {
        var page = await source.ReadChangesAsync(context, request).ConfigureAwait(false);
        return enricher is null || page.Deliveries.IsDefaultOrEmpty ? page
            : await enricher.EnrichAsync(context, request, page).ConfigureAwait(false);
    }
    /// <inheritdoc />
    public ValueTask<MaterializationSourceSettlementResult> SettleAsync(OperationContext context, MaterializationSourceSettlementRequest request) => source.SettleAsync(context, request);
}
