using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessDurableFingerprintPolicyTests
{
    [Theory]
    [InlineData(ProcessCheckpointVerification.Standard)]
    [InlineData(ProcessCheckpointVerification.Diagnostic)]
    public async Task VerificationPolicyPreservesDurabilityAndRoundTrips(ProcessCheckpointVerification verification)
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var store = new InMemoryProcessDurableStore();
        var runtime = new ProcessDurableRuntime(store, new Host(fixture.OperationResult),
            new("worker/policy", TimeSpan.FromMinutes(5), checkpointVerification: verification),
            new Bindings(fixture.DurableOperation.Binding));
        await runtime.InitializeAsync(Context(fixture.Start.AcceptedAtUtc), fixture.Plan, fixture.Start);
        var result = await runtime.ActivateAsync(Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan, fixture.Checkpoint.ContinuationIdentity, fixture.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        var receipt = Assert.Single(result.Snapshot!.Checkpoint.Activations);
        Assert.Equal(1, receipt.Sequence);
        Assert.Equal(verification == ProcessCheckpointVerification.Diagnostic, receipt.BeforeContinuation.HasValue);
        Assert.Equal(verification == ProcessCheckpointVerification.Diagnostic, receipt.AfterContinuation.HasValue);
        var restored = new InMemoryProcessDurableStore(ProcessDurableStoreJsonSerializer.Deserialize(
            ProcessDurableStoreJsonSerializer.Serialize(store.CaptureDocument())));
        var replay = await restored.CommitAsync(Context(ProcessDurabilityTestFixture.CheckpointedAtUtc), result.Commit!);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, replay.Disposition);
        Assert.Equal(result.Snapshot.Revision, replay.Snapshot!.Revision);
        Assert.True(ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, replay.Snapshot.Checkpoint).IsValid);

        if (verification == ProcessCheckpointVerification.Diagnostic)
        {
            var json = JsonNode.Parse(ProcessDurableCheckpointJsonSerializer.Serialize(replay.Snapshot.Checkpoint))!;
            json["activations"]![0]!["afterContinuation"] = "sha256-v1:" + new string('0', 64);
            Assert.False(ProcessDurableCheckpointJsonSerializer.TryDeserialize(json.ToJsonString(), fixture.Plan, out _).IsValid);
        }
    }

    [Fact]
    public async Task RetryAfterRestartAndLaterCommitComparesOriginalCheckpointAndIntent()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var checkpoint = Initial(fixture, "accepted");
        var now = checkpoint.UpdatedAtUtc;
        var context = Context(now);
        var store = new InMemoryProcessDurableStore();
        var initialized = await store.InitializeAsync(context, new("initialize"), checkpoint);
        var acquired = await store.AcquireWorkerAsync(context, checkpoint.ContinuationIdentity.ProcessInstanceId,
            initialized.Snapshot!.Revision, "worker/policy", TimeSpan.FromMinutes(5), now);
        var commit = Commit(checkpoint, acquired.Snapshot!.Revision, acquired.Snapshot.WorkerLease!.Fence);
        var first = await store.CommitAsync(context, commit);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, first.Disposition);
        var later = new ProcessDurableCommit(new("commit/later"), first.Snapshot!.Revision, commit.Owner,
            commit.Fence, checkpoint, [], now);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, (await store.CommitAsync(context, later)).Disposition);
        var restored = new InMemoryProcessDurableStore(ProcessDurableStoreJsonSerializer.Deserialize(
            ProcessDurableStoreJsonSerializer.Serialize(store.CaptureDocument())));
        Assert.Equal(ProcessStoreMutationDisposition.Replayed, (await restored.CommitAsync(context, commit)).Disposition);
        var altered = Commit(Initial(fixture, "different"), commit.ExpectedRevision, commit.Fence);
        // The small metadata digest intentionally excludes the checkpoint. The retained snapshot catches this conflict.
        Assert.Equal(commit.IntentFingerprint, altered.IntentFingerprint);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, (await restored.CommitAsync(context, altered)).Disposition);
        var changedOwner = new ProcessDurableCommit(commit.Id, commit.ExpectedRevision, "worker/other", commit.Fence,
            checkpoint, [], now);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, (await restored.CommitAsync(context, changedOwner)).Disposition);
        var changedMutations = new ProcessDurableCommit(commit.Id, commit.ExpectedRevision, commit.Owner, commit.Fence,
            checkpoint, [new("mutation", "resource", ProcessDurabilityTestFixture.StringValue("different"))], now);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, (await restored.CommitAsync(context, changedMutations)).Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.Replayed,
            (await restored.InitializeAsync(context, new("initialize"), checkpoint)).Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict,
            (await restored.InitializeAsync(context, new("initialize"), Initial(fixture, "different"))).Disposition);
    }

    [Theory]
    [InlineData(131072)]
    [InlineData(1048576)]
    public void FreshCommitConstructionAndInitializationDoNotAllocateWithCheckpointSize(int payloadBytes)
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var checkpoint = Initial(fixture, new string('x', payloadBytes));
        var context = Context(checkpoint.UpdatedAtUtc);
        Action construct = () => GC.KeepAlive(Commit(checkpoint, new("1"), new("1")));
        Action initialize = () => GC.KeepAlive(new InMemoryProcessDurableStore()
            .InitializeAsync(context, new("initialize"), checkpoint).GetAwaiter().GetResult());
        Assert.InRange(MedianAllocation(construct), 0, 2048);
        Assert.InRange(MedianAllocation(initialize), 0, 3072);
    }

    [Fact]
    public void VerificationPolicyRejectsUndefinedValuesAndPartialDiagnosticEvidence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessDurableRuntimeOptions("worker", TimeSpan.FromMinutes(1),
            checkpointVerification: (ProcessCheckpointVerification)99));
        var receipt = Assert.Single(ProcessDurabilityTestFixture.Create().Checkpoint.Activations);
        Assert.Throws<ArgumentException>(() => new ProcessActivationCommitReceipt(receipt.Sequence, receipt.Continuation,
            receipt.BeforeContinuation, null, receipt.Activation, receipt.Disposition, receipt.Evidence, receipt.CommittedAtUtc));
    }

    static long MedianAllocation(Action action)
    {
        for (var i = 0; i < 8; i++) action();
        long[] samples = new long[7];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 16; i++) action();
            samples[sample] = (GC.GetAllocatedBytesForCurrentThread() - before) / 16;
        }
        Array.Sort(samples);
        return samples[3];
    }

    static ProcessDurableCheckpoint Initial(ProcessDurabilityTestFixture fixture, string input)
    {
        var source = fixture.Start.Request;
        var start = new ProcessStartReceipt(new(source.SchemaVersion, source.Definition, source.Context,
            source.InitialContinuation, ProcessDurabilityTestFixture.StringValue(input)), fixture.Start.AcceptedAtUtc);
        return new(ProcessDurableCheckpoint.CurrentSchemaVersion, start, ProcessReferenceInterpreter.Create(fixture.Plan, start),
            start.CreateInitialState(), createdAtUtc: start.AcceptedAtUtc, updatedAtUtc: start.AcceptedAtUtc);
    }

    static ProcessDurableCommit Commit(ProcessDurableCheckpoint checkpoint, ProcessStorageRevision revision, ProcessWorkerFence fence) =>
        new(new("commit"), revision, "worker/policy", fence, checkpoint, [], checkpoint.UpdatedAtUtc);

    static OperationContext Context(DateTimeOffset now) => OperationContext.Create(timeProvider: new Clock(now));
    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    sealed class Bindings(DurableRequestBinding binding) : IDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? resolved)
        {
            resolved = binding;
            return true;
        }
    }
    sealed class Host(ProcessOperationResult result) : IProcessReferenceHost
    {
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) => result;
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => throw new NotSupportedException();
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new NotSupportedException();
    }
}
