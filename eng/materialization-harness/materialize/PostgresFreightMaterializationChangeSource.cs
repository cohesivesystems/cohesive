using System.Collections.Immutable;
using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>
/// Composes an official PostgreSQL logical-replication source with exact auxiliary Relations impact-read evidence.
/// </summary>
public sealed class PostgresFreightMaterializationChangeSource :
    IMaterializationRetainedChangeSource,
    IMaterializationSettlingSource,
    IMaterializationCurrentStateEnrichmentSource
{
    readonly PostgresLogicalReplicationMaterializationChangeSource source;
    readonly MaterializationCurrentStateEnricher? currentStateEnricher;

    /// <summary>Creates one complete freight source capability closure.</summary>
    /// <param name="source">Official baseline, logical-replication, retained-history, and settlement source.</param>
    /// <param name="requirement">Complete canonical requirement for this acquisition input.</param>
    /// <param name="impactEvidenceReference">Exact auxiliary Relations impact-plan evidence.</param>
    /// <param name="currentStateScope">
    /// Optional exact aggregate scope whose physical change signals require current-state enrichment. Non-root inputs
    /// omit this composition.
    /// </param>
    /// <param name="currentStateReader">
    /// Optional authoritative aggregate reader used to reconcile root-row WAL signals with current owned-component
    /// state. Non-root inputs omit this composition.
    /// </param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A missing capability cannot be supplied by auxiliary impact reads.</exception>
    public PostgresFreightMaterializationChangeSource(
        PostgresLogicalReplicationMaterializationChangeSource source,
        MaterializationSourceRequirement requirement,
        string impactEvidenceReference,
        MaterializationSourceScope? currentStateScope = null,
        MaterializationObservationReader? currentStateReader = null)
    {
        this.source = Guard.RequireNotNull(source);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentException.ThrowIfNullOrWhiteSpace(impactEvidenceReference);
        if ((currentStateScope is null) != (currentStateReader is null))
        {
            throw new ArgumentException(
                "Aggregate current-state enrichment requires both its exact scope and reader.",
                nameof(currentStateReader));
        }
        var baseProfile = source.Descriptor.CapabilityProfile;
        var evidence = baseProfile.Evidence
            .Select(item => new MaterializationCapabilityEvidence(
                id: item.Id,
                capability: item.Capability,
                realization: item.Realization,
                guarantees: item.Guarantees,
                operatingLimits: item.OperatingLimits,
                sourceReferences:
                [
                    "cohesive.adapters.postgres/logical-replication-source/v1",
                    impactEvidenceReference
                ],
                description: item.Description))
            .ToImmutableArray()
            .ToBuilder();
        foreach (var capability in requirement.Capabilities)
        {
            if (MaterializationCapabilityMatcher.Match([capability], baseProfile).IsSatisfied)
                continue;
            if (capability.Capability is not (
                    MaterializationCapabilityKind.SourceBatchedPointRead
                    or MaterializationCapabilityKind.SourceParameterizedPredicateQuery))
            {
                throw new ArgumentException(
                    $"The PostgreSQL freight source cannot satisfy capability '{capability.Capability}'.",
                    nameof(requirement));
            }
            evidence.Add(new(
                id: new($"materialization-harness/postgres-impact/{Uri.EscapeDataString(capability.Id.Value)}/v1"),
                capability: capability.Capability,
                realization: CapabilityRealizationKind.Composed,
                guarantees: capability.Guarantees,
                operatingLimits: capability.OperatingLimits,
                sourceReferences:
                [
                    "cohesive.adapters.postgres/relation-query-source/v1",
                    impactEvidenceReference
                ],
                description: "The official PostgreSQL Relations reader executes the exact bounded identity or relationship predicate impact lookup."));
        }
        MaterializationCapabilityRequirement? currentStateReadRequirement = null;
        if (currentStateScope is not null)
        {
            var boundedRead = requirement.Capabilities
                .Where(capability => capability.Capability is MaterializationCapabilityKind.SourceBatchedPointRead
                    or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                    or MaterializationCapabilityKind.SourceBoundedEnumeration)
                .OrderBy(static capability => capability.Id.Value, StringComparer.Ordinal)
                .First();
            currentStateReadRequirement = new(
                id: new($"{requirement.Input.Value}/current-state-enrichment/read"),
                capability: MaterializationCapabilityKind.SourceBatchedPointRead,
                guarantees:
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.RequestLocalCompleteness
                ],
                operatingLimits: boundedRead.OperatingLimits,
                modes: MaterializationSynchronizationMode.Incremental);
            MaterializationCapabilityProfile availableReads = new(
                id: new($"{baseProfile.Id.Value}/current-state-read-candidates/v1"),
                role: baseProfile.Role,
                subject: baseProfile.Subject,
                evidence: evidence.ToImmutable());
            if (!MaterializationCapabilityMatcher.Match([currentStateReadRequirement], availableReads).IsSatisfied)
            {
                evidence.Add(new(
                    id: new($"materialization-harness/postgres-current-state/{Uri.EscapeDataString(requirement.Input.Value)}/read/v1"),
                    capability: MaterializationCapabilityKind.SourceBatchedPointRead,
                    realization: CapabilityRealizationKind.Composed,
                    guarantees: currentStateReadRequirement.Guarantees,
                    operatingLimits: currentStateReadRequirement.OperatingLimits,
                    sourceReferences:
                    [
                        "cohesive.adapters.postgres/relation-query-source/v1",
                        impactEvidenceReference
                    ],
                    description: "The official PostgreSQL Relations reader executes complete bounded aggregate identity lookup for current-state enrichment."));
            }
        }
        MaterializationCapabilityProfile profile = new(
            id: new($"materialization-harness/postgres/{Uri.EscapeDataString(baseProfile.Subject)}/{Uri.EscapeDataString(requirement.Input.Value)}/freight-impact/v1"),
            role: baseProfile.Role,
            subject: baseProfile.Subject,
            evidence: evidence.ToImmutable(),
            description: "Partition-portable PostgreSQL feed capabilities composed from an exact preflighted dedicated-slot source and auxiliary Relations impact reads; slot affinity remains authenticated by provider positions and settlement.");
        if (currentStateScope is not null)
        {
            if (currentStateScope.Input != requirement.Input
                || currentStateScope.Source != source.Descriptor.Source)
            {
                throw new ArgumentException(
                    "Aggregate current-state enrichment must belong to the exact PostgreSQL source requirement.",
                    nameof(currentStateScope));
            }
            var maximumItems = currentStateReadRequirement!.OperatingLimits.Single(limit =>
                limit.Kind == MaterializationLimitKind.ReadItems).Maximum;
            var maximumBytes = currentStateReadRequirement.OperatingLimits.Single(limit =>
                limit.Kind == MaterializationLimitKind.ReadBytes).Maximum;
            var changeDelivery = requirement.Capabilities.Single(capability =>
                capability.Capability == MaterializationCapabilityKind.SourceChangeDelivery);
            var compilation = MaterializationCurrentStateEnrichmentCompiler.Compile(
                input: requirement.Input,
                shape: currentStateScope.Shape,
                source: currentStateScope.Source,
                changeRequirement: changeDelivery,
                profile: profile,
                policy: new(
                    maximumIdentitiesPerRead: maximumItems,
                    maximumReadBytes: maximumBytes,
                    evidenceReference: $"{impactEvidenceReference}/current-aggregate-state/v1"));
            if (!compilation.IsSuccessful)
            {
                throw new ArgumentException(
                    string.Join(" ", compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                    nameof(requirement));
            }
            profile = compilation.Profile!;
            CurrentStateEnrichment = compilation.Plan;
            currentStateEnricher = new(
                plan: compilation.Plan!,
                reader: currentStateReader!);
        }
        Descriptor = new(
            relationReader: source.Descriptor.RelationReader,
            capabilityProfile: profile);
    }

    /// <inheritdoc />
    public MaterializationQuerySourceDescriptor Descriptor { get; }

    /// <inheritdoc />
    public MaterializationCurrentStateEnrichmentPlan? CurrentStateEnrichment { get; }

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePage> ReadPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request) => source.ReadPageAsync(context, request);

    /// <inheritdoc />
    public async ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope)
    {
        try
        {
            return await source.CaptureCurrentPositionAsync(context, scope).ConfigureAwait(false);
        }
        catch (PostgresLogicalReplicationException exception)
        {
            throw Explain(exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationSourcePosition> CaptureRetainedStartPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope)
    {
        try
        {
            return await source.CaptureRetainedStartPositionAsync(context, scope).ConfigureAwait(false);
        }
        catch (PostgresLogicalReplicationException exception)
        {
            throw Explain(exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationChangePage> ReadChangesAsync(
        OperationContext context,
        MaterializationChangeReadRequest request)
    {
        try
        {
            var page = await source.ReadChangesAsync(context, request).ConfigureAwait(false);
            return currentStateEnricher is null || page.Deliveries.IsDefaultOrEmpty
                ? page
                : await currentStateEnricher.EnrichAsync(context, request, page).ConfigureAwait(false);
        }
        catch (PostgresLogicalReplicationException exception)
        {
            throw Explain(exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationSourceSettlementResult> SettleAsync(
        OperationContext context,
        MaterializationSourceSettlementRequest request)
    {
        try
        {
            return await source.SettleAsync(context, request).ConfigureAwait(false);
        }
        catch (PostgresLogicalReplicationException exception)
        {
            throw Explain(exception);
        }
    }

    static InvalidOperationException Explain(PostgresLogicalReplicationException exception)
    {
        var operation = exception.Observation;
        var health = exception.Health is null
            ? "unavailable"
            : exception.Health.State.ToString();
        return new(
            $"PostgreSQL logical replication failed closed: failure={exception.FailureKind}; "
            + $"operation={operation.Operation}; attempt={operation.Attempt}; "
            + $"evidence={operation.EvidenceReference}; health={health}.",
            exception);
    }
}
