using System.Collections.Immutable;
using System.Security.Cryptography;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Observable outcome of one physical Process-store mutation.</summary>
public enum ProcessStoreMutationDisposition
{
    /// <summary>The mutation committed atomically.</summary>
    Applied = 0,

    /// <summary>The exact prior mutation result was reused without another physical write.</summary>
    Replayed = 1,

    /// <summary>No stored Process aggregate exists for the requested instance.</summary>
    NotFound = 2,

    /// <summary>A Process aggregate already exists and another initialization cannot replace it.</summary>
    AlreadyExists = 3,

    /// <summary>The expected physical compare-and-swap revision is stale.</summary>
    RevisionConflict = 4,

    /// <summary>Another live worker owns the Process aggregate.</summary>
    LeaseHeld = 5,

    /// <summary>The supplied worker fence has been superseded.</summary>
    StaleFence = 6,

    /// <summary>The supplied worker lease expired before the mutation boundary.</summary>
    LeaseExpired = 7,

    /// <summary>A stable commit or emission identity was reused for different canonical content.</summary>
    IdentityConflict = 8,

    /// <summary>A local mutation failed its expected version or write-once identity contract.</summary>
    LocalMutationConflict = 9
}

/// <summary>Capability evidence advertised by a physical Process durability provider.</summary>
/// <param name="SupportsAtomicAggregateCommit">
/// Whether checkpoint, inbox, outbox, operation ledger, and local mutation evidence can commit all-or-nothing.
/// </param>
/// <param name="SupportsCompareAndSwap">Whether physical checkpoint revision checks are enforced.</param>
/// <param name="SupportsWorkerFencing">Whether expired ownership can be reclaimed with a greater fence.</param>
/// <param name="MaxCommitItems">Optional provider limit across one atomic commit.</param>
/// <param name="MaxCommitBytes">Optional provider byte limit across one atomic commit.</param>
public sealed record ProcessDurableStoreCapabilities(
    bool SupportsAtomicAggregateCommit,
    bool SupportsCompareAndSwap,
    bool SupportsWorkerFencing,
    int? MaxCommitItems = null,
    long? MaxCommitBytes = null);

/// <summary>One provider-neutral local value mutation composed with a Process checkpoint commit.</summary>
public sealed record ProcessLocalMutation
{
    /// <summary>Creates a local value mutation.</summary>
    /// <param name="identity">Stable write-once mutation identity.</param>
    /// <param name="resource">Provider-neutral local resource identity.</param>
    /// <param name="value">Materialized portable replacement value.</param>
    /// <param name="expectedVersion">Expected current version; zero requires an absent resource.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="identity"/> or <paramref name="resource"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity is empty, <paramref name="value"/> is unknown or failed, or
    /// <paramref name="expectedVersion"/> is negative.
    /// </exception>
    public ProcessLocalMutation(
        string identity,
        string resource,
        PortableValue value,
        long? expectedVersion = null)
    {
        Identity = Guard.RequireNotNullOrWhiteSpace(identity);
        Resource = Guard.RequireNotNullOrWhiteSpace(resource);
        Value = value ?? throw new ArgumentNullException(nameof(value));
        if (value.State is PortableValueState.Unknown or PortableValueState.Failed)
        {
            throw new ArgumentException("A local mutation requires a materialized portable value.", nameof(value));
        }

        if (expectedVersion < 0)
        {
            throw new ArgumentException("An expected local resource version cannot be negative.", nameof(expectedVersion));
        }

        ExpectedVersion = expectedVersion;
    }

    /// <summary>Stable write-once mutation identity.</summary>
    public string Identity { get; }

    /// <summary>Provider-neutral local resource identity.</summary>
    public string Resource { get; }

    /// <summary>Materialized portable replacement value.</summary>
    public PortableValue Value { get; }

    /// <summary>Expected current version; zero requires an absent resource.</summary>
    public long? ExpectedVersion { get; }
}

/// <summary>Durable local value and version observed in a Process-store snapshot.</summary>
/// <param name="Resource">Provider-neutral local resource identity.</param>
/// <param name="Version">Positive committed resource version.</param>
/// <param name="Value">Materialized portable value.</param>
/// <param name="MutationIdentity">Write-once mutation identity that produced this version.</param>
public sealed record ProcessLocalState(
    string Resource,
    long Version,
    PortableValue Value,
    string MutationIdentity);

/// <summary>One complete atomic Process checkpoint commit request.</summary>
public sealed class ProcessDurableCommit
{
    /// <summary>Creates a complete atomic commit intent.</summary>
    /// <param name="id">Stable commit identity reused after an ambiguous outcome.</param>
    /// <param name="expectedRevision">Physical revision read before interpreting the activation.</param>
    /// <param name="owner">Worker that owns the supplied fence.</param>
    /// <param name="fence">Exact current worker fence.</param>
    /// <param name="checkpoint">Complete replacement checkpoint.</param>
    /// <param name="localMutations">Provider-neutral local values to commit with the checkpoint.</param>
    /// <param name="observedAtUtc">UTC commit-boundary observation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="owner"/> or <paramref name="checkpoint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A commit identity, expected revision, fence, or <paramref name="owner"/> is empty; local mutation identities
    /// or resources are duplicated; the commit time is not UTC; or the replacement checkpoint records another
    /// update time.
    /// </exception>
    public ProcessDurableCommit(
        ProcessCommitId id,
        ProcessStorageRevision expectedRevision,
        string owner,
        ProcessWorkerFence fence,
        ProcessDurableCheckpoint checkpoint,
        ImmutableArray<ProcessLocalMutation> localMutations,
        DateTimeOffset observedAtUtc)
    {
        ProcessCheckpointRequirements.RequireIdentity(id.Value, nameof(id));
        ProcessCheckpointRequirements.RequireIdentity(expectedRevision.Value, nameof(expectedRevision));
        ProcessCheckpointRequirements.RequireIdentity(fence.Value, nameof(fence));
        Id = id;
        ExpectedRevision = expectedRevision;
        Owner = Guard.RequireNotNullOrWhiteSpace(owner);
        Fence = fence;
        Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        ProcessCheckpointRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (checkpoint.UpdatedAtUtc != observedAtUtc)
        {
            throw new ArgumentException("Replacement checkpoint and commit must record one atomic update time.", nameof(checkpoint));
        }

        var normalized = localMutations.IsDefault ? [] : localMutations;
        HashSet<string> identities = new(StringComparer.Ordinal);
        HashSet<string> resources = new(StringComparer.Ordinal);
        foreach (var mutation in normalized)
        {
            if (mutation is null)
            {
                throw new ArgumentException("Local mutations cannot contain null entries.", nameof(localMutations));
            }

            if (!identities.Add(mutation.Identity))
            {
                throw new ArgumentException($"Local mutation identity '{mutation.Identity}' is duplicated.", nameof(localMutations));
            }

            if (!resources.Add(mutation.Resource))
            {
                throw new ArgumentException($"Local resource '{mutation.Resource}' is written more than once.", nameof(localMutations));
            }
        }

        LocalMutations = normalized
            .OrderBy(static mutation => mutation.Resource, StringComparer.Ordinal)
            .ToImmutableArray();
        ObservedAtUtc = observedAtUtc;
        Fingerprint = ProcessDurableCommitFingerprinter.Compute(this);
    }

    /// <summary>Stable commit identity reused after an ambiguous outcome.</summary>
    public ProcessCommitId Id { get; }

    /// <summary>Physical revision read before interpreting the activation.</summary>
    public ProcessStorageRevision ExpectedRevision { get; }

    /// <summary>Worker that owns the supplied fence.</summary>
    public string Owner { get; }

    /// <summary>Exact current worker fence.</summary>
    public ProcessWorkerFence Fence { get; }

    /// <summary>Complete replacement checkpoint.</summary>
    public ProcessDurableCheckpoint Checkpoint { get; }

    /// <summary>Provider-neutral local values to commit with the checkpoint.</summary>
    public ImmutableArray<ProcessLocalMutation> LocalMutations { get; }

    /// <summary>UTC commit-boundary observation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Deterministic fingerprint of the entire commit intent.</summary>
    public ProcessCommitFingerprint Fingerprint { get; }
}

/// <summary>Immutable view of one atomically stored Process aggregate.</summary>
public sealed record ProcessDurableStoreSnapshot
{
    /// <summary>Creates a durable store snapshot.</summary>
    /// <param name="checkpoint">Complete Process checkpoint.</param>
    /// <param name="revision">Current physical compare-and-swap revision.</param>
    /// <param name="workerLease">Current or expired worker ownership evidence.</param>
    /// <param name="localState">Local value state ordered by resource identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="revision"/> is default, or local state is null, duplicated, malformed, or unordered.
    /// </exception>
    public ProcessDurableStoreSnapshot(
        ProcessDurableCheckpoint checkpoint,
        ProcessStorageRevision revision,
        ProcessWorkerLease? workerLease,
        ImmutableArray<ProcessLocalState> localState)
    {
        Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        ProcessCheckpointRequirements.RequireIdentity(revision.Value, nameof(revision));
        Revision = revision;
        WorkerLease = workerLease;
        var normalized = localState.IsDefault ? [] : localState;
        string? prior = null;
        foreach (var state in normalized)
        {
            if (state is null
                || state.Version <= 0
                || string.IsNullOrWhiteSpace(state.Resource)
                || string.IsNullOrWhiteSpace(state.MutationIdentity)
                || state.Value is null
                || state.Value.State is PortableValueState.Unknown or PortableValueState.Failed
                || (prior is not null && StringComparer.Ordinal.Compare(prior, state.Resource) >= 0))
            {
                throw new ArgumentException("Local state must be valid, unique, and ordered by resource identity.", nameof(localState));
            }
            prior = state.Resource;
        }
        LocalState = normalized;
    }

    /// <summary>Complete Process checkpoint.</summary>
    public ProcessDurableCheckpoint Checkpoint { get; }

    /// <summary>Current physical compare-and-swap revision.</summary>
    public ProcessStorageRevision Revision { get; }

    /// <summary>Current or expired worker ownership evidence.</summary>
    public ProcessWorkerLease? WorkerLease { get; }

    /// <summary>Local value state ordered by resource identity.</summary>
    public ImmutableArray<ProcessLocalState> LocalState { get; }
}

/// <summary>Observable result of one Process-store mutation.</summary>
/// <param name="Disposition">Physical mutation disposition.</param>
/// <param name="Snapshot">Current immutable aggregate snapshot, when the instance exists.</param>
public sealed record ProcessStoreMutationResult(
    ProcessStoreMutationDisposition Disposition,
    ProcessDurableStoreSnapshot? Snapshot);

/// <summary>Atomic provider-neutral persistence port for durable Process aggregates.</summary>
public interface IProcessDurableStore
{
    /// <summary>Physical capabilities and limits of this provider.</summary>
    ProcessDurableStoreCapabilities Capabilities { get; }

    /// <summary>Loads the latest coherent aggregate snapshot.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="instanceId">Logical Process instance.</param>
    /// <returns>The latest snapshot, or null when the instance is absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="instanceId"/> is default.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled before reading.</exception>
    Task<ProcessDurableStoreSnapshot?> LoadAsync(OperationContext context, ProcessInstanceId instanceId);

    /// <summary>Creates the initial aggregate and start/checkpoint evidence atomically.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="commitId">Stable initialization identity.</param>
    /// <param name="checkpoint">Complete initial checkpoint.</param>
    /// <returns>Creation, replay, conflict, or already-exists evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="checkpoint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="commitId"/> is default.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled before the atomic boundary.</exception>
    Task<ProcessStoreMutationResult> InitializeAsync(
        OperationContext context,
        ProcessCommitId commitId,
        ProcessDurableCheckpoint checkpoint);

    /// <summary>Durably admits one exact interaction input without requiring a live Process worker.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="instanceId">Logical Process instance.</param>
    /// <param name="input">Exact canonical input.</param>
    /// <param name="admittedAtUtc">UTC durable-admission time.</param>
    /// <returns>Admission, replay, missing-instance, or identity-conflict evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="input"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity is default or <paramref name="admittedAtUtc"/> is not UTC.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled before the atomic boundary.</exception>
    Task<ProcessStoreMutationResult> AdmitInputAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        ProcessActivationInput input,
        DateTimeOffset admittedAtUtc);

    /// <summary>Acquires or reclaims leased and fenced activation ownership.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="instanceId">Logical Process instance.</param>
    /// <param name="expectedRevision">
    /// Physical revision whose checkpoint passed compatibility admission before this ownership request.
    /// </param>
    /// <param name="owner">Stable physical worker identity.</param>
    /// <param name="leaseDuration">Strictly positive ownership lifetime.</param>
    /// <param name="observedAtUtc">UTC claim observation.</param>
    /// <returns>Acquisition, replay, revision-conflict, held-lease, or missing-instance evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="owner"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The instance or expected-revision identity is default, <paramref name="owner"/> is empty,
    /// <paramref name="leaseDuration"/> is not positive, <paramref name="observedAtUtc"/> is not UTC, or the
    /// observation predates retained aggregate or lease evidence.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled before the atomic boundary.</exception>
    /// <remarks>
    /// An exact retry of a committed acquisition may replay from its retained owner and lease-time evidence even
    /// after another compatible aggregate mutation advances the physical revision. Every acquisition that would
    /// create or replace a lease requires <paramref name="expectedRevision"/> to match the current aggregate
    /// revision.
    /// </remarks>
    Task<ProcessStoreMutationResult> AcquireWorkerAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        ProcessStorageRevision expectedRevision,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset observedAtUtc);

    /// <summary>Renews an exact live worker fence without changing its value.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="instanceId">Logical Process instance.</param>
    /// <param name="owner">Stable physical worker identity.</param>
    /// <param name="fence">Exact worker fence to renew.</param>
    /// <param name="leaseDuration">Strictly positive replacement lifetime.</param>
    /// <param name="observedAtUtc">UTC renewal observation.</param>
    /// <returns>Renewal, replay/subsumption, stale-fence, expired-lease, or missing-instance evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="owner"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// An identity is default, <paramref name="owner"/> is empty, <paramref name="leaseDuration"/> is not positive,
    /// <paramref name="observedAtUtc"/> is not UTC, or the observation predates retained aggregate or lease
    /// evidence.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled before the atomic boundary.</exception>
    /// <remarks>
    /// An exact retry replays when retained lease evidence proves the requested renewal, including when a later
    /// same-fence renewal or compatible aggregate mutation already subsumes its requested expiry.
    /// </remarks>
    Task<ProcessStoreMutationResult> RenewWorkerAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        string owner,
        ProcessWorkerFence fence,
        TimeSpan leaseDuration,
        DateTimeOffset observedAtUtc);

    /// <summary>Commits a complete replacement checkpoint and local mutations atomically under CAS and fencing.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="commit">Complete deterministic commit intent.</param>
    /// <returns>Commit, replay, revision, fence, lease, identity, or local mutation evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="commit"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled before the atomic boundary.</exception>
    /// <remarks>
    /// The provider must evaluate worker-lease liveness at its physical commit boundary using the current time
    /// exposed by <paramref name="context"/> (or a stricter provider-owned clock). <see cref="ProcessDurableCommit.ObservedAtUtc"/>
    /// is deterministic checkpoint evidence and must never substitute for that fresh fencing observation.
    /// </remarks>
    Task<ProcessStoreMutationResult> CommitAsync(OperationContext context, ProcessDurableCommit commit);
}

static class ProcessDurableCommitFingerprinter
{
    static readonly System.Text.Json.JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    internal static ProcessCommitFingerprint Compute(ProcessDurableCommit commit)
    {
        var content = new ProcessDurableCommitContent(
            commit.Id,
            commit.ExpectedRevision,
            commit.Owner,
            commit.Fence,
            commit.Checkpoint,
            commit.LocalMutations,
            commit.ObservedAtUtc);
        var bytes = StrictDocumentJson.GetCanonicalBytes(content, Options);
        var digest = SHA256.HashData(bytes);
        return new($"sha256-v1:{Convert.ToHexStringLower(digest)}");
    }

    internal static ProcessCommitFingerprint ComputeCheckpoint(ProcessDurableCheckpoint checkpoint)
    {
        var bytes = ProcessDurableCheckpointJsonSerializer.GetCanonicalBytes(checkpoint);
        var digest = SHA256.HashData(bytes);
        return new($"sha256-v1:{Convert.ToHexStringLower(digest)}");
    }

    sealed record ProcessDurableCommitContent(
        ProcessCommitId Id,
        ProcessStorageRevision ExpectedRevision,
        string Owner,
        ProcessWorkerFence Fence,
        ProcessDurableCheckpoint Checkpoint,
        ImmutableArray<ProcessLocalMutation> LocalMutations,
        DateTimeOffset ObservedAtUtc);
}
