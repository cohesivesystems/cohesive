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
    IMaterializationSettlingSource
{
    readonly PostgresLogicalReplicationMaterializationChangeSource source;
    readonly MaterializationImpactObservationReader? currentRootReader;

    /// <summary>Creates one complete freight source capability closure.</summary>
    /// <param name="source">Official baseline, logical-replication, retained-history, and settlement source.</param>
    /// <param name="requirement">Complete canonical requirement for this acquisition input.</param>
    /// <param name="impactEvidenceReference">Exact auxiliary Relations impact-plan evidence.</param>
    /// <param name="currentRootReader">
    /// Optional authoritative aggregate reader used to reconcile root-row WAL signals with current owned-component
    /// state. Non-root inputs omit this composition.
    /// </param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A missing capability cannot be supplied by auxiliary impact reads.</exception>
    public PostgresFreightMaterializationChangeSource(
        PostgresLogicalReplicationMaterializationChangeSource source,
        MaterializationSourceRequirement requirement,
        string impactEvidenceReference,
        MaterializationImpactObservationReader? currentRootReader = null)
    {
        this.source = Guard.RequireNotNull(source);
        this.currentRootReader = currentRootReader;
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentException.ThrowIfNullOrWhiteSpace(impactEvidenceReference);
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
        Descriptor = new(
            relationReader: source.Descriptor.RelationReader,
            capabilityProfile: new(
                id: new($"materialization-harness/postgres/{Uri.EscapeDataString(baseProfile.Subject)}/{Uri.EscapeDataString(requirement.Input.Value)}/freight-impact/v1"),
                role: baseProfile.Role,
                subject: baseProfile.Subject,
                evidence: evidence.ToImmutable(),
                description: "Partition-portable PostgreSQL feed capabilities composed from an exact preflighted dedicated-slot source and auxiliary Relations impact reads; slot affinity remains authenticated by provider positions and settlement."));
    }

    /// <inheritdoc />
    public MaterializationQuerySourceDescriptor Descriptor { get; }

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
            return currentRootReader is null || page.Deliveries.IsDefaultOrEmpty
                ? page
                : await ReconcileCurrentRootsAsync(context, request, page).ConfigureAwait(false);
        }
        catch (PostgresLogicalReplicationException exception)
        {
            throw Explain(exception);
        }
    }

    async ValueTask<MaterializationChangePage> ReconcileCurrentRootsAsync(
        OperationContext context,
        MaterializationChangeReadRequest request,
        MaterializationChangePage page)
    {
        HashSet<string> requestedIdentities = new(StringComparer.Ordinal);
        foreach (var delivery in page.Deliveries)
            requestedIdentities.Add(delivery.Change.SubjectIdentity);
        var identities = requestedIdentities.Order(StringComparer.Ordinal).ToImmutableArray();
        var result = await currentRootReader!(
                context,
                new(
                    kind: MaterializationImpactObservationReadKind.IdentityLookup,
                    input: request.Scope.Input,
                    shape: request.Scope.Shape,
                    logicalPartition: request.Scope.LogicalPartition,
                    keys: identities,
                    maximumRows: identities.Length,
                    maximumBytes: request.MaximumBytes))
            .ConfigureAwait(false);
        if (result.State is not (RelationQuerySourceReadState.Complete or RelationQuerySourceReadState.NotFound))
        {
            throw new InvalidOperationException(
                $"PostgreSQL aggregate current-state reconciliation returned '{result.State}' instead of complete evidence "
                + $"('{result.EvidenceReference}').");
        }
        var currentByIdentity = result.Observations.ToImmutableDictionary(
            static observation => observation.Identity,
            StringComparer.Ordinal);
        if (currentByIdentity.Keys.Any(identity => !requestedIdentities.Contains(identity)))
        {
            throw new InvalidOperationException(
                "PostgreSQL aggregate current-state reconciliation returned an unrequested root observation.");
        }

        var deliveries = page.Deliveries.ToBuilder();
        for (var index = 0; index < deliveries.Count; index++)
        {
            var delivery = deliveries[index];
            var change = delivery.Change;
            var identity = change.SubjectIdentity;
            currentByIdentity.TryGetValue(identity, out var current);
            var reconciled = new MaterializationChangeEnvelope(
                id: change.Id,
                subjectIdentity: change.SubjectIdentity,
                scope: change.Scope,
                shape: change.Shape,
                position: change.Position,
                kind: current is null ? MaterializationChangeKind.Delete : MaterializationChangeKind.Upsert,
                before: null,
                after: current,
                occurredAtUtc: change.OccurredAtUtc,
                observedAtUtc: change.ObservedAtUtc,
                evidenceReference: change.EvidenceReference is null
                    ? "materialization-harness/postgres/current-aggregate-root/v1"
                    : $"{change.EvidenceReference}/current-aggregate-root/v1");
            deliveries[index] = new(
                id: delivery.Id,
                change: reconciled,
                deliveredAtUtc: delivery.DeliveredAtUtc,
                evidenceReference: delivery.EvidenceReference);
        }
        return new(
            deliveries: deliveries.ToImmutable(),
            throughPosition: page.ThroughPosition,
            state: page.State);
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
