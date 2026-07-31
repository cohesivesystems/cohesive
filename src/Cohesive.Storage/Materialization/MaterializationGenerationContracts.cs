using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable identity of one physical materialization target.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationTargetId
{
    /// <summary>Creates a materialization-target identity.</summary>
    /// <param name="value">Stable provider-neutral target identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationTargetId(string value) => Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the stable provider-neutral target identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable provider-neutral target identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Caller-assigned identity of one isolated target generation.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationGenerationId
{
    /// <summary>Creates a materialization-generation identity.</summary>
    /// <param name="value">Stable identity that is never reused for different generation intent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationGenerationId(string value) => Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the stable generation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable generation identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one bounded target-write batch.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationBatchId
{
    /// <summary>Creates a materialization batch identity.</summary>
    /// <param name="value">Idempotency identity retained across an ambiguous batch retry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationBatchId(string value) => Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the stable batch identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable batch identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable logical key of one materialized item.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationItemId
{
    /// <summary>Creates a materialization-item identity.</summary>
    /// <param name="value">Stable key ordered by Unicode scalar value in canonical seals.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.
    /// </exception>
    [JsonConstructor]
    public MaterializationItemId(string value) =>
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the stable logical item key.</summary>
    public string Value { get; }

    /// <summary>Returns the stable logical item key.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable idempotency identity of one logical item mutation.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationItemMutationId
{
    /// <summary>Creates an item-mutation identity.</summary>
    /// <param name="value">Identity reused only for an exact retry of the same mutation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationItemMutationId(string value) => Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the stable item-mutation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable item-mutation identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Monotonic logical version of one materialized item.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationItemVersion : IComparable<MaterializationItemVersion>
{
    /// <summary>Creates a positive canonical item version.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a canonical positive integer.</exception>
    [JsonConstructor]
    public MaterializationItemVersion(string value)
    {
        Value = MaterializationContract.RequireOrdinal(value, nameof(value), allowZero: false, out var ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Gets the canonical item-version value.</summary>
    public string Value { get; }

    /// <summary>Gets the positive numeric version used for ordering.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <inheritdoc />
    public int CompareTo(MaterializationItemVersion other) => Ordinal.CompareTo(other.Ordinal);

    /// <summary>Returns the canonical item version.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Monotonic revision of one isolated generation.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationGenerationRevision
{
    /// <summary>Gets the first revision assigned to a newly begun generation.</summary>
    public static MaterializationGenerationRevision Initial { get; } = new("1");

    /// <summary>Creates a positive canonical generation revision.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a canonical positive integer.</exception>
    [JsonConstructor]
    public MaterializationGenerationRevision(string value)
    {
        Value = MaterializationContract.RequireOrdinal(value, nameof(value), allowZero: false, out var ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Gets the canonical generation revision.</summary>
    public string Value { get; }

    /// <summary>Gets the positive numeric revision.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <summary>Returns the canonical generation revision.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;

    internal MaterializationGenerationRevision Next() =>
        new(checked(Ordinal + 1).ToString(CultureInfo.InvariantCulture));
}

/// <summary>Monotonic compare-and-swap revision of a target's active-generation pointer.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationTargetRevision
{
    /// <summary>Gets the revision of a target on which no promotion has committed.</summary>
    public static MaterializationTargetRevision Initial { get; } = new("0");

    /// <summary>Creates a nonnegative canonical target revision.</summary>
    /// <param name="value">Canonical invariant-culture nonnegative 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a canonical nonnegative integer.</exception>
    [JsonConstructor]
    public MaterializationTargetRevision(string value)
    {
        Value = MaterializationContract.RequireOrdinal(value, nameof(value), allowZero: true, out var ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Gets the canonical target revision.</summary>
    public string Value { get; }

    /// <summary>Gets the nonnegative numeric revision.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <summary>Returns the canonical target revision.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;

    internal MaterializationTargetRevision Next() =>
        new(checked(Ordinal + 1).ToString(CultureInfo.InvariantCulture));
}

/// <summary>Monotonic ownership fence of a materialization worker.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationWorkerFence
{
    /// <summary>Gets the first valid materialization-worker fence.</summary>
    public static MaterializationWorkerFence Initial { get; } = new("1");

    /// <summary>Creates a positive canonical worker fence.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a canonical positive integer.</exception>
    [JsonConstructor]
    public MaterializationWorkerFence(string value)
    {
        Value = MaterializationContract.RequireOrdinal(value, nameof(value), allowZero: false, out var ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Gets the canonical worker-fence value.</summary>
    public string Value { get; }

    /// <summary>Gets the positive numeric fence.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <summary>Returns the canonical worker fence.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Monotonic fence for the target-wide active-generation pointer.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationPromotionFence
{
    /// <summary>Gets the first valid target-promotion fence.</summary>
    public static MaterializationPromotionFence Initial { get; } = new("1");

    /// <summary>Creates a positive canonical target-promotion fence.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a canonical positive integer.</exception>
    [JsonConstructor]
    public MaterializationPromotionFence(string value)
    {
        Value = MaterializationContract.RequireOrdinal(value, nameof(value), allowZero: false, out var ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Gets the canonical target-promotion-fence value.</summary>
    public string Value { get; }

    /// <summary>Gets the positive numeric fence.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <summary>Returns the canonical target-promotion fence.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of a generation-seal operation.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationSealId
{
    /// <summary>Creates a seal identity.</summary>
    /// <param name="value">Identity reused only for an exact retry of the same seal request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationSealId(string value) => Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the seal identity.</summary>
    public string Value { get; }

    /// <summary>Returns the seal identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Deterministic fingerprint of immutable sealed-generation content.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationSealFingerprint
{
    /// <summary>Creates a seal fingerprint.</summary>
    /// <param name="value">Versioned deterministic fingerprint value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationSealFingerprint(string value) => Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the versioned deterministic fingerprint.</summary>
    public string Value { get; }

    /// <summary>Returns the seal fingerprint.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one validation attempt.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationValidationId
{
    /// <summary>Creates a validation identity.</summary>
    /// <param name="value">Identity reused only for an exact retry of the same validation request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationValidationId(string value) => Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the validation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the validation identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Deterministic fingerprint of one validation request and its observed result.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationValidationFingerprint
{
    /// <summary>Creates a validation fingerprint.</summary>
    /// <param name="value">Versioned deterministic fingerprint value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationValidationFingerprint(string value) => Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the versioned deterministic fingerprint.</summary>
    public string Value { get; }

    /// <summary>Returns the validation fingerprint.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable idempotency identity of one active-generation promotion.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationPromotionId
{
    /// <summary>Creates a promotion identity.</summary>
    /// <param name="value">Identity reused only for an exact retry of the same promotion intent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationPromotionId(string value) => Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the promotion identity.</summary>
    public string Value { get; }

    /// <summary>Returns the promotion identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable idempotency identity of one logical generation retirement.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationRetirementId
{
    /// <summary>Creates a retirement identity.</summary>
    /// <param name="value">Identity reused only for an exact retry of the same retirement intent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationRetirementId(string value) => Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the retirement identity.</summary>
    public string Value { get; }

    /// <summary>Returns the retirement identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable idempotency identity of one physical generation cleanup.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationCleanupId
{
    /// <summary>Creates a cleanup identity.</summary>
    /// <param name="value">Identity reused only for an exact retry of the same cleanup intent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.</exception>
    [JsonConstructor]
    public MaterializationCleanupId(string value) => Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the cleanup identity.</summary>
    public string Value { get; }

    /// <summary>Returns the cleanup identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Lifecycle state of one isolated target generation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationGenerationState
{
    /// <summary>The generation accepts bounded item batches.</summary>
    Loading = 0,

    /// <summary>The generation is immutable and carries a seal receipt.</summary>
    Sealed = 1,

    /// <summary>The sealed generation passed validation and may be promoted.</summary>
    Validated = 2,

    /// <summary>The generation is the target's single active read generation and accepts fenced incremental mutations.</summary>
    Active = 3,

    /// <summary>The generation was displaced from active reads but remains available for drain or rollback policy.</summary>
    Inactive = 4,

    /// <summary>The inactive or abandoned generation is logically retired and eligible for separate cleanup.</summary>
    Retired = 5
}

/// <summary>Kind of one provider-neutral materialized-item mutation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationItemMutationKind
{
    /// <summary>Insert or replace one portable materialized value.</summary>
    Upsert = 0,

    /// <summary>Remove one portable materialized value while retaining version evidence.</summary>
    Delete = 1
}

/// <summary>Outcome of one item in a bounded target-write batch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationItemOutcomeDisposition
{
    /// <summary>The mutation was applied.</summary>
    Applied = 0,

    /// <summary>The exact prior mutation result was reused without another physical write.</summary>
    Replayed = 1,

    /// <summary>The target rejected this item transiently and a later batch may retry it.</summary>
    RetryableRejected = 2,

    /// <summary>The target rejected this item permanently for its current content.</summary>
    PermanentFailure = 3,

    /// <summary>The supplied item version does not advance retained version evidence.</summary>
    VersionConflict = 4,

    /// <summary>The mutation identity was reused for different item content.</summary>
    IdempotencyConflict = 5
}

/// <summary>Batch-level disposition independent of individual item outcomes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationBatchDisposition
{
    /// <summary>The batch was evaluated and every request item has one outcome.</summary>
    Applied = 0,

    /// <summary>The exact prior batch result was replayed.</summary>
    Replayed = 1,

    /// <summary>The batch identity was reused for different canonical content; every item reports idempotency conflict.</summary>
    IdentityConflict = 2,

    /// <summary>The addressed generation does not exist; every item reports permanent failure.</summary>
    GenerationNotFound = 3,

    /// <summary>The addressed generation is not writable; every item reports permanent failure.</summary>
    GenerationNotWritable = 4,

    /// <summary>The request exceeds a declared target batch limit; every item reports retryable rejection.</summary>
    LimitExceeded = 5,

    /// <summary>A newer materialization worker fence superseded the request; every item reports retryable rejection.</summary>
    StaleFence = 6
}

/// <summary>Common lifecycle-operation disposition.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationTargetOperationDisposition
{
    /// <summary>The requested lifecycle transition committed.</summary>
    Applied = 0,

    /// <summary>The exact prior transition result was replayed.</summary>
    Replayed = 1,

    /// <summary>The addressed generation does not exist.</summary>
    NotFound = 2,

    /// <summary>The caller-assigned generation already exists.</summary>
    AlreadyExists = 3,

    /// <summary>The stable operation identity was reused for different content.</summary>
    IdentityConflict = 4,

    /// <summary>The expected generation or target revision is stale.</summary>
    RevisionConflict = 5,

    /// <summary>The generation is not in a state that permits the transition.</summary>
    StateConflict = 6,

    /// <summary>The requested active-generation expectation does not match the target.</summary>
    ActiveGenerationConflict = 7,

    /// <summary>A newer materialization worker fence superseded the request.</summary>
    StaleFence = 8,

    /// <summary>Validation did not establish that the generation is promotable.</summary>
    ValidationFailed = 9,

    /// <summary>The request belongs to a different logical materialization than the bound target.</summary>
    MaterializationConflict = 10
}

/// <summary>Descriptor of a concrete materialization target and its capability evidence.</summary>
public sealed record MaterializationTargetDescriptor
{
    /// <summary>Creates a target descriptor.</summary>
    /// <param name="id">Stable target identity.</param>
    /// <param name="materializationId">Logical materialization whose single active generation the target stores.</param>
    /// <param name="capabilities">Capability profile whose target role and subject address <paramref name="id"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity is default, or the profile is not target-role evidence for <paramref name="id"/>.
    /// </exception>
    public MaterializationTargetDescriptor(
        MaterializationTargetId id,
        MaterializationId materializationId,
        MaterializationCapabilityProfile capabilities)
    {
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        MaterializationContract.RequireDefinedIdentity(materializationId.Value, nameof(materializationId));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        if (capabilities.Role != MaterializationEndpointRole.Target
            || !string.Equals(capabilities.Subject, id.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A target descriptor requires target-role capability evidence for its exact target identity.",
                nameof(capabilities));
        }

        Id = id;
        MaterializationId = materializationId;
    }

    /// <summary>Gets the stable target identity.</summary>
    public MaterializationTargetId Id { get; }

    /// <summary>Gets the logical materialization whose single active generation the target stores.</summary>
    public MaterializationId MaterializationId { get; }

    /// <summary>Gets the complete capability and limit evidence advertised by the target adapter.</summary>
    public MaterializationCapabilityProfile Capabilities { get; }
}

/// <summary>Provider-neutral mutation of one materialized item.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$mutation")]
[JsonDerivedType(typeof(MaterializationUpsert), "upsert")]
[JsonDerivedType(typeof(MaterializationDelete), "delete")]
public abstract record MaterializationItemMutation
{
    /// <summary>Creates a provider-neutral item mutation.</summary>
    /// <param name="itemId">Stable logical output key.</param>
    /// <param name="mutationId">Stable idempotency identity.</param>
    /// <param name="version">Monotonic logical item version.</param>
    /// <exception cref="ArgumentException">An identity or version is default.</exception>
    protected MaterializationItemMutation(
        MaterializationItemId itemId,
        MaterializationItemMutationId mutationId,
        MaterializationItemVersion version)
    {
        MaterializationContract.RequireDefinedIdentity(itemId.Value, nameof(itemId));
        MaterializationContract.RequireDefinedIdentity(mutationId.Value, nameof(mutationId));
        MaterializationContract.RequireDefinedIdentity(version.Value, nameof(version));
        ItemId = itemId;
        MutationId = mutationId;
        Version = version;
    }

    /// <summary>Gets the provider-neutral mutation kind projected authoritatively from the concrete wire subtype.</summary>
    [JsonIgnore]
    public abstract MaterializationItemMutationKind Kind { get; }

    /// <summary>Gets the stable logical output key.</summary>
    public MaterializationItemId ItemId { get; }

    /// <summary>Gets the stable mutation idempotency identity.</summary>
    public MaterializationItemMutationId MutationId { get; }

    /// <summary>Gets the monotonic item version.</summary>
    public MaterializationItemVersion Version { get; }
}

/// <summary>Provider-neutral upsert of one portable materialized value.</summary>
public sealed record MaterializationUpsert : MaterializationItemMutation
{
    /// <summary>Creates a materialized-value upsert.</summary>
    /// <param name="itemId">Stable logical output key.</param>
    /// <param name="mutationId">Stable idempotency identity.</param>
    /// <param name="version">Monotonic logical item version.</param>
    /// <param name="value">Materialized portable value.</param>
    /// <exception cref="ArgumentException">An identity or version is default, or <paramref name="value"/> is undefined.</exception>
    public MaterializationUpsert(
        MaterializationItemId itemId,
        MaterializationItemMutationId mutationId,
        MaterializationItemVersion version,
        ObservationValue value)
        : base(itemId, mutationId, version)
    {
        if (value.Kind == ObservationValueKind.Undefined)
        {
            throw new ArgumentException("A materialized upsert value cannot be undefined.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the portable replacement value.</summary>
    public ObservationValue Value { get; }

    /// <inheritdoc />
    [JsonIgnore]
    public override MaterializationItemMutationKind Kind => MaterializationItemMutationKind.Upsert;
}

/// <summary>Provider-neutral delete of one materialized item.</summary>
public sealed record MaterializationDelete : MaterializationItemMutation
{
    /// <summary>Creates a materialized-item delete.</summary>
    /// <param name="itemId">Stable logical output key.</param>
    /// <param name="mutationId">Stable idempotency identity.</param>
    /// <param name="version">Monotonic logical item version.</param>
    /// <exception cref="ArgumentException">An identity or version is default.</exception>
    public MaterializationDelete(
        MaterializationItemId itemId,
        MaterializationItemMutationId mutationId,
        MaterializationItemVersion version)
        : base(itemId, mutationId, version)
    {
    }

    /// <inheritdoc />
    [JsonIgnore]
    public override MaterializationItemMutationKind Kind => MaterializationItemMutationKind.Delete;
}

/// <summary>Request to begin a new isolated caller-identified generation.</summary>
public sealed record MaterializationBeginGenerationRequest
{
    /// <summary>Creates a begin-generation request.</summary>
    /// <param name="materializationId">Stable logical materialization identity.</param>
    /// <param name="generationId">Caller-assigned generation identity.</param>
    /// <param name="definitionFingerprint">Fingerprint of the exact canonical materialization definition.</param>
    /// <param name="workerFence">Current monotonic ownership fence of the requesting worker.</param>
    /// <param name="createdAtUtc">UTC generation creation time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definitionFingerprint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default, or <paramref name="createdAtUtc"/> is not UTC.</exception>
    public MaterializationBeginGenerationRequest(
        MaterializationId materializationId,
        MaterializationGenerationId generationId,
        ExecutionDefinitionFingerprint definitionFingerprint,
        MaterializationWorkerFence workerFence,
        DateTimeOffset createdAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(materializationId.Value, nameof(materializationId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        MaterializationId = materializationId;
        GenerationId = generationId;
        DefinitionFingerprint = definitionFingerprint ?? throw new ArgumentNullException(nameof(definitionFingerprint));
        MaterializationContract.RequireDefinedIdentity(workerFence.Value, nameof(workerFence));
        WorkerFence = workerFence;
        MaterializationContract.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>Gets the stable logical materialization identity.</summary>
    public MaterializationId MaterializationId { get; }

    /// <summary>Gets the caller-assigned generation identity.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the fingerprint of the exact canonical materialization definition.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Gets the current monotonic ownership fence of the requesting worker.</summary>
    public MaterializationWorkerFence WorkerFence { get; }

    /// <summary>Gets the UTC creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; }
}

/// <summary>One bounded bulk upsert/delete request.</summary>
public sealed record MaterializationApplyBatchRequest
{
    /// <summary>Creates a bounded item-mutation request.</summary>
    /// <param name="batchId">Stable batch identity.</param>
    /// <param name="generationId">Loading candidate or active generation receiving the mutations.</param>
    /// <param name="workerFence">Current monotonic ownership fence of the requesting worker.</param>
    /// <param name="mutations">Non-empty item mutations with unique item and idempotency identities.</param>
    /// <exception cref="ArgumentException">An identity is default, or mutations are default, empty, null, or duplicate an item or mutation identity.</exception>
    public MaterializationApplyBatchRequest(
        MaterializationBatchId batchId,
        MaterializationGenerationId generationId,
        MaterializationWorkerFence workerFence,
        ImmutableArray<MaterializationItemMutation> mutations)
    {
        MaterializationContract.RequireDefinedIdentity(batchId.Value, nameof(batchId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        MaterializationContract.RequireDefinedIdentity(workerFence.Value, nameof(workerFence));
        if (mutations.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A target batch requires one or more non-null item mutations.", nameof(mutations));
        }

        HashSet<MaterializationItemId> keys = [];
        HashSet<MaterializationItemMutationId> mutationIds = [];
        foreach (var mutation in mutations)
        {
            if (mutation is null)
            {
                throw new ArgumentException("A target batch requires non-null item mutations.", nameof(mutations));
            }

            if (!keys.Add(mutation.ItemId))
            {
                throw new ArgumentException($"Item '{mutation.ItemId.Value}' occurs more than once in a target batch.", nameof(mutations));
            }

            if (!mutationIds.Add(mutation.MutationId))
            {
                throw new ArgumentException(
                    $"Mutation identity '{mutation.MutationId.Value}' occurs more than once in a target batch.",
                    nameof(mutations));
            }
        }

        BatchId = batchId;
        GenerationId = generationId;
        WorkerFence = workerFence;
        Mutations = mutations;
    }

    /// <summary>Gets the stable batch identity.</summary>
    public MaterializationBatchId BatchId { get; }

    /// <summary>Gets the addressed generation.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the current monotonic ownership fence of the requesting worker.</summary>
    public MaterializationWorkerFence WorkerFence { get; }

    /// <summary>Gets the immutable input-order item mutations.</summary>
    public ImmutableArray<MaterializationItemMutation> Mutations { get; }
}

/// <summary>Outcome of exactly one requested materialized-item mutation.</summary>
public sealed record MaterializationItemOutcome
{
    /// <summary>Creates one keyed item outcome.</summary>
    /// <param name="itemId">Requested logical item key.</param>
    /// <param name="mutationId">Requested mutation identity.</param>
    /// <param name="disposition">Observable item outcome.</param>
    /// <param name="code">Optional stable failure or rejection code.</param>
    /// <param name="message">Optional human-readable failure or rejection explanation.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">An identity is default, or failure details do not match the disposition.</exception>
    public MaterializationItemOutcome(
        MaterializationItemId itemId,
        MaterializationItemMutationId mutationId,
        MaterializationItemOutcomeDisposition disposition,
        string? code = null,
        string? message = null)
    {
        MaterializationContract.RequireDefinedIdentity(itemId.Value, nameof(itemId));
        MaterializationContract.RequireDefinedIdentity(mutationId.Value, nameof(mutationId));
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported item outcome disposition.");
        }

        var success = disposition is MaterializationItemOutcomeDisposition.Applied or MaterializationItemOutcomeDisposition.Replayed;
        if (success && (code is not null || message is not null))
        {
            throw new ArgumentException("Successful item outcomes cannot carry failure details.", nameof(code));
        }

        if (!success && (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(message)))
        {
            throw new ArgumentException("Unsuccessful item outcomes require a stable code and message.", nameof(code));
        }

        ItemId = itemId;
        MutationId = mutationId;
        Disposition = disposition;
        Code = code?.Trim();
        Message = message?.Trim();
    }

    /// <summary>Gets the requested logical item key.</summary>
    public MaterializationItemId ItemId { get; }

    /// <summary>Gets the requested mutation identity.</summary>
    public MaterializationItemMutationId MutationId { get; }

    /// <summary>Gets the observable item outcome.</summary>
    public MaterializationItemOutcomeDisposition Disposition { get; }

    /// <summary>Gets the optional stable failure or rejection code.</summary>
    public string? Code { get; }

    /// <summary>Gets the optional failure or rejection explanation.</summary>
    public string? Message { get; }
}

/// <summary>Complete result of one bounded target-write batch.</summary>
public sealed record MaterializationBatchResult
{
    /// <summary>Creates a complete keyed batch result.</summary>
    /// <param name="batchId">Stable batch identity.</param>
    /// <param name="generationId">Addressed generation.</param>
    /// <param name="disposition">Batch-level outcome.</param>
    /// <param name="generationRevision">Generation revision after the request, when the generation exists.</param>
    /// <param name="outcomes">Exactly one outcome per requested item in request order.</param>
    /// <exception cref="ArgumentException">An identity is default, outcomes are null or duplicate keys, or a revision is inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    public MaterializationBatchResult(
        MaterializationBatchId batchId,
        MaterializationGenerationId generationId,
        MaterializationBatchDisposition disposition,
        MaterializationGenerationRevision? generationRevision,
        ImmutableArray<MaterializationItemOutcome> outcomes)
    {
        MaterializationContract.RequireDefinedIdentity(batchId.Value, nameof(batchId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported batch disposition.");
        }

        if (disposition == MaterializationBatchDisposition.GenerationNotFound && generationRevision is not null)
        {
            throw new ArgumentException("A missing generation cannot carry a generation revision.", nameof(generationRevision));
        }

        if (disposition is MaterializationBatchDisposition.Applied or MaterializationBatchDisposition.Replayed
            && generationRevision is null)
        {
            throw new ArgumentException("An applied or replayed batch result requires a generation revision.", nameof(generationRevision));
        }
        if (outcomes.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A batch result requires non-null keyed outcomes.", nameof(outcomes));
        }

        HashSet<MaterializationItemId> keys = [];
        foreach (var outcome in outcomes)
        {
            if (outcome is null)
            {
                throw new ArgumentException("A batch result requires non-null keyed outcomes.", nameof(outcomes));
            }

            if (!keys.Add(outcome.ItemId))
            {
                throw new ArgumentException("A batch result cannot repeat a logical item key.", nameof(outcomes));
            }
        }
        ValidateDispositionOutcomes(disposition, outcomes);

        BatchId = batchId;
        GenerationId = generationId;
        Disposition = disposition;
        GenerationRevision = generationRevision;
        Outcomes = outcomes;
    }

    static void ValidateDispositionOutcomes(
        MaterializationBatchDisposition disposition,
        ImmutableArray<MaterializationItemOutcome> outcomes)
    {
        var coherent = disposition switch
        {
            MaterializationBatchDisposition.Applied => true,
            MaterializationBatchDisposition.Replayed => outcomes.All(static outcome =>
                outcome.Disposition != MaterializationItemOutcomeDisposition.Applied),
            MaterializationBatchDisposition.IdentityConflict => outcomes.All(static outcome =>
                outcome.Disposition == MaterializationItemOutcomeDisposition.IdempotencyConflict),
            MaterializationBatchDisposition.GenerationNotFound
                or MaterializationBatchDisposition.GenerationNotWritable => outcomes.All(static outcome =>
                    outcome.Disposition == MaterializationItemOutcomeDisposition.PermanentFailure),
            MaterializationBatchDisposition.LimitExceeded
                or MaterializationBatchDisposition.StaleFence => outcomes.All(static outcome =>
                    outcome.Disposition == MaterializationItemOutcomeDisposition.RetryableRejected),
            _ => false
        };
        if (!coherent)
        {
            throw new ArgumentException(
                "Batch-level disposition and per-item outcomes describe contradictory target effects.",
                nameof(outcomes));
        }
    }

    /// <summary>Creates a result and proves exact request-order correspondence for every item.</summary>
    /// <param name="request">Exact bounded batch request being answered.</param>
    /// <param name="disposition">Batch-level disposition.</param>
    /// <param name="generationRevision">Generation revision after evaluation, when available.</param>
    /// <param name="outcomes">Exactly one keyed outcome per request mutation in request order.</param>
    /// <returns>A validated complete batch result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The result is structurally invalid or does not correspond exactly to <paramref name="request"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    public static MaterializationBatchResult ForRequest(
        MaterializationApplyBatchRequest request,
        MaterializationBatchDisposition disposition,
        MaterializationGenerationRevision? generationRevision,
        ImmutableArray<MaterializationItemOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(request);
        MaterializationBatchResult result = new(
            request.BatchId,
            request.GenerationId,
            disposition,
            generationRevision,
            outcomes);
        result.ValidateAgainst(request);
        return result;
    }

    /// <summary>Validates that this result answers one exact request with no missing, extra, reordered, or substituted item.</summary>
    /// <param name="request">Exact bounded batch request that should correspond to this result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Batch identity, generation identity, outcome count, order, item identity, or mutation identity differs.</exception>
    public void ValidateAgainst(MaterializationApplyBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (BatchId != request.BatchId || GenerationId != request.GenerationId)
        {
            throw new ArgumentException("A batch result must address the exact request batch and generation.", nameof(request));
        }

        if (Outcomes.Length != request.Mutations.Length)
        {
            throw new ArgumentException("A batch result must contain exactly one outcome per request item.", nameof(request));
        }

        for (var index = 0; index < Outcomes.Length; index++)
        {
            var outcome = Outcomes[index];
            var mutation = request.Mutations[index];
            if (outcome.ItemId != mutation.ItemId || outcome.MutationId != mutation.MutationId)
            {
                throw new ArgumentException(
                    $"Batch outcome {index} does not correspond to its request item and mutation identity.",
                    nameof(request));
            }
        }
    }

    /// <summary>Gets the stable batch identity.</summary>
    public MaterializationBatchId BatchId { get; }

    /// <summary>Gets the addressed generation.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the batch-level disposition.</summary>
    public MaterializationBatchDisposition Disposition { get; }

    /// <summary>Gets the generation revision after the request, when present.</summary>
    public MaterializationGenerationRevision? GenerationRevision { get; }

    /// <summary>Gets exactly one outcome per requested item in request order.</summary>
    public ImmutableArray<MaterializationItemOutcome> Outcomes { get; }

    /// <summary>Compares batch results using structural ordered-outcome value semantics.</summary>
    /// <param name="other">Batch result to compare with this result.</param>
    /// <returns><see langword="true"/> when the batch envelope and every ordered outcome are equal; otherwise <see langword="false"/>.</returns>
    public bool Equals(MaterializationBatchResult? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && BatchId == other.BatchId
        && GenerationId == other.GenerationId
        && Disposition == other.Disposition
        && GenerationRevision == other.GenerationRevision
        && Outcomes.SequenceEqual(other.Outcomes);

    /// <summary>Returns a structural hash code for the batch envelope and ordered outcomes.</summary>
    /// <returns>A hash code derived from the complete semantic batch result.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(BatchId);
        hash.Add(GenerationId);
        hash.Add(Disposition);
        hash.Add(GenerationRevision);
        foreach (var outcome in Outcomes)
        {
            hash.Add(outcome);
        }
        return hash.ToHashCode();
    }
}

/// <summary>Request to seal an immutable generation at a known write revision.</summary>
public sealed record MaterializationSealGenerationRequest
{
    /// <summary>Creates a generation-seal request.</summary>
    /// <param name="sealId">Stable seal idempotency identity.</param>
    /// <param name="generationId">Generation that must still be loading.</param>
    /// <param name="expectedRevision">Exact revision after all intended writes completed.</param>
    /// <param name="workerFence">Current monotonic ownership fence in the generation scope.</param>
    /// <param name="sealedAtUtc">UTC seal-boundary time.</param>
    /// <exception cref="ArgumentException">An identity or revision is default, or <paramref name="sealedAtUtc"/> is not UTC.</exception>
    [JsonConstructor]
    public MaterializationSealGenerationRequest(
        MaterializationSealId sealId,
        MaterializationGenerationId generationId,
        MaterializationGenerationRevision expectedRevision,
        MaterializationWorkerFence workerFence,
        DateTimeOffset sealedAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(sealId.Value, nameof(sealId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        MaterializationContract.RequireDefinedIdentity(expectedRevision.Value, nameof(expectedRevision));
        MaterializationContract.RequireDefinedIdentity(workerFence.Value, nameof(workerFence));
        MaterializationContract.RequireUtc(sealedAtUtc, nameof(sealedAtUtc));
        SealId = sealId;
        GenerationId = generationId;
        ExpectedRevision = expectedRevision;
        WorkerFence = workerFence;
        SealedAtUtc = sealedAtUtc;
    }

    /// <summary>Gets the stable seal idempotency identity.</summary>
    public MaterializationSealId SealId { get; }

    /// <summary>Gets the loading generation to seal.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the exact expected revision.</summary>
    public MaterializationGenerationRevision ExpectedRevision { get; }

    /// <summary>Gets the current worker fence in the generation scope.</summary>
    public MaterializationWorkerFence WorkerFence { get; }

    /// <summary>Gets the UTC seal-boundary time.</summary>
    public DateTimeOffset SealedAtUtc { get; }
}

/// <summary>Immutable receipt proving the generation content sealed at one revision.</summary>
public sealed record MaterializationSealReceipt
{
    /// <summary>Creates an immutable seal receipt.</summary>
    /// <param name="sealId">Stable seal identity.</param>
    /// <param name="generationId">Sealed generation.</param>
    /// <param name="generationRevision">Revision committed by sealing.</param>
    /// <param name="visibleItemCount">Number of non-deleted materialized items.</param>
    /// <param name="fingerprint">Fingerprint of all immutable item and version evidence.</param>
    /// <param name="sealedAtUtc">UTC seal-boundary time.</param>
    /// <exception cref="ArgumentException">An identity or revision is default, or the timestamp is not UTC.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="visibleItemCount"/> is negative.</exception>
    [JsonConstructor]
    public MaterializationSealReceipt(
        MaterializationSealId sealId,
        MaterializationGenerationId generationId,
        MaterializationGenerationRevision generationRevision,
        long visibleItemCount,
        MaterializationSealFingerprint fingerprint,
        DateTimeOffset sealedAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(sealId.Value, nameof(sealId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        MaterializationContract.RequireDefinedIdentity(generationRevision.Value, nameof(generationRevision));
        if (visibleItemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleItemCount), visibleItemCount, "A visible-item count cannot be negative.");
        }

        MaterializationContract.RequireDefinedIdentity(fingerprint.Value, nameof(fingerprint));
        MaterializationContract.RequireUtc(sealedAtUtc, nameof(sealedAtUtc));
        SealId = sealId;
        GenerationId = generationId;
        GenerationRevision = generationRevision;
        VisibleItemCount = visibleItemCount;
        Fingerprint = fingerprint;
        SealedAtUtc = sealedAtUtc;
    }

    /// <summary>Gets the stable seal identity.</summary>
    public MaterializationSealId SealId { get; }

    /// <summary>Gets the sealed generation.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the revision committed by sealing.</summary>
    public MaterializationGenerationRevision GenerationRevision { get; }

    /// <summary>Gets the number of non-deleted materialized items.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long VisibleItemCount { get; }

    /// <summary>Gets the fingerprint of all immutable item and version evidence.</summary>
    public MaterializationSealFingerprint Fingerprint { get; }

    /// <summary>Gets the UTC seal-boundary time.</summary>
    public DateTimeOffset SealedAtUtc { get; }
}

/// <summary>Request for target-native validation of one immutable sealed generation.</summary>
public sealed record MaterializationValidateGenerationRequest
{
    /// <summary>Creates a generation-validation request.</summary>
    /// <param name="validationId">Stable validation idempotency identity.</param>
    /// <param name="generationId">Sealed generation to validate.</param>
    /// <param name="expectedRevision">Exact sealed generation revision.</param>
    /// <param name="expectedSealFingerprint">Expected immutable seal fingerprint.</param>
    /// <param name="expectedVisibleItemCount">Optional exact visible-item count asserted by the materialization engine.</param>
    /// <param name="validator">Stable identity and version of the validation interpretation.</param>
    /// <param name="workerFence">Current monotonic ownership fence of the requesting worker.</param>
    /// <param name="validatedAtUtc">UTC validation-boundary time.</param>
    /// <exception cref="ArgumentException">An identity is default, <paramref name="validator"/> contains ill-formed Unicode, the expected count is negative, or the time is not UTC.</exception>
    public MaterializationValidateGenerationRequest(
        MaterializationValidationId validationId,
        MaterializationGenerationId generationId,
        MaterializationGenerationRevision expectedRevision,
        MaterializationSealFingerprint expectedSealFingerprint,
        long? expectedVisibleItemCount,
        string validator,
        MaterializationWorkerFence workerFence,
        DateTimeOffset validatedAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(validationId.Value, nameof(validationId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        MaterializationContract.RequireDefinedIdentity(expectedRevision.Value, nameof(expectedRevision));
        MaterializationContract.RequireDefinedIdentity(expectedSealFingerprint.Value, nameof(expectedSealFingerprint));
        if (expectedVisibleItemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVisibleItemCount), "An expected item count cannot be negative.");
        }

        ValidationId = validationId;
        GenerationId = generationId;
        ExpectedRevision = expectedRevision;
        ExpectedSealFingerprint = expectedSealFingerprint;
        ExpectedVisibleItemCount = expectedVisibleItemCount;
        Validator = MaterializationContract.RequireUnicodeIdentity(validator, nameof(validator));
        MaterializationContract.RequireDefinedIdentity(workerFence.Value, nameof(workerFence));
        WorkerFence = workerFence;
        MaterializationContract.RequireUtc(validatedAtUtc, nameof(validatedAtUtc));
        ValidatedAtUtc = validatedAtUtc;
    }

    /// <summary>Gets the validation idempotency identity.</summary>
    public MaterializationValidationId ValidationId { get; }

    /// <summary>Gets the sealed generation to validate.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the exact expected sealed revision.</summary>
    public MaterializationGenerationRevision ExpectedRevision { get; }

    /// <summary>Gets the expected immutable seal fingerprint.</summary>
    public MaterializationSealFingerprint ExpectedSealFingerprint { get; }

    /// <summary>Gets the optional exact visible-item count.</summary>
    public long? ExpectedVisibleItemCount { get; }

    /// <summary>Gets the stable validation interpretation identity.</summary>
    public string Validator { get; }

    /// <summary>Gets the current monotonic ownership fence of the requesting worker.</summary>
    public MaterializationWorkerFence WorkerFence { get; }

    /// <summary>Gets the UTC validation-boundary time.</summary>
    public DateTimeOffset ValidatedAtUtc { get; }
}

/// <summary>Immutable receipt for one target-native validation result.</summary>
public sealed record MaterializationValidationReceipt
{
    /// <summary>Creates an immutable validation receipt.</summary>
    /// <param name="validationId">Stable validation identity.</param>
    /// <param name="generationId">Validated generation.</param>
    /// <param name="generationRevision">Revision committed by the validation attempt.</param>
    /// <param name="sealFingerprint">Seal fingerprint that was validated.</param>
    /// <param name="fingerprint">Fingerprint of the validation request and observed result.</param>
    /// <param name="validation">Portable validation result with deterministically ordered diagnostics.</param>
    /// <param name="validatedAtUtc">UTC validation-boundary time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or revision is default, or the timestamp is not UTC.</exception>
    [JsonConstructor]
    public MaterializationValidationReceipt(
        MaterializationValidationId validationId,
        MaterializationGenerationId generationId,
        MaterializationGenerationRevision generationRevision,
        MaterializationSealFingerprint sealFingerprint,
        MaterializationValidationFingerprint fingerprint,
        DocumentValidationResult validation,
        DateTimeOffset validatedAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(validationId.Value, nameof(validationId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        MaterializationContract.RequireDefinedIdentity(generationRevision.Value, nameof(generationRevision));
        MaterializationContract.RequireDefinedIdentity(sealFingerprint.Value, nameof(sealFingerprint));
        MaterializationContract.RequireDefinedIdentity(fingerprint.Value, nameof(fingerprint));
        Validation = MaterializationContract.NormalizeValidation(validation, nameof(validation));
        MaterializationContract.RequireUtc(validatedAtUtc, nameof(validatedAtUtc));
        ValidationId = validationId;
        GenerationId = generationId;
        GenerationRevision = generationRevision;
        SealFingerprint = sealFingerprint;
        Fingerprint = fingerprint;
        ValidatedAtUtc = validatedAtUtc;
    }

    /// <summary>Gets the stable validation identity.</summary>
    public MaterializationValidationId ValidationId { get; }

    /// <summary>Gets the validated generation.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the revision committed by the validation attempt.</summary>
    public MaterializationGenerationRevision GenerationRevision { get; }

    /// <summary>Gets the seal fingerprint that was validated.</summary>
    public MaterializationSealFingerprint SealFingerprint { get; }

    /// <summary>Gets the fingerprint of the validation request and observed result.</summary>
    public MaterializationValidationFingerprint Fingerprint { get; }

    /// <summary>Gets the portable validation result.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Gets the UTC validation-boundary time.</summary>
    public DateTimeOffset ValidatedAtUtc { get; }
}

/// <summary>Request to atomically promote one validated candidate generation.</summary>
public sealed record MaterializationPromoteGenerationRequest
{
    /// <summary>Creates a fenced compare-and-swap promotion request.</summary>
    /// <param name="promotionId">Stable promotion idempotency identity.</param>
    /// <param name="generationId">Validated candidate generation.</param>
    /// <param name="expectedGenerationRevision">Exact validated candidate revision.</param>
    /// <param name="validationFingerprint">Exact successful validation receipt fingerprint.</param>
    /// <param name="expectedActiveGenerationId">Expected active generation, or null when no generation should be active.</param>
    /// <param name="expectedTargetRevision">Exact active-pointer revision.</param>
    /// <param name="generationWorkerFence">Monotonic ownership fence in the candidate-generation scope.</param>
    /// <param name="promotionFence">Monotonic ownership fence in the independent target-pointer scope.</param>
    /// <param name="promotedAtUtc">UTC promotion-boundary time.</param>
    /// <exception cref="ArgumentException">An identity or revision is default, or the time is not UTC.</exception>
    public MaterializationPromoteGenerationRequest(
        MaterializationPromotionId promotionId,
        MaterializationGenerationId generationId,
        MaterializationGenerationRevision expectedGenerationRevision,
        MaterializationValidationFingerprint validationFingerprint,
        MaterializationGenerationId? expectedActiveGenerationId,
        MaterializationTargetRevision expectedTargetRevision,
        MaterializationWorkerFence generationWorkerFence,
        MaterializationPromotionFence promotionFence,
        DateTimeOffset promotedAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(promotionId.Value, nameof(promotionId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        MaterializationContract.RequireDefinedIdentity(expectedGenerationRevision.Value, nameof(expectedGenerationRevision));
        MaterializationContract.RequireDefinedIdentity(validationFingerprint.Value, nameof(validationFingerprint));
        if (expectedActiveGenerationId is { } active)
        {
            MaterializationContract.RequireDefinedIdentity(active.Value, nameof(expectedActiveGenerationId));
        }

        MaterializationContract.RequireDefinedIdentity(expectedTargetRevision.Value, nameof(expectedTargetRevision));
        MaterializationContract.RequireDefinedIdentity(generationWorkerFence.Value, nameof(generationWorkerFence));
        MaterializationContract.RequireDefinedIdentity(promotionFence.Value, nameof(promotionFence));
        MaterializationContract.RequireUtc(promotedAtUtc, nameof(promotedAtUtc));

        PromotionId = promotionId;
        GenerationId = generationId;
        ExpectedGenerationRevision = expectedGenerationRevision;
        ValidationFingerprint = validationFingerprint;
        ExpectedActiveGenerationId = expectedActiveGenerationId;
        ExpectedTargetRevision = expectedTargetRevision;
        GenerationWorkerFence = generationWorkerFence;
        PromotionFence = promotionFence;
        PromotedAtUtc = promotedAtUtc;
    }

    /// <summary>Gets the promotion idempotency identity.</summary>
    public MaterializationPromotionId PromotionId { get; }

    /// <summary>Gets the validated candidate generation.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the exact candidate revision.</summary>
    public MaterializationGenerationRevision ExpectedGenerationRevision { get; }

    /// <summary>Gets the exact successful validation fingerprint.</summary>
    public MaterializationValidationFingerprint ValidationFingerprint { get; }

    /// <summary>Gets the expected active generation, or null when none should be active.</summary>
    public MaterializationGenerationId? ExpectedActiveGenerationId { get; }

    /// <summary>Gets the exact expected active-pointer revision.</summary>
    public MaterializationTargetRevision ExpectedTargetRevision { get; }

    /// <summary>Gets the monotonic candidate-generation worker fence.</summary>
    public MaterializationWorkerFence GenerationWorkerFence { get; }

    /// <summary>Gets the monotonic target-pointer promotion fence.</summary>
    public MaterializationPromotionFence PromotionFence { get; }

    /// <summary>Gets the UTC promotion-boundary time.</summary>
    public DateTimeOffset PromotedAtUtc { get; }
}

/// <summary>Immutable receipt of one successful active-generation swap.</summary>
public sealed record MaterializationPromotionReceipt
{
    /// <summary>Creates an immutable promotion receipt.</summary>
    /// <param name="promotionId">Stable promotion identity.</param>
    /// <param name="targetId">Promoted target.</param>
    /// <param name="generationId">New active generation.</param>
    /// <param name="previousGenerationId">Previously active generation, when present.</param>
    /// <param name="targetRevision">New active-pointer revision.</param>
    /// <param name="generationWorkerFence">Accepted candidate-generation worker fence.</param>
    /// <param name="promotionFence">Accepted independent target-pointer fence.</param>
    /// <param name="validationFingerprint">Validation evidence authorizing the swap.</param>
    /// <param name="promotedAtUtc">UTC promotion-boundary time.</param>
    /// <exception cref="ArgumentException">An identity, revision, fence, or timestamp is invalid, or both generation identities are equal.</exception>
    [JsonConstructor]
    public MaterializationPromotionReceipt(
        MaterializationPromotionId promotionId,
        MaterializationTargetId targetId,
        MaterializationGenerationId generationId,
        MaterializationGenerationId? previousGenerationId,
        MaterializationTargetRevision targetRevision,
        MaterializationWorkerFence generationWorkerFence,
        MaterializationPromotionFence promotionFence,
        MaterializationValidationFingerprint validationFingerprint,
        DateTimeOffset promotedAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(promotionId.Value, nameof(promotionId));
        MaterializationContract.RequireDefinedIdentity(targetId.Value, nameof(targetId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        if (previousGenerationId is { } previous)
        {
            MaterializationContract.RequireDefinedIdentity(previous.Value, nameof(previousGenerationId));
            if (previous == generationId)
            {
                throw new ArgumentException("A promotion cannot displace the same generation it activates.", nameof(previousGenerationId));
            }
        }
        if (targetRevision.Ordinal <= 0)
        {
            throw new ArgumentException("A committed promotion requires a positive target revision.", nameof(targetRevision));
        }

        MaterializationContract.RequireDefinedIdentity(generationWorkerFence.Value, nameof(generationWorkerFence));
        MaterializationContract.RequireDefinedIdentity(promotionFence.Value, nameof(promotionFence));
        MaterializationContract.RequireDefinedIdentity(validationFingerprint.Value, nameof(validationFingerprint));
        MaterializationContract.RequireUtc(promotedAtUtc, nameof(promotedAtUtc));
        PromotionId = promotionId;
        TargetId = targetId;
        GenerationId = generationId;
        PreviousGenerationId = previousGenerationId;
        TargetRevision = targetRevision;
        GenerationWorkerFence = generationWorkerFence;
        PromotionFence = promotionFence;
        ValidationFingerprint = validationFingerprint;
        PromotedAtUtc = promotedAtUtc;
    }

    /// <summary>Gets the stable promotion identity.</summary>
    public MaterializationPromotionId PromotionId { get; }

    /// <summary>Gets the promoted target.</summary>
    public MaterializationTargetId TargetId { get; }

    /// <summary>Gets the new active generation.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the previously active generation, when present.</summary>
    public MaterializationGenerationId? PreviousGenerationId { get; }

    /// <summary>Gets the new active-pointer revision.</summary>
    public MaterializationTargetRevision TargetRevision { get; }

    /// <summary>Gets the accepted candidate-generation worker fence.</summary>
    public MaterializationWorkerFence GenerationWorkerFence { get; }

    /// <summary>Gets the accepted target-pointer promotion fence.</summary>
    public MaterializationPromotionFence PromotionFence { get; }

    /// <summary>Gets the validation evidence authorizing the swap.</summary>
    public MaterializationValidationFingerprint ValidationFingerprint { get; }

    /// <summary>Gets the UTC promotion-boundary time.</summary>
    public DateTimeOffset PromotedAtUtc { get; }
}

/// <summary>Request to make an inactive or abandoned generation eligible for cleanup.</summary>
public sealed record MaterializationRetireGenerationRequest
{
    /// <summary>Creates a logical generation-retirement request.</summary>
    /// <param name="retirementId">Stable retirement idempotency identity.</param>
    /// <param name="generationId">Inactive or abandoned generation to retire.</param>
    /// <param name="expectedRevision">Exact generation revision.</param>
    /// <param name="workerFence">Current monotonic ownership fence in the generation scope.</param>
    /// <param name="retiredAtUtc">UTC retirement-boundary time.</param>
    /// <exception cref="ArgumentException">An identity or revision is default, or <paramref name="retiredAtUtc"/> is not UTC.</exception>
    [JsonConstructor]
    public MaterializationRetireGenerationRequest(
        MaterializationRetirementId retirementId,
        MaterializationGenerationId generationId,
        MaterializationGenerationRevision expectedRevision,
        MaterializationWorkerFence workerFence,
        DateTimeOffset retiredAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(retirementId.Value, nameof(retirementId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        MaterializationContract.RequireDefinedIdentity(expectedRevision.Value, nameof(expectedRevision));
        MaterializationContract.RequireDefinedIdentity(workerFence.Value, nameof(workerFence));
        MaterializationContract.RequireUtc(retiredAtUtc, nameof(retiredAtUtc));
        RetirementId = retirementId;
        GenerationId = generationId;
        ExpectedRevision = expectedRevision;
        WorkerFence = workerFence;
        RetiredAtUtc = retiredAtUtc;
    }

    /// <summary>Gets the stable retirement idempotency identity.</summary>
    public MaterializationRetirementId RetirementId { get; }

    /// <summary>Gets the inactive or abandoned generation to retire.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the exact expected generation revision.</summary>
    public MaterializationGenerationRevision ExpectedRevision { get; }

    /// <summary>Gets the current worker fence in the generation scope.</summary>
    public MaterializationWorkerFence WorkerFence { get; }

    /// <summary>Gets the UTC retirement-boundary time.</summary>
    public DateTimeOffset RetiredAtUtc { get; }
}

/// <summary>Request to physically clean one already retired generation.</summary>
public sealed record MaterializationCleanupGenerationRequest
{
    /// <summary>Creates a physical generation-cleanup request.</summary>
    /// <param name="cleanupId">Stable cleanup idempotency identity.</param>
    /// <param name="generationId">Retired generation to remove.</param>
    /// <param name="expectedRevision">Exact retired generation revision.</param>
    /// <param name="workerFence">Current monotonic ownership fence in the generation scope.</param>
    /// <param name="cleanedAtUtc">UTC cleanup-boundary time.</param>
    /// <exception cref="ArgumentException">An identity or revision is default, or <paramref name="cleanedAtUtc"/> is not UTC.</exception>
    [JsonConstructor]
    public MaterializationCleanupGenerationRequest(
        MaterializationCleanupId cleanupId,
        MaterializationGenerationId generationId,
        MaterializationGenerationRevision expectedRevision,
        MaterializationWorkerFence workerFence,
        DateTimeOffset cleanedAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(cleanupId.Value, nameof(cleanupId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        MaterializationContract.RequireDefinedIdentity(expectedRevision.Value, nameof(expectedRevision));
        MaterializationContract.RequireDefinedIdentity(workerFence.Value, nameof(workerFence));
        MaterializationContract.RequireUtc(cleanedAtUtc, nameof(cleanedAtUtc));
        CleanupId = cleanupId;
        GenerationId = generationId;
        ExpectedRevision = expectedRevision;
        WorkerFence = workerFence;
        CleanedAtUtc = cleanedAtUtc;
    }

    /// <summary>Gets the stable cleanup idempotency identity.</summary>
    public MaterializationCleanupId CleanupId { get; }

    /// <summary>Gets the retired generation to physically remove.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the exact expected retired-generation revision.</summary>
    public MaterializationGenerationRevision ExpectedRevision { get; }

    /// <summary>Gets the current worker fence in the generation scope.</summary>
    public MaterializationWorkerFence WorkerFence { get; }

    /// <summary>Gets the UTC cleanup-boundary time.</summary>
    public DateTimeOffset CleanedAtUtc { get; }
}

/// <summary>Bounded immutable metadata for one isolated generation.</summary>
public sealed record MaterializationGenerationSnapshot
{
    /// <summary>Creates generation metadata without enumerating retained items.</summary>
    /// <param name="materializationId">Stable logical materialization identity.</param>
    /// <param name="generationId">Stable generation identity.</param>
    /// <param name="definitionFingerprint">Fingerprint of the exact canonical materialization definition.</param>
    /// <param name="state">Current generation lifecycle state.</param>
    /// <param name="revision">Current generation revision.</param>
    /// <param name="latestWorkerFence">Greatest accepted fence in this generation's independent ownership scope.</param>
    /// <param name="hasPermanentFailures">Whether unresolved permanent item-mutation failures are retained.</param>
    /// <param name="pendingRetryableMutationCount">Number of unresolved retryable mutation identities.</param>
    /// <param name="visibleItemCount">Current number of retained non-delete items.</param>
    /// <param name="tombstoneCount">Current number of retained delete tombstones.</param>
    /// <param name="sealReceipt">Immutable seal evidence, when sealed.</param>
    /// <param name="validationReceipt">Latest validation evidence, when validation was attempted.</param>
    /// <param name="createdAtUtc">UTC creation time.</param>
    /// <param name="inactivatedAtUtc">UTC active-pointer displacement time, when the generation was displaced.</param>
    /// <param name="retiredAtUtc">UTC retirement time, when retired.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definitionFingerprint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Identity, lifecycle evidence, counts, revisions, fences, or timestamps are contradictory.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An item count is negative or their sum overflows.</exception>
    [JsonConstructor]
    public MaterializationGenerationSnapshot(
        MaterializationId materializationId,
        MaterializationGenerationId generationId,
        ExecutionDefinitionFingerprint definitionFingerprint,
        MaterializationGenerationState state,
        MaterializationGenerationRevision revision,
        MaterializationWorkerFence latestWorkerFence,
        bool hasPermanentFailures,
        long pendingRetryableMutationCount,
        long visibleItemCount,
        long tombstoneCount,
        MaterializationSealReceipt? sealReceipt,
        MaterializationValidationReceipt? validationReceipt,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? inactivatedAtUtc,
        DateTimeOffset? retiredAtUtc)
    {
        MaterializationContract.RequireDefinedIdentity(materializationId.Value, nameof(materializationId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        DefinitionFingerprint = definitionFingerprint ?? throw new ArgumentNullException(nameof(definitionFingerprint));
        MaterializationContract.RequireDefinedIdentity(definitionFingerprint.Algorithm, nameof(definitionFingerprint));
        MaterializationContract.RequireDefinedIdentity(definitionFingerprint.Canonicalization, nameof(definitionFingerprint));
        MaterializationContract.RequireDefinedIdentity(definitionFingerprint.Value, nameof(definitionFingerprint));
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported generation state.");
        }

        MaterializationContract.RequireDefinedIdentity(revision.Value, nameof(revision));
        MaterializationContract.RequireDefinedIdentity(latestWorkerFence.Value, nameof(latestWorkerFence));
        if (pendingRetryableMutationCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pendingRetryableMutationCount), pendingRetryableMutationCount, "A pending retryable-mutation count cannot be negative.");
        }

        if (visibleItemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleItemCount), visibleItemCount, "A visible-item count cannot be negative.");
        }

        if (tombstoneCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tombstoneCount), tombstoneCount, "A tombstone count cannot be negative.");
        }

        _ = checked(visibleItemCount + tombstoneCount);
        MaterializationContract.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        if (inactivatedAtUtc is { } inactivated)
        {
            MaterializationContract.RequireUtc(inactivated, nameof(inactivatedAtUtc));
            if (inactivated < createdAtUtc)
            {
                throw new ArgumentException("A generation displacement cannot predate its creation.", nameof(inactivatedAtUtc));
            }
        }

        if (state == MaterializationGenerationState.Inactive && inactivatedAtUtc is null)
        {
            throw new ArgumentException("An inactive generation requires its active-pointer displacement time.", nameof(inactivatedAtUtc));
        }

        if (inactivatedAtUtc is not null
            && state is not (MaterializationGenerationState.Inactive or MaterializationGenerationState.Retired))
        {
            throw new ArgumentException(
                "Only an inactive or retired generation may retain an active-pointer displacement time.",
                nameof(inactivatedAtUtc));
        }

        if (retiredAtUtc is { } retired)
        {
            MaterializationContract.RequireUtc(retired, nameof(retiredAtUtc));
            if (retired < createdAtUtc)
            {
                throw new ArgumentException("A generation retirement cannot predate its creation.", nameof(retiredAtUtc));
            }
        }
        if (state == MaterializationGenerationState.Retired != (retiredAtUtc is not null))
        {
            throw new ArgumentException("Only a retired generation may carry a retirement time, and every retired generation requires one.", nameof(retiredAtUtc));
        }

        if (state == MaterializationGenerationState.Validated
            && (hasPermanentFailures || pendingRetryableMutationCount != 0))
        {
            throw new ArgumentException(
                "A validated candidate cannot retain failed or unresolved item mutations.",
                nameof(state));
        }
        var latestPreDisplacementEvidenceAtUtc = validationReceipt?.ValidatedAtUtc
            ?? sealReceipt?.SealedAtUtc
            ?? createdAtUtc;
        if (inactivatedAtUtc is { } displacement && displacement < latestPreDisplacementEvidenceAtUtc)
        {
            throw new ArgumentException(
                "A generation displacement cannot predate its latest retained lifecycle evidence.",
                nameof(inactivatedAtUtc));
        }

        var latestLifecycleEvidenceAtUtc = inactivatedAtUtc ?? latestPreDisplacementEvidenceAtUtc;
        if (retiredAtUtc is { } retirement && retirement < latestLifecycleEvidenceAtUtc)
        {
            throw new ArgumentException(
                "A generation retirement cannot predate its latest retained lifecycle evidence.",
                nameof(retiredAtUtc));
        }

        if (inactivatedAtUtc is not null
            && (sealReceipt is null || validationReceipt is not { Validation.IsValid: true }))
        {
            throw new ArgumentException(
                "A displaced generation requires the successful seal and validation evidence that authorized activation.",
                nameof(inactivatedAtUtc));
        }

        ValidateLifecycleEvidence(
            generationId,
            state,
            revision,
            visibleItemCount,
            sealReceipt,
            validationReceipt,
            createdAtUtc);

        MaterializationId = materializationId;
        GenerationId = generationId;
        State = state;
        Revision = revision;
        LatestWorkerFence = latestWorkerFence;
        HasPermanentFailures = hasPermanentFailures;
        PendingRetryableMutationCount = pendingRetryableMutationCount;
        VisibleItemCount = visibleItemCount;
        TombstoneCount = tombstoneCount;
        SealReceipt = sealReceipt;
        ValidationReceipt = validationReceipt;
        CreatedAtUtc = createdAtUtc;
        InactivatedAtUtc = inactivatedAtUtc;
        RetiredAtUtc = retiredAtUtc;
    }

    /// <summary>Gets the stable logical materialization identity.</summary>
    public MaterializationId MaterializationId { get; }

    /// <summary>Gets the stable generation identity.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Gets the exact canonical materialization-definition fingerprint.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Gets the current lifecycle state.</summary>
    public MaterializationGenerationState State { get; }

    /// <summary>Gets the current generation revision.</summary>
    public MaterializationGenerationRevision Revision { get; }

    /// <summary>Gets the greatest accepted fence in this generation's independent ownership scope.</summary>
    public MaterializationWorkerFence LatestWorkerFence { get; }

    /// <summary>Gets whether retained permanent item-mutation failures remain unresolved.</summary>
    public bool HasPermanentFailures { get; }

    /// <summary>Gets the number of exact mutation identities with unresolved retryable outcomes.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long PendingRetryableMutationCount { get; }

    /// <summary>Gets the current number of retained non-delete items.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long VisibleItemCount { get; }

    /// <summary>Gets the current number of retained delete tombstones.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long TombstoneCount { get; }

    /// <summary>Gets the current number of retained item identities, including tombstones.</summary>
    [JsonIgnore]
    public long RetainedItemCount => checked(VisibleItemCount + TombstoneCount);

    /// <summary>Gets immutable seal evidence, when the generation was sealed.</summary>
    public MaterializationSealReceipt? SealReceipt { get; }

    /// <summary>Gets latest validation evidence, when validation was attempted.</summary>
    public MaterializationValidationReceipt? ValidationReceipt { get; }

    /// <summary>Gets the UTC creation time.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Gets the UTC active-pointer displacement time, when the generation was made inactive.</summary>
    public DateTimeOffset? InactivatedAtUtc { get; }

    /// <summary>Gets the UTC retirement time, when retired.</summary>
    public DateTimeOffset? RetiredAtUtc { get; }

    static void ValidateLifecycleEvidence(
        MaterializationGenerationId generationId,
        MaterializationGenerationState state,
        MaterializationGenerationRevision revision,
        long visibleItemCount,
        MaterializationSealReceipt? sealReceipt,
        MaterializationValidationReceipt? validationReceipt,
        DateTimeOffset createdAtUtc)
    {
        if (sealReceipt is not null)
        {
            if (sealReceipt.GenerationId != generationId || sealReceipt.GenerationRevision.Ordinal > revision.Ordinal)
            {
                throw new ArgumentException("Seal evidence must belong to this generation at or before its current revision.", nameof(sealReceipt));
            }

            if (sealReceipt.SealedAtUtc < createdAtUtc)
            {
                throw new ArgumentException("A seal cannot predate generation creation.", nameof(sealReceipt));
            }
        }
        if (validationReceipt is not null)
        {
            if (sealReceipt is null
                || validationReceipt.GenerationId != generationId
                || validationReceipt.GenerationRevision.Ordinal > revision.Ordinal
                || validationReceipt.GenerationRevision.Ordinal <= sealReceipt.GenerationRevision.Ordinal
                || validationReceipt.SealFingerprint != sealReceipt.Fingerprint)
            {
                throw new ArgumentException("Validation evidence must refer to this generation and its retained seal.", nameof(validationReceipt));
            }
            if (validationReceipt.ValidatedAtUtc < sealReceipt.SealedAtUtc)
            {
                throw new ArgumentException("Validation cannot predate its retained seal.", nameof(validationReceipt));
            }
        }

        switch (state)
        {
            case MaterializationGenerationState.Loading when sealReceipt is not null || validationReceipt is not null:
                throw new ArgumentException("A loading generation cannot carry seal or validation evidence.", nameof(state));
            case MaterializationGenerationState.Sealed when sealReceipt is null:
                throw new ArgumentException("A sealed generation requires seal evidence.", nameof(state));
            case MaterializationGenerationState.Sealed when validationReceipt is { Validation.IsValid: true }:
                throw new ArgumentException("A successfully validated generation must use the validated state.", nameof(state));
            case MaterializationGenerationState.Validated
                when sealReceipt is null || validationReceipt is not { Validation.IsValid: true }:
                throw new ArgumentException("A validated generation requires successful validation evidence.", nameof(state));
            case MaterializationGenerationState.Active or MaterializationGenerationState.Inactive
                when sealReceipt is null || validationReceipt is not { Validation.IsValid: true }:
                throw new ArgumentException("An active or inactive generation requires successful promotion evidence.", nameof(state));
        }

        if (state is MaterializationGenerationState.Sealed or MaterializationGenerationState.Validated
            && sealReceipt!.VisibleItemCount != visibleItemCount)
        {
            throw new ArgumentException("An immutable generation's visible count must match its seal receipt.", nameof(visibleItemCount));
        }
    }
}

/// <summary>Bounded immutable snapshot of target pointer state.</summary>
public sealed record MaterializationTargetSnapshot
{
    /// <summary>Creates target pointer metadata without enumerating retained generations or items.</summary>
    /// <param name="targetId">Stable target identity.</param>
    /// <param name="materializationId">Logical materialization bound to the target.</param>
    /// <param name="revision">Current active-pointer revision.</param>
    /// <param name="activeGenerationId">Single active generation, when present.</param>
    /// <param name="latestPromotionFence">Greatest accepted fence in the independent promotion-pointer scope.</param>
    /// <param name="retainedGenerationCount">Number of generation identities whose physical state is retained.</param>
    /// <exception cref="ArgumentException">Identity, pointer revision, active identity, fence, or count is contradictory.</exception>
    [JsonConstructor]
    public MaterializationTargetSnapshot(
        MaterializationTargetId targetId,
        MaterializationId materializationId,
        MaterializationTargetRevision revision,
        MaterializationGenerationId? activeGenerationId,
        MaterializationPromotionFence? latestPromotionFence,
        long retainedGenerationCount)
    {
        MaterializationContract.RequireDefinedIdentity(targetId.Value, nameof(targetId));
        MaterializationContract.RequireDefinedIdentity(materializationId.Value, nameof(materializationId));
        MaterializationContract.RequireDefinedIdentity(revision.Value, nameof(revision));
        if (activeGenerationId is { } active)
        {
            MaterializationContract.RequireDefinedIdentity(active.Value, nameof(activeGenerationId));
        }

        if (latestPromotionFence is { } fence)
        {
            MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(latestPromotionFence));
        }

        if (retainedGenerationCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedGenerationCount), retainedGenerationCount, "A retained-generation count cannot be negative.");
        }

        if (activeGenerationId is null && revision.Ordinal != 0)
        {
            throw new ArgumentException("A target without an active generation must remain at the initial pointer revision.", nameof(activeGenerationId));
        }

        if (activeGenerationId is not null && (revision.Ordinal == 0 || latestPromotionFence is null || retainedGenerationCount == 0))
        {
            throw new ArgumentException("An active generation requires a positive pointer revision, promotion fence, and retained generation.", nameof(activeGenerationId));
        }

        TargetId = targetId;
        MaterializationId = materializationId;
        Revision = revision;
        ActiveGenerationId = activeGenerationId;
        LatestPromotionFence = latestPromotionFence;
        RetainedGenerationCount = retainedGenerationCount;
    }

    /// <summary>Gets the stable target identity.</summary>
    public MaterializationTargetId TargetId { get; }

    /// <summary>Gets the logical materialization bound to the target.</summary>
    public MaterializationId MaterializationId { get; }

    /// <summary>Gets the current active-pointer revision.</summary>
    public MaterializationTargetRevision Revision { get; }

    /// <summary>Gets the single active generation, when present.</summary>
    public MaterializationGenerationId? ActiveGenerationId { get; }

    /// <summary>Gets the greatest accepted fence in the independent promotion-pointer scope.</summary>
    public MaterializationPromotionFence? LatestPromotionFence { get; }

    /// <summary>Gets the number of generation identities whose physical state is retained.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long RetainedGenerationCount { get; }
}

/// <summary>Result of one generation lifecycle operation.</summary>
public sealed record MaterializationGenerationOperationResult
{
    /// <summary>Creates a generation lifecycle result.</summary>
    /// <param name="disposition">Observable operation disposition.</param>
    /// <param name="generation">Current generation metadata, or bounded historical metadata for an exact replay after cleanup.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">An applied or replayed result does not carry generation metadata.</exception>
    [JsonConstructor]
    public MaterializationGenerationOperationResult(
        MaterializationTargetOperationDisposition disposition,
        MaterializationGenerationSnapshot? generation)
    {
        RequireDisposition(disposition);
        if (disposition is MaterializationTargetOperationDisposition.Applied or MaterializationTargetOperationDisposition.Replayed
            && generation is null)
        {
            throw new ArgumentException("An applied or replayed lifecycle result requires generation metadata.", nameof(generation));
        }
        Disposition = disposition;
        Generation = generation;
    }

    /// <summary>Gets the observable operation disposition.</summary>
    public MaterializationTargetOperationDisposition Disposition { get; }

    /// <summary>Gets current generation metadata, or bounded historical metadata for an exact replay after cleanup.</summary>
    public MaterializationGenerationSnapshot? Generation { get; }

    internal static void RequireDisposition(MaterializationTargetOperationDisposition disposition)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported target operation disposition.");
        }
    }
}

/// <summary>Result of one generation seal operation.</summary>
public sealed record MaterializationSealResult
{
    /// <summary>Creates a generation seal result.</summary>
    /// <param name="disposition">Observable seal disposition.</param>
    /// <param name="generation">Current generation metadata, or bounded historical metadata for an exact replay after cleanup.</param>
    /// <param name="receipt">Immutable seal receipt for applied or replayed outcomes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Receipt presence or identity contradicts the disposition or generation.</exception>
    [JsonConstructor]
    public MaterializationSealResult(
        MaterializationTargetOperationDisposition disposition,
        MaterializationGenerationSnapshot? generation,
        MaterializationSealReceipt? receipt)
    {
        MaterializationGenerationOperationResult.RequireDisposition(disposition);
        var succeeded = disposition is MaterializationTargetOperationDisposition.Applied or MaterializationTargetOperationDisposition.Replayed;
        if (succeeded != (generation is not null && receipt is not null))
        {
            throw new ArgumentException("Only applied or replayed seal results require both generation metadata and a receipt.", nameof(receipt));
        }

        if (receipt is not null
            && (receipt.GenerationId != generation!.GenerationId || generation.SealReceipt != receipt))
        {
            throw new ArgumentException("A seal receipt must be the exact retained seal of the returned generation.", nameof(receipt));
        }

        if (disposition == MaterializationTargetOperationDisposition.Applied
            && (generation!.State != MaterializationGenerationState.Sealed
                || generation.Revision != receipt!.GenerationRevision))
        {
            throw new ArgumentException(
                "An applied seal result requires the exact newly sealed generation revision.",
                nameof(generation));
        }

        Disposition = disposition;
        Generation = generation;
        Receipt = receipt;
    }

    /// <summary>Gets the observable seal disposition.</summary>
    public MaterializationTargetOperationDisposition Disposition { get; }

    /// <summary>Gets current generation metadata, or bounded historical metadata for an exact replay after cleanup.</summary>
    public MaterializationGenerationSnapshot? Generation { get; }

    /// <summary>Gets immutable seal evidence for an applied or replayed result.</summary>
    public MaterializationSealReceipt? Receipt { get; }
}

/// <summary>Result of one generation validation operation.</summary>
public sealed record MaterializationValidationResult
{
    /// <summary>Creates a generation validation result.</summary>
    /// <param name="disposition">Observable validation-operation disposition.</param>
    /// <param name="generation">Current generation metadata, or bounded historical metadata for an exact replay after cleanup.</param>
    /// <param name="receipt">Validation receipt for applied, failed, or replayed outcomes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Receipt presence, identity, or validity contradicts the disposition.</exception>
    [JsonConstructor]
    public MaterializationValidationResult(
        MaterializationTargetOperationDisposition disposition,
        MaterializationGenerationSnapshot? generation,
        MaterializationValidationReceipt? receipt)
    {
        MaterializationGenerationOperationResult.RequireDisposition(disposition);
        var evaluated = disposition is MaterializationTargetOperationDisposition.Applied
            or MaterializationTargetOperationDisposition.Replayed
            or MaterializationTargetOperationDisposition.ValidationFailed;
        if (evaluated != (generation is not null && receipt is not null))
        {
            throw new ArgumentException("An evaluated validation result requires both generation metadata and a receipt.", nameof(receipt));
        }

        if (receipt is not null
            && (receipt.GenerationId != generation!.GenerationId
                || generation.SealReceipt?.Fingerprint != receipt.SealFingerprint
                || generation.Revision.Ordinal < receipt.GenerationRevision.Ordinal
                || disposition != MaterializationTargetOperationDisposition.Replayed && generation.ValidationReceipt != receipt
                || disposition == MaterializationTargetOperationDisposition.Replayed
                    && generation.Revision == receipt.GenerationRevision
                    && generation.ValidationReceipt != receipt))
        {
            throw new ArgumentException(
                "Validation evidence must correlate with the returned generation, seal, revision, and retained evaluation.",
                nameof(receipt));
        }

        if (disposition == MaterializationTargetOperationDisposition.Applied && receipt is not { Validation.IsValid: true })
        {
            throw new ArgumentException("An applied validation result requires successful validation evidence.", nameof(receipt));
        }

        if (disposition == MaterializationTargetOperationDisposition.ValidationFailed && receipt is not { Validation.IsValid: false })
        {
            throw new ArgumentException("A failed validation result requires unsuccessful validation evidence.", nameof(receipt));
        }

        if (disposition == MaterializationTargetOperationDisposition.Applied
            && (generation!.State != MaterializationGenerationState.Validated
                || generation.Revision != receipt!.GenerationRevision))
        {
            throw new ArgumentException(
                "An applied validation result requires the exact newly validated generation revision.",
                nameof(generation));
        }

        if (disposition == MaterializationTargetOperationDisposition.ValidationFailed
            && (generation!.State != MaterializationGenerationState.Sealed
                || generation.Revision != receipt!.GenerationRevision))
        {
            throw new ArgumentException(
                "A failed validation result requires the exact evaluated sealed generation revision.",
                nameof(generation));
        }

        Disposition = disposition;
        Generation = generation;
        Receipt = receipt;
    }

    /// <summary>Gets the observable validation-operation disposition.</summary>
    public MaterializationTargetOperationDisposition Disposition { get; }

    /// <summary>Gets current generation metadata, or bounded historical metadata for an exact replay after cleanup.</summary>
    public MaterializationGenerationSnapshot? Generation { get; }

    /// <summary>Gets validation evidence for an evaluated result.</summary>
    public MaterializationValidationReceipt? Receipt { get; }
}

/// <summary>Result of one fenced active-generation promotion.</summary>
public sealed record MaterializationPromotionResult
{
    /// <summary>Creates a promotion result.</summary>
    /// <param name="disposition">Observable promotion disposition.</param>
    /// <param name="snapshot">Current bounded target pointer snapshot.</param>
    /// <param name="receipt">Promotion receipt for applied or replayed outcomes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">Receipt presence or target/pointer evidence contradicts the result.</exception>
    [JsonConstructor]
    public MaterializationPromotionResult(
        MaterializationTargetOperationDisposition disposition,
        MaterializationTargetSnapshot snapshot,
        MaterializationPromotionReceipt? receipt)
    {
        MaterializationGenerationOperationResult.RequireDisposition(disposition);
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        var succeeded = disposition is MaterializationTargetOperationDisposition.Applied or MaterializationTargetOperationDisposition.Replayed;
        if (succeeded != (receipt is not null))
        {
            throw new ArgumentException("Only applied or replayed promotions carry a receipt.", nameof(receipt));
        }

        if (receipt is not null && receipt.TargetId != snapshot.TargetId)
        {
            throw new ArgumentException("Promotion evidence must belong to the returned target.", nameof(receipt));
        }

        if (receipt is not null
            && (snapshot.Revision.Ordinal < receipt.TargetRevision.Ordinal
                || snapshot.LatestPromotionFence is not { } latestFence
                || latestFence.Ordinal < receipt.PromotionFence.Ordinal))
        {
            throw new ArgumentException(
                "Promotion evidence cannot be newer than the returned target revision or pointer fence.",
                nameof(receipt));
        }

        if (disposition == MaterializationTargetOperationDisposition.Applied
            && (snapshot.ActiveGenerationId != receipt!.GenerationId
                || snapshot.Revision != receipt.TargetRevision
                || snapshot.LatestPromotionFence != receipt.PromotionFence))
        {
            throw new ArgumentException("An applied promotion receipt must exactly describe the returned active pointer.", nameof(receipt));
        }

        if (disposition == MaterializationTargetOperationDisposition.Replayed
            && snapshot.Revision == receipt!.TargetRevision
            && snapshot.ActiveGenerationId != receipt.GenerationId)
        {
            throw new ArgumentException(
                "A replay at the receipt's target revision must retain the promoted generation as active.",
                nameof(receipt));
        }
        Disposition = disposition;
        Receipt = receipt;
    }

    /// <summary>Gets the observable promotion disposition.</summary>
    public MaterializationTargetOperationDisposition Disposition { get; }

    /// <summary>Gets the current bounded target pointer snapshot.</summary>
    public MaterializationTargetSnapshot Snapshot { get; }

    /// <summary>Gets promotion evidence for an applied or replayed result.</summary>
    public MaterializationPromotionReceipt? Receipt { get; }
}

/// <summary>Result of one physical generation cleanup.</summary>
public sealed record MaterializationCleanupResult
{
    /// <summary>Creates a physical-cleanup result.</summary>
    /// <param name="disposition">Observable cleanup disposition.</param>
    /// <param name="snapshot">Current bounded target pointer snapshot.</param>
    /// <param name="wasRemoved">Whether this invocation physically removed retained generation state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException"><paramref name="wasRemoved"/> contradicts the disposition.</exception>
    [JsonConstructor]
    public MaterializationCleanupResult(
        MaterializationTargetOperationDisposition disposition,
        MaterializationTargetSnapshot snapshot,
        bool wasRemoved)
    {
        MaterializationGenerationOperationResult.RequireDisposition(disposition);
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        if (wasRemoved != (disposition == MaterializationTargetOperationDisposition.Applied))
        {
            throw new ArgumentException("Exactly an applied cleanup reports physical removal.", nameof(wasRemoved));
        }

        Disposition = disposition;
        WasRemoved = wasRemoved;
    }

    /// <summary>Gets the observable cleanup disposition.</summary>
    public MaterializationTargetOperationDisposition Disposition { get; }

    /// <summary>Gets the current bounded target pointer snapshot.</summary>
    public MaterializationTargetSnapshot Snapshot { get; }

    /// <summary>Gets whether this invocation physically removed retained generation state.</summary>
    public bool WasRemoved { get; }
}

/// <summary>Provider-neutral target port for isolated, validated, fenced materialization generations.</summary>
public interface IMaterializationTarget
{
    /// <summary>Gets the target identity and complete advertised capability evidence.</summary>
    MaterializationTargetDescriptor Descriptor { get; }

    /// <summary>Reads one deterministic immutable target snapshot.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <returns>The current target snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    ValueTask<MaterializationTargetSnapshot> InspectAsync(OperationContext context);

    /// <summary>Reads bounded metadata for one retained generation without enumerating its items.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="generationId">Generation identity to inspect.</param>
    /// <returns>Current generation metadata, or null when physical generation state is not retained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="generationId"/> is default.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    ValueTask<MaterializationGenerationSnapshot?> InspectGenerationAsync(
        OperationContext context,
        MaterializationGenerationId generationId);

    /// <summary>Begins a new empty isolated caller-identified generation.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Complete begin-generation intent.</param>
    /// <returns>The observable begin result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    ValueTask<MaterializationGenerationOperationResult> BeginGenerationAsync(
        OperationContext context,
        MaterializationBeginGenerationRequest request);

    /// <summary>Applies one bounded upsert/delete union with exactly one keyed outcome per request item.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Bounded batch intent.</param>
    /// <returns>Complete request-order per-item outcomes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    ValueTask<MaterializationBatchResult> ApplyBatchAsync(
        OperationContext context,
        MaterializationApplyBatchRequest request);

    /// <summary>Atomically seals a loading generation, making its content immutable.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Fenced seal intent.</param>
    /// <returns>The seal result and immutable receipt when successful.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    ValueTask<MaterializationSealResult> SealGenerationAsync(
        OperationContext context,
        MaterializationSealGenerationRequest request);

    /// <summary>Validates a sealed generation and records immutable diagnostics and evidence.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Complete target-native validation intent.</param>
    /// <returns>The validation result and receipt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    ValueTask<MaterializationValidationResult> ValidateGenerationAsync(
        OperationContext context,
        MaterializationValidateGenerationRequest request);

    /// <summary>Atomically promotes a validated generation by compare-and-swap under a worker fence.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Complete fenced promotion intent.</param>
    /// <returns>The promotion result and current target snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    ValueTask<MaterializationPromotionResult> PromoteGenerationAsync(
        OperationContext context,
        MaterializationPromoteGenerationRequest request);

    /// <summary>Logically retires an inactive generation without physically deleting it.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Complete retirement intent.</param>
    /// <returns>The observable retirement result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    ValueTask<MaterializationGenerationOperationResult> RetireGenerationAsync(
        OperationContext context,
        MaterializationRetireGenerationRequest request);

    /// <summary>Physically removes one retired generation without ever deleting the active generation.</summary>
    /// <param name="context">Operation context carrying cancellation and trace metadata.</param>
    /// <param name="request">Complete cleanup intent.</param>
    /// <returns>The cleanup result and current target snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation cancellation token was canceled.</exception>
    ValueTask<MaterializationCleanupResult> CleanupGenerationAsync(
        OperationContext context,
        MaterializationCleanupGenerationRequest request);
}
