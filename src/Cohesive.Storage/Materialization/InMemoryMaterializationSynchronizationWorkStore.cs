using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Execution;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Deterministic, thread-safe reference implementation of generation-wide synchronization-work durability.
/// </summary>
/// <remarks>
/// The implementation retains idempotency audit evidence internally while each public snapshot remains bounded to
/// one pending prepared write. Preparation and completion are separate atomic boundaries so a caller can recover
/// concrete target mutation identities after an ambiguous write or process crash.
/// </remarks>
public sealed class InMemoryMaterializationSynchronizationWorkStore
    : IMaterializationSynchronizationWorkStore
{
    const string MutationIdentityPrefix = "materialization-sync/";
    static readonly MaterializationItemVersion FirstIncrementalVersion = new("2");
    readonly object gate = new();
    readonly Dictionary<MaterializationSynchronizationWorkKey, Aggregate> aggregates = [];

    /// <inheritdoc />
    public Task<MaterializationSynchronizationWorkSnapshot?> LoadAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key)
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
    public Task<MaterializationSynchronizationWorkMutationResult> AcquireFenceAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision? expectedRevision,
        string owner)
    {
        ValidateMutation(context, key, mutationId, owner);
        context.CancellationToken.ThrowIfCancellationRequested();
        MutationAudit intent = new(
            MutationKind.AcquireFence,
            expectedRevision,
            owner,
            Fence: null,
            Work: null,
            PreparationId: null,
            Version: null,
            PreparedWork: null,
            Activation: null);

        lock (gate)
        {
            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                if (expectedRevision is not null)
                {
                    return Task.FromResult(Rejected(
                        MaterializationSynchronizationWorkMutationDisposition.NotFound,
                        aggregate: null,
                        key));
                }

                aggregate = new Aggregate(
                    key,
                    MaterializationProgressRevision.Initial,
                    MaterializationProgressFence.Initial,
                    owner,
                    FirstIncrementalVersion);
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
                    MaterializationSynchronizationWorkMutationDisposition.RevisionConflict,
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
    public Task<MaterializationSynchronizationWorkMutationResult> PrepareAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationSynchronizationWorkIntent intent)
    {
        ValidateMutation(context, key, mutationId, owner);
        ArgumentNullException.ThrowIfNull(intent);
        MaterializationContract.RequireDefinedIdentity(expectedRevision.Value, nameof(expectedRevision));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        context.CancellationToken.ThrowIfCancellationRequested();
        MutationAudit audit = new(
            MutationKind.Prepare,
            expectedRevision,
            owner,
            fence,
            intent,
            PreparationId: null,
            Version: null,
            PreparedWork: null,
            Activation: null);

        lock (gate)
        {
            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                return Task.FromResult(Rejected(
                    MaterializationSynchronizationWorkMutationDisposition.NotFound,
                    aggregate: null,
                    key));
            }

            if (TryReplay(aggregate, mutationId, audit, out var replay))
            {
                return Task.FromResult(replay);
            }

            var admission = AdmitWorker(aggregate, key, expectedRevision, owner, fence);
            if (admission is not null)
            {
                return Task.FromResult(admission);
            }

            if (aggregate.PendingWork is not null)
            {
                return Task.FromResult(Rejected(
                    MaterializationSynchronizationWorkMutationDisposition.PendingWorkConflict,
                    aggregate,
                    key));
            }
            if (aggregate.Activation is { IsComplete: false })
            {
                return Task.FromResult(Rejected(
                    MaterializationSynchronizationWorkMutationDisposition.ActivationConflict,
                    aggregate,
                    key));
            }

            MaterializationItemVersion? version = null;
            ImmutableArray<MaterializationItemMutation> mutations = [];
            if (!intent.Items.IsDefaultOrEmpty)
            {
                version = aggregate.NextItemVersion;
                aggregate.NextItemVersion = NextItemVersion(version.Value);
                mutations = PrepareMutations(key, mutationId, version.Value, intent);
            }
            MaterializationPreparedSynchronizationWork prepared = new(
                preparationId: mutationId,
                page: intent.Page,
                version,
                mutations);

            aggregate.PendingWork = prepared;
            aggregate.Revision = aggregate.Revision.Next();
            aggregate.Mutations.Add(mutationId, audit with { PreparedWork = prepared });
            return Task.FromResult(Applied(aggregate, prepared));
        }
    }

    /// <inheritdoc />
    public Task<MaterializationSynchronizationWorkMutationResult> CompleteAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationProgressMutationId preparationId,
        MaterializationItemVersion? version)
    {
        ValidateMutation(context, key, mutationId, owner);
        MaterializationContract.RequireDefinedIdentity(expectedRevision.Value, nameof(expectedRevision));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        MaterializationContract.RequireDefinedIdentity(preparationId.Value, nameof(preparationId));
        if (version is { } assignedVersion)
            MaterializationContract.RequireDefinedIdentity(assignedVersion.Value, nameof(version));
        context.CancellationToken.ThrowIfCancellationRequested();
        MutationAudit audit = new(
            MutationKind.Complete,
            expectedRevision,
            owner,
            fence,
            Work: null,
            preparationId,
            version,
            PreparedWork: null,
            Activation: null);

        lock (gate)
        {
            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                return Task.FromResult(Rejected(
                    MaterializationSynchronizationWorkMutationDisposition.NotFound,
                    aggregate: null,
                    key));
            }

            if (TryReplay(aggregate, mutationId, audit, out var replay))
            {
                return Task.FromResult(replay);
            }

            var admission = AdmitWorker(aggregate, key, expectedRevision, owner, fence);
            if (admission is not null)
            {
                return Task.FromResult(admission);
            }

            if (aggregate.PendingWork is not { } pending
                || pending.PreparationId != preparationId
                || pending.Version != version)
            {
                return Task.FromResult(Rejected(
                    MaterializationSynchronizationWorkMutationDisposition.PendingWorkConflict,
                    aggregate,
                    key));
            }

            aggregate.PendingWork = null;
            aggregate.Revision = aggregate.Revision.Next();
            aggregate.Mutations.Add(mutationId, audit);
            return Task.FromResult(Applied(aggregate));
        }
    }

    /// <inheritdoc />
    public Task<MaterializationSynchronizationWorkMutationResult> SaveActivationAsync(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence,
        MaterializationGenerationActivationState activation)
    {
        ValidateMutation(context, key, mutationId, owner);
        ArgumentNullException.ThrowIfNull(activation);
        if (activation.Convergence.Synchronization != key)
        {
            throw new ArgumentException(
                "Generation activation must belong to the exact synchronization-work key.",
                nameof(activation));
        }
        MaterializationContract.RequireDefinedIdentity(expectedRevision.Value, nameof(expectedRevision));
        MaterializationContract.RequireDefinedIdentity(fence.Value, nameof(fence));
        context.CancellationToken.ThrowIfCancellationRequested();
        MutationAudit audit = new(
            MutationKind.SaveActivation,
            expectedRevision,
            owner,
            fence,
            Work: null,
            PreparationId: null,
            Version: null,
            PreparedWork: null,
            activation);

        lock (gate)
        {
            if (!aggregates.TryGetValue(key, out var aggregate))
            {
                return Task.FromResult(Rejected(
                    MaterializationSynchronizationWorkMutationDisposition.NotFound,
                    aggregate: null,
                    key));
            }
            if (TryReplay(aggregate, mutationId, audit, out var replay))
                return Task.FromResult(replay);

            var admission = AdmitWorker(aggregate, key, expectedRevision, owner, fence);
            if (admission is not null)
                return Task.FromResult(admission);
            if (aggregate.PendingWork is not null)
            {
                return Task.FromResult(Rejected(
                    MaterializationSynchronizationWorkMutationDisposition.PendingWorkConflict,
                    aggregate,
                    key));
            }
            if (!IsNextActivationPrefix(aggregate.Activation, activation))
            {
                return Task.FromResult(Rejected(
                    MaterializationSynchronizationWorkMutationDisposition.ActivationConflict,
                    aggregate,
                    key));
            }

            aggregate.Activation = activation;
            aggregate.Revision = aggregate.Revision.Next();
            aggregate.Mutations.Add(mutationId, audit);
            return Task.FromResult(Applied(aggregate));
        }
    }

    static void ValidateMutation(
        OperationContext context,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId mutationId,
        string owner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(key);
        MaterializationContract.RequireDefinedIdentity(mutationId.Value, nameof(mutationId));
        _ = MaterializationContract.RequireUnicodeIdentity(owner, nameof(owner));
    }

    static MaterializationSynchronizationWorkMutationResult? AdmitWorker(
        Aggregate aggregate,
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressRevision expectedRevision,
        string owner,
        MaterializationProgressFence fence)
    {
        if (fence != aggregate.Fence
            || !string.Equals(owner, aggregate.Owner, StringComparison.Ordinal))
        {
            return Rejected(
                MaterializationSynchronizationWorkMutationDisposition.StaleFence,
                aggregate,
                key);
        }
        if (expectedRevision != aggregate.Revision)
        {
            return Rejected(
                MaterializationSynchronizationWorkMutationDisposition.RevisionConflict,
                aggregate,
                key);
        }

        return null;
    }

    static bool TryReplay(
        Aggregate aggregate,
        MaterializationProgressMutationId mutationId,
        MutationAudit intent,
        out MaterializationSynchronizationWorkMutationResult result)
    {
        if (!aggregate.Mutations.TryGetValue(mutationId, out var prior))
        {
            result = null!;
            return false;
        }

        result = prior.HasSameIntent(intent)
            ? Replayed(aggregate, prior.PreparedWork)
            : Rejected(
                MaterializationSynchronizationWorkMutationDisposition.IdentityConflict,
                aggregate,
                aggregate.Key);
        return true;
    }

    static ImmutableArray<MaterializationItemMutation> PrepareMutations(
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId preparationId,
        MaterializationItemVersion version,
        MaterializationSynchronizationWorkIntent intent)
    {
        var builder = ImmutableArray.CreateBuilder<MaterializationItemMutation>(intent.Items.Length);
        foreach (var item in intent.Items)
        {
            var mutationId = CreateMutationId(key, preparationId, version, item);
            builder.Add(item switch
            {
                MaterializationSynchronizationUpsertIntent upsert => new MaterializationUpsert(
                    itemId: upsert.ItemId,
                    mutationId,
                    version,
                    upsert.Value),
                MaterializationSynchronizationDeleteIntent delete => new MaterializationDelete(
                    itemId: delete.ItemId,
                    mutationId,
                    version),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(intent),
                    item.GetType().FullName,
                    "Unsupported synchronization item-intent subtype.")
            });
        }
        return builder.MoveToImmutable();
    }

    static MaterializationItemMutationId CreateMutationId(
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressMutationId preparationId,
        MaterializationItemVersion version,
        MaterializationSynchronizationItemIntent item)
    {
        using MaterializationStableIdentity.DigestBuilder builder = new();
        builder.Append("cohesive-materialization-synchronization-mutation/v1");
        builder.Append(key.Materialization.Value);
        builder.Append(key.DefinitionFingerprint.Algorithm);
        builder.Append(key.DefinitionFingerprint.Canonicalization);
        builder.Append(key.DefinitionFingerprint.Value);
        builder.Append(key.RebuildPlanFingerprint.Algorithm);
        builder.Append(key.RebuildPlanFingerprint.Canonicalization);
        builder.Append(key.RebuildPlanFingerprint.Value);
        builder.Append(key.ImpactPlanFingerprint.Algorithm);
        builder.Append(key.ImpactPlanFingerprint.Canonicalization);
        builder.Append(key.ImpactPlanFingerprint.Value);
        builder.Append(key.Generation.Value);
        builder.Append(preparationId.Value);
        builder.Append(version.Value);
        builder.Append(((int)item.Kind).ToString(CultureInfo.InvariantCulture));
        builder.Append(item.ItemId.Value);
        return new(MutationIdentityPrefix + builder.Complete());
    }

    static MaterializationItemVersion NextItemVersion(MaterializationItemVersion version) =>
        new(checked(version.Ordinal + 1).ToString(CultureInfo.InvariantCulture));

    static MaterializationSynchronizationWorkMutationResult Applied(
        Aggregate aggregate,
        MaterializationPreparedSynchronizationWork? preparedWork = null) =>
        new(
            MaterializationSynchronizationWorkMutationDisposition.Applied,
            aggregate.Snapshot(),
            preparedWork);

    static MaterializationSynchronizationWorkMutationResult Replayed(
        Aggregate aggregate,
        MaterializationPreparedSynchronizationWork? preparedWork) =>
        new(
            MaterializationSynchronizationWorkMutationDisposition.Replayed,
            aggregate.Snapshot(),
            preparedWork);

    static MaterializationSynchronizationWorkMutationResult Rejected(
        MaterializationSynchronizationWorkMutationDisposition disposition,
        Aggregate? aggregate,
        MaterializationSynchronizationWorkKey key)
    {
        var (code, message, expected, observed) = disposition switch
        {
            MaterializationSynchronizationWorkMutationDisposition.NotFound =>
                (MaterializationSynchronizationWorkDiagnosticCodes.NotFound,
                    "No synchronization-work aggregate exists for the exact requested key.",
                    "existing synchronization-work aggregate",
                    "aggregate absent"),
            MaterializationSynchronizationWorkMutationDisposition.RevisionConflict =>
                (MaterializationSynchronizationWorkDiagnosticCodes.RevisionConflict,
                    "The expected synchronization-work revision is stale.",
                    $"current revision '{aggregate?.Revision.Value ?? "unknown"}'",
                    "non-current expected revision"),
            MaterializationSynchronizationWorkMutationDisposition.StaleFence =>
                (MaterializationSynchronizationWorkDiagnosticCodes.StaleFence,
                    "The supplied synchronization worker fence is stale.",
                    $"current owner '{aggregate?.Owner ?? "unknown"}' at fence '{aggregate?.Fence.Value ?? "unknown"}'",
                    "supplied owner or fence was superseded"),
            MaterializationSynchronizationWorkMutationDisposition.IdentityConflict =>
                (MaterializationSynchronizationWorkDiagnosticCodes.IdentityConflict,
                    "A synchronization-work mutation identity was reused for different content.",
                    "stable mutation identity retains exact original intent",
                    "mutation identity mapped to different intent"),
            MaterializationSynchronizationWorkMutationDisposition.PendingWorkConflict =>
                (MaterializationSynchronizationWorkDiagnosticCodes.PendingWorkConflict,
                    "The operation conflicts with the exact pending prepared synchronization work.",
                    aggregate?.PendingWork is null
                        ? "one exact pending prepared work"
                        : $"completion or retry of preparation '{aggregate.PendingWork.PreparationId.Value}' at version "
                            + $"'{aggregate.PendingWork.Version?.Value ?? "effect-free"}'",
                    aggregate?.PendingWork is null
                        ? "no pending prepared work"
                        : "different work is already pending"),
            MaterializationSynchronizationWorkMutationDisposition.ActivationConflict =>
                (MaterializationSynchronizationWorkDiagnosticCodes.ActivationConflict,
                    "The operation conflicts with the exact durable generation-activation prefix.",
                    aggregate?.Activation is null
                        ? "initial seal intent"
                        : aggregate.Activation.IsComplete
                            ? "completed activation remains immutable"
                            : "the next exact seal, validation, or promotion prefix",
                    "work overlapped or skipped an activation stage"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unsupported rejected synchronization-work disposition.")
        };
        var subject = string.Join(
            '/',
            key.Materialization.Value,
            key.DefinitionFingerprint.Value,
            key.RebuildPlanFingerprint.Value,
            key.ImpactPlanFingerprint.Value,
            key.Generation.Value);
        return new(
            disposition,
            aggregate?.Snapshot(),
            preparedWork: null,
            [
                MaterializationContract.CreateDiagnostic(
                    code,
                    DiagnosticSeverity.Error,
                    message,
                    "/synchronizationWork",
                    "materialization-synchronization-work-store",
                    subject,
                    [
                        key.DefinitionFingerprint.Value,
                        key.RebuildPlanFingerprint.Value,
                        key.ImpactPlanFingerprint.Value
                    ],
                    expected,
                    observed)
            ]);
    }

    enum MutationKind
    {
        AcquireFence,
        Prepare,
        Complete,
        SaveActivation
    }

    sealed record MutationAudit(
        MutationKind Kind,
        MaterializationProgressRevision? ExpectedRevision,
        string Owner,
        MaterializationProgressFence? Fence,
        MaterializationSynchronizationWorkIntent? Work,
        MaterializationProgressMutationId? PreparationId,
        MaterializationItemVersion? Version,
        MaterializationPreparedSynchronizationWork? PreparedWork,
        MaterializationGenerationActivationState? Activation)
    {
        public bool HasSameIntent(MutationAudit other) =>
            Kind == other.Kind
            && ExpectedRevision == other.ExpectedRevision
            && string.Equals(Owner, other.Owner, StringComparison.Ordinal)
            && Fence == other.Fence
            && PreparationId == other.PreparationId
            && Version == other.Version
            && Activation == other.Activation
            && SamePage(Work?.Page, other.Work?.Page)
            && SameWork(Work, other.Work);
        static bool SamePage(
            MaterializationSynchronizationPageIntent? left,
            MaterializationSynchronizationPageIntent? right) =>
            ReferenceEquals(left, right)
            || left is not null
                && right is not null
                && left.Feed == right.Feed
                && left.Checkpoint == right.Checkpoint
                && left.ThroughPosition == right.ThroughPosition
                && left.State == right.State
                && left.ReadStartedAtUtc == right.ReadStartedAtUtc
                && left.ReadCompletedAtUtc == right.ReadCompletedAtUtc
                && left.AppliedDeliveries.SequenceEqual(right.AppliedDeliveries);

        static bool SameWork(
            MaterializationSynchronizationWorkIntent? left,
            MaterializationSynchronizationWorkIntent? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            if (left is null || right is null || left.Items.Length != right.Items.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Items.Length; index++)
            {
                var leftItem = left.Items[index];
                var rightItem = right.Items[index];
                if (leftItem.ItemId != rightItem.ItemId || leftItem.Kind != rightItem.Kind)
                {
                    return false;
                }
                if (leftItem is MaterializationSynchronizationUpsertIntent leftUpsert
                    && rightItem is MaterializationSynchronizationUpsertIntent rightUpsert
                    && !leftUpsert.Value.Equals(rightUpsert.Value))
                {
                    return false;
                }
            }
            return true;
        }
    }

    sealed class Aggregate(
        MaterializationSynchronizationWorkKey key,
        MaterializationProgressRevision revision,
        MaterializationProgressFence fence,
        string owner,
        MaterializationItemVersion nextItemVersion)
    {
        public MaterializationSynchronizationWorkKey Key { get; } = key;

        public MaterializationProgressRevision Revision { get; set; } = revision;

        public MaterializationProgressFence Fence { get; set; } = fence;

        public string Owner { get; set; } = owner;

        public MaterializationItemVersion NextItemVersion { get; set; } = nextItemVersion;

        public MaterializationPreparedSynchronizationWork? PendingWork { get; set; }

        public MaterializationGenerationActivationState? Activation { get; set; }

        public Dictionary<MaterializationProgressMutationId, MutationAudit> Mutations { get; } = [];

        public MaterializationSynchronizationWorkSnapshot Snapshot() => new(
            Key,
            Revision,
            Fence,
            Owner,
            NextItemVersion,
            PendingWork,
            Activation);
    }

    static bool IsNextActivationPrefix(
        MaterializationGenerationActivationState? current,
        MaterializationGenerationActivationState next)
    {
        if (current is null)
        {
            return next.SealReceipt is null
                && next.ValidationRequest is null
                && next.ValidationReceipt is null
                && next.PromotionRequest is null
                && next.PromotionReceipt is null;
        }
        if (current.IsComplete
            || current.Convergence != next.Convergence
            || current.SealRequest != next.SealRequest
            || (current.SealReceipt != next.SealReceipt && current.SealReceipt is not null)
            || (current.ValidationRequest != next.ValidationRequest && current.ValidationRequest is not null)
            || (current.ValidationReceipt != next.ValidationReceipt && current.ValidationReceipt is not null)
            || (current.PromotionRequest != next.PromotionRequest && current.PromotionRequest is not null))
        {
            return false;
        }

        if (current.SealReceipt is null)
        {
            return next.SealReceipt is not null
                && next.ValidationRequest is not null
                && next.ValidationReceipt is null;
        }
        if (current.ValidationReceipt is null)
            return next.ValidationReceipt is not null && next.PromotionReceipt is null;
        if (!current.ValidationReceipt.Validation.IsValid)
            return false;
        return current.PromotionReceipt is null && next.PromotionReceipt is not null;
    }
}
