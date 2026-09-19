using System.Diagnostics;
using Cohesive.Model.Serialization;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;
using Cohesive.Tests.ExecutionKernel;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteProcessDurableStoreTests
{
    static readonly OperationContext Context = OperationContext.Create();
    internal static SqliteDatabase Database(string path) => new(new(path, durability: SqliteDurability.Full));
    internal static SqliteProcessDurableStore Open(string path, string authority = "process-tests", int maximumBytes = 4 * 1024 * 1024) =>
        new(Database(path), authority, maximumBytes);
    static SqliteProcessDurableStore Create(DatabaseFixture file)
    {
        SqliteProcessDurableStore.Schema.Apply(Database(file.Path));
        return Open(file.Path);
    }

    [Fact]
    public void ProfileRejectsWeakDurabilityAndClassifiesOnlyProviderFailuresAsAmbiguous()
    {
        using var file = new DatabaseFixture();
        Assert.Throws<ArgumentException>(() => new SqliteProcessDurableStore(new(new(file.Path, durability: SqliteDurability.Normal)), "authority"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Open(file.Path, maximumBytes: 0));
        var store = Open(file.Path, maximumBytes: 1024);
        Assert.Equal(1024, store.Capabilities.MaxCommitBytes);
        Assert.True(store.Capabilities.SupportsAtomicAggregateCommit);
        Assert.True(store.Capabilities.SupportsCompareAndSwap);
        Assert.True(store.Capabilities.SupportsWorkerFencing);
        var classifier = SqliteProcessStoreMutationExceptionClassifier.Instance;
        Assert.Equal(ProcessStoreMutationExceptionClassification.NotAmbiguous, classifier.Classify(new InvalidDataException("size")));
        Assert.Equal(ProcessStoreMutationExceptionClassification.Ambiguous, classifier.Classify(new Microsoft.Data.Sqlite.SqliteException("native", 5)));
        Assert.Equal(ProcessStoreMutationExceptionClassification.Ambiguous, classifier.Classify(new OperationCanceledException()));
    }

    [Fact]
    public async Task NativeRoundTripPreservesInboxOutboxOperationsLocalValuesAndOriginalReceipts()
    {
        using var file = new DatabaseFixture();
        var store = Create(file);
        var fixture = ProcessDurabilityTestFixture.Create();
        var checkpoint = fixture.Checkpoint;
        var instance = checkpoint.ContinuationIdentity.ProcessInstanceId;
        var original = await store.InitializeAsync(Context, new("initialize"), checkpoint);
        var reference = new InMemoryProcessDurableStore();
        await reference.InitializeAsync(Context, new("initialize"), checkpoint);
        var now = DateTimeOffset.UtcNow;
        var acquired = await Open(file.Path).AcquireWorkerAsync(Context, instance, original.Snapshot!.Revision, "worker", TimeSpan.FromMinutes(5), now);
        await reference.AcquireWorkerAsync(Context, instance, original.Snapshot.Revision, "worker", TimeSpan.FromMinutes(5), now);
        var next = WithTime(checkpoint, now);
        var commit = new ProcessDurableCommit(new("commit"), acquired.Snapshot!.Revision, "worker", acquired.Snapshot.WorkerLease!.Fence,
            next, [new("local-mutation", "resource", ProcessDurabilityTestFixture.StringValue("value"), expectedVersion: 0)], now);
        var committed = await Open(file.Path).CommitAsync(Context, commit);
        var expected = await reference.CommitAsync(Context, commit);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, committed.Disposition);
        EqualSnapshot(expected.Snapshot!, committed.Snapshot!);
        EqualSnapshot(committed.Snapshot!, (await Open(file.Path).LoadAsync(Context, instance))!);
        Assert.NotEmpty(committed.Snapshot!.Checkpoint.Inbox);
        Assert.NotEmpty(committed.Snapshot.Checkpoint.Emissions);
        Assert.NotEmpty(committed.Snapshot.Checkpoint.DurableOperations);
        Assert.Single(committed.Snapshot.LocalState);
        var renewed = await Open(file.Path).RenewWorkerAsync(Context, instance, "worker", acquired.Snapshot.WorkerLease.Fence,
            TimeSpan.FromMinutes(10), now.AddSeconds(1));
        Assert.Equal(ProcessStoreMutationDisposition.Applied, renewed.Disposition);
        var replay = await Open(file.Path).CommitAsync(Context, commit);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, replay.Disposition);
        EqualSnapshot(committed.Snapshot, replay.Snapshot!);
        Assert.Equal(renewed.Snapshot!.Revision, (await Open(file.Path).LoadAsync(Context, instance))!.Revision);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, (await Open(file.Path).InitializeAsync(Context, new("initialize"), checkpoint)).Disposition);
        Assert.Null(await Open(file.Path, "other-authority").LoadAsync(Context, instance));
    }

    [Fact]
    public async Task CompetingWorkersCannotShareAFenceAndStaleRevisionCannotClaim()
    {
        using var file = new DatabaseFixture();
        var store = Create(file);
        var checkpoint = ProcessDurabilityTestFixture.Create().Checkpoint;
        var instance = checkpoint.ContinuationIdentity.ProcessInstanceId;
        var initialized = await store.InitializeAsync(Context, new("initialize"), checkpoint);
        var now = DateTimeOffset.UtcNow;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            Open(file.Path).AcquireWorkerAsync(Context, instance, initialized.Snapshot!.Revision, "worker/" + index, TimeSpan.FromMinutes(5), now))));
        var winner = Assert.Single(results, result => result.Disposition == ProcessStoreMutationDisposition.Applied).Snapshot!;
        Assert.Equal(7, results.Count(result => result.Disposition == ProcessStoreMutationDisposition.RevisionConflict));
        var reclaimed = await Open(file.Path).AcquireWorkerAsync(Context, instance, winner.Revision, "replacement", TimeSpan.FromMinutes(5), now.AddMinutes(6));
        Assert.Equal(ProcessStoreMutationDisposition.Applied, reclaimed.Disposition);
        Assert.True(reclaimed.Snapshot!.WorkerLease!.Fence.Ordinal > winner.WorkerLease!.Fence.Ordinal);
        Assert.Equal(ProcessStoreMutationDisposition.StaleFence, (await Open(file.Path).RenewWorkerAsync(Context, instance,
            winner.WorkerLease.Owner, winner.WorkerLease.Fence, TimeSpan.FromMinutes(5), now.AddMinutes(7))).Disposition);
    }

    [Fact]
    public async Task BoundsCancellationAndExpiredPhysicalLeaseDoNotPublish()
    {
        using var file = new DatabaseFixture();
        var store = Create(file);
        var checkpoint = ProcessDurabilityTestFixture.Create().Checkpoint;
        var instance = checkpoint.ContinuationIdentity.ProcessInstanceId;
        await Assert.ThrowsAsync<InvalidDataException>(() => Open(file.Path, maximumBytes: 1).InitializeAsync(Context, new("initialize"), checkpoint));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InitializeAsync(Context.WithCancellationToken(new(true)), new("initialize"), checkpoint));
        Assert.Null(await store.LoadAsync(Context, instance));
        var initialized = await store.InitializeAsync(Context, new("initialize"), checkpoint);
        var historical = checkpoint.UpdatedAtUtc.AddMinutes(1);
        var acquired = await store.AcquireWorkerAsync(Context, instance, initialized.Snapshot!.Revision, "worker", TimeSpan.FromSeconds(1), historical);
        var commit = new ProcessDurableCommit(new("expired"), acquired.Snapshot!.Revision, "worker", acquired.Snapshot.WorkerLease!.Fence,
            WithTime(checkpoint, historical), [], historical);
        // A caller-provided historical replay clock cannot extend an expired physical lease.
        var result = await Open(file.Path).CommitAsync(DurableOperationTestFixture.ContextAt(historical), commit);
        Assert.Equal(ProcessStoreMutationDisposition.LeaseExpired, result.Disposition);
        Assert.Equal(acquired.Snapshot.Revision, (await store.LoadAsync(Context, instance))!.Revision);
        await Assert.ThrowsAsync<InvalidDataException>(() => Open(file.Path, maximumBytes: 1).LoadAsync(Context, instance));
    }

    [Fact]
    public async Task DurableRuntimeReopensActivationWithoutRepeatingHostWork()
    {
        using var file = new DatabaseFixture();
        var store = Create(file);
        var fixture = ProcessDurabilityTestFixture.Create();
        var host = new Host(fixture.OperationResult);
        var runtime = Runtime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(Context, fixture.Plan, fixture.Start);
        var activated = await runtime.ActivateAsync(Context, fixture.Plan, initialized.Snapshot!.Checkpoint.ContinuationIdentity, fixture.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, activated.Disposition);
        Assert.Equal(1, host.Calls);
        var reopenedHost = new Host(fixture.OperationResult);
        var replay = await Runtime(Open(file.Path), fixture, reopenedHost).ActivateAsync(Context, fixture.Plan,
            initialized.Snapshot.Checkpoint.ContinuationIdentity, fixture.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Equal(0, reopenedHost.Calls);
        Assert.Single(replay.Snapshot!.Checkpoint.Activations);
        Assert.Single(replay.Snapshot.Checkpoint.DurableOperations);
        Assert.Single(replay.Snapshot.Checkpoint.Emissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KilledWriterPreservesOnlyCommittedProcessEvidence(bool afterCommit)
    {
        using var file = new DatabaseFixture();
        var store = Create(file);
        var fixture = ProcessDurabilityTestFixture.Create();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(SqliteCrashWorker).Assembly.Location);
        start.ArgumentList.Add(afterCommit ? "--process-retain-worker" : "--sqlite-crash-worker");
        start.ArgumentList.Add(file.Path);
        start.ArgumentList.Add(afterCommit ? "activate" : "DELETE FROM __cohesive_storage_commits_v1;");
        if (!afterCommit) await store.InitializeAsync(Context, new("initialize"), fixture.Checkpoint);
        using var worker = Process.Start(start)!;
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            Assert.Equal(afterCommit ? "activated" : "uncommitted", await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var reopened = Open(file.Path);
            var snapshot = await reopened.LoadAsync(Context, fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId);
            Assert.NotNull(snapshot);
            Assert.Single(snapshot.Checkpoint.Activations);
            Assert.Single(snapshot.Checkpoint.Emissions);
            Assert.Single(snapshot.Checkpoint.DurableOperations);
            if (afterCommit)
            {
                var host = new Host(fixture.OperationResult);
                var result = await Runtime(reopened, fixture, host).ActivateAsync(Context, fixture.Plan, snapshot.Checkpoint.ContinuationIdentity, fixture.Activation);
                Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, result.Disposition);
                Assert.Equal(0, host.Calls);
            }
            else Assert.Equal(ProcessStoreMutationDisposition.Replayed, (await reopened.InitializeAsync(Context, new("initialize"), fixture.Checkpoint)).Disposition);
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync();
            Assert.Equal("", await errors);
        }
    }

    [Theory]
    [InlineData("DELETE FROM __cohesive_storage_commits_v1 WHERE kind = 'receipt'")]
    [InlineData("UPDATE __cohesive_storage_commits_v1 SET token = 'changed' WHERE kind = 'item'")]
    public async Task CorruptCommitEvidenceCannotAuthorizeReadOrReplay(string sabotage)
    {
        using var file = new DatabaseFixture();
        var store = Create(file);
        var checkpoint = ProcessDurabilityTestFixture.Create().Checkpoint;
        await store.InitializeAsync(Context, new("initialize"), checkpoint);
        using (var connection = Database(file.Path).OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sabotage;
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Open(file.Path).LoadAsync(Context, checkpoint.ContinuationIdentity.ProcessInstanceId));
        await Assert.ThrowsAsync<InvalidDataException>(() => Open(file.Path).InitializeAsync(Context, new("initialize"), checkpoint));
    }

    internal static ProcessDurableRuntime Runtime(IProcessDurableStore store, ProcessDurabilityTestFixture fixture, IProcessReferenceHost host) =>
        new(store, host, new("worker", TimeSpan.FromMinutes(5)), new Binding(fixture.DurableOperation.Binding),
            storeMutationExceptionClassifier: SqliteProcessStoreMutationExceptionClassifier.Instance);
    sealed class Binding(DurableRequestBinding binding) : IDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? resolved) { resolved = binding; return true; }
    }
    internal sealed class Host(ProcessOperationResult result) : IProcessReferenceHost
    {
        internal int Calls { get; private set; }
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) { Calls++; return result; }
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => throw new InvalidOperationException();
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new InvalidOperationException();
    }
    static ProcessDurableCheckpoint WithTime(ProcessDurableCheckpoint checkpoint, DateTimeOffset now) =>
        new(checkpoint.SchemaVersion, checkpoint.Start, checkpoint.Continuation, checkpoint.Control, checkpoint.Activations,
            checkpoint.Operations, checkpoint.Inbox, checkpoint.Emissions, checkpoint.DurableOperations, checkpoint.CreatedAtUtc, now);
    static void EqualSnapshot(ProcessDurableStoreSnapshot expected, ProcessDurableStoreSnapshot actual)
    {
        Assert.Equal(StrictDocumentJson.GetCanonicalBytes(expected, StrictDocumentJson.CreateOptions()),
            StrictDocumentJson.GetCanonicalBytes(actual, StrictDocumentJson.CreateOptions()));
    }
}
