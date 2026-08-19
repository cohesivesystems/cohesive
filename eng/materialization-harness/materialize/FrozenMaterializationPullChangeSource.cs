using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>
/// Harness-only positioned change source for an explicitly frozen seed fixture.
/// </summary>
/// <remarks>
/// The wrapper delegates bounded reads to an official provider source and adds complete empty change-delivery
/// evidence only for the interval in which the harness seed is contractually immutable. It is not a production
/// PostgreSQL logical-replication or Cosmos change-feed realization and must not be used while source writes occur.
/// </remarks>
public sealed class FrozenMaterializationPullChangeSource : IMaterializationPullChangeSource
{
    const int PositionFormatVersion = 1;
    const string PositionValue = "materialization-harness/frozen-seed/end/v1";
    readonly IMaterializationSource source;
    readonly MaterializationSourceScope scope;

    /// <summary>Creates a frozen-fixture change source over one official provider baseline source.</summary>
    /// <param name="source">Official provider source used for bounded relation reads.</param>
    /// <param name="scope">Only exact tenant, input, physical partition, and ordering scope accepted by the wrapper.</param>
    /// <param name="requirements">Complete canonical requirements for the exact acquisition input.</param>
    /// <param name="providerEvidenceReference">Stable evidence identifying the frozen provider fixture.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="scope"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Requirements contain null entries, the provider evidence reference is absent, or an unsupported missing
    /// provider capability would have to be invented.
    /// </exception>
    public FrozenMaterializationPullChangeSource(
        IMaterializationSource source,
        MaterializationSourceScope scope,
        ImmutableArray<MaterializationCapabilityRequirement> requirements,
        string providerEvidenceReference)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        if (source.Descriptor.Source != scope.Source
            || source.Descriptor.RelationReader.Descriptor.LogicalPartition != scope.LogicalPartition)
        {
            throw new ArgumentException(
                "The frozen source must implement the exact physical source and logical tenant partition.",
                nameof(scope));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(providerEvidenceReference);
        var normalized = requirements.IsDefault ? [] : requirements;
        if (normalized.Any(static requirement => requirement is null))
            throw new ArgumentException("Frozen source requirements cannot contain null entries.", nameof(requirements));
        var evidence = source.Descriptor.CapabilityProfile.Evidence.ToBuilder();
        foreach (var requirement in normalized)
        {
            if (MaterializationCapabilityMatcher.Match(
                    requirements: [requirement],
                    profile: source.Descriptor.CapabilityProfile)
                .IsSatisfied)
                continue;
            if (requirement.Capability is not (MaterializationCapabilityKind.SourceChangeDelivery
                    or MaterializationCapabilityKind.SourceBoundedEnumeration
                    or MaterializationCapabilityKind.SourceBatchedPointRead
                    or MaterializationCapabilityKind.SourceParameterizedPredicateQuery))
            {
                throw new ArgumentException(
                    $"The official provider source does not realize required capability '{requirement.Capability}'.",
                    nameof(requirements));
            }
            evidence.Add(new(
                id: new($"materialization-harness/frozen/{Uri.EscapeDataString(requirement.Id.Value)}/v1"),
                capability: requirement.Capability,
                realization: CapabilityRealizationKind.Composed,
                guarantees: requirement.Guarantees,
                operatingLimits: requirement.OperatingLimits,
                sourceReferences:
                [
                    providerEvidenceReference,
                    $"relations-physical-plan/{scope.PhysicalPlan.Algorithm}/{scope.PhysicalPlan.Canonicalization}/{scope.PhysicalPlan.Value}",
                    "materialization-harness/frozen-seed-contract/v1"
                ],
                description: requirement.Capability switch
                {
                    MaterializationCapabilityKind.SourceChangeDelivery =>
                        "The local seed is frozen for the attempt, so its complete change interval is provably empty.",
                    MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                        when requirement.Modes == MaterializationSynchronizationMode.Incremental =>
                        "The frozen fixture cannot emit contributor changes, so this inverse impact path is retained but unreachable.",
                    _ =>
                        "The official provider Relations reader executes this acquisition through the deterministic in-memory reconciliation pager."
                }));
        }
        var baseProfile = source.Descriptor.CapabilityProfile;
        Descriptor = new(
            relationReader: source.Descriptor.RelationReader,
            capabilityProfile: new(
                id: new($"{baseProfile.Id.Value}/frozen-seed/v1"),
                role: baseProfile.Role,
                subject: baseProfile.Subject,
                evidence: evidence.ToImmutable(),
                description: "Official provider reads plus a harness-only complete empty change interval over frozen seed data."));
    }

    /// <inheritdoc />
    public MaterializationQuerySourceDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePage> ReadPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireScope(context, request.Scope);
        return source.ReadPageAsync(context, request);
    }

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope)
    {
        RequireScope(context, scope);
        return ValueTask.FromResult(new MaterializationSourcePosition(
            formatVersion: PositionFormatVersion,
            scope: scope,
            value: PositionValue));
    }

    /// <inheritdoc />
    public ValueTask<MaterializationChangePage> ReadChangesAsync(
        OperationContext context,
        MaterializationChangeReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireScope(context, request.Scope);
        if (request.AfterPosition.FormatVersion != PositionFormatVersion
            || !string.Equals(request.AfterPosition.Value, PositionValue, StringComparison.Ordinal))
        {
            throw new ArgumentException("The frozen source position is incompatible with this fixture.", nameof(request));
        }
        MaterializationCapabilityLimits.RequireSupportedBounds(
            profile: Descriptor.CapabilityProfile,
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            itemLimitKind: MaterializationLimitKind.ChangeItems,
            requestedItems: request.MaximumDeliveries,
            byteLimitKind: MaterializationLimitKind.ReadBytes,
            requestedBytes: request.MaximumBytes,
            parameterName: nameof(request));
        return ValueTask.FromResult(new MaterializationChangePage(
            deliveries: [],
            throughPosition: request.AfterPosition,
            state: MaterializationChangePageState.CaughtUp));
    }

    void RequireScope(OperationContext context, MaterializationSourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);
        context.ThrowIfCancellationRequested();
        if (scope != this.scope)
        {
            throw new ArgumentException(
                "The frozen source scope differs from its exact input, tenant, physical partition, or ordering scope.",
                nameof(scope));
        }
    }
}
