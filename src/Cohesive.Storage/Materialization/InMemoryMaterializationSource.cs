using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Deterministic reference materialization source that pages one exact Relations reader and replays seeded changes.
/// </summary>
/// <remarks>
/// The fake intentionally has no progress-store dependency: reading observations or changes cannot implicitly
/// checkpoint or settle them. The wrapped Relations reader should itself be deterministic while a multi-page read
/// is in progress; this reference fake does not claim a cross-request snapshot guarantee.
/// </remarks>
public sealed class InMemoryMaterializationSource : IMaterializationSettlingSource
{
    const int ContinuationFormatVersion = 1;
    const string ContinuationPrefix = "in-memory-offset/";
    const int ChangePositionFormatVersion = 1;
    const string ChangePositionPrefix = "in-memory-change-offset/";
    static readonly JsonSerializerOptions CanonicalJsonOptions = MaterializationJsonSerializer.CreateOptions();

    readonly ImmutableArray<MaterializationChangeDelivery> changes;
    readonly object settlementGate = new();
    readonly Dictionary<MaterializationSettlementId, SettlementRecord> settlements = [];

    /// <summary>Creates a deterministic reference source.</summary>
    /// <param name="descriptor">Exact Relations reader and attributable source capability profile.</param>
    /// <param name="changes">Source-ordered change deliveries available from the fake.</param>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The descriptor advertises retained-history start even though this base source does not expose its required
    /// interface, or <paramref name="changes"/> contains a null entry, duplicate delivery identity, or a delivery
    /// from another source.
    /// </exception>
    public InMemoryMaterializationSource(
        MaterializationSourceDescriptor descriptor,
        ImmutableArray<MaterializationChangeDelivery> changes = default)
        : this(descriptor, changes, retainedStartExposed: false)
    {
    }

    InMemoryMaterializationSource(
        MaterializationSourceDescriptor descriptor,
        ImmutableArray<MaterializationChangeDelivery> changes,
        bool retainedStartExposed)
    {
        Descriptor = Guard.RequireNotNull(descriptor);
        var advertisesRetainedStart = descriptor.CapabilityProfile.Evidence.Any(static evidence =>
            evidence.Capability == MaterializationCapabilityKind.SourceChangeDelivery
            && evidence.Guarantees.Contains(MaterializationGuaranteeKind.RetainedHistoryStart));
        if (advertisesRetainedStart != retainedStartExposed)
        {
            throw new ArgumentException(
                retainedStartExposed
                    ? "A retained in-memory source must advertise retained-history start."
                    : "The base in-memory source cannot advertise retained-history start without exposing the retained-change-source interface.",
                nameof(descriptor));
        }
        var normalized = changes.IsDefault ? [] : changes;
        HashSet<MaterializationDeliveryId> deliveryIds = [];
        foreach (var delivery in normalized)
        {
            if (delivery is null)
            {
                throw new ArgumentException("Reference-source changes cannot contain null entries.", nameof(changes));
            }

            if (delivery.Change.Scope.Source != descriptor.Source)
            {
                throw new ArgumentException("Every seeded change must belong to the descriptor source.", nameof(changes));
            }

            if (!deliveryIds.Add(delivery.Id))
            {
                throw new ArgumentException("Seeded changes cannot repeat a delivery identity.", nameof(changes));
            }
        }

        this.changes = normalized;
    }

    internal static InMemoryMaterializationSource CreateRetained(
        MaterializationSourceDescriptor descriptor,
        ImmutableArray<MaterializationChangeDelivery> changes) =>
        new(descriptor, changes, retainedStartExposed: true);

    /// <inheritdoc />
    public MaterializationSourceDescriptor Descriptor { get; }

    /// <inheritdoc />
    public async ValueTask<MaterializationSourcePage> ReadPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        RequireSource(request.Scope.Source, nameof(request));
        MaterializationCapabilityLimits.RequireSupportedBounds(
            Descriptor.CapabilityProfile,
            MaterializationSourceAcquisitionCatalog.GetReadCapability(request.Read.Constraint),
            MaterializationLimitKind.ReadItems,
            request.MaximumItems,
            MaterializationLimitKind.ReadBytes,
            request.MaximumBytes,
            nameof(request));

        var offset = DecodeOffset(request.Continuation, nameof(request));
        var readFingerprint = MaterializationSourceReadFingerprinter.Compute(request.Read);
        var complete = await Descriptor.RelationReader
            .ReadAsync(request.Read, context.CancellationToken)
            .ConfigureAwait(false);

        if (complete.State is RelationQuerySourceReadState.Failed
            or RelationQuerySourceReadState.Inconclusive
            or RelationQuerySourceReadState.NotFound)
        {
            if (offset != 0)
            {
                throw new ArgumentException("A continuation cannot resume a terminal Relations read.", nameof(request));
            }

            return new MaterializationSourcePage(
                request.Scope,
                readFingerprint,
                complete,
                MaterializationSourcePageState.Exhausted);
        }

        var observations = complete.Observations;
        if (offset > observations.Length)
        {
            throw new ArgumentException("The continuation lies beyond the current deterministic result.", nameof(request));
        }

        var capacity = Math.Min(request.MaximumItems, observations.Length - offset);
        var page = ImmutableArray.CreateBuilder<RelationQuerySourceReadObservation>(capacity);
        long encodedBytes = 0;
        for (var index = offset; index < observations.Length && page.Count < request.MaximumItems; index++)
        {
            var observation = observations[index];
            var observationBytes = CanonicalByteCount(observation);
            if (observationBytes > request.MaximumBytes)
            {
                if (page.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Observation '{observation.Identity}' requires {observationBytes} canonical bytes, "
                        + $"which exceeds the indivisible item bound of {request.MaximumBytes} bytes.");
                }

                break;
            }
            if (observationBytes > request.MaximumBytes - encodedBytes)
            {
                break;
            }

            page.Add(observation);
            encodedBytes += observationBytes;
        }

        var pageObservations = page.Count == page.Capacity
            ? page.MoveToImmutable()
            : page.ToImmutable();
        var nextOffset = offset + pageObservations.Length;
        var hasMore = nextOffset < observations.Length;
        var state = hasMore ? RelationQuerySourceReadState.Partial : complete.State;
        var read = new RelationQuerySourceReadResult(state, pageObservations, complete.EvidenceReference);
        var continuation = hasMore
            ? new MaterializationSourceContinuation(
                ContinuationFormatVersion,
                readFingerprint,
                request.Scope,
                EncodeOffset(nextOffset))
            : null;
        return new MaterializationSourcePage(
            request.Scope,
            readFingerprint,
            read,
            hasMore ? MaterializationSourcePageState.MoreAvailable : MaterializationSourcePageState.Exhausted,
            continuation);
    }

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);
        context.CancellationToken.ThrowIfCancellationRequested();
        RequireSource(scope.Source, nameof(scope));
        return ValueTask.FromResult(new MaterializationSourcePosition(
            ChangePositionFormatVersion,
            scope,
            EncodeChangeOffset(changes.Length)));
    }

    internal ValueTask<MaterializationSourcePosition> CaptureRetainedStartPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scope);
        context.CancellationToken.ThrowIfCancellationRequested();
        RequireSource(scope.Source, nameof(scope));
        return ValueTask.FromResult(new MaterializationSourcePosition(
            ChangePositionFormatVersion,
            scope,
            EncodeChangeOffset(0)));
    }

    /// <inheritdoc />
    public ValueTask<MaterializationChangePage> ReadChangesAsync(
        OperationContext context,
        MaterializationChangeReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        RequireSource(request.Scope.Source, nameof(request));
        MaterializationCapabilityLimits.RequireSupportedBounds(
            Descriptor.CapabilityProfile,
            MaterializationCapabilityKind.SourceChangeDelivery,
            MaterializationLimitKind.ChangeItems,
            request.MaximumDeliveries,
            MaterializationLimitKind.ReadBytes,
            request.MaximumBytes,
            nameof(request));

        var firstMatch = DecodeChangeOffset(request.AfterPosition, nameof(request));
        if (firstMatch > changes.Length)
        {
            throw new ArgumentException("The supplied source position lies beyond the retained change feed.", nameof(request));
        }

        var capacity = Math.Min(request.MaximumDeliveries, changes.Length);
        var selected = ImmutableArray.CreateBuilder<MaterializationChangeDelivery>(capacity);
        var matchingAfterPage = false;
        var throughOffset = firstMatch;
        long encodedBytes = 0;
        for (var index = firstMatch; index < changes.Length; index++)
        {
            var delivery = changes[index];
            if (!Matches(request, delivery))
            {
                continue;
            }

            if (selected.Count >= request.MaximumDeliveries)
            {
                matchingAfterPage = true;
                break;
            }

            var deliveryBytes = CanonicalByteCount(delivery);
            if (deliveryBytes > request.MaximumBytes)
            {
                if (selected.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Delivery '{delivery.Id.Value}' requires {deliveryBytes} canonical bytes, "
                        + $"which exceeds the indivisible item bound of {request.MaximumBytes} bytes.");
                }

                matchingAfterPage = true;
                break;
            }
            if (deliveryBytes > request.MaximumBytes - encodedBytes)
            {
                matchingAfterPage = true;
                break;
            }

            selected.Add(delivery);
            encodedBytes += deliveryBytes;
            throughOffset = index + 1;
        }

        var page = selected.Count == selected.Capacity
            ? selected.MoveToImmutable()
            : selected.ToImmutable();
        if (!matchingAfterPage)
        {
            throughOffset = changes.Length;
        }

        return ValueTask.FromResult(new MaterializationChangePage(
            page,
            new MaterializationSourcePosition(
                ChangePositionFormatVersion,
                request.Scope,
                EncodeChangeOffset(throughOffset)),
            matchingAfterPage
                ? MaterializationChangePageState.MoreAvailable
                : MaterializationChangePageState.CaughtUp));
    }

    /// <inheritdoc />
    public ValueTask<MaterializationSourceSettlementResult> SettleAsync(
        OperationContext context,
        MaterializationSourceSettlementRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.CancellationToken.ThrowIfCancellationRequested();
        RequireSource(request.Position.Scope.Source, nameof(request));
        var settledOffset = DecodeChangeOffset(request.Position, nameof(request));
        if (settledOffset > changes.Length)
        {
            throw new ArgumentException("The supplied settlement position lies beyond the retained change feed.", nameof(request));
        }

        lock (settlementGate)
        {
            if (settlements.TryGetValue(request.Id, out var prior))
            {
                if (prior.Request == request)
                {
                    return ValueTask.FromResult(new MaterializationSourceSettlementResult(
                        MaterializationSourceSettlementDisposition.Replayed,
                        prior.Receipt));
                }

                return ValueTask.FromResult(SettlementRejected(
                    MaterializationSourceSettlementDisposition.IdentityConflict,
                    MaterializationSourceDiagnosticCodes.SettlementIdentityConflict,
                    "The settlement identity was already used for a different source acknowledgement request.",
                    request,
                    expected: "an unused settlement identity or an exact replay of the prior request",
                    observed: "the identity is bound to a different checkpoint or source position"));
            }

            var settledAtUtc = context.UtcNow;
            if (settledAtUtc < request.RequestedAtUtc)
            {
                return ValueTask.FromResult(SettlementRejected(
                    MaterializationSourceSettlementDisposition.Rejected,
                    MaterializationSourceDiagnosticCodes.SettlementClockRegression,
                    "The source acknowledgement clock precedes the request time.",
                    request,
                    expected: $"settledAtUtc>={request.RequestedAtUtc.ToString("O", CultureInfo.InvariantCulture)}",
                    observed: settledAtUtc.ToString("O", CultureInfo.InvariantCulture)));
            }

            MaterializationSourceSettlement receipt = new(
                request.Id,
                request.Checkpoint,
                request.Position,
                settledAtUtc,
                "cohesive.storage.in-memory/settlement/v1");
            settlements.Add(request.Id, new(request, receipt));
            return ValueTask.FromResult(new MaterializationSourceSettlementResult(
                MaterializationSourceSettlementDisposition.Acknowledged,
                receipt));
        }
    }

    static bool Matches(
        MaterializationChangeReadRequest request,
        MaterializationChangeDelivery delivery) =>
        delivery.Change.Scope == request.Scope;

    static long CanonicalByteCount<T>(T item) where T : class =>
        StrictDocumentJson.GetCanonicalBytes(item, CanonicalJsonOptions).LongLength;

    void RequireSource(Cohesive.Relations.Physical.RelationQuerySourceInstanceId source, string parameterName)
    {
        if (source != Descriptor.Source)
        {
            throw new ArgumentException("The request targets a different physical source.", parameterName);
        }
    }

    static int DecodeOffset(MaterializationSourceContinuation? continuation, string parameterName)
    {
        if (continuation is null)
        {
            return 0;
        }

        if (continuation.FormatVersion != ContinuationFormatVersion
            || !continuation.Value.StartsWith(ContinuationPrefix, StringComparison.Ordinal)
            || !int.TryParse(
                continuation.Value.AsSpan(ContinuationPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var offset)
            || offset <= 0
            || !string.Equals(EncodeOffset(offset), continuation.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException("The reference-source continuation is unsupported or malformed.", parameterName);
        }

        return offset;
    }

    static string EncodeOffset(int offset) =>
        string.Concat(ContinuationPrefix, offset.ToString(CultureInfo.InvariantCulture));

    static int DecodeChangeOffset(MaterializationSourcePosition position, string parameterName)
    {
        if (position.FormatVersion != ChangePositionFormatVersion
            || !position.Value.StartsWith(ChangePositionPrefix, StringComparison.Ordinal)
            || !int.TryParse(
                position.Value.AsSpan(ChangePositionPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var offset)
            || offset < 0
            || !string.Equals(EncodeChangeOffset(offset), position.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException("The reference-source change position is unsupported or malformed.", parameterName);
        }

        return offset;
    }

    static string EncodeChangeOffset(int offset) =>
        string.Concat(ChangePositionPrefix, offset.ToString(CultureInfo.InvariantCulture));

    MaterializationSourceSettlementResult SettlementRejected(
        MaterializationSourceSettlementDisposition disposition,
        string code,
        string message,
        MaterializationSourceSettlementRequest request,
        string expected,
        string observed) => new(
        disposition,
        receipt: null,
        [MaterializationContract.CreateDiagnostic(
            code,
            DiagnosticSeverity.Error,
            message,
            $"/settlements/{Uri.EscapeDataString(request.Id.Value)}",
            "materialization-source-settlement",
            request.Id.Value,
            [Descriptor.CapabilityProfile.Id.Value, "cohesive.storage.in-memory/v1"],
            expected,
            observed)]);

    sealed record SettlementRecord(
        MaterializationSourceSettlementRequest Request,
        MaterializationSourceSettlement Receipt);
}

/// <summary>
/// Deterministic in-memory materialization source whose descriptor explicitly guarantees retained-history capture.
/// </summary>
/// <remarks>
/// This capability-specific wrapper keeps interface discovery truthful: an in-memory source whose profile does not
/// advertise <see cref="MaterializationGuaranteeKind.RetainedHistoryStart"/> does not implement
/// <see cref="IMaterializationRetainedChangeSource"/>.
/// </remarks>
public sealed class InMemoryRetainedMaterializationSource : IMaterializationSettlingSource, IMaterializationRetainedChangeSource
{
    readonly InMemoryMaterializationSource source;

    /// <summary>Creates a deterministic source with explicit retained-history-start support.</summary>
    /// <param name="descriptor">Exact Relations reader and attributable source capability profile.</param>
    /// <param name="changes">Source-ordered change deliveries available from the fake.</param>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The profile does not advertise retained-history start, or <paramref name="changes"/> contains a null entry,
    /// duplicate delivery identity, or a delivery from another source.
    /// </exception>
    public InMemoryRetainedMaterializationSource(
        MaterializationSourceDescriptor descriptor,
        ImmutableArray<MaterializationChangeDelivery> changes = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.CapabilityProfile.Evidence.Any(static evidence =>
                evidence.Capability == MaterializationCapabilityKind.SourceChangeDelivery
                && evidence.Guarantees.Contains(MaterializationGuaranteeKind.RetainedHistoryStart)))
        {
            throw new ArgumentException(
                "A retained in-memory source profile must advertise retained-history start.",
                nameof(descriptor));
        }

        source = InMemoryMaterializationSource.CreateRetained(descriptor, changes);
    }

    /// <inheritdoc />
    public MaterializationSourceDescriptor Descriptor => source.Descriptor;

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePage> ReadPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request) => source.ReadPageAsync(context, request);

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope) => source.CaptureCurrentPositionAsync(context, scope);

    /// <inheritdoc />
    public ValueTask<MaterializationSourcePosition> CaptureRetainedStartPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope) => source.CaptureRetainedStartPositionAsync(context, scope);

    /// <inheritdoc />
    public ValueTask<MaterializationChangePage> ReadChangesAsync(
        OperationContext context,
        MaterializationChangeReadRequest request) => source.ReadChangesAsync(context, request);

    /// <inheritdoc />
    public ValueTask<MaterializationSourceSettlementResult> SettleAsync(
        OperationContext context,
        MaterializationSourceSettlementRequest request) => source.SettleAsync(context, request);
}
