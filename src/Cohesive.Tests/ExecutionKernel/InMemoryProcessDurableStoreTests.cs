using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class InMemoryProcessDurableStoreTests
{
    static readonly DateTimeOffset StartedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc;

    static OperationContext Context { get; } = OperationContext.Create();

    [Fact]
    public void WorkerLease_IsLiveOnlyFromClaimThroughExclusiveExpiry()
    {
        var claimedAtUtc = StartedAtUtc.AddMinutes(1);
        var expiresAtUtc = claimedAtUtc.AddMinutes(5);
        var lease = new ProcessWorkerLease(
            "worker/a",
            new("1"),
            claimedAtUtc,
            claimedAtUtc.AddMinutes(1),
            expiresAtUtc);

        Assert.False(lease.IsLive(claimedAtUtc.AddTicks(-1)));
        Assert.True(lease.IsLive(claimedAtUtc));
        Assert.True(lease.IsLive(expiresAtUtc.AddTicks(-1)));
        Assert.False(lease.IsLive(expiresAtUtc));
    }

    [Fact]
    public async Task Initialize_ExactIntentReplaysAndIdentityReuseConflicts()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("initialize");
        ProcessCommitId commitId = new("commit/initialize");

        var applied = await store.InitializeAsync(Context, commitId, checkpoint);
        var replayed = await store.InitializeAsync(Context, commitId, Checkpoint("initialize"));
        var conflicted = await store.InitializeAsync(
            Context,
            commitId,
            Checkpoint("initialize", StartedAtUtc.AddMinutes(1)));
        var alreadyExists = await store.InitializeAsync(
            Context,
            new("commit/initialize/other"),
            checkpoint);

        Assert.Equal(ProcessStoreMutationDisposition.Applied, applied.Disposition);
        Assert.Equal(ProcessStorageRevision.Initial, applied.Snapshot!.Revision);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, replayed.Disposition);
        Assert.Equal(ProcessStorageRevision.Initial, replayed.Snapshot!.Revision);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, conflicted.Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.AlreadyExists, alreadyExists.Disposition);
        Assert.Equal(ProcessStorageRevision.Initial, alreadyExists.Snapshot!.Revision);
        Assert.Equal(StartedAtUtc, alreadyExists.Snapshot.Checkpoint.UpdatedAtUtc);
    }

    [Fact]
    public async Task WorkerLease_AcquireReplayHeldRenewAndReclaimAdvanceMonotonicFence()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("worker-lease");
        await InitializeAsync(store, checkpoint);
        var acquiredAt = StartedAtUtc.AddMinutes(1);

        var acquired = await store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            ProcessStorageRevision.Initial,
            "worker/a",
            TimeSpan.FromMinutes(5),
            acquiredAt);
        var firstLease = acquired.Snapshot!.WorkerLease!;
        var replayed = await store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            acquired.Snapshot!.Revision,
            "worker/a",
            TimeSpan.FromMinutes(20),
            acquiredAt.AddMinutes(1));
        var held = await store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            acquired.Snapshot!.Revision,
            "worker/b",
            TimeSpan.FromMinutes(5),
            acquiredAt.AddMinutes(1));
        var renewedAt = acquiredAt.AddMinutes(2);
        var renewed = await store.RenewWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            "worker/a",
            firstLease.Fence,
            TimeSpan.FromMinutes(10),
            renewedAt);
        var renewalReplay = await store.RenewWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            "worker/a",
            firstLease.Fence,
            TimeSpan.FromMinutes(10),
            renewedAt);
        var delayedRenewal = await store.RenewWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            "worker/a",
            firstLease.Fence,
            TimeSpan.FromMinutes(10),
            renewedAt.AddSeconds(-1));
        var reclaimedAt = renewedAt.AddMinutes(10);
        var reclaimed = await store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            renewed.Snapshot!.Revision,
            "worker/b",
            TimeSpan.FromMinutes(5),
            reclaimedAt);
        var staleRenewal = await store.RenewWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            "worker/a",
            firstLease.Fence,
            TimeSpan.FromMinutes(5),
            reclaimedAt.AddMinutes(1));

        Assert.Equal(ProcessStoreMutationDisposition.Applied, acquired.Disposition);
        Assert.Equal(2, acquired.Snapshot.Revision.Ordinal);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, replayed.Disposition);
        Assert.Equal(firstLease, replayed.Snapshot!.WorkerLease);
        Assert.Equal(acquired.Snapshot.Revision, replayed.Snapshot.Revision);
        Assert.Equal(ProcessStoreMutationDisposition.LeaseHeld, held.Disposition);
        Assert.Equal(acquired.Snapshot.Revision, held.Snapshot!.Revision);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, renewed.Disposition);
        Assert.Equal(firstLease.Fence, renewed.Snapshot!.WorkerLease!.Fence);
        Assert.Equal(firstLease.ClaimedAtUtc, renewed.Snapshot.WorkerLease.ClaimedAtUtc);
        Assert.Equal(renewedAt, renewed.Snapshot.WorkerLease.RenewedAtUtc);
        Assert.Equal(renewedAt.AddMinutes(10), renewed.Snapshot.WorkerLease.ExpiresAtUtc);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, renewalReplay.Disposition);
        Assert.Equal(renewed.Snapshot.Revision, renewalReplay.Snapshot!.Revision);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, delayedRenewal.Disposition);
        Assert.Equal(renewed.Snapshot.Revision, delayedRenewal.Snapshot?.Revision);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, reclaimed.Disposition);
        Assert.Equal("worker/b", reclaimed.Snapshot!.WorkerLease!.Owner);
        Assert.True(reclaimed.Snapshot.WorkerLease.Fence.Ordinal > firstLease.Fence.Ordinal);
        Assert.Equal(renewed.Snapshot.Revision.Ordinal + 1, reclaimed.Snapshot.Revision.Ordinal);
        Assert.Equal(ProcessStoreMutationDisposition.StaleFence, staleRenewal.Disposition);
        Assert.Equal(reclaimed.Snapshot.Revision, staleRenewal.Snapshot!.Revision);
    }

    [Fact]
    public async Task WorkerLease_AcquireWithStaleExpectedRevisionDoesNotChangeLeaseOrRevision()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("worker-acquisition-stale-revision");
        var initialized = await store.InitializeAsync(
            Context,
            new("commit/initialize/worker-acquisition-stale-revision"),
            checkpoint);
        var initializedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var input = Input(checkpoint, "emission/input/before-worker-acquisition", "new-checkpoint-evidence");
        var admitted = await store.AdmitInputAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            input,
            StartedAtUtc.AddMinutes(1));
        var current = Assert.IsType<ProcessDurableStoreSnapshot>(admitted.Snapshot);

        var rejected = await store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            initializedSnapshot.Revision,
            "worker/stale-reader",
            TimeSpan.FromMinutes(5),
            StartedAtUtc.AddMinutes(2));
        var retained = await store.LoadAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId);

        Assert.Equal(ProcessStoreMutationDisposition.RevisionConflict, rejected.Disposition);
        Assert.Equal(current.Revision, rejected.Snapshot!.Revision);
        Assert.Null(rejected.Snapshot.WorkerLease);
        Assert.Equal(current.Revision, retained!.Revision);
        Assert.Null(retained.WorkerLease);
        Assert.Equal(current.Checkpoint, retained.Checkpoint);
    }

    [Fact]
    public async Task WorkerLease_AcquisitionAndRenewalRejectObservationsBeforeDurableChronology()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("worker-lease-chronology");
        await InitializeAsync(store, checkpoint);
        var beforeCheckpoint = await Assert.ThrowsAsync<ArgumentException>(() => store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            ProcessStorageRevision.Initial,
            "worker/a",
            TimeSpan.FromMinutes(10),
            checkpoint.UpdatedAtUtc.AddTicks(-1)));
        var acquiredAtUtc = checkpoint.UpdatedAtUtc.AddMinutes(1);
        var acquired = await store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            ProcessStorageRevision.Initial,
            "worker/a",
            TimeSpan.FromMinutes(10),
            acquiredAtUtc);
        var renewedAtUtc = acquiredAtUtc.AddMinutes(2);
        var renewed = await store.RenewWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            "worker/a",
            acquired.Snapshot!.WorkerLease!.Fence,
            TimeSpan.FromMinutes(10),
            renewedAtUtc);
        var renewedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(renewed.Snapshot);
        var beforeRenewal = renewedAtUtc.AddTicks(-1);
        var staleAcquisition = await Assert.ThrowsAsync<ArgumentException>(() => store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            renewedSnapshot.Revision,
            "worker/b",
            TimeSpan.FromMinutes(10),
            beforeRenewal));
        var subsumedRenewal = await store.RenewWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            "worker/a",
            renewedSnapshot.WorkerLease!.Fence,
            TimeSpan.FromMinutes(10),
            beforeRenewal);
        var retained = await store.LoadAsync(Context, checkpoint.ContinuationIdentity.ProcessInstanceId);

        Assert.Equal("observedAtUtc", beforeCheckpoint.ParamName);
        Assert.Equal("observedAtUtc", staleAcquisition.ParamName);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, subsumedRenewal.Disposition);
        Assert.Equal(renewedSnapshot.Revision, subsumedRenewal.Snapshot?.Revision);
        Assert.Equal(renewedSnapshot.Revision, retained!.Revision);
        Assert.Equal(renewedSnapshot.WorkerLease, retained.WorkerLease);
    }

    [Fact]
    public async Task AdmitInput_ExactRetryReplaysButSameEmissionWithChangedContentConflicts()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("input-replay");
        await InitializeAsync(store, checkpoint);
        var instance = checkpoint.ContinuationIdentity.ProcessInstanceId;
        var input = Input(checkpoint, "emission/input/replay", "original");

        var admitted = await store.AdmitInputAsync(
            Context,
            instance,
            input,
            StartedAtUtc.AddMinutes(1));
        var replayed = await store.AdmitInputAsync(
            Context,
            instance,
            Input(checkpoint, "emission/input/replay", "original"),
            StartedAtUtc.AddMinutes(2));
        var conflicted = await store.AdmitInputAsync(
            Context,
            instance,
            Input(checkpoint, "emission/input/replay", "changed"),
            StartedAtUtc.AddMinutes(2));

        Assert.Equal(ProcessStoreMutationDisposition.Applied, admitted.Disposition);
        Assert.Equal(2, admitted.Snapshot!.Revision.Ordinal);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, replayed.Disposition);
        Assert.Equal(admitted.Snapshot.Revision, replayed.Snapshot!.Revision);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, conflicted.Disposition);
        Assert.Equal(admitted.Snapshot.Revision, conflicted.Snapshot!.Revision);
        Assert.Equal(checkpoint.Inbox.Length + 1, conflicted.Snapshot.Checkpoint.Inbox.Length);
        var retained = Assert.Single(
            conflicted.Snapshot.Checkpoint.Inbox,
            entry => entry.EmissionId == input.Envelope.Context.EmissionId);
        Assert.Equal(input, retained.Input);
        Assert.Equal(StartedAtUtc.AddMinutes(1), retained.AdmittedAtUtc);
    }

    [Fact]
    public async Task InputAdmissionAfterWorkerLoad_InvalidatesCommitWithoutLosingWakeup()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("lost-wakeup");
        var loaded = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            "worker/a",
            StartedAtUtc.AddMinutes(1),
            TimeSpan.FromHours(1));
        var input = Input(checkpoint, "emission/input/racing");
        var admittedAt = StartedAtUtc.AddMinutes(2);

        var admitted = await store.AdmitInputAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            input,
            admittedAt);
        var staleWorkerCommit = Commit(
            "commit/loaded-before-input",
            loaded,
            "worker/a",
            StartedAtUtc.AddMinutes(3));
        var rejected = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, staleWorkerCommit);
        var current = await store.LoadAsync(Context, checkpoint.ContinuationIdentity.ProcessInstanceId);

        Assert.Equal(ProcessStoreMutationDisposition.Applied, admitted.Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.RevisionConflict, rejected.Disposition);
        Assert.Equal(admitted.Snapshot!.Revision, rejected.Snapshot!.Revision);
        Assert.Equal(admitted.Snapshot.Revision, current!.Revision);
        Assert.Equal(checkpoint.Inbox.Length + 1, current.Checkpoint.Inbox.Length);
        var retained = Assert.Single(
            current.Checkpoint.Inbox,
            entry => entry.EmissionId == input.Envelope.Context.EmissionId);
        Assert.Equal(input, retained.Input);
        Assert.Equal(admittedAt, retained.AdmittedAtUtc);
    }

    [Fact]
    public async Task Commit_RejectsSupersededWorkerFenceBeforePublishing()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("stale-fence");
        var first = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            "worker/a",
            StartedAtUtc.AddMinutes(1),
            TimeSpan.FromMinutes(1));
        var reclaimed = await store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            first.Revision,
            "worker/b",
            TimeSpan.FromMinutes(10),
            StartedAtUtc.AddMinutes(2));
        var stale = Commit(
            "commit/stale-fence",
            reclaimed.Snapshot!,
            "worker/a",
            StartedAtUtc.AddMinutes(3),
            fence: first.WorkerLease!.Fence);

        var rejected = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, stale);
        var reclaimedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(reclaimed.Snapshot);

        Assert.Equal(ProcessStoreMutationDisposition.StaleFence, rejected.Disposition);
        Assert.Equal(reclaimedSnapshot.Revision, rejected.Snapshot!.Revision);
        Assert.Equal("worker/b", rejected.Snapshot.WorkerLease!.Owner);
        Assert.Empty(rejected.Snapshot.LocalState);
    }

    [Fact]
    public async Task Commit_RejectsExpiredMatchingLeaseBeforePublishing()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("expired-lease");
        var acquiredAt = StartedAtUtc.AddMinutes(1);
        var ready = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            "worker/a",
            acquiredAt,
            TimeSpan.FromMinutes(1));
        var expired = Commit(
            "commit/expired-lease",
            ready,
            "worker/a",
            acquiredAt.AddMinutes(1));

        var rejected = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, expired);

        Assert.Equal(ProcessStoreMutationDisposition.LeaseExpired, rejected.Disposition);
        Assert.Equal(ready.Revision, rejected.Snapshot!.Revision);
        Assert.Equal(checkpoint.UpdatedAtUtc, rejected.Snapshot.Checkpoint.UpdatedAtUtc);
    }

    [Fact]
    public async Task Commit_UsesFreshPhysicalObservationRatherThanRetainedCommitEvidenceForLeaseLiveness()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("physical-lease-observation");
        var acquiredAtUtc = StartedAtUtc.AddMinutes(1);
        var ready = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            "worker/a",
            acquiredAtUtc,
            TimeSpan.FromMinutes(10));
        var commit = Commit(
            "commit/physical-lease-observation",
            ready,
            "worker/a",
            acquiredAtUtc.AddMinutes(2));

        var rejected = await store.CommitAsync(
            DurableOperationTestFixture.ContextAt(acquiredAtUtc.AddMinutes(11)),
            commit);

        Assert.Equal(ProcessStoreMutationDisposition.LeaseExpired, rejected.Disposition);
        Assert.Equal(ready.Revision, rejected.Snapshot?.Revision);
    }

    [Fact]
    public async Task Commit_RejectsMatchingLeaseBeforeItsClaimBeforePublishing()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("pre-claim-lease");
        var acquiredAt = StartedAtUtc.AddMinutes(1);
        var ready = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            "worker/a",
            acquiredAt,
            TimeSpan.FromMinutes(10));
        var beforeClaim = Commit(
            "commit/pre-claim-lease",
            ready,
            "worker/a",
            acquiredAt.AddTicks(-1));

        var rejected = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, beforeClaim);

        Assert.Equal(ProcessStoreMutationDisposition.LeaseExpired, rejected.Disposition);
        Assert.Equal(ready.Revision, rejected.Snapshot!.Revision);
        Assert.Equal(checkpoint.UpdatedAtUtc, rejected.Snapshot.Checkpoint.UpdatedAtUtc);
    }

    [Fact]
    public async Task Commit_RejectsMatchingLeaseBeforeItsLatestRenewalBeforePublishing()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("pre-renewal-lease");
        var acquiredAt = StartedAtUtc.AddMinutes(1);
        var acquired = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            "worker/a",
            acquiredAt,
            TimeSpan.FromMinutes(10));
        var renewedAt = acquiredAt.AddMinutes(2);
        var renewed = await store.RenewWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            "worker/a",
            acquired.WorkerLease!.Fence,
            TimeSpan.FromMinutes(10),
            renewedAt);
        var ready = Assert.IsType<ProcessDurableStoreSnapshot>(renewed.Snapshot);
        var beforeRenewal = Commit(
            "commit/pre-renewal-lease",
            ready,
            "worker/a",
            renewedAt.AddTicks(-1));

        var rejected = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, beforeRenewal);

        Assert.Equal(ProcessStoreMutationDisposition.LeaseExpired, rejected.Disposition);
        Assert.Equal(ready.Revision, rejected.Snapshot!.Revision);
        Assert.Equal(checkpoint.UpdatedAtUtc, rejected.Snapshot.Checkpoint.UpdatedAtUtc);
    }

    [Fact]
    public async Task Commit_ExactReplayPrecedesRevisionAndFenceChecksWhileChangedIdentityConflicts()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("commit-replay");
        var acquiredAt = StartedAtUtc.AddMinutes(1);
        var ready = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            "worker/a",
            acquiredAt,
            TimeSpan.FromMinutes(1));
        var exact = Commit(
            "commit/exact",
            ready,
            "worker/a",
            acquiredAt.AddSeconds(30));
        var applied = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, exact);
        var reclaimed = await store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            applied.Snapshot!.Revision,
            "worker/b",
            TimeSpan.FromMinutes(10),
            acquiredAt.AddMinutes(2));
        var reclaimedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(reclaimed.Snapshot);

        var replayed = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, exact);
        var changed = Commit(
            "commit/exact",
            reclaimedSnapshot,
            "worker/b",
            acquiredAt.AddMinutes(3),
            [LocalMutation("mutation/changed", "local/changed", "changed", expectedVersion: 0)]);
        var conflicted = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, changed);

        Assert.Equal(ProcessStoreMutationDisposition.Applied, applied.Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, replayed.Disposition);
        Assert.Equal(applied.Snapshot!.Revision, replayed.Snapshot!.Revision);
        Assert.Equal("worker/a", replayed.Snapshot.WorkerLease!.Owner);
        Assert.Equal(applied.Snapshot.Checkpoint, replayed.Snapshot.Checkpoint);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, conflicted.Disposition);
        Assert.Equal(reclaimedSnapshot.Revision, conflicted.Snapshot!.Revision);
        Assert.Empty(conflicted.Snapshot.LocalState);
    }

    [Fact]
    public async Task LocalMutations_EnforceResourceCASAndWriteOnceIdentityIdempotence()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("local-mutations");
        var current = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            "worker/a",
            StartedAtUtc.AddMinutes(1),
            TimeSpan.FromHours(1));
        var firstMutation = LocalMutation(
            "mutation/resource/1",
            "local/resource",
            "one",
            expectedVersion: 0);

        var first = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(
            store,
            Commit(
                "commit/local/1",
                current,
                "worker/a",
                StartedAtUtc.AddMinutes(2),
                [firstMutation]));
        var idempotent = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(
            store,
            Commit(
                "commit/local/idempotent",
                first.Snapshot!,
                "worker/a",
                StartedAtUtc.AddMinutes(3),
                [firstMutation]));
        var changedIdentity = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(
            store,
            Commit(
                "commit/local/changed-identity",
                idempotent.Snapshot!,
                "worker/a",
                StartedAtUtc.AddMinutes(4),
                [LocalMutation("mutation/resource/1", "local/resource", "changed", expectedVersion: 0)]));
        var staleVersion = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(
            store,
            Commit(
                "commit/local/stale-version",
                idempotent.Snapshot!,
                "worker/a",
                StartedAtUtc.AddMinutes(4),
                [LocalMutation("mutation/resource/2", "local/resource", "two", expectedVersion: 0)]));
        var second = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(
            store,
            Commit(
                "commit/local/2",
                idempotent.Snapshot!,
                "worker/a",
                StartedAtUtc.AddMinutes(5),
                [LocalMutation("mutation/resource/2", "local/resource", "two", expectedVersion: 1)]));

        Assert.Equal(ProcessStoreMutationDisposition.Applied, first.Disposition);
        var firstState = Assert.Single(first.Snapshot!.LocalState);
        Assert.Equal(1, firstState.Version);
        Assert.Equal(firstMutation.Identity, firstState.MutationIdentity);
        Assert.Equal(firstMutation.Value, firstState.Value);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, idempotent.Disposition);
        var idempotentState = Assert.Single(idempotent.Snapshot!.LocalState);
        Assert.Equal(1, idempotentState.Version);
        Assert.Equal(firstMutation.Identity, idempotentState.MutationIdentity);
        Assert.Equal(ProcessStoreMutationDisposition.LocalMutationConflict, changedIdentity.Disposition);
        Assert.Equal(idempotent.Snapshot.Revision, changedIdentity.Snapshot!.Revision);
        Assert.Equal(ProcessStoreMutationDisposition.LocalMutationConflict, staleVersion.Disposition);
        Assert.Equal(idempotent.Snapshot.Revision, staleVersion.Snapshot!.Revision);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, second.Disposition);
        var secondState = Assert.Single(second.Snapshot!.LocalState);
        Assert.Equal(2, secondState.Version);
        Assert.Equal("mutation/resource/2", secondState.MutationIdentity);
        Assert.Equal(StringValue("two"), secondState.Value);
    }

    [Fact]
    public async Task Commit_AcceptsCanonicalSuccessorEvidenceRestoredFromJson()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/restored-successor",
            semanticVariant: "restored-successor");
        var store = new InMemoryProcessDurableStore();
        var ready = await InitializeAndAcquireAsync(
            store,
            fixture.Checkpoint,
            "worker/restored",
            StartedAtUtc.AddMinutes(1),
            TimeSpan.FromHours(1));
        var json = ProcessDurableCheckpointJsonSerializer.Serialize(ready.Checkpoint);
        var restored = ProcessDurableCheckpointJsonSerializer.Deserialize(json, fixture.Plan);
        var committedAtUtc = StartedAtUtc.AddMinutes(2);
        var replacement = WithUpdatedAt(restored, committedAtUtc);
        var commit = new ProcessDurableCommit(
            new("commit/restored-successor"),
            ready.Revision,
            "worker/restored",
            ready.WorkerLease!.Fence,
            replacement,
            [],
            committedAtUtc);

        var result = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, commit);

        Assert.Equal(ProcessStoreMutationDisposition.Applied, result.Disposition);
        Assert.Equal(ready.Revision.Ordinal + 1, result.Snapshot!.Revision.Ordinal);
        Assert.Equal(committedAtUtc, result.Snapshot.Checkpoint.UpdatedAtUtc);
    }

    [Fact]
    public async Task AuthorityDocument_RoundTripsCompleteReplayAndFenceState()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("authority-document");
        var ready = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            owner: "worker/document",
            observedAtUtc: StartedAtUtc.AddMinutes(1),
            leaseDuration: TimeSpan.FromHours(1));
        var commit = Commit(
            id: "commit/authority-document",
            snapshot: ready,
            owner: "worker/document",
            observedAtUtc: StartedAtUtc.AddMinutes(2),
            localMutations:
            [
                LocalMutation(
                    identity: "mutation/authority-document",
                    resource: "local/document",
                    value: "retained",
                    expectedVersion: 0)
            ]);
        var applied = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, commit);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, applied.Disposition);

        var json = ProcessDurableStoreJsonSerializer.Serialize(store.CaptureDocument());
        var restored = new InMemoryProcessDurableStore(
            ProcessDurableStoreJsonSerializer.Deserialize(json));
        var restoredJson = ProcessDurableStoreJsonSerializer.Serialize(restored.CaptureDocument());
        var replayed = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(restored, commit);

        Assert.Equal(json, restoredJson);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, replayed.Disposition);
        Assert.Equal(applied.Snapshot!.Revision, replayed.Snapshot!.Revision);
        Assert.Equal(applied.Snapshot.WorkerLease, replayed.Snapshot.WorkerLease);
        var restoredLocal = Assert.Single(replayed.Snapshot.LocalState);
        Assert.Equal("local/document", restoredLocal.Resource);
        Assert.Equal(1, restoredLocal.Version);
        Assert.Equal("mutation/authority-document", restoredLocal.MutationIdentity);
    }

    [Fact]
    public async Task ConcurrentCommits_PublishOneCompleteWinnerWithoutMixingState()
    {
        var store = new InMemoryProcessDurableStore();
        var checkpoint = Checkpoint("concurrent");
        var loaded = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            "worker/a",
            StartedAtUtc.AddMinutes(1),
            TimeSpan.FromHours(1));
        var mutationA = LocalMutation("mutation/concurrent/a", "local/winner", "a", expectedVersion: 0);
        var mutationB = LocalMutation("mutation/concurrent/b", "local/winner", "b", expectedVersion: 0);
        var commitA = Commit(
            "commit/concurrent/a",
            loaded,
            "worker/a",
            StartedAtUtc.AddMinutes(2),
            [mutationA]);
        var commitB = Commit(
            "commit/concurrent/b",
            loaded,
            "worker/a",
            StartedAtUtc.AddMinutes(3),
            [mutationB]);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var taskA = Task.Run(async () =>
        {
            await start.Task;
            return await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, commitA);
        });
        var taskB = Task.Run(async () =>
        {
            await start.Task;
            return await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, commitB);
        });

        start.SetResult();
        var results = await Task.WhenAll(taskA, taskB);
        var current = await store.LoadAsync(Context, checkpoint.ContinuationIdentity.ProcessInstanceId);
        var aWon = results[0].Disposition == ProcessStoreMutationDisposition.Applied;
        var winnerCommit = aWon ? commitA : commitB;
        var winnerMutation = aWon ? mutationA : mutationB;

        Assert.Single(results, static result => result.Disposition == ProcessStoreMutationDisposition.Applied);
        Assert.Single(results, static result => result.Disposition == ProcessStoreMutationDisposition.RevisionConflict);
        Assert.Equal(loaded.Revision.Ordinal + 1, current!.Revision.Ordinal);
        Assert.Equal(winnerCommit.ObservedAtUtc, current.Checkpoint.UpdatedAtUtc);
        var local = Assert.Single(current.LocalState);
        Assert.Equal(winnerMutation.Identity, local.MutationIdentity);
        Assert.Equal(winnerMutation.Value, local.Value);
    }

    static async Task InitializeAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableCheckpoint checkpoint)
    {
        var result = await store.InitializeAsync(
            Context,
            new($"commit/initialize/{checkpoint.ContinuationIdentity.ProcessInstanceId.Value}"),
            checkpoint);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, result.Disposition);
    }

    static async Task<ProcessDurableStoreSnapshot> InitializeAndAcquireAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableCheckpoint checkpoint,
        string owner,
        DateTimeOffset observedAtUtc,
        TimeSpan leaseDuration)
    {
        await InitializeAsync(store, checkpoint);
        var acquired = await store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            ProcessStorageRevision.Initial,
            owner,
            leaseDuration,
            observedAtUtc);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, acquired.Disposition);
        return Assert.IsType<ProcessDurableStoreSnapshot>(acquired.Snapshot);
    }

    static ProcessDurableCheckpoint Checkpoint(
        string semanticVariant,
        DateTimeOffset? updatedAtUtc = null)
    {
        var checkpoint = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-store/{semanticVariant}",
            semanticVariant: semanticVariant).Checkpoint;
        return updatedAtUtc is { } updated
            ? WithUpdatedAt(checkpoint, updated)
            : checkpoint;
    }

    static ProcessDurableCheckpoint WithUpdatedAt(
        ProcessDurableCheckpoint checkpoint,
        DateTimeOffset updatedAtUtc) =>
        new(
            checkpoint.SchemaVersion,
            checkpoint.Start,
            checkpoint.Continuation,
            checkpoint.Control,
            checkpoint.Activations,
            checkpoint.Operations,
            checkpoint.Inbox,
            checkpoint.Emissions,
            checkpoint.DurableOperations,
            checkpoint.CreatedAtUtc,
            updatedAtUtc);

    static ProcessActivationInput Input(
        ProcessDurableCheckpoint checkpoint,
        string emission,
        string payload = "payload")
    {
        TokenId token = new("token/inbox");
        var envelope = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new(emission),
                new ProcessInteractionOrigin(
                    checkpoint.Definition,
                    new("node/source"),
                    checkpoint.ContinuationIdentity,
                    new("activation/source"),
                    token),
                new("correlation/durable-store-tests"),
                causationId: null,
                ProcessDurabilityTestFixture.Authority,
                new($"idempotency/{emission}"),
                ordering: null,
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                checkpoint.Start.Request.Context.Provenance),
            new(ProcessDurabilityTestFixture.DefinitionReference("event/durable-store", 'e')),
            StringValue(payload));
        return new(
            new ProcessTokenInteractionTarget(checkpoint.ContinuationIdentity, token),
            envelope);
    }

    static ProcessDurableCommit Commit(
        string id,
        ProcessDurableStoreSnapshot snapshot,
        string owner,
        DateTimeOffset observedAtUtc,
        ImmutableArray<ProcessLocalMutation> localMutations = default,
        ProcessStorageRevision? expectedRevision = null,
        ProcessWorkerFence? fence = null,
        ProcessDurableCheckpoint? checkpoint = null) =>
        new(
            new(id),
            expectedRevision ?? snapshot.Revision,
            owner,
            fence ?? snapshot.WorkerLease!.Fence,
            checkpoint ?? WithUpdatedAt(snapshot.Checkpoint, observedAtUtc),
            localMutations,
            observedAtUtc);

    static ProcessLocalMutation LocalMutation(
        string identity,
        string resource,
        string value,
        long? expectedVersion = null) =>
        new(identity, resource, StringValue(value), expectedVersion);

    static PortableValue StringValue(string value) =>
        ProcessDurabilityTestFixture.StringValue(value);
}
