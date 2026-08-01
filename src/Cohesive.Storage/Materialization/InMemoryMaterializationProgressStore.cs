namespace Cohesive.Storage.Materialization;

/// <summary>
/// Deterministic, thread-safe reference implementation of fenced materialization progress durability.
/// </summary>
/// <remarks>
/// The fake retains checkpoint and settlement audit evidence internally for write-once identity and settlement
/// validation. Ordinary snapshots remain bounded and expose only the latest checkpoint and settlement.
/// </remarks>
public sealed class InMemoryMaterializationProgressStore : IMaterializationProgressStore
{
    readonly object gate = new();
    readonly Dictionary<MaterializationProgressKey, Aggregate> aggregates = [];

    /// <inheritdoc />
    public Task<MaterializationProgressSnapshot?> LoadAsync(
        OperationContext context,
        MaterializationProgressKey key)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(key);
        context.CancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult(aggregates.TryGetValue(key, out var aggregate)
                ? aggregate.Snapshot()
                : null);
        }
    }

    /// <inheritdoc />
    public Task<MaterializationProgressMutationResult> AcquireFenceAsync(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision? expectedRevision,
        string owner)
    {
        ValidateMutation(context, key, mutationId, owner);
        context.CancellationToken.ThrowIfCancellationRequested();
        var intent = MaterializationProgressIntent.Fence(expectedRevision, owner);

        lock (gate)
        {
            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                if (expectedRevision is not null)
                {
                    return Task.FromResult(Rejected(MaterializationProgressMutationDisposition.NotFound, null, key));
                }

                aggregate = new Aggregate(
                    key,
                    MaterializationProgressRevision.Initial,
                    MaterializationProgressFence.Initial,
                    owner);
                aggregate.Mutations.Add(mutationId, intent);
                aggregates.Add(key, aggregate);
                return Task.FromResult(Applied(aggregate));
            }

            if (TryReplay(aggregate, mutationId, intent, out var replay))
            {
                return Task.FromResult(replay);
            }

            if (expectedRevision is null || expectedRevision.Value != aggregate.Revision)
            {
                return Task.FromResult(Rejected(
                    MaterializationProgressMutationDisposition.RevisionConflict,
                    aggregate,
                    key));
            }

            aggregate.Revision = aggregate.Revision.Next();
            aggregate.Fence = aggregate.Fence.Next();
            aggregate.Owner = owner;
            aggregate.Mutations.Add(mutationId, intent);
            return Task.FromResult(Applied(aggregate));
        }
    }

    /// <inheritdoc />
    public Task<MaterializationProgressMutationResult> SaveCheckpointAsync(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationApplicationCheckpoint checkpoint)
    {
        ValidateMutation(context, key, mutationId, owner);
        ArgumentNullException.ThrowIfNull(checkpoint);
        MaterializationContract.RequireDefinedIdentity(expectedRevision.Value, nameof(expectedRevision));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        MaterializationProgressSnapshot.RequireCheckpointScope(key, checkpoint);
        context.CancellationToken.ThrowIfCancellationRequested();
        var intent = MaterializationProgressIntent.Checkpoint(
            expectedRevision,
            owner,
            fence,
            checkpoint);

        lock (gate)
        {
            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                return Task.FromResult(Rejected(MaterializationProgressMutationDisposition.NotFound, null, key));
            }

            if (TryReplay(aggregate, mutationId, intent, out var replay))
            {
                return Task.FromResult(replay);
            }

            var admission = AdmitWorker(aggregate, key, expectedRevision, owner, fence);
            if (admission is not null)
            {
                return Task.FromResult(admission);
            }

            if (aggregate.CheckpointAudit.TryGetValue(checkpoint.Id, out var existing))
            {
                if (!SameCheckpoint(existing, checkpoint))
                {
                    return Task.FromResult(Rejected(
                        MaterializationProgressMutationDisposition.IdentityConflict,
                        aggregate,
                        key));
                }

                aggregate.Mutations.Add(mutationId, intent);
                return Task.FromResult(Replayed(aggregate));
            }
            if (aggregate.LatestCheckpoint?.CommittedAtUtc > checkpoint.CommittedAtUtc)
            {
                return Task.FromResult(Rejected(
                    MaterializationProgressMutationDisposition.IdentityConflict,
                    aggregate,
                    key));
            }

            aggregate.CheckpointAudit.Add(checkpoint.Id, checkpoint);
            aggregate.LatestCheckpoint = checkpoint;
            aggregate.Revision = aggregate.Revision.Next();
            aggregate.Mutations.Add(mutationId, intent);
            return Task.FromResult(Applied(aggregate));
        }
    }

    /// <inheritdoc />
    public Task<MaterializationProgressMutationResult> SaveSettlementAsync(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationSourceSettlement settlement)
    {
        ValidateMutation(context, key, mutationId, owner);
        ArgumentNullException.ThrowIfNull(settlement);
        MaterializationContract.RequireDefinedIdentity(expectedRevision.Value, nameof(expectedRevision));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        if (settlement.Scope != key.Scope)
        {
            throw new ArgumentException("A settlement must belong to its exact progress source-feed scope.", nameof(settlement));
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        var intent = MaterializationProgressIntent.Settlement(
            expectedRevision,
            owner,
            fence,
            settlement);

        lock (gate)
        {
            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                return Task.FromResult(Rejected(MaterializationProgressMutationDisposition.NotFound, null, key));
            }

            if (TryReplay(aggregate, mutationId, intent, out var replay))
            {
                return Task.FromResult(replay);
            }

            var admission = AdmitWorker(aggregate, key, expectedRevision, owner, fence);
            if (admission is not null)
            {
                return Task.FromResult(admission);
            }

            if (aggregate.SettlementAudit.TryGetValue(settlement.Id, out var existing))
            {
                if (!SameSettlement(existing, settlement))
                {
                    return Task.FromResult(Rejected(
                        MaterializationProgressMutationDisposition.IdentityConflict,
                        aggregate,
                        key));
                }

                aggregate.Mutations.Add(mutationId, intent);
                return Task.FromResult(Replayed(aggregate));
            }

            if (!aggregate.CheckpointAudit.TryGetValue(settlement.Checkpoint, out var checkpoint))
            {
                return Task.FromResult(Rejected(
                    MaterializationProgressMutationDisposition.CheckpointNotFound,
                    aggregate,
                    key));
            }
            if (!settlement.IsCoveredBy(checkpoint, key.Scope))
            {
                return Task.FromResult(Rejected(
                    MaterializationProgressMutationDisposition.CheckpointMismatch,
                    aggregate,
                    key));
            }
            if (settlement.SettledAtUtc < checkpoint.CommittedAtUtc
                || aggregate.LatestSettlement?.SettledAtUtc > settlement.SettledAtUtc)
            {
                return Task.FromResult(Rejected(
                    MaterializationProgressMutationDisposition.CheckpointMismatch,
                    aggregate,
                    key));
            }

            aggregate.SettlementAudit.Add(settlement.Id, settlement);
            aggregate.LatestSettlement = settlement;
            aggregate.Revision = aggregate.Revision.Next();
            aggregate.Mutations.Add(mutationId, intent);
            return Task.FromResult(Applied(aggregate));
        }
    }

    static void ValidateMutation(
        OperationContext context,
        MaterializationProgressKey key,
        MaterializationProgressMutationId mutationId,
        string owner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(key);
        MaterializationContract.RequireDefinedIdentity(mutationId.Value, nameof(mutationId));
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
    }

    static MaterializationProgressMutationResult? AdmitWorker(
        Aggregate aggregate,
        MaterializationProgressKey key,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence)
    {
        if (fence != aggregate.Fence
            || !string.Equals(owner, aggregate.Owner, StringComparison.Ordinal))
        {
            return Rejected(MaterializationProgressMutationDisposition.StaleFence, aggregate, key);
        }
        if (expectedRevision != aggregate.Revision)
        {
            return Rejected(MaterializationProgressMutationDisposition.RevisionConflict, aggregate, key);
        }

        return null;
    }

    static bool TryReplay(
        Aggregate aggregate,
        MaterializationProgressMutationId mutationId,
        string intent,
        out MaterializationProgressMutationResult result)
    {
        if (!aggregate.Mutations.TryGetValue(mutationId, out var prior))
        {
            result = null!;
            return false;
        }

        result = string.Equals(prior, intent, StringComparison.Ordinal)
            ? Replayed(aggregate)
            : Rejected(
                MaterializationProgressMutationDisposition.IdentityConflict,
                aggregate,
                aggregate.Key);
        return true;
    }

    static bool SameCheckpoint(
        MaterializationApplicationCheckpoint left,
        MaterializationApplicationCheckpoint right) =>
        left.Id == right.Id
        && left.Kind == right.Kind
        && left.Continuation == right.Continuation
        && left.Completion == right.Completion
        && left.Position == right.Position
        && left.AppliedDeliveries.SequenceEqual(right.AppliedDeliveries)
        && left.ChannelProgress == right.ChannelProgress
        && left.CommittedAtUtc == right.CommittedAtUtc
        && string.Equals(left.EvidenceReference, right.EvidenceReference, StringComparison.Ordinal);

    static bool SameSettlement(
        MaterializationSourceSettlement left,
        MaterializationSourceSettlement right) =>
        left.Id == right.Id
        && left.Checkpoint == right.Checkpoint
        && left.Scope == right.Scope
        && left.Kind == right.Kind
        && left.Position == right.Position
        && left.Deliveries.SequenceEqual(right.Deliveries)
        && left.SettledAtUtc == right.SettledAtUtc
        && string.Equals(left.EvidenceReference, right.EvidenceReference, StringComparison.Ordinal);

    static MaterializationProgressMutationResult Applied(Aggregate aggregate) =>
        new(MaterializationProgressMutationDisposition.Applied, aggregate.Snapshot());

    static MaterializationProgressMutationResult Replayed(Aggregate aggregate) =>
        new(MaterializationProgressMutationDisposition.Replayed, aggregate.Snapshot());

    static MaterializationProgressMutationResult Rejected(
        MaterializationProgressMutationDisposition disposition,
        Aggregate? aggregate,
        MaterializationProgressKey key)
    {
        var (code, message) = disposition switch
        {
            MaterializationProgressMutationDisposition.NotFound =>
                (MaterializationProgressDiagnosticCodes.NotFound, "No progress aggregate exists for the requested key."),
            MaterializationProgressMutationDisposition.RevisionConflict =>
                (MaterializationProgressDiagnosticCodes.RevisionConflict, "The expected progress revision is stale."),
            MaterializationProgressMutationDisposition.StaleFence =>
                (MaterializationProgressDiagnosticCodes.StaleFence, "The supplied progress worker fence is stale."),
            MaterializationProgressMutationDisposition.IdentityConflict =>
                (MaterializationProgressDiagnosticCodes.IdentityConflict, "A write-once identity was reused for different content."),
            MaterializationProgressMutationDisposition.CheckpointNotFound =>
                (MaterializationProgressDiagnosticCodes.CheckpointNotFound, "Settlement requires an already-persisted checkpoint."),
            MaterializationProgressMutationDisposition.CheckpointMismatch =>
                (MaterializationProgressDiagnosticCodes.CheckpointMismatch, "Settlement coverage does not match its cited checkpoint."),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported rejected disposition.")
        };
        var subject = string.Join('/',
            key.Materialization.Value,
            key.DefinitionFingerprint.Value,
            key.Generation.Value,
            key.Scope.Input.Value,
            key.Scope.Source.Value,
            key.Scope.Partition.Value,
            key.Scope.OrderingScope.Value);
        var (expected, observed) = disposition switch
        {
            MaterializationProgressMutationDisposition.NotFound =>
                ("existing progress aggregate", "progress aggregate absent"),
            MaterializationProgressMutationDisposition.RevisionConflict =>
                ($"current revision '{aggregate?.Revision.Value ?? "unknown"}'", "non-current expected revision"),
            MaterializationProgressMutationDisposition.StaleFence =>
                ($"current owner '{aggregate?.Owner ?? "unknown"}' at fence '{aggregate?.Fence.Value ?? "unknown"}'", "supplied owner or fence was superseded"),
            MaterializationProgressMutationDisposition.IdentityConflict =>
                ("write-once identity retains identical content", "write-once identity mapped to different content"),
            MaterializationProgressMutationDisposition.CheckpointNotFound =>
                ("settlement cites a durable checkpoint", "cited checkpoint was absent"),
            MaterializationProgressMutationDisposition.CheckpointMismatch =>
                ("settlement has exact coverage in the cited checkpoint and valid chronology", "settlement coverage or chronology differed"),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported rejected disposition.")
        };
        return new(
            disposition,
            aggregate?.Snapshot(),
            [
                MaterializationContract.CreateDiagnostic(
                    code,
                    DiagnosticSeverity.Error,
                    message,
                    "/progress",
                    "materialization-progress-store",
                    subject,
                    [key.DefinitionFingerprint.Value],
                    expected,
                    observed)
            ]);
    }

    sealed class Aggregate(
        MaterializationProgressKey key,
        MaterializationProgressRevision revision,
        MaterializationProgressFence fence,
        string owner)
    {
        public MaterializationProgressKey Key { get; } = key;

        public MaterializationProgressRevision Revision { get; set; } = revision;

        public MaterializationProgressFence Fence { get; set; } = fence;

        public string Owner { get; set; } = owner;

        public MaterializationApplicationCheckpoint? LatestCheckpoint { get; set; }

        public MaterializationSourceSettlement? LatestSettlement { get; set; }

        public Dictionary<MaterializationCheckpointId, MaterializationApplicationCheckpoint> CheckpointAudit { get; } = [];

        public Dictionary<MaterializationSettlementId, MaterializationSourceSettlement> SettlementAudit { get; } = [];

        public Dictionary<MaterializationProgressMutationId, string> Mutations { get; } = [];

        public MaterializationProgressSnapshot Snapshot() => new(
            Key,
            Revision,
            Fence,
            Owner,
            LatestCheckpoint,
            LatestSettlement);
    }
}
