using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Control;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Durable bounded materialization source over one fixed logical-partition Cosmos Relations placement and
/// full-fidelity change feed.
/// </summary>
/// <remarks>
/// Baseline pages retain provider continuations but do not claim an MVCC snapshot across pages. Rebuilds capture a
/// current change-feed position before scanning and then catch up from that cut. Both operations request
/// Strong baseline reads against a caller-attested Strong account, while the change-feed client may not weaken the
/// account policy, so a write committed before the cut cannot be absent from the later baseline. Full-fidelity
/// positions remain
/// valid only inside the deployment-attested retention horizon. The source owns neither application progress nor
/// provider settlement: callers fence and checkpoint returned positions through the materialization progress store.
/// Every change read requires an explicit position because Cosmos all-versions-and-deletes pull consumption cannot
/// start at the beginning; callers must explicitly capture the current cut they intend to use.
/// Intra-provider-page positions replay the page-start continuation and authenticate the consumed canonical prefix.
/// If physical-range evolution resegments that page, replay fails closed and the owning process must start a new
/// generation rather than silently skip or reorder changes.
/// Within each intact SDK response page, changes use adapter-canonical LSN and physical identity order. Multiple
/// changes to one physical item at the same LSN are ordered only when their full previous/current image chain proves
/// one unique transition sequence; an ambiguous chain fails closed instead of using arbitrary provider order.
/// Cohesive observation versions additionally validate in-scope replacements. Cross-item ordering remains
/// deterministic and is not a claim about transaction statement execution order. If distinct physical items in one
/// transaction affect the same semantic observation identity, the page fails closed because their relative semantic
/// order cannot be proven from independent physical-image chains.
/// </remarks>
public sealed class CosmosMaterializationSource : IMaterializationChangeSource
{
    /// <summary>Stable diagnostic code for a failed canonical Cosmos baseline read.</summary>
    public const string SourceReadFailedDiagnosticCode =
        "cohesive.adapters.cosmos.materialization.sourceReadFailed";

    /// <summary>Stable diagnostic code for an inconclusive canonical Cosmos baseline read.</summary>
    public const string SourceReadInconclusiveDiagnosticCode =
        "cohesive.adapters.cosmos.materialization.sourceReadInconclusive";

    /// <summary>Stable diagnostic code for a baseline that reached its Relations acquisition boundary.</summary>
    public const string ReadBoundaryReachedDiagnosticCode =
        "cohesive.adapters.cosmos.materialization.readBoundaryReached";

    const int ContinuationFormatVersion = 1;
    const int PositionFormatVersion = 1;
    const string ContinuationPrefix = "cosmos-materialization-read/v1/";
    const string PositionPrefix = "cosmos-materialization-change/v1/";
    const string EvidencePrefix = "cohesive.adapters.cosmos/materialization-source/v1";
    static ReadOnlySpan<byte> ContinuationAuthenticationDomain =>
        "cohesive.adapters.cosmos/materialization-read/v1\0"u8;
    static ReadOnlySpan<byte> PositionAuthenticationDomain =>
        "cohesive.adapters.cosmos/materialization-change/v1\0"u8;
    static readonly JsonSerializerOptions CanonicalJsonOptions = MaterializationJsonSerializer.CreateOptions();

    readonly CosmosRelationQuerySourceReader reader;
    readonly CosmosMaterializationSourcePolicy policy;
    readonly ICosmosMaterializationChangeFeedReader changeFeedReader;
    readonly ICosmosMaterializationSourceObserver? observer;
    readonly FeedRange? feedRange;
    readonly MaterializationAuthenticatedValueCodec continuationCodec;
    readonly MaterializationAuthenticatedValueCodec positionCodec;
    readonly string scopeDigest;
    readonly CosmosMaterializationAdmission admission;
    readonly ImmutableArray<RelationQuerySourceReadField> changeFields;

    /// <summary>Creates a production Cosmos materialization source.</summary>
    /// <param name="reader">Canonical Cosmos Relations source reader reused for baseline semantics.</param>
    /// <param name="physicalPlan">Exact physical-plan fingerprint authorizing this materialization scope.</param>
    /// <param name="placement">Exact canonical source placement represented by the reader.</param>
    /// <param name="container">Borrowed SDK container for full-fidelity change-feed consumption.</param>
    /// <param name="policy">
    /// Explicit page, cursor, parallelism, retention, previous-image, and Strong-account evidence.
    /// </param>
    /// <param name="admissionIndex">
    /// Runtime-owned hierarchical admission index shared by sources using the same Cosmos resources.
    /// </param>
    /// <param name="authenticationKey">
    /// Caller-owned secret used to authenticate opaque continuations and positions. The source copies the key. The
    /// same secret must be supplied after a restart while outstanding cursors remain resumable; deliberate rotation
    /// invalidates those cursors and requires a new materialization generation.
    /// </param>
    /// <param name="observer">Optional sink for typed operational and Control evidence.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="reader"/>, <paramref name="physicalPlan"/>, <paramref name="placement"/>,
    /// <paramref name="container"/>, <paramref name="policy"/>, or <paramref name="admissionIndex"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Reader, placement, container, source affinity, or Strong-account cut-consistency requirements conflict, or
    /// <paramref name="authenticationKey"/> contains fewer than 32 bytes.
    /// </exception>
    /// <exception cref="ObjectDisposedException"><paramref name="admissionIndex"/> has been disposed.</exception>
    public CosmosMaterializationSource(
        CosmosRelationQuerySourceReader reader,
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        RelationQuerySourcePlacementBinding placement,
        Container container,
        CosmosMaterializationSourcePolicy policy,
        CosmosMaterializationAdmissionIndex admissionIndex,
        ReadOnlySpan<byte> authenticationKey,
        ICosmosMaterializationSourceObserver? observer = null)
        : this(
            reader: reader,
            physicalPlan: physicalPlan,
            placement: placement,
            policy: policy,
            admissionIndex: admissionIndex,
            changeFeedReader: new CosmosMaterializationChangeFeedReader(
                container: ValidateContainer(reader: reader, container: container)),
            authenticationKey: authenticationKey,
            observer: observer)
    {
    }

    /// <summary>Creates a source over a narrow full-fidelity transport for deterministic conformance tests.</summary>
    internal CosmosMaterializationSource(
        CosmosRelationQuerySourceReader reader,
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        RelationQuerySourcePlacementBinding placement,
        CosmosMaterializationSourcePolicy policy,
        CosmosMaterializationAdmissionIndex admissionIndex,
        ICosmosMaterializationChangeFeedReader changeFeedReader,
        ReadOnlySpan<byte> authenticationKey,
        ICosmosMaterializationSourceObserver? observer = null)
    {
        this.reader = Guard.RequireNotNull(reader);
        physicalPlan = Guard.RequireNotNull(physicalPlan);
        placement = Guard.RequireNotNull(placement);
        this.policy = Guard.RequireNotNull(policy);
        admissionIndex = Guard.RequireNotNull(admissionIndex);
        this.changeFeedReader = Guard.RequireNotNull(changeFeedReader);
        if (authenticationKey.Length < MaterializationAuthenticatedValueCodec.MinimumAuthenticationKeyBytes)
        {
            throw new ArgumentException(
                message: $"Cosmos materialization cursor authentication requires at least {MaterializationAuthenticatedValueCodec.MinimumAuthenticationKeyBytes} secret bytes.",
                paramName: nameof(authenticationKey));
        }
        if (placement.Source != reader.Descriptor.Source
            || placement.Shape != reader.Shape
            || placement.Identity is not { } placementIdentity
            || !string.Equals(
                placementIdentity.SourceSelector,
                reader.IdentitySourceSelector,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                message: "The materialization placement must belong to the exact wrapped Cosmos reader source and shape and carry its exact observation-identity selector.",
                paramName: nameof(placement));
        }
        if (placement.Partition is { } placementPartition
            && !string.Equals(
                placementPartition.SourceSelector,
                reader.Policy.PartitionSourceSelector,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                message: "The materialization placement partition selector conflicts with the wrapped Cosmos reader policy.",
                paramName: nameof(placement));
        }
        var projectedChangeFields = ImmutableArray.CreateBuilder<RelationQuerySourceReadField>(
            placement.Fields.Length);
        foreach (var field in placement.Fields)
        {
            var canonicalSelector = CanonicalObservationFieldSelector(
                semanticPath: field.SemanticPath,
                parameterName: nameof(placement));
            if (!string.Equals(
                    field.SourceSelector,
                    reader.FieldSourceSelector(field.SemanticPath),
                    StringComparison.Ordinal)
                || !string.Equals(field.SourceSelector, canonicalSelector, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    message: "A materialization placement field selector must match the wrapped Cosmos reader and the canonical observation envelope represented by full-fidelity change images.",
                    paramName: nameof(placement));
            }

            projectedChangeFields.Add(new(
                input: field.Input,
                semanticPath: field.SemanticPath,
                sourceSelector: field.SourceSelector,
                purpose: RelationQuerySourceReadFieldPurpose.SemanticInput));
        }
        HashSet<FieldPath> projectedCorrelationPaths = [];
        foreach (var relationshipKey in placement.RelationshipKeys)
        {
            var canonicalSelector = CanonicalObservationFieldSelector(
                semanticPath: relationshipKey.SemanticPath,
                parameterName: nameof(placement));
            if (!string.Equals(
                    relationshipKey.SourceSelector,
                    reader.RelationshipKeySourceSelector(relationshipKey.SemanticPath),
                    StringComparison.Ordinal)
                || !string.Equals(relationshipKey.SourceSelector, canonicalSelector, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    message: "A materialization placement relationship-key selector must match the wrapped Cosmos reader and the canonical observation envelope represented by full-fidelity change images.",
                    paramName: nameof(placement));
            }
            if (projectedCorrelationPaths.Add(relationshipKey.SemanticPath))
            {
                projectedChangeFields.Add(new(
                    input: null,
                    semanticPath: relationshipKey.SemanticPath,
                    sourceSelector: relationshipKey.SourceSelector,
                    purpose: RelationQuerySourceReadFieldPurpose.Correlation));
            }
        }
        if (placement.Acquisition == RelationQuerySourceAcquisitionKind.Supplied)
        {
            throw new ArgumentException(
                message: "A Cosmos materialization placement must authorize external acquisition.",
                paramName: nameof(placement));
        }
        if (reader.Policy.FixedPartitionKey is null)
        {
            throw new ArgumentException(
                message: "The initial Cosmos materialization source requires one fixed logical partition; whole-container change delivery needs a composite per-range position model.",
                paramName: nameof(reader));
        }
        if (reader.Policy.ReadConsistencyLevel != ConsistencyLevel.Strong
            || reader.ClientConsistencyLevel is { } clientConsistency
               && clientConsistency != ConsistencyLevel.Strong)
        {
            throw new ArgumentException(
                message: "Cosmos baseline-plus-catch-up materialization requires request-level Strong reads and no explicitly weaker Cosmos client override so the baseline cannot omit a write committed before the captured change-feed cut.",
                paramName: nameof(reader));
        }
        if (!string.Equals(
                reader.Policy.PartitionSourceSelector,
                CosmosRelationQuerySourceReader.ObservationPartitionSourceSelector,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                message: "Cosmos full-fidelity materialization currently requires the canonical partitionKey selector represented by change images.",
                paramName: nameof(reader));
        }
        if (!string.Equals(
                reader.IdentitySourceSelector,
                CosmosRelationQuerySourceReader.ObservationIdentitySourceSelector,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                message: "Cosmos full-fidelity materialization currently requires the canonical observationId identity selector so baseline and change identities cannot diverge.",
                paramName: nameof(reader));
        }

        changeFields = projectedChangeFields.MoveToImmutable();

        this.observer = observer;
        continuationCodec = new(
            formatPrefix: ContinuationPrefix,
            authenticationDomain: ContinuationAuthenticationDomain,
            authenticationKey: authenticationKey,
            maximumValueCharacters: policy.MaximumCursorCharacters);
        positionCodec = new(
            formatPrefix: PositionPrefix,
            authenticationDomain: PositionAuthenticationDomain,
            authenticationKey: authenticationKey,
            maximumValueCharacters: policy.MaximumCursorCharacters);
        var partitionEvidence = reader.Policy.FixedPartitionKey is { } fixedPartition
            ? FeedRange.FromPartitionKey(fixedPartition).ToJsonString()
            : "whole-container";
        var partitionDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(partitionEvidence)));
        feedRange = reader.Policy.FixedPartitionKey is { } key
            ? FeedRange.FromPartitionKey(key)
            : null;
        var accountFingerprint = CosmosPhysicalAffinity.Fingerprint(reader.AccountEndpoint);
        var maximumProviderConcurrency = checked((int)reader.Limits.MaximumConcurrency);
        admission = admissionIndex.Bind(
            containerIdentity: string.Concat(
                accountFingerprint, "\0",
                reader.DatabaseId, "\0",
                reader.ContainerId),
            partitionIdentity: reader.Policy.FixedPartitionKey is null ? null : partitionDigest,
            maximumContainerParallelism: Math.Min(
                policy.MaximumContainerParallelism,
                maximumProviderConcurrency),
            maximumPartitionParallelism: Math.Min(
                policy.MaximumPartitionParallelism,
                maximumProviderConcurrency));
        Scope = new(
            physicalPlan: physicalPlan,
            placement: placement,
            partition: new MaterializationSourcePartitionId(string.Concat(
                "cosmos/container/", accountFingerprint,
                "/database/", Uri.EscapeDataString(reader.DatabaseId),
                "/container/", Uri.EscapeDataString(reader.ContainerId),
                "/logical-scope/sha256/", partitionDigest)),
            orderingScope: new MaterializationOrderingScopeId(string.Concat(
                "cosmos/change-feed/logical-scope/sha256/", partitionDigest,
                "/provider-continuation/v1")));
        Descriptor = new(
            relationReader: reader,
            capabilityProfile: CreateCapabilityProfile(
                reader: reader,
                physicalPlan: physicalPlan,
                placement: placement,
                policy: policy,
                partitionDigest: partitionDigest));
        scopeDigest = ComputeScopeDigest(
            scope: Scope,
            profile: Descriptor.CapabilityProfile.Id.Value);
        try
        {
            _ = Encode(
                codec: continuationCodec,
                payload: new BaselineCursorPayload(
                    SourceProfile: Descriptor.CapabilityProfile.Id.Value,
                    ScopeDigest: scopeDigest,
                    Kind: BaselineCursorKind.Enumeration,
                    ProviderContinuation: "provider-token",
                    ProviderPageSizeHint: 1,
                    Offset: 1,
                    EmittedRows: 1,
                    LastIdentity: "observation-id",
                    PrefixDigest: EmptyDigest));
            _ = Encode(
                codec: positionCodec,
                payload: new ChangeCursorPayload(
                    SourceProfile: Descriptor.CapabilityProfile.Id.Value,
                    ScopeDigest: scopeDigest,
                    ProviderContinuation: "provider-token",
                    ProviderPageSizeHint: 0,
                    Offset: 0,
                    PrefixDigest: EmptyDigest));
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(
                message: "The Cosmos cursor bound cannot contain the fixed authenticated source envelope.",
                paramName: nameof(policy),
                innerException: exception);
        }
    }

    /// <inheritdoc />
    public MaterializationSourceDescriptor Descriptor { get; }

    /// <summary>Exact source placement, logical partition, and ordering scope accepted by this source.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <inheritdoc />
    /// <exception cref="CosmosMaterializationSourceException">
    /// Cosmos rejects the baseline acquisition, the provider response violates its protocol, or resumed content
    /// conflicts with its authenticated prefix. <see cref="CosmosMaterializationSourceException.FailureKind"/>
    /// determines whether the owning process may retry, must start a new generation, or must change configuration.
    /// </exception>
    public async ValueTask<MaterializationSourcePage> ReadPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        RequireScope(scope: request.Scope, parameterName: nameof(request));
        MaterializationSourceAcquisitionCatalog.RequireCompatibleRead(
            read: request.Read,
            scope: request.Scope);
        var capability = MaterializationSourceAcquisitionCatalog.GetReadCapability(request.Read.Constraint);
        MaterializationCapabilityLimits.RequireSupportedBounds(
            profile: Descriptor.CapabilityProfile,
            capability: capability,
            itemLimitKind: MaterializationLimitKind.ReadItems,
            requestedItems: request.MaximumItems,
            byteLimitKind: MaterializationLimitKind.ReadBytes,
            requestedBytes: request.MaximumBytes,
            parameterName: nameof(request));
        var started = context.UtcNow;
        using var admissionLease = await EnterObservedAsync(
            context: context,
            operation: CosmosMaterializationSourceOperationKind.BaselineRead,
            started: started,
            canceledEvidence: "baseline-admission-canceled").ConfigureAwait(false);
        try
        {
            var result = request.Read.Constraint is RelationQueryBoundedEnumeration
                ? await ReadEnumerationPageAsync(
                    context: context,
                    request: request,
                    startedAtUtc: started).ConfigureAwait(false)
                : await ReadBufferedPageAsync(
                    context: context,
                    request: request,
                    startedAtUtc: started).ConfigureAwait(false);
            var completed = context.UtcNow;
            var bytes = CanonicalByteCount(result.Page.Read.Observations);
            Observe(CreateObservation(
                operation: CosmosMaterializationSourceOperationKind.BaselineRead,
                disposition: result.Disposition,
                started: started,
                completed: completed,
                itemCount: result.Page.Read.Observations.Length,
                byteCount: bytes,
                requestCharge: result.RequestCharge,
                evidenceReference: result.EvidenceReference,
                statusCode: result.StatusCode));
            return result.Page;
        }
        catch (OperationCanceledException exception) when (context.CancellationToken.IsCancellationRequested)
        {
            var canceled = CancellationEvidence(
                started: started,
                completed: context.UtcNow,
                exception: exception);
            Observe(CreateObservation(
                operation: CosmosMaterializationSourceOperationKind.BaselineRead,
                disposition: CosmosMaterializationSourceDisposition.Canceled,
                started: canceled.StartedAtUtc,
                completed: canceled.CompletedAtUtc,
                itemCount: 0,
                byteCount: 0,
                requestCharge: canceled.RequestCharge,
                evidenceReference: FailureEvidence(
                    suffix: "baseline-canceled",
                    providerEvidenceReference: canceled.ProviderEvidenceReference),
                statusCode: canceled.StatusCode));
            throw;
        }
        catch (CosmosRelationQueryMaterializationProtocolException exception)
        {
            throw ProviderProtocolFailure(
                operation: CosmosMaterializationSourceOperationKind.BaselineRead,
                started: started,
                completed: context.UtcNow,
                exception: exception.ProviderException,
                resumedPosition: false,
                requestCharge: exception.CompletedRequestCharge,
                statusCode: exception.CompletedStatusCode,
                providerEvidenceReference: exception.ProviderEvidenceReference);
        }
        catch (CosmosProviderProtocolException exception)
        {
            throw ProviderProtocolFailure(
                operation: CosmosMaterializationSourceOperationKind.BaselineRead,
                started: started,
                completed: context.UtcNow,
                exception: exception,
                resumedPosition: false,
                requestCharge: exception.RequestCharge ?? 0,
                statusCode: exception.StatusCode,
                providerEvidenceReference: exception.ProviderEvidenceReference);
        }
        catch (CosmosRelationQueryMaterializationProviderException exception)
        {
            throw ProviderFailure(
                operation: CosmosMaterializationSourceOperationKind.BaselineRead,
                started: started,
                completed: context.UtcNow,
                exception: exception.ProviderException,
                resumedPosition: false,
                completedRequestCharge: exception.CompletedRequestCharge);
        }
        catch (CosmosException exception)
        {
            throw ProviderFailure(
                operation: CosmosMaterializationSourceOperationKind.BaselineRead,
                started: started,
                completed: context.UtcNow,
                exception: exception,
                resumedPosition: false);
        }
    }

    /// <inheritdoc />
    /// <exception cref="CosmosMaterializationSourceException">
    /// Cosmos cannot capture a valid current full-fidelity position. The exception carries typed recovery and
    /// Control evidence in <see cref="CosmosMaterializationSourceException.Observation"/>.
    /// </exception>
    public async ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireScope(scope: scope, parameterName: nameof(scope));
        var started = context.UtcNow;
        using var admissionLease = await EnterObservedAsync(
            context: context,
            operation: CosmosMaterializationSourceOperationKind.CaptureCurrentPosition,
            started: started,
            canceledEvidence: "capture-admission-canceled").ConfigureAwait(false);
        try
        {
            var page = await changeFeedReader.ReadPageAsync(
                start: new(kind: CosmosMaterializationChangeFeedStartKind.Now),
                feedRange: feedRange,
                pageSizeHint: policy.MaximumProviderPageItems,
                cancellationToken: context.CancellationToken).ConfigureAwait(false);
            var position = CreatePosition(payload: new(
                SourceProfile: Descriptor.CapabilityProfile.Id.Value,
                ScopeDigest: scopeDigest,
                ProviderContinuation: page.ContinuationToken,
                ProviderPageSizeHint: 0,
                Offset: 0,
                PrefixDigest: EmptyDigest));
            Observe(CreateObservation(
                operation: CosmosMaterializationSourceOperationKind.CaptureCurrentPosition,
                disposition: CosmosMaterializationSourceDisposition.Complete,
                started: started,
                completed: context.UtcNow,
                itemCount: 0,
                byteCount: 0,
                requestCharge: page.RequestCharge,
                evidenceReference: page.ProviderEvidenceReference,
                statusCode: page.StatusCode));
            return position;
        }
        catch (OperationCanceledException exception) when (context.CancellationToken.IsCancellationRequested)
        {
            var canceled = CancellationEvidence(
                started: started,
                completed: context.UtcNow,
                exception: exception);
            Observe(CreateObservation(
                operation: CosmosMaterializationSourceOperationKind.CaptureCurrentPosition,
                disposition: CosmosMaterializationSourceDisposition.Canceled,
                started: canceled.StartedAtUtc,
                completed: canceled.CompletedAtUtc,
                itemCount: 0,
                byteCount: 0,
                requestCharge: canceled.RequestCharge,
                evidenceReference: FailureEvidence(
                    suffix: "capture-canceled",
                    providerEvidenceReference: canceled.ProviderEvidenceReference),
                statusCode: canceled.StatusCode));
            throw;
        }
        catch (CosmosProviderProtocolException exception)
        {
            throw ProviderProtocolFailure(
                operation: CosmosMaterializationSourceOperationKind.CaptureCurrentPosition,
                started: started,
                completed: context.UtcNow,
                exception: exception,
                resumedPosition: false,
                requestCharge: exception.RequestCharge ?? 0,
                statusCode: exception.StatusCode,
                providerEvidenceReference: exception.ProviderEvidenceReference);
        }
        catch (CosmosException exception)
        {
            throw ProviderFailure(
                operation: CosmosMaterializationSourceOperationKind.CaptureCurrentPosition,
                started: started,
                completed: context.UtcNow,
                exception: exception,
                resumedPosition: false);
        }
    }

    /// <inheritdoc />
    /// <exception cref="CosmosMaterializationSourceException">
    /// The position is unavailable or incompatible, required change evidence is unavailable, replay conflicts with
    /// the authenticated prefix, or Cosmos rejects the read. The failure kind determines the required recovery.
    /// </exception>
    public async ValueTask<MaterializationChangePage> ReadChangesAsync(
        OperationContext context,
        MaterializationChangeReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        RequireScope(scope: request.Scope, parameterName: nameof(request));
        MaterializationCapabilityLimits.RequireSupportedBounds(
            profile: Descriptor.CapabilityProfile,
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            itemLimitKind: MaterializationLimitKind.ChangeItems,
            requestedItems: request.MaximumDeliveries,
            byteLimitKind: MaterializationLimitKind.ReadBytes,
            requestedBytes: request.MaximumBytes,
            parameterName: nameof(request));
        var started = context.UtcNow;
        using var admissionLease = await EnterObservedAsync(
            context: context,
            operation: CosmosMaterializationSourceOperationKind.ChangeRead,
            started: started,
            canceledEvidence: "change-admission-canceled").ConfigureAwait(false);
        ProviderOperationEvidence? completedProviderEvidence = null;
        try
        {
            var cursor = DecodePosition(
                position: request.AfterPosition,
                parameterName: nameof(request));
            var providerPageSizeHint = cursor.Offset == 0
                ? Math.Min(policy.MaximumProviderPageItems, request.MaximumDeliveries)
                : cursor.ProviderPageSizeHint;
            var providerPage = await changeFeedReader.ReadPageAsync(
                start: new(
                    kind: CosmosMaterializationChangeFeedStartKind.Continuation,
                    continuationToken: cursor.ProviderContinuation),
                feedRange: null,
                pageSizeHint: providerPageSizeHint,
                cancellationToken: context.CancellationToken).ConfigureAwait(false);
            ProviderOperationEvidence providerEvidence = new(
                StartedAtUtc: started,
                CompletedAtUtc: context.UtcNow,
                RequestCharge: providerPage.RequestCharge,
                StatusCode: providerPage.StatusCode,
                ProviderEvidenceReference: providerPage.ProviderEvidenceReference);
            completedProviderEvidence = providerEvidence;
            foreach (var providerChange in providerPage.Changes)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                ValidateChangeEvidence(
                    provider: providerChange,
                    operationEvidence: providerEvidence);
            }
            var canonicalChanges = CanonicalizeProviderChanges(
                changes: providerPage.Changes,
                providerEvidence: providerEvidence);
            var prefixDigests = BuildPrefixDigestChain(canonicalChanges);
            VerifyPrefix(
                prefixDigests: prefixDigests,
                count: cursor.Offset,
                expectedDigest: cursor.PrefixDigest,
                cursorName: "change position",
                providerEvidence: providerEvidence);
            var effectiveItems = Math.Min(request.MaximumDeliveries, policy.MaximumChangePageItems);
            var effectiveBytes = Math.Min(request.MaximumBytes, policy.MaximumChangePageBytes);
            var deliveries = ImmutableArray.CreateBuilder<MaterializationChangeDelivery>(
                Math.Min(effectiveItems, canonicalChanges.Length - cursor.Offset));
            long canonicalBytes = 0;
            var consumed = cursor.Offset;
            for (; consumed < canonicalChanges.Length; consumed++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (deliveries.Count >= effectiveItems)
                {
                    break;
                }

                var prefixCount = consumed + 1;
                var nextPosition = CreatePosition(payload: new(
                    SourceProfile: Descriptor.CapabilityProfile.Id.Value,
                    ScopeDigest: scopeDigest,
                    ProviderContinuation: cursor.ProviderContinuation,
                    ProviderPageSizeHint: providerPageSizeHint,
                    Offset: prefixCount,
                    PrefixDigest: prefixDigests[prefixCount]));
                var delivery = ProjectChange(
                    provider: canonicalChanges[consumed].Change,
                    canonicalProviderRecord: canonicalChanges[consumed].CanonicalBytes,
                    position: nextPosition,
                    providerEvidence: providerPage.ProviderEvidenceReference,
                    operationEvidence: providerEvidence);
                if (delivery is null)
                {
                    continue;
                }

                var deliveryBytes = CanonicalByteCount(delivery);
                if (deliveryBytes > effectiveBytes)
                {
                    if (deliveries.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"Change delivery '{delivery.Id.Value}' requires {deliveryBytes.ToString(CultureInfo.InvariantCulture)} canonical bytes, exceeding the indivisible bound of {effectiveBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
                    }
                    break;
                }
                if (deliveryBytes > effectiveBytes - canonicalBytes)
                {
                    break;
                }

                deliveries.Add(delivery);
                canonicalBytes += deliveryBytes;
            }

            var consumedWholeProviderPage = consumed >= canonicalChanges.Length;
            var through = consumedWholeProviderPage
                ? CreatePosition(payload: new(
                    SourceProfile: Descriptor.CapabilityProfile.Id.Value,
                    ScopeDigest: scopeDigest,
                    ProviderContinuation: providerPage.ContinuationToken,
                    ProviderPageSizeHint: 0,
                    Offset: 0,
                    PrefixDigest: EmptyDigest))
                : CreatePosition(payload: new(
                    SourceProfile: Descriptor.CapabilityProfile.Id.Value,
                    ScopeDigest: scopeDigest,
                    ProviderContinuation: cursor.ProviderContinuation,
                    ProviderPageSizeHint: providerPageSizeHint,
                    Offset: consumed,
                    PrefixDigest: prefixDigests[consumed]));
            var materialized = deliveries.Count == deliveries.Capacity
                ? deliveries.MoveToImmutable()
                : deliveries.ToImmutable();
            var caughtUp = consumedWholeProviderPage
                && providerPage.StatusCode == HttpStatusCode.NotModified;
            var state = caughtUp
                ? MaterializationChangePageState.CaughtUp
                : materialized.IsDefaultOrEmpty
                    ? MaterializationChangePageState.Progressed
                    : MaterializationChangePageState.MoreAvailable;
            var disposition = state switch
            {
                MaterializationChangePageState.CaughtUp => CosmosMaterializationSourceDisposition.CaughtUp,
                MaterializationChangePageState.Progressed => CosmosMaterializationSourceDisposition.Progressed,
                _ => CosmosMaterializationSourceDisposition.Partial
            };
            Observe(CreateObservation(
                operation: CosmosMaterializationSourceOperationKind.ChangeRead,
                disposition: disposition,
                started: started,
                completed: context.UtcNow,
                itemCount: materialized.Length,
                byteCount: canonicalBytes,
                requestCharge: providerPage.RequestCharge,
                evidenceReference: providerPage.ProviderEvidenceReference,
                statusCode: providerPage.StatusCode));
            return new(
                deliveries: materialized,
                throughPosition: through,
                state: state);
        }
        catch (OperationCanceledException exception) when (context.CancellationToken.IsCancellationRequested)
        {
            var canceled = CancellationEvidence(
                started: started,
                completed: context.UtcNow,
                exception: exception,
                completedProviderEvidence: completedProviderEvidence);
            Observe(CreateObservation(
                operation: CosmosMaterializationSourceOperationKind.ChangeRead,
                disposition: CosmosMaterializationSourceDisposition.Canceled,
                started: canceled.StartedAtUtc,
                completed: canceled.CompletedAtUtc,
                itemCount: 0,
                byteCount: 0,
                requestCharge: canceled.RequestCharge,
                evidenceReference: FailureEvidence(
                    suffix: "change-canceled",
                    providerEvidenceReference: canceled.ProviderEvidenceReference),
                statusCode: canceled.StatusCode));
            throw;
        }
        catch (CosmosMaterializationSourceException)
        {
            throw;
        }
        catch (CosmosProviderProtocolException exception)
        {
            throw ProviderProtocolFailure(
                operation: CosmosMaterializationSourceOperationKind.ChangeRead,
                started: started,
                completed: context.UtcNow,
                exception: exception,
                resumedPosition: true,
                requestCharge: exception.RequestCharge ?? 0,
                statusCode: exception.StatusCode,
                providerEvidenceReference: exception.ProviderEvidenceReference);
        }
        catch (CosmosException exception)
        {
            throw ProviderFailure(
                operation: CosmosMaterializationSourceOperationKind.ChangeRead,
                started: started,
                completed: context.UtcNow,
                exception: exception,
                resumedPosition: true);
        }
    }

    async ValueTask<BaselineReadResult> ReadEnumerationPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request,
        DateTimeOffset startedAtUtc)
    {
        var readFingerprint = MaterializationSourceReadFingerprinter.Compute(request.Read);
        var cursor = DecodeContinuation(
            continuation: request.Continuation,
            readFingerprint: readFingerprint,
            kind: BaselineCursorKind.Enumeration,
            parameterName: nameof(request));
        var readBoundary = EffectiveReadBoundary(request.Read);
        var remainingRows = readBoundary - cursor.EmittedRows;
        if (remainingRows <= 0)
        {
            throw new ArgumentException(
                message: "The Cosmos continuation has already reached the Relations read boundary.",
                paramName: nameof(request));
        }

        var maximumItems = checked((int)Math.Min(
            Math.Min(request.MaximumItems, policy.MaximumScanPageItems),
            remainingRows));
        var providerPageSizeHint = cursor.Offset == 0
            ? Math.Min(policy.MaximumProviderPageItems, maximumItems)
            : cursor.ProviderPageSizeHint;
        var providerPage = await reader.ReadMaterializationPageAsync(
            request: request.Read,
            providerContinuation: cursor.ProviderContinuation,
            maximumProviderItems: providerPageSizeHint,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        ProviderOperationEvidence providerEvidence = new(
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: context.UtcNow,
            RequestCharge: providerPage.RequestCharge,
            StatusCode: providerPage.StatusCode,
            ProviderEvidenceReference: providerPage.ProviderEvidenceReference);
        if (providerPage.Read.State is RelationQuerySourceReadState.Failed
            or RelationQuerySourceReadState.Inconclusive)
        {
            if (cursor.Offset != 0)
            {
                throw ReplayConflict(
                    operation: CosmosMaterializationSourceOperationKind.BaselineRead,
                    providerEvidence: providerEvidence,
                    message: "A resumed baseline page no longer projected canonical provider rows.");
            }

            var diagnostics = Diagnostics(providerPage.Read);
            return new(
                Page: new(
                    scope: Scope,
                    readFingerprint: readFingerprint,
                    read: providerPage.Read,
                    state: MaterializationSourcePageState.Exhausted,
                    diagnostics: diagnostics),
                Disposition: providerPage.Read.State == RelationQuerySourceReadState.Failed
                    ? CosmosMaterializationSourceDisposition.TerminalFailure
                    : CosmosMaterializationSourceDisposition.Partial,
                RequestCharge: providerPage.RequestCharge,
                StatusCode: providerPage.StatusCode,
                EvidenceReference: providerPage.ProviderEvidenceReference ?? Evidence("baseline-provider-result"));
        }

        var observations = providerPage.Read.Observations;
        var prefixDigests = BuildPrefixDigestChain(observations);
        VerifyPrefix(
            prefixDigests: prefixDigests,
            count: cursor.Offset,
            expectedDigest: cursor.PrefixDigest,
            cursorName: "baseline continuation",
            providerEvidence: providerEvidence);
        if (cursor.LastIdentity is not null
            && cursor.Offset < observations.Length
            && StringComparer.Ordinal.Compare(observations[cursor.Offset].Identity, cursor.LastIdentity) <= 0)
        {
            throw ReplayConflict(
                operation: CosmosMaterializationSourceOperationKind.BaselineRead,
                providerEvidence: providerEvidence,
                message: "A resumed Cosmos baseline violated strict cross-page observation-identity order.");
        }
        var maximumBytes = Math.Min(request.MaximumBytes, policy.MaximumScanPageBytes);
        var selected = SelectObservationsWithEvidence(
            observations: observations,
            offset: cursor.Offset,
            maximumItems: maximumItems,
            maximumBytes: maximumBytes,
            cancellationToken: context.CancellationToken,
            providerEvidence: providerEvidence,
            consumed: out var consumed,
            encodedBytes: out _);
        var totalEmitted = checked(cursor.EmittedRows + selected.Length);
        var providerTailRemains = consumed < observations.Length || providerPage.HasMoreResults;
        var boundaryReached = providerTailRemains && totalEmitted >= readBoundary;
        var hasMore = providerTailRemains && !boundaryReached;
        MaterializationSourceContinuation? continuation = null;
        if (hasMore)
        {
            var consumedWholePage = consumed >= observations.Length;
            continuation = CreateContinuation(
                readFingerprint: readFingerprint,
                payload: new(
                    SourceProfile: Descriptor.CapabilityProfile.Id.Value,
                    ScopeDigest: scopeDigest,
                    Kind: BaselineCursorKind.Enumeration,
                    ProviderContinuation: consumedWholePage
                        ? providerPage.NextContinuationToken
                        : cursor.ProviderContinuation,
                    ProviderPageSizeHint: consumedWholePage ? 0 : providerPageSizeHint,
                    Offset: consumedWholePage ? 0 : consumed,
                    EmittedRows: totalEmitted,
                    LastIdentity: selected.IsDefaultOrEmpty ? cursor.LastIdentity : selected[^1].Identity,
                    PrefixDigest: consumedWholePage ? EmptyDigest : prefixDigests[consumed]));
        }
        var state = hasMore || boundaryReached
            ? RelationQuerySourceReadState.Partial
            : totalEmitted == 0
                ? RelationQuerySourceReadState.NotFound
                : RelationQuerySourceReadState.Complete;
        var read = new RelationQuerySourceReadResult(
            state: state,
            observations: selected,
            evidenceReference: providerPage.Read.EvidenceReference);
        var page = new MaterializationSourcePage(
            scope: Scope,
            readFingerprint: readFingerprint,
            read: read,
            state: hasMore
                ? MaterializationSourcePageState.MoreAvailable
                : MaterializationSourcePageState.Exhausted,
            continuation: continuation,
            diagnostics: boundaryReached ? BoundaryDiagnostics(read) : []);
        return new(
            Page: page,
            Disposition: hasMore || boundaryReached
                ? CosmosMaterializationSourceDisposition.Partial
                : CosmosMaterializationSourceDisposition.Complete,
            RequestCharge: providerPage.RequestCharge,
            StatusCode: providerPage.StatusCode,
            EvidenceReference: providerPage.ProviderEvidenceReference ?? Evidence("baseline-enumeration"));
    }

    async ValueTask<BaselineReadResult> ReadBufferedPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request,
        DateTimeOffset startedAtUtc)
    {
        var readFingerprint = MaterializationSourceReadFingerprinter.Compute(request.Read);
        var cursor = DecodeContinuation(
            continuation: request.Continuation,
            readFingerprint: readFingerprint,
            kind: BaselineCursorKind.BufferedRead,
            parameterName: nameof(request));
        if (cursor.ProviderContinuation is not null)
        {
            throw new ArgumentException(
                message: "A buffered Cosmos continuation cannot carry a provider query token.",
                paramName: nameof(request));
        }

        var providerRead = await reader
            .ReadMaterializationBufferedAsync(
                request: request.Read,
                cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        ProviderOperationEvidence providerEvidence = new(
            StartedAtUtc: startedAtUtc,
            CompletedAtUtc: context.UtcNow,
            RequestCharge: providerRead.RequestCharge,
            StatusCode: providerRead.StatusCode,
            ProviderEvidenceReference: providerRead.Read.EvidenceReference);
        var read = providerRead.Read;
        if (read.State is RelationQuerySourceReadState.Failed
            or RelationQuerySourceReadState.Inconclusive
            or RelationQuerySourceReadState.NotFound)
        {
            if (cursor.Offset != 0)
            {
                throw ReplayConflict(
                    operation: CosmosMaterializationSourceOperationKind.BaselineRead,
                    providerEvidence: providerEvidence,
                    message: "A resumed buffered read no longer projects its prior canonical prefix.");
            }

            return new(
                Page: new(
                    scope: Scope,
                    readFingerprint: readFingerprint,
                    read: read,
                    state: MaterializationSourcePageState.Exhausted,
                    diagnostics: Diagnostics(read)),
                Disposition: read.State switch
                {
                    RelationQuerySourceReadState.Failed => CosmosMaterializationSourceDisposition.TerminalFailure,
                    RelationQuerySourceReadState.Inconclusive => CosmosMaterializationSourceDisposition.Partial,
                    _ => CosmosMaterializationSourceDisposition.Complete
                },
                RequestCharge: providerRead.RequestCharge,
                StatusCode: providerRead.StatusCode,
                EvidenceReference: read.EvidenceReference ?? Evidence("baseline-buffered-result"));
        }

        var prefixDigests = BuildPrefixDigestChain(read.Observations);
        VerifyPrefix(
            prefixDigests: prefixDigests,
            count: cursor.Offset,
            expectedDigest: cursor.PrefixDigest,
            cursorName: "baseline continuation",
            providerEvidence: providerEvidence);
        var selected = SelectObservationsWithEvidence(
            observations: read.Observations,
            offset: cursor.Offset,
            maximumItems: Math.Min(request.MaximumItems, policy.MaximumScanPageItems),
            maximumBytes: Math.Min(request.MaximumBytes, policy.MaximumScanPageBytes),
            cancellationToken: context.CancellationToken,
            providerEvidence: providerEvidence,
            consumed: out var consumed,
            encodedBytes: out _);
        var hasMore = consumed < read.Observations.Length;
        var totalEmitted = checked(cursor.EmittedRows + selected.Length);
        var continuation = hasMore
            ? CreateContinuation(
                readFingerprint: readFingerprint,
                payload: new(
                    SourceProfile: Descriptor.CapabilityProfile.Id.Value,
                    ScopeDigest: scopeDigest,
                    Kind: BaselineCursorKind.BufferedRead,
                    ProviderContinuation: null,
                    ProviderPageSizeHint: 0,
                    Offset: consumed,
                    EmittedRows: totalEmitted,
                    LastIdentity: selected.IsDefaultOrEmpty ? cursor.LastIdentity : selected[^1].Identity,
                    PrefixDigest: prefixDigests[consumed]))
            : null;
        var pageRead = new RelationQuerySourceReadResult(
            state: hasMore ? RelationQuerySourceReadState.Partial : read.State,
            observations: selected,
            evidenceReference: read.EvidenceReference);
        return new(
            Page: new(
                scope: Scope,
                readFingerprint: readFingerprint,
                read: pageRead,
                state: hasMore
                    ? MaterializationSourcePageState.MoreAvailable
                    : MaterializationSourcePageState.Exhausted,
                continuation: continuation,
                diagnostics: !hasMore && read.State == RelationQuerySourceReadState.Partial
                    ? BoundaryDiagnostics(pageRead)
                    : []),
            Disposition: hasMore || read.State == RelationQuerySourceReadState.Partial
                ? CosmosMaterializationSourceDisposition.Partial
                : CosmosMaterializationSourceDisposition.Complete,
            RequestCharge: providerRead.RequestCharge,
            StatusCode: providerRead.StatusCode,
            EvidenceReference: read.EvidenceReference ?? Evidence("baseline-buffered"));
    }

    MaterializationChangeDelivery? ProjectChange(
        CosmosMaterializationProviderChange provider,
        ReadOnlySpan<byte> canonicalProviderRecord,
        MaterializationSourcePosition position,
        string providerEvidence,
        ProviderOperationEvidence operationEvidence)
    {
        var observedAtUtc = operationEvidence.CompletedAtUtc;
        var currentMatches = MatchesScope(provider.Current);
        var previousMatches = MatchesScope(provider.Previous);
        CosmosObservationContainerDocument? beforeDocument = null;
        CosmosObservationContainerDocument? afterDocument = null;
        MaterializationChangeKind? kind = null;
        switch (provider.OperationType)
        {
            case CosmosMaterializationProviderChangeKind.Create:
                if (currentMatches)
                {
                    kind = MaterializationChangeKind.Create;
                    afterDocument = provider.Current!;
                }
                break;
            case CosmosMaterializationProviderChangeKind.Replace:
                if (previousMatches && currentMatches)
                {
                    kind = MaterializationChangeKind.Update;
                    beforeDocument = provider.Previous!;
                    afterDocument = provider.Current!;
                }
                else if (previousMatches)
                {
                    kind = MaterializationChangeKind.Delete;
                    beforeDocument = provider.Previous!;
                }
                else if (currentMatches)
                {
                    kind = MaterializationChangeKind.Create;
                    afterDocument = provider.Current!;
                }
                break;
            case CosmosMaterializationProviderChangeKind.Delete:
                if (previousMatches)
                {
                    kind = MaterializationChangeKind.Delete;
                    beforeDocument = provider.Previous!;
                }
                break;
            default:
                throw ChangeEvidenceFailure(
                    providerEvidence: operationEvidence,
                    message: "Cosmos returned an unsupported full-fidelity operation kind.");
        }
        if (kind is null)
        {
            return null;
        }

        var identity = afterDocument?.ObservationId ?? beforeDocument!.ObservationId;
        var before = beforeDocument is null
            ? null
            : Observation(document: beforeDocument, providerEvidence: providerEvidence);
        var after = afterDocument is null
            ? null
            : Observation(document: afterDocument, providerEvidence: providerEvidence);
        var occurredAtUtc = ProviderTimestamp(provider.ConflictResolutionTimestamp);
        if (observedAtUtc < occurredAtUtc)
        {
            observedAtUtc = occurredAtUtc;
        }

        var stable = StableChangeIdentity(
            sourceScopeDigest: scopeDigest,
            partitionKey: afterDocument?.PartitionKey ?? beforeDocument!.PartitionKey,
            physicalItemId: afterDocument?.Id ?? beforeDocument!.Id,
            lsn: provider.Lsn,
            previousLsn: provider.PreviousLsn,
            providerKind: provider.OperationType,
            kind: kind.Value,
            identity: identity,
            canonicalProviderRecord: canonicalProviderRecord);
        var evidence = string.Concat(
            providerEvidence,
            "/change/sha256/",
            stable);
        var change = new MaterializationChangeEnvelope(
            id: new MaterializationChangeId(string.Concat("cosmos-change/sha256/", stable)),
            subjectIdentity: identity,
            scope: Scope,
            shape: reader.Shape,
            position: position,
            kind: kind.Value,
            before: before,
            after: after,
            occurredAtUtc: occurredAtUtc,
            observedAtUtc: observedAtUtc,
            evidenceReference: evidence);
        return new(
            id: new MaterializationDeliveryId(string.Concat("cosmos-delivery/sha256/", stable)),
            change: change,
            deliveredAtUtc: observedAtUtc,
            evidenceReference: evidence);
    }

    void ValidateChangeEvidence(
        CosmosMaterializationProviderChange provider,
        ProviderOperationEvidence operationEvidence)
    {
        switch (provider.OperationType)
        {
            case CosmosMaterializationProviderChangeKind.Create:
                if (provider.Current is null || provider.Previous is not null)
                {
                    throw ChangeEvidenceFailure(
                        providerEvidence: operationEvidence,
                        message: "A Cosmos create did not carry exactly one current image and no previous image.");
                }
                ValidatePhysicalChangeImage(
                    document: provider.Current,
                    imageName: "current",
                    operationEvidence: operationEvidence);
                ValidateSemanticIdentityWhenInScope(
                    document: provider.Current,
                    imageName: "current",
                    operationEvidence: operationEvidence);
                break;
            case CosmosMaterializationProviderChangeKind.Replace:
                if (provider.Current is null || provider.Previous is null)
                {
                    throw ChangeEvidenceFailure(
                        providerEvidence: operationEvidence,
                        message: "A Cosmos replace omitted its current image or the deployment-required previous image.");
                }
                ValidatePhysicalChangeImage(
                    document: provider.Previous,
                    imageName: "previous",
                    operationEvidence: operationEvidence);
                ValidatePhysicalChangeImage(
                    document: provider.Current,
                    imageName: "current",
                    operationEvidence: operationEvidence);
                if (!string.Equals(provider.Previous.Id, provider.Current.Id, StringComparison.Ordinal)
                    || !string.Equals(provider.Previous.PartitionKey, provider.Current.PartitionKey, StringComparison.Ordinal))
                {
                    throw ChangeEvidenceFailure(
                        providerEvidence: operationEvidence,
                        message: "A Cosmos replace changed physical item identity or logical partition inside one provider change.");
                }
                ValidateSemanticIdentityWhenInScope(
                    document: provider.Previous,
                    imageName: "previous",
                    operationEvidence: operationEvidence);
                ValidateSemanticIdentityWhenInScope(
                    document: provider.Current,
                    imageName: "current",
                    operationEvidence: operationEvidence);
                if (MatchesScope(provider.Previous)
                    && MatchesScope(provider.Current)
                    && !string.Equals(provider.Previous.ObservationId, provider.Current.ObservationId, StringComparison.Ordinal))
                {
                    throw ChangeEvidenceFailure(
                        providerEvidence: operationEvidence,
                        message: "A Cosmos replace changed semantic observation identity inside one provider change.");
                }
                if (MatchesScope(provider.Previous)
                    && MatchesScope(provider.Current)
                    && provider.Current.ObservationVersion <= provider.Previous.ObservationVersion)
                {
                    throw ChangeEvidenceFailure(
                        providerEvidence: operationEvidence,
                        message: "A Cosmos replace did not advance the Cohesive observation version required for same-item change ordering.");
                }
                break;
            case CosmosMaterializationProviderChangeKind.Delete:
                if (provider.Current is not null || provider.Previous is null)
                {
                    throw ChangeEvidenceFailure(
                        providerEvidence: operationEvidence,
                        message: "A Cosmos delete did not carry exactly the deployment-required previous image.");
                }
                ValidatePhysicalChangeImage(
                    document: provider.Previous,
                    imageName: "previous",
                    operationEvidence: operationEvidence);
                ValidateSemanticIdentityWhenInScope(
                    document: provider.Previous,
                    imageName: "previous",
                    operationEvidence: operationEvidence);
                if (string.IsNullOrWhiteSpace(provider.DeletedItemId)
                    || !string.Equals(provider.DeletedItemId, provider.Previous.Id, StringComparison.Ordinal))
                {
                    throw ChangeEvidenceFailure(
                        providerEvidence: operationEvidence,
                        message: "A Cosmos delete omitted its physical item identity or conflicted with its previous image.");
                }
                break;
            default:
                throw ChangeEvidenceFailure(
                    providerEvidence: operationEvidence,
                    message: "Cosmos returned an unsupported full-fidelity operation kind.");
        }
    }

    void ValidatePhysicalChangeImage(
        CosmosObservationContainerDocument document,
        string imageName,
        ProviderOperationEvidence operationEvidence)
    {
        if (string.IsNullOrWhiteSpace(document.Id))
        {
            throw ChangeEvidenceFailure(
                providerEvidence: operationEvidence,
                message: $"A required Cosmos {imageName} image omitted its physical item id.");
        }

        if (string.IsNullOrWhiteSpace(document.PartitionKey))
        {
            throw ChangeEvidenceFailure(
                providerEvidence: operationEvidence,
                message: $"A required Cosmos {imageName} image omitted its logical partition key.");
        }

    }

    void ValidateSemanticIdentityWhenInScope(
        CosmosObservationContainerDocument document,
        string imageName,
        ProviderOperationEvidence operationEvidence)
    {
        if (MatchesScope(document) && string.IsNullOrWhiteSpace(document.ObservationId))
        {
            throw ChangeEvidenceFailure(
                providerEvidence: operationEvidence,
                message: $"An in-scope Cosmos {imageName} image omitted its semantic observation identity.");
        }
        if (MatchesScope(document) && document.ObservationVersion < 0)
        {
            throw ChangeEvidenceFailure(
                providerEvidence: operationEvidence,
                message: $"An in-scope Cosmos {imageName} image carried a negative observation version.");
        }
    }

    bool MatchesScope(CosmosObservationContainerDocument? document) => document is not null
        && string.Equals(document.DocumentKind, reader.EntityDocumentKind, StringComparison.Ordinal)
        && string.Equals(document.ObservationType, reader.Shape.ShapeId.Value, StringComparison.Ordinal)
        && document.Observation is not null;

    RelationQuerySourceReadObservation Observation(
        CosmosObservationContainerDocument document,
        string providerEvidence)
    {
        var projected = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(changeFields.Length);
        foreach (var field in changeFields)
        {
            var evidence = string.Concat(
                providerEvidence,
                "/field/",
                Uri.EscapeDataString(field.SemanticPath.ToString()));
            if (!TryGetObservationValue(
                    observation: document.Observation!,
                    path: field.SemanticPath,
                    value: out var value)
                || value.Kind == ObservationValueKind.Undefined)
            {
                projected.Add(new(
                    field: field,
                    state: RelationQuerySourceReadFieldState.Missing,
                    evidenceReference: evidence));
            }
            else if (value.Kind == ObservationValueKind.Null)
            {
                projected.Add(new(
                    field: field,
                    state: RelationQuerySourceReadFieldState.Null,
                    evidenceReference: evidence));
            }
            else
            {
                projected.Add(new(
                    field: field,
                    state: RelationQuerySourceReadFieldState.Value,
                    value: value,
                    evidenceReference: evidence));
            }
        }

        return new(
            identity: document.ObservationId,
            shape: reader.Shape,
            fields: projected.MoveToImmutable());
    }

    static bool TryGetObservationValue(
        IReadOnlyDictionary<string, ObservationValue> observation,
        FieldPath path,
        out ObservationValue value)
    {
        var segments = path.Segments.AsSpan();
        if (!observation.TryGetValue(segments[0].Segment!, out value))
        {
            return false;
        }

        return segments.Length == 1
            || value.TryGetFieldSegments(
                path: segments[1..],
                value: out value);
    }

    static string CanonicalObservationFieldSelector(FieldPath semanticPath, string parameterName)
    {
        foreach (ref readonly var segment in semanticPath.Segments.AsSpan())
        {
            if (segment.Kind != SegmentKind.Field)
            {
                throw new ArgumentException(
                    message: "Cosmos full-fidelity materialization currently supports only field-navigation paths represented by the canonical observation envelope.",
                    paramName: parameterName);
            }
        }

        return CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(semanticPath);
    }

    MaterializationSourceContinuation CreateContinuation(
        MaterializationSourceReadFingerprint readFingerprint,
        BaselineCursorPayload payload) =>
        new(
            formatVersion: ContinuationFormatVersion,
            readFingerprint: readFingerprint,
            scope: Scope,
            value: Encode(codec: continuationCodec, payload: payload));

    BaselineCursorPayload DecodeContinuation(
        MaterializationSourceContinuation? continuation,
        MaterializationSourceReadFingerprint readFingerprint,
        BaselineCursorKind kind,
        string parameterName)
    {
        if (continuation is null)
        {
            return new(
                SourceProfile: Descriptor.CapabilityProfile.Id.Value,
                ScopeDigest: scopeDigest,
                Kind: kind,
                ProviderContinuation: null,
                ProviderPageSizeHint: 0,
                Offset: 0,
                EmittedRows: 0,
                LastIdentity: null,
                PrefixDigest: EmptyDigest);
        }
        if (continuation.FormatVersion != ContinuationFormatVersion
            || continuation.Value.Length > policy.MaximumCursorCharacters)
        {
            throw new ArgumentException(
                message: "The Cosmos materialization continuation version or size is unsupported.",
                paramName: parameterName);
        }
        var payload = Decode<BaselineCursorPayload>(
            value: continuation.Value,
            codec: continuationCodec,
            parameterName: parameterName,
            cursorName: "continuation");
        if (!string.Equals(payload.SourceProfile, Descriptor.CapabilityProfile.Id.Value, StringComparison.Ordinal)
            || !string.Equals(payload.ScopeDigest, scopeDigest, StringComparison.Ordinal)
            || payload.Kind != kind
            || payload.ProviderPageSizeHint < 0
            || payload.ProviderPageSizeHint > policy.MaximumProviderPageItems
            || payload.Offset < 0
            || payload.EmittedRows < 0
            || payload.Offset > payload.EmittedRows
            || (payload.LastIdentity is not null && string.IsNullOrWhiteSpace(payload.LastIdentity))
            || (payload.EmittedRows == 0) != (payload.LastIdentity is null)
            || (payload.Kind == BaselineCursorKind.Enumeration
                ? (payload.Offset == 0) != (payload.ProviderPageSizeHint == 0)
                : payload.ProviderPageSizeHint != 0)
            || !IsDigest(payload.PrefixDigest)
            || continuation.ReadFingerprint != readFingerprint)
        {
            throw new ArgumentException(
                message: "The Cosmos continuation conflicts with the source profile, scope, read, or cursor progress.",
                paramName: parameterName);
        }
        return payload;
    }

    MaterializationSourcePosition CreatePosition(ChangeCursorPayload payload) =>
        new(
            formatVersion: PositionFormatVersion,
            scope: Scope,
            value: Encode(codec: positionCodec, payload: payload));

    ChangeCursorPayload DecodePosition(MaterializationSourcePosition position, string parameterName)
    {
        if (position.FormatVersion != PositionFormatVersion
            || position.Scope != Scope
            || position.Value.Length > policy.MaximumCursorCharacters)
        {
            throw new ArgumentException(
                message: "The Cosmos source position version, scope, or size is unsupported.",
                paramName: parameterName);
        }
        var payload = Decode<ChangeCursorPayload>(
            value: position.Value,
            codec: positionCodec,
            parameterName: parameterName,
            cursorName: "position");
        if (!string.Equals(payload.SourceProfile, Descriptor.CapabilityProfile.Id.Value, StringComparison.Ordinal)
            || !string.Equals(payload.ScopeDigest, scopeDigest, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payload.ProviderContinuation)
            || payload.ProviderPageSizeHint < 0
            || payload.ProviderPageSizeHint > policy.MaximumProviderPageItems
            || payload.Offset < 0
            || (payload.Offset == 0) != (payload.ProviderPageSizeHint == 0)
            || !IsDigest(payload.PrefixDigest))
        {
            throw new ArgumentException(
                message: "The Cosmos source position conflicts with the exact profile, scope, provider boundary, or progress.",
                paramName: parameterName);
        }
        return payload;
    }

    static string Encode<T>(MaterializationAuthenticatedValueCodec codec, T payload) =>
        codec.Encode(JsonSerializer.SerializeToUtf8Bytes(
            value: payload,
            options: CanonicalJsonOptions));

    static T Decode<T>(
        string value,
        MaterializationAuthenticatedValueCodec codec,
        string parameterName,
        string cursorName)
    {
        var payloadBytes = codec.Decode(
            value: value,
            parameterName: parameterName,
            valueKind: cursorName);
        try
        {
            var payload = JsonSerializer.Deserialize<T>(
                utf8Json: payloadBytes,
                options: CanonicalJsonOptions)
                ?? throw new JsonException("Authenticated cursor payload was null.");
            if (!string.Equals(
                    Encode(codec: codec, payload: payload),
                    value,
                    StringComparison.Ordinal))
            {
                throw new JsonException("Authenticated cursor was not canonical.");
            }

            return payload;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                message: $"The Cosmos {cursorName} is malformed.",
                paramName: parameterName,
                innerException: exception);
        }
    }

    static ImmutableArray<RelationQuerySourceReadObservation> SelectObservationsWithEvidence(
        ImmutableArray<RelationQuerySourceReadObservation> observations,
        int offset,
        int maximumItems,
        long maximumBytes,
        CancellationToken cancellationToken,
        ProviderOperationEvidence providerEvidence,
        out int consumed,
        out long encodedBytes)
    {
        try
        {
            return SelectObservations(
                observations: observations,
                offset: offset,
                maximumItems: maximumItems,
                maximumBytes: maximumBytes,
                cancellationToken: cancellationToken,
                consumed: out consumed,
                encodedBytes: out encodedBytes);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new CosmosRelationQueryMaterializationCanceledException(
                cancellation: exception,
                completedRequestCharge: providerEvidence.RequestCharge,
                completedStatusCode: providerEvidence.StatusCode,
                providerEvidenceReference: providerEvidence.ProviderEvidenceReference,
                cancellationToken: cancellationToken);
        }
    }

    static ImmutableArray<RelationQuerySourceReadObservation> SelectObservations(
        ImmutableArray<RelationQuerySourceReadObservation> observations,
        int offset,
        int maximumItems,
        long maximumBytes,
        CancellationToken cancellationToken,
        out int consumed,
        out long encodedBytes)
    {
        var selected = ImmutableArray.CreateBuilder<RelationQuerySourceReadObservation>(
            Math.Min(maximumItems, observations.Length - offset));
        encodedBytes = 0;
        consumed = offset;
        for (; consumed < observations.Length && selected.Count < maximumItems; consumed++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = observations[consumed];
            var bytes = CanonicalByteCount(observation);
            if (bytes > maximumBytes)
            {
                if (selected.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Observation '{observation.Identity}' requires {bytes.ToString(CultureInfo.InvariantCulture)} canonical bytes, exceeding the indivisible bound of {maximumBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
                }
                break;
            }
            if (bytes > maximumBytes - encodedBytes)
            {
                break;
            }

            selected.Add(observation);
            encodedBytes += bytes;
        }
        return selected.Count == selected.Capacity ? selected.MoveToImmutable() : selected.ToImmutable();
    }

    void VerifyPrefix(
        ImmutableArray<string> prefixDigests,
        int count,
        string expectedDigest,
        string cursorName,
        ProviderOperationEvidence providerEvidence)
    {
        if (count < 0
            || count >= prefixDigests.Length
            || !string.Equals(prefixDigests[count], expectedDigest, StringComparison.Ordinal))
        {
            throw ReplayConflict(
                operation: cursorName.Contains("change", StringComparison.Ordinal)
                    ? CosmosMaterializationSourceOperationKind.ChangeRead
                    : CosmosMaterializationSourceOperationKind.BaselineRead,
                providerEvidence: providerEvidence,
                message: $"The {cursorName} replay observed a different provider prefix and failed closed.");
        }
    }

    static ImmutableArray<string> BuildPrefixDigestChain(
        ImmutableArray<RelationQuerySourceReadObservation> values)
    {
        var digests = ImmutableArray.CreateBuilder<string>(values.Length + 1);
        var prior = SHA256.HashData([]);
        digests.Add(Convert.ToHexStringLower(prior));
        Span<byte> length = stackalloc byte[sizeof(int)];
        for (var index = 0; index < values.Length; index++)
        {
            var bytes = StableObservationBytes(values[index]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(prior);
            hash.AppendData(length);
            hash.AppendData(bytes);
            prior = hash.GetHashAndReset();
            digests.Add(Convert.ToHexStringLower(prior));
        }
        return digests.MoveToImmutable();
    }

    static byte[] StableObservationBytes(RelationQuerySourceReadObservation observation)
    {
        var containsOperationalEvidence = false;
        foreach (var field in observation.Fields)
        {
            if (field.EvidenceReference is not null)
            {
                containsOperationalEvidence = true;
                break;
            }
        }
        if (!containsOperationalEvidence)
        {
            return StrictDocumentJson.GetCanonicalBytes(
                value: observation,
                options: CanonicalJsonOptions);
        }

        var stableFields = ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(
            observation.Fields.Length);
        foreach (var field in observation.Fields)
        {
            stableFields.Add(new(
                field: field.Field,
                state: field.State,
                value: field.Value));
        }
        var stableObservation = new RelationQuerySourceReadObservation(
            identity: observation.Identity,
            shape: observation.Shape,
            fields: stableFields.MoveToImmutable());
        return StrictDocumentJson.GetCanonicalBytes(
            value: stableObservation,
            options: CanonicalJsonOptions);
    }

    ImmutableArray<CanonicalProviderChange> CanonicalizeProviderChanges(
        ImmutableArray<CosmosMaterializationProviderChange> changes,
        ProviderOperationEvidence providerEvidence)
    {
        if (changes.IsDefaultOrEmpty)
        {
            return [];
        }

        var ordered = ImmutableArray.CreateBuilder<CanonicalProviderChange>(changes.Length);
        try
        {
            foreach (var change in changes)
            {
                var physicalImage = change.Current ?? change.Previous!;
                var currentMatches = MatchesScope(change.Current);
                var previousMatches = MatchesScope(change.Previous);
                var subjectIdentity = currentMatches
                    ? change.Current!.ObservationId
                    : previousMatches
                        ? change.Previous!.ObservationId
                        : null;
                ordered.Add(new(
                    Change: change,
                    PhysicalId: physicalImage.Id,
                    PartitionKey: physicalImage.PartitionKey,
                    FromStateFingerprint: ChangeStateFingerprint(change.Previous),
                    ToStateFingerprint: ChangeStateFingerprint(change.Current),
                    SubjectIdentity: subjectIdentity,
                    AffectsScope: currentMatches || previousMatches,
                    CanonicalBytes: StrictDocumentJson.GetCanonicalBytes(
                        value: change,
                        options: CanonicalJsonOptions)));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            throw ChangeEvidenceFailure(
                providerEvidence: providerEvidence,
                message: "A Cosmos full-fidelity record could not be canonicalized for deterministic delivery order.");
        }

        ordered.Sort(static (left, right) =>
        {
            var comparison = left.Change.Lsn.CompareTo(right.Change.Lsn);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.PartitionKey, right.PartitionKey);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.PhysicalId, right.PhysicalId);
            if (comparison != 0)
            {
                return comparison;
            }

            return left.CanonicalBytes.AsSpan().SequenceCompareTo(right.CanonicalBytes);
        });

        var chained = ImmutableArray.CreateBuilder<CanonicalProviderChange>(ordered.Count);
        for (var offset = 0; offset < ordered.Count;)
        {
            var count = 1;
            while (offset + count < ordered.Count
                   && SameTransactionalItem(
                       left: ordered[offset],
                       right: ordered[offset + count]))
            {
                count++;
            }

            AppendTransactionalItemGroup(
                ordered: ordered,
                offset: offset,
                count: count,
                destination: chained,
                providerEvidence: providerEvidence);
            offset += count;
        }
        RequireUnambiguousSemanticSubjectOrder(
            changes: chained,
            providerEvidence: providerEvidence);
        return chained.MoveToImmutable();
    }

    void RequireUnambiguousSemanticSubjectOrder(
        ImmutableArray<CanonicalProviderChange>.Builder changes,
        ProviderOperationEvidence providerEvidence)
    {
        Dictionary<(long Lsn, string SubjectIdentity), (string PartitionKey, string PhysicalId)> physicalItems = [];
        foreach (var change in changes)
        {
            if (change.SubjectIdentity is not { } subjectIdentity)
            {
                continue;
            }

            var key = (change.Change.Lsn, subjectIdentity);
            var physicalItem = (change.PartitionKey, change.PhysicalId);
            if (!physicalItems.TryGetValue(key, out var priorPhysicalItem))
            {
                physicalItems.Add(key, physicalItem);
                continue;
            }

            if (!string.Equals(
                    priorPhysicalItem.PartitionKey,
                    physicalItem.PartitionKey,
                    StringComparison.Ordinal)
                || !string.Equals(
                    priorPhysicalItem.PhysicalId,
                    physicalItem.PhysicalId,
                    StringComparison.Ordinal))
            {
                throw ChangeEvidenceFailure(
                    providerEvidence: providerEvidence,
                    message: "A Cosmos transactional page contained distinct physical items affecting the same semantic observation identity, so their relative materialization order cannot be proven.");
            }
        }
    }

    static bool SameTransactionalItem(
        CanonicalProviderChange left,
        CanonicalProviderChange right) => left.Change.Lsn == right.Change.Lsn
        && string.Equals(left.PartitionKey, right.PartitionKey, StringComparison.Ordinal)
        && string.Equals(left.PhysicalId, right.PhysicalId, StringComparison.Ordinal);

    void AppendTransactionalItemGroup(
        ImmutableArray<CanonicalProviderChange>.Builder ordered,
        int offset,
        int count,
        ImmutableArray<CanonicalProviderChange>.Builder destination,
        ProviderOperationEvidence providerEvidence)
    {
        if (count == 1 || !GroupAffectsScope(
                changes: ordered,
                offset: offset,
                count: count))
        {
            for (var index = 0; index < count; index++)
            {
                destination.Add(ordered[offset + index]);
            }
            return;
        }

        var start = -1;
        for (var candidate = 0; candidate < count; candidate++)
        {
            var hasPredecessor = false;
            for (var other = 0; other < count; other++)
            {
                if (candidate != other
                    && string.Equals(
                        ordered[offset + other].ToStateFingerprint,
                        ordered[offset + candidate].FromStateFingerprint,
                        StringComparison.Ordinal))
                {
                    hasPredecessor = true;
                    break;
                }
            }
            if (!hasPredecessor)
            {
                if (start >= 0)
                {
                    throw AmbiguousTransactionalOrder(providerEvidence);
                }
                start = candidate;
            }
        }
        if (start < 0)
        {
            throw AmbiguousTransactionalOrder(providerEvidence);
        }

        var consumed = new bool[count];
        var current = start;
        for (var emitted = 0; emitted < count; emitted++)
        {
            if (consumed[current])
            {
                throw AmbiguousTransactionalOrder(providerEvidence);
            }
            consumed[current] = true;
            var currentChange = ordered[offset + current];
            destination.Add(currentChange);
            if (emitted == count - 1)
            {
                break;
            }

            var next = -1;
            for (var candidate = 0; candidate < count; candidate++)
            {
                if (consumed[candidate]
                    || !string.Equals(
                        ordered[offset + candidate].FromStateFingerprint,
                        currentChange.ToStateFingerprint,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (next >= 0)
                {
                    throw AmbiguousTransactionalOrder(providerEvidence);
                }
                next = candidate;
            }
            if (next < 0)
            {
                throw AmbiguousTransactionalOrder(providerEvidence);
            }
            current = next;
        }
    }

    static bool GroupAffectsScope(
        ImmutableArray<CanonicalProviderChange>.Builder changes,
        int offset,
        int count)
    {
        for (var index = 0; index < count; index++)
        {
            if (changes[offset + index].AffectsScope)
            {
                return true;
            }
        }
        return false;
    }

    CosmosMaterializationSourceException AmbiguousTransactionalOrder(
        ProviderOperationEvidence providerEvidence) => ChangeEvidenceFailure(
        providerEvidence: providerEvidence,
        message: "A Cosmos transactional page contained same-item changes whose previous/current image chain does not prove one unique transition order.");

    static string ChangeStateFingerprint(CosmosObservationContainerDocument? document)
    {
        if (document is null)
        {
            return "absent";
        }

        var canonical = StrictDocumentJson.GetCanonicalBytes(
            value: document,
            options: CanonicalJsonOptions);
        return string.Concat("present/sha256/", Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    static ImmutableArray<string> BuildPrefixDigestChain(ImmutableArray<CanonicalProviderChange> values)
    {
        var digests = ImmutableArray.CreateBuilder<string>(values.Length + 1);
        var prior = SHA256.HashData([]);
        digests.Add(Convert.ToHexStringLower(prior));
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (ref readonly var value in values.AsSpan())
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, value.CanonicalBytes.Length);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(prior);
            hash.AppendData(length);
            hash.AppendData(value.CanonicalBytes);
            prior = hash.GetHashAndReset();
            digests.Add(Convert.ToHexStringLower(prior));
        }
        return digests.MoveToImmutable();
    }

    static string EmptyDigest { get; } = Convert.ToHexStringLower(SHA256.HashData([]));

    long EffectiveReadBoundary(RelationQuerySourceReadRequest read)
    {
        var enumeration = (RelationQueryBoundedEnumeration)read.Constraint;
        return Math.Min(
            enumeration.MaximumRows,
            Math.Min(
                read.MaximumBufferedRows,
                Math.Min(reader.Limits.MaximumBufferedRows, reader.Policy.MaximumEnumerationRows)));
    }

    ImmutableArray<DocumentValidationDiagnostic> Diagnostics(RelationQuerySourceReadResult read)
    {
        if (read.State is not (RelationQuerySourceReadState.Failed or RelationQuerySourceReadState.Inconclusive))
        {
            return [];
        }

        var failed = read.State == RelationQuerySourceReadState.Failed;
        return
        [
            MaterializationContract.CreateDiagnostic(
                code: failed ? SourceReadFailedDiagnosticCode : SourceReadInconclusiveDiagnosticCode,
                severity: failed ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                message: failed
                    ? "The Cosmos source read failed without producing attributable observations."
                    : "The Cosmos source read could not prove a complete bounded result.",
                location: "$runtime.sourcePage",
                stage: "source-read",
                subject: Descriptor.Source.Value,
                sourceReferences: EvidenceReferences(read.EvidenceReference),
                expected: "one complete bounded canonical Cosmos result",
                observed: failed ? "failed" : "inconclusive")
        ];
    }

    ImmutableArray<DocumentValidationDiagnostic> BoundaryDiagnostics(RelationQuerySourceReadResult read) =>
    [
        MaterializationContract.CreateDiagnostic(
            code: ReadBoundaryReachedDiagnosticCode,
            severity: DiagnosticSeverity.Warning,
            message: "The Cosmos source has more matching provider rows, but the canonical Relations request reached its declared acquisition boundary.",
            location: "$runtime.sourcePage",
            stage: "source-read",
            subject: Descriptor.Source.Value,
            sourceReferences: EvidenceReferences(read.EvidenceReference),
            expected: "authoritative exhaustion inside the declared Relations boundary",
            observed: "additional provider rows exist beyond that boundary")
    ];

    ImmutableArray<string> EvidenceReferences(string? providerEvidence) => providerEvidence is null
        ? [EvidencePrefix, Descriptor.CapabilityProfile.Id.Value]
        : [EvidencePrefix, Descriptor.CapabilityProfile.Id.Value, providerEvidence];

    CosmosMaterializationSourceException ProviderFailure(
        CosmosMaterializationSourceOperationKind operation,
        DateTimeOffset started,
        DateTimeOffset completed,
        CosmosException exception,
        bool resumedPosition,
        double completedRequestCharge = 0)
    {
        var kind = ClassifyProviderFailure(
            statusCode: exception.StatusCode,
            resumedPosition: resumedPosition);
        var disposition = FailureDisposition(kind);
        var observation = CreateObservation(
            operation: operation,
            disposition: disposition,
            started: started,
            completed: completed,
            itemCount: 0,
            byteCount: 0,
            requestCharge: AddRequestCharge(
                accumulated: completedRequestCharge,
                response: Math.Max(0, exception.RequestCharge)),
            evidenceReference: Evidence(string.Concat(
                "provider-failure/",
                (int)exception.StatusCode,
                "/",
                exception.SubStatusCode)),
            statusCode: exception.StatusCode,
            subStatusCode: exception.SubStatusCode,
            retryAfter: exception.RetryAfter);
        Observe(observation);
        return new(
            message: kind == CosmosMaterializationFailureKind.PositionUnavailable
                ? "The Cosmos full-fidelity position is unavailable inside the configured retention horizon."
                : "The Cosmos materialization source operation failed; inspect typed provider evidence.",
            failureKind: kind,
            observation: observation);
    }

    CosmosMaterializationSourceException ProviderProtocolFailure(
        CosmosMaterializationSourceOperationKind operation,
        DateTimeOffset started,
        DateTimeOffset completed,
        CosmosProviderProtocolException exception,
        bool resumedPosition,
        double requestCharge,
        HttpStatusCode? statusCode,
        string? providerEvidenceReference)
    {
        var kind = ClassifyProviderFailure(
            statusCode: statusCode,
            resumedPosition: resumedPosition);
        var observation = CreateObservation(
            operation: operation,
            disposition: FailureDisposition(kind),
            started: started,
            completed: completed,
            itemCount: 0,
            byteCount: 0,
            requestCharge: requestCharge,
            evidenceReference: FailureEvidence(
                suffix: string.Concat("provider-protocol/", Uri.EscapeDataString(exception.Reason)),
                providerEvidenceReference: providerEvidenceReference),
            statusCode: statusCode);
        Observe(observation);
        return new(
            message: kind == CosmosMaterializationFailureKind.PositionUnavailable
                ? "The Cosmos full-fidelity position was rejected by a completed provider response."
                : "The Cosmos provider response violated the materialization transport contract; inspect typed evidence.",
            failureKind: kind,
            observation: observation);
    }

    static CosmosMaterializationFailureKind ClassifyProviderFailure(
        HttpStatusCode? statusCode,
        bool resumedPosition) => statusCode switch
        {
            HttpStatusCode.TooManyRequests => CosmosMaterializationFailureKind.Throttled,
            HttpStatusCode.BadRequest when resumedPosition => CosmosMaterializationFailureKind.PositionUnavailable,
            HttpStatusCode.RequestTimeout or HttpStatusCode.Gone => CosmosMaterializationFailureKind.Transient,
            { } status when (int)status == 449 || (int)status >= 500 =>
                CosmosMaterializationFailureKind.Transient,
            _ => CosmosMaterializationFailureKind.Terminal
        };

    static CosmosMaterializationSourceDisposition FailureDisposition(
        CosmosMaterializationFailureKind kind) => kind switch
        {
            CosmosMaterializationFailureKind.Throttled => CosmosMaterializationSourceDisposition.Throttled,
            CosmosMaterializationFailureKind.Transient => CosmosMaterializationSourceDisposition.RetryableFailure,
            _ => CosmosMaterializationSourceDisposition.TerminalFailure
        };

    static double AddRequestCharge(double accumulated, double response)
    {
        var total = accumulated + response;
        if (!double.IsFinite(total) || total < 0)
        {
            throw new InvalidOperationException(
                "Cosmos materialization request charge overflowed its finite range.");
        }

        return total;
    }

    static ProviderOperationEvidence CancellationEvidence(
        DateTimeOffset started,
        DateTimeOffset completed,
        OperationCanceledException exception,
        ProviderOperationEvidence? completedProviderEvidence = null) => exception switch
        {
            CosmosRelationQueryMaterializationCanceledException canceled => new(
                StartedAtUtc: started,
                CompletedAtUtc: completed,
                RequestCharge: canceled.CompletedRequestCharge,
                StatusCode: canceled.CompletedStatusCode,
                ProviderEvidenceReference: canceled.ProviderEvidenceReference),
            CosmosProviderResponseCanceledException canceled => new(
                StartedAtUtc: started,
                CompletedAtUtc: completed,
                RequestCharge: canceled.RequestCharge,
                StatusCode: canceled.StatusCode,
                ProviderEvidenceReference: canceled.ProviderEvidenceReference),
            _ when completedProviderEvidence is { } evidence => evidence with
            {
                CompletedAtUtc = completed
            },
            _ => new(
                StartedAtUtc: started,
                CompletedAtUtc: completed,
                RequestCharge: 0,
                StatusCode: null,
                ProviderEvidenceReference: null)
        };

    CosmosMaterializationSourceException ReplayConflict(
        CosmosMaterializationSourceOperationKind operation,
        ProviderOperationEvidence providerEvidence,
        string message)
    {
        var observation = CreateObservation(
            operation: operation,
            disposition: CosmosMaterializationSourceDisposition.TerminalFailure,
            started: providerEvidence.StartedAtUtc,
            completed: providerEvidence.CompletedAtUtc,
            itemCount: 0,
            byteCount: 0,
            requestCharge: providerEvidence.RequestCharge,
            evidenceReference: FailureEvidence(
                suffix: "replay-conflict",
                providerEvidenceReference: providerEvidence.ProviderEvidenceReference),
            statusCode: providerEvidence.StatusCode);
        Observe(observation);
        return new(
            message: message,
            failureKind: CosmosMaterializationFailureKind.ReplayConflict,
            observation: observation);
    }

    CosmosMaterializationSourceException ChangeEvidenceFailure(
        ProviderOperationEvidence providerEvidence,
        string message)
    {
        var observation = CreateObservation(
            operation: CosmosMaterializationSourceOperationKind.ChangeRead,
            disposition: CosmosMaterializationSourceDisposition.TerminalFailure,
            started: providerEvidence.StartedAtUtc,
            completed: providerEvidence.CompletedAtUtc,
            itemCount: 0,
            byteCount: 0,
            requestCharge: providerEvidence.RequestCharge,
            evidenceReference: FailureEvidence(
                suffix: "change-evidence-unavailable",
                providerEvidenceReference: providerEvidence.ProviderEvidenceReference),
            statusCode: providerEvidence.StatusCode);
        Observe(observation);
        return new(
            message: message,
            failureKind: CosmosMaterializationFailureKind.ChangeEvidenceUnavailable,
            observation: observation);
    }

    CosmosMaterializationSourceObservation CreateObservation(
        CosmosMaterializationSourceOperationKind operation,
        CosmosMaterializationSourceDisposition disposition,
        DateTimeOffset started,
        DateTimeOffset completed,
        long itemCount,
        long byteCount,
        double requestCharge,
        string evidenceReference,
        HttpStatusCode? statusCode = null,
        int? subStatusCode = null,
        TimeSpan? retryAfter = null)
    {
        var elapsedMilliseconds = Math.Max(0, (completed - started).TotalMilliseconds);
        var latency = elapsedMilliseconds >= ControlQuantity.MaximumPortableValue
            ? ControlQuantity.MaximumPortableValue
            : (long)Math.Ceiling(elapsedMilliseconds);
        var rejected = disposition is CosmosMaterializationSourceDisposition.Throttled
            or CosmosMaterializationSourceDisposition.RetryableFailure
            or CosmosMaterializationSourceDisposition.TerminalFailure
                ? 10_000L
                : 0L;
        return new(
            operation: operation,
            disposition: disposition,
            scope: Scope,
            startedAtUtc: started,
            completedAtUtc: completed,
            itemCount: itemCount,
            canonicalByteCount: byteCount,
            requestCharge: requestCharge,
            measurements:
            [
                new(
                    metric: ControlMetricKind.Latency,
                    statistic: ControlStatisticKind.Last,
                    availability: ControlMeasurementAvailability.Available,
                    value: new(
                        value: latency,
                        unit: ControlUnit.Milliseconds),
                    sampleCount: 1),
                new(
                    metric: ControlMetricKind.RejectionRatio,
                    statistic: ControlStatisticKind.Last,
                    availability: ControlMeasurementAvailability.Available,
                    value: new(
                        value: rejected,
                        unit: ControlUnit.BasisPoints),
                    sampleCount: 1)
            ],
            evidenceReference: evidenceReference,
            statusCode: statusCode,
            subStatusCode: subStatusCode,
            retryAfter: retryAfter);
    }

    void Observe(CosmosMaterializationSourceObservation observation)
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
            // Observation is advisory. An observer cannot alter source semantics or cursor durability.
        }
    }

    void RequireScope(MaterializationSourceScope scope, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(
            argument: scope,
            paramName: parameterName);
        if (scope != Scope)
        {
            throw new ArgumentException(
                message: "The request targets a different Cosmos materialization scope.",
                paramName: parameterName);
        }
    }

    string Evidence(string suffix) => string.Concat(EvidencePrefix, "/", suffix);

    string FailureEvidence(string suffix, string? providerEvidenceReference) =>
        providerEvidenceReference is null
            ? Evidence(suffix)
            : string.Concat(Evidence(suffix), "/", providerEvidenceReference);

    async ValueTask<CosmosMaterializationAdmissionLease> EnterObservedAsync(
        OperationContext context,
        CosmosMaterializationSourceOperationKind operation,
        DateTimeOffset started,
        string canceledEvidence)
    {
        try
        {
            return await admission.EnterAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            Observe(CreateObservation(
                operation: operation,
                disposition: CosmosMaterializationSourceDisposition.Canceled,
                started: started,
                completed: context.UtcNow,
                itemCount: 0,
                byteCount: 0,
                requestCharge: 0,
                evidenceReference: Evidence(canceledEvidence)));
            throw;
        }
    }

    static MaterializationCapabilityProfile CreateCapabilityProfile(
        CosmosRelationQuerySourceReader reader,
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        RelationQuerySourcePlacementBinding placement,
        CosmosMaterializationSourcePolicy policy,
        string partitionDigest)
    {
        var providerParallelism = checked((int)reader.Limits.MaximumConcurrency);
        var containerParallelism = Math.Min(policy.MaximumContainerParallelism, providerParallelism);
        var partitionParallelism = Math.Min(policy.MaximumPartitionParallelism, providerParallelism);
        var parallelism = reader.Policy.FixedPartitionKey is null
            ? containerParallelism
            : Math.Min(containerParallelism, partitionParallelism);
        var readItems = Math.Min(
            policy.MaximumScanPageItems,
            checked((int)Math.Min(int.MaxValue, reader.Limits.MaximumBufferedRows)));
        var placementFingerprint = Convert.ToHexStringLower(SHA256.HashData(
            StrictDocumentJson.GetCanonicalBytes(
                value: placement,
                options: CanonicalJsonOptions)));
        var configurationReferences = ImmutableArray.Create(
            EvidencePrefix,
            string.Concat(
                "relations-source/", Uri.EscapeDataString(reader.Descriptor.Source.Value),
                "/target-profile/", Uri.EscapeDataString(reader.Descriptor.TargetProfile.Id.Value),
                "/shape/", Uri.EscapeDataString(reader.Shape.GraphId.Value), "/",
                Uri.EscapeDataString(reader.Shape.ShapeId.Value)),
            string.Concat("cosmos-account/sha256/", CosmosPhysicalAffinity.Fingerprint(reader.AccountEndpoint)),
            string.Concat("cosmos-database/", Uri.EscapeDataString(reader.DatabaseId)),
            string.Concat("cosmos-container/", Uri.EscapeDataString(reader.ContainerId)),
            string.Concat("cosmos-document-kind/", Uri.EscapeDataString(reader.EntityDocumentKind)),
            string.Concat("cosmos-identity-selector/", Uri.EscapeDataString(reader.IdentitySourceSelector)),
            string.Concat("cosmos-partition-selector/", Uri.EscapeDataString(reader.Policy.PartitionSourceSelector)),
            string.Concat("cosmos-logical-scope/sha256/", partitionDigest),
            string.Concat(
                "cosmos-query-policy/cross-partition/", ((int)reader.Policy.CrossPartitionPolicy).ToString(CultureInfo.InvariantCulture),
                "/enumeration/", reader.Policy.MaximumEnumerationRows.ToString(CultureInfo.InvariantCulture),
                "/keys/", reader.Policy.MaximumKeysPerQuery.ToString(CultureInfo.InvariantCulture),
                "/chunks/", reader.Policy.MaximumQueryChunks.ToString(CultureInfo.InvariantCulture),
                "/sdk-page/", reader.Policy.MaximumSdkPageSize.ToString(CultureInfo.InvariantCulture),
                "/read-consistency/", ((int)reader.Policy.ReadConsistencyLevel!.Value).ToString(CultureInfo.InvariantCulture),
                "/sql-bytes/", reader.Policy.RequestSizeLimits.MaximumSqlQueryUtf8Bytes.ToString(CultureInfo.InvariantCulture),
                "/request-bytes/", reader.Policy.RequestSizeLimits.MaximumRequestUtf8Bytes.ToString(CultureInfo.InvariantCulture)),
            string.Concat(
                "relations-source-limits/batch/", reader.Limits.MaximumBatchSize.ToString(CultureInfo.InvariantCulture),
                "/rows/", reader.Limits.MaximumBufferedRows.ToString(CultureInfo.InvariantCulture),
                "/fan-out/", reader.Limits.MaximumFanOut.ToString(CultureInfo.InvariantCulture),
                "/parallelism/", reader.Limits.MaximumConcurrency.ToString(CultureInfo.InvariantCulture)),
            string.Concat(
                "cosmos-full-fidelity/retention-ticks/",
                policy.FullFidelityRetention.Ticks.ToString(CultureInfo.InvariantCulture)),
            policy.ContinuousBackupEvidenceReference,
            policy.StrongConsistencyEvidenceReference,
            string.Concat(
                "cosmos-admission/container/", containerParallelism.ToString(CultureInfo.InvariantCulture),
                "/partition/", partitionParallelism.ToString(CultureInfo.InvariantCulture)),
            string.Concat(
                "cosmos-materialization-policy/scan-items/", policy.MaximumScanPageItems.ToString(CultureInfo.InvariantCulture),
                "/scan-bytes/", policy.MaximumScanPageBytes.ToString(CultureInfo.InvariantCulture),
                "/change-items/", policy.MaximumChangePageItems.ToString(CultureInfo.InvariantCulture),
                "/change-bytes/", policy.MaximumChangePageBytes.ToString(CultureInfo.InvariantCulture),
                "/provider-page/", policy.MaximumProviderPageItems.ToString(CultureInfo.InvariantCulture),
                "/cursor-characters/", policy.MaximumCursorCharacters.ToString(CultureInfo.InvariantCulture)),
            policy.PreviousImageEvidenceReference,
            string.Concat(
                "relations-physical-plan/",
                Uri.EscapeDataString(physicalPlan.Algorithm), "/",
                Uri.EscapeDataString(physicalPlan.Canonicalization), "/",
                Uri.EscapeDataString(physicalPlan.Value)),
            string.Concat("relations-placement/sha256/", placementFingerprint));
        var profileFingerprint = ComputeReferenceFingerprint(configurationReferences);
        ImmutableArray<string> sourceReferences =
        [
            .. configurationReferences,
            string.Concat("cosmos-materialization-profile/sha256/", profileFingerprint)
        ];
        var readLimits = ImmutableArray.Create(
            new MaterializationOperatingLimit(
                kind: MaterializationLimitKind.ReadItems,
                maximum: readItems),
            new MaterializationOperatingLimit(
                kind: MaterializationLimitKind.ReadBytes,
                maximum: policy.MaximumScanPageBytes),
            new MaterializationOperatingLimit(
                kind: MaterializationLimitKind.Parallelism,
                maximum: parallelism));
        var readCapabilities = ImmutableArray.CreateBuilder<MaterializationCapabilityKind>(2);
        if (placement.Kind == RelationQuerySourcePlacementBindingKind.SourceSet
            && placement.Acquisition == RelationQuerySourceAcquisitionKind.BoundedEnumeration)
        {
            readCapabilities.Add(MaterializationCapabilityKind.SourceBoundedEnumeration);
        }
        if (placement.Kind == RelationQuerySourcePlacementBindingKind.RelationshipTraversal
            && placement.Acquisition == RelationQuerySourceAcquisitionKind.BoundedLookup)
        {
            readCapabilities.Add(MaterializationCapabilityKind.SourceBatchedPointRead);
            if (!placement.RelationshipKeys.IsDefaultOrEmpty)
            {
                readCapabilities.Add(MaterializationCapabilityKind.SourceParameterizedPredicateQuery);
            }
        }
        var evidence = ImmutableArray.CreateBuilder<MaterializationCapabilityEvidence>(readCapabilities.Count + 2);
        foreach (var capability in readCapabilities)
        {
            evidence.Add(new(
                id: new($"cohesive.adapters.cosmos/materialization/{(int)capability}/v1"),
                capability: capability,
                realization: capability == MaterializationCapabilityKind.SourceBatchedPointRead
                    ? MaterializationCapabilityRealizationKind.Composed
                    : MaterializationCapabilityRealizationKind.Constrained,
                guarantees:
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.RequestLocalCompleteness,
                    MaterializationGuaranteeKind.Reconciliation
                ],
                operatingLimits: readLimits,
                sourceReferences: sourceReferences,
                description: capability == MaterializationCapabilityKind.SourceBatchedPointRead
                    ? "Bounded stable-identity acquisition composed as chunked parameterized Cosmos queries because the current binding does not prove native item-id and partition-key addresses; explicit item, byte, query, and hierarchical admission bounds apply."
                    : "Canonical Cosmos Relations acquisition with explicit item, byte, partition, provider-page, and runtime-owned hierarchical admission bounds; no cross-page snapshot claim."));
        }
        var changeGuarantees = ImmutableArray.Create(
            MaterializationGuaranteeKind.StableOrdering,
            MaterializationGuaranteeKind.BaselinePlusCatchUp,
            MaterializationGuaranteeKind.AtLeastOnceDelivery,
            MaterializationGuaranteeKind.BeforeImage);
        evidence.Add(new(
            id: new("cohesive.adapters.cosmos/materialization/continuation/v1"),
            capability: MaterializationCapabilityKind.SourceContinuation,
            realization: MaterializationCapabilityRealizationKind.Constrained,
            guarantees:
            [
                MaterializationGuaranteeKind.StableOrdering,
                MaterializationGuaranteeKind.Reconciliation
            ],
            operatingLimits:
            [
                new MaterializationOperatingLimit(
                    kind: MaterializationLimitKind.Parallelism,
                    maximum: parallelism)
            ],
            sourceReferences: sourceReferences,
            description: "Authenticated adapter-owned cursor retaining opaque Cosmos provider continuation and intra-page progress; a changed replay prefix fails closed and requires a new generation."));
        evidence.Add(new(
            id: new("cohesive.adapters.cosmos/materialization/change-delivery/v1"),
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            realization: MaterializationCapabilityRealizationKind.Constrained,
            guarantees: changeGuarantees,
            operatingLimits:
            [
                new MaterializationOperatingLimit(
                    kind: MaterializationLimitKind.ChangeItems,
                    maximum: policy.MaximumChangePageItems),
                new MaterializationOperatingLimit(
                    kind: MaterializationLimitKind.ReadBytes,
                    maximum: policy.MaximumChangePageBytes),
                new MaterializationOperatingLimit(
                    kind: MaterializationLimitKind.Parallelism,
                    maximum: parallelism)
            ],
            sourceReferences: sourceReferences,
            description: "Full-fidelity create, replace, and delete delivery from captured current cuts within one fixed logical partition and the attested retention horizon; selected fields and correlation keys are projected from current and previous observation envelopes, same-item transaction order requires one unique full-image transition chain, cross-item semantic-subject collisions fail closed, and no settlement is claimed."));
        var profileId = string.Concat(
            "cohesive.adapters.cosmos/materialization-source/v2/sha256/",
            profileFingerprint);
        return new(
            id: new(profileId),
            role: MaterializationEndpointRole.Source,
            subject: reader.Descriptor.Source.Value,
            evidence: evidence.MoveToImmutable(),
            description: "Cosmos baseline and full-fidelity catch-up source with durable adapter-owned positions, runtime-owned hierarchical admission, and explicit deployment evidence.");
    }

    static Container ValidateContainer(CosmosRelationQuerySourceReader reader, Container container)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(container);
        if (!string.Equals(container.Database.Id, reader.DatabaseId, StringComparison.Ordinal)
            || !string.Equals(container.Id, reader.ContainerId, StringComparison.Ordinal)
            || !string.Equals(
                CosmosPhysicalAffinity.CanonicalAccountEndpointText(container.Database.Client.Endpoint),
                reader.AccountEndpoint,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                message: "The Cosmos change-feed container must exactly match the wrapped canonical Relations reader.",
                paramName: nameof(container));
        }
        if (container.Database.Client.ClientOptions.ConsistencyLevel is { } consistency
            && consistency != ConsistencyLevel.Strong)
        {
            throw new ArgumentException(
                message: "The Cosmos change-feed client must inherit the caller-attested Strong account policy or explicitly request Strong consistency.",
                paramName: nameof(container));
        }
        return container;
    }

    static string ComputeScopeDigest(MaterializationSourceScope scope, string profile)
    {
        var text = string.Concat(
            profile, "\0",
            scope.PhysicalPlan.Algorithm, "\0",
            scope.PhysicalPlan.Canonicalization, "\0",
            scope.PhysicalPlan.Value, "\0",
            scope.Placement.Id.Value, "\0",
            scope.Partition.Value, "\0",
            scope.OrderingScope.Value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    static string ComputeReferenceFingerprint(ImmutableArray<string> references)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var reference in references)
        {
            var bytes = Encoding.UTF8.GetBytes(reference);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static string StableChangeIdentity(
        string sourceScopeDigest,
        string partitionKey,
        string physicalItemId,
        long lsn,
        long previousLsn,
        CosmosMaterializationProviderChangeKind providerKind,
        MaterializationChangeKind kind,
        string identity,
        ReadOnlySpan<byte> canonicalProviderRecord)
    {
        var text = string.Concat(
            sourceScopeDigest, "\0",
            partitionKey, "\0",
            physicalItemId, "\0",
            lsn.ToString(CultureInfo.InvariantCulture), "\0",
            previousLsn.ToString(CultureInfo.InvariantCulture), "\0",
            ((int)providerKind).ToString(CultureInfo.InvariantCulture), "\0",
            ((int)kind).ToString(CultureInfo.InvariantCulture), "\0",
            identity, "\0");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(text));
        hash.AppendData(canonicalProviderRecord);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static DateTimeOffset ProviderTimestamp(DateTime timestamp)
    {
        var utc = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };
        return new DateTimeOffset(utc);
    }

    static long CanonicalByteCount<T>(T item) where T : class =>
        StrictDocumentJson.GetCanonicalBytes(
            value: item,
            options: CanonicalJsonOptions).LongLength;

    static long CanonicalByteCount(ImmutableArray<RelationQuerySourceReadObservation> observations)
    {
        long total = 0;
        foreach (var observation in observations)
        {
            total = checked(total + CanonicalByteCount(observation));
        }

        return total;
    }

    static bool IsDigest(string value) => value.Length == 64
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    enum BaselineCursorKind
    {
        Enumeration = 0,
        BufferedRead = 1
    }

    sealed record BaselineCursorPayload(
        string SourceProfile,
        string ScopeDigest,
        BaselineCursorKind Kind,
        string? ProviderContinuation,
        int ProviderPageSizeHint,
        int Offset,
        long EmittedRows,
        string? LastIdentity,
        string PrefixDigest);

    sealed record ChangeCursorPayload(
        string SourceProfile,
        string ScopeDigest,
        string ProviderContinuation,
        int ProviderPageSizeHint,
        int Offset,
        string PrefixDigest);

    readonly record struct CanonicalProviderChange(
        CosmosMaterializationProviderChange Change,
        string PhysicalId,
        string PartitionKey,
        string FromStateFingerprint,
        string ToStateFingerprint,
        string? SubjectIdentity,
        bool AffectsScope,
        byte[] CanonicalBytes);

    readonly record struct ProviderOperationEvidence(
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        double RequestCharge,
        HttpStatusCode? StatusCode,
        string? ProviderEvidenceReference);

    readonly record struct BaselineReadResult(
        MaterializationSourcePage Page,
        CosmosMaterializationSourceDisposition Disposition,
        double RequestCharge,
        HttpStatusCode? StatusCode,
        string EvidenceReference);

}
