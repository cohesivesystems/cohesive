using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Cosmos;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Microsoft.Azure.Cosmos;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>
/// Emulator-compatible pull source over the complete deterministic Cosmos change envelopes written with entities.
/// </summary>
/// <remarks>
/// This harness interpretation deliberately does not claim Cosmos full-fidelity change-feed capability. It reads the
/// explicit ARI-433 envelope schema through bounded tenant-partition queries and retains exact before/after evidence.
/// </remarks>
public sealed class CosmosScenarioEnvelopeMaterializationChangeSource : IMaterializationPullChangeSource
{
    const int PositionFormatVersion = 1;
    const string PositionPrefix = "cosmos-scenario-envelope/v1/";
    readonly IMaterializationSource baseline;
    readonly Container container;
    readonly MaterializationSourceScope scope;
    readonly RelationQuerySourcePlacementBinding placement;
    readonly string scenarioId;
    readonly string entityKind;
    readonly long baselineThroughSequence;
    readonly JsonSerializerOptions observationJson = CosmosSystemTextJsonSerializer.CreateDefaultOptions();

    /// <summary>Creates one exact tenant/input envelope source.</summary>
    /// <param name="baseline">Official provider source used for bounded baseline reads.</param>
    /// <param name="container">Cosmos container retaining entity and change-envelope documents.</param>
    /// <param name="scope">Exact tenant, acquisition input, physical plan, placement, and ordering scope.</param>
    /// <param name="placement">Exact placement fields projected into canonical change observations.</param>
    /// <param name="requirement">Complete materialization capability requirements for the acquisition input.</param>
    /// <param name="journal">Canonical deterministic scenario authority.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Scope, placement, journal, or capability evidence is inconsistent.</exception>
    public CosmosScenarioEnvelopeMaterializationChangeSource(
        IMaterializationSource baseline,
        Container container,
        MaterializationSourceScope scope,
        RelationQuerySourcePlacementBinding placement,
        MaterializationSourceRequirement requirement,
        FreightScenarioJournal journal)
    {
        this.baseline = Guard.RequireNotNull(baseline);
        this.container = Guard.RequireNotNull(container);
        this.scope = Guard.RequireNotNull(scope);
        this.placement = Guard.RequireNotNull(placement);
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(journal);
        if (scope.Input != requirement.Input
            || scope.Placement.Id != placement.Id
            || scope.Shape != placement.Shape
            || scope.Source != baseline.Descriptor.Source
            || scope.LogicalPartition != baseline.Descriptor.RelationReader.Descriptor.LogicalPartition)
        {
            throw new ArgumentException(
                "The Cosmos envelope source must retain one exact scope, placement, source, and logical partition.",
                nameof(scope));
        }
        scenarioId = journal.ScenarioId;
        baselineThroughSequence = journal.BaselineThroughSequence;
        entityKind = EntityKind(placement.Shape);
        Descriptor = new(
            relationReader: baseline.Descriptor.RelationReader,
            capabilityProfile: CreateCapabilityProfile(baseline, requirement, scope));
    }

    /// <inheritdoc />
    public MaterializationQuerySourceDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePage> ReadPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request)
    {
        RequireScope(context, request?.Scope);
        return baseline.ReadPageAsync(context, request!);
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope)
    {
        RequireScope(context, scope);
        var sequence = baselineThroughSequence;
        using var iterator = container.GetItemQueryIterator<long>(
            new QueryDefinition(
                    "SELECT VALUE c.sequence FROM c WHERE c.documentKind = @documentKind AND c.schemaVersion = @schemaVersion AND c.scenarioId = @scenarioId AND c.entityKind = @entityKind ORDER BY c.sequence DESC OFFSET 0 LIMIT 1")
                .WithParameter("@documentKind", FreightMaterializationChangeFeedConventions.CosmosEnvelopeDocumentKind)
                .WithParameter("@schemaVersion", FreightMaterializationChangeFeedConventions.CosmosEnvelopeSchemaVersion)
                .WithParameter("@scenarioId", scenarioId)
                .WithParameter("@entityKind", entityKind),
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(Tenant(scope)),
                MaxItemCount = 1,
                MaxBufferedItemCount = 1,
                MaxConcurrency = 1
            });
        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(context.CancellationToken).ConfigureAwait(false);
            if (response.Count != 0)
                sequence = Math.Max(sequence, response.Single());
        }
        return Position(scope, sequence);
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationChangePage> ReadChangesAsync(
        OperationContext context,
        MaterializationChangeReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireScope(context, request.Scope);
        MaterializationCapabilityLimits.RequireSupportedBounds(
            profile: Descriptor.CapabilityProfile,
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            itemLimitKind: MaterializationLimitKind.ChangeItems,
            requestedItems: request.MaximumDeliveries,
            byteLimitKind: MaterializationLimitKind.ReadBytes,
            requestedBytes: request.MaximumBytes,
            parameterName: nameof(request));
        var after = ParsePosition(request.AfterPosition);
        using var iterator = container.GetItemQueryIterator<JsonElement>(
            new QueryDefinition(
                    "SELECT * FROM c WHERE c.documentKind = @documentKind AND c.schemaVersion = @schemaVersion AND c.scenarioId = @scenarioId AND c.entityKind = @entityKind AND c.sequence > @after ORDER BY c.sequence ASC")
                .WithParameter("@documentKind", FreightMaterializationChangeFeedConventions.CosmosEnvelopeDocumentKind)
                .WithParameter("@schemaVersion", FreightMaterializationChangeFeedConventions.CosmosEnvelopeSchemaVersion)
                .WithParameter("@scenarioId", scenarioId)
                .WithParameter("@entityKind", entityKind)
                .WithParameter("@after", after),
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(Tenant(scope)),
                MaxItemCount = request.MaximumDeliveries,
                MaxBufferedItemCount = request.MaximumDeliveries,
                MaxConcurrency = 1
            });
        if (!iterator.HasMoreResults)
        {
            return new(
                deliveries: [],
                throughPosition: request.AfterPosition,
                state: MaterializationChangePageState.CaughtUp);
        }

        var response = await iterator.ReadNextAsync(context.CancellationToken).ConfigureAwait(false);
        var deliveredAtUtc = context.UtcNow;
        long bytes = 0;
        var through = after;
        var stoppedForBytes = false;
        var stoppedForItems = false;
        var deliveries = ImmutableArray.CreateBuilder<MaterializationChangeDelivery>(
            Math.Min(response.Count, request.MaximumDeliveries));
        foreach (var document in response)
        {
            context.ThrowIfCancellationRequested();
            if (deliveries.Count == request.MaximumDeliveries)
            {
                stoppedForItems = true;
                break;
            }
            var encodedBytes = JsonSerializer.SerializeToUtf8Bytes(document).LongLength;
            if (checked(bytes + encodedBytes) > request.MaximumBytes)
            {
                if (deliveries.Count == 0)
                    throw new InvalidOperationException("One Cosmos scenario change envelope exceeds the requested hard byte boundary.");
                stoppedForBytes = true;
                break;
            }
            bytes += encodedBytes;
            var sequence = RequiredInt64(document, "sequence");
            if (sequence <= through)
                throw new InvalidOperationException("Cosmos scenario change envelopes are not strictly source ordered.");
            through = sequence;
            var changePosition = Position(scope, sequence);
            var occurredAtUtc = RequiredDateTimeOffset(document, "occurredAtUtc");
            var observedAtUtc = deliveredAtUtc < occurredAtUtc ? occurredAtUtc : deliveredAtUtc;
            var before = Observation(document, "beforeState");
            var afterObservation = Observation(document, "afterState");
            var operation = RequiredString(document, "operation");
            var change = new MaterializationChangeEnvelope(
                id: new($"{scenarioId}/change/{sequence}"),
                subjectIdentity: RequiredString(document, "entityId"),
                scope: scope,
                shape: scope.Shape,
                position: changePosition,
                kind: operation switch
                {
                    "upsert" when before is null => MaterializationChangeKind.Create,
                    "upsert" => MaterializationChangeKind.Update,
                    "delete" => MaterializationChangeKind.Delete,
                    _ => throw new InvalidOperationException($"Unsupported Cosmos scenario operation '{operation}'.")
                },
                before: before,
                after: afterObservation,
                occurredAtUtc: occurredAtUtc,
                observedAtUtc: observedAtUtc,
                evidenceReference: $"cosmos-scenario-envelope/{scenarioId}/{sequence}");
            deliveries.Add(new(
                id: new(RequiredString(document, "deliveryId")),
                change: change,
                deliveredAtUtc: observedAtUtc,
                evidenceReference: RequiredString(document, "fingerprint")));
        }

        if (deliveries.Count == 0)
        {
            return new(
                deliveries: [],
                throughPosition: request.AfterPosition,
                state: MaterializationChangePageState.CaughtUp);
        }
        return new(
            deliveries: deliveries.MoveToImmutable(),
            throughPosition: Position(scope, through),
            state: stoppedForBytes || stoppedForItems || iterator.HasMoreResults || response.Count >= request.MaximumDeliveries
                ? MaterializationChangePageState.MoreAvailable
                : MaterializationChangePageState.CaughtUp);
    }

    RelationQuerySourceReadObservation? Observation(JsonElement document, string propertyName)
    {
        if (!document.TryGetProperty(propertyName, out var state)
            || state.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        var value = DeserializeState(scope.Shape, state);
        var objectFields = value.Fields
            ?? throw new InvalidOperationException("A Cosmos scenario before/after state is not an object.");
        var fields = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(placement.Fields.Length);
        foreach (var field in placement.Fields)
        {
            if (!TryGetPath(objectFields, field.SemanticPath, out var observed)
                || observed.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
            {
                throw new InvalidOperationException(
                    $"Cosmos scenario state is missing required semantic field '{field.SemanticPath}'.");
            }
            fields.Add(new(
                field: new(
                    input: field.Input,
                    semanticPath: field.SemanticPath,
                    sourceSelector: field.SourceSelector,
                    purpose: RelationQuerySourceReadFieldPurpose.SemanticInput),
                state: RelationQuerySourceReadFieldState.Value,
                value: observed));
        }
        return new(
            identity: RequiredString(document, "entityId"),
            shape: scope.Shape,
            fields: fields.MoveToImmutable());
    }

    ObservationValue DeserializeState(QualifiedShapeId shape, JsonElement state)
    {
        object entity = shape == FreightOrderMaterializationModel.OrderShapeId
            ? state.Deserialize<FreightOrder>(observationJson)!
            : shape == FreightOrderMaterializationModel.CustomerAccountShapeId
                ? state.Deserialize<FreightCustomerAccount>(observationJson)!
                : shape == FreightOrderMaterializationModel.OrderStopShapeId
                    ? state.Deserialize<FreightOrderStop>(observationJson)!
                    : shape == FreightOrderMaterializationModel.LocationShapeId
                        ? state.Deserialize<FreightLocation>(observationJson)!
                        : throw new InvalidOperationException($"Unsupported freight change shape '{shape}'.");
        return ObservationValue.FromObject(entity);
    }

    static bool TryGetPath(
        IReadOnlyDictionary<string, ObservationValue> fields,
        FieldPath path,
        out ObservationValue value)
    {
        value = ObservationValue.Undefined;
        IReadOnlyDictionary<string, ObservationValue>? current = fields;
        for (var index = 0; index < path.Segments.Length; index++)
        {
            var segment = path.Segments[index];
            if (!segment.TryGetFieldIdentity(out var fieldIdentity)
                || current is null
                || !current.TryGetValue(fieldIdentity, out value))
            {
                return false;
            }
            current = index == path.Segments.Length - 1 ? null : value.Fields;
        }
        return true;
    }

    static MaterializationCapabilityProfile CreateCapabilityProfile(
        IMaterializationSource baseline,
        MaterializationSourceRequirement requirement,
        MaterializationSourceScope scope)
    {
        var baseProfile = baseline.Descriptor.CapabilityProfile;
        var evidence = baseProfile.Evidence.ToBuilder();
        foreach (var capability in requirement.Capabilities)
        {
            if (MaterializationCapabilityMatcher.Match([capability], baseProfile).IsSatisfied)
                continue;
            if (capability.Capability is not (
                    MaterializationCapabilityKind.SourceChangeDelivery
                    or MaterializationCapabilityKind.SourceBatchedPointRead
                    or MaterializationCapabilityKind.SourceParameterizedPredicateQuery))
            {
                throw new ArgumentException(
                    $"The Cosmos envelope realization cannot satisfy capability '{capability.Capability}'.",
                    nameof(requirement));
            }
            evidence.Add(new(
                id: new($"cosmos-scenario-envelope/{Uri.EscapeDataString(capability.Id.Value)}/v1"),
                capability: capability.Capability,
                realization: CapabilityRealizationKind.Composed,
                guarantees: capability.Guarantees,
                operatingLimits: capability.OperatingLimits,
                sourceReferences:
                [
                    $"relations-physical-plan/{scope.PhysicalPlan.Algorithm}/{scope.PhysicalPlan.Canonicalization}/{scope.PhysicalPlan.Value}",
                    capability.Capability == MaterializationCapabilityKind.SourceChangeDelivery
                        ? "cohesive.materialization-harness/cosmos-scenario-envelope/v1"
                        : "cohesive.adapters.cosmos/relation-query-source/v1"
                ],
                description: capability.Capability == MaterializationCapabilityKind.SourceChangeDelivery
                    ? "Complete before/after scenario envelopes are transactionally written with local emulator entity changes and pulled in stable journal order."
                    : "The official Cosmos Relations reader executes the exact bounded identity or relationship predicate lookup."));
        }
        return new(
            id: new($"{baseProfile.Id.Value}/scenario-envelope/v1"),
            role: baseProfile.Role,
            subject: baseProfile.Subject,
            evidence: evidence.ToImmutable(),
            description: "Local Cosmos relation reads plus explicit emulator-compatible deterministic change envelopes.");
    }

    MaterializationSourcePosition Position(MaterializationSourceScope scope, long sequence) => new(
        formatVersion: PositionFormatVersion,
        scope: scope,
        value: $"{PositionPrefix}{Uri.EscapeDataString(scenarioId)}/{sequence}");

    long ParsePosition(MaterializationSourcePosition position)
    {
        if (position.Scope != scope
            || position.FormatVersion != PositionFormatVersion
            || !position.Value.StartsWith(PositionPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("The Cosmos scenario position belongs to another schema or source scope.", nameof(position));
        }
        var suffix = position.Value[PositionPrefix.Length..];
        var separator = suffix.LastIndexOf('/');
        if (separator <= 0
            || !string.Equals(Uri.UnescapeDataString(suffix[..separator]), scenarioId, StringComparison.Ordinal)
            || !long.TryParse(
                suffix[(separator + 1)..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var sequence)
            || sequence < baselineThroughSequence)
        {
            throw new ArgumentException("The Cosmos scenario position has incompatible scenario or sequence evidence.", nameof(position));
        }
        return sequence;
    }

    void RequireScope(OperationContext context, MaterializationSourceScope? scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);
        context.ThrowIfCancellationRequested();
        if (scope != this.scope)
            throw new ArgumentException("The Cosmos scenario source scope differs from its exact input or tenant binding.", nameof(scope));
    }

    static string Tenant(MaterializationSourceScope scope)
    {
        const string Prefix = "materialization-harness/freight/tenant/";
        return scope.LogicalPartition.Value.StartsWith(Prefix, StringComparison.Ordinal)
            ? scope.LogicalPartition.Value[Prefix.Length..]
            : throw new InvalidOperationException("The Cosmos scenario source has an unknown logical tenant partition.");
    }

    static string EntityKind(QualifiedShapeId shape) => shape == FreightOrderMaterializationModel.OrderShapeId
        ? "order"
        : shape == FreightOrderMaterializationModel.CustomerAccountShapeId
            ? "customerAccount"
            : shape == FreightOrderMaterializationModel.OrderStopShapeId
                ? "orderStop"
                : shape == FreightOrderMaterializationModel.LocationShapeId
                    ? "location"
                    : throw new ArgumentException($"Unsupported freight change shape '{shape}'.", nameof(shape));

    static string RequiredString(JsonElement document, string property) =>
        document.GetProperty(property).GetString()
        ?? throw new InvalidOperationException($"Cosmos scenario envelope property '{property}' is absent.");

    static long RequiredInt64(JsonElement document, string property) => document.GetProperty(property).GetInt64();

    static DateTimeOffset RequiredDateTimeOffset(JsonElement document, string property) =>
        document.GetProperty(property).GetDateTimeOffset().ToUniversalTime();
}
