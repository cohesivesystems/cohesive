using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
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

    /// <summary>Settlement coverage differs from the progress proven by its cited checkpoint.</summary>
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

/// <summary>Semantic progress family represented by an application checkpoint.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationCheckpointKind
{
    /// <summary>A batch partition has more data and persists its next-page continuation.</summary>
    BatchContinuation = 0,

    /// <summary>A batch partition was authoritatively exhausted.</summary>
    BatchCompleted = 1,

    /// <summary>
    /// Incremental effects and their complete Channel progress evidence were durably applied. Positioned pull feeds
    /// retain their source position as a narrow projection; leased delivery may omit it.
    /// </summary>
    ChangeProgress = 2
}

/// <summary>
/// Durable application progress, intentionally distinct from both a raw source position and source settlement.
/// </summary>
public sealed record MaterializationApplicationCheckpoint
{
    /// <summary>Creates an application checkpoint.</summary>
    /// <param name="id">Stable write-once checkpoint identity.</param>
    /// <param name="kind">Batch-continuation, batch-completed, or incremental-change progress semantics.</param>
    /// <param name="continuation">Next batch page for <see cref="MaterializationCheckpointKind.BatchContinuation"/>.</param>
    /// <param name="completion">Exact authoritative read completion for <see cref="MaterializationCheckpointKind.BatchCompleted"/>.</param>
    /// <param name="position">
    /// Optional materialization source position corresponding to <paramref name="channelProgress"/>'s replay cursor.
    /// </param>
    /// <param name="appliedDeliveries">
    /// Exact stable delivery identities whose application effects are covered by this checkpoint. This application
    /// coverage is independent of provider pending-delivery progress in <paramref name="channelProgress"/>.
    /// </param>
    /// <param name="committedAtUtc">UTC durable application-commit time.</param>
    /// <param name="evidenceReference">Optional opaque application evidence reference.</param>
    /// <param name="channelProgress">
    /// Complete replay, floor, and pending-delivery progress required for an incremental change checkpoint; must be
    /// <see langword="null"/> for a batch checkpoint.
    /// </param>
    /// <param name="batchPageOrdinal">
    /// One-based cumulative baseline-page ordinal for a batch checkpoint; <see langword="null"/> for change progress.
    /// </param>
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
        string? evidenceReference = null,
        ChannelDurableProgressEvidence? channelProgress = null,
        long? batchPageOrdinal = null)
    {
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported application-checkpoint kind.");
        }

        var normalizedDeliveries = NormalizeDeliveries(
            appliedDeliveries,
            nameof(appliedDeliveries),
            "Applied delivery identities");
        ValidateCursor(kind, continuation, completion, position, normalizedDeliveries, channelProgress);
        var batchCheckpoint = kind is MaterializationCheckpointKind.BatchContinuation
            or MaterializationCheckpointKind.BatchCompleted;
        if (batchCheckpoint != (batchPageOrdinal is > 0))
        {
            throw new ArgumentException(
                "A batch checkpoint requires a positive cumulative page ordinal and change progress must omit it.",
                nameof(batchPageOrdinal));
        }
        ValidatePositionProjection(position, channelProgress);
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
        AppliedDeliveries = normalizedDeliveries;
        ChannelProgress = channelProgress;
        BatchPageOrdinal = batchPageOrdinal;
        CommittedAtUtc = committedAtUtc;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Stable write-once checkpoint identity.</summary>
    public MaterializationCheckpointId Id { get; }

    /// <summary>Batch-continuation, batch-completed, or incremental-change progress semantics.</summary>
    public MaterializationCheckpointKind Kind { get; }

    /// <summary>Next batch page, or <see langword="null"/> for another checkpoint kind.</summary>
    public MaterializationSourceContinuation? Continuation { get; }

    /// <summary>Exact authoritative read completion, or <see langword="null"/> for another checkpoint kind.</summary>
    public MaterializationSourceReadCompletion? Completion { get; }

    /// <summary>
    /// Optional source-specific replay position retained for positioned source operations. Incremental progress
    /// authority belongs to <see cref="ChannelProgress"/>; when present, this projection must identify its exact
    /// replay cursor.
    /// </summary>
    public MaterializationSourcePosition? Position { get; }

    /// <summary>
    /// Stable delivery identities whose application effects are covered, in deterministic ordinal order.
    /// </summary>
    public ImmutableArray<MaterializationDeliveryId> AppliedDeliveries { get; }

    /// <summary>
    /// Complete incremental Channel progress, or <see langword="null"/> for a batch continuation or completion.
    /// </summary>
    public ChannelDurableProgressEvidence? ChannelProgress { get; }

    /// <summary>
    /// One-based cumulative number of baseline pages durably applied through this batch checkpoint, or
    /// <see langword="null"/> for incremental change progress.
    /// </summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? BatchPageOrdinal { get; }

    /// <summary>UTC durable application-commit time.</summary>
    public DateTimeOffset CommittedAtUtc { get; }

    /// <summary>Optional opaque application evidence reference.</summary>
    public string? EvidenceReference { get; }

    /// <summary>Determines whether this durable checkpoint covers one exact positioned replay boundary.</summary>
    /// <param name="position">Materialization source position whose application must be durable.</param>
    /// <returns><see langword="true"/> when the checkpoint's replay cursor is the exact projected position.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <see langword="null"/>.</exception>
    public bool CoversReplayPosition(MaterializationSourcePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        var cursor = ChannelProgress?.ReplayCursor;
        return Kind == MaterializationCheckpointKind.ChangeProgress
            && cursor is not null
            && MaterializationChannelSemantics.IsSameReplayPosition(cursor, position);
    }

    internal bool CoversIndividualDeliveries(ImmutableArray<MaterializationDeliveryId> deliveries)
    {
        if (Kind != MaterializationCheckpointKind.ChangeProgress)
        {
            return false;
        }

        foreach (var delivery in deliveries)
        {
            if (!ContainsCanonical(AppliedDeliveries, delivery.Value))
                return false;
        }
        return !deliveries.IsDefaultOrEmpty;
    }

    static void ValidateCursor(
        MaterializationCheckpointKind kind,
        MaterializationSourceContinuation? continuation,
        MaterializationSourceReadCompletion? completion,
        MaterializationSourcePosition? position,
        ImmutableArray<MaterializationDeliveryId> appliedDeliveries,
        ChannelDurableProgressEvidence? channelProgress)
    {
        var deliveries = appliedDeliveries.IsDefault ? [] : appliedDeliveries;
        var valid = kind switch
        {
            MaterializationCheckpointKind.BatchContinuation => continuation is not null
                && completion is null
                && position is null
                && deliveries.IsDefaultOrEmpty
                && channelProgress is null,
            MaterializationCheckpointKind.BatchCompleted => continuation is null
                && completion is not null
                && position is null
                && deliveries.IsDefaultOrEmpty
                && channelProgress is null,
            MaterializationCheckpointKind.ChangeProgress => continuation is null
                && completion is null
                && channelProgress is not null,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                "Batch checkpoints carry only their batch cursor evidence, while change checkpoints require complete Channel progress and may additionally retain a matching source position.",
                nameof(kind));
        }
    }

    static void ValidatePositionProjection(
        MaterializationSourcePosition? position,
        ChannelDurableProgressEvidence? channelProgress)
    {
        if (position is not null
            && (channelProgress?.ReplayCursor is null
                || !MaterializationChannelSemantics.IsSameReplayPosition(channelProgress.ReplayCursor, position)))
        {
            throw new ArgumentException(
                "A retained materialization source position must equal the Channel replay cursor.",
                nameof(channelProgress));
        }
    }

    internal static ImmutableArray<MaterializationDeliveryId> NormalizeDeliveries(
        ImmutableArray<MaterializationDeliveryId> values,
        string parameterName,
        string description)
    {
        if (values.IsDefaultOrEmpty)
        {
            return [];
        }

        var isCanonical = true;
        for (var index = 0; index < values.Length; index++)
        {
            MaterializationContract.RequireDefinedIdentity(values[index].Value, parameterName);
            if (index == 0)
            {
                continue;
            }

            var comparison = StringComparer.Ordinal.Compare(values[index - 1].Value, values[index].Value);
            if (comparison == 0)
            {
                throw new ArgumentException($"{description} cannot repeat.", parameterName);
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
                throw new ArgumentException($"{description} cannot repeat.", parameterName);
            }
        }
        return sorted.MoveToImmutable();
    }

    static bool ContainsCanonical(
        ImmutableArray<MaterializationDeliveryId> deliveries,
        string sought)
    {
        return CanonicalDocumentCollections.BinarySearchIndex(
            deliveries,
            sought,
            static (delivery, expected) =>
                StringComparer.Ordinal.Compare(delivery.Value, expected)) >= 0;
    }
}

/// <summary>Explicit individual or cumulative acknowledgement of one already-persisted change checkpoint.</summary>
public sealed record MaterializationSourceSettlement
{
    /// <summary>Creates a positioned cumulative source settlement.</summary>
    /// <param name="id">Stable write-once settlement identity.</param>
    /// <param name="checkpoint">Already-persisted application checkpoint cited by the settlement.</param>
    /// <param name="position">Raw source position proven by the cited checkpoint.</param>
    /// <param name="settledAtUtc">UTC time at which settlement completed.</param>
    /// <param name="evidenceReference">Optional opaque source acknowledgement evidence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="position"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, timestamp, or evidence reference is invalid.</exception>
    public MaterializationSourceSettlement(
        MaterializationSettlementId id,
        MaterializationCheckpointId checkpoint,
        MaterializationSourcePosition position,
        DateTimeOffset settledAtUtc,
        string? evidenceReference = null)
        : this(
            id: id,
            checkpoint: checkpoint,
            scope: Guard.RequireNotNull(position).Scope,
            kind: ChannelSettlementKind.CumulativePrefix,
            position: position,
            deliveries: [],
            settledAtUtc: settledAtUtc,
            evidenceReference: evidenceReference)
    {
    }

    /// <summary>Creates an individual or cumulative source settlement.</summary>
    /// <param name="id">Stable write-once settlement identity.</param>
    /// <param name="checkpoint">Already-persisted application checkpoint cited by the settlement.</param>
    /// <param name="scope">Exact materialization source scope whose provider state changed.</param>
    /// <param name="kind">Individual delivery or cumulative-prefix settlement.</param>
    /// <param name="position">Exact cumulative position, or <see langword="null"/> for individual settlement.</param>
    /// <param name="deliveries">One exact delivery for individual settlement; empty for cumulative settlement.</param>
    /// <param name="settledAtUtc">UTC time at which settlement completed.</param>
    /// <param name="evidenceReference">Optional opaque source acknowledgement evidence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity, scope, coverage, timestamp, or evidence reference is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not individual or cumulative.</exception>
    [JsonConstructor]
    public MaterializationSourceSettlement(
        MaterializationSettlementId id,
        MaterializationCheckpointId checkpoint,
        MaterializationSourceScope scope,
        ChannelSettlementKind kind,
        MaterializationSourcePosition? position,
        ImmutableArray<MaterializationDeliveryId> deliveries,
        DateTimeOffset settledAtUtc,
        string? evidenceReference = null)
    {
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        MaterializationContract.RequireDefinedIdentity(checkpoint.Value, nameof(checkpoint));
        Scope = Guard.RequireNotNull(scope);
        if (kind is not (ChannelSettlementKind.CumulativePrefix or ChannelSettlementKind.Individual))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Materialization source settlement supports cumulative-prefix or individual acknowledgement.");
        }

        var normalizedDeliveries = MaterializationApplicationCheckpoint.NormalizeDeliveries(
            deliveries,
            nameof(deliveries),
            "Settled delivery identities");
        var validCoverage = kind switch
        {
            ChannelSettlementKind.CumulativePrefix => position is not null
                && normalizedDeliveries.IsDefaultOrEmpty,
            ChannelSettlementKind.Individual => position is null
                && normalizedDeliveries.Length == 1,
            _ => false
        };
        if (!validCoverage)
        {
            throw new ArgumentException(
                "Cumulative settlement requires one position and no deliveries; individual settlement requires exactly one delivery and no position.",
                nameof(kind));
        }
        if (position is not null && position.Scope != scope)
        {
            throw new ArgumentException(
                "A cumulative settlement position must belong to the exact settlement source scope.",
                nameof(position));
        }

        MaterializationContract.RequireUtc(settledAtUtc, nameof(settledAtUtc));
        if (evidenceReference is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        }

        Id = id;
        Checkpoint = checkpoint;
        Kind = kind;
        Position = position;
        Deliveries = normalizedDeliveries;
        SettledAtUtc = settledAtUtc;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Stable write-once settlement identity.</summary>
    public MaterializationSettlementId Id { get; }

    /// <summary>Already-persisted application checkpoint cited by the settlement.</summary>
    public MaterializationCheckpointId Checkpoint { get; }

    /// <summary>Exact source scope whose provider delivery state changed.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Individual or cumulative-prefix settlement operation.</summary>
    public ChannelSettlementKind Kind { get; }

    /// <summary>Raw cumulative source position, or <see langword="null"/> for individual settlement.</summary>
    public MaterializationSourcePosition? Position { get; }

    /// <summary>Exact delivery covered by individual settlement; empty for cumulative settlement.</summary>
    public ImmutableArray<MaterializationDeliveryId> Deliveries { get; }

    /// <summary>UTC time at which settlement completed.</summary>
    public DateTimeOffset SettledAtUtc { get; }

    /// <summary>Optional opaque source acknowledgement evidence.</summary>
    public string? EvidenceReference { get; }

    internal bool IsCoveredBy(
        MaterializationApplicationCheckpoint checkpoint,
        MaterializationSourceScope expectedScope) =>
        Checkpoint == checkpoint.Id
        && Scope == expectedScope
        && (Kind switch
        {
            ChannelSettlementKind.CumulativePrefix => checkpoint.CoversReplayPosition(Position!),
            ChannelSettlementKind.Individual => checkpoint.CoversIndividualDeliveries(Deliveries),
            _ => false
        });
}

/// <summary>Immutable bounded view of the latest state in one materialization progress aggregate.</summary>
/// <remarks>
/// Batch enumeration and incremental Channel delivery are independent progress tracks. The snapshot deliberately
/// carries only the latest checkpoint for each track and the latest source settlement. Durable implementations may
/// retain additional audit or idempotency evidence internally, but history does not cross this core persistence port.
/// </remarks>
public sealed record MaterializationProgressSnapshot
{
    /// <summary>Creates a coherent bounded progress snapshot.</summary>
    /// <param name="key">Exact generation and source-feed progress key.</param>
    /// <param name="revision">Current compare-and-swap revision.</param>
    /// <param name="fence">Current worker fence.</param>
    /// <param name="fenceOwner">Stable current worker identity.</param>
    /// <param name="latestBatchCheckpoint">
    /// Latest persisted batch-continuation or batch-completed checkpoint, when baseline enumeration progress exists.
    /// </param>
    /// <param name="latestChangeCheckpoint">
    /// Latest persisted incremental Channel checkpoint, when change-delivery progress exists.
    /// </param>
    /// <param name="latestSettlement">Latest persisted source settlement, when acknowledgement progress exists.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="key"/> or <paramref name="fenceOwner"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="revision"/> or <paramref name="fence"/> is default; an identity is otherwise invalid; a
    /// checkpoint is stored in the wrong progress track; a checkpoint or settlement belongs to another source-feed
    /// scope; a settlement exists without incremental application progress; or a settlement that cites the latest
    /// change checkpoint conflicts with it.
    /// </exception>
    [JsonConstructor]
    public MaterializationProgressSnapshot(
        MaterializationProgressKey key,
        MaterializationProgressRevision revision,
        MaterializationProgressFence fence,
        string fenceOwner,
        MaterializationApplicationCheckpoint? latestBatchCheckpoint = null,
        MaterializationApplicationCheckpoint? latestChangeCheckpoint = null,
        MaterializationSourceSettlement? latestSettlement = null)
    {
        Key = Guard.RequireNotNull(key);
        MaterializationContract.RequireDefinedIdentity(revision.Value, nameof(revision));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        FenceOwner = Guard.RequireNotNullOrWhiteSpace(fenceOwner);
        ValidateLatestState(key, latestBatchCheckpoint, latestChangeCheckpoint, latestSettlement);
        Revision = revision;
        Fence = fence;
        LatestBatchCheckpoint = latestBatchCheckpoint;
        LatestChangeCheckpoint = latestChangeCheckpoint;
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

    /// <summary>
    /// Latest persisted batch-continuation or batch-completed checkpoint, or <see langword="null"/> before baseline
    /// enumeration progress exists.
    /// </summary>
    public MaterializationApplicationCheckpoint? LatestBatchCheckpoint { get; }

    /// <summary>
    /// Latest persisted incremental Channel checkpoint, or <see langword="null"/> before change progress exists.
    /// </summary>
    public MaterializationApplicationCheckpoint? LatestChangeCheckpoint { get; }

    /// <summary>Latest persisted source settlement, or <see langword="null"/> before acknowledgement.</summary>
    public MaterializationSourceSettlement? LatestSettlement { get; }

    static void ValidateLatestState(
        MaterializationProgressKey key,
        MaterializationApplicationCheckpoint? latestBatchCheckpoint,
        MaterializationApplicationCheckpoint? latestChangeCheckpoint,
        MaterializationSourceSettlement? latestSettlement)
    {
        if (latestBatchCheckpoint is not null)
        {
            if (latestBatchCheckpoint.Kind is not (
                MaterializationCheckpointKind.BatchContinuation
                or MaterializationCheckpointKind.BatchCompleted))
            {
                throw new ArgumentException(
                    "The latest batch checkpoint must carry batch-continuation or batch-completed semantics.",
                    nameof(latestBatchCheckpoint));
            }
            RequireCheckpointScope(key, latestBatchCheckpoint);
        }

        if (latestChangeCheckpoint is not null)
        {
            if (latestChangeCheckpoint.Kind != MaterializationCheckpointKind.ChangeProgress)
            {
                throw new ArgumentException(
                    "The latest change checkpoint must carry incremental Channel progress semantics.",
                    nameof(latestChangeCheckpoint));
            }
            RequireCheckpointScope(key, latestChangeCheckpoint);
        }

        if (latestSettlement is null)
        {
            return;
        }

        if (latestChangeCheckpoint is null)
        {
            throw new ArgumentException(
                "A source settlement requires prior durable incremental Channel progress.",
                nameof(latestSettlement));
        }
        if (latestSettlement.Scope != key.Scope)
        {
            throw new ArgumentException(
                "A source settlement must belong to its exact progress source-feed scope.",
                nameof(latestSettlement));
        }

        // The latest settlement may cite an older checkpoint after application has advanced. When it cites the
        // checkpoint retained by this compact snapshot, the complete cross-record invariant remains verifiable.
        if (latestSettlement.Checkpoint == latestChangeCheckpoint.Id
            && (!latestSettlement.IsCoveredBy(latestChangeCheckpoint, key.Scope)
                || latestSettlement.SettledAtUtc < latestChangeCheckpoint.CommittedAtUtc))
        {
            throw new ArgumentException(
                "A source settlement that cites the latest change checkpoint must have exact durable coverage and chronology.",
                nameof(latestSettlement));
        }
    }

    internal static void RequireCheckpointScope(
        MaterializationProgressKey key,
        MaterializationApplicationCheckpoint checkpoint)
    {
        var scopeMatches = checkpoint.Kind switch
        {
            MaterializationCheckpointKind.BatchContinuation => checkpoint.Continuation?.Scope == key.Scope,
            MaterializationCheckpointKind.BatchCompleted => checkpoint.Completion?.Scope == key.Scope,
            MaterializationCheckpointKind.ChangeProgress => checkpoint.ChannelProgress is not null
                && MaterializationChannelSemantics.GetChannelScope(checkpoint.ChannelProgress)
                    == MaterializationChannelSemantics.ToChannelScopeId(key.Scope),
            _ => false
        };
        if (!scopeMatches)
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

    /// <summary>Settlement coverage conflicts with its cited checkpoint.</summary>
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
    static readonly JsonSerializerOptions CanonicalJsonOptions = StrictDocumentJson.CreateOptions();

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
        Append(builder, checkpoint.BatchPageOrdinal?.ToString(CultureInfo.InvariantCulture));
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
        Append(builder, checkpoint.ChannelProgress is null
            ? null
            : Convert.ToBase64String(StrictDocumentJson.GetCanonicalBytes(
                checkpoint.ChannelProgress,
                CanonicalJsonOptions)));

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
        AppendScope(builder, settlement.Scope);
        Append(builder, ((int)settlement.Kind).ToString(CultureInfo.InvariantCulture));
        Append(builder, settlement.Position?.FormatVersion.ToString(CultureInfo.InvariantCulture));
        Append(builder, settlement.Position?.Value);
        Append(builder, settlement.Deliveries.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var delivery in settlement.Deliveries)
            Append(builder, delivery.Value);
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
