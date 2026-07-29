using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>
/// Copy-on-write in-memory reference implementation of the atomic Process durability contract.
/// </summary>
/// <remarks>
/// The implementation is a semantic test oracle, not a production durability claim. All aggregate mutations are
/// staged in isolated immutable values and published under one lock so an injected pre-commit crash exposes none of
/// the staged state and an injected post-commit crash exposes all of it.
/// </remarks>
public sealed class InMemoryProcessDurableStore : IProcessDurableStore
{
    readonly Lock gate = new();
    readonly Func<ProcessStoreCrashContext, bool>? shouldCrash;
    readonly Dictionary<ProcessInstanceId, StoredAggregate> aggregates = [];

    /// <summary>Creates an empty reference Process store.</summary>
    public InMemoryProcessDurableStore()
    {
    }

    internal InMemoryProcessDurableStore(Func<ProcessStoreCrashContext, bool> shouldCrash)
    {
        this.shouldCrash = shouldCrash ?? throw new ArgumentNullException(nameof(shouldCrash));
    }

    /// <inheritdoc />
    public ProcessDurableStoreCapabilities Capabilities { get; } = new(
        SupportsAtomicAggregateCommit: true,
        SupportsCompareAndSwap: true,
        SupportsWorkerFencing: true);

    /// <inheritdoc />
    public Task<ProcessDurableStoreSnapshot?> LoadAsync(
        OperationContext context,
        ProcessInstanceId instanceId)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireInstance(instanceId);
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            return Task.FromResult(
                aggregates.TryGetValue(instanceId, out var aggregate)
                    ? aggregate.Snapshot()
                    : null);
        }
    }

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> InitializeAsync(
        OperationContext context,
        ProcessCommitId commitId,
        ProcessDurableCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ProcessCheckpointRequirements.RequireIdentity(commitId.Value, nameof(commitId));
        context.ThrowIfCancellationRequested();
        var instanceId = checkpoint.ContinuationIdentity.ProcessInstanceId;
        var fingerprint = ProcessDurableCommitFingerprinter.ComputeCheckpoint(checkpoint);

        lock (gate)
        {
            if (aggregates.TryGetValue(instanceId, out var existing))
            {
                if (existing.CommitReceipts.TryGetValue(commitId, out var prior))
                {
                    return Task.FromResult(prior.Fingerprint == fingerprint
                        ? new ProcessStoreMutationResult(
                            ProcessStoreMutationDisposition.Replayed,
                            prior.Snapshot)
                        : Result(ProcessStoreMutationDisposition.IdentityConflict, existing));
                }

                return Task.FromResult(Result(ProcessStoreMutationDisposition.AlreadyExists, existing));
            }

            var revision = ProcessStorageRevision.Initial;
            var created = new StoredAggregate(
                checkpoint,
                revision,
                WorkerLease: null,
                LatestWorkerFence: 0,
                LocalState: [],
                LocalMutationFingerprints: [],
                CommitReceipts: []);
            created = created with
            {
                CommitReceipts = created.CommitReceipts.Add(
                    commitId,
                    new(fingerprint, created.Snapshot()))
            };
            Publish(instanceId, ProcessStoreMutationKind.Initialize, created);
            return Task.FromResult(Result(ProcessStoreMutationDisposition.Applied, created));
        }
    }

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> AdmitInputAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        ProcessActivationInput input,
        DateTimeOffset admittedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        RequireInstance(instanceId);
        ProcessCheckpointRequirements.RequireUtc(admittedAtUtc, nameof(admittedAtUtc));
        if (input.Target.Continuation.ProcessInstanceId != instanceId)
        {
            throw new ArgumentException("The durable input target must address the requested Process instance.", nameof(input));
        }

        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!aggregates.TryGetValue(instanceId, out var aggregate))
            {
                return Task.FromResult(new ProcessStoreMutationResult(ProcessStoreMutationDisposition.NotFound, null));
            }

            var emissionId = input.Envelope.Context.EmissionId;
            var existing = FindInboxEntry(aggregate.Checkpoint.Inbox, emissionId);
            if (existing is not null)
            {
                var same = ProcessStorageContentFingerprints.Input(existing.Input)
                    == ProcessStorageContentFingerprints.Input(input);
                return Task.FromResult(Result(
                    same
                        ? ProcessStoreMutationDisposition.Replayed
                        : ProcessStoreMutationDisposition.IdentityConflict,
                    aggregate));
            }

            if (admittedAtUtc < aggregate.Checkpoint.UpdatedAtUtc)
            {
                throw new ArgumentException(
                    "Durable input admission cannot predate the latest aggregate update.",
                    nameof(admittedAtUtc));
            }

            var inbox = aggregate.Checkpoint.Inbox.Add(new(input, admittedAtUtc));
            var checkpoint = aggregate.Checkpoint.WithInbox(inbox, admittedAtUtc);
            var updated = aggregate with
            {
                Checkpoint = checkpoint,
                Revision = aggregate.Revision.Next()
            };
            Publish(instanceId, ProcessStoreMutationKind.InboxAdmission, updated);
            return Task.FromResult(Result(ProcessStoreMutationDisposition.Applied, updated));
        }
    }

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> AcquireWorkerAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        ProcessStorageRevision expectedRevision,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireInstance(instanceId);
        ProcessCheckpointRequirements.RequireIdentity(expectedRevision.Value, nameof(expectedRevision));
        owner = Guard.RequireNotNullOrWhiteSpace(owner);
        ValidateLease(leaseDuration, observedAtUtc);
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!aggregates.TryGetValue(instanceId, out var aggregate))
            {
                return Task.FromResult(new ProcessStoreMutationResult(ProcessStoreMutationDisposition.NotFound, null));
            }

            var current = aggregate.WorkerLease;
            if (current is not null && current.IsLive(observedAtUtc))
            {
                var requestedExpiry = AddLease(observedAtUtc, leaseDuration);
                var exactReplay = string.Equals(current.Owner, owner, StringComparison.Ordinal)
                    && current.ClaimedAtUtc == observedAtUtc
                    && current.RenewedAtUtc >= observedAtUtc
                    && current.ExpiresAtUtc >= requestedExpiry;
                if (exactReplay)
                {
                    return Task.FromResult(Result(ProcessStoreMutationDisposition.Replayed, aggregate));
                }
            }

            if (aggregate.Revision != expectedRevision)
            {
                return Task.FromResult(Result(ProcessStoreMutationDisposition.RevisionConflict, aggregate));
            }

            if (observedAtUtc < aggregate.Checkpoint.UpdatedAtUtc
                || (current is not null && observedAtUtc < current.RenewedAtUtc))
            {
                throw new ArgumentException(
                    "A worker acquisition observation cannot predate retained aggregate or lease evidence.",
                    nameof(observedAtUtc));
            }

            if (current is not null && current.IsLive(observedAtUtc))
            {
                return Task.FromResult(Result(
                    string.Equals(current.Owner, owner, StringComparison.Ordinal)
                        ? ProcessStoreMutationDisposition.Replayed
                        : ProcessStoreMutationDisposition.LeaseHeld,
                    aggregate));
            }

            var nextFence = checked(aggregate.LatestWorkerFence + 1);
            var expiresAtUtc = AddLease(observedAtUtc, leaseDuration);
            var lease = new ProcessWorkerLease(
                owner,
                new(nextFence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                observedAtUtc,
                observedAtUtc,
                expiresAtUtc);
            var updated = aggregate with
            {
                Revision = aggregate.Revision.Next(),
                WorkerLease = lease,
                LatestWorkerFence = nextFence
            };
            Publish(instanceId, ProcessStoreMutationKind.WorkerAcquisition, updated);
            return Task.FromResult(Result(ProcessStoreMutationDisposition.Applied, updated));
        }
    }

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> RenewWorkerAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        string owner,
        ProcessWorkerFence fence,
        TimeSpan leaseDuration,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireInstance(instanceId);
        owner = Guard.RequireNotNullOrWhiteSpace(owner);
        ProcessCheckpointRequirements.RequireIdentity(fence.Value, nameof(fence));
        ValidateLease(leaseDuration, observedAtUtc);
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!aggregates.TryGetValue(instanceId, out var aggregate))
            {
                return Task.FromResult(new ProcessStoreMutationResult(ProcessStoreMutationDisposition.NotFound, null));
            }

            var current = aggregate.WorkerLease;
            if (current is null
                || current.Fence != fence
                || !string.Equals(current.Owner, owner, StringComparison.Ordinal))
            {
                return Task.FromResult(Result(ProcessStoreMutationDisposition.StaleFence, aggregate));
            }

            var expiresAtUtc = AddLease(observedAtUtc, leaseDuration);
            var replayedOrSubsumed = observedAtUtc >= current.ClaimedAtUtc
                && current.RenewedAtUtc >= observedAtUtc
                && current.ExpiresAtUtc >= expiresAtUtc;
            if (replayedOrSubsumed)
            {
                return Task.FromResult(Result(ProcessStoreMutationDisposition.Replayed, aggregate));
            }

            if (observedAtUtc < aggregate.Checkpoint.UpdatedAtUtc
                || observedAtUtc < current.RenewedAtUtc)
            {
                throw new ArgumentException(
                    "A worker renewal observation cannot predate retained aggregate or lease evidence.",
                    nameof(observedAtUtc));
            }

            if (!current.IsLive(observedAtUtc))
            {
                return Task.FromResult(Result(ProcessStoreMutationDisposition.LeaseExpired, aggregate));
            }

            var renewed = new ProcessWorkerLease(
                owner,
                fence,
                current.ClaimedAtUtc,
                observedAtUtc,
                expiresAtUtc);
            var updated = aggregate with
            {
                Revision = aggregate.Revision.Next(),
                WorkerLease = renewed
            };
            Publish(instanceId, ProcessStoreMutationKind.WorkerRenewal, updated);
            return Task.FromResult(Result(ProcessStoreMutationDisposition.Applied, updated));
        }
    }

    /// <inheritdoc />
    public Task<ProcessStoreMutationResult> CommitAsync(
        OperationContext context,
        ProcessDurableCommit commit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        context.ThrowIfCancellationRequested();
        var instanceId = commit.Checkpoint.ContinuationIdentity.ProcessInstanceId;

        lock (gate)
        {
            if (!aggregates.TryGetValue(instanceId, out var aggregate))
            {
                return Task.FromResult(new ProcessStoreMutationResult(ProcessStoreMutationDisposition.NotFound, null));
            }

            if (aggregate.CommitReceipts.TryGetValue(commit.Id, out var priorCommit))
            {
                return Task.FromResult(priorCommit.Fingerprint == commit.Fingerprint
                    ? new ProcessStoreMutationResult(
                        ProcessStoreMutationDisposition.Replayed,
                        priorCommit.Snapshot)
                    : Result(ProcessStoreMutationDisposition.IdentityConflict, aggregate));
            }

            if (aggregate.Revision != commit.ExpectedRevision)
            {
                return Task.FromResult(Result(ProcessStoreMutationDisposition.RevisionConflict, aggregate));
            }

            if (aggregate.WorkerLease is not { } lease
                || lease.Fence != commit.Fence
                || !string.Equals(lease.Owner, commit.Owner, StringComparison.Ordinal))
            {
                return Task.FromResult(Result(ProcessStoreMutationDisposition.StaleFence, aggregate));
            }

            var physicalObservedAtUtc = context.UtcNow;
            if (!lease.IsLive(physicalObservedAtUtc)
                || physicalObservedAtUtc < lease.RenewedAtUtc
                || commit.ObservedAtUtc < lease.RenewedAtUtc)
            {
                return Task.FromResult(Result(ProcessStoreMutationDisposition.LeaseExpired, aggregate));
            }

            if (!IsValidSuccessor(aggregate.Checkpoint, commit.Checkpoint))
            {
                return Task.FromResult(Result(ProcessStoreMutationDisposition.IdentityConflict, aggregate));
            }

            if (!TryApplyLocalMutations(
                    aggregate,
                    commit.LocalMutations,
                    out var localState,
                    out var mutationFingerprints))
            {
                return Task.FromResult(Result(ProcessStoreMutationDisposition.LocalMutationConflict, aggregate));
            }

            var revision = aggregate.Revision.Next();
            var updated = aggregate with
            {
                Checkpoint = commit.Checkpoint,
                Revision = revision,
                LocalState = localState,
                LocalMutationFingerprints = mutationFingerprints
            };
            updated = updated with
            {
                CommitReceipts = updated.CommitReceipts.Add(
                    commit.Id,
                    new(commit.Fingerprint, updated.Snapshot()))
            };
            Publish(instanceId, ProcessStoreMutationKind.AggregateCommit, updated);
            return Task.FromResult(Result(ProcessStoreMutationDisposition.Applied, updated));
        }
    }

    void Publish(
        ProcessInstanceId instanceId,
        ProcessStoreMutationKind kind,
        StoredAggregate replacement)
    {
        Crash(instanceId, kind, ProcessStoreCrashPhase.BeforeAtomicCommit);
        aggregates[instanceId] = replacement;
        Crash(instanceId, kind, ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn);
    }

    void Crash(
        ProcessInstanceId instanceId,
        ProcessStoreMutationKind kind,
        ProcessStoreCrashPhase phase)
    {
        var context = new ProcessStoreCrashContext(instanceId, kind, phase);
        if (shouldCrash?.Invoke(context) == true)
        {
            throw new ProcessStoreInjectedCrashException(context);
        }
    }

    static ProcessStoreMutationResult Result(
        ProcessStoreMutationDisposition disposition,
        StoredAggregate aggregate) =>
        new(disposition, aggregate.Snapshot());

    static ProcessDurableInboxEntry? FindInboxEntry(
        ImmutableArray<ProcessDurableInboxEntry> inbox,
        EmissionId emissionId)
    {
        var low = 0;
        var high = inbox.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = StringComparer.Ordinal.Compare(inbox[middle].EmissionId.Value, emissionId.Value);
            if (comparison == 0)
            {
                return inbox[middle];
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return null;
    }

    static bool TryApplyLocalMutations(
        StoredAggregate aggregate,
        ImmutableArray<ProcessLocalMutation> mutations,
        out ImmutableDictionary<string, ProcessLocalState> localState,
        out ImmutableDictionary<string, ProcessCommitFingerprint> mutationFingerprints)
    {
        var state = aggregate.LocalState;
        var fingerprints = aggregate.LocalMutationFingerprints;
        foreach (var mutation in mutations)
        {
            var fingerprint = ProcessStorageContentFingerprints.LocalMutation(mutation);
            if (fingerprints.TryGetValue(mutation.Identity, out var prior))
            {
                if (prior != fingerprint)
                {
                    localState = aggregate.LocalState;
                    mutationFingerprints = aggregate.LocalMutationFingerprints;
                    return false;
                }
                continue;
            }

            state.TryGetValue(mutation.Resource, out var current);
            var currentVersion = current?.Version ?? 0;
            if (mutation.ExpectedVersion is { } expected && expected != currentVersion)
            {
                localState = aggregate.LocalState;
                mutationFingerprints = aggregate.LocalMutationFingerprints;
                return false;
            }

            var next = checked(currentVersion + 1);
            state = state.SetItem(
                mutation.Resource,
                new(mutation.Resource, next, mutation.Value, mutation.Identity));
            fingerprints = fingerprints.Add(mutation.Identity, fingerprint);
        }

        localState = state;
        mutationFingerprints = fingerprints;
        return true;
    }

    static bool IsValidSuccessor(
        ProcessDurableCheckpoint current,
        ProcessDurableCheckpoint replacement)
    {
        if (current.SchemaVersion != replacement.SchemaVersion
            || !CanonicalEquals(current.Start, replacement.Start)
            || current.Definition != replacement.Definition
            || current.ContinuationIdentity.ProcessInstanceId
                != replacement.ContinuationIdentity.ProcessInstanceId
            || current.CreatedAtUtc != replacement.CreatedAtUtc
            || replacement.UpdatedAtUtc < current.UpdatedAtUtc
            || !IsContinuationSuccessor(current, replacement)
            || !IsControlSuccessor(current.Control, replacement.Control)
            || !IsCanonicalPrefix(current.Activations, replacement.Activations)
            || !IsOperationReceiptSuccessor(current.Operations, replacement.Operations)
            || !IsInboxSuccessor(current.Inbox, replacement.Inbox)
            || FailsToClosePendingInboxOnAttemptReplacement(current, replacement)
            || !IsEmissionSuccessor(current.Emissions, replacement.Emissions)
            || !IsDurableOperationSuccessor(current.DurableOperations, replacement.DurableOperations)
            || AppendsPhysicalAttemptAcrossBlockedControlCut(current, replacement)
            || AppendsEvidenceToClosedAttempt(current, replacement))
        {
            return false;
        }

        return true;
    }

    static bool FailsToClosePendingInboxOnAttemptReplacement(
        ProcessDurableCheckpoint current,
        ProcessDurableCheckpoint replacement)
    {
        if (current.ContinuationIdentity.ProcessAttemptId
            == replacement.ContinuationIdentity.ProcessAttemptId)
        {
            return false;
        }

        var abandoned = replacement.Control.Attempts.SingleOrDefault(attempt =>
            attempt.AttemptId == current.ContinuationIdentity.ProcessAttemptId);
        if (abandoned?.Closure is not { } closure)
        {
            return true;
        }

        var replacementById = replacement.Inbox.ToDictionary(static entry => entry.EmissionId);
        return current.Inbox.Any(entry =>
            (entry.Receipt is null
             || entry.Receipt.Disposition == ProcessInputAdmissionDisposition.Buffered)
            && (!replacementById.TryGetValue(entry.EmissionId, out var candidate)
                || candidate.Receipt is not { Disposition: ProcessInputAdmissionDisposition.Stale } receipt
                || candidate.DispositionContinuation != current.ContinuationIdentity
                || receipt.ObservedAtUtc != closure.OccurredAtUtc));
    }

    static bool AppendsPhysicalAttemptAcrossBlockedControlCut(
        ProcessDurableCheckpoint current,
        ProcessDurableCheckpoint replacement)
    {
        var physicalAdmissionIsOpen =
            current.Control.Mode == ProcessControlMode.Running
            && replacement.Control.Mode == ProcessControlMode.Running
            && current.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.None
            && replacement.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.None;
        var replacementClosedAttempts = replacement.Control.Attempts
            .Where(static attempt => attempt.Disposition != ProcessControlAttemptDisposition.Current)
            .Select(static attempt => attempt.AttemptId)
            .ToHashSet();
        var replacementClosedOperationEmissions = replacement.Operations
            .Where(receipt => replacementClosedAttempts.Contains(receipt.Key.Continuation.ProcessAttemptId))
            .SelectMany(static receipt => receipt.Result.Emissions)
            .Select(static emission => emission.Context.EmissionId)
            .ToHashSet();

        var currentEmissionsById = current.Emissions.ToDictionary(static emission => emission.EmissionId);
        foreach (var emission in replacement.Emissions)
        {
            var priorAttemptCount = currentEmissionsById.TryGetValue(emission.EmissionId, out var prior)
                ? prior.Attempts.Length
                : 0;
            if (emission.Attempts.Length <= priorAttemptCount)
            {
                continue;
            }

            if (!physicalAdmissionIsOpen
                || IsAttributedToClosedAttempt(
                    emission.Envelope,
                    replacementClosedOperationEmissions,
                    replacementClosedAttempts))
            {
                return true;
            }
        }

        var currentById = current.DurableOperations.ToDictionary(static operation => operation.OperationId);
        foreach (var operation in replacement.DurableOperations)
        {
            var priorAttemptCount = currentById.TryGetValue(operation.OperationId, out var prior)
                ? prior.Attempts.Length
                : 0;
            if (operation.Attempts.Length <= priorAttemptCount)
            {
                continue;
            }

            if (!physicalAdmissionIsOpen)
            {
                return true;
            }

            if (operation.Request.Context.Origin is ProcessInteractionOrigin origin
                && replacement.Control.Attempts.FirstOrDefault(attempt =>
                    attempt.AttemptId == origin.Continuation.ProcessAttemptId) is not
                    { Disposition: ProcessControlAttemptDisposition.Current })
            {
                return true;
            }
        }
        return false;
    }

    static bool AppendsEvidenceToClosedAttempt(
        ProcessDurableCheckpoint current,
        ProcessDurableCheckpoint replacement)
    {
        var closedAttempts = current.Control.Attempts
            .Where(static attempt => attempt.Disposition != ProcessControlAttemptDisposition.Current)
            .Select(static attempt => attempt.AttemptId)
            .ToHashSet();
        var terminalAttemptClosed =
            current.Continuation.Terminal.Kind != ExecutionTerminalOutcomeKind.None;
        if (terminalAttemptClosed)
        {
            closedAttempts.Add(current.ContinuationIdentity.ProcessAttemptId);
        }

        if (closedAttempts.Count == 0)
        {
            return false;
        }

        var currentActivations = current.Activations
            .ToDictionary(static receipt => (receipt.Continuation.ProcessAttemptId, receipt.Activation.Id));
        if (replacement.Activations.Any(receipt =>
            closedAttempts.Contains(receipt.Continuation.ProcessAttemptId)
            && !currentActivations.ContainsKey((receipt.Continuation.ProcessAttemptId, receipt.Activation.Id))))
        {
            return true;
        }

        var currentOperations = current.Operations.ToDictionary(static receipt => receipt.Key);
        if (replacement.Operations.Any(receipt =>
            closedAttempts.Contains(receipt.Key.Continuation.ProcessAttemptId)
            && !currentOperations.ContainsKey(receipt.Key)))
        {
            return true;
        }

        var currentInbox = current.Inbox.ToDictionary(static entry => entry.EmissionId);
        if (replacement.Inbox.Any(entry =>
            entry.DispositionContinuation is { } continuation
            && closedAttempts.Contains(continuation.ProcessAttemptId)
            && (!currentInbox.TryGetValue(entry.EmissionId, out var prior)
                || !CanonicalEquals(prior, entry))))
        {
            return true;
        }

        var closedOperationEmissions = replacement.Operations
            .Where(receipt => closedAttempts.Contains(receipt.Key.Continuation.ProcessAttemptId))
            .SelectMany(static receipt => receipt.Result.Emissions)
            .Select(static emission => emission.Context.EmissionId)
            .ToHashSet();
        var currentEmissions = current.Emissions.ToDictionary(static emission => emission.EmissionId);
        if (replacement.Emissions.Any(emission =>
        {
            var appendsLogicalEmission = !currentEmissions.TryGetValue(emission.EmissionId, out var prior);
            var appendsPhysicalAttempt = !appendsLogicalEmission
                && emission.Attempts.Length > prior!.Attempts.Length;
            return (terminalAttemptClosed || IsAttributedToClosedAttempt(
                    emission.Envelope,
                    closedOperationEmissions,
                    closedAttempts))
                && (appendsLogicalEmission || appendsPhysicalAttempt);
        }))
        {
            return true;
        }

        var currentDurableOperations = current.DurableOperations
            .ToDictionary(static operation => operation.OperationId);
        foreach (var operation in replacement.DurableOperations)
        {
            var appendsLogicalOperation =
                !currentDurableOperations.TryGetValue(operation.OperationId, out var prior);
            var appendsPhysicalAttempt = !appendsLogicalOperation
                && operation.Attempts.Length > prior!.Attempts.Length;
            if (!appendsLogicalOperation && !appendsPhysicalAttempt)
            {
                continue;
            }

            if (terminalAttemptClosed
                || operation.Request.Context.Origin is ProcessInteractionOrigin origin
                && closedAttempts.Contains(origin.Continuation.ProcessAttemptId))
            {
                return true;
            }
        }

        return false;
    }

    static bool IsAttributedToClosedAttempt(
        InteractionEnvelope envelope,
        IReadOnlySet<EmissionId> closedOperationEmissions,
        IReadOnlySet<ProcessAttemptId> closedAttempts) =>
        envelope.Context.Origin is ProcessInteractionOrigin origin
            && closedAttempts.Contains(origin.Continuation.ProcessAttemptId)
        || closedOperationEmissions.Contains(envelope.Context.EmissionId);

    static bool IsControlSuccessor(
        ProcessControlState current,
        ProcessControlState replacement)
    {
        if (current.SchemaVersion != replacement.SchemaVersion
            || current.Definition != replacement.Definition
            || current.AuthorityScope != replacement.AuthorityScope
            || current.ProcessInstanceId != replacement.ProcessInstanceId
            || current.CreatedAtUtc != replacement.CreatedAtUtc
            || replacement.UpdatedAtUtc < current.UpdatedAtUtc
            || replacement.Revision.CompareTo(current.Revision) < 0
            || !IsCanonicalPrefix(current.Receipts, replacement.Receipts)
            || replacement.Attempts.Length < current.Attempts.Length
            || replacement.Attempts.Length > current.Attempts.Length + 1)
        {
            return false;
        }

        for (var index = 0; index < current.Attempts.Length - 1; index++)
        {
            if (!CanonicalEquals(current.Attempts[index], replacement.Attempts[index]))
            {
                return false;
            }
        }

        if (!IsControlAttemptSuccessor(
                current.CurrentAttempt,
                replacement.Attempts[current.Attempts.Length - 1]))
        {
            return false;
        }

        if (replacement.Revision == current.Revision)
        {
            return replacement.Attempts.Length == current.Attempts.Length
                && replacement.Mode == current.Mode
                && replacement.PendingCommandId == current.PendingCommandId;
        }

        return true;
    }

    static bool IsControlAttemptSuccessor(
        ProcessControlAttemptState current,
        ProcessControlAttemptState replacement)
    {
        if (current.AttemptId != replacement.AttemptId
            || current.StartedAtUtc != replacement.StartedAtUtc)
        {
            return false;
        }

        if (current.Disposition != ProcessControlAttemptDisposition.Current)
        {
            return CanonicalEquals(current, replacement);
        }

        if (!IsCanonicalPrefix(current.SafePoints, replacement.SafePoints)
            || !AreAffinityBindingsRetained(current.AffinityBindings, replacement.AffinityBindings)
            || (current.Closure is not null
                && (replacement.Closure is null
                    || !CanonicalEquals(current.Closure, replacement.Closure))))
        {
            return false;
        }

        if (current.ActiveActivation is not { } active
            || replacement.ActiveActivation is { } retainedActive
            && CanonicalEquals(active, retainedActive))
        {
            return true;
        }

        var completedAtSafePoint = replacement.SafePoints
            .Skip(current.SafePoints.Length)
            .Any(safePoint => CanonicalEquals(active, safePoint.Activation));
        var interruptedByClosure = replacement.Closure?.InterruptedActivation is { } interrupted
            && CanonicalEquals(active, interrupted);
        return completedAtSafePoint || interruptedByClosure;
    }

    static bool AreAffinityBindingsRetained(
        ImmutableArray<ProcessAttemptAffinityObservation> current,
        ImmutableArray<ProcessAttemptAffinityObservation> replacement)
    {
        var replacementBySlot = replacement.ToDictionary(static binding => binding.Affinity.Slot);
        foreach (var binding in current)
        {
            if (!replacementBySlot.TryGetValue(binding.Affinity.Slot, out var candidate)
                || !CanonicalEquals(binding, candidate))
            {
                return false;
            }
        }
        return true;
    }

    static bool IsContinuationSuccessor(
        ProcessDurableCheckpoint current,
        ProcessDurableCheckpoint replacement)
    {
        if (current.ContinuationIdentity.ProcessAttemptId
            == replacement.ContinuationIdentity.ProcessAttemptId)
        {
            if (replacement.Continuation.CompletedActivationCount
                < current.Continuation.CompletedActivationCount)
            {
                return false;
            }

            if (replacement.Continuation.CompletedActivationCount
                == current.Continuation.CompletedActivationCount)
            {
                return ProcessStorageContentFingerprints.Continuation(current.Continuation)
                    == ProcessStorageContentFingerprints.Continuation(replacement.Continuation);
            }

            var firstAppendedReceipt = replacement.Activations.FirstOrDefault(receipt =>
                receipt.Continuation == current.ContinuationIdentity
                && receipt.Sequence == current.Continuation.CompletedActivationCount + 1);
            return firstAppendedReceipt is not null
                && firstAppendedReceipt.BeforeContinuation
                    == ProcessStorageContentFingerprints.Continuation(current.Continuation);
        }

        if (replacement.Control.Attempts.Length != current.Control.Attempts.Length + 1
            || replacement.Control.CurrentAttempt.AttemptId
                != replacement.ContinuationIdentity.ProcessAttemptId
            || replacement.Continuation.CompletedActivationCount != 0
            || replacement.Activations.Any(receipt =>
                receipt.Continuation == replacement.ContinuationIdentity))
        {
            return false;
        }

        for (var index = 0; index < current.Control.Attempts.Length - 1; index++)
        {
            if (!CanonicalEquals(current.Control.Attempts[index], replacement.Control.Attempts[index]))
            {
                return false;
            }
        }

        var abandoned = replacement.Control.Attempts[^2];
        if (abandoned.AttemptId != current.Control.CurrentAttempt.AttemptId
            || abandoned.Disposition != ProcessControlAttemptDisposition.Abandoned
            || abandoned.Closure is not { } closure)
        {
            return false;
        }

        return replacement.Control.Receipts.Any(receipt =>
            receipt.Command.Context.CommandId == closure.CommandId
            && receipt.BeforeAttemptId == abandoned.AttemptId
            && receipt.Command is RestartProcessAttemptCommand restart
            && restart.Plan.NewAttemptId == replacement.Control.CurrentAttempt.AttemptId);
    }

    static bool IsOperationReceiptSuccessor(
        ImmutableArray<ProcessOperationReceipt> current,
        ImmutableArray<ProcessOperationReceipt> replacement)
    {
        var replacementByKey = replacement.ToDictionary(static receipt => receipt.Key);
        foreach (var receipt in current)
        {
            if (!replacementByKey.TryGetValue(receipt.Key, out var candidate)
                || !CanonicalEquals(receipt, candidate))
            {
                return false;
            }
        }
        return true;
    }

    static bool IsInboxSuccessor(
        ImmutableArray<ProcessDurableInboxEntry> current,
        ImmutableArray<ProcessDurableInboxEntry> replacement)
    {
        var replacementById = replacement.ToDictionary(static entry => entry.EmissionId);
        foreach (var entry in current)
        {
            if (!replacementById.TryGetValue(entry.EmissionId, out var candidate)
                || ProcessStorageContentFingerprints.Input(entry.Input)
                    != ProcessStorageContentFingerprints.Input(candidate.Input)
                || entry.AdmittedAtUtc != candidate.AdmittedAtUtc
                || !IsInboxReceiptSuccessor(entry, candidate))
            {
                return false;
            }
        }
        return true;
    }

    static bool IsInboxReceiptSuccessor(
        ProcessDurableInboxEntry current,
        ProcessDurableInboxEntry replacement)
    {
        if (current.Receipt is null)
        {
            return true;
        }

        if (replacement.Receipt is null)
        {
            return false;
        }

        if (CanonicalEquals(current.Receipt, replacement.Receipt))
        {
            return current.DispositionContinuation == replacement.DispositionContinuation;
        }

        return current.Receipt.Disposition == ProcessInputAdmissionDisposition.Buffered
            && replacement.Receipt.Disposition != ProcessInputAdmissionDisposition.Buffered;
    }

    static bool IsEmissionSuccessor(
        ImmutableArray<ProcessEmissionRecord> current,
        ImmutableArray<ProcessEmissionRecord> replacement)
    {
        var replacementById = replacement.ToDictionary(static entry => entry.EmissionId);
        foreach (var entry in current)
        {
            if (!replacementById.TryGetValue(entry.EmissionId, out var candidate)
                || ProcessStorageContentFingerprints.Envelope(entry.Envelope)
                    != ProcessStorageContentFingerprints.Envelope(candidate.Envelope)
                || entry.EnqueuedAtUtc != candidate.EnqueuedAtUtc
                || !IsAttemptHistorySuccessor(entry.Attempts, candidate.Attempts)
                || (entry.Publication is not null
                    && (candidate.Publication is null
                        || !CanonicalEquals(entry.Publication, candidate.Publication))))
            {
                return false;
            }
        }

        var currentIds = current.Select(static entry => entry.EmissionId).ToHashSet();
        if (replacement.Any(entry =>
            !currentIds.Contains(entry.EmissionId)
            && (!entry.Attempts.IsEmpty || entry.Publication is not null)))
        {
            return false;
        }
        return true;
    }

    static bool IsDurableOperationSuccessor(
        ImmutableArray<DurableOperationState> current,
        ImmutableArray<DurableOperationState> replacement)
    {
        var replacementById = replacement.ToDictionary(static state => state.OperationId);
        foreach (var state in current)
        {
            if (!replacementById.TryGetValue(state.OperationId, out var candidate)
                || state.SchemaVersion != candidate.SchemaVersion
                || ProcessStorageContentFingerprints.Envelope(state.Request)
                    != ProcessStorageContentFingerprints.Envelope(candidate.Request)
                || !CanonicalEquals(state.Binding, candidate.Binding)
                || state.CreatedAtUtc != candidate.CreatedAtUtc
                || !IsAttemptHistorySuccessor(state.Attempts, candidate.Attempts)
                || !IsCanonicalPrefix(state.Reconciliations, candidate.Reconciliations)
                || (state.Acknowledgement is not null
                    && (candidate.Acknowledgement is null
                        || !CanonicalEquals(state.Acknowledgement, candidate.Acknowledgement)))
                || (state.Admission is not null
                    && (candidate.Admission is null
                        || !CanonicalEquals(state.Admission, candidate.Admission)))
                || (state.RecoveryRequirement != candidate.RecoveryRequirement
                    && !HasDurableOperationProgress(state, candidate)))
            {
                return false;
            }
        }


        var currentIds = current.Select(static state => state.OperationId).ToHashSet();
        if (replacement.Any(state =>
            !currentIds.Contains(state.OperationId)
            && (!state.Attempts.IsEmpty
                || !state.Reconciliations.IsEmpty
                || state.RecoveryRequirement != DurableOperationRecoveryRequirement.None
                || state.Acknowledgement is not null
                || state.Admission is not null)))
        {
            return false;
        }
        return true;
    }

    static bool HasDurableOperationProgress(
        DurableOperationState current,
        DurableOperationState replacement) =>
        !CanonicalSequenceEquals(current.Attempts, replacement.Attempts)
        || replacement.Reconciliations.Length > current.Reconciliations.Length
        || (current.Acknowledgement is null && replacement.Acknowledgement is not null)
        || (current.Admission is null && replacement.Admission is not null);

    static bool IsAttemptHistorySuccessor(
        ImmutableArray<DurableOperationAttempt> current,
        ImmutableArray<DurableOperationAttempt> replacement)
    {
        if (replacement.Length < current.Length || replacement.Length > current.Length + 1)
        {
            return false;
        }

        if (current.IsEmpty)
        {
            return replacement.IsEmpty
                || replacement is [{ Stage: DurableOperationAttemptStage.Claimed }];
        }

        for (var index = 0; index < current.Length - 1; index++)
        {
            if (!CanonicalEquals(current[index], replacement[index]))
            {
                return false;
            }
        }

        var candidate = replacement[current.Length - 1];
        if (!IsAttemptSuccessor(current[^1], candidate))
        {
            return false;
        }

        return replacement.Length == current.Length
            || candidate.Stage == DurableOperationAttemptStage.Failed
            && replacement[^1].Stage == DurableOperationAttemptStage.Claimed;
    }

    static bool IsAttemptSuccessor(
        DurableOperationAttempt current,
        DurableOperationAttempt replacement)
    {
        var currentClaim = current.Claim;
        var replacementClaim = replacement.Claim;
        if (current.Ordinal != replacement.Ordinal
            || currentClaim.AttemptId != replacementClaim.AttemptId
            || !string.Equals(currentClaim.Claimant, replacementClaim.Claimant, StringComparison.Ordinal)
            || currentClaim.Fence != replacementClaim.Fence
            || currentClaim.ClaimedAtUtc != replacementClaim.ClaimedAtUtc)
        {
            return false;
        }

        var claimUnchanged = currentClaim.RenewedAtUtc == replacementClaim.RenewedAtUtc
            && currentClaim.ExpiresAtUtc == replacementClaim.ExpiresAtUtc;
        var claimAdvanced = replacementClaim.RenewedAtUtc > currentClaim.RenewedAtUtc
            && replacementClaim.ExpiresAtUtc > currentClaim.ExpiresAtUtc;
        if (!claimUnchanged
            && (!claimAdvanced
                || current.Stage is not (DurableOperationAttemptStage.Claimed
                    or DurableOperationAttemptStage.Dispatched)))
        {
            return false;
        }

        if (current.DispatchedAtUtc is { } dispatched
            && replacement.DispatchedAtUtc != dispatched
            || current.CompletedAtUtc is { } completed
            && replacement.CompletedAtUtc != completed
            || current.Failure is not null
            && (replacement.Failure is null || !CanonicalEquals(current.Failure, replacement.Failure)))
        {
            return false;
        }

        return (current.Stage, replacement.Stage) switch
        {
            (DurableOperationAttemptStage.Claimed, DurableOperationAttemptStage.Claimed) => true,
            (DurableOperationAttemptStage.Claimed, DurableOperationAttemptStage.Dispatched) => true,
            (DurableOperationAttemptStage.Claimed, DurableOperationAttemptStage.Failed) =>
                replacement.DispatchedAtUtc is null,
            (DurableOperationAttemptStage.Dispatched, DurableOperationAttemptStage.Dispatched) => true,
            (DurableOperationAttemptStage.Dispatched, DurableOperationAttemptStage.Failed
                or DurableOperationAttemptStage.Acknowledged) => true,
            (DurableOperationAttemptStage.Failed, DurableOperationAttemptStage.Failed) =>
                CanonicalEquals(current, replacement),
            (DurableOperationAttemptStage.Failed, DurableOperationAttemptStage.Resolved) => true,
            (DurableOperationAttemptStage.Acknowledged, DurableOperationAttemptStage.Acknowledged) =>
                CanonicalEquals(current, replacement),
            (DurableOperationAttemptStage.Resolved, DurableOperationAttemptStage.Resolved) =>
                CanonicalEquals(current, replacement),
            _ => false
        };
    }

    static bool CanonicalSequenceEquals<T>(ImmutableArray<T> left, ImmutableArray<T> right)
        where T : class
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (!CanonicalEquals(left[index], right[index]))
            {
                return false;
            }
        }
        return true;
    }

    static bool IsCanonicalPrefix<T>(ImmutableArray<T> prefix, ImmutableArray<T> values)
        where T : class
    {
        if (prefix.Length > values.Length)
        {
            return false;
        }

        for (var index = 0; index < prefix.Length; index++)
        {
            if (!CanonicalEquals(prefix[index], values[index]))
            {
                return false;
            }
        }
        return true;
    }

    static bool CanonicalEquals<T>(T left, T right)
        where T : class =>
        EqualityComparer<T>.Default.Equals(left, right)
        || ProcessStorageContentFingerprints.Value(left)
            == ProcessStorageContentFingerprints.Value(right);

    static void RequireInstance(ProcessInstanceId instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId.Value))
        {
            throw new ArgumentException("A Process durable-store operation requires an instance identity.", nameof(instanceId));
        }
    }

    static void ValidateLease(TimeSpan leaseDuration, DateTimeOffset observedAtUtc)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException("A Process worker lease must be positive.", nameof(leaseDuration));
        }

        ProcessCheckpointRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
    }

    static DateTimeOffset AddLease(DateTimeOffset observedAtUtc, TimeSpan leaseDuration)
    {
        try
        {
            return observedAtUtc.Add(leaseDuration);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("The Process worker lease expiry cannot be represented.", nameof(leaseDuration), exception);
        }
    }

    sealed record StoredCommitReceipt(
        ProcessCommitFingerprint Fingerprint,
        ProcessDurableStoreSnapshot Snapshot);

    sealed record StoredAggregate(
        ProcessDurableCheckpoint Checkpoint,
        ProcessStorageRevision Revision,
        ProcessWorkerLease? WorkerLease,
        long LatestWorkerFence,
        ImmutableDictionary<string, ProcessLocalState> LocalState,
        ImmutableDictionary<string, ProcessCommitFingerprint> LocalMutationFingerprints,
        ImmutableDictionary<ProcessCommitId, StoredCommitReceipt> CommitReceipts)
    {
        internal ProcessDurableStoreSnapshot Snapshot() =>
            new(
                Checkpoint,
                Revision,
                WorkerLease,
                [.. LocalState.Values.OrderBy(static value => value.Resource, StringComparer.Ordinal)]);
    }
}

internal enum ProcessStoreMutationKind
{
    Initialize = 0,
    InboxAdmission = 1,
    WorkerAcquisition = 2,
    WorkerRenewal = 3,
    AggregateCommit = 4
}

internal enum ProcessStoreCrashPhase
{
    BeforeAtomicCommit = 0,
    AfterAtomicCommitBeforeReturn = 1
}

internal readonly record struct ProcessStoreCrashContext(
    ProcessInstanceId InstanceId,
    ProcessStoreMutationKind MutationKind,
    ProcessStoreCrashPhase Phase);

internal sealed class ProcessStoreInjectedCrashException(ProcessStoreCrashContext context)
    : Exception($"Injected Process-store crash at '{context.MutationKind}/{context.Phase}'.")
{
    internal ProcessStoreCrashContext Context { get; } = context;
}
