using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostic codes emitted by materialization progress stores.</summary>
public static class MaterializationProgressDiagnosticCodes
{
    /// <summary>No progress aggregate exists for the requested key.</summary>
    public const string NotFound = "materialization.progress.notFound";

    /// <summary>The expected compare-and-swap revision is stale.</summary>
    public const string RevisionConflict = "materialization.progress.revisionConflict";

    /// <summary>The supplied owner or worker fence has been superseded.</summary>
    public const string StaleFence = "materialization.progress.staleFence";

    /// <summary>A stable mutation or checkpoint identity was reused for different content.</summary>
    public const string IdentityConflict = "materialization.progress.identityConflict";

    /// <summary>A settlement cites a checkpoint that has not been durably persisted.</summary>
    public const string CheckpointNotFound = "materialization.progress.checkpointNotFound";

    /// <summary>A settlement position differs from the position proven by its cited checkpoint.</summary>
    public const string CheckpointMismatch = "materialization.progress.checkpointMismatch";
}

/// <summary>Stable idempotency identity of one materialization-progress mutation.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationProgressMutationId
{
    /// <summary>Creates a progress-mutation identity.</summary>
    /// <param name="value">Stable identity reused only for an exact retry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationProgressMutationId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable mutation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable mutation identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one durable application checkpoint.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationCheckpointId
{
    /// <summary>Creates an application-checkpoint identity.</summary>
    /// <param name="value">Stable write-once checkpoint identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationCheckpointId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable checkpoint identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable checkpoint identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one explicit source settlement.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationSettlementId
{
    /// <summary>Creates a source-settlement identity.</summary>
    /// <param name="value">Stable write-once settlement identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationSettlementId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable settlement identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable settlement identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Monotonic compare-and-swap revision of one progress aggregate.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationProgressRevision
{
    /// <summary>First persisted revision.</summary>
    public static MaterializationProgressRevision Initial { get; } = new("1");

    /// <summary>Creates a positive canonical progress revision.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a canonical positive integer.</exception>
    [JsonConstructor]
    public MaterializationProgressRevision(string value)
    {
        Value = MaterializationContract.RequireOrdinal(value, nameof(value), allowZero: false, out var ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Canonical positive revision value.</summary>
    public string Value { get; }

    /// <summary>Positive numeric revision used for compare-and-swap progression.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <summary>Returns the canonical revision value.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;

    internal MaterializationProgressRevision Next() =>
        new(checked(Ordinal + 1).ToString(CultureInfo.InvariantCulture));
}

/// <summary>Monotonic ownership fence of one materialization progress worker.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationProgressFence
{
    /// <summary>First acquired worker fence.</summary>
    public static MaterializationProgressFence Initial { get; } = new("1");

    /// <summary>Creates a positive canonical worker fence.</summary>
    /// <param name="value">Canonical invariant-culture positive 64-bit integer string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a canonical positive integer.</exception>
    [JsonConstructor]
    public MaterializationProgressFence(string value)
    {
        Value = MaterializationContract.RequireOrdinal(value, nameof(value), allowZero: false, out var ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Canonical positive fence value.</summary>
    public string Value { get; }

    /// <summary>Positive numeric fence used to reject superseded workers.</summary>
    [JsonIgnore]
    public long Ordinal { get; }

    /// <summary>Returns the canonical fence value.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;

    internal MaterializationProgressFence Next() =>
        new(checked(Ordinal + 1).ToString(CultureInfo.InvariantCulture));
}

/// <summary>Exact durable-progress aggregate for one materialization generation and source-feed scope.</summary>
public sealed record MaterializationProgressKey
{
    /// <summary>Creates a durable-progress key.</summary>
    /// <param name="materialization">Logical materialization definition.</param>
    /// <param name="definitionFingerprint">Exact canonical execution-definition content being applied.</param>
    /// <param name="generation">Candidate or active target generation receiving the source work.</param>
    /// <param name="scope">Exact Relations acquisition input, physical source, partition, and ordering scope.</param>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    [JsonConstructor]
    public MaterializationProgressKey(
        MaterializationId materialization,
        ExecutionDefinitionFingerprint definitionFingerprint,
        MaterializationGenerationId generation,
        MaterializationSourceScope scope)
    {
        MaterializationContract.RequireDefinedIdentity(materialization.Value, nameof(materialization));
        DefinitionFingerprint = Guard.RequireNotNull(definitionFingerprint);
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        Scope = Guard.RequireNotNull(scope);
        Materialization = materialization;
        Generation = generation;
    }

    /// <summary>Logical materialization definition.</summary>
    public MaterializationId Materialization { get; }

    /// <summary>Exact canonical execution-definition content being applied.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Candidate or active target generation receiving the source work.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Exact Relations acquisition input, physical source, partition, and ordering scope.</summary>
    public MaterializationSourceScope Scope { get; }
}

/// <summary>Semantic cursor represented by an application checkpoint.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationCheckpointKind
{
    /// <summary>A batch partition has more data and persists its next-page continuation.</summary>
    BatchContinuation = 0,

    /// <summary>A batch partition was authoritatively exhausted.</summary>
    BatchCompleted = 1,

    /// <summary>Incremental effects through one raw source position were durably applied.</summary>
    ChangePosition = 2
}

/// <summary>
/// Durable application progress, intentionally distinct from both a raw source position and source settlement.
/// </summary>
public sealed record MaterializationApplicationCheckpoint
{
    /// <summary>Creates an application checkpoint.</summary>
    /// <param name="id">Stable write-once checkpoint identity.</param>
    /// <param name="kind">Batch-continuation, batch-completed, or change-position semantics.</param>
    /// <param name="continuation">Next batch page for <see cref="MaterializationCheckpointKind.BatchContinuation"/>.</param>
    /// <param name="completion">Exact authoritative read completion for <see cref="MaterializationCheckpointKind.BatchCompleted"/>.</param>
    /// <param name="position">Applied raw source position for <see cref="MaterializationCheckpointKind.ChangePosition"/>.</param>
    /// <param name="appliedDeliveries">Delivery identities whose effects are covered by a change checkpoint.</param>
    /// <param name="committedAtUtc">UTC durable application-commit time.</param>
    /// <param name="evidenceReference">Optional opaque application evidence reference.</param>
    /// <exception cref="ArgumentException">
    /// An identity, cursor, delivery set, or timestamp is invalid, or cursor fields conflict with
    /// <paramref name="kind"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationApplicationCheckpoint(
        MaterializationCheckpointId id,
        MaterializationCheckpointKind kind,
        MaterializationSourceContinuation? continuation,
        MaterializationSourceReadCompletion? completion,
        MaterializationSourcePosition? position,
        ImmutableArray<MaterializationDeliveryId> appliedDeliveries,
        DateTimeOffset committedAtUtc,
        string? evidenceReference = null)
    {
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported application-checkpoint kind.");
        }

        ValidateCursor(kind, continuation, completion, position, appliedDeliveries);
        MaterializationContract.RequireUtc(committedAtUtc, nameof(committedAtUtc));
        if (evidenceReference is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        }

        Id = id;
        Kind = kind;
        Continuation = continuation;
        Completion = completion;
        Position = position;
        AppliedDeliveries = NormalizeDeliveries(appliedDeliveries);
        CommittedAtUtc = committedAtUtc;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Stable write-once checkpoint identity.</summary>
    public MaterializationCheckpointId Id { get; }

    /// <summary>Batch-continuation, batch-completed, or change-position semantics.</summary>
    public MaterializationCheckpointKind Kind { get; }

    /// <summary>Next batch page, or <see langword="null"/> for another checkpoint kind.</summary>
    public MaterializationSourceContinuation? Continuation { get; }

    /// <summary>Exact authoritative read completion, or <see langword="null"/> for another checkpoint kind.</summary>
    public MaterializationSourceReadCompletion? Completion { get; }

    /// <summary>Applied raw source position, or <see langword="null"/> for a batch checkpoint.</summary>
    public MaterializationSourcePosition? Position { get; }

    /// <summary>Applied delivery identities in deterministic ordinal order.</summary>
    public ImmutableArray<MaterializationDeliveryId> AppliedDeliveries { get; }

    /// <summary>UTC durable application-commit time.</summary>
    public DateTimeOffset CommittedAtUtc { get; }

    /// <summary>Optional opaque application evidence reference.</summary>
    public string? EvidenceReference { get; }

    static void ValidateCursor(
        MaterializationCheckpointKind kind,
        MaterializationSourceContinuation? continuation,
        MaterializationSourceReadCompletion? completion,
        MaterializationSourcePosition? position,
        ImmutableArray<MaterializationDeliveryId> appliedDeliveries)
    {
        var deliveries = appliedDeliveries.IsDefault ? [] : appliedDeliveries;
        var valid = kind switch
        {
            MaterializationCheckpointKind.BatchContinuation => continuation is not null
                && completion is null
                && position is null
                && deliveries.IsDefaultOrEmpty,
            MaterializationCheckpointKind.BatchCompleted => continuation is null
                && completion is not null
                && position is null
                && deliveries.IsDefaultOrEmpty,
            MaterializationCheckpointKind.ChangePosition => continuation is null
                && completion is null
                && position is not null,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                "Batch continuations require only a continuation, completed batches require exact authoritative read evidence, and change checkpoints require a position with optional applied delivery identities.",
                nameof(kind));
        }
    }

    static ImmutableArray<MaterializationDeliveryId> NormalizeDeliveries(
        ImmutableArray<MaterializationDeliveryId> values)
    {
        if (values.IsDefaultOrEmpty)
        {
            return [];
        }

        var isCanonical = true;
        for (var index = 0; index < values.Length; index++)
        {
            MaterializationContract.RequireDefinedIdentity(values[index].Value, nameof(values));
            if (index == 0)
            {
                continue;
            }

            var comparison = StringComparer.Ordinal.Compare(values[index - 1].Value, values[index].Value);
            if (comparison == 0)
            {
                throw new ArgumentException("Applied delivery identities cannot repeat.", nameof(values));
            }

            if (comparison > 0)
            {
                isCanonical = false;
            }
        }
        if (isCanonical)
        {
            return values;
        }

        var sorted = ImmutableArray.CreateBuilder<MaterializationDeliveryId>(values.Length);
        sorted.AddRange(values);
        sorted.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
        for (var index = 1; index < sorted.Count; index++)
        {
            if (sorted[index - 1] == sorted[index])
            {
                throw new ArgumentException("Applied delivery identities cannot repeat.", nameof(values));
            }
        }
        return sorted.MoveToImmutable();
    }
}

/// <summary>Explicit acknowledgement of one already-persisted change application checkpoint.</summary>
public sealed record MaterializationSourceSettlement
{
    /// <summary>Creates a source settlement.</summary>
    /// <param name="id">Stable write-once settlement identity.</param>
    /// <param name="checkpoint">Already-persisted application checkpoint cited by the settlement.</param>
    /// <param name="position">Raw source position proven by the cited checkpoint.</param>
    /// <param name="settledAtUtc">UTC time at which settlement completed.</param>
    /// <param name="evidenceReference">Optional opaque source acknowledgement evidence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, timestamp, or evidence reference is invalid.</exception>
    [JsonConstructor]
    public MaterializationSourceSettlement(
        MaterializationSettlementId id,
        MaterializationCheckpointId checkpoint,
        MaterializationSourcePosition position,
        DateTimeOffset settledAtUtc,
        string? evidenceReference = null)
    {
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        MaterializationContract.RequireDefinedIdentity(checkpoint.Value, nameof(checkpoint));
        Position = Guard.RequireNotNull(position);
        MaterializationContract.RequireUtc(settledAtUtc, nameof(settledAtUtc));
        if (evidenceReference is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        }

        Id = id;
        Checkpoint = checkpoint;
        SettledAtUtc = settledAtUtc;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Stable write-once settlement identity.</summary>
    public MaterializationSettlementId Id { get; }

    /// <summary>Already-persisted application checkpoint cited by the settlement.</summary>
    public MaterializationCheckpointId Checkpoint { get; }

    /// <summary>Raw source position proven by the cited checkpoint.</summary>
    public MaterializationSourcePosition Position { get; }

    /// <summary>UTC time at which settlement completed.</summary>
    public DateTimeOffset SettledAtUtc { get; }

    /// <summary>Optional opaque source acknowledgement evidence.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Immutable bounded view of the latest state in one materialization progress aggregate.</summary>
/// <remarks>
/// The snapshot deliberately carries only the latest application checkpoint and latest source settlement. Durable
/// implementations may retain additional audit or idempotency evidence internally, but history does not cross this
/// core persistence port.
/// </remarks>
public sealed record MaterializationProgressSnapshot
{
    /// <summary>Creates a coherent bounded progress snapshot.</summary>
    /// <param name="key">Exact generation and source-feed progress key.</param>
    /// <param name="revision">Current compare-and-swap revision.</param>
    /// <param name="fence">Current worker fence.</param>
    /// <param name="fenceOwner">Stable current worker identity.</param>
    /// <param name="latestCheckpoint">Latest persisted application checkpoint, when application progress exists.</param>
    /// <param name="latestSettlement">Latest persisted source settlement, when acknowledgement progress exists.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="key"/> or <paramref name="fenceOwner"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="revision"/> or <paramref name="fence"/> is default; an identity is otherwise invalid; a
    /// checkpoint or settlement belongs to another source-feed scope; a settlement exists without any application
    /// checkpoint; or a settlement that cites the latest checkpoint conflicts with it.
    /// </exception>
    [JsonConstructor]
    public MaterializationProgressSnapshot(
        MaterializationProgressKey key,
        MaterializationProgressRevision revision,
        MaterializationProgressFence fence,
        string fenceOwner,
        MaterializationApplicationCheckpoint? latestCheckpoint = null,
        MaterializationSourceSettlement? latestSettlement = null)
    {
        Key = Guard.RequireNotNull(key);
        MaterializationContract.RequireDefinedIdentity(revision.Value, nameof(revision));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        FenceOwner = Guard.RequireNotNullOrWhiteSpace(fenceOwner);
        ValidateLatestState(key, latestCheckpoint, latestSettlement);
        Revision = revision;
        Fence = fence;
        LatestCheckpoint = latestCheckpoint;
        LatestSettlement = latestSettlement;
    }

    /// <summary>Exact generation and source-feed progress key.</summary>
    public MaterializationProgressKey Key { get; }

    /// <summary>Current compare-and-swap revision.</summary>
    public MaterializationProgressRevision Revision { get; }

    /// <summary>Current worker fence.</summary>
    public MaterializationProgressFence Fence { get; }

    /// <summary>Stable current worker identity.</summary>
    public string FenceOwner { get; }

    /// <summary>Latest persisted checkpoint, or <see langword="null"/> before application progress exists.</summary>
    public MaterializationApplicationCheckpoint? LatestCheckpoint { get; }

    /// <summary>Latest persisted source settlement, or <see langword="null"/> before acknowledgement.</summary>
    public MaterializationSourceSettlement? LatestSettlement { get; }

    static void ValidateLatestState(
        MaterializationProgressKey key,
        MaterializationApplicationCheckpoint? latestCheckpoint,
        MaterializationSourceSettlement? latestSettlement)
    {
        if (latestCheckpoint is not null)
        {
            RequireCheckpointScope(key, latestCheckpoint);
        }

        if (latestSettlement is null)
        {
            return;
        }

        if (latestCheckpoint is null)
        {
            throw new ArgumentException(
                "A source settlement requires prior durable application progress.",
                nameof(latestSettlement));
        }
        if (latestSettlement.Position.Scope != key.Scope)
        {
            throw new ArgumentException(
                "A source settlement must belong to its exact progress source-feed scope.",
                nameof(latestSettlement));
        }

        // The latest settlement may cite an older checkpoint after application has advanced. When it cites the
        // checkpoint retained by this compact snapshot, the complete cross-record invariant remains verifiable.
        if (latestSettlement.Checkpoint == latestCheckpoint.Id
            && (latestCheckpoint.Kind != MaterializationCheckpointKind.ChangePosition
                || latestCheckpoint.Position != latestSettlement.Position
                || latestSettlement.SettledAtUtc < latestCheckpoint.CommittedAtUtc))
        {
            throw new ArgumentException(
                "A source settlement that cites the latest checkpoint must match its change position and chronology.",
                nameof(latestSettlement));
        }
    }

    internal static void RequireCheckpointScope(
        MaterializationProgressKey key,
        MaterializationApplicationCheckpoint checkpoint)
    {
        var scope = checkpoint.Kind switch
        {
            MaterializationCheckpointKind.BatchContinuation => checkpoint.Continuation?.Scope,
            MaterializationCheckpointKind.BatchCompleted => checkpoint.Completion?.Scope,
            MaterializationCheckpointKind.ChangePosition => checkpoint.Position?.Scope,
            _ => null
        };
        if (scope != key.Scope)
        {
            throw new ArgumentException("A checkpoint cursor must belong to its exact progress source-feed scope.", nameof(checkpoint));
        }
    }
}

/// <summary>Observable outcome of one progress-store mutation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationProgressMutationDisposition
{
    /// <summary>The mutation committed atomically.</summary>
    Applied = 0,

    /// <summary>The exact prior committed mutation or write-once semantic identity was reused.</summary>
    Replayed = 1,

    /// <summary>No progress aggregate exists for the requested key.</summary>
    NotFound = 2,

    /// <summary>The expected compare-and-swap revision is stale.</summary>
    RevisionConflict = 3,

    /// <summary>The supplied worker owner or fence has been superseded.</summary>
    StaleFence = 4,

    /// <summary>A stable mutation, checkpoint, or settlement identity was reused for different content.</summary>
    IdentityConflict = 5,

    /// <summary>A settlement cites a checkpoint that has not been persisted.</summary>
    CheckpointNotFound = 6,

    /// <summary>A settlement position conflicts with its cited checkpoint.</summary>
    CheckpointMismatch = 7
}

/// <summary>Result of one atomic materialization-progress mutation.</summary>
public sealed record MaterializationProgressMutationResult
{
    /// <summary>Creates a progress mutation result.</summary>
    /// <param name="disposition">Observable mutation disposition.</param>
    /// <param name="snapshot">Current coherent snapshot, when an aggregate exists.</param>
    /// <param name="diagnostics">Structured deterministic diagnostics for rejected mutations.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// Diagnostics contain a null entry, an applied or replayed result lacks a snapshot or carries diagnostics, a
    /// not-found result carries a snapshot, another rejection lacks a snapshot, or a rejection has no diagnostics.
    /// </exception>
    [JsonConstructor]
    public MaterializationProgressMutationResult(
        MaterializationProgressMutationDisposition disposition,
        MaterializationProgressSnapshot? snapshot,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported progress mutation disposition.");
        }

        var normalizedDiagnostics = MaterializationContract.NormalizeDiagnostics(diagnostics, nameof(diagnostics));
        var succeeded = disposition is MaterializationProgressMutationDisposition.Applied
            or MaterializationProgressMutationDisposition.Replayed;
        if (succeeded)
        {
            if (snapshot is null)
            {
                throw new ArgumentException("An applied or replayed progress mutation requires a snapshot.", nameof(snapshot));
            }

            if (!normalizedDiagnostics.IsDefaultOrEmpty)
            {
                throw new ArgumentException("An applied or replayed progress mutation cannot carry diagnostics.", nameof(diagnostics));
            }
        }
        else
        {
            var aggregateExists = disposition != MaterializationProgressMutationDisposition.NotFound;
            if (aggregateExists != (snapshot is not null))
            {
                throw new ArgumentException(
                    "Only a not-found progress mutation omits the current aggregate snapshot.",
                    nameof(snapshot));
            }
            if (normalizedDiagnostics.IsDefaultOrEmpty)
            {
                throw new ArgumentException("A rejected progress mutation requires a diagnostic.", nameof(diagnostics));
            }
        }

        Disposition = disposition;
        Snapshot = snapshot;
        Diagnostics = normalizedDiagnostics;
    }

    /// <summary>Observable mutation disposition.</summary>
    public MaterializationProgressMutationDisposition Disposition { get; }

    /// <summary>Current coherent snapshot, when an aggregate exists.</summary>
    public MaterializationProgressSnapshot? Snapshot { get; }

    /// <summary>Structured deterministic diagnostics for rejected mutations.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

}

/// <summary>Atomic provider-neutral persistence port for materialization application progress.</summary>
public interface IMaterializationProgressStore
{
    /// <summary>Loads the latest coherent progress aggregate.</summary>
    /// <param name="context">Operation context carrying tracing and cancellation.</param>
    /// <param name="key">Exact generation and source-feed progress key.</param>
    /// <returns>The latest snapshot, or <see langword="null"/> when absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="key"/> is null.</exception>
    /// <exception cref="OperationCanceledException">The context is canceled before reading.</exception>
    Task<MaterializationProgressSnapshot?> LoadAsync(
        OperationContext context,
        MaterializationProgressKey key);

    /// <summary>Creates or supersedes fenced worker ownership under compare-and-swap.</summary>
    /// <param name="context">Operation context carrying tracing and cancellation.</param>
    /// <param name="key">Exact generation and source-feed progress key.</param>
    /// <param name="mutationId">Stable identity reused only for an exact acquisition retry.</param>
    /// <param name="expectedRevision"><see langword="null"/> requires absence; otherwise the exact current revision.</param>
    /// <param name="owner">Stable physical worker identity.</param>
    /// <returns>Applied, replayed, missing, conflicting, or identity-conflict evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="key"/> is null.</exception>
    /// <exception cref="ArgumentException">A mutation or owner identity is invalid.</exception>
    /// <exception cref="OperationCanceledException">The context is canceled before the atomic boundary.</exception>
    Task<MaterializationProgressMutationResult> AcquireFenceAsync(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision? expectedRevision,
        string owner);

    /// <summary>Persists an application checkpoint under compare-and-swap and the current worker fence.</summary>
    /// <param name="context">Operation context carrying tracing and cancellation.</param>
    /// <param name="key">Exact generation and source-feed progress key.</param>
    /// <param name="mutationId">Stable identity reused only for an exact checkpoint retry.</param>
    /// <param name="expectedRevision">Exact current compare-and-swap revision.</param>
    /// <param name="owner">Stable current worker identity.</param>
    /// <param name="fence">Exact current worker fence.</param>
    /// <param name="checkpoint">Complete application checkpoint to persist.</param>
    /// <returns>Applied, replayed, missing, revision, fence, or identity-conflict evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="key"/>, or <paramref name="checkpoint"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">An identity or checkpoint scope is invalid.</exception>
    /// <exception cref="OperationCanceledException">The context is canceled before the atomic boundary.</exception>
    Task<MaterializationProgressMutationResult> SaveCheckpointAsync(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationApplicationCheckpoint checkpoint);

    /// <summary>Persists explicit source settlement for one already-persisted application checkpoint.</summary>
    /// <param name="context">Operation context carrying tracing and cancellation.</param>
    /// <param name="key">Exact generation and source-feed progress key.</param>
    /// <param name="mutationId">Stable identity reused only for an exact settlement retry.</param>
    /// <param name="expectedRevision">Exact current compare-and-swap revision.</param>
    /// <param name="owner">Stable current worker identity.</param>
    /// <param name="fence">Exact current worker fence.</param>
    /// <param name="settlement">Settlement that cites a durable change checkpoint.</param>
    /// <returns>Applied, replayed, missing, revision, fence, checkpoint, or identity-conflict evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="key"/>, or <paramref name="settlement"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">An identity or settlement scope is invalid.</exception>
    /// <exception cref="OperationCanceledException">The context is canceled before the atomic boundary.</exception>
    Task<MaterializationProgressMutationResult> SaveSettlementAsync(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationSourceSettlement settlement);
}

static class MaterializationProgressIntent
{
    public static string Fence(MaterializationProgressRevision? expectedRevision, string owner)
    {
        StringBuilder builder = new();
        Append(builder, "fence");
        Append(builder, expectedRevision?.Value);
        Append(builder, owner);
        return builder.ToString();
    }

    public static string Checkpoint(
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationApplicationCheckpoint checkpoint)
    {
        StringBuilder builder = new();
        Append(builder, "checkpoint");
        Append(builder, expectedRevision.Value);
        Append(builder, owner);
        Append(builder, fence.Value);
        Append(builder, checkpoint.Id.Value);
        Append(builder, ((int)checkpoint.Kind).ToString(CultureInfo.InvariantCulture));
        Append(builder, checkpoint.Continuation?.FormatVersion.ToString(CultureInfo.InvariantCulture));
        Append(builder, checkpoint.Continuation?.ReadFingerprint.Algorithm);
        Append(builder, checkpoint.Continuation?.ReadFingerprint.Canonicalization);
        Append(builder, checkpoint.Continuation?.ReadFingerprint.Value);
        AppendScope(builder, checkpoint.Continuation?.Scope);
        Append(builder, checkpoint.Continuation?.Value);
        AppendScope(builder, checkpoint.Completion?.Scope);
        Append(builder, checkpoint.Completion?.ReadFingerprint.Algorithm);
        Append(builder, checkpoint.Completion?.ReadFingerprint.Canonicalization);
        Append(builder, checkpoint.Completion?.ReadFingerprint.Value);
        Append(builder, checkpoint.Completion is null
            ? null
            : ((int)checkpoint.Completion.EvidenceState).ToString(CultureInfo.InvariantCulture));
        Append(builder, checkpoint.Completion?.EvidenceReference);
        AppendScope(builder, checkpoint.Position?.Scope);
        Append(builder, checkpoint.Position?.FormatVersion.ToString(CultureInfo.InvariantCulture));
        Append(builder, checkpoint.Position?.Value);
        Append(builder, checkpoint.AppliedDeliveries.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var delivery in checkpoint.AppliedDeliveries)
        {
            Append(builder, delivery.Value);
        }

        Append(builder, checkpoint.CommittedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Append(builder, checkpoint.EvidenceReference);
        return builder.ToString();
    }

    public static string Settlement(
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationSourceSettlement settlement)
    {
        StringBuilder builder = new();
        Append(builder, "settlement");
        Append(builder, expectedRevision.Value);
        Append(builder, owner);
        Append(builder, fence.Value);
        Append(builder, settlement.Id.Value);
        Append(builder, settlement.Checkpoint.Value);
        AppendScope(builder, settlement.Position.Scope);
        Append(builder, settlement.Position.FormatVersion.ToString(CultureInfo.InvariantCulture));
        Append(builder, settlement.Position.Value);
        Append(builder, settlement.SettledAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
        Append(builder, settlement.EvidenceReference);
        return builder.ToString();
    }

    static void AppendScope(StringBuilder builder, MaterializationSourceScope? scope)
    {
        Append(builder, scope?.PhysicalPlan.Algorithm);
        Append(builder, scope?.PhysicalPlan.Canonicalization);
        Append(builder, scope?.PhysicalPlan.Value);
        Append(builder, scope?.Placement.Id.Value);
        Append(builder, scope?.Input.Value);
        Append(builder, scope?.Source.Value);
        Append(builder, scope?.Shape.GraphId.Value);
        Append(builder, scope?.Shape.ShapeId.Value);
        Append(builder, scope is null
            ? null
            : ((int)scope.Placement.Acquisition).ToString(CultureInfo.InvariantCulture));
        Append(builder, scope?.Partition.Value);
        Append(builder, scope?.OrderingScope.Value);
    }

    static void Append(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
    }
}
