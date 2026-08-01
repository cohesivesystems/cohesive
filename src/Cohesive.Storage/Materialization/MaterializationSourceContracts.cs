using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable provider-neutral identity of one logical materialization source partition.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationSourcePartitionId
{
    /// <summary>Creates a source-partition identity.</summary>
    /// <param name="value">Non-empty stable partition identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationSourcePartitionId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable partition identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable partition identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one source ordering domain within a materialization partition.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationOrderingScopeId
{
    /// <summary>Creates a source ordering-scope identity.</summary>
    /// <param name="value">Non-empty adapter-stable ordering-scope identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationOrderingScopeId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable ordering-scope identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable ordering-scope identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Exact source-feed scope for one canonical Relations physical plan and source-placement binding, partition, and
/// ordering domain.
/// </summary>
public sealed record MaterializationSourceScope
{
    /// <summary>Creates an exact source-feed scope.</summary>
    /// <param name="physicalPlan">Exact physical-plan fingerprint authorizing reads in this scope.</param>
    /// <param name="placement">Canonical placement that binds one Relations acquisition input to its source.</param>
    /// <param name="partition">Logical materialization partition.</param>
    /// <param name="orderingScope">Adapter-stable domain within which source positions are ordered.</param>
    /// <exception cref="ArgumentNullException"><paramref name="physicalPlan"/> or <paramref name="placement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default or the placement cannot perform a source read.</exception>
    [JsonConstructor]
    public MaterializationSourceScope(
        RelationQueryPhysicalPlanFingerprint physicalPlan,
        RelationQuerySourcePlacementBinding placement,
        MaterializationSourcePartitionId partition,
        MaterializationOrderingScopeId orderingScope)
    {
        PhysicalPlan = Guard.RequireNotNull(physicalPlan);
        Placement = Guard.RequireNotNull(placement);
        if (placement.Acquisition == RelationQuerySourceAcquisitionKind.Supplied)
        {
            throw new ArgumentException("A materialization source scope requires an externally readable placement.", nameof(placement));
        }

        MaterializationContract.RequirePartition(partition, nameof(partition));
        MaterializationContract.RequireDefinedIdentity(orderingScope.Value, nameof(orderingScope));
        Partition = partition;
        OrderingScope = orderingScope;
    }

    /// <summary>Exact physical-plan fingerprint authorizing reads in this scope.</summary>
    public RelationQueryPhysicalPlanFingerprint PhysicalPlan { get; }

    /// <summary>Canonical placement binding one Relations acquisition input to its source.</summary>
    public RelationQuerySourcePlacementBinding Placement { get; }

    /// <summary>Canonical Relations acquisition source input projected from <see cref="Placement"/>.</summary>
    [JsonIgnore]
    public RelationQueryInputId Input => Placement.Input;

    /// <summary>Physical source instance projected from <see cref="Placement"/>.</summary>
    [JsonIgnore]
    public RelationQuerySourceInstanceId Source => Placement.Source;

    /// <summary>Graph-qualified shape supplied by <see cref="Placement"/>.</summary>
    [JsonIgnore]
    public QualifiedShapeId Shape => Placement.Shape;

    /// <summary>Logical materialization partition.</summary>
    public MaterializationSourcePartitionId Partition { get; }

    /// <summary>Adapter-stable domain within which source positions are ordered.</summary>
    public MaterializationOrderingScopeId OrderingScope { get; }
}

/// <summary>
/// Opaque, versioned continuation scoped to one exact materialization source feed.
/// </summary>
/// <remarks>
/// A continuation is portable storage data, not a provider SDK object. Consumers may persist and return
/// <see cref="Value"/> but must not inspect or compare its provider-defined contents.
/// </remarks>
public sealed record MaterializationSourceContinuation
{
    /// <summary>Creates a source continuation.</summary>
    /// <param name="formatVersion">Positive version of the opaque continuation representation.</param>
    /// <param name="readFingerprint">Fingerprint of the exact Relations source-read intent that issued the continuation.</param>
    /// <param name="scope">Exact acquisition input, source, partition, and ordering scope that issued the continuation.</param>
    /// <param name="value">Non-empty opaque continuation value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="readFingerprint"/>, <paramref name="scope"/>, or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatVersion"/> is not positive.</exception>
    [JsonConstructor]
    public MaterializationSourceContinuation(
        int formatVersion,
        MaterializationSourceReadFingerprint readFingerprint,
        MaterializationSourceScope scope,
        string value)
    {
        if (formatVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion, "A continuation version must be positive.");
        }

        ReadFingerprint = Guard.RequireNotNull(readFingerprint);
        Scope = Guard.RequireNotNull(scope);
        FormatVersion = formatVersion;
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Positive version of the opaque continuation representation.</summary>
    public int FormatVersion { get; }

    /// <summary>Fingerprint of the exact Relations source-read intent that issued the continuation.</summary>
    public MaterializationSourceReadFingerprint ReadFingerprint { get; }

    /// <summary>Exact source-feed scope that issued the continuation.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Opaque continuation value.</summary>
    public string Value { get; }
}

/// <summary>
/// Runtime binding of the canonical Relations reader to attributable materialization-source capabilities.
/// </summary>
public sealed class MaterializationSourceDescriptor
{
    /// <summary>Creates a source descriptor.</summary>
    /// <param name="relationReader">Exact canonical Relations source reader used for semantic reads.</param>
    /// <param name="capabilityProfile">Attributable materialization capabilities for the same source.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relationReader"/> or <paramref name="capabilityProfile"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The profile is not a source profile or describes a different physical source instance.
    /// </exception>
    public MaterializationSourceDescriptor(
        IRelationQuerySourceReader relationReader,
        MaterializationCapabilityProfile capabilityProfile)
    {
        RelationReader = Guard.RequireNotNull(relationReader);
        CapabilityProfile = Guard.RequireNotNull(capabilityProfile);
        if (capabilityProfile.Role != MaterializationEndpointRole.Source)
        {
            throw new ArgumentException("A materialization source requires a source capability profile.", nameof(capabilityProfile));
        }

        if (!string.Equals(
                capabilityProfile.Subject,
                relationReader.Descriptor.Source.Value,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The materialization capability profile must describe the exact Relations source reader.",
                nameof(capabilityProfile));
        }
    }

    /// <summary>Exact canonical Relations source reader used for semantic reads.</summary>
    public IRelationQuerySourceReader RelationReader { get; }

    /// <summary>Physical source identity projected from <see cref="RelationReader"/>.</summary>
    public RelationQuerySourceInstanceId Source => RelationReader.Descriptor.Source;

    /// <summary>Execution or consistency domain projected from <see cref="RelationReader"/>.</summary>
    public RelationQueryExecutionDomainId ExecutionDomain => RelationReader.Descriptor.ExecutionDomain;

    /// <summary>Attributable materialization capabilities for the same source.</summary>
    public MaterializationCapabilityProfile CapabilityProfile { get; }
}

/// <summary>One bounded materialization page request around an exact canonical Relations source request.</summary>
public sealed record MaterializationSourcePageRequest
{
    /// <summary>Creates a bounded source-page request.</summary>
    /// <param name="read">Exact canonical Relations source request.</param>
    /// <param name="scope">Exact materialization source-feed scope.</param>
    /// <param name="continuation">Exclusive continuation from a prior page, or <see langword="null"/>.</param>
    /// <param name="maximumItems">Positive maximum number of observations to return in this page.</param>
    /// <param name="maximumBytes">Positive maximum sum of canonical encoded observation bytes to return in this page.</param>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The read addresses another source or continuation scope/read intent conflicts with the request.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumItems"/> or <paramref name="maximumBytes"/> is not positive.
    /// </exception>
    [JsonConstructor]
    public MaterializationSourcePageRequest(
        RelationQuerySourceReadRequest read,
        MaterializationSourceScope scope,
        MaterializationSourceContinuation? continuation,
        int maximumItems,
        long maximumBytes)
    {
        Read = Guard.RequireNotNull(read);
        Scope = Guard.RequireNotNull(scope);
        MaterializationSourceAcquisitionCatalog.RequireCompatibleRead(read, scope);
        if (continuation is not null
            && (continuation.Scope != scope
                || continuation.ReadFingerprint != MaterializationSourceReadFingerprinter.Compute(read)))
        {
            throw new ArgumentException(
                "A continuation must belong to the exact source-feed scope and Relations read intent.",
                nameof(continuation));
        }
        if (maximumItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumItems), maximumItems, "A source page must be bounded.");
        }

        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), maximumBytes, "A source page byte bound must be positive.");
        }

        Continuation = continuation;
        MaximumItems = maximumItems;
        MaximumBytes = maximumBytes;
    }

    /// <summary>Exact canonical Relations source request.</summary>
    public RelationQuerySourceReadRequest Read { get; }

    /// <summary>Exact materialization source-feed scope.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Exclusive continuation from a prior page, or <see langword="null"/>.</summary>
    public MaterializationSourceContinuation? Continuation { get; }

    /// <summary>Positive maximum number of observations to return in this page.</summary>
    public int MaximumItems { get; }

    /// <summary>Positive maximum sum of canonical encoded observation bytes to return in this page.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumBytes { get; }
}

/// <summary>Whether one materialization read page has a resumable successor.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationSourcePageState
{
    /// <summary>A continuation identifies another page inside the same exact Relations read.</summary>
    MoreAvailable = 0,

    /// <summary>No continuation is available; the retained Relations result states whether exhaustion is authoritative.</summary>
    Exhausted = 1
}

/// <summary>One bounded, exactly attributed Relations read result and its optional next-page continuation.</summary>
public sealed record MaterializationSourcePage
{
    /// <summary>Creates a materialization source page.</summary>
    /// <param name="scope">Exact physical-plan, placement, partition, and ordering scope read by this page.</param>
    /// <param name="readFingerprint">Fingerprint of the exact Relations read intent.</param>
    /// <param name="read">Canonical Relations source-read result for the page.</param>
    /// <param name="state">Whether another materialization page is available.</param>
    /// <param name="continuation">Continuation for the next page, or <see langword="null"/>.</param>
    /// <param name="diagnostics">Deterministic portable diagnostics about continuation or source behavior.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/>, <paramref name="readFingerprint"/>, or <paramref name="read"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Continuation presence does not match <paramref name="state"/>, continuation attribution conflicts, or
    /// diagnostics contain null entries.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationSourcePage(
        MaterializationSourceScope scope,
        MaterializationSourceReadFingerprint readFingerprint,
        RelationQuerySourceReadResult read,
        MaterializationSourcePageState state,
        MaterializationSourceContinuation? continuation = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        Scope = Guard.RequireNotNull(scope);
        ReadFingerprint = Guard.RequireNotNull(readFingerprint);
        Read = Guard.RequireNotNull(read);
        if (read.Observations.Any(observation => observation.Shape != scope.Shape))
        {
            throw new ArgumentException(
                "Every source-page observation must match the exact Relations placement shape.",
                nameof(read));
        }
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported materialization source-page state.");
        }

        var hasMore = state == MaterializationSourcePageState.MoreAvailable;
        if (hasMore != (continuation is not null))
        {
            throw new ArgumentException(
                "A page with more data requires a continuation and every exhausted page must omit it.",
                nameof(continuation));
        }
        if (continuation is not null
            && (continuation.Scope != scope || continuation.ReadFingerprint != readFingerprint))
        {
            throw new ArgumentException(
                "A page continuation must retain the page's exact source scope and Relations read fingerprint.",
                nameof(continuation));
        }
        State = state;
        Continuation = continuation;
        Diagnostics = MaterializationContract.NormalizeDiagnostics(diagnostics, nameof(diagnostics));
    }

    /// <summary>Exact physical-plan, placement, partition, and ordering scope read by this page.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Fingerprint of the exact Relations read intent.</summary>
    public MaterializationSourceReadFingerprint ReadFingerprint { get; }

    /// <summary>Canonical Relations source-read result for the page.</summary>
    public RelationQuerySourceReadResult Read { get; }

    /// <summary>Whether another materialization page is available.</summary>
    public MaterializationSourcePageState State { get; }

    /// <summary>Continuation for the next page, or <see langword="null"/> when no continuation is available.</summary>
    public MaterializationSourceContinuation? Continuation { get; }

    /// <summary>Deterministic portable diagnostics about continuation or source behavior.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>Durable proof that one exact Relations source read exhausted its declared acquisition boundary.</summary>
public sealed record MaterializationSourceReadCompletion
{
    /// <summary>Creates authoritative terminal read evidence.</summary>
    /// <param name="scope">Exact source scope that was exhausted.</param>
    /// <param name="readFingerprint">Fingerprint of the exact Relations read intent.</param>
    /// <param name="evidenceState">Authoritative complete or not-found Relations evidence state.</param>
    /// <param name="evidenceReference">Optional opaque source evidence reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> or <paramref name="readFingerprint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="evidenceReference"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="evidenceState"/> is not authoritative terminal evidence.</exception>
    [JsonConstructor]
    public MaterializationSourceReadCompletion(
        MaterializationSourceScope scope,
        MaterializationSourceReadFingerprint readFingerprint,
        RelationQuerySourceReadState evidenceState,
        string? evidenceReference = null)
    {
        Scope = Guard.RequireNotNull(scope);
        ReadFingerprint = Guard.RequireNotNull(readFingerprint);
        if (evidenceState is not (RelationQuerySourceReadState.Complete or RelationQuerySourceReadState.NotFound))
        {
            throw new ArgumentOutOfRangeException(
                nameof(evidenceState),
                evidenceState,
                "A completed materialization read requires authoritative complete or not-found Relations evidence.");
        }
        if (evidenceReference is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        }

        EvidenceState = evidenceState;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Exact source scope that was exhausted.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Fingerprint of the exact Relations read intent.</summary>
    public MaterializationSourceReadFingerprint ReadFingerprint { get; }

    /// <summary>Authoritative complete or not-found Relations evidence state.</summary>
    public RelationQuerySourceReadState EvidenceState { get; }

    /// <summary>Optional opaque source evidence reference.</summary>
    public string? EvidenceReference { get; }

    /// <summary>Projects durable completion evidence from one authoritative exhausted page.</summary>
    /// <param name="page">Exhausted page with complete or not-found Relations evidence.</param>
    /// <returns>Exact durable read-completion evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The page has a successor or lacks authoritative terminal Relations evidence.</exception>
    public static MaterializationSourceReadCompletion FromPage(MaterializationSourcePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.State != MaterializationSourcePageState.Exhausted
            || page.Read.State is not (RelationQuerySourceReadState.Complete or RelationQuerySourceReadState.NotFound))
        {
            throw new ArgumentException(
                "Read completion requires an exhausted page with authoritative complete or not-found evidence.",
                nameof(page));
        }
        return new(page.Scope, page.ReadFingerprint, page.Read.State, page.Read.EvidenceReference);
    }
}

/// <summary>Narrow provider-neutral port for paged materialization source reads.</summary>
public interface IMaterializationSource
{
    /// <summary>Exact canonical Relations reader and attributable materialization capability profile.</summary>
    MaterializationSourceDescriptor Descriptor { get; }

    /// <summary>Executes one bounded, continuation-aware source page read.</summary>
    /// <param name="context">Operation context carrying time, identity, tracing, and cancellation.</param>
    /// <param name="request">Bounded page request around one exact Relations source request.</param>
    /// <returns>The bounded source page and optional continuation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The request targets a different source or has an invalid continuation.</exception>
    /// <exception cref="InvalidOperationException">
    /// One canonical source observation exceeds <see cref="MaterializationSourcePageRequest.MaximumBytes"/> and
    /// therefore cannot be represented in any valid page for this request.
    /// </exception>
    /// <exception cref="OperationCanceledException">The context is canceled before completion.</exception>
    ValueTask<MaterializationSourcePage> ReadPageAsync(
        OperationContext context,
        MaterializationSourcePageRequest request);
}

/// <summary>Common authority for canonical materialization change delivery.</summary>
/// <remarks>
/// Pull and provider-managed delivery are execution realizations of the same canonical change authority. A source
/// position, an application checkpoint, and source settlement remain distinct progress concepts. Provider-managed
/// leases, bookmarks, processor names, and native callback contexts do not cross this boundary and are not
/// application checkpoints.
/// </remarks>
public interface IMaterializationChangeSource
{
    /// <summary>Exact canonical Relations reader and attributable materialization capability profile.</summary>
    MaterializationSourceDescriptor Descriptor { get; }
}

/// <summary>Optional source port for bounded pull-based typed change delivery.</summary>
/// <remarks>
/// Reads expose positions without mutating application progress or provider-owned acknowledgement state. The owning
/// process may durably commit an application checkpoint covering a returned position through
/// <see cref="IMaterializationProgressStore"/>; only then may it acknowledge the position through
/// <see cref="IMaterializationSettlingSource"/>, when the source advertises settlement.
/// Change-item and read-byte request bounds are hard page ceilings unless the descriptor's selected change-delivery
/// evidence advertises <see cref="MaterializationGuaranteeKind.TransactionAlignedDelivery"/>. Transaction-aligned
/// sources preserve each source transaction as one indivisible unit and may cross a preferred page budget only by the
/// final admitted transaction, which remains subject to separately advertised hard transaction limits.
/// </remarks>
public interface IMaterializationPullChangeSource : IMaterializationSource, IMaterializationChangeSource
{
    /// <summary>
    /// Exact canonical Relations reader and attributable materialization capability profile shared by the paged and
    /// change-delivery source views.
    /// </summary>
    new MaterializationSourceDescriptor Descriptor { get; }

    /// <summary>Captures the current opaque end position of one exact source-feed scope.</summary>
    /// <remarks>
    /// Capturing a position does not read, settle, or checkpoint changes and does not imply a coordinated snapshot
    /// across other scopes. A subsequent change read may use the returned position as its exclusive boundary.
    /// </remarks>
    /// <param name="context">Operation context carrying time, identity, tracing, and cancellation.</param>
    /// <param name="scope">Exact acquisition input, source, partition, and ordering scope whose end is captured.</param>
    /// <returns>
    /// An opaque resumable position at the source-feed boundary observed by this operation. Durability is bounded by
    /// the source's declared retention and compatibility guarantees; expired or incompatible positions fail closed.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="scope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The scope targets a different physical source.</exception>
    /// <exception cref="OperationCanceledException">The context is canceled before the position is captured.</exception>
    ValueTask<MaterializationSourcePosition> CaptureCurrentPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope);

    /// <summary>Reads a safety-bounded source-ordered page of changes without settling or checkpointing it.</summary>
    /// <remarks>
    /// For ordinary sources, the returned page never exceeds
    /// <see cref="MaterializationChangeReadRequest.MaximumDeliveries"/> or
    /// <see cref="MaterializationChangeReadRequest.MaximumBytes"/>. For a source advertising
    /// <see cref="MaterializationGuaranteeKind.TransactionAlignedDelivery"/>, those values are preferred page budgets:
    /// one complete source transaction may make the page exceed either budget, no later transaction may be admitted,
    /// and the indivisible transaction may not exceed the evidence's
    /// <see cref="MaterializationLimitKind.TransactionItems"/> or
    /// <see cref="MaterializationLimitKind.TransactionBytes"/> hard safety limit. Reading never settles the returned
    /// through-position.
    /// </remarks>
    /// <param name="context">Operation context carrying time, identity, tracing, and cancellation.</param>
    /// <param name="request">Bounded change request.</param>
    /// <returns>Currently visible deliveries, an opaque through-position, and catch-up state.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The request targets a different source or an unknown source position.</exception>
    /// <exception cref="InvalidOperationException">
    /// An ordinary canonical change delivery exceeds <see cref="MaterializationChangeReadRequest.MaximumBytes"/> and
    /// therefore cannot be represented in a valid ordinary page, or an indivisible transaction exceeds the selected
    /// transaction-aligned evidence's hard transaction-item or transaction-byte safety limit.
    /// </exception>
    /// <exception cref="OperationCanceledException">The context is canceled before completion.</exception>
    ValueTask<MaterializationChangePage> ReadChangesAsync(
        OperationContext context,
        MaterializationChangeReadRequest request);
}

/// <summary>
/// Exact materialization definition and generation binding for one provider-managed change execution.
/// </summary>
public sealed record MaterializationManagedChangeRequest
{
    /// <summary>Creates a provider-managed change execution binding.</summary>
    /// <param name="materialization">Logical materialization definition receiving the changes.</param>
    /// <param name="definitionFingerprint">Exact canonical execution-definition content being applied.</param>
    /// <param name="generation">Candidate or active target generation receiving the changes.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definitionFingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="materialization"/> or <paramref name="generation"/> is default.
    /// </exception>
    [JsonConstructor]
    public MaterializationManagedChangeRequest(
        MaterializationId materialization,
        ExecutionDefinitionFingerprint definitionFingerprint,
        MaterializationGenerationId generation)
    {
        MaterializationContract.RequireDefinedIdentity(materialization.Value, nameof(materialization));
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        Materialization = materialization;
        Generation = generation;
    }

    /// <summary>Logical materialization definition receiving the changes.</summary>
    public MaterializationId Materialization { get; }

    /// <summary>Exact canonical execution-definition content being applied.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Candidate or active target generation receiving the changes.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Creates the exact durable progress key for one delivered source-feed scope.</summary>
    /// <param name="scope">Exact scope of the provider-managed batch.</param>
    /// <returns>
    /// A progress key retaining this request's materialization, definition, and generation with
    /// <paramref name="scope"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    public MaterializationProgressKey CreateProgressKey(MaterializationSourceScope scope) =>
        new(Materialization, DefinitionFingerprint, Generation, Guard.RequireNotNull(scope));
}

/// <summary>Availability of one provider-defined managed change lag estimate.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationChangeLagEstimateState
{
    /// <summary>The provider supplied a non-negative approximate pending-work estimate.</summary>
    Estimated = 0,

    /// <summary>The provider could not supply a meaningful pending-work estimate.</summary>
    Unavailable = 1
}

/// <summary>Provider-neutral lag observation for one managed materialization change execution.</summary>
public sealed record MaterializationChangeLagObservation
{
    /// <summary>Creates one attributable managed-source lag observation.</summary>
    /// <param name="request">Exact materialization definition and generation being observed.</param>
    /// <param name="source">Physical source instance whose managed processor supplied the observation.</param>
    /// <param name="scope">
    /// Exact materialization source-feed scope when the provider can attribute the estimate, or
    /// <see langword="null"/> for an intentionally source-wide estimate.
    /// </param>
    /// <param name="estimateState">Whether an approximate provider-work estimate is available.</param>
    /// <param name="estimatedPendingProviderWork">
    /// Non-negative source-defined estimate of pending provider work when <paramref name="estimateState"/> is
    /// <see cref="MaterializationChangeLagEstimateState.Estimated"/>. This is not an exact item or delivery count.
    /// </param>
    /// <param name="observedAtUtc">UTC time at which the estimate was observed.</param>
    /// <param name="evidenceReference">Optional opaque provider-neutral evidence reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="estimateState"/> is unsupported or <paramref name="estimatedPendingProviderWork"/> is
    /// negative.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> is default, <paramref name="scope"/> belongs to another source, estimate presence
    /// conflicts with <paramref name="estimateState"/>, <paramref name="observedAtUtc"/> is not UTC, or
    /// <paramref name="evidenceReference"/> is empty.
    /// </exception>
    [JsonConstructor]
    public MaterializationChangeLagObservation(
        MaterializationManagedChangeRequest request,
        RelationQuerySourceInstanceId source,
        MaterializationSourceScope? scope,
        MaterializationChangeLagEstimateState estimateState,
        long? estimatedPendingProviderWork,
        DateTimeOffset observedAtUtc,
        string? evidenceReference = null)
    {
        Request = Guard.RequireNotNull(request);
        MaterializationContract.RequireDefinedIdentity(source.Value, nameof(source));
        if (scope is not null && scope.Source != source)
        {
            throw new ArgumentException(
                "A scoped managed-source lag observation must belong to its declared physical source.",
                nameof(scope));
        }

        if (!Enum.IsDefined(estimateState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(estimateState),
                estimateState,
                "Unsupported managed-source lag estimate state.");
        }

        var hasEstimate = estimatedPendingProviderWork.HasValue;
        if ((estimateState == MaterializationChangeLagEstimateState.Estimated) != hasEstimate)
        {
            throw new ArgumentException(
                "An estimated lag observation requires provider work, while an unavailable observation must omit it.",
                nameof(estimatedPendingProviderWork));
        }

        if (estimatedPendingProviderWork < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(estimatedPendingProviderWork),
                estimatedPendingProviderWork,
                "A managed-source lag estimate cannot be negative.");
        }

        MaterializationContract.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (evidenceReference is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        }

        Source = source;
        Scope = scope;
        EstimateState = estimateState;
        EstimatedPendingProviderWork = estimatedPendingProviderWork;
        ObservedAtUtc = observedAtUtc;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Exact materialization definition and generation being observed.</summary>
    public MaterializationManagedChangeRequest Request { get; }

    /// <summary>Physical source instance whose managed processor supplied the observation.</summary>
    public RelationQuerySourceInstanceId Source { get; }

    /// <summary>
    /// Exact source-feed scope, or <see langword="null"/> when this is intentionally a source-wide estimate that
    /// must not be projected as exact per-scope lag.
    /// </summary>
    public MaterializationSourceScope? Scope { get; }

    /// <summary>Whether an approximate provider-work estimate is available.</summary>
    public MaterializationChangeLagEstimateState EstimateState { get; }

    /// <summary>Source-defined approximate pending provider work, not an exact item or delivery count.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? EstimatedPendingProviderWork { get; }

    /// <summary>UTC time at which the estimate was observed.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Optional opaque provider-neutral evidence reference.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>
/// Provider-neutral observation that source settlement followed one exact durable application checkpoint.
/// </summary>
public sealed record MaterializationChangeSettlementObservation
{
    /// <summary>Creates one checkpoint-attributed managed source settlement observation.</summary>
    /// <param name="progress">Durable application progress that authorized provider settlement.</param>
    /// <param name="settlement">Provider acknowledgement of the checkpoint's exact source position.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="progress"/> or <paramref name="settlement"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The progress lacks the cited change checkpoint, the checkpoint or position differs, the source scope differs,
    /// or settlement predates the durable application commit.
    /// </exception>
    [JsonConstructor]
    public MaterializationChangeSettlementObservation(
        MaterializationProgressSnapshot progress,
        MaterializationSourceSettlement settlement)
    {
        Progress = Guard.RequireNotNull(progress);
        Settlement = Guard.RequireNotNull(settlement);
        var checkpoint = progress.LatestCheckpoint;
        if (checkpoint is null
            || checkpoint.Kind != MaterializationCheckpointKind.ChangePosition
            || checkpoint.Id != settlement.Checkpoint
            || checkpoint.Position != settlement.Position
            || progress.Key.Scope != settlement.Position.Scope
            || settlement.SettledAtUtc < checkpoint.CommittedAtUtc)
        {
            throw new ArgumentException(
                "A managed settlement observation must cite the exact durable change checkpoint, position, source scope, and causal commit boundary.",
                nameof(settlement));
        }
    }

    /// <summary>Durable application progress that authorized provider settlement.</summary>
    public MaterializationProgressSnapshot Progress { get; }

    /// <summary>Provider acknowledgement of the checkpoint's exact source position.</summary>
    public MaterializationSourceSettlement Settlement { get; }
}

/// <summary>
/// Applies one canonical managed change page and durably commits its exact application checkpoint.
/// </summary>
/// <remarks>
/// A managed source may invoke this delegate concurrently for independent source scopes; no transaction spans those
/// calls. Lease transfer may also overlap attempts carrying the same stable delivery identities. Implementations
/// must therefore be thread-safe and use durable progress fencing plus idempotent effects. They return only after
/// both application effects and the exact supplied progress key are durable. Cancellation signals lost ownership or
/// run shutdown, but work that became durable before cancellation may be replayed; after this delegate returns, the
/// adapter rechecks cancellation and durable proof before requesting provider settlement. Once a provider settlement
/// operation that cannot accept cancellation is in flight, its authoritative outcome wins: success is recorded even
/// if cancellation arrives concurrently, while failure leaves the stable deliveries replayable.
/// </remarks>
/// <param name="context">Operation context carrying provider callback cancellation and attribution.</param>
/// <param name="progress">Exact definition, generation, and source-feed progress key for <paramref name="page"/>.</param>
/// <param name="page">Canonical bounded page delivered by the managed source.</param>
/// <returns>
/// Durable progress evidence. Only an applied or replayed exact change checkpoint accepted by
/// <see cref="MaterializationChangePage.RequireDurableCheckpointForSettlement"/> authorizes provider settlement.
/// </returns>
public delegate ValueTask<MaterializationProgressMutationResult> MaterializationManagedChangeHandler(
    OperationContext context,
    MaterializationProgressKey progress,
    MaterializationChangePage page);

/// <summary>Optional source port for provider-managed canonical change delivery.</summary>
/// <remarks>
/// The adapter retains provider leases, bookmarks, native callback contexts, and acknowledgement functions. For each
/// delivered page it creates an exact progress key from the run request and page scope, invokes the handler, validates
/// the returned durable evidence through
/// <see cref="MaterializationChangePage.RequireDurableCheckpointForSettlement"/>, and only then settles the provider.
/// A handler failure, cancellation observed before settlement is requested, rejected progress mutation, or mismatched
/// checkpoint must leave the provider position unsettled so the same stable deliveries can be replayed. Once a
/// non-cancellable provider settlement is in flight, the adapter records its authoritative success despite concurrent
/// cancellation; a failed settlement remains replayable.
/// Managed callbacks may run concurrently across independent scopes, and no all-scope transaction is implied. A
/// handler must fence durable progress and make stable delivery replay idempotent rather than relying on callback
/// serialization or lease ownership as application state.
/// </remarks>
public interface IMaterializationManagedChangeSource : IMaterializationChangeSource
{
    /// <summary>Runs provider-managed delivery until completion or cancellation.</summary>
    /// <param name="context">Operation context carrying execution attribution and cancellation.</param>
    /// <param name="request">Exact materialization definition and generation receiving managed changes.</param>
    /// <param name="handler">Application work and durable-checkpoint handler.</param>
    /// <returns>A task that completes when managed delivery stops.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="request"/>, or <paramref name="handler"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The handler returns progress that does not exactly authorize settlement of its delivered page.
    /// </exception>
    /// <exception cref="OperationCanceledException">Managed delivery is canceled.</exception>
    Task RunAsync(
        OperationContext context,
        MaterializationManagedChangeRequest request,
        MaterializationManagedChangeHandler handler);

    /// <summary>Observes provider-neutral lag for the managed materialization execution.</summary>
    /// <param name="context">Operation context carrying observation attribution and cancellation.</param>
    /// <param name="request">Exact materialization definition and generation whose lag is observed.</param>
    /// <returns>An asynchronous sequence of source-wide or exact source-scope lag estimates.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">Lag observation is canceled.</exception>
    IAsyncEnumerable<MaterializationChangeLagObservation> ObserveLagAsync(
        OperationContext context,
        MaterializationManagedChangeRequest request);
}

/// <summary>Optional change-source port that can capture the beginning of its currently retained history.</summary>
/// <remarks>
/// Implementers must advertise <see cref="MaterializationGuaranteeKind.RetainedHistoryStart"/> on their
/// <see cref="MaterializationCapabilityKind.SourceChangeDelivery"/> evidence. Sources that can capture only a
/// current cut do not implement this port.
/// </remarks>
public interface IMaterializationRetainedChangeSource : IMaterializationPullChangeSource
{
    /// <summary>Captures an exclusive resumable boundary immediately before the earliest currently retained change.</summary>
    /// <param name="context">Operation context carrying time, identity, tracing, and cancellation.</param>
    /// <param name="scope">Exact acquisition input, source, partition, and ordering scope whose retained start is captured.</param>
    /// <returns>
    /// An opaque position suitable as <see cref="MaterializationChangeReadRequest.AfterPosition"/> for reading all
    /// changes retained at capture time and any changes that subsequently become visible, while the position remains
    /// inside the source's declared retention and compatibility boundary. Expired positions fail closed.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="scope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The scope targets a different physical source.</exception>
    /// <exception cref="OperationCanceledException">The context is canceled before the position is captured.</exception>
    ValueTask<MaterializationSourcePosition> CaptureRetainedStartPositionAsync(
        OperationContext context,
        MaterializationSourceScope scope);
}

/// <summary>Stable diagnostic codes emitted by source-side materialization operations.</summary>
public static class MaterializationSourceDiagnosticCodes
{
    /// <summary>A settlement identity was reused for a different acknowledgement request.</summary>
    public const string SettlementIdentityConflict = "materialization.source.settlement.identityConflict";

    /// <summary>The source could not produce an acknowledgement at or after its request time.</summary>
    public const string SettlementClockRegression = "materialization.source.settlement.clockRegression";
}

/// <summary>
/// Explicit request to acknowledge a raw source position only after its application checkpoint is durable.
/// </summary>
/// <remarks>
/// The engine establishes the ordering precondition: it first commits <see cref="Checkpoint"/> through
/// <see cref="IMaterializationProgressStore"/>, then supplies the exact checkpoint identity and position here. The
/// source adapter acknowledges that position but does not persist application progress.
/// </remarks>
public sealed record MaterializationSourceSettlementRequest
{
    /// <summary>Creates an explicit source-settlement request.</summary>
    /// <param name="id">Stable settlement identity reused only for an exact acknowledgement retry.</param>
    /// <param name="checkpoint">Already-durable application checkpoint identity.</param>
    /// <param name="position">Raw source position proven by the checkpoint.</param>
    /// <param name="requestedAtUtc">UTC time at which source acknowledgement was requested.</param>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or timestamp is invalid.</exception>
    [JsonConstructor]
    public MaterializationSourceSettlementRequest(
        MaterializationSettlementId id,
        MaterializationCheckpointId checkpoint,
        MaterializationSourcePosition position,
        DateTimeOffset requestedAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        MaterializationContract.RequireDefinedIdentity(checkpoint.Value, nameof(checkpoint));
        Position = Guard.RequireNotNull(position);
        MaterializationContract.RequireUtc(requestedAtUtc, nameof(requestedAtUtc));
        Id = id;
        Checkpoint = checkpoint;
        RequestedAtUtc = requestedAtUtc;
    }

    /// <summary>Stable settlement identity reused only for an exact acknowledgement retry.</summary>
    public MaterializationSettlementId Id { get; }

    /// <summary>Already-durable application checkpoint identity.</summary>
    public MaterializationCheckpointId Checkpoint { get; }

    /// <summary>Raw source position proven by the checkpoint.</summary>
    public MaterializationSourcePosition Position { get; }

    /// <summary>UTC time at which source acknowledgement was requested.</summary>
    public DateTimeOffset RequestedAtUtc { get; }
}

/// <summary>Observable disposition of one source-side settlement request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationSourceSettlementDisposition
{
    /// <summary>The source acknowledged the requested position.</summary>
    Acknowledged = 0,

    /// <summary>The source replayed the exact prior acknowledgement receipt.</summary>
    Replayed = 1,

    /// <summary>The stable settlement identity was reused for a different request.</summary>
    IdentityConflict = 2,

    /// <summary>The source rejected the acknowledgement request with attributable diagnostics.</summary>
    Rejected = 3
}

/// <summary>Attributable outcome of one explicit source acknowledgement.</summary>
public sealed record MaterializationSourceSettlementResult
{
    /// <summary>Creates a source-settlement result.</summary>
    /// <param name="disposition">Acknowledged, replayed, identity-conflict, or rejected outcome.</param>
    /// <param name="receipt">Attributable acknowledgement receipt for acknowledged or replayed outcomes.</param>
    /// <param name="diagnostics">Structured deterministic diagnostics for rejected outcomes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Receipt/disposition invariants conflict or diagnostics contain null entries.</exception>
    [JsonConstructor]
    public MaterializationSourceSettlementResult(
        MaterializationSourceSettlementDisposition disposition,
        MaterializationSourceSettlement? receipt,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported source-settlement disposition.");
        }

        var acknowledged = disposition is MaterializationSourceSettlementDisposition.Acknowledged
            or MaterializationSourceSettlementDisposition.Replayed;
        if (acknowledged != (receipt is not null))
        {
            throw new ArgumentException("Only acknowledged or replayed source settlements carry a receipt.", nameof(receipt));
        }

        var normalizedDiagnostics = MaterializationContract.NormalizeDiagnostics(diagnostics, nameof(diagnostics));
        if (acknowledged && !normalizedDiagnostics.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An acknowledged or replayed source settlement cannot carry diagnostics.", nameof(diagnostics));
        }

        if (!acknowledged && normalizedDiagnostics.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A rejected source settlement requires a diagnostic.", nameof(diagnostics));
        }

        Disposition = disposition;
        Receipt = receipt;
        Diagnostics = normalizedDiagnostics;
    }

    /// <summary>Acknowledged, replayed, identity-conflict, or rejected outcome.</summary>
    public MaterializationSourceSettlementDisposition Disposition { get; }

    /// <summary>Attributable acknowledgement receipt for acknowledged or replayed outcomes.</summary>
    public MaterializationSourceSettlement? Receipt { get; }

    /// <summary>Structured deterministic diagnostics for rejected outcomes.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>Optional source port for explicit acknowledgement after durable application checkpointing.</summary>
public interface IMaterializationSettlingSource : IMaterializationChangeSource
{
    /// <summary>Acknowledges one raw source position without persisting application progress.</summary>
    /// <param name="context">Operation context carrying time, identity, tracing, and cancellation.</param>
    /// <param name="request">Checkpoint-attributed source acknowledgement request.</param>
    /// <returns>An attributable acknowledgement receipt or structured rejection.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The request targets a different physical source, or its opaque position is malformed or outside the retained
    /// change feed.
    /// </exception>
    /// <exception cref="OperationCanceledException">The context is canceled before acknowledgement.</exception>
    ValueTask<MaterializationSourceSettlementResult> SettleAsync(
        OperationContext context,
        MaterializationSourceSettlementRequest request);
}
