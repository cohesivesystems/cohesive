using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Sink for provider-neutral settlements completed by a managed Cosmos materialization source.</summary>
public interface ICosmosManagedMaterializationChangeSourceObserver
{
    /// <summary>Observes one provider checkpoint completed after its exact application checkpoint became durable.</summary>
    /// <param name="observation">Provider-neutral durable progress and settlement evidence.</param>
    /// <remarks>
    /// Different Cosmos leases may invoke the observer concurrently. Implementations must be thread-safe, should
    /// return promptly, and must not throw; adapter-side observation failures are isolated after settlement.
    /// </remarks>
    void Observe(MaterializationChangeSettlementObservation observation);
}

/// <summary>
/// Provider-managed latest-version Cosmos change delivery projected into canonical materialization changes.
/// </summary>
/// <remarks>
/// This realization uses the Cosmos manual-checkpoint processor. A callback is grouped into canonical pages by
/// logical partition key, the ordering boundary Cosmos actually guarantees. Every page position authenticates the
/// callback's provider range and <see cref="Headers.ContinuationToken"/> inside an adapter-owned wire format. All
/// group handlers must return exact durable application progress before the adapter invokes the one SDK checkpoint.
/// A fully filtered callback still delivers one empty <see cref="MaterializationChangePageState.Progressed"/> page
/// and requires the same durable proof. Latest-version callbacks are necessarily upserts: this source does not claim
/// previous images, deletes, full-fidelity history, or a hard callback item or byte bound.
/// </remarks>
public sealed class CosmosManagedMaterializationChangeSource : IMaterializationManagedChangeSource
{
    const int PositionFormatVersion = 1;
    const string PositionPrefix = "cosmos-managed-materialization-change/v1/";
    const string EvidencePrefix = "cohesive.adapters.cosmos/managed-change/v1";
    const string DefaultTestLeaseStoreIdentity = "tests/cosmos-managed/lease-store/default";
    static ReadOnlySpan<byte> PositionAuthenticationDomain =>
        "cohesive.adapters.cosmos/managed-materialization-change/v1\0"u8;
    static readonly JsonSerializerOptions CanonicalJsonOptions = MaterializationJsonSerializer.CreateOptions();

    readonly CosmosRelationQuerySourceReader reader;
    readonly RelationQueryPhysicalPlanFingerprint physicalPlan;
    readonly RelationQuerySourcePlacementBinding placement;
    readonly CosmosManagedMaterializationChangeBinding binding;
    readonly CosmosManagedMaterializationChangeSourcePolicy policy;
    readonly CosmosManagedMaterializationChangeFeedProcessorFactory processorFactory;
    readonly ICosmosManagedMaterializationChangeSourceObserver? observer;
    readonly MaterializationAuthenticatedValueCodec positionCodec;
    readonly ImmutableArray<RelationQuerySourceReadField> projectedFields;
    readonly FieldPath identitySelector;
    readonly string accountFingerprint;
    readonly string bindingDigest;
    readonly string semanticChangeScopeDigest;
    readonly string? fixedPartitionValue;

    /// <summary>Creates a production managed Cosmos materialization change source.</summary>
    /// <param name="reader">Canonical Cosmos Relations reader defining source identity and selector semantics.</param>
    /// <param name="physicalPlan">Exact physical-plan fingerprint authorizing this materialization source.</param>
    /// <param name="placement">Exact canonical source placement projected by callbacks.</param>
    /// <param name="monitoredContainer">Borrowed Cosmos container whose latest-version feed is processed.</param>
    /// <param name="leaseContainer">
    /// Borrowed Cosmos container storing provider-owned processor leases. Its exact physical affinity participates
    /// in capability provenance and processor deployment identity.
    /// </param>
    /// <param name="binding">Explicit entity or outbox envelope filter.</param>
    /// <param name="policy">Processor ownership, initial-position, polling, hint, and position-size policy.</param>
    /// <param name="authenticationKey">
    /// Caller-owned secret used to authenticate managed positions. The source copies it; rotation invalidates
    /// outstanding application checkpoints and requires a new materialization generation.
    /// </param>
    /// <param name="observer">Optional provider-neutral settlement observer.</param>
    /// <exception cref="ArgumentNullException">
    /// A reference argument is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The reader, placement, binding, selectors, monitored or lease container, or authentication key conflict, or
    /// the lease database and container identifiers equal the monitored database and container identifiers.
    /// </exception>
    public CosmosManagedMaterializationChangeSource(
        CosmosRelationQuerySourceReader reader,
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        RelationQuerySourcePlacementBinding placement,
        Container monitoredContainer,
        Container leaseContainer,
        CosmosManagedMaterializationChangeBinding binding,
        CosmosManagedMaterializationChangeSourcePolicy policy,
        ReadOnlySpan<byte> authenticationKey,
        ICosmosManagedMaterializationChangeSourceObserver? observer = null)
        : this(
            reader: reader,
            physicalPlan: physicalPlan,
            placement: placement,
            binding: binding,
            policy: policy,
            processorFactory: CreateProductionProcessorFactory(
                reader: reader,
                monitoredContainer: monitoredContainer,
                leaseContainer: leaseContainer,
                policy: policy),
            authenticationKey: authenticationKey,
            observer: observer,
            leaseStoreIdentity: ComputeLeaseStoreIdentity(leaseContainer: leaseContainer))
    {
    }

    /// <summary>Creates a managed source over a narrow processor seam for deterministic conformance tests.</summary>
    internal CosmosManagedMaterializationChangeSource(
        CosmosRelationQuerySourceReader reader,
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        RelationQuerySourcePlacementBinding placement,
        CosmosManagedMaterializationChangeBinding binding,
        CosmosManagedMaterializationChangeSourcePolicy policy,
        ICosmosManagedMaterializationChangeFeedProcessor processor,
        ReadOnlySpan<byte> authenticationKey,
        ICosmosManagedMaterializationChangeSourceObserver? observer = null)
        : this(
            reader: reader,
            physicalPlan: physicalPlan,
            placement: placement,
            binding: binding,
            policy: policy,
            processorFactory: _ => Guard.RequireNotNull(processor),
            authenticationKey: authenticationKey,
            observer: observer,
            leaseStoreIdentity: DefaultTestLeaseStoreIdentity)
    {
    }

    /// <summary>Creates a managed source over a processor factory for lease-namespace conformance tests.</summary>
    internal CosmosManagedMaterializationChangeSource(
        CosmosRelationQuerySourceReader reader,
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        RelationQuerySourcePlacementBinding placement,
        CosmosManagedMaterializationChangeBinding binding,
        CosmosManagedMaterializationChangeSourcePolicy policy,
        CosmosManagedMaterializationChangeFeedProcessorFactory processorFactory,
        ReadOnlySpan<byte> authenticationKey,
        ICosmosManagedMaterializationChangeSourceObserver? observer = null,
        string leaseStoreIdentity = DefaultTestLeaseStoreIdentity)
    {
        this.reader = Guard.RequireNotNull(reader);
        this.physicalPlan = Guard.RequireNotNull(physicalPlan);
        this.placement = Guard.RequireNotNull(placement);
        this.binding = Guard.RequireNotNull(binding);
        this.policy = Guard.RequireNotNull(policy);
        this.processorFactory = Guard.RequireNotNull(processorFactory);
        this.observer = observer;
        leaseStoreIdentity = Guard.RequireNotNullOrWhiteSpace(leaseStoreIdentity);
        if (authenticationKey.Length < MaterializationAuthenticatedValueCodec.MinimumAuthenticationKeyBytes)
        {
            throw new ArgumentException(
                message: $"Managed Cosmos position authentication requires at least {MaterializationAuthenticatedValueCodec.MinimumAuthenticationKeyBytes} secret bytes.",
                paramName: nameof(authenticationKey));
        }

        ValidatePlacement(reader: reader, placement: placement, binding: binding);
        identitySelector = CosmosRelationQuerySourceSelectors.RequirePropertyPath(
            selector: reader.IdentitySourceSelector,
            parameterName: nameof(reader));
        RequireSupportedSelector(selector: identitySelector, parameterName: nameof(reader));
        projectedFields = CreateProjectedFields(reader: reader, placement: placement);
        fixedPartitionValue = reader.Policy.FixedPartitionKey is { } fixedPartition
            ? GetStringPartitionValue(partitionKey: fixedPartition, parameterName: nameof(reader))
            : null;
        accountFingerprint = CosmosPhysicalAffinity.Fingerprint(reader.AccountEndpoint);
        bindingDigest = ComputeBindingDigest(reader: reader, binding: binding);
        var placementDigest = CosmosMaterializationIdentity.ComputePlacementFingerprint(placement: placement);
        ProcessorNamespace = ComputeProcessorNamespace(
            reader: reader,
            physicalPlan: physicalPlan,
            placementDigest: placementDigest,
            bindingDigest: bindingDigest,
            leaseStoreIdentity: leaseStoreIdentity,
            processorNameSeed: policy.ProcessorName,
            initialPosition: policy.InitialPosition,
            initialTimeUtc: policy.InitialTimeUtc);
        semanticChangeScopeDigest = HashParts(
            values:
            [
                reader.Descriptor.Source.Value,
                accountFingerprint,
                reader.DatabaseId,
                reader.ContainerId,
                physicalPlan.Algorithm,
                physicalPlan.Canonicalization,
                physicalPlan.Value,
                placementDigest,
                bindingDigest
            ]);
        positionCodec = new(
            formatPrefix: PositionPrefix,
            authenticationDomain: PositionAuthenticationDomain,
            authenticationKey: authenticationKey,
            maximumValueCharacters: policy.MaximumPositionCharacters);
        Descriptor = new(
            source: reader.Descriptor.Source,
            executionDomain: reader.Descriptor.ExecutionDomain,
            capabilityProfile: CreateCapabilityProfile(
                reader: reader,
                physicalPlan: physicalPlan,
                placementDigest: placementDigest,
                bindingDigest: bindingDigest,
                processorNamespace: ProcessorNamespace,
                leaseStoreIdentity: leaseStoreIdentity,
                policy: policy));
    }

    /// <inheritdoc />
    public MaterializationSourceDescriptor Descriptor { get; }

    /// <summary>
    /// Stable binding, lease-store, and initial-boundary namespace for request-specific processor names.
    /// </summary>
    /// <remarks>
    /// The namespace excludes ephemeral worker ownership, polling, and page-size hints. It includes initial-position
    /// policy because workers with different first-lease boundaries must not race to initialize the same deployment.
    /// The effective SDK name additionally includes materialization, exact definition fingerprint, and generation.
    /// </remarks>
    public string ProcessorNamespace { get; }

    /// <summary>Gets the stable Cosmos processor deployment name for one exact managed execution.</summary>
    /// <param name="request">Materialization, definition fingerprint, and generation owning the provider leases.</param>
    /// <returns>
    /// A deterministic deployment name shared by workers executing the same request and distinct for a different
    /// materialization, definition fingerprint, generation, semantic source binding, or lease-store binding.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public string GetEffectiveProcessorName(MaterializationManagedChangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.Concat(
            "cohesive-materialization-",
            HashParts(
                values:
                [
                    ProcessorNamespace,
                    request.Materialization.Value,
                    request.DefinitionFingerprint.Algorithm,
                    request.DefinitionFingerprint.Canonicalization,
                    request.DefinitionFingerprint.Value,
                    request.Generation.Value
                ]));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Exceptions raised by <paramref name="handler"/> propagate unchanged after leaving the provider position
    /// unsettled. Settlement-observer failures are isolated because observation occurs after provider checkpointing.
    /// </remarks>
    /// <exception cref="CosmosException">
    /// Cosmos fails to start, read, checkpoint, or stop the change-feed processor.
    /// </exception>
    public Task RunAsync(
        OperationContext context,
        MaterializationManagedChangeRequest request,
        MaterializationManagedChangeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(handler);
        context.CancellationToken.ThrowIfCancellationRequested();
        var processor = processorFactory(GetEffectiveProcessorName(request: request));
        return processor.RunAsync(
            handler: (batch, cancellationToken) => HandleBatchAsync(
                context: context,
                request: request,
                handler: handler,
                batch: batch,
                cancellationToken: cancellationToken),
            cancellationToken: context.CancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="CosmosException">Cosmos fails while reading change-feed estimator state.</exception>
    public async IAsyncEnumerable<MaterializationChangeLagObservation> ObserveLagAsync(
        OperationContext context,
        MaterializationManagedChangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var processor = processorFactory(GetEffectiveProcessorName(request: request));
        await foreach (var lag in processor.ObserveLagAsync(
            cancellationToken: context.CancellationToken).ConfigureAwait(false))
        {
            var observedAtUtc = context.UtcNow.ToUniversalTime();
            yield return new(
                request: request,
                source: Descriptor.Source,
                scope: null,
                estimateState: lag.EstimatedPendingProviderWork.HasValue
                    ? MaterializationChangeLagEstimateState.Estimated
                    : MaterializationChangeLagEstimateState.Unavailable,
                estimatedPendingProviderWork: lag.EstimatedPendingProviderWork,
                observedAtUtc: observedAtUtc,
                evidenceReference: lag.EvidenceReference);
        }
    }

    async Task HandleBatchAsync(
        OperationContext context,
        MaterializationManagedChangeRequest request,
        MaterializationManagedChangeHandler handler,
        CosmosManagedMaterializationProviderBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken,
            cancellationToken);
        var callbackContext = context.WithCancellationToken(linkedCancellation.Token);
        callbackContext.CancellationToken.ThrowIfCancellationRequested();
        var deliveredAtUtc = callbackContext.UtcNow.ToUniversalTime();
        SortedDictionary<string, List<CosmosObservationContainerDocument>> partitionGroups =
            new(StringComparer.Ordinal);
        foreach (var document in batch.Documents)
        {
            callbackContext.CancellationToken.ThrowIfCancellationRequested();
            if (!MatchesBinding(document: document))
            {
                continue;
            }

            ValidateMatchingDocument(document: document);
            if (!partitionGroups.TryGetValue(document.PartitionKey, out var group))
            {
                group = new();
                partitionGroups.Add(document.PartitionKey, group);
            }

            group.Add(document);
        }

        var prepared = ImmutableArray.CreateBuilder<PreparedSettlement>(
            Math.Max(partitionGroups.Count, 1));
        if (partitionGroups.Count == 0)
        {
            var scope = CreateFilteredCallbackScope(feedRangeJson: batch.FeedRangeJson);
            var position = CreatePosition(
                scope: scope,
                feedRangeJson: batch.FeedRangeJson,
                providerContinuation: batch.ContinuationToken);
            var page = new MaterializationChangePage(
                deliveries: [],
                throughPosition: position,
                state: MaterializationChangePageState.Progressed);
            prepared.Add(await ApplyPageAsync(
                callbackContext: callbackContext,
                request: request,
                handler: handler,
                page: page).ConfigureAwait(false));
        }
        else
        {
            foreach (var (partitionKey, documents) in partitionGroups)
            {
                callbackContext.CancellationToken.ThrowIfCancellationRequested();
                var scope = CreateLogicalPartitionScope(partitionKey: partitionKey);
                var position = CreatePosition(
                    scope: scope,
                    feedRangeJson: batch.FeedRangeJson,
                    providerContinuation: batch.ContinuationToken);
                var deliveries = ImmutableArray.CreateBuilder<MaterializationChangeDelivery>(documents.Count);
                foreach (var document in documents)
                {
                    deliveries.Add(ProjectChange(
                        document: document,
                        scope: scope,
                        position: position,
                        deliveredAtUtc: deliveredAtUtc));
                }

                var page = new MaterializationChangePage(
                    deliveries: deliveries.MoveToImmutable(),
                    throughPosition: position,
                    state: MaterializationChangePageState.MoreAvailable);
                prepared.Add(await ApplyPageAsync(
                    callbackContext: callbackContext,
                    request: request,
                    handler: handler,
                    page: page).ConfigureAwait(false));
            }
        }

        callbackContext.CancellationToken.ThrowIfCancellationRequested();
        var settlementRequestedAtUtc = callbackContext.UtcNow.ToUniversalTime();
        var settlementAttempt = Guid.NewGuid().ToString("N");
        await batch.CheckpointAsync().ConfigureAwait(false);

        var settledAtUtc = callbackContext.UtcNow.ToUniversalTime();
        if (settledAtUtc < settlementRequestedAtUtc)
        {
            settledAtUtc = settlementRequestedAtUtc;
        }
        foreach (var item in prepared)
        {
            if (settledAtUtc < item.Checkpoint.CommittedAtUtc)
            {
                // The handler's returned durable result establishes causal order. Normalize distributed clock skew
                // so the provider-neutral observation does not falsely claim that settlement preceded that commit.
                settledAtUtc = item.Checkpoint.CommittedAtUtc;
            }
        }

        foreach (var item in prepared)
        {
            var settlementDigest = HashParts(
                values:
                [
                    settlementAttempt,
                    ComputePositionScopeDigest(scope: item.Position.Scope),
                    item.Checkpoint.Id.Value,
                    item.Position.Value
                ]);
            var settlement = new MaterializationSourceSettlement(
                id: new MaterializationSettlementId(string.Concat(
                    "cosmos-managed-settlement/v1/sha256/",
                    settlementDigest)),
                checkpoint: item.Checkpoint.Id,
                position: item.Position,
                settledAtUtc: settledAtUtc,
                evidenceReference: string.Concat(
                    EvidencePrefix,
                    "/settlement/sha256/",
                    settlementDigest));
            Observe(new(
                progress: item.Progress,
                settlement: settlement));
        }
    }

    static async ValueTask<PreparedSettlement> ApplyPageAsync(
        OperationContext callbackContext,
        MaterializationManagedChangeRequest request,
        MaterializationManagedChangeHandler handler,
        MaterializationChangePage page)
    {
        var progress = request.CreateProgressKey(scope: page.ThroughPosition.Scope);
        var result = await handler(
            context: callbackContext,
            progress: progress,
            page: page).ConfigureAwait(false);
        var checkpoint = page.RequireDurableCheckpointForSettlement(
            progress: progress,
            result: result);
        return new(
            Progress: result.Snapshot!,
            Checkpoint: checkpoint,
            Position: page.ThroughPosition);
    }

    MaterializationChangeDelivery ProjectChange(
        CosmosObservationContainerDocument document,
        MaterializationSourceScope scope,
        MaterializationSourcePosition position,
        DateTimeOffset deliveredAtUtc)
    {
        var subjectIdentity = ResolveIdentity(document: document);
        var stableIdentity = StableChangeIdentity(
            semanticScopeDigest: semanticChangeScopeDigest,
            document: document,
            subjectIdentity: subjectIdentity);
        var evidence = string.Concat(
            EvidencePrefix,
            "/change/sha256/",
            stableIdentity);
        var after = ProjectObservation(
            document: document,
            identity: subjectIdentity,
            evidenceReference: evidence);
        var occurredAtUtc = (document.OccurredAtUtc ?? deliveredAtUtc).ToUniversalTime();
        var observedAtUtc = deliveredAtUtc < occurredAtUtc ? occurredAtUtc : deliveredAtUtc;
        var change = new MaterializationChangeEnvelope(
            id: new MaterializationChangeId(string.Concat(
                "cosmos-managed-change/v1/sha256/",
                stableIdentity)),
            subjectIdentity: subjectIdentity,
            scope: scope,
            shape: reader.Shape,
            position: position,
            kind: MaterializationChangeKind.Upsert,
            before: null,
            after: after,
            occurredAtUtc: occurredAtUtc,
            observedAtUtc: observedAtUtc,
            evidenceReference: evidence);
        return new(
            id: new MaterializationDeliveryId(string.Concat(
                "cosmos-managed-delivery/v1/sha256/",
                stableIdentity)),
            change: change,
            deliveredAtUtc: observedAtUtc,
            evidenceReference: evidence);
    }

    RelationQuerySourceReadObservation ProjectObservation(
        CosmosObservationContainerDocument document,
        string identity,
        string evidenceReference)
    {
        var fields = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(projectedFields.Length);
        foreach (var field in projectedFields)
        {
            var selector = CosmosRelationQuerySourceSelectors.RequirePropertyPath(
                selector: field.SourceSelector,
                parameterName: nameof(field));
            var fieldEvidence = string.Concat(
                evidenceReference,
                "/field/",
                Uri.EscapeDataString(field.SemanticPath.ToString()));
            if (!TryResolveValue(document: document, selector: selector, value: out var value)
                || value.Kind == ObservationValueKind.Undefined)
            {
                fields.Add(new(
                    field: field,
                    state: RelationQuerySourceReadFieldState.Missing,
                    evidenceReference: fieldEvidence));
            }
            else if (value.Kind == ObservationValueKind.Null)
            {
                fields.Add(new(
                    field: field,
                    state: RelationQuerySourceReadFieldState.Null,
                    evidenceReference: fieldEvidence));
            }
            else
            {
                fields.Add(new(
                    field: field,
                    state: RelationQuerySourceReadFieldState.Value,
                    value: value,
                    evidenceReference: fieldEvidence));
            }
        }

        return new(
            identity: identity,
            shape: reader.Shape,
            fields: fields.MoveToImmutable());
    }

    string ResolveIdentity(CosmosObservationContainerDocument document)
    {
        if (!TryResolveValue(document: document, selector: identitySelector, value: out var identity)
            || identity.Kind != ObservationValueKind.String
            || string.IsNullOrWhiteSpace(identity.String))
        {
            throw new InvalidOperationException(
                $"A matching Cosmos document did not contain a non-empty string at declared identity selector '{reader.IdentitySourceSelector}'.");
        }

        return identity.String;
    }

    bool MatchesBinding(CosmosObservationContainerDocument document) =>
        document is not null
        && string.Equals(document.DocumentKind, binding.DocumentKind, StringComparison.Ordinal)
        && string.Equals(
            document.ObservationType,
            binding.PersistedObservationType.ShapeId.Value,
            StringComparison.Ordinal)
        && (binding.Kind != CosmosManagedMaterializationDocumentKind.Outbox
            || binding.StreamName is null
            || string.Equals(document.StreamName, binding.StreamName, StringComparison.Ordinal))
        && (fixedPartitionValue is null
            || string.Equals(document.PartitionKey, fixedPartitionValue, StringComparison.Ordinal));

    void ValidateMatchingDocument(CosmosObservationContainerDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Id))
        {
            throw new InvalidOperationException("A matching Cosmos change omitted its physical item id.");
        }
        if (string.IsNullOrWhiteSpace(document.PartitionKey))
        {
            throw new InvalidOperationException("A matching Cosmos change omitted its logical partition key.");
        }
        if (document.ObservationVersion < 0)
        {
            throw new InvalidOperationException("A matching Cosmos change carried a negative observation version.");
        }
        if (document.Observation is null)
        {
            throw new InvalidOperationException("A matching Cosmos change omitted its observation payload.");
        }
        if (string.IsNullOrWhiteSpace(document.ETag))
        {
            throw new InvalidOperationException(
                "A matching Cosmos latest-version change omitted the ETag required for stable revision identity.");
        }
    }

    MaterializationSourceScope CreateLogicalPartitionScope(string partitionKey)
    {
        partitionKey = Guard.RequireNotNullOrWhiteSpace(partitionKey);
        var partitionDigest = HashParts(values: [partitionKey]);
        return new(
            physicalPlan: physicalPlan,
            placement: placement,
            logicalPartition: reader.Descriptor.LogicalPartition,
            partition: new MaterializationSourcePartitionId(string.Concat(
                "cosmos/container/", accountFingerprint,
                "/database/", Uri.EscapeDataString(reader.DatabaseId),
                "/container/", Uri.EscapeDataString(reader.ContainerId),
                "/binding/sha256/", bindingDigest,
                "/logical-partition/sha256/", partitionDigest)),
            orderingScope: new MaterializationOrderingScopeId(string.Concat(
                "cosmos/change-feed/latest-version-upsert/binding/sha256/", bindingDigest,
                "/logical-partition/sha256/", partitionDigest)));
    }

    MaterializationSourceScope CreateFilteredCallbackScope(string feedRangeJson)
    {
        feedRangeJson = Guard.RequireNotNullOrWhiteSpace(feedRangeJson);
        var feedRangeDigest = HashParts(values: [feedRangeJson]);
        return new(
            physicalPlan: physicalPlan,
            placement: placement,
            logicalPartition: reader.Descriptor.LogicalPartition,
            partition: new MaterializationSourcePartitionId(string.Concat(
                "cosmos/container/", accountFingerprint,
                "/database/", Uri.EscapeDataString(reader.DatabaseId),
                "/container/", Uri.EscapeDataString(reader.ContainerId),
                "/binding/sha256/", bindingDigest,
                "/filtered-provider-range/sha256/", feedRangeDigest)),
            orderingScope: new MaterializationOrderingScopeId(string.Concat(
                "cosmos/change-feed/latest-version-upsert/binding/sha256/", bindingDigest,
                "/filtered-provider-range/sha256/", feedRangeDigest)));
    }

    MaterializationSourcePosition CreatePosition(
        MaterializationSourceScope scope,
        string feedRangeJson,
        string providerContinuation)
    {
        feedRangeJson = Guard.RequireNotNullOrWhiteSpace(feedRangeJson);
        providerContinuation = Guard.RequireNotNullOrWhiteSpace(providerContinuation);
        ManagedPositionPayload payload = new(
            Version: PositionFormatVersion,
            Source: Descriptor.Source.Value,
            ScopeDigest: ComputePositionScopeDigest(scope: scope),
            FeedRangeJson: feedRangeJson,
            ProviderContinuation: providerContinuation);
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            value: payload,
            options: CanonicalJsonOptions);
        return new(
            formatVersion: PositionFormatVersion,
            scope: scope,
            value: positionCodec.Encode(payload: canonical));
    }

    /// <summary>Authenticates one managed position and extracts its exact provider boundary.</summary>
    /// <param name="position">Managed position to authenticate and decode.</param>
    /// <returns>The exact provider range representation and continuation retained by the position.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The position is incompatible, unauthentic, or malformed.</exception>
    internal CosmosManagedMaterializationProviderBoundary DecodeProviderBoundary(
        MaterializationSourcePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (position.FormatVersion != PositionFormatVersion
            || position.Scope.Source != Descriptor.Source
            || position.Scope.PhysicalPlan != physicalPlan
            || position.Scope.Placement != placement)
        {
            throw new ArgumentException(
                "The managed Cosmos source position version, scope, or source is incompatible.",
                nameof(position));
        }

        var payloadBytes = positionCodec.Decode(
            value: position.Value,
            parameterName: nameof(position),
            valueKind: "managed Cosmos source position");
        var payloadJson = Encoding.UTF8.GetString(payloadBytes);
        if (!StrictDocumentJson.TryReadCanonicalObject<ManagedPositionPayload>(
                json: payloadJson,
                options: CanonicalJsonOptions,
                contractName: "managed Cosmos source position payload",
                value: out var payload,
                error: out var error)
            || payload is null)
        {
            throw new ArgumentException(
                $"The managed Cosmos source position payload is invalid: {error.Message}",
                nameof(position));
        }
        if (payload.Version != PositionFormatVersion
            || !string.Equals(payload.Source, Descriptor.Source.Value, StringComparison.Ordinal)
            || !string.Equals(
                payload.ScopeDigest,
                ComputePositionScopeDigest(scope: position.Scope),
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payload.FeedRangeJson)
            || string.IsNullOrWhiteSpace(payload.ProviderContinuation))
        {
            throw new ArgumentException(
                "The managed Cosmos source position payload does not match its exact source scope.",
                nameof(position));
        }

        return new(
            FeedRangeJson: payload.FeedRangeJson,
            ContinuationToken: payload.ProviderContinuation);
    }

    /// <summary>Authenticates one managed position and extracts its provider continuation for conformance tests.</summary>
    internal string DecodeProviderContinuation(MaterializationSourcePosition position) =>
        DecodeProviderBoundary(position: position).ContinuationToken;

    void Observe(MaterializationChangeSettlementObservation observation)
    {
        if (observer is null)
        {
            return;
        }

        try
        {
            observer.Observe(observation);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Observability is advisory and occurs after provider settlement; it cannot alter delivery semantics.
        }
    }

    static ImmutableArray<RelationQuerySourceReadField> CreateProjectedFields(
        CosmosRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement)
    {
        var fields = ImmutableArray.CreateBuilder<RelationQuerySourceReadField>(
            placement.Fields.Length + placement.RelationshipKeys.Length);
        foreach (var field in placement.Fields)
        {
            if (!string.Equals(
                    field.SourceSelector,
                    reader.FieldSourceSelector(field.SemanticPath),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A managed Cosmos field selector must match the wrapped Relations reader.",
                    nameof(placement));
            }

            RequireSupportedSelector(
                selector: CosmosRelationQuerySourceSelectors.RequirePropertyPath(
                    selector: field.SourceSelector,
                    parameterName: nameof(placement)),
                parameterName: nameof(placement));
            fields.Add(new(
                input: field.Input,
                semanticPath: field.SemanticPath,
                sourceSelector: field.SourceSelector,
                purpose: RelationQuerySourceReadFieldPurpose.SemanticInput));
        }

        foreach (var relationshipKey in placement.RelationshipKeys)
        {
            if (!string.Equals(
                    relationshipKey.SourceSelector,
                    reader.RelationshipKeySourceSelector(relationshipKey.SemanticPath),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A managed Cosmos relationship-key selector must match the wrapped Relations reader.",
                    nameof(placement));
            }

            RequireSupportedSelector(
                selector: CosmosRelationQuerySourceSelectors.RequirePropertyPath(
                    selector: relationshipKey.SourceSelector,
                    parameterName: nameof(placement)),
                parameterName: nameof(placement));
            fields.Add(new(
                input: null,
                semanticPath: relationshipKey.SemanticPath,
                sourceSelector: relationshipKey.SourceSelector,
                purpose: RelationQuerySourceReadFieldPurpose.Correlation));
        }

        return fields.MoveToImmutable();
    }

    static void ValidatePlacement(
        CosmosRelationQuerySourceReader reader,
        RelationQuerySourcePlacementBinding placement,
        CosmosManagedMaterializationChangeBinding binding)
    {
        if (!string.Equals(
                reader.Policy.PartitionSourceSelector,
                CosmosRelationQuerySourceReader.ObservationPartitionSourceSelector,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Managed Cosmos change delivery requires the canonical partitionKey selector represented by its persisted envelope.",
                nameof(reader));
        }

        if (placement.Source != reader.Descriptor.Source
            || placement.Shape != reader.Shape
            || placement.Identity is not { } identity
            || !string.Equals(
                identity.SourceSelector,
                reader.IdentitySourceSelector,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The managed materialization placement must belong to the exact wrapped reader and carry its declared identity selector.",
                nameof(placement));
        }
        if (binding.PersistedObservationType != reader.PersistedObservationType)
        {
            throw new ArgumentException(
                "The managed Cosmos binding persisted observation type must equal the wrapped reader's persisted envelope type.",
                nameof(binding));
        }
        if (!string.Equals(binding.StreamName, reader.PersistedStreamName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The managed Cosmos binding stream filter must equal the wrapped reader's persisted stream filter.",
                nameof(binding));
        }
        if (!string.Equals(binding.DocumentKind, reader.EntityDocumentKind, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The managed Cosmos document discriminator must equal the discriminator used by the wrapped reader.",
                nameof(binding));
        }
        if (placement.Partition is { } partition
            && !string.Equals(
                partition.SourceSelector,
                reader.Policy.PartitionSourceSelector,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The managed materialization partition selector must match the wrapped reader policy.",
                nameof(placement));
        }
    }

    static void RequireSupportedSelector(FieldPath selector, string parameterName)
    {
        var segments = selector.Segments.AsSpan();
        var root = segments[0].Segment!;
        var supportedRoot = root is
            "id" or "partitionKey" or "documentKind" or "observationType" or "observationId"
            or "observationVersion" or "observation" or "streamName" or "subjectType" or "subjectId"
            or "subjectVersion" or "correlationId" or "occurredAtUtc" or "traceId" or "spanId" or "_etag";
        if (!supportedRoot || (segments.Length > 1 && !string.Equals(root, "observation", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Managed Cosmos change projection does not support declared source selector '{selector}'.",
                parameterName);
        }
    }

    static bool TryResolveValue(
        CosmosObservationContainerDocument document,
        FieldPath selector,
        out ObservationValue value)
    {
        var segments = selector.Segments.AsSpan();
        var root = segments[0].Segment!;
        if (string.Equals(root, "observation", StringComparison.Ordinal))
        {
            if (document.Observation is null)
            {
                value = default;
                return false;
            }

            if (segments.Length == 1)
            {
                value = ObservationValue.FromObject(document.Observation);
                return true;
            }

            if (!document.Observation.TryGetValue(segments[1].Segment!, out value))
            {
                return false;
            }

            return segments.Length == 2
                || value.TryGetFieldSegments(
                    path: segments[2..],
                    value: out value);
        }

        value = root switch
        {
            "id" => ObservationValue.FromString(document.Id),
            "partitionKey" => ObservationValue.FromString(document.PartitionKey),
            "documentKind" => ObservationValue.FromString(document.DocumentKind),
            "observationType" => ObservationValue.FromString(document.ObservationType),
            "observationId" => ObservationValue.FromString(document.ObservationId),
            "observationVersion" => ObservationValue.FromInt64(document.ObservationVersion),
            "streamName" when document.StreamName is not null => ObservationValue.FromString(document.StreamName),
            "subjectType" when document.SubjectType is not null => ObservationValue.FromString(document.SubjectType),
            "subjectId" when document.SubjectId is not null => ObservationValue.FromString(document.SubjectId),
            "subjectVersion" when document.SubjectVersion.HasValue => ObservationValue.FromInt64(document.SubjectVersion.Value),
            "correlationId" when document.CorrelationId is not null => ObservationValue.FromString(document.CorrelationId),
            "occurredAtUtc" when document.OccurredAtUtc.HasValue => ObservationValue.FromDateTimeOffset(document.OccurredAtUtc.Value),
            "traceId" when document.TraceId is not null => ObservationValue.FromString(document.TraceId),
            "spanId" when document.SpanId is not null => ObservationValue.FromString(document.SpanId),
            "_etag" when document.ETag is not null => ObservationValue.FromString(document.ETag),
            _ => ObservationValue.Undefined
        };
        return value.Kind != ObservationValueKind.Undefined;
    }

    static MaterializationCapabilityProfile CreateCapabilityProfile(
        CosmosRelationQuerySourceReader reader,
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        string placementDigest,
        string bindingDigest,
        string processorNamespace,
        string leaseStoreIdentity,
        CosmosManagedMaterializationChangeSourcePolicy policy)
    {
        var configurationReferences = ImmutableArray.Create(
            EvidencePrefix,
            "azure-cosmos-dotnet/3.62/manual-checkpoint",
            "cosmos-change-mode/latest-version-upsert/v1",
            string.Concat(
                "relations-source/", Uri.EscapeDataString(reader.Descriptor.Source.Value),
                "/target-profile/", Uri.EscapeDataString(reader.Descriptor.TargetProfile.Id.Value),
                "/projected-shape/", Uri.EscapeDataString(reader.Shape.GraphId.Value), "/",
                Uri.EscapeDataString(reader.Shape.ShapeId.Value),
                "/persisted-type/", Uri.EscapeDataString(reader.PersistedObservationType.GraphId.Value), "/",
                Uri.EscapeDataString(reader.PersistedObservationType.ShapeId.Value)),
            string.Concat("cosmos-account/sha256/", CosmosPhysicalAffinity.Fingerprint(reader.AccountEndpoint)),
            string.Concat("cosmos-database/", Uri.EscapeDataString(reader.DatabaseId)),
            string.Concat("cosmos-container/", Uri.EscapeDataString(reader.ContainerId)),
            string.Concat("cosmos-document-kind/", Uri.EscapeDataString(reader.EntityDocumentKind)),
            string.Concat("cosmos-persisted-stream/", Uri.EscapeDataString(reader.PersistedStreamName ?? "none")),
            string.Concat("cosmos-identity-selector/", Uri.EscapeDataString(reader.IdentitySourceSelector)),
            string.Concat("cosmos-partition-selector/", Uri.EscapeDataString(reader.Policy.PartitionSourceSelector)),
            string.Concat(
                "relations-physical-plan/",
                Uri.EscapeDataString(physicalPlan.Algorithm), "/",
                Uri.EscapeDataString(physicalPlan.Canonicalization), "/",
                Uri.EscapeDataString(physicalPlan.Value)),
            string.Concat("relations-placement/sha256/", placementDigest),
            string.Concat("cosmos-managed-binding/sha256/", bindingDigest),
            string.Concat("cosmos-managed-lease-store/", Uri.EscapeDataString(leaseStoreIdentity)),
            string.Concat("cosmos-managed-processor-seed/", Uri.EscapeDataString(policy.ProcessorName)),
            string.Concat("cosmos-managed-processor-namespace/", Uri.EscapeDataString(processorNamespace)),
            string.Concat(
                "cosmos-managed-initial-position/",
                ((int)policy.InitialPosition).ToString(CultureInfo.InvariantCulture)),
            string.Concat(
                "cosmos-managed-initial-time/",
                policy.InitialTimeUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "none"),
            string.Concat(
                "cosmos-managed-poll-ticks/",
                policy.PollInterval.Ticks.ToString(CultureInfo.InvariantCulture)),
            string.Concat(
                "cosmos-managed-page-hint/",
                policy.MaximumProviderPageItems.ToString(CultureInfo.InvariantCulture)),
            string.Concat(
                "cosmos-managed-lag-state-hint/",
                policy.MaximumLagStateItems.ToString(CultureInfo.InvariantCulture)),
            string.Concat(
                "cosmos-managed-position-characters/",
                policy.MaximumPositionCharacters.ToString(CultureInfo.InvariantCulture)));
        var profileDigest = CosmosMaterializationIdentity.ComputeReferenceFingerprint(configurationReferences);
        ImmutableArray<string> sourceReferences =
        [
            .. configurationReferences,
            string.Concat("cosmos-managed-materialization-profile/sha256/", profileDigest)
        ];
        var evidence = ImmutableArray.Create(
            new MaterializationCapabilityEvidence(
                id: new("cohesive.adapters.cosmos/managed-change/latest-version-upsert/v1"),
                capability: MaterializationCapabilityKind.SourceChangeDelivery,
                realization: CapabilityRealizationKind.Constrained,
                guarantees:
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.AtLeastOnceDelivery,
                    MaterializationGuaranteeKind.LatestVersionUpsertDelivery
                ],
                operatingLimits: [],
                sourceReferences: sourceReferences,
                description: "Cosmos latest-version callbacks grouped in provider order by logical partition and projected as upserts; the SDK page size is advisory, and delete, previous-image, and complete mutation delivery are not claimed."),
            new MaterializationCapabilityEvidence(
                id: new("cohesive.adapters.cosmos/managed-change/manual-settlement/v1"),
                capability: MaterializationCapabilityKind.SourceSettlement,
                realization: CapabilityRealizationKind.Constrained,
                guarantees: [MaterializationGuaranteeKind.ExplicitSettlement],
                operatingLimits: [],
                sourceReferences: sourceReferences,
                description: "The provider lease advances only after exact applied or replayed durable Cohesive checkpoint evidence authorizes the SDK manual checkpoint."));
        return new(
            id: new MaterializationCapabilityProfileId(string.Concat(
                "cohesive.adapters.cosmos/managed-materialization-source/v1/sha256/",
                profileDigest)),
            role: MaterializationEndpointRole.Source,
            subject: reader.Descriptor.Source.Value,
            evidence: evidence,
            description: "Provider-managed Cosmos latest-version upsert delivery with explicit durable-before-provider settlement ordering.");
    }

    static CosmosManagedMaterializationChangeFeedProcessorFactory CreateProductionProcessorFactory(
        CosmosRelationQuerySourceReader reader,
        Container monitoredContainer,
        Container leaseContainer,
        CosmosManagedMaterializationChangeSourcePolicy policy)
    {
        reader = Guard.RequireNotNull(reader);
        policy = Guard.RequireNotNull(policy);
        var validatedMonitoredContainer = ValidateMonitoredContainer(
            reader: reader,
            monitoredContainer: monitoredContainer);
        var validatedLeaseContainer = Guard.RequireNotNull(leaseContainer);
        var leaseDatabase = Guard.RequireNotNull(validatedLeaseContainer.Database);
        if (string.Equals(validatedMonitoredContainer.Database.Id, leaseDatabase.Id, StringComparison.Ordinal)
            && string.Equals(validatedMonitoredContainer.Id, validatedLeaseContainer.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                message: "The managed Cosmos lease store must use a different database or container identifier from the monitored container so provider lease writes cannot enter the monitored feed; endpoint text alone is not sufficient physical-separation evidence.",
                paramName: nameof(leaseContainer));
        }

        return effectiveProcessorName => new CosmosManagedMaterializationChangeFeedProcessor(
            monitoredContainer: validatedMonitoredContainer,
            leaseContainer: validatedLeaseContainer,
            policy: policy,
            effectiveProcessorName: effectiveProcessorName);
    }

    static string ComputeBindingDigest(
        CosmosRelationQuerySourceReader reader,
        CosmosManagedMaterializationChangeBinding binding)
    {
        reader = Guard.RequireNotNull(reader);
        binding = Guard.RequireNotNull(binding);
        return HashParts(
            values:
            [
                ((int)binding.Kind).ToString(CultureInfo.InvariantCulture),
                binding.DocumentKind,
                binding.PersistedObservationType.GraphId.Value,
                binding.PersistedObservationType.ShapeId.Value,
                binding.StreamName ?? string.Empty,
                reader.IdentitySourceSelector,
                reader.Policy.PartitionSourceSelector,
                reader.Policy.FixedPartitionKey?.ToString() ?? string.Empty
            ]);
    }

    static string ComputeProcessorNamespace(
        CosmosRelationQuerySourceReader reader,
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        string placementDigest,
        string bindingDigest,
        string leaseStoreIdentity,
        string processorNameSeed,
        CosmosManagedMaterializationInitialPosition initialPosition,
        DateTimeOffset? initialTimeUtc) => string.Concat(
            "cohesive-materialization-namespace/sha256/",
            HashParts(
                values:
                [
                    Guard.RequireNotNullOrWhiteSpace(processorNameSeed),
                    Guard.RequireNotNull(reader).Descriptor.Source.Value,
                    reader.AccountEndpoint,
                    reader.DatabaseId,
                    reader.ContainerId,
                    Guard.RequireNotNull(physicalPlan).Algorithm,
                    physicalPlan.Canonicalization,
                    physicalPlan.Value,
                    Guard.RequireNotNullOrWhiteSpace(placementDigest),
                    Guard.RequireNotNullOrWhiteSpace(bindingDigest),
                    Guard.RequireNotNullOrWhiteSpace(leaseStoreIdentity),
                    ((int)initialPosition).ToString(CultureInfo.InvariantCulture),
                    initialTimeUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty
                ]));

    static string ComputeLeaseStoreIdentity(Container leaseContainer)
    {
        leaseContainer = Guard.RequireNotNull(leaseContainer);
        var database = Guard.RequireNotNull(leaseContainer.Database);
        var client = Guard.RequireNotNull(database.Client);
        var accountEndpoint = CosmosPhysicalAffinity.CanonicalAccountEndpointText(client.Endpoint);
        return string.Concat(
            "account/sha256/", CosmosPhysicalAffinity.Fingerprint(accountEndpoint),
            "/database/", Uri.EscapeDataString(Guard.RequireNotNullOrWhiteSpace(database.Id)),
            "/container/", Uri.EscapeDataString(Guard.RequireNotNullOrWhiteSpace(leaseContainer.Id)));
    }

    static Container ValidateMonitoredContainer(
        CosmosRelationQuerySourceReader reader,
        Container monitoredContainer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(monitoredContainer);
        if (!string.Equals(monitoredContainer.Database.Id, reader.DatabaseId, StringComparison.Ordinal)
            || !string.Equals(monitoredContainer.Id, reader.ContainerId, StringComparison.Ordinal)
            || !string.Equals(
                CosmosPhysicalAffinity.CanonicalAccountEndpointText(monitoredContainer.Database.Client.Endpoint),
                reader.AccountEndpoint,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                message: "The managed Cosmos monitored container must exactly match the wrapped Relations reader.",
                paramName: nameof(monitoredContainer));
        }

        return monitoredContainer;
    }

    static string GetStringPartitionValue(PartitionKey partitionKey, string parameterName)
    {
        var json = partitionKey.ToString();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                return Guard.RequireNotNullOrWhiteSpace(root.GetString());
            }
            if (root.ValueKind == JsonValueKind.Array
                && root.GetArrayLength() == 1
                && root[0].ValueKind == JsonValueKind.String)
            {
                return Guard.RequireNotNullOrWhiteSpace(root[0].GetString());
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "A managed Cosmos source requires a scalar string fixed partition key.",
                parameterName,
                exception);
        }

        throw new ArgumentException(
            "A managed Cosmos source requires a scalar string fixed partition key.",
            parameterName);
    }

    string ComputePositionScopeDigest(MaterializationSourceScope scope) => HashParts(
        values:
        [
            scope.PhysicalPlan.Algorithm,
            scope.PhysicalPlan.Canonicalization,
            scope.PhysicalPlan.Value,
            CosmosMaterializationIdentity.ComputePlacementFingerprint(placement: scope.Placement),
            scope.Source.Value,
            scope.Partition.Value,
            scope.OrderingScope.Value
        ]);

    static string StableChangeIdentity(
        string semanticScopeDigest,
        CosmosObservationContainerDocument document,
        string subjectIdentity)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        CosmosMaterializationIdentity.AppendFingerprintPart(
            hash: hash,
            value: semanticScopeDigest,
            parameterName: nameof(semanticScopeDigest));
        CosmosMaterializationIdentity.AppendFingerprintPart(
            hash: hash,
            value: document.PartitionKey,
            parameterName: nameof(document));
        CosmosMaterializationIdentity.AppendFingerprintPart(
            hash: hash,
            value: document.Id,
            parameterName: nameof(document));
        CosmosMaterializationIdentity.AppendFingerprintPart(
            hash: hash,
            value: document.ObservationVersion.ToString(CultureInfo.InvariantCulture),
            parameterName: nameof(document));
        CosmosMaterializationIdentity.AppendFingerprintPart(
            hash: hash,
            value: document.ETag!,
            parameterName: nameof(document));
        CosmosMaterializationIdentity.AppendFingerprintPart(
            hash: hash,
            value: subjectIdentity,
            parameterName: nameof(subjectIdentity));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static string HashParts(params string[] values) =>
        CosmosMaterializationIdentity.ComputeOrderedFingerprint(values: values);

    sealed record PreparedSettlement(
        MaterializationProgressSnapshot Progress,
        MaterializationApplicationCheckpoint Checkpoint,
        MaterializationSourcePosition Position);

    sealed record ManagedPositionPayload(
        int Version,
        string Source,
        string ScopeDigest,
        string FeedRangeJson,
        string ProviderContinuation);
}
