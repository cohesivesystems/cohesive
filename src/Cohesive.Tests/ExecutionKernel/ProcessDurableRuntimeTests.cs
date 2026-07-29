using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessDurableRuntimeTests
{
    static readonly TimeSpan WorkerLease = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task InitializeThenActivate_CommitsOneCoherentAggregate()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/coherent-activation",
            semanticVariant: "coherent-activation");
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);

        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, initialized.Disposition);
        Assert.Empty(before.Checkpoint.Activations);
        Assert.Empty(before.Checkpoint.Operations);
        Assert.Empty(before.Checkpoint.Emissions);
        Assert.Empty(before.Checkpoint.DurableOperations);

        var activated = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan,
            before.Checkpoint.ContinuationIdentity,
            fixture.Activation);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, activated.Disposition);
        Assert.Equal(ProcessActivationDisposition.DurableCut, activated.Decision?.Disposition);
        Assert.Equal(1, host.RelationCalls);
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(activated.Snapshot);
        var checkpoint = snapshot.Checkpoint;
        var activation = Assert.Single(checkpoint.Activations);
        var operation = Assert.Single(checkpoint.Operations);
        var emission = Assert.Single(checkpoint.Emissions);
        var durableOperation = Assert.Single(checkpoint.DurableOperations);
        var request = Assert.IsType<RequestEnvelope>(emission.Envelope);

        Assert.Equal(checkpoint.ContinuationIdentity, activation.Continuation);
        Assert.Equal(fixture.Activation, activation.Activation);
        Assert.Equal(
            ProcessStorageContentFingerprints.Continuation(before.Checkpoint.Continuation),
            activation.BeforeContinuation);
        Assert.Equal(
            ProcessStorageContentFingerprints.Continuation(checkpoint.Continuation),
            activation.AfterContinuation);
        Assert.Equal(checkpoint.Continuation.CompletedActivationCount, activation.Sequence);
        Assert.Equal(fixture.OperationResult, operation.Result);
        Assert.Equal(fixture.Operation.Definition, operation.OperationDefinition);
        Assert.Equal(fixture.Operation.Continuation, operation.Key.Continuation);
        Assert.Equal(fixture.Operation.Activation, operation.Key.Activation);
        Assert.Equal(fixture.Operation.Token, operation.Key.Token);
        Assert.Equal(fixture.Operation.Node, operation.Key.Node);
        Assert.Equal(fixture.Operation.Occurrence, operation.Key.Occurrence);
        Assert.Equal(
            ProcessStorageContentFingerprints.Envelope(fixture.Request),
            ProcessStorageContentFingerprints.Envelope(request));
        Assert.Equal(request.Context.EmissionId, durableOperation.OperationId);
        Assert.Equal(
            ProcessStorageContentFingerprints.Envelope(request),
            ProcessStorageContentFingerprints.Envelope(durableOperation.Request));
        Assert.Equal(fixture.DurableOperation.Binding, durableOperation.Binding);
    }

    [Fact]
    public async Task ExactActivationReplay_DoesNoHostWork_WhileChangedContentConflicts()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/activation-replay",
            semanticVariant: "activation-replay");
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var continuation = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot)
            .Checkpoint
            .ContinuationIdentity;
        var first = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan,
            continuation,
            fixture.Activation);
        var firstSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(first.Snapshot);

        var replay = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1)),
            fixture.Plan,
            continuation,
            fixture.Activation);
        var replaySnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(replay.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Equal(1, host.RelationCalls);
        Assert.Equal(firstSnapshot.Revision, replaySnapshot.Revision);
        Assert.Single(replaySnapshot.Checkpoint.Activations);
        Assert.Single(replaySnapshot.Checkpoint.Operations);
        Assert.Single(replaySnapshot.Checkpoint.Emissions);
        Assert.Single(replaySnapshot.Checkpoint.DurableOperations);

        var conflictingActivation = new ProcessActivation(
            fixture.Activation.Id,
            fixture.Activation.Cause,
            fixture.Activation.ObservedAtUtc.AddSeconds(1),
            fixture.Activation.Context,
            fixture.Activation.Inputs,
            fixture.Activation.Cancellation);
        var conflict = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(2)),
            fixture.Plan,
            continuation,
            conflictingActivation);

        Assert.Equal(ProcessDurableRuntimeDisposition.IdentityConflict, conflict.Disposition);
        Assert.Contains(
            conflict.Diagnostics,
            static diagnostic => diagnostic.Code
                == ProcessDurableRuntimeDiagnosticCodes.ActivationIdentityConflict);
        Assert.Equal(1, host.RelationCalls);
        Assert.Equal(firstSnapshot.Revision, conflict.Snapshot?.Revision);
    }

    [Fact]
    public async Task DuplicateHostEmissionIdentity_IsRejectedWithoutPublishingPartialActivation()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/duplicate-host-emission",
            semanticVariant: "duplicate-host-emission");
        var emission = HostOperationReply(fixture);
        var host = new RecordingHost(ProcessOperationResult.Completed(
            fixture.OperationResult.Value!,
            [emission, emission]));
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);

        var activated = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan,
            before.Checkpoint.ContinuationIdentity,
            fixture.Activation);

        Assert.Equal(ProcessDurableRuntimeDisposition.Rejected, activated.Disposition);
        Assert.Contains(
            activated.Diagnostics,
            static diagnostic => diagnostic.Code
                == ProcessDurableRuntimeDiagnosticCodes.ActivationIdentityConflict
                && diagnostic.Location == "/decision/emissions/1/context/emissionId");
        Assert.Equal(1, host.RelationCalls);
        var retained = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            before.Checkpoint.ContinuationIdentity.ProcessInstanceId));
        Assert.Equal(before.Revision.Next(), retained.Revision);
        Assert.Empty(retained.Checkpoint.Activations);
        Assert.Empty(retained.Checkpoint.Operations);
        Assert.Empty(retained.Checkpoint.Emissions);
        Assert.Empty(retained.Checkpoint.DurableOperations);
    }

    [Theory]
    [InlineData(
        false,
        ProcessDurableRuntimeDisposition.Applied)]
    [InlineData(
        true,
        ProcessDurableRuntimeDisposition.Replayed)]
    public async Task AmbiguousActivationCommit_RetriesExactIntentWithoutDuplicatingLogicalEvidence(
        bool crashAfterCommit,
        ProcessDurableRuntimeDisposition expectedDisposition)
    {
        var crashPhase = crashAfterCommit
            ? ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn
            : ProcessStoreCrashPhase.BeforeAtomicCommit;
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime/ambiguous-{crashPhase}",
            semanticVariant: $"ambiguous-{crashPhase}");
        var crashed = false;
        var store = new InMemoryProcessDurableStore(context =>
        {
            if (crashed
                || context.MutationKind != ProcessStoreMutationKind.AggregateCommit
                || context.Phase != crashPhase)
            {
                return false;
            }

            crashed = true;
            return true;
        });
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var continuation = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot)
            .Checkpoint
            .ContinuationIdentity;

        var result = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan,
            continuation,
            fixture.Activation);
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot);

        Assert.True(crashed);
        Assert.Equal(expectedDisposition, result.Disposition);
        Assert.Equal(1, host.RelationCalls);
        Assert.Single(snapshot.Checkpoint.Activations);
        Assert.Single(snapshot.Checkpoint.Operations);
        Assert.Single(snapshot.Checkpoint.Emissions);
        Assert.Single(snapshot.Checkpoint.DurableOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderCancellationDuringInitialization_RetriesExactMutation(
        bool taskCanceledException)
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime/initialization-provider-cancellation/{taskCanceledException}",
            semanticVariant: $"initialization-provider-cancellation/{taskCanceledException}");
        var store = new ProviderCancellationStore(
            new InMemoryProcessDurableStore(),
            ProviderCancellationMutation.Initialization,
            taskCanceledException);
        var runtime = Runtime(store, fixture, new RecordingHost(fixture.OperationResult));

        var result = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        Assert.Equal(2, store.Initializations.Count);
        Assert.Equal(store.Initializations[0], store.Initializations[1]);
        Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderCancellationDuringCommit_RetriesExactMutation(
        bool taskCanceledException)
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime/commit-provider-cancellation/{taskCanceledException}",
            semanticVariant: $"commit-provider-cancellation/{taskCanceledException}");
        var store = new ProviderCancellationStore(
            new InMemoryProcessDurableStore(),
            ProviderCancellationMutation.Commit,
            taskCanceledException);
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var continuation = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot)
            .Checkpoint.ContinuationIdentity;

        var result = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan,
            continuation,
            fixture.Activation);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        Assert.Equal(2, store.Commits.Count);
        Assert.Equal(store.Commits[0], store.Commits[1]);
        Assert.Equal(1, host.RelationCalls);
        Assert.Single(Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot).Checkpoint.Activations);
    }

    [Fact]
    public async Task CallerCancellationAtStoreBoundary_PropagatesWithoutRetry()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/store-caller-cancellation",
            semanticVariant: "store-caller-cancellation");
        using var cancellation = new CancellationTokenSource();
        var store = new ProviderCancellationStore(
            new InMemoryProcessDurableStore(),
            ProviderCancellationMutation.Initialization,
            taskCanceledException: false,
            callerCancellation: cancellation);
        var runtime = Runtime(store, fixture, new RecordingHost(fixture.OperationResult));
        var context = OperationContext.Create(
            timeProvider: new FixedTimeProvider(ProcessDurabilityTestFixture.AcceptedAtUtc),
            cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.InitializeAsync(
            context,
            fixture.Plan,
            fixture.Start));

        Assert.Single(store.Initializations);
        Assert.Null(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmbiguousWorkerAcquisition_RetriesExactClaimWithoutDuplicatingFenceOrHostWork(
        bool crashAfterAcquisition)
    {
        var crashPhase = crashAfterAcquisition
            ? ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn
            : ProcessStoreCrashPhase.BeforeAtomicCommit;
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime/acquisition-{crashPhase}",
            semanticVariant: $"acquisition-{crashPhase}");
        var crashed = false;
        var innerStore = new InMemoryProcessDurableStore(context =>
        {
            if (crashed
                || context.MutationKind != ProcessStoreMutationKind.WorkerAcquisition
                || context.Phase != crashPhase)
            {
                return false;
            }

            crashed = true;
            return true;
        });
        var store = new AcquisitionRecordingStore(innerStore);
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initializedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);

        var result = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan,
            initializedSnapshot.Checkpoint.ContinuationIdentity,
            fixture.Activation);
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot);
        var lease = Assert.IsType<ProcessWorkerLease>(snapshot.WorkerLease);

        Assert.True(crashed);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        Assert.Equal(2, store.Acquisitions.Count);
        Assert.Equal(store.Acquisitions[0], store.Acquisitions[1]);
        Assert.Equal(initializedSnapshot.Revision.Ordinal + 2, snapshot.Revision.Ordinal);
        Assert.Equal("worker/durable-runtime-tests", lease.Owner);
        Assert.Equal(new ProcessWorkerFence("1"), lease.Fence);
        Assert.Equal(ProcessDurabilityTestFixture.CheckpointedAtUtc, lease.ClaimedAtUtc);
        Assert.Equal(1, host.RelationCalls);
        Assert.Single(snapshot.Checkpoint.Activations);
        Assert.Single(snapshot.Checkpoint.Operations);
        Assert.Single(snapshot.Checkpoint.Emissions);
        Assert.Single(snapshot.Checkpoint.DurableOperations);
    }

    [Fact]
    public async Task SameOwnerLiveLease_IsRenewedWithItsExistingFenceBeforeActivationCommit()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/same-owner-lease-renewal",
            semanticVariant: "same-owner-lease-renewal");
        var innerStore = new InMemoryProcessDurableStore();
        var store = new RenewalTrackingStore(innerStore);
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initializedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var instanceId = initializedSnapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId;
        var claimedAtUtc = ProcessDurabilityTestFixture.AcceptedAtUtc.AddSeconds(1);
        var seeded = await innerStore.AcquireWorkerAsync(
            Context(claimedAtUtc),
            instanceId,
            initializedSnapshot.Revision,
            "worker/durable-runtime-tests",
            WorkerLease,
            claimedAtUtc);
        var seededSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(seeded.Snapshot);
        var seededLease = Assert.IsType<ProcessWorkerLease>(seededSnapshot.WorkerLease);
        var renewedAtUtc = seededLease.ExpiresAtUtc.AddSeconds(-1);

        var result = await runtime.ActivateAsync(
            Context(renewedAtUtc),
            fixture.Plan,
            initializedSnapshot.Checkpoint.ContinuationIdentity,
            fixture.Activation);
        var committed = Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot);
        var renewedLease = Assert.IsType<ProcessWorkerLease>(committed.WorkerLease);
        var renewal = Assert.Single(store.Renewals);

        Assert.Equal(ProcessStoreMutationDisposition.Applied, seeded.Disposition);
        Assert.True(seededLease.IsLive(renewedAtUtc));
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        Assert.Equal(instanceId, renewal.InstanceId);
        Assert.Equal(seededLease.Owner, renewal.Owner);
        Assert.Equal(seededLease.Fence, renewal.Fence);
        Assert.Equal(WorkerLease, renewal.LeaseDuration);
        Assert.Equal(renewedAtUtc, renewal.ObservedAtUtc);
        Assert.Equal(seededLease.Fence, renewedLease.Fence);
        Assert.Equal(seededLease.ClaimedAtUtc, renewedLease.ClaimedAtUtc);
        Assert.Equal(renewedAtUtc, renewedLease.RenewedAtUtc);
        Assert.Equal(renewedAtUtc.Add(WorkerLease), renewedLease.ExpiresAtUtc);
        Assert.Equal(seededSnapshot.Revision.Ordinal + 2, committed.Revision.Ordinal);
        Assert.Equal(1, store.CommitCalls);
        Assert.Equal(1, host.RelationCalls);
        Assert.Single(committed.Checkpoint.Activations);
    }

    [Theory]
    [InlineData(ProcessStoreMutationDisposition.StaleFence, ProcessDurableRuntimeDisposition.StaleFence)]
    [InlineData(ProcessStoreMutationDisposition.LeaseExpired, ProcessDurableRuntimeDisposition.LeaseExpired)]
    public async Task SameOwnerLeaseRenewalFailure_StopsBeforeHostWorkOrCommit(
        ProcessStoreMutationDisposition storeDisposition,
        ProcessDurableRuntimeDisposition runtimeDisposition)
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime/lease-renewal-{storeDisposition}",
            semanticVariant: $"lease-renewal-{storeDisposition}");
        var innerStore = new InMemoryProcessDurableStore();
        var store = new RenewalTrackingStore(innerStore, storeDisposition);
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initializedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var instanceId = initializedSnapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId;
        var claimedAtUtc = ProcessDurabilityTestFixture.AcceptedAtUtc.AddSeconds(1);
        var seeded = await innerStore.AcquireWorkerAsync(
            Context(claimedAtUtc),
            instanceId,
            initializedSnapshot.Revision,
            "worker/durable-runtime-tests",
            WorkerLease,
            claimedAtUtc);
        var seededSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(seeded.Snapshot);
        var seededLease = Assert.IsType<ProcessWorkerLease>(seededSnapshot.WorkerLease);
        var renewalAtUtc = seededLease.ExpiresAtUtc.AddSeconds(-1);

        var result = await runtime.ActivateAsync(
            Context(renewalAtUtc),
            fixture.Plan,
            initializedSnapshot.Checkpoint.ContinuationIdentity,
            fixture.Activation);
        var retained = Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot);
        var renewal = Assert.Single(store.Renewals);

        Assert.Equal(runtimeDisposition, result.Disposition);
        Assert.Equal(seededSnapshot.Revision, retained.Revision);
        Assert.Equal(seededLease.Fence, renewal.Fence);
        Assert.Equal(renewalAtUtc, renewal.ObservedAtUtc);
        Assert.Equal(0, store.CommitCalls);
        Assert.Equal(0, host.RelationCalls);
        Assert.Empty(retained.Checkpoint.Activations);
    }

    [Fact]
    public async Task SupersededWorker_CannotPublishItsStagedActivationCheckpoint()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/superseded-worker",
            semanticVariant: "superseded-worker");
        var innerStore = new InMemoryProcessDurableStore();
        var store = new SupersedingCommitStore(innerStore, WorkerLease);
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initializedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);

        var result = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan,
            initializedSnapshot.Checkpoint.ContinuationIdentity,
            fixture.Activation);
        var staged = Assert.IsType<ProcessDurableCommit>(result.Commit);
        var retained = await innerStore.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            initializedSnapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId);
        var retainedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(retained);
        var supersedingLease = Assert.IsType<ProcessWorkerLease>(retainedSnapshot.WorkerLease);

        Assert.Equal(ProcessDurableRuntimeDisposition.RevisionConflict, result.Disposition);
        Assert.Equal(1, host.RelationCalls);
        Assert.Single(staged.Checkpoint.Activations);
        Assert.Single(staged.Checkpoint.Operations);
        Assert.Single(staged.Checkpoint.Emissions);
        Assert.Single(staged.Checkpoint.DurableOperations);
        Assert.NotEqual(staged.Fence, supersedingLease.Fence);
        Assert.Equal("worker/superseding-runtime-tests", supersedingLease.Owner);
        Assert.Equal(
            ProcessStorageContentFingerprints.Continuation(initializedSnapshot.Checkpoint.Continuation),
            ProcessStorageContentFingerprints.Continuation(retainedSnapshot.Checkpoint.Continuation));
        Assert.Empty(retainedSnapshot.Checkpoint.Activations);
        Assert.Empty(retainedSnapshot.Checkpoint.Operations);
        Assert.Empty(retainedSnapshot.Checkpoint.Emissions);
        Assert.Empty(retainedSnapshot.Checkpoint.DurableOperations);
    }

    [Fact]
    public async Task StaleAcquisitionRevision_IsReportedBeforeNewerAggregateChronology()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/stale-acquisition-race",
            semanticVariant: "stale-acquisition-race");
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, new RecordingHost(fixture.OperationResult));
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var loaded = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var instanceId = loaded.Checkpoint.ContinuationIdentity.ProcessInstanceId;
        var admitted = await store.AdmitInputAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            instanceId,
            fixture.PendingReply,
            ProcessDurabilityTestFixture.CheckpointedAtUtc);

        var acquired = await store.AcquireWorkerAsync(
            Context(loaded.Checkpoint.UpdatedAtUtc),
            instanceId,
            loaded.Revision,
            "worker/stale-acquisition-race",
            WorkerLease,
            loaded.Checkpoint.UpdatedAtUtc);

        Assert.Equal(ProcessStoreMutationDisposition.Applied, admitted.Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.RevisionConflict, acquired.Disposition);
        Assert.Equal(admitted.Snapshot?.Revision, acquired.Snapshot?.Revision);
        Assert.Null(Assert.IsType<ProcessDurableStoreSnapshot>(acquired.Snapshot).WorkerLease);
    }

    [Fact]
    public async Task IncompatiblePlan_IsRejectedBeforeWorkerAcquisitionOrHostWork()
    {
        const string definitionId = "process/durable-runtime/compatibility-preflight";
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId,
            semanticVariant: "compatible");
        var incompatible = ProcessDurabilityTestFixture.Create(
            definitionId,
            semanticVariant: "incompatible");
        var innerStore = new InMemoryProcessDurableStore();
        var store = new TrackingStore(innerStore);
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);

        var result = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            incompatible.Plan,
            before.Checkpoint.ContinuationIdentity,
            fixture.Activation);
        var after = await innerStore.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            before.Checkpoint.ContinuationIdentity.ProcessInstanceId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Incompatible, result.Disposition);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Equal(0, store.AcquireWorkerCalls);
        Assert.Equal(0, host.RelationCalls);
        Assert.Equal(before.Revision, after?.Revision);
        Assert.Null(Assert.IsType<ProcessDurableStoreSnapshot>(after).WorkerLease);
    }

    [Fact]
    public async Task HistoricalAffinityObservation_UsesFreshPhysicalLeaseAndCommitTime()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/historical-affinity",
            semanticVariant: "historical-affinity");
        var innerStore = new InMemoryProcessDurableStore();
        var store = new AcquisitionRecordingStore(innerStore);
        var runtime = Runtime(store, fixture, new RecordingHost(fixture.OperationResult));
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initial = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var semanticObservedAtUtc = ProcessDurabilityTestFixture.AcceptedAtUtc.AddSeconds(1);
        var physicalObservedAtUtc = semanticObservedAtUtc.Add(WorkerLease).AddMinutes(1);

        var result = await runtime.BindAttemptAffinityAsync(
            Context(physicalObservedAtUtc),
            fixture.Plan,
            new(
                Expectation(initial.Control),
                ProcessControlTestFixture.Affinity(value: "generation/historical"),
                semanticObservedAtUtc));
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot);
        var lease = Assert.IsType<ProcessWorkerLease>(snapshot.WorkerLease);
        var acquisition = Assert.Single(store.Acquisitions);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        Assert.Equal(physicalObservedAtUtc, acquisition.ObservedAtUtc);
        Assert.Equal(physicalObservedAtUtc, lease.ClaimedAtUtc);
        Assert.Equal(physicalObservedAtUtc.Add(WorkerLease), lease.ExpiresAtUtc);
        Assert.Equal(physicalObservedAtUtc, snapshot.Checkpoint.UpdatedAtUtc);
        Assert.Equal(
            semanticObservedAtUtc,
            Assert.Single(snapshot.Checkpoint.Control.CurrentAttempt.AffinityBindings).ObservedAtUtc);
    }

    [Fact]
    public async Task ControlCommit_CannotUseAnExpiredPhysicalLeaseWithAnEarlierCheckpointTime()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/control-physical-fence",
            semanticVariant: "control-physical-fence");
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, new RecordingHost(fixture.OperationResult));
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initialSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var observedAtUtc = ProcessDurabilityTestFixture.AcceptedAtUtc.AddSeconds(1);
        var command = ProcessControlTestFixture.Create().Pause(
            initialSnapshot.Checkpoint.Control,
            id: "pause/physical-fence",
            issuedAtUtc: observedAtUtc);
        var time = new StepAtReadTimeProvider(
            observedAtUtc,
            observedAtUtc.Add(WorkerLease),
            readsBeforeStep: 3);

        var result = await runtime.ApplyControlAsync(
            OperationContext.Create(timeProvider: time),
            fixture.Plan,
            command);
        var retained = await store.LoadAsync(
            Context(observedAtUtc.Add(WorkerLease)),
            initialSnapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId);

        Assert.Equal(ProcessDurableRuntimeDisposition.LeaseExpired, result.Disposition);
        Assert.Equal(ProcessControlMode.Running, retained?.Checkpoint.Control.Mode);
        Assert.Equal(initialSnapshot.Checkpoint.Control.Revision, retained?.Checkpoint.Control.Revision);
    }

    [Fact]
    public async Task CancellationCommit_CannotUseAnExpiredPhysicalLeaseWithAnEarlierActivationTime()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/cancel-physical-fence",
            semanticVariant: "cancel-physical-fence");
        var store = new InMemoryProcessDurableStore();
        var runtime = Runtime(store, fixture, new RecordingHost(fixture.OperationResult));
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initialSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var observedAtUtc = ProcessDurabilityTestFixture.AcceptedAtUtc.AddSeconds(1);
        var command = ProcessControlTestFixture.Create().Cancel(
            initialSnapshot.Checkpoint.Control,
            id: "cancel/physical-fence",
            issuedAtUtc: observedAtUtc);
        var time = new StepAtReadTimeProvider(
            observedAtUtc,
            observedAtUtc.Add(WorkerLease),
            readsBeforeStep: 3);

        var result = await runtime.CancelAsync(
            OperationContext.Create(timeProvider: time),
            fixture.Plan,
            command,
            fixture.Activation.Context);
        var retained = await store.LoadAsync(
            Context(observedAtUtc.Add(WorkerLease)),
            initialSnapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId);

        Assert.Equal(ProcessDurableRuntimeDisposition.LeaseExpired, result.Disposition);
        Assert.Equal(ProcessControlMode.Running, retained?.Checkpoint.Control.Mode);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, retained?.Checkpoint.Continuation.Terminal.Kind);
        Assert.Empty(retained?.Checkpoint.Activations ?? []);
    }

    [Fact]
    public async Task PauseAndContinue_RetainCurrentAttemptAndAffinity_AndPausedActivationDoesNoHostWork()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/pause-continue",
            semanticVariant: "pause-continue");
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var commands = ProcessControlTestFixture.Create();
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initial = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var initialAttempt = initial.ContinuationIdentity.ProcessAttemptId;
        var affinity = ProcessControlTestFixture.Affinity(
            slot: "node/index-generation",
            value: "generation/retained");
        var affinityAt = ProcessDurabilityTestFixture.AcceptedAtUtc.AddSeconds(1);
        var bound = await runtime.BindAttemptAffinityAsync(
            Context(affinityAt),
            fixture.Plan,
            new(
                Expectation(initial.Control),
                affinity,
                affinityAt));
        var boundCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(bound.Snapshot).Checkpoint;
        var pauseAt = affinityAt.AddSeconds(1);

        var paused = await runtime.ApplyControlAsync(
            Context(pauseAt),
            fixture.Plan,
            commands.Pause(
                boundCheckpoint.Control,
                id: "pause/runtime-retains-affinity",
                issuedAtUtc: pauseAt));
        var pausedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(paused.Snapshot).Checkpoint;

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, paused.Disposition);
        Assert.Equal(ProcessControlMode.Paused, pausedCheckpoint.Control.Mode);
        Assert.Equal(initialAttempt, pausedCheckpoint.Control.CurrentAttempt.AttemptId);
        Assert.Equal(initialAttempt, pausedCheckpoint.ContinuationIdentity.ProcessAttemptId);
        Assert.Equal(affinity, Assert.Single(pausedCheckpoint.Control.CurrentAttempt.AffinityBindings).Affinity);

        var blockedActivation = new ProcessActivation(
            new("activation/while-paused"),
            ProcessActivationCause.Continue,
            pauseAt.AddSeconds(1),
            fixture.Activation.Context);
        var blocked = await runtime.ActivateAsync(
            Context(pauseAt.AddSeconds(1)),
            fixture.Plan,
            pausedCheckpoint.ContinuationIdentity,
            blockedActivation);

        Assert.Equal(ProcessDurableRuntimeDisposition.Paused, blocked.Disposition);
        Assert.Equal(0, host.RelationCalls);

        var continueAt = pauseAt.AddSeconds(2);
        var continued = await runtime.ApplyControlAsync(
            Context(continueAt),
            fixture.Plan,
            commands.Continue(
                pausedCheckpoint.Control,
                id: "continue/runtime-retains-affinity",
                issuedAtUtc: continueAt));
        var continuedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(continued.Snapshot).Checkpoint;

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, continued.Disposition);
        Assert.Equal(ProcessControlMode.Running, continuedCheckpoint.Control.Mode);
        Assert.Equal(initialAttempt, continuedCheckpoint.Control.CurrentAttempt.AttemptId);
        Assert.Equal(initialAttempt, continuedCheckpoint.ContinuationIdentity.ProcessAttemptId);
        Assert.Equal(
            affinity,
            Assert.Single(continuedCheckpoint.Control.CurrentAttempt.AffinityBindings).Affinity);
    }

    [Fact]
    public async Task DurableSignalAdmission_IsConsumedExactlyOnce_AndReplayDoesNotReopenTheInbox()
    {
        var controls = ProcessControlTestFixture.Create();
        var (plan, start) = SignalAwaitProcess(controls);
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(
            ProcessOperationResult.Completed(ProcessDurabilityTestFixture.StringValue("unused")));
        var runtime = Runtime(store, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            plan,
            start);
        var initial = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var activationContext = SignalActivationContext();
        var register = new ProcessActivation(
            new("activation/register-signal-wait"),
            ProcessActivationCause.Start,
            ProcessDurabilityTestFixture.ActivatedAtUtc,
            activationContext);
        var registered = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            plan,
            initial.ContinuationIdentity,
            register);
        var registeredCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(registered.Snapshot).Checkpoint;
        var token = Assert.Single(registeredCheckpoint.Continuation.Tokens);
        var wait = Assert.Single(registeredCheckpoint.Continuation.Waits);
        var target = new ProcessTokenInteractionTarget(
            registeredCheckpoint.ContinuationIdentity,
            token.Id,
            wait.RegistrationId);
        var signal = Signal(
            plan,
            controls,
            target,
            activationContext,
            "durable-runtime-signal");
        var signalAt = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var command = SignalCommand(
            registeredCheckpoint.Control,
            signal,
            "signal/admit-runtime-input",
            signalAt);

        var admitted = await runtime.ApplyControlAsync(
            Context(signalAt),
            plan,
            command);
        var admittedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(admitted.Snapshot);
        var pendingEntry = Assert.Single(admittedSnapshot.Checkpoint.Inbox);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, admitted.Disposition);
        Assert.Equal(signal.Context.EmissionId, pendingEntry.EmissionId);
        Assert.Null(pendingEntry.Receipt);

        var admissionReplay = await runtime.ApplyControlAsync(
            Context(signalAt.AddSeconds(1)),
            plan,
            command);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, admissionReplay.Disposition);
        Assert.Equal(admittedSnapshot.Revision, admissionReplay.Snapshot?.Revision);
        Assert.Null(Assert.Single(
            Assert.IsType<ProcessDurableStoreSnapshot>(admissionReplay.Snapshot).Checkpoint.Inbox).Receipt);

        var consumeAt = signalAt.AddSeconds(2);
        var consume = new ProcessActivation(
            new("activation/consume-signal"),
            ProcessActivationCause.Interaction,
            consumeAt,
            activationContext,
            [new(target, signal)]);
        var consumed = await runtime.ActivateAsync(
            Context(consumeAt),
            plan,
            registeredCheckpoint.ContinuationIdentity,
            consume);
        var consumedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(consumed.Snapshot);
        var consumedCheckpoint = consumedSnapshot.Checkpoint;
        var consumedEntry = Assert.Single(consumedCheckpoint.Inbox);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, consumed.Disposition);
        Assert.Equal(ProcessActivationDisposition.Completed, consumed.Decision?.Disposition);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            Assert.IsType<ProcessInputReceipt>(consumedEntry.Receipt).Disposition);
        Assert.Equal(
            registeredCheckpoint.ContinuationIdentity,
            consumedEntry.DispositionContinuation);
        Assert.Equal(2, consumedCheckpoint.Activations.Length);
        Assert.Single(consumedCheckpoint.Continuation.InputReceipts);

        var activationReplay = await runtime.ActivateAsync(
            Context(consumeAt.AddSeconds(1)),
            plan,
            registeredCheckpoint.ContinuationIdentity,
            consume);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, activationReplay.Disposition);
        Assert.Equal(consumedSnapshot.Revision, activationReplay.Snapshot?.Revision);

        var duplicateAt = consumeAt.AddSeconds(2);
        var duplicate = await runtime.ApplyControlAsync(
            Context(duplicateAt),
            plan,
            SignalCommand(
                consumedCheckpoint.Control,
                signal,
                "signal/duplicate-runtime-input",
                duplicateAt));
        var duplicateSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(duplicate.Snapshot);
        var duplicateCheckpoint = duplicateSnapshot.Checkpoint;

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, duplicate.Disposition);
        Assert.True(duplicateSnapshot.Revision.Ordinal > consumedSnapshot.Revision.Ordinal);
        Assert.Equal(
            ProcessStorageContentFingerprints.Continuation(consumedCheckpoint.Continuation),
            ProcessStorageContentFingerprints.Continuation(duplicateCheckpoint.Continuation));
        Assert.Single(duplicateCheckpoint.Inbox);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            Assert.IsType<ProcessInputReceipt>(duplicateCheckpoint.Inbox[0].Receipt).Disposition);
        Assert.Equal(2, duplicateCheckpoint.Activations.Length);
        Assert.Single(duplicateCheckpoint.Continuation.InputReceipts);
        var signalReceipts = duplicateCheckpoint.Control.Receipts
            .Where(static receipt => receipt.Command is SignalProcessCommand)
            .ToArray();
        Assert.Equal(2, signalReceipts.Length);
        Assert.Equal(ProcessControlReceiptDisposition.SignalAccepted, signalReceipts[0].Disposition);
        Assert.Equal(ProcessControlReceiptDisposition.SignalDuplicate, signalReceipts[1].Disposition);
        Assert.Equal(0, host.RelationCalls);
    }

    [Fact]
    public async Task ConflictingSignalEmissionIdentity_ReturnsTypedConflictWithoutMutation()
    {
        var controls = ProcessControlTestFixture.Create();
        var (plan, start) = SignalAwaitProcess(controls);
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(
            ProcessOperationResult.Completed(ProcessDurabilityTestFixture.StringValue("unused")));
        var runtime = Runtime(store, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            plan,
            start);
        var initial = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var activationContext = SignalActivationContext();
        var register = new ProcessActivation(
            new("activation/register-signal-conflict"),
            ProcessActivationCause.Start,
            ProcessDurabilityTestFixture.ActivatedAtUtc,
            activationContext);
        var registered = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            plan,
            initial.ContinuationIdentity,
            register);
        var registeredCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(registered.Snapshot).Checkpoint;
        var token = Assert.Single(registeredCheckpoint.Continuation.Tokens);
        var wait = Assert.Single(registeredCheckpoint.Continuation.Waits);
        var target = new ProcessTokenInteractionTarget(
            registeredCheckpoint.ContinuationIdentity,
            token.Id,
            wait.RegistrationId);
        var signal = Signal(
            plan,
            controls,
            target,
            activationContext,
            "conflicting-runtime-input");
        var conflictingSignal = new SignalEnvelope(
            signal.SchemaVersion,
            signal.Context,
            signal.Contract,
            ProcessDurabilityTestFixture.StringValue("conflicting-payload"),
            signal.Target);
        var conflictingInput = new ProcessActivationInput(target, conflictingSignal);
        var admittedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var admitted = await store.AdmitInputAsync(
            Context(admittedAtUtc),
            registeredCheckpoint.ContinuationIdentity.ProcessInstanceId,
            conflictingInput,
            admittedAtUtc);
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(admitted.Snapshot);
        var commandAtUtc = admittedAtUtc.AddSeconds(1);
        var command = SignalCommand(
            before.Checkpoint.Control,
            signal,
            "signal/conflicting-runtime-input",
            commandAtUtc);

        var result = await runtime.ApplyControlAsync(
            Context(commandAtUtc),
            plan,
            command);
        var retained = Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.IdentityConflict, result.Disposition);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == ProcessDurableRuntimeDiagnosticCodes.ActivationIdentityConflict);
        Assert.Equal(before.Revision, retained.Revision);
        Assert.Equal(before.Checkpoint.Control.Revision, retained.Checkpoint.Control.Revision);
        Assert.Equal(before.Checkpoint.Control.Receipts.Length, retained.Checkpoint.Control.Receipts.Length);
        Assert.Equal(
            ProcessStorageContentFingerprints.Input(conflictingInput),
            ProcessStorageContentFingerprints.Input(Assert.Single(retained.Checkpoint.Inbox).Input));
        Assert.Equal(0, host.RelationCalls);
    }

    [Fact]
    public async Task Cancellation_RedispositionsRetainedBufferedInputWithoutSupplyingItAgain()
    {
        var controls = ProcessControlTestFixture.Create();
        var (plan, start) = BufferedSignalProcess(controls);
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(
            ProcessOperationResult.Completed(ProcessDurabilityTestFixture.StringValue("unused")));
        var runtime = Runtime(store, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            plan,
            start);
        var initial = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var activationContext = SignalActivationContext();
        var register = new ProcessActivation(
            new("activation/register-buffering-cut"),
            ProcessActivationCause.Start,
            ProcessDurabilityTestFixture.ActivatedAtUtc,
            activationContext);
        var registered = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            plan,
            initial.ContinuationIdentity,
            register);
        var registeredCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(registered.Snapshot).Checkpoint;
        var token = Assert.Single(registeredCheckpoint.Continuation.Tokens);
        var cut = Assert.Single(registeredCheckpoint.Continuation.Waits);
        Assert.Equal(ProcessWaitKind.DurableCut, cut.Kind);
        var target = new ProcessTokenInteractionTarget(
            registeredCheckpoint.ContinuationIdentity,
            token.Id,
            cut.RegistrationId);
        var signal = Signal(
            plan,
            controls,
            target,
            activationContext,
            "buffer-before-cancellation");
        var signalAt = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var admitted = await runtime.ApplyControlAsync(
            Context(signalAt),
            plan,
            SignalCommand(
                registeredCheckpoint.Control,
                signal,
                "signal/admit-before-cancellation",
                signalAt));
        var admittedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(admitted.Snapshot).Checkpoint;
        Assert.Null(Assert.Single(admittedCheckpoint.Inbox).Receipt);

        var bufferAt = signalAt.AddSeconds(1);
        var bufferingActivation = new ProcessActivation(
            new("activation/buffer-signal"),
            ProcessActivationCause.Interaction,
            bufferAt,
            activationContext,
            [new(target, signal)]);
        var buffered = await runtime.ActivateAsync(
            Context(bufferAt),
            plan,
            registeredCheckpoint.ContinuationIdentity,
            bufferingActivation);
        var bufferedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(buffered.Snapshot).Checkpoint;
        var bufferedEntry = Assert.Single(bufferedCheckpoint.Inbox);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, buffered.Disposition);
        Assert.Equal(ProcessActivationDisposition.DurableCut, buffered.Decision?.Disposition);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Buffered,
            Assert.IsType<ProcessInputReceipt>(bufferedEntry.Receipt).Disposition);
        Assert.Single(bufferedCheckpoint.Continuation.BufferedInputs);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, bufferedCheckpoint.Continuation.Terminal.Kind);

        var cancelAt = bufferAt.AddSeconds(1);
        var cancel = await runtime.CancelAsync(
            Context(cancelAt),
            plan,
            controls.Cancel(
                bufferedCheckpoint.Control,
                id: "cancel/runtime-buffered-input",
                issuedAtUtc: cancelAt),
            activationContext);
        var cancelledCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(cancel.Snapshot).Checkpoint;
        var terminalEntry = Assert.Single(cancelledCheckpoint.Inbox);
        var cancellationReceipt = cancelledCheckpoint.Activations[^1];

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, cancel.Disposition);
        Assert.Equal(ProcessControlMode.Cancelled, cancelledCheckpoint.Control.Mode);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, cancelledCheckpoint.Continuation.Terminal.Kind);
        Assert.Equal(
            ProcessInputAdmissionDisposition.TerminalUnconsumed,
            Assert.IsType<ProcessInputReceipt>(terminalEntry.Receipt).Disposition);
        Assert.Empty(cancelledCheckpoint.Continuation.BufferedInputs);
        Assert.Equal(3, cancelledCheckpoint.Activations.Length);
        Assert.Equal(ProcessActivationDisposition.Cancelled, cancellationReceipt.Disposition);
        Assert.Empty(cancellationReceipt.Activation.Inputs);
        Assert.Equal(signal.Context.EmissionId, terminalEntry.EmissionId);
        Assert.Equal(0, host.RelationCalls);
    }

    [Fact]
    public async Task RestartAttempt_ClosesRetainedBufferedInputUnderOldAttempt()
    {
        var controls = ProcessControlTestFixture.Create();
        var (plan, start) = BufferedSignalProcess(
            controls,
            ProcessRecoveryPolicy.RestartAttempt);
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(
            ProcessOperationResult.Completed(ProcessDurabilityTestFixture.StringValue("unused")));
        var runtime = Runtime(store, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            plan,
            start);
        var initial = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var activationContext = SignalActivationContext();
        var register = new ProcessActivation(
            new("activation/register-buffering-cut-for-restart"),
            ProcessActivationCause.Start,
            ProcessDurabilityTestFixture.ActivatedAtUtc,
            activationContext);
        var registered = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            plan,
            initial.ContinuationIdentity,
            register);
        var registeredCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(registered.Snapshot).Checkpoint;
        var token = Assert.Single(registeredCheckpoint.Continuation.Tokens);
        var cut = Assert.Single(registeredCheckpoint.Continuation.Waits);
        var target = new ProcessTokenInteractionTarget(
            registeredCheckpoint.ContinuationIdentity,
            token.Id,
            cut.RegistrationId);
        var signal = Signal(
            plan,
            controls,
            target,
            activationContext,
            "buffer-before-restart");
        var signalAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var admitted = await runtime.ApplyControlAsync(
            Context(signalAtUtc),
            plan,
            SignalCommand(
                registeredCheckpoint.Control,
                signal,
                "signal/admit-before-restart",
                signalAtUtc));
        var admittedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(admitted.Snapshot).Checkpoint;
        Assert.Null(Assert.Single(admittedCheckpoint.Inbox).Receipt);
        var bufferAtUtc = signalAtUtc.AddSeconds(1);
        var bufferingActivation = new ProcessActivation(
            new("activation/buffer-signal-before-restart"),
            ProcessActivationCause.Interaction,
            bufferAtUtc,
            activationContext,
            [new(target, signal)]);
        var buffered = await runtime.ActivateAsync(
            Context(bufferAtUtc),
            plan,
            registeredCheckpoint.ContinuationIdentity,
            bufferingActivation);
        var bufferedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(buffered.Snapshot).Checkpoint;
        var bufferedEntry = Assert.Single(bufferedCheckpoint.Inbox);
        var closingContinuation = bufferedCheckpoint.ContinuationIdentity;

        Assert.Equal(
            ProcessInputAdmissionDisposition.Buffered,
            Assert.IsType<ProcessInputReceipt>(bufferedEntry.Receipt).Disposition);
        Assert.Single(bufferedCheckpoint.Continuation.BufferedInputs);
        Assert.Single(bufferedCheckpoint.Continuation.InputReceipts);

        var restartAtUtc = bufferAtUtc.AddSeconds(1);
        var restarted = await runtime.ApplyControlAsync(
            Context(restartAtUtc),
            plan,
            controls.Restart(
                bufferedCheckpoint.Control,
                newAttemptId: "process-attempt/after-buffered-input",
                id: "restart/runtime-buffered-input",
                issuedAtUtc: restartAtUtc));
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(restarted.Snapshot).Checkpoint;
        var closed = Assert.Single(checkpoint.Inbox);
        var receipt = Assert.IsType<ProcessInputReceipt>(closed.Receipt);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, restarted.Disposition);
        Assert.Equal(ProcessInputAdmissionDisposition.Stale, receipt.Disposition);
        Assert.Equal(restartAtUtc, receipt.ObservedAtUtc);
        Assert.Equal(closingContinuation, closed.DispositionContinuation);
        Assert.NotEqual(closingContinuation, checkpoint.ContinuationIdentity);
        Assert.Empty(checkpoint.Continuation.BufferedInputs);
        Assert.Empty(checkpoint.Continuation.InputReceipts);
        Assert.Empty(checkpoint.Continuation.OutstandingRequests);
        Assert.Equal(0, host.RelationCalls);
    }

    [Fact]
    public async Task Cancellation_TerminallyDispositionsPendingReplyAndReplaysInertly()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/cancel-pending-reply",
            semanticVariant: "cancel-pending-reply");
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var commands = ProcessControlTestFixture.Create();
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initial = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var activated = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan,
            initial.ContinuationIdentity,
            fixture.Activation);
        var waiting = Assert.IsType<ProcessDurableStoreSnapshot>(activated.Snapshot).Checkpoint;
        var admittedAt = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var admitted = await store.AdmitInputAsync(
            Context(admittedAt),
            waiting.ContinuationIdentity.ProcessInstanceId,
            fixture.PendingReply,
            admittedAt);
        var admittedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(admitted.Snapshot);
        var pendingEntry = Assert.Single(admittedSnapshot.Checkpoint.Inbox);

        Assert.Equal(ProcessStoreMutationDisposition.Applied, admitted.Disposition);
        Assert.Null(pendingEntry.Receipt);
        Assert.Equal(
            ProcessStorageContentFingerprints.Input(fixture.PendingReply),
            ProcessStorageContentFingerprints.Input(pendingEntry.Input));

        var cancelAt = admittedAt.AddSeconds(1);
        var command = commands.Cancel(
            admittedSnapshot.Checkpoint.Control,
            id: "cancel/runtime-pending-reply",
            issuedAtUtc: cancelAt);
        var cancelled = await runtime.CancelAsync(
            Context(cancelAt),
            fixture.Plan,
            command,
            fixture.Activation.Context);
        var cancelledSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(cancelled.Snapshot);
        var checkpoint = cancelledSnapshot.Checkpoint;
        var terminalEntry = Assert.Single(checkpoint.Inbox);
        var terminalReceipt = Assert.IsType<ProcessInputReceipt>(terminalEntry.Receipt);
        var cancellationActivation = checkpoint.Activations[^1];
        var cancellationInput = Assert.Single(cancellationActivation.Activation.Inputs);
        var inputTraces = cancellationActivation.Evidence.Trace.Where(trace =>
                trace.Kind == ProcessTraceEventKind.InputAdmitted
                && trace.Emission == fixture.PendingReply.Envelope.Context.EmissionId)
            .ToArray();

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, cancelled.Disposition);
        Assert.Equal(ProcessControlMode.Cancelled, checkpoint.Control.Mode);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(ProcessActivationDisposition.Cancelled, cancellationActivation.Disposition);
        Assert.Equal(2, checkpoint.Activations.Length);
        Assert.Equal(
            ProcessStorageContentFingerprints.Input(fixture.PendingReply),
            ProcessStorageContentFingerprints.Input(cancellationInput));
        Assert.Equal(
            ProcessStorageContentFingerprints.Input(fixture.PendingReply),
            ProcessStorageContentFingerprints.Input(terminalReceipt.Input));
        Assert.Equal(ProcessInputAdmissionDisposition.Observed, terminalReceipt.Disposition);
        Assert.Collection(
            inputTraces,
            bufferedTrace =>
            {
                Assert.Equal("buffered:Buffered", bufferedTrace.Detail);
                Assert.Equal(ProcessInputAdmissionDisposition.Buffered, bufferedTrace.InputDisposition);
            },
            terminalTrace =>
            {
                Assert.Equal("terminal-late:Observed", terminalTrace.Detail);
                Assert.Equal(terminalReceipt.Disposition, terminalTrace.InputDisposition);
            });
        Assert.DoesNotContain(checkpoint.Inbox, static entry => entry.Receipt is null);
        Assert.Equal(1, host.RelationCalls);

        var replay = await runtime.CancelAsync(
            Context(cancelAt.AddSeconds(1)),
            fixture.Plan,
            command,
            fixture.Activation.Context);
        var replayed = Assert.IsType<ProcessDurableStoreSnapshot>(replay.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Equal(cancelledSnapshot.Revision, replayed.Revision);
        Assert.Equal(2, replayed.Checkpoint.Activations.Length);
        Assert.Single(replayed.Checkpoint.Inbox);
        Assert.DoesNotContain(replayed.Checkpoint.Inbox, static entry => entry.Receipt is null);
        Assert.Equal(
            terminalReceipt,
            Assert.IsType<ProcessInputReceipt>(replayed.Checkpoint.Inbox[0].Receipt));
        Assert.Equal(1, host.RelationCalls);
    }

    [Fact]
    public async Task RestartAttemptReplay_CreatesExactlyOneCleanReplacement_AndFencesOldAttempt()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/restart-attempt",
            semanticVariant: "restart-attempt",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var commands = ProcessControlTestFixture.Create();
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initial = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var activated = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan,
            initial.ContinuationIdentity,
            fixture.Activation);
        var activatedCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(activated.Snapshot).Checkpoint;
        var affinityAt = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var bound = await runtime.BindAttemptAffinityAsync(
            Context(affinityAt),
            fixture.Plan,
            new(
                Expectation(activatedCheckpoint.Control),
                ProcessControlTestFixture.Affinity(value: "generation/abandoned"),
                affinityAt));
        var boundCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(bound.Snapshot).Checkpoint;
        var restartAt = affinityAt.AddSeconds(1);
        var command = commands.Restart(
            boundCheckpoint.Control,
            newAttemptId: "process-attempt/replacement",
            id: "restart/runtime-clean-attempt",
            issuedAtUtc: restartAt);

        var restarted = await runtime.ApplyControlAsync(
            Context(restartAt),
            fixture.Plan,
            command);
        var restartedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(restarted.Snapshot);
        var restartedCheckpoint = restartedSnapshot.Checkpoint;

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, restarted.Disposition);
        Assert.Equal(2, restartedCheckpoint.Control.Attempts.Length);
        Assert.Equal(
            ProcessControlAttemptDisposition.Abandoned,
            restartedCheckpoint.Control.Attempts[0].Disposition);
        Assert.Single(restartedCheckpoint.Control.Attempts[0].AffinityBindings);
        Assert.Equal(
            new ProcessAttemptId("process-attempt/replacement"),
            restartedCheckpoint.Control.CurrentAttempt.AttemptId);
        Assert.Empty(restartedCheckpoint.Control.CurrentAttempt.AffinityBindings);
        Assert.Equal(
            restartedCheckpoint.Control.CurrentAttempt.AttemptId,
            restartedCheckpoint.ContinuationIdentity.ProcessAttemptId);
        Assert.Equal(0, restartedCheckpoint.Continuation.CompletedActivationCount);

        var replay = await runtime.ApplyControlAsync(
            Context(restartAt.AddSeconds(1)),
            fixture.Plan,
            command);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Equal(restartedSnapshot.Revision, replay.Snapshot?.Revision);
        Assert.Equal(2, replay.Snapshot?.Checkpoint.Control.Attempts.Length);
        Assert.Empty(Assert.IsType<ProcessDurableStoreSnapshot>(replay.Snapshot)
            .Checkpoint.Control.CurrentAttempt.AffinityBindings);

        var oldReceiptReplay = await runtime.ActivateAsync(
            Context(restartAt.AddSeconds(2)),
            fixture.Plan,
            initial.ContinuationIdentity,
            fixture.Activation);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, oldReceiptReplay.Disposition);
        Assert.Equal(restartedSnapshot.Revision, oldReceiptReplay.Snapshot?.Revision);
        Assert.Equal(1, host.RelationCalls);

        var staleActivation = new ProcessActivation(
            new("activation/stale-old-attempt"),
            ProcessActivationCause.Recovery,
            restartAt.AddSeconds(3),
            fixture.Activation.Context);
        var stale = await runtime.ActivateAsync(
            Context(restartAt.AddSeconds(3)),
            fixture.Plan,
            boundCheckpoint.ContinuationIdentity,
            staleActivation);

        Assert.Equal(ProcessDurableRuntimeDisposition.StaleFence, stale.Disposition);
        Assert.Contains(
            stale.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessControlDiagnosticCodes.StaleAttempt);
        Assert.Equal(1, host.RelationCalls);
    }

    [Fact]
    public async Task RestartAttempt_ClosesPendingInboxUnderOldAttemptAndReplaysInertly()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/restart-pending-inbox",
            semanticVariant: "restart-pending-inbox",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var commands = ProcessControlTestFixture.Create();
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initial = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var activated = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan,
            initial.ContinuationIdentity,
            fixture.Activation);
        var waiting = Assert.IsType<ProcessDurableStoreSnapshot>(activated.Snapshot).Checkpoint;
        var closingContinuation = waiting.ContinuationIdentity;
        var admittedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var admitted = await store.AdmitInputAsync(
            Context(admittedAtUtc),
            closingContinuation.ProcessInstanceId,
            fixture.PendingReply,
            admittedAtUtc);
        var admittedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(admitted.Snapshot);
        Assert.Null(Assert.Single(admittedSnapshot.Checkpoint.Inbox).Receipt);
        var restartAtUtc = admittedAtUtc.AddSeconds(1);
        var command = commands.Restart(
            admittedSnapshot.Checkpoint.Control,
            newAttemptId: "process-attempt/restart-after-pending-input",
            id: "restart/runtime-pending-inbox",
            issuedAtUtc: restartAtUtc);

        var committedAtUtc = restartAtUtc.AddMilliseconds(1);
        var restartClock = new StepAtReadTimeProvider(
            restartAtUtc,
            committedAtUtc,
            readsBeforeStep: 3);
        var restarted = await runtime.ApplyControlAsync(
            OperationContext.Create(timeProvider: restartClock),
            fixture.Plan,
            command);
        var restartedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(restarted.Snapshot);
        var checkpoint = restartedSnapshot.Checkpoint;
        var closed = Assert.Single(checkpoint.Inbox);
        var receipt = Assert.IsType<ProcessInputReceipt>(closed.Receipt);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, restarted.Disposition);
        Assert.Equal(ProcessInputAdmissionDisposition.Stale, receipt.Disposition);
        Assert.Equal(restartAtUtc, receipt.ObservedAtUtc);
        Assert.Equal(committedAtUtc, checkpoint.UpdatedAtUtc);
        Assert.Equal(closingContinuation, closed.DispositionContinuation);
        Assert.Equal(
            ProcessStorageContentFingerprints.Input(fixture.PendingReply),
            ProcessStorageContentFingerprints.Input(closed.Input));
        Assert.NotEqual(closingContinuation, checkpoint.ContinuationIdentity);
        Assert.Empty(checkpoint.Continuation.InputReceipts);
        Assert.Empty(checkpoint.Continuation.BufferedInputs);
        Assert.Empty(checkpoint.Continuation.OutstandingRequests);
        Assert.Equal(1, host.RelationCalls);

        var replay = await runtime.ApplyControlAsync(
            Context(restartAtUtc.AddSeconds(1)),
            fixture.Plan,
            command);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Equal(restartedSnapshot.Revision, replay.Snapshot?.Revision);
        Assert.Equal(
            receipt,
            Assert.IsType<ProcessInputReceipt>(
                Assert.Single(replay.Snapshot!.Checkpoint.Inbox).Receipt));
        Assert.Equal(1, host.RelationCalls);
    }

    [Fact]
    public async Task CooperativeCancellation_CommitsControlAndTerminalContinuationTogether_AndReplaysInertly()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/cooperative-cancellation",
            semanticVariant: "cooperative-cancellation");
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var commands = ProcessControlTestFixture.Create();
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc),
            fixture.Plan,
            fixture.Start);
        var initial = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint;
        var cancelAt = ProcessDurabilityTestFixture.AcceptedAtUtc.AddSeconds(1);
        var command = commands.Cancel(
            initial.Control,
            id: "cancel/runtime-terminal-cut",
            issuedAtUtc: cancelAt);

        var cancelled = await runtime.CancelAsync(
            Context(cancelAt),
            fixture.Plan,
            command,
            fixture.Activation.Context);
        var cancelledSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(cancelled.Snapshot);
        var checkpoint = cancelledSnapshot.Checkpoint;

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, cancelled.Disposition);
        var receipt = Assert.Single(checkpoint.Activations);
        Assert.Equal(ProcessControlMode.Cancelled, checkpoint.Control.Mode);
        Assert.Equal(
            ProcessControlAttemptDisposition.Cancelled,
            checkpoint.Control.CurrentAttempt.Disposition);
        Assert.Equal(
            ProcessControlExecutionPhase.Stopped,
            checkpoint.Control.CurrentAttempt.Phase);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(ProcessActivationDisposition.Cancelled, receipt.Disposition);
        Assert.Equal(checkpoint.ContinuationIdentity, receipt.Continuation);
        Assert.Equal(
            ProcessStorageContentFingerprints.Continuation(checkpoint.Continuation),
            receipt.AfterContinuation);
        Assert.Empty(checkpoint.Operations);
        Assert.Empty(checkpoint.Emissions);
        Assert.Equal(0, host.RelationCalls);

        var replay = await runtime.CancelAsync(
            Context(cancelAt.AddSeconds(1)),
            fixture.Plan,
            command,
            fixture.Activation.Context);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Equal(cancelledSnapshot.Revision, replay.Snapshot?.Revision);
        Assert.Single(Assert.IsType<ProcessDurableStoreSnapshot>(replay.Snapshot).Checkpoint.Activations);
        Assert.Equal(0, host.RelationCalls);

        var conflictingContext = new ProcessActivationContext(
            fixture.Activation.Context.AuthorityScope,
            new("correlation/conflicting-cancellation-replay"),
            fixture.Activation.Context.Delivery,
            fixture.Activation.Context.Provenance);
        var conflict = await runtime.CancelAsync(
            Context(cancelAt.AddSeconds(2)),
            fixture.Plan,
            command,
            conflictingContext);

        Assert.Equal(ProcessDurableRuntimeDisposition.IdentityConflict, conflict.Disposition);
        Assert.Equal(cancelledSnapshot.Revision, conflict.Snapshot?.Revision);
        Assert.Single(Assert.IsType<ProcessDurableStoreSnapshot>(conflict.Snapshot).Checkpoint.Activations);
        Assert.Equal(0, host.RelationCalls);

        var alreadySatisfiedAt = cancelAt.AddSeconds(3);
        var alreadySatisfiedCommand = commands.Cancel(
            checkpoint.Control,
            id: "cancel/runtime-already-satisfied",
            issuedAtUtc: alreadySatisfiedAt);
        var alreadySatisfied = await runtime.CancelAsync(
            Context(alreadySatisfiedAt),
            fixture.Plan,
            alreadySatisfiedCommand,
            fixture.Activation.Context);
        var alreadySatisfiedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(alreadySatisfied.Snapshot);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, alreadySatisfied.Disposition);
        Assert.Equal(
            ProcessControlDecisionDisposition.AlreadySatisfied,
            alreadySatisfied.Decision?.Disposition);
        Assert.True(alreadySatisfiedSnapshot.Revision.Ordinal > cancelledSnapshot.Revision.Ordinal);
        Assert.Single(alreadySatisfiedSnapshot.Checkpoint.Activations);
        Assert.Equal(2, alreadySatisfiedSnapshot.Checkpoint.Control.Receipts.Length);
        Assert.Equal(
            ProcessControlReceiptDisposition.AlreadySatisfied,
            alreadySatisfiedSnapshot.Checkpoint.Control.Receipts[^1].Disposition);

        var alreadySatisfiedReplay = await runtime.CancelAsync(
            Context(alreadySatisfiedAt.AddSeconds(1)),
            fixture.Plan,
            alreadySatisfiedCommand,
            fixture.Activation.Context);

        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, alreadySatisfiedReplay.Disposition);
        Assert.Equal(alreadySatisfiedSnapshot.Revision, alreadySatisfiedReplay.Snapshot?.Revision);
        Assert.Single(
            Assert.IsType<ProcessDurableStoreSnapshot>(alreadySatisfiedReplay.Snapshot).Checkpoint.Activations);
        Assert.Equal(0, host.RelationCalls);
    }

    static (CompiledProcessPlan Plan, ProcessStartReceipt Start) SignalAwaitProcess(
        ProcessControlTestFixture controls)
    {
        var definition = new Cohesive.Processes.IR.ProcessDefinition(
            ProcessDurabilityTestFixture.StringContract,
            ProcessDurabilityTestFixture.StringContract,
            new("await-signal"),
            [
                new AwaitMatchProcessNode(
                    new("await-signal"),
                    ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                    [
                        new ProcessAwaitInteractionClause(
                            new("clause/signal"),
                            controls.SignalContract,
                            new(
                                new("await.signal"),
                                ProcessDurabilityTestFixture.StringContract),
                            requestObligation: null,
                            guard: null,
                            priority: 0,
                            new(new(
                                new("edge/signal-return"),
                                new("return"))))
                    ],
                    ProcessAwaitInputDisposition.Observe,
                    ProcessAwaitInputDisposition.Reject,
                    ProcessAwaitInputDisposition.ReusePriorDisposition,
                    ProcessAwaitMissingTargetDisposition.DeadLetter,
                    TimeSpan.FromDays(7)),
                new ReturnProcessNode(new("return"), Expr.Const("signal-consumed"))
            ],
            ProcessRecoveryPolicy.ContinueAttempt);
        return CompileSignalProcess(controls, "signal-await", definition);
    }

    static (CompiledProcessPlan Plan, ProcessStartReceipt Start) BufferedSignalProcess(
        ProcessControlTestFixture controls,
        ProcessRecoveryPolicy recoveryPolicy = ProcessRecoveryPolicy.ContinueAttempt)
    {
        var definition = new Cohesive.Processes.IR.ProcessDefinition(
            ProcessDurabilityTestFixture.StringContract,
            ProcessDurabilityTestFixture.StringContract,
            new("buffering-cut"),
            [
                new DurableCutProcessNode(
                    new("buffering-cut"),
                    new(
                        new("edge/cut-timer"),
                        new("timer"))),
                new TimerProcessNode(
                    new("timer"),
                    Expr.Const(ObservationValue.FromDateTimeOffset(
                        ProcessDurabilityTestFixture.ActivatedAtUtc.AddDays(1))),
                    new(
                        new("edge/timer-return"),
                        new("return"))),
                new ReturnProcessNode(new("return"), Expr.Const("timer-completed"))
            ],
            recoveryPolicy);
        return CompileSignalProcess(controls, "buffered-signal", definition);
    }

    static (CompiledProcessPlan Plan, ProcessStartReceipt Start) CompileSignalProcess(
        ProcessControlTestFixture controls,
        string identity,
        Cohesive.Processes.IR.ProcessDefinition definition)
    {
        var document = ProcessDefinitionDocuments.Create(
            new($"process/durable-runtime/{identity}"),
            new("revision/1"),
            definition,
            ProcessControlTestFixture.Provenance());
        var compilation = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext(
                interactionContracts: controls.Catalog));
        Assert.True(
            compilation.IsSuccessful,
            ProcessControlTestFixture.FormatDiagnostics(compilation.Validation));
        var plan = Assert.IsType<CompiledProcessPlan>(compilation.Plan);
        var continuation = new ProcessContinuationIdentity(
            new($"process-instance/durable-runtime-{identity}"),
            new("process-attempt/1"));
        var start = new ProcessStartReceipt(
            new(
                ProcessStartRequest.CurrentSchemaVersion,
                plan.DefinitionReference,
                new(
                    new($"start-command/durable-runtime-{identity}"),
                    new($"start-idempotency/durable-runtime-{identity}"),
                    continuation.ProcessInstanceId,
                    new(
                        "operator/tests",
                        ProcessControlTestFixture.Authority,
                        "policy/tests/allow"),
                    ProcessDurabilityTestFixture.IssuedAtUtc,
                    ProcessControlTestFixture.Provenance()),
                continuation,
                ProcessDurabilityTestFixture.StringValue("start")),
            ProcessDurabilityTestFixture.AcceptedAtUtc);
        return (plan, start);
    }

    static ProcessActivationContext SignalActivationContext() =>
        new(
            ProcessControlTestFixture.Authority,
            new("correlation/durable-runtime-signal"),
            new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            ProcessControlTestFixture.Provenance());

    static SignalEnvelope Signal(
        CompiledProcessPlan plan,
        ProcessControlTestFixture controls,
        ProcessTokenInteractionTarget target,
        ProcessActivationContext activationContext,
        string identity) =>
        new(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new($"emission/{identity}"),
                new ProcessInteractionOrigin(
                    plan.DefinitionReference,
                    new("source/signal-test"),
                    target.Continuation,
                    new("activation/signal-source"),
                    target.Token),
                activationContext.CorrelationId,
                causationId: null,
                activationContext.AuthorityScope,
                new($"idempotency/{identity}"),
                ordering: null,
                activationContext.Delivery,
                activationContext.Provenance),
            controls.SignalContract,
            ProcessDurabilityTestFixture.StringValue("ready"),
            target);

    static SignalProcessCommand SignalCommand(
        ProcessControlState state,
        SignalEnvelope signal,
        string id,
        DateTimeOffset issuedAtUtc) =>
        new(
            ProcessControlCommand.CurrentSchemaVersion,
            new(
                new(id),
                new($"idempotency/{id}"),
                state.ProcessInstanceId,
                new(
                    "operator/tests",
                    state.AuthorityScope,
                    "policy/tests/allow"),
                issuedAtUtc,
                ProcessControlTestFixture.Provenance()),
            Expectation(state),
            signal);

    static ProcessDurableRuntime Runtime(
        IProcessDurableStore store,
        ProcessDurabilityTestFixture fixture,
        IProcessReferenceHost host) =>
        Runtime(store, host, new BindingResolver(fixture.DurableOperation.Binding));

    static ProcessDurableRuntime Runtime(
        IProcessDurableStore store,
        IProcessReferenceHost host,
        IProcessDurableRequestBindingResolver? bindingResolver = null) =>
        new(
            store,
            host,
            new(
                workerId: "worker/durable-runtime-tests",
                WorkerLease),
            bindingResolver);

    static ProcessControlExpectation Expectation(ProcessControlState state) =>
        new(
            new(state.ProcessInstanceId, state.CurrentAttempt.AttemptId),
            state.Revision);

    static ReplyEnvelope HostOperationReply(ProcessDurabilityTestFixture fixture)
    {
        var pending = Assert.IsType<ReplyEnvelope>(fixture.PendingReply.Envelope);
        return new(
            pending.SchemaVersion,
            new(
                new("emission/reply/duplicate-host-operation"),
                new ProcessInteractionOrigin(
                    fixture.Plan.DefinitionReference,
                    fixture.Operation.Node,
                    fixture.Operation.Continuation,
                    fixture.Operation.Activation,
                    fixture.Operation.Token),
                pending.Context.CorrelationId,
                pending.InReplyTo,
                pending.Context.AuthorityScope,
                new("idempotency/reply/duplicate-host-operation"),
                pending.Context.Ordering,
                pending.Context.Delivery,
                pending.Context.Provenance),
            pending.Contract,
            pending.InReplyTo,
            pending.Outcome);
    }

    static OperationContext Context(DateTimeOffset utcNow) =>
        OperationContext.Create(timeProvider: new FixedTimeProvider(utcNow));

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    sealed class StepAtReadTimeProvider(
        DateTimeOffset initialUtcNow,
        DateTimeOffset steppedUtcNow,
        int readsBeforeStep) : TimeProvider
    {
        int reads;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref reads) <= readsBeforeStep
                ? initialUtcNow
                : steppedUtcNow;
    }

    sealed class BindingResolver(DurableRequestBinding binding) : IProcessDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? resolved)
        {
            resolved = binding;
            return true;
        }
    }

    sealed class RecordingHost(ProcessOperationResult result) : IProcessReferenceHost
    {
        internal int RelationCalls { get; private set; }

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation)
        {
            RelationCalls++;
            return result;
        }

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }

    sealed class TrackingStore(IProcessDurableStore inner) : DelegatingStore(inner)
    {
        internal int AcquireWorkerCalls { get; private set; }

        public override Task<ProcessStoreMutationResult> AcquireWorkerAsync(
            OperationContext context,
            ProcessInstanceId instanceId,
            ProcessStorageRevision expectedRevision,
            string owner,
            TimeSpan leaseDuration,
            DateTimeOffset observedAtUtc)
        {
            AcquireWorkerCalls++;
            return Inner.AcquireWorkerAsync(
                context,
                instanceId,
                expectedRevision,
                owner,
                leaseDuration,
                observedAtUtc);
        }
    }

    sealed class AcquisitionRecordingStore(IProcessDurableStore inner) : DelegatingStore(inner)
    {
        internal List<WorkerAcquisitionCall> Acquisitions { get; } = [];

        public override Task<ProcessStoreMutationResult> AcquireWorkerAsync(
            OperationContext context,
            ProcessInstanceId instanceId,
            ProcessStorageRevision expectedRevision,
            string owner,
            TimeSpan leaseDuration,
            DateTimeOffset observedAtUtc)
        {
            Acquisitions.Add(new(
                instanceId,
                expectedRevision,
                owner,
                leaseDuration,
                observedAtUtc));
            return base.AcquireWorkerAsync(
                context,
                instanceId,
                expectedRevision,
                owner,
                leaseDuration,
                observedAtUtc);
        }
    }

    sealed class RenewalTrackingStore(
        IProcessDurableStore inner,
        ProcessStoreMutationDisposition? rejection = null) : DelegatingStore(inner)
    {
        internal List<WorkerRenewalCall> Renewals { get; } = [];

        internal int CommitCalls { get; private set; }

        public override async Task<ProcessStoreMutationResult> RenewWorkerAsync(
            OperationContext context,
            ProcessInstanceId instanceId,
            string owner,
            ProcessWorkerFence fence,
            TimeSpan leaseDuration,
            DateTimeOffset observedAtUtc)
        {
            Renewals.Add(new(
                instanceId,
                owner,
                fence,
                leaseDuration,
                observedAtUtc));
            if (rejection is { } disposition)
            {
                var snapshot = await Inner.LoadAsync(context, instanceId);
                return new(disposition, snapshot);
            }

            return await base.RenewWorkerAsync(
                context,
                instanceId,
                owner,
                fence,
                leaseDuration,
                observedAtUtc);
        }

        public override Task<ProcessStoreMutationResult> CommitAsync(
            OperationContext context,
            ProcessDurableCommit commit)
        {
            CommitCalls++;
            return base.CommitAsync(context, commit);
        }
    }

    sealed class SupersedingCommitStore(
        InMemoryProcessDurableStore inner,
        TimeSpan workerLease) : DelegatingStore(inner)
    {
        bool superseded;

        public override async Task<ProcessStoreMutationResult> CommitAsync(
            OperationContext context,
            ProcessDurableCommit commit)
        {
            if (superseded)
            {
                return await base.CommitAsync(context, commit);
            }

            superseded = true;
            var instanceId = commit.Checkpoint.ContinuationIdentity.ProcessInstanceId;
            var current = Assert.IsType<ProcessDurableStoreSnapshot>(
                await Inner.LoadAsync(context, instanceId));
            var currentLease = Assert.IsType<ProcessWorkerLease>(current.WorkerLease);
            var acquired = await Inner.AcquireWorkerAsync(
                context,
                instanceId,
                current.Revision,
                "worker/superseding-runtime-tests",
                workerLease,
                currentLease.ExpiresAtUtc);

            Assert.Equal(ProcessStoreMutationDisposition.Applied, acquired.Disposition);
            return await Inner.CommitAsync(context, commit);
        }
    }

    enum ProviderCancellationMutation
    {
        Initialization,
        Commit
    }

    sealed class ProviderCancellationStore(
        IProcessDurableStore inner,
        ProviderCancellationMutation mutation,
        bool taskCanceledException,
        CancellationTokenSource? callerCancellation = null) : DelegatingStore(inner)
    {
        bool thrown;

        internal List<(ProcessCommitId Id, ProcessCommitFingerprint Fingerprint)> Initializations { get; } = [];

        internal List<(ProcessCommitId Id, ProcessCommitFingerprint Fingerprint)> Commits { get; } = [];

        public override Task<ProcessStoreMutationResult> InitializeAsync(
            OperationContext context,
            ProcessCommitId commitId,
            ProcessDurableCheckpoint checkpoint)
        {
            Initializations.Add((commitId, ProcessDurableCommitFingerprinter.ComputeCheckpoint(checkpoint)));
            if (mutation == ProviderCancellationMutation.Initialization && !thrown)
            {
                thrown = true;
                return ProviderCancellation(context);
            }

            return base.InitializeAsync(context, commitId, checkpoint);
        }

        public override Task<ProcessStoreMutationResult> CommitAsync(
            OperationContext context,
            ProcessDurableCommit commit)
        {
            Commits.Add((commit.Id, commit.Fingerprint));
            if (mutation == ProviderCancellationMutation.Commit && !thrown)
            {
                thrown = true;
                return ProviderCancellation(context);
            }

            return base.CommitAsync(context, commit);
        }

        Task<ProcessStoreMutationResult> ProviderCancellation(OperationContext context)
        {
            if (callerCancellation is not null)
            {
                callerCancellation.Cancel();
                return Task.FromException<ProcessStoreMutationResult>(
                    new OperationCanceledException("The caller cancelled the store mutation.", callerCancellation.Token));
            }

            return Task.FromException<ProcessStoreMutationResult>(
                taskCanceledException
                    ? new TaskCanceledException("The provider timed out independently.")
                    : new OperationCanceledException("The provider cancelled independently."));
        }
    }

    abstract class DelegatingStore(IProcessDurableStore inner) : IProcessDurableStore
    {
        protected IProcessDurableStore Inner { get; } = inner;

        public ProcessDurableStoreCapabilities Capabilities => Inner.Capabilities;

        public virtual Task<ProcessDurableStoreSnapshot?> LoadAsync(
            OperationContext context,
            ProcessInstanceId instanceId) =>
            Inner.LoadAsync(context, instanceId);

        public virtual Task<ProcessStoreMutationResult> InitializeAsync(
            OperationContext context,
            ProcessCommitId commitId,
            ProcessDurableCheckpoint checkpoint) =>
            Inner.InitializeAsync(context, commitId, checkpoint);

        public virtual Task<ProcessStoreMutationResult> AdmitInputAsync(
            OperationContext context,
            ProcessInstanceId instanceId,
            ProcessActivationInput input,
            DateTimeOffset admittedAtUtc) =>
            Inner.AdmitInputAsync(context, instanceId, input, admittedAtUtc);

        public virtual Task<ProcessStoreMutationResult> AcquireWorkerAsync(
            OperationContext context,
            ProcessInstanceId instanceId,
            ProcessStorageRevision expectedRevision,
            string owner,
            TimeSpan leaseDuration,
            DateTimeOffset observedAtUtc) =>
            Inner.AcquireWorkerAsync(
                context,
                instanceId,
                expectedRevision,
                owner,
                leaseDuration,
                observedAtUtc);

        public virtual Task<ProcessStoreMutationResult> RenewWorkerAsync(
            OperationContext context,
            ProcessInstanceId instanceId,
            string owner,
            ProcessWorkerFence fence,
            TimeSpan leaseDuration,
            DateTimeOffset observedAtUtc) =>
            Inner.RenewWorkerAsync(
                context,
                instanceId,
                owner,
                fence,
                leaseDuration,
                observedAtUtc);

        public virtual Task<ProcessStoreMutationResult> CommitAsync(
            OperationContext context,
            ProcessDurableCommit commit) =>
            Inner.CommitAsync(context, commit);
    }

    sealed record WorkerAcquisitionCall(
        ProcessInstanceId InstanceId,
        ProcessStorageRevision ExpectedRevision,
        string Owner,
        TimeSpan LeaseDuration,
        DateTimeOffset ObservedAtUtc);

    sealed record WorkerRenewalCall(
        ProcessInstanceId InstanceId,
        string Owner,
        ProcessWorkerFence Fence,
        TimeSpan LeaseDuration,
        DateTimeOffset ObservedAtUtc);
}
