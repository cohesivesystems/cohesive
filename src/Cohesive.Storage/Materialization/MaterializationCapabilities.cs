using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostic codes emitted while matching materialization requirements to endpoint evidence.</summary>
public static class MaterializationCapabilityDiagnosticCodes
{
    /// <summary>The supplied profile belongs to the wrong endpoint role.</summary>
    public const string EndpointRoleMismatch = "materialization.capability.endpointRoleMismatch";

    /// <summary>No endpoint evidence advertises a required capability.</summary>
    public const string CapabilityUnavailable = "materialization.capability.unavailable";

    /// <summary>Advertised evidence omits a required semantic guarantee.</summary>
    public const string GuaranteeUnavailable = "materialization.capability.guaranteeUnavailable";

    /// <summary>Advertised evidence omits a required operating limit.</summary>
    public const string LimitUnavailable = "materialization.capability.limitUnavailable";

    /// <summary>An advertised hard limit is below the required operating bound.</summary>
    public const string LimitExceeded = "materialization.capability.limitExceeded";
}

/// <summary>Owner role of one materialization endpoint capability profile.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationEndpointRole
{
    /// <summary>The endpoint reads relation inputs or delivers changes.</summary>
    Source = 0,

    /// <summary>The endpoint stores and promotes materialized generations.</summary>
    Target = 1
}

/// <summary>Complementary synchronization modes supported by one materialization definition.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationSynchronizationMode
{
    /// <summary>Builds a fresh isolated generation from bounded source enumeration and catch-up.</summary>
    Rebuild = 1,

    /// <summary>Maintains a generation from typed source changes.</summary>
    Incremental = 2,

    /// <summary>Supports both fresh rebuild and incremental maintenance.</summary>
    All = Rebuild | Incremental
}

/// <summary>Closed source and target facilities used by the materialization protocol.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationCapabilityKind
{
    /// <summary>Reads one bounded batch of stable observation identities.</summary>
    SourceBatchedPointRead = 0,

    /// <summary>Executes a bounded parameterized predicate query.</summary>
    SourceParameterizedPredicateQuery = 1,

    /// <summary>Enumerates a stable logical source set inside a declared bound.</summary>
    SourceBoundedEnumeration = 2,

    /// <summary>Resumes a bounded read from an opaque durable continuation.</summary>
    SourceContinuation = 3,

    /// <summary>Delivers typed source changes from a durable position.</summary>
    /// <remarks>
    /// Every requirement and evidence declaration selects exactly one change-coverage guarantee:
    /// <see cref="MaterializationGuaranteeKind.CompleteMutationDelivery"/> or
    /// <see cref="MaterializationGuaranteeKind.LatestVersionUpsertDelivery"/>.
    /// Pull realizations may advertise hard change-item and byte limits. Managed realizations whose provider exposes
    /// only advisory callback-size hints omit those limits; they cannot satisfy requirements that demand hard bounds.
    /// </remarks>
    SourceChangeDelivery = 4,

    /// <summary>Explicitly settles a delivered source position.</summary>
    SourceSettlement = 5,

    /// <summary>Creates an isolated target generation invisible to active readers.</summary>
    TargetGenerationIsolation = 6,

    /// <summary>Upserts a bounded batch of materialized items.</summary>
    TargetBulkUpsert = 7,

    /// <summary>Deletes a bounded batch of materialized items.</summary>
    TargetBulkDelete = 8,

    /// <summary>Returns one exact terminal outcome for every bulk item.</summary>
    TargetPerItemOutcomes = 9,

    /// <summary>Seals a generation against further writes.</summary>
    TargetSeal = 10,

    /// <summary>Validates a sealed candidate generation with attributable evidence.</summary>
    TargetValidation = 11,

    /// <summary>Promotes a validated generation through compare-and-swap fencing.</summary>
    TargetFencedPromotion = 12,

    /// <summary>Retires a displaced or abandoned generation without deleting it.</summary>
    TargetRetirement = 13,

    /// <summary>Physically cleans up a non-active retired generation.</summary>
    TargetCleanup = 14,

    /// <summary>
    /// Resolves complete durable contributor-to-root associations and replaces them atomically with their target
    /// materialization mutations before application progress may advance.
    /// </summary>
    TargetContributorLedger = 15
}

/// <summary>Semantic guarantee that a materialization endpoint may prove.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationGuaranteeKind
{
    /// <summary>Equivalent reads use a stable deterministic order inside the declared scope.</summary>
    StableOrdering = 0,

    /// <summary>Completeness is authoritative for one exact bounded request.</summary>
    RequestLocalCompleteness = 1,

    /// <summary>Participating reads share one proven coordinated snapshot boundary.</summary>
    CoordinatedSnapshot = 2,

    /// <summary>A baseline cut plus change catch-up converges without a global snapshot claim.</summary>
    BaselinePlusCatchUp = 3,

    /// <summary>Repeated reconciliation converges inside a declared bounded scope.</summary>
    Reconciliation = 4,

    /// <summary>
    /// A delivered source change may be repeated until the consumer durably advances its application checkpoint;
    /// explicit provider settlement is required only when separately advertised.
    /// </summary>
    AtLeastOnceDelivery = 5,

    /// <summary>Source retention or redelivery advances only through an explicit settlement request.</summary>
    ExplicitSettlement = 6,

    /// <summary>Change delivery supplies attributable state from before the change.</summary>
    BeforeImage = 7,

    /// <summary>Candidate generation writes are invisible until successful promotion.</summary>
    GenerationIsolation = 8,

    /// <summary>Reapplying identical write intent has no additional semantic effect.</summary>
    IdempotentWrite = 9,

    /// <summary>Writes can reject a stale semantic item version.</summary>
    VersionConditionalWrite = 10,

    /// <summary>Every bulk input has one and only one keyed terminal outcome.</summary>
    ExactPerItemOutcome = 11,

    /// <summary>Readers observe either the prior or promoted generation, never a mixed publication.</summary>
    AtomicPromotion = 12,

    /// <summary>Promotion rejects a stale expected generation, revision, or worker fence.</summary>
    FencedPromotion = 13,

    /// <summary>Generation mutations reject workers superseded within that generation's ownership scope.</summary>
    FencedMutation = 14,

    /// <summary>The source can capture an exclusive boundary before its earliest currently retained change.</summary>
    RetainedHistoryStart = 15,

    /// <summary>
    /// Contributor associations and their corresponding materialized-item mutations commit as one atomic effect.
    /// </summary>
    AtomicWithMaterializationMutation = 16,

    /// <summary>
    /// Every retained create, update, and delete mutation is delivered without latest-version coalescing. This does
    /// not itself imply that a before image is available.
    /// </summary>
    CompleteMutationDelivery = 17,

    /// <summary>
    /// Currently visible latest versions are delivered as upserts, without claiming deletes or intermediate
    /// versions.
    /// </summary>
    LatestVersionUpsertDelivery = 18
}

/// <summary>Positive hard operating maximum advertised or required for a materialization operation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationLimitKind
{
    /// <summary>Maximum items returned by one bounded read operation.</summary>
    ReadItems = 0,

    /// <summary>Maximum encoded bytes returned by one bounded read operation.</summary>
    ReadBytes = 1,

    /// <summary>Maximum changes delivered by one source batch.</summary>
    ChangeItems = 2,

    /// <summary>Maximum target mutations accepted by one bulk request.</summary>
    WriteItems = 3,

    /// <summary>Maximum encoded bytes accepted by one bulk request.</summary>
    WriteBytes = 4,

    /// <summary>Maximum independently admitted operations.</summary>
    Parallelism = 5,

    /// <summary>Maximum Unicode characters accepted by an identity encoded into a target index key.</summary>
    IndexedIdentityCharacters = 6
}

/// <summary>How attributable endpoint evidence realizes one requested capability.</summary>
/// <remarks>
/// This is part of the persisted <c>cohesive-materialization/v1</c> evidence contract. It is intentionally distinct
/// from a Relations compiler's realization-decision type: endpoint profiles classify durable adapter evidence before
/// any materialization requirement is selected, while a compiler decision records the outcome of planning one
/// demand. The matching implementation does not maintain a conversion table between the two closed models.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationCapabilityRealizationKind
{
    /// <summary>The endpoint implements the capability directly.</summary>
    Native = 0,

    /// <summary>Declared endpoint facilities compose exact support.</summary>
    Composed = 1,

    /// <summary>Exact support holds only inside the evidence's declared limits.</summary>
    Constrained = 2,

    /// <summary>An explicit local override supplies exact attributable support.</summary>
    Override = 3,

    /// <summary>No supplied evidence preserves the requested capability.</summary>
    Unavailable = 4
}

/// <summary>Stable identity of a materialization capability requirement.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationCapabilityRequirementId
{
    /// <summary>Creates a requirement identity.</summary>
    /// <param name="value">Stable identity within its owning definition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationCapabilityRequirementId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable requirement identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one attributable capability assertion.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationCapabilityEvidenceId
{
    /// <summary>Creates an evidence identity.</summary>
    /// <param name="value">Stable identity within its endpoint profile.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationCapabilityEvidenceId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable evidence identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable versioned identity of a materialization endpoint capability profile.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationCapabilityProfileId
{
    /// <summary>Creates a profile identity.</summary>
    /// <param name="value">Stable identity that changes when capability evidence changes semantically.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationCapabilityProfileId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable profile identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>One positive operating maximum attached to a capability requirement or assertion.</summary>
public readonly record struct MaterializationOperatingLimit
{
    /// <summary>Creates an operating limit.</summary>
    /// <param name="kind">Bounded operational dimension.</param>
    /// <param name="maximum">Positive maximum value in the canonical unit implied by <paramref name="kind"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported or <paramref name="maximum"/> is not positive.</exception>
    [JsonConstructor]
    public MaterializationOperatingLimit(MaterializationLimitKind kind, long maximum)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported materialization limit kind.");
        }

        if (maximum <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "A materialization operating limit must be positive.");
        }

        Kind = kind;
        Maximum = maximum;
    }

    /// <summary>Bounded operational dimension.</summary>
    public MaterializationLimitKind Kind { get; }

    /// <summary>Positive maximum value in the kind's canonical unit.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long Maximum { get; }
}

/// <summary>Backend-independent capability and guarantee required by a materialization definition.</summary>
public sealed record MaterializationCapabilityRequirement
{
    /// <summary>Creates a materialization capability requirement.</summary>
    /// <param name="id">Stable requirement identity.</param>
    /// <param name="capability">Required source or target facility.</param>
    /// <param name="guarantees">Required semantic guarantees.</param>
    /// <param name="operatingLimits">Largest operation sizes the selected realization must accept.</param>
    /// <param name="modes">Synchronization modes for which the requirement applies.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default, a collection contains duplicates, a limit kind is repeated, or a guarantee or operating
    /// limit does not apply to <paramref name="capability"/>, source change delivery does not declare exactly one
    /// change-coverage guarantee, or per-item outcome coverage omits a required write bound.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> or a guarantee is unsupported.</exception>
    [JsonConstructor]
    public MaterializationCapabilityRequirement(
        MaterializationCapabilityRequirementId id,
        MaterializationCapabilityKind capability,
        ImmutableArray<MaterializationGuaranteeKind> guarantees = default,
        ImmutableArray<MaterializationOperatingLimit> operatingLimits = default,
        MaterializationSynchronizationMode modes = MaterializationSynchronizationMode.All)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A capability requirement requires a stable identity.", nameof(id));
        }

        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unsupported materialization capability.");
        }

        MaterializationSynchronizationModes.RequireValid(modes, nameof(modes), allowCombined: true);

        var normalizedGuarantees = MaterializationCapabilityOrdering.NormalizeGuarantees(guarantees, nameof(guarantees));
        var normalizedLimits = MaterializationCapabilityOrdering.NormalizeLimits(operatingLimits, nameof(operatingLimits));
        MaterializationCapabilityCatalog.RequireApplicableDimensions(
            capability,
            normalizedGuarantees,
            normalizedLimits,
            nameof(guarantees),
            nameof(operatingLimits));
        MaterializationCapabilityCatalog.RequireRequirementLimits(
            capability,
            normalizedLimits,
            nameof(operatingLimits));

        Id = id;
        Capability = capability;
        Guarantees = normalizedGuarantees;
        OperatingLimits = normalizedLimits;
        Modes = modes;
    }

    /// <summary>Stable requirement identity.</summary>
    public MaterializationCapabilityRequirementId Id { get; }

    /// <summary>Required source or target facility.</summary>
    public MaterializationCapabilityKind Capability { get; }

    /// <summary>Required semantic guarantees in canonical order.</summary>
    public ImmutableArray<MaterializationGuaranteeKind> Guarantees { get; }

    /// <summary>Required maximum operation sizes in canonical limit order.</summary>
    public ImmutableArray<MaterializationOperatingLimit> OperatingLimits { get; }

    /// <summary>Synchronization modes for which this requirement applies.</summary>
    public MaterializationSynchronizationMode Modes { get; }
}

/// <summary>Attributable proof that one endpoint realizes a materialization capability.</summary>
public sealed record MaterializationCapabilityEvidence
{
    /// <summary>Creates one capability assertion.</summary>
    /// <param name="id">Stable evidence identity.</param>
    /// <param name="capability">Facility supplied by the endpoint.</param>
    /// <param name="realization">How the endpoint realizes the facility.</param>
    /// <param name="guarantees">Semantic guarantees preserved by the realization.</param>
    /// <param name="operatingLimits">Hard positive maxima under which the evidence holds.</param>
    /// <param name="sourceReferences">One or more adapter, deployment, compiler, or override evidence references.</param>
    /// <param name="description">Optional human-facing explanation excluded from matching.</param>
    /// <exception cref="ArgumentException">
    /// An identity or source reference is invalid, a collection contains duplicates, a limit kind is repeated, or a
    /// guarantee or operating limit does not apply to <paramref name="capability"/>, or bounded operation evidence
    /// omits a required item or byte hard limit, or source change delivery does not declare exactly one
    /// change-coverage guarantee.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/>, <paramref name="realization"/>, or a guarantee is unsupported.</exception>
    [JsonConstructor]
    public MaterializationCapabilityEvidence(
        MaterializationCapabilityEvidenceId id,
        MaterializationCapabilityKind capability,
        MaterializationCapabilityRealizationKind realization,
        ImmutableArray<MaterializationGuaranteeKind> guarantees,
        ImmutableArray<MaterializationOperatingLimit> operatingLimits,
        ImmutableArray<string> sourceReferences,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("Capability evidence requires a stable identity.", nameof(id));
        }

        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unsupported materialization capability.");
        }

        if (!Enum.IsDefined(realization) || realization == MaterializationCapabilityRealizationKind.Unavailable)
        {
            throw new ArgumentOutOfRangeException(nameof(realization), realization, "Evidence must describe an available realization.");
        }

        var normalizedGuarantees = MaterializationCapabilityOrdering.NormalizeGuarantees(guarantees, nameof(guarantees));
        var normalizedLimits = MaterializationCapabilityOrdering.NormalizeLimits(operatingLimits, nameof(operatingLimits));
        MaterializationCapabilityCatalog.RequireApplicableDimensions(
            capability,
            normalizedGuarantees,
            normalizedLimits,
            nameof(guarantees),
            nameof(operatingLimits));
        MaterializationCapabilityCatalog.RequireEvidenceLimits(
            capability,
            normalizedLimits,
            nameof(operatingLimits));

        Id = id;
        Capability = capability;
        Realization = realization;
        Guarantees = normalizedGuarantees;
        OperatingLimits = normalizedLimits;
        SourceReferences = MaterializationCapabilityOrdering.NormalizeStrings(sourceReferences, nameof(sourceReferences), requireNonEmpty: true);
        if (description is not null && string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("A capability evidence description cannot be empty.", nameof(description));
        }

        Description = description;
    }

    /// <summary>Stable evidence identity.</summary>
    public MaterializationCapabilityEvidenceId Id { get; }

    /// <summary>Facility supplied by the endpoint.</summary>
    public MaterializationCapabilityKind Capability { get; }

    /// <summary>How the endpoint realizes the facility.</summary>
    public MaterializationCapabilityRealizationKind Realization { get; }

    /// <summary>Preserved semantic guarantees in canonical order.</summary>
    public ImmutableArray<MaterializationGuaranteeKind> Guarantees { get; }

    /// <summary>Hard positive maxima in canonical limit order.</summary>
    public ImmutableArray<MaterializationOperatingLimit> OperatingLimits { get; }

    /// <summary>Attributable evidence references in canonical ordinal order.</summary>
    public ImmutableArray<string> SourceReferences { get; }

    /// <summary>Optional human-facing explanation excluded from matching.</summary>
    public string? Description { get; }
}

/// <summary>Portable capability snapshot exposed by one exact materialization endpoint.</summary>
public sealed record MaterializationCapabilityProfile
{
    /// <summary>Creates an endpoint capability profile.</summary>
    /// <param name="id">Stable versioned profile identity.</param>
    /// <param name="role">Source or target ownership role.</param>
    /// <param name="subject">Stable source-instance or target identity described by the profile.</param>
    /// <param name="evidence">Attributable capability assertions.</param>
    /// <param name="description">Optional human-facing explanation excluded from matching.</param>
    /// <exception cref="ArgumentNullException"><paramref name="subject"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is invalid, evidence identities repeat, evidence belongs to the other endpoint role, or <paramref name="description"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationCapabilityProfile(
        MaterializationCapabilityProfileId id,
        MaterializationEndpointRole role,
        string subject,
        ImmutableArray<MaterializationCapabilityEvidence> evidence,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A capability profile requires a stable identity.", nameof(id));
        }

        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported endpoint role.");
        }

        Id = id;
        Role = role;
        Subject = Guard.RequireNotNullOrWhiteSpace(subject);

        var normalized = evidence.IsDefault ? [] : evidence;
        if (normalized.Any(static item => item is null))
        {
            throw new ArgumentException("Capability evidence cannot contain null entries.", nameof(evidence));
        }

        if (normalized.GroupBy(static item => item.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A capability profile cannot repeat an evidence identity.", nameof(evidence));
        }

        if (normalized.Any(item => MaterializationCapabilityCatalog.RoleOf(item.Capability) != role))
        {
            throw new ArgumentException("Every capability assertion must belong to the profile's endpoint role.", nameof(evidence));
        }

        Evidence = [.. normalized.OrderBy(static item => item.Id.Value, StringComparer.Ordinal)];

        if (description is not null && string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("A capability profile description cannot be empty.", nameof(description));
        }

        Description = description;
    }

    /// <summary>Stable versioned profile identity.</summary>
    public MaterializationCapabilityProfileId Id { get; }

    /// <summary>Source or target ownership role.</summary>
    public MaterializationEndpointRole Role { get; }

    /// <summary>Stable source-instance or target identity described by the profile.</summary>
    public string Subject { get; }

    /// <summary>Attributable capability assertions in canonical identity order.</summary>
    public ImmutableArray<MaterializationCapabilityEvidence> Evidence { get; }

    /// <summary>Optional human-facing explanation excluded from matching.</summary>
    public string? Description { get; }
}

/// <summary>One final evidence-backed decision for a materialization capability requirement.</summary>
public sealed record MaterializationCapabilityDecision
{
    /// <summary>Creates a capability decision.</summary>
    /// <param name="requirement">Requirement receiving the decision.</param>
    /// <param name="realization">Available realization kind or <see cref="MaterializationCapabilityRealizationKind.Unavailable"/>.</param>
    /// <param name="evidence">Selected evidence, or <see langword="null"/> when unavailable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="requirement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Availability, realization, capability, guarantees, limits, or evidence conflict.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="realization"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationCapabilityDecision(
        MaterializationCapabilityRequirement requirement,
        MaterializationCapabilityRealizationKind realization,
        MaterializationCapabilityEvidence? evidence = null)
    {
        Requirement = Guard.RequireNotNull(requirement);
        if (!Enum.IsDefined(realization))
        {
            throw new ArgumentOutOfRangeException(nameof(realization), realization, "Unsupported realization kind.");
        }

        if ((realization == MaterializationCapabilityRealizationKind.Unavailable) != (evidence is null))
        {
            throw new ArgumentException("Unavailable decisions omit evidence; available decisions require it.", nameof(evidence));
        }

        if (evidence is not null && evidence.Capability != requirement.Capability)
        {
            throw new ArgumentException("Selected evidence must address the required capability.", nameof(evidence));
        }

        if (evidence is not null && evidence.Realization != realization)
        {
            throw new ArgumentException("A decision realization must equal its selected evidence realization.", nameof(realization));
        }

        if (evidence is not null && !MaterializationCapabilityMatcher.Satisfies(requirement, evidence))
        {
            throw new ArgumentException("Selected evidence must prove every required guarantee and operating limit.", nameof(evidence));
        }

        Realization = realization;
        Evidence = evidence;
    }

    /// <summary>Requirement receiving the decision.</summary>
    public MaterializationCapabilityRequirement Requirement { get; }

    /// <summary>Final realization classification.</summary>
    public MaterializationCapabilityRealizationKind Realization { get; }

    /// <summary>Selected endpoint evidence, or <see langword="null"/> when unavailable.</summary>
    public MaterializationCapabilityEvidence? Evidence { get; }
}

/// <summary>Deterministic result of matching one requirement set against an endpoint profile.</summary>
public sealed record MaterializationCapabilityMatch
{
    /// <summary>Creates a capability match result.</summary>
    /// <param name="decisions">One decision for every requirement in canonical identity order.</param>
    /// <param name="validation">Structured mismatch diagnostics.</param>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="decisions"/> contains null or duplicate requirements, or <paramref name="validation"/>
    /// contains an incomplete materialization diagnostic.
    /// </exception>
    [JsonConstructor]
    public MaterializationCapabilityMatch(
        ImmutableArray<MaterializationCapabilityDecision> decisions,
        DocumentValidationResult validation)
    {
        var normalized = decisions.IsDefault ? [] : decisions;
        if (normalized.Any(static decision => decision is null))
        {
            throw new ArgumentException("Capability decisions cannot contain null entries.", nameof(decisions));
        }

        if (normalized.GroupBy(static decision => decision.Requirement.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A capability match cannot repeat a requirement.", nameof(decisions));
        }

        Decisions = [.. normalized.OrderBy(static decision => decision.Requirement.Id.Value, StringComparer.Ordinal)];
        ArgumentNullException.ThrowIfNull(validation);
        var diagnostics = MaterializationContract.NormalizeDiagnostics(validation.Diagnostics, nameof(validation));
        Validation = diagnostics == validation.Diagnostics
            ? validation
            : diagnostics.IsDefaultOrEmpty
                ? DocumentValidationResult.Valid
                : new DocumentValidationResult(diagnostics);
    }

    /// <summary>One decision per requirement in canonical identity order.</summary>
    public ImmutableArray<MaterializationCapabilityDecision> Decisions { get; }

    /// <summary>Structured mismatch diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether every requirement has an evidence-backed realization and no error diagnostic.</summary>
    [JsonIgnore]
    public bool IsSatisfied => Validation.IsValid
        && Decisions.All(static decision => decision.Realization != MaterializationCapabilityRealizationKind.Unavailable);
}

/// <summary>Matches backend-independent requirements to one exact endpoint capability snapshot.</summary>
public static class MaterializationCapabilityMatcher
{
    /// <summary>Matches one role-homogeneous requirement set against an endpoint profile.</summary>
    /// <param name="requirements">Capability requirements to prove.</param>
    /// <param name="profile">Exact endpoint profile supplying evidence.</param>
    /// <returns>Deterministic decisions and structured fail-closed diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="requirements"/> contains null or duplicate identities.</exception>
    public static MaterializationCapabilityMatch Match(
        ImmutableArray<MaterializationCapabilityRequirement> requirements,
        MaterializationCapabilityProfile profile) => MatchCore(requirements, profile, mode: null);

    /// <summary>Matches only requirements that apply to one selected synchronization mode.</summary>
    /// <param name="requirements">Capability requirements declared by the materialization definition.</param>
    /// <param name="profile">Exact endpoint profile supplying evidence.</param>
    /// <param name="mode">One concrete rebuild or incremental run mode.</param>
    /// <returns>Deterministic decisions and structured fail-closed diagnostics for that mode.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="requirements"/> contains null or duplicate identities.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is combined or unsupported.</exception>
    public static MaterializationCapabilityMatch MatchForMode(
        ImmutableArray<MaterializationCapabilityRequirement> requirements,
        MaterializationCapabilityProfile profile,
        MaterializationSynchronizationMode mode)
    {
        MaterializationSynchronizationModes.RequireValid(mode, nameof(mode), allowCombined: false);
        return MatchCore(requirements, profile, mode);
    }

    static MaterializationCapabilityMatch MatchCore(
        ImmutableArray<MaterializationCapabilityRequirement> requirements,
        MaterializationCapabilityProfile profile,
        MaterializationSynchronizationMode? mode)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalized = requirements.IsDefault ? [] : requirements;
        if (normalized.Any(static requirement => requirement is null))
        {
            throw new ArgumentException("Capability requirements cannot contain null entries.", nameof(requirements));
        }

        if (normalized.GroupBy(static requirement => requirement.Id).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Capability requirement identities cannot repeat.", nameof(requirements));
        }

        normalized =
        [
            .. normalized
                .Where(requirement => mode is null || (requirement.Modes & mode.Value) != 0)
                .OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)
        ];
        List<DocumentValidationDiagnostic> diagnostics = [];
        var decisions = ImmutableArray.CreateBuilder<MaterializationCapabilityDecision>(normalized.Length);
        foreach (var requirement in normalized)
        {
            var requiredRole = MaterializationCapabilityCatalog.RoleOf(requirement.Capability);
            if (requiredRole != profile.Role)
            {
                diagnostics.Add(Error(
                    MaterializationCapabilityDiagnosticCodes.EndpointRoleMismatch,
                    requirement,
                    profile,
                    expected: requiredRole.ToString(),
                    observed: profile.Role.ToString()));
                decisions.Add(new(requirement, MaterializationCapabilityRealizationKind.Unavailable));
                continue;
            }

            var candidates = profile.Evidence.Where(item => item.Capability == requirement.Capability).ToArray();
            if (candidates.Length == 0)
            {
                diagnostics.Add(Error(
                    MaterializationCapabilityDiagnosticCodes.CapabilityUnavailable,
                    requirement,
                    profile,
                    expected: requirement.Capability.ToString(),
                    observed: "not advertised"));
                decisions.Add(new(requirement, MaterializationCapabilityRealizationKind.Unavailable));
                continue;
            }

            MaterializationCapabilityEvidence? selected = null;
            foreach (var candidate in candidates)
            {
                if (Satisfies(requirement, candidate))
                {
                    selected = candidate;
                    break;
                }
            }

            if (selected is not null)
            {
                decisions.Add(new(requirement, selected.Realization, selected));
                continue;
            }

            var exemplar = candidates
                .OrderBy(candidate => DeficitCount(requirement, candidate))
                .ThenBy(static candidate => candidate.Id.Value, StringComparer.Ordinal)
                .First();
            foreach (var guarantee in requirement.Guarantees.Except(exemplar.Guarantees))
            {
                diagnostics.Add(Error(
                    MaterializationCapabilityDiagnosticCodes.GuaranteeUnavailable,
                    requirement,
                    profile,
                    expected: guarantee.ToString(),
                    observed: string.Join(",", exemplar.Guarantees),
                    evidence: exemplar));
            }

            foreach (var limit in requirement.OperatingLimits)
            {
                var supplied = exemplar.OperatingLimits.FirstOrDefault(item => item.Kind == limit.Kind);
                if (supplied.Maximum == 0)
                {
                    diagnostics.Add(Error(
                        MaterializationCapabilityDiagnosticCodes.LimitUnavailable,
                        requirement,
                        profile,
                        expected: $"{limit.Kind}>={limit.Maximum}",
                        observed: "not advertised",
                        evidence: exemplar));
                }
                else if (supplied.Maximum < limit.Maximum)
                {
                    diagnostics.Add(Error(
                        MaterializationCapabilityDiagnosticCodes.LimitExceeded,
                        requirement,
                        profile,
                        expected: $"{limit.Kind}>={limit.Maximum}",
                        observed: supplied.Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        evidence: exemplar));
                }
            }

            decisions.Add(new(requirement, MaterializationCapabilityRealizationKind.Unavailable));
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return new(decisions.MoveToImmutable(), DocumentValidationResult.FromDiagnostics(diagnostics));
    }

    internal static bool Satisfies(
        MaterializationCapabilityRequirement requirement,
        MaterializationCapabilityEvidence evidence)
    {
        if (requirement.Guarantees.Any(guarantee => !evidence.Guarantees.Contains(guarantee)))
        {
            return false;
        }

        foreach (var required in requirement.OperatingLimits)
        {
            var supplied = evidence.OperatingLimits.FirstOrDefault(limit => limit.Kind == required.Kind);
            if (supplied.Maximum < required.Maximum)
            {
                return false;
            }
        }

        return true;
    }

    static int DeficitCount(
        MaterializationCapabilityRequirement requirement,
        MaterializationCapabilityEvidence evidence)
    {
        var deficits = requirement.Guarantees.Count(guarantee => !evidence.Guarantees.Contains(guarantee));
        foreach (var required in requirement.OperatingLimits)
        {
            var supplied = evidence.OperatingLimits.FirstOrDefault(limit => limit.Kind == required.Kind);
            if (supplied.Maximum < required.Maximum)
            {
                deficits++;
            }
        }
        return deficits;
    }

    static DocumentValidationDiagnostic Error(
        string code,
        MaterializationCapabilityRequirement requirement,
        MaterializationCapabilityProfile profile,
        string expected,
        string observed,
        MaterializationCapabilityEvidence? evidence = null) =>
        new(
            code,
            DiagnosticSeverity.Error,
            $"Endpoint '{profile.Subject}' cannot prove materialization requirement '{requirement.Id.Value}'.",
            $"/capabilities/{requirement.Id.Value}",
            Evidence: new(
                stage: "materialization-capability-matching",
                subject: requirement.Capability.ToString(),
                sourceReferences: evidence?.SourceReferences ?? [profile.Id.Value],
                expected: expected,
                observed: observed));
}

static class MaterializationSynchronizationModes
{
    internal static void RequireValid(
        MaterializationSynchronizationMode modes,
        string parameterName,
        bool allowCombined)
    {
        const MaterializationSynchronizationMode all = MaterializationSynchronizationMode.All;
        if (modes == 0 || (modes & ~all) != 0 || (!allowCombined && modes == all))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                modes,
                allowCombined
                    ? "At least one supported materialization synchronization mode is required."
                    : "A run must select exactly one materialization synchronization mode.");
        }
    }
}

/// <summary>Canonical ownership metadata for materialization capability kinds.</summary>
public static class MaterializationCapabilityCatalog
{
    static readonly ImmutableArray<MaterializationLimitKind> ReadHardLimits =
        [MaterializationLimitKind.ReadItems, MaterializationLimitKind.ReadBytes];
    static readonly ImmutableArray<MaterializationLimitKind> WriteHardLimits =
        [MaterializationLimitKind.WriteItems, MaterializationLimitKind.WriteBytes];
    static readonly ImmutableArray<MaterializationLimitKind> ContributorLedgerHardLimits =
    [
        MaterializationLimitKind.ReadItems,
        MaterializationLimitKind.ReadBytes,
        MaterializationLimitKind.WriteItems,
        MaterializationLimitKind.WriteBytes
    ];

    /// <summary>Gets the endpoint role that owns a capability.</summary>
    /// <param name="capability">Capability whose owner is requested.</param>
    /// <returns>The source or target role that may advertise the capability.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capability"/> is unsupported.</exception>
    public static MaterializationEndpointRole RoleOf(MaterializationCapabilityKind capability) => capability switch
    {
        MaterializationCapabilityKind.SourceBatchedPointRead
            or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
            or MaterializationCapabilityKind.SourceBoundedEnumeration
            or MaterializationCapabilityKind.SourceContinuation
            or MaterializationCapabilityKind.SourceChangeDelivery
            or MaterializationCapabilityKind.SourceSettlement => MaterializationEndpointRole.Source,
        MaterializationCapabilityKind.TargetGenerationIsolation
            or MaterializationCapabilityKind.TargetBulkUpsert
            or MaterializationCapabilityKind.TargetBulkDelete
            or MaterializationCapabilityKind.TargetPerItemOutcomes
            or MaterializationCapabilityKind.TargetSeal
            or MaterializationCapabilityKind.TargetValidation
            or MaterializationCapabilityKind.TargetFencedPromotion
            or MaterializationCapabilityKind.TargetRetirement
            or MaterializationCapabilityKind.TargetCleanup
            or MaterializationCapabilityKind.TargetContributorLedger => MaterializationEndpointRole.Target,
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unsupported materialization capability.")
    };

    /// <summary>Gets whether a semantic guarantee may be asserted for one materialization capability.</summary>
    /// <param name="capability">Capability whose guarantee dimensions are requested.</param>
    /// <param name="guarantee">Guarantee to test.</param>
    /// <returns><see langword="true"/> when <paramref name="guarantee"/> applies to <paramref name="capability"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capability"/> or <paramref name="guarantee"/> is unsupported.
    /// </exception>
    public static bool AllowsGuarantee(
        MaterializationCapabilityKind capability,
        MaterializationGuaranteeKind guarantee)
    {
        _ = RoleOf(capability);
        if (!Enum.IsDefined(guarantee))
        {
            throw new ArgumentOutOfRangeException(nameof(guarantee), guarantee, "Unsupported materialization guarantee.");
        }

        return guarantee switch
        {
            MaterializationGuaranteeKind.StableOrdering => IsSourceRead(capability)
                || capability == MaterializationCapabilityKind.SourceChangeDelivery,
            MaterializationGuaranteeKind.RequestLocalCompleteness => IsSourceRead(capability)
                || capability == MaterializationCapabilityKind.TargetContributorLedger,
            MaterializationGuaranteeKind.CoordinatedSnapshot
                or MaterializationGuaranteeKind.Reconciliation => IsSourceRead(capability),
            MaterializationGuaranteeKind.BaselinePlusCatchUp
                or MaterializationGuaranteeKind.AtLeastOnceDelivery
                or MaterializationGuaranteeKind.BeforeImage
                or MaterializationGuaranteeKind.RetainedHistoryStart
                or MaterializationGuaranteeKind.CompleteMutationDelivery
                or MaterializationGuaranteeKind.LatestVersionUpsertDelivery =>
                capability == MaterializationCapabilityKind.SourceChangeDelivery,
            MaterializationGuaranteeKind.ExplicitSettlement =>
                capability == MaterializationCapabilityKind.SourceSettlement,
            MaterializationGuaranteeKind.GenerationIsolation =>
                capability == MaterializationCapabilityKind.TargetGenerationIsolation,
            MaterializationGuaranteeKind.IdempotentWrite
                or MaterializationGuaranteeKind.VersionConditionalWrite => IsBulkMutation(capability)
                    || capability == MaterializationCapabilityKind.TargetContributorLedger,
            MaterializationGuaranteeKind.ExactPerItemOutcome =>
                capability == MaterializationCapabilityKind.TargetPerItemOutcomes,
            MaterializationGuaranteeKind.AtomicPromotion
                or MaterializationGuaranteeKind.FencedPromotion =>
                capability == MaterializationCapabilityKind.TargetFencedPromotion,
            MaterializationGuaranteeKind.FencedMutation => IsFencedGenerationMutation(capability),
            MaterializationGuaranteeKind.AtomicWithMaterializationMutation =>
                capability == MaterializationCapabilityKind.TargetContributorLedger,
            _ => false
        };
    }

    /// <summary>Gets whether an operating-limit dimension may be asserted for one materialization capability.</summary>
    /// <param name="capability">Capability whose operating dimensions are requested.</param>
    /// <param name="limit">Operating-limit dimension to test.</param>
    /// <returns><see langword="true"/> when <paramref name="limit"/> applies to <paramref name="capability"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capability"/> or <paramref name="limit"/> is unsupported.
    /// </exception>
    public static bool AllowsLimit(
        MaterializationCapabilityKind capability,
        MaterializationLimitKind limit)
    {
        _ = RoleOf(capability);
        if (!Enum.IsDefined(limit))
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Unsupported materialization limit kind.");
        }

        return limit switch
        {
            MaterializationLimitKind.ReadItems => IsSourceRead(capability)
                || capability == MaterializationCapabilityKind.TargetContributorLedger,
            MaterializationLimitKind.ReadBytes => IsSourceRead(capability)
                || capability is MaterializationCapabilityKind.SourceChangeDelivery
                    or MaterializationCapabilityKind.TargetContributorLedger,
            MaterializationLimitKind.ChangeItems =>
                capability == MaterializationCapabilityKind.SourceChangeDelivery,
            MaterializationLimitKind.WriteItems or MaterializationLimitKind.WriteBytes =>
                IsBulkMutation(capability)
                || capability is MaterializationCapabilityKind.TargetPerItemOutcomes
                    or MaterializationCapabilityKind.TargetContributorLedger,
            MaterializationLimitKind.Parallelism => true,
            MaterializationLimitKind.IndexedIdentityCharacters =>
                IsBulkMutation(capability)
                || capability is MaterializationCapabilityKind.TargetPerItemOutcomes
                    or MaterializationCapabilityKind.TargetGenerationIsolation
                    or MaterializationCapabilityKind.TargetContributorLedger,
            _ => false
        };
    }

    internal static void RequireApplicableDimensions(
        MaterializationCapabilityKind capability,
        ImmutableArray<MaterializationGuaranteeKind> guarantees,
        ImmutableArray<MaterializationOperatingLimit> operatingLimits,
        string guaranteesParameterName,
        string operatingLimitsParameterName)
    {
        foreach (var guarantee in guarantees)
        {
            if (!AllowsGuarantee(capability, guarantee))
            {
                throw new ArgumentException(
                    $"Guarantee '{guarantee}' does not apply to capability '{capability}'.",
                    guaranteesParameterName);
            }
        }

        if (capability == MaterializationCapabilityKind.SourceChangeDelivery)
        {
            var declaresCompleteMutations = guarantees.Contains(
                MaterializationGuaranteeKind.CompleteMutationDelivery);
            var declaresLatestVersionUpserts = guarantees.Contains(
                MaterializationGuaranteeKind.LatestVersionUpsertDelivery);
            if (declaresCompleteMutations == declaresLatestVersionUpserts)
            {
                throw new ArgumentException(
                    $"Capability '{capability}' must declare exactly one change-coverage guarantee: "
                    + $"'{MaterializationGuaranteeKind.CompleteMutationDelivery}' or "
                    + $"'{MaterializationGuaranteeKind.LatestVersionUpsertDelivery}'.",
                    guaranteesParameterName);
            }
        }

        foreach (var limit in operatingLimits)
        {
            if (!AllowsLimit(capability, limit.Kind))
            {
                throw new ArgumentException(
                    $"Operating limit '{limit.Kind}' does not apply to capability '{capability}'.",
                    operatingLimitsParameterName);
            }
        }
    }

    internal static void RequireEvidenceLimits(
        MaterializationCapabilityKind capability,
        ImmutableArray<MaterializationOperatingLimit> operatingLimits,
        string parameterName)
    {
        foreach (var required in RequiredHardLimits(capability))
        {
            RequireLimit(capability, operatingLimits, required, parameterName, "Capability evidence");
        }
    }

    internal static void RequireRequirementLimits(
        MaterializationCapabilityKind capability,
        ImmutableArray<MaterializationOperatingLimit> operatingLimits,
        string parameterName)
    {
        if (capability != MaterializationCapabilityKind.TargetPerItemOutcomes)
        {
            return;
        }

        foreach (var required in RequiredHardLimits(capability))
        {
            RequireLimit(capability, operatingLimits, required, parameterName, "Capability requirement");
        }
    }

    internal static ImmutableArray<MaterializationLimitKind> RequiredHardLimits(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration => ReadHardLimits,
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes => WriteHardLimits,
            MaterializationCapabilityKind.TargetContributorLedger => ContributorLedgerHardLimits,
            _ => []
        };

    static void RequireLimit(
        MaterializationCapabilityKind capability,
        ImmutableArray<MaterializationOperatingLimit> operatingLimits,
        MaterializationLimitKind required,
        string parameterName,
        string contract)
    {
        if (operatingLimits.Any(limit => limit.Kind == required))
        {
            return;
        }

        throw new ArgumentException(
            $"{contract} for '{capability}' must declare a positive '{required}' hard limit.",
            parameterName);
    }

    static bool IsSourceRead(MaterializationCapabilityKind capability) => capability is
        MaterializationCapabilityKind.SourceBatchedPointRead
        or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
        or MaterializationCapabilityKind.SourceBoundedEnumeration
        or MaterializationCapabilityKind.SourceContinuation;

    static bool IsBulkMutation(MaterializationCapabilityKind capability) => capability is
        MaterializationCapabilityKind.TargetBulkUpsert
        or MaterializationCapabilityKind.TargetBulkDelete;

    static bool IsFencedGenerationMutation(MaterializationCapabilityKind capability) =>
        IsBulkMutation(capability)
        || capability is MaterializationCapabilityKind.TargetGenerationIsolation
            or MaterializationCapabilityKind.TargetSeal
            or MaterializationCapabilityKind.TargetValidation
            or MaterializationCapabilityKind.TargetRetirement
            or MaterializationCapabilityKind.TargetCleanup
            or MaterializationCapabilityKind.TargetContributorLedger;
}

/// <summary>Shared validation of operation bounds against attributable materialization capability evidence.</summary>
public static class MaterializationCapabilityLimits
{
    /// <summary>Requires one evidence assertion to cover the complete requested item-and-byte bound pair.</summary>
    /// <param name="profile">Endpoint capability profile supplying attributable evidence.</param>
    /// <param name="capability">Capability required by the operation.</param>
    /// <param name="itemLimitKind">Item-count dimension applicable to the capability.</param>
    /// <param name="requestedItems">Positive requested item count.</param>
    /// <param name="byteLimitKind">Encoded-byte dimension applicable to the capability.</param>
    /// <param name="requestedBytes">Positive requested encoded-byte count.</param>
    /// <param name="parameterName">Caller parameter reported when the bound is unsupported.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> or <paramref name="parameterName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="parameterName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A capability or limit kind is unsupported, a limit kind is not the canonical item or byte dimension for the
    /// capability, a requested bound is not positive, or no single evidence assertion covers both requested bounds.
    /// </exception>
    public static void RequireSupportedBounds(
        MaterializationCapabilityProfile profile,
        MaterializationCapabilityKind capability,
        MaterializationLimitKind itemLimitKind,
        long requestedItems,
        MaterializationLimitKind byteLimitKind,
        long requestedBytes,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        if (SupportsBounds(
                profile,
                capability,
                itemLimitKind,
                requestedItems,
                byteLimitKind,
                requestedBytes))
        {
            return;
        }

        throw new ArgumentOutOfRangeException(
            parameterName,
            $"Requested {capability} bounds ({requestedItems} {itemLimitKind}, {requestedBytes} {byteLimitKind}) "
            + $"exceed every attributable realization in capability profile '{profile.Id.Value}'.");
    }

    /// <summary>Determines whether one evidence assertion covers the complete requested item-and-byte bound pair.</summary>
    /// <param name="profile">Endpoint capability profile supplying attributable evidence.</param>
    /// <param name="capability">Capability required by the operation.</param>
    /// <param name="itemLimitKind">Item-count dimension applicable to the capability.</param>
    /// <param name="requestedItems">Positive requested item count.</param>
    /// <param name="byteLimitKind">Encoded-byte dimension applicable to the capability.</param>
    /// <param name="requestedBytes">Positive requested encoded-byte count.</param>
    /// <returns><see langword="true"/> when one attributable assertion covers both bounds.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The capability or a limit kind is unsupported, a limit kind is not the canonical item or byte dimension for
    /// the capability, or a requested bound is not positive.
    /// </exception>
    public static bool SupportsBounds(
        MaterializationCapabilityProfile profile,
        MaterializationCapabilityKind capability,
        MaterializationLimitKind itemLimitKind,
        long requestedItems,
        MaterializationLimitKind byteLimitKind,
        long requestedBytes)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(capability))
            throw new ArgumentOutOfRangeException(nameof(capability), capability, "The materialization capability is unsupported.");
        if (!Enum.IsDefined(itemLimitKind))
            throw new ArgumentOutOfRangeException(nameof(itemLimitKind), itemLimitKind, "The materialization limit kind is unsupported.");
        if (!Enum.IsDefined(byteLimitKind))
            throw new ArgumentOutOfRangeException(nameof(byteLimitKind), byteLimitKind, "The materialization limit kind is unsupported.");
        if (!IsItemLimit(itemLimitKind)
            || !MaterializationCapabilityCatalog.AllowsLimit(capability, itemLimitKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(itemLimitKind),
                itemLimitKind,
                $"'{itemLimitKind}' is not an applicable item-count limit for capability '{capability}'.");
        }
        if (!IsByteLimit(byteLimitKind)
            || !MaterializationCapabilityCatalog.AllowsLimit(capability, byteLimitKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteLimitKind),
                byteLimitKind,
                $"'{byteLimitKind}' is not an applicable encoded-byte limit for capability '{capability}'.");
        }
        if (requestedItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedItems), requestedItems, "A requested item bound must be positive.");
        if (requestedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedBytes), requestedBytes, "A requested byte bound must be positive.");
        foreach (var evidence in profile.Evidence)
        {
            if (evidence.Capability != capability)
            {
                continue;
            }

            var itemLimit = Maximum(evidence, itemLimitKind);
            var byteLimit = Maximum(evidence, byteLimitKind);
            if (requestedItems <= itemLimit && requestedBytes <= byteLimit)
            {
                return true;
            }
        }

        return false;
    }

    static bool IsItemLimit(MaterializationLimitKind kind) => kind is
        MaterializationLimitKind.ReadItems
        or MaterializationLimitKind.ChangeItems
        or MaterializationLimitKind.WriteItems;

    static bool IsByteLimit(MaterializationLimitKind kind) => kind is
        MaterializationLimitKind.ReadBytes
        or MaterializationLimitKind.WriteBytes;

    static long Maximum(
        MaterializationCapabilityEvidence evidence,
        MaterializationLimitKind kind)
    {
        foreach (var limit in evidence.OperatingLimits)
        {
            if (limit.Kind == kind)
            {
                return limit.Maximum;
            }
        }

        return 0;
    }
}

static class MaterializationCapabilityOrdering
{
    internal static bool HasOverlappingModes(
        ImmutableArray<MaterializationCapabilityRequirement> requirements) =>
        requirements
            .GroupBy(static requirement => requirement.Capability)
            .Any(static group =>
            {
                var occupied = (MaterializationSynchronizationMode)0;
                foreach (var requirement in group)
                {
                    if ((occupied & requirement.Modes) != 0)
                    {
                        return true;
                    }

                    occupied |= requirement.Modes;
                }
                return false;
            });

    internal static ImmutableArray<MaterializationGuaranteeKind> NormalizeGuarantees(
        ImmutableArray<MaterializationGuaranteeKind> values,
        string parameterName)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => !Enum.IsDefined(value)))
        {
            throw new ArgumentOutOfRangeException(parameterName, "A materialization guarantee is unsupported.");
        }

        if (normalized.Distinct().Count() != normalized.Length)
        {
            throw new ArgumentException("Materialization guarantees cannot repeat.", parameterName);
        }

        return normalized.Sort();
    }

    internal static ImmutableArray<MaterializationOperatingLimit> NormalizeLimits(
        ImmutableArray<MaterializationOperatingLimit> values,
        string parameterName)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => value.Maximum <= 0 || !Enum.IsDefined(value.Kind)))
        {
            throw new ArgumentException("Materialization operating limits must be defined and positive.", parameterName);
        }

        if (normalized.GroupBy(static value => value.Kind).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A materialization limit kind cannot repeat.", parameterName);
        }

        return normalized.Sort(static (left, right) => left.Kind.CompareTo(right.Kind));
    }

    internal static ImmutableArray<string> NormalizeStrings(
        ImmutableArray<string> values,
        string parameterName,
        bool requireNonEmpty)
    {
        var normalized = values.IsDefault ? [] : values;
        if (requireNonEmpty && normalized.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one evidence reference is required.", parameterName);
        }

        if (normalized.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Evidence references cannot be empty.", parameterName);
        }

        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("Evidence references cannot repeat.", parameterName);
        }

        return [.. normalized.Order(StringComparer.Ordinal)];
    }
}
