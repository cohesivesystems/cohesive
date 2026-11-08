using Cohesive.Model;
using Cohesive.Processes.Compilation;
using System.Text.Json.Nodes;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed partial class ProcessDurableRuntimeTests
{
    [Fact]
    public async Task RetainedCompilationPolicySurvivesCommitAndRejectsReplacementBeforeLeaseOrEffects()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var options = new ProcessCompilationOptions(requiredGuarantees: ProcessRecoveryRequirements.WorkerTakeover);
        var plan = ProcessStaticCompiler.Compile(fixture.Plan.Document, fixture.Plan.ValidationContext, options).Plan!;
        var profile = new ProcessInterpreterCapabilityProfile(new("test/recovery"), new("test/runtime"),
            [.. options.RequiredGuarantees.Select(key => new ProcessInterpreterCapabilityEvidence(
                new("test/" + key.Name), key, CapabilityRealizationKind.Native))]);
        var store = new InMemoryProcessDurableStore();
        var host = new AsyncRecordingHost(fixture.OperationResult);
        ProcessDurableRuntime QualifiedRuntime() => new(store, _ => host,
            new("worker/policy", WorkerLease), new BindingResolver(fixture.DurableOperation.Binding), recoveryProfile: profile);
        var initialized = await QualifiedRuntime().InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc), plan, fixture.Start);
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        Assert.Equal(options, before.Checkpoint.CompilationOptions);
        var json = ProcessDurableCheckpointJsonSerializer.Serialize(before.Checkpoint);
        Assert.Equal(options, ProcessDurableCheckpointJsonSerializer.Deserialize(json, plan).CompilationOptions);
        var missing = JsonNode.Parse(json)!.AsObject();
        missing.Remove("compilationOptions");
        Assert.False(ProcessDurableCheckpointJsonSerializer.TryDeserialize(missing.ToJsonString(), plan, out _).IsValid);
        missing = JsonNode.Parse(json)!.AsObject();
        missing["compilationOptions"] = null;
        Assert.False(ProcessDurableCheckpointJsonSerializer.TryDeserialize(missing.ToJsonString(), plan, out _).IsValid);

        // Both an omitted policy and each individually weakened guarantee must fail before acquisition.
        var policies = options.RequiredGuarantees.Select(removed => new ProcessCompilationOptions(
            requiredGuarantees: [.. options.RequiredGuarantees.Where(key => key != removed)]))
            .Append(ProcessCompilationOptions.Default);
        foreach (var weaker in policies)
        {
            var changedPlan = ProcessStaticCompiler.Compile(fixture.Plan.Document, fixture.Plan.ValidationContext, weaker).Plan!;
            Assert.Equal(plan.DefinitionReference, changedPlan.DefinitionReference);
            var result = await QualifiedRuntime().ActivateAsync(Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
                changedPlan, before.Checkpoint.ContinuationIdentity, fixture.Activation);
            Assert.Equal(ProcessDurableRuntimeDisposition.Incompatible, result.Disposition);
            Assert.Contains(result.Diagnostics, d => d.Code == ProcessCheckpointDiagnosticCodes.CompilationPolicyIncompatible);
            var retained = await store.LoadAsync(Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
                before.Checkpoint.ContinuationIdentity.ProcessInstanceId);
            Assert.Equal(before, retained);
            Assert.Equal(0, host.RelationCalls);
        }

        var unqualified = await AsyncRuntime(store, fixture, host).ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc), plan, before.Checkpoint.ContinuationIdentity, fixture.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Incompatible, unqualified.Disposition);
        Assert.Contains(unqualified.Diagnostics, d => d.Location == "/recovery/transitionEffectFencing");
        Assert.Equal(0, host.RelationCalls);

        var activated = await QualifiedRuntime().ActivateAsync(Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            plan, before.Checkpoint.ContinuationIdentity, fixture.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, activated.Disposition);
        Assert.Equal(options, activated.Snapshot!.Checkpoint.CompilationOptions);
        var restored = ProcessDurableCheckpointJsonSerializer.Deserialize(
            ProcessDurableCheckpointJsonSerializer.Serialize(activated.Snapshot.Checkpoint), plan);
        Assert.Equal(options, restored.CompilationOptions);
        var weakenedReplay = await QualifiedRuntime().ActivateAsync(Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan, before.Checkpoint.ContinuationIdentity, fixture.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Incompatible, weakenedReplay.Disposition);
        Assert.Contains(weakenedReplay.Diagnostics, d => d.Code == ProcessCheckpointDiagnosticCodes.CompilationPolicyIncompatible);
        var replay = await QualifiedRuntime().ActivateAsync(Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            plan, before.Checkpoint.ContinuationIdentity, fixture.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Equal(1, host.RelationCalls);
    }

    [Fact]
    public async Task ExplicitRecoveryDemandRejectsUnqualifiedHostBeforeCheckpointInitialization()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/recovery/admission", semanticVariant: "recovery/admission");
        var plan = Cohesive.Processes.Compilation.ProcessStaticCompiler.Compile(fixture.Plan.Document,
            fixture.Plan.ValidationContext, new(requiredGuarantees: Cohesive.Processes.Compilation.ProcessRecoveryRequirements.WorkerTakeover)).Plan!;
        var store = new InMemoryProcessDurableStore();
        var host = new AsyncRecordingHost(fixture.OperationResult);
        var runtime = AsyncRuntime(store, fixture, host);
        var result = await runtime.InitializeAsync(Context(ProcessDurabilityTestFixture.AcceptedAtUtc), plan, fixture.Start);
        Assert.Equal(ProcessDurableRuntimeDisposition.Incompatible, result.Disposition);
        Assert.Contains(result.Diagnostics, d => d.Location == "/recovery/transitionEffectFencing");
        Assert.Null(await store.LoadAsync(Context(ProcessDurabilityTestFixture.AcceptedAtUtc), fixture.Start.Request.Context.ProcessInstanceId));
        Assert.Equal(0, host.RelationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncHost_CommitRecoveryAndNewDriverReplayRetainOneObservation(bool crashAfterCommit)
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime/async-recovery/{crashAfterCommit}",
            semanticVariant: $"async-recovery/{crashAfterCommit}");
        var crash = ProcessStoreCrashScript.Once(ProcessStoreMutationKind.AggregateCommit,
            crashAfterCommit ? ProcessStoreCrashPhase.AfterAtomicCommitBeforeReturn : ProcessStoreCrashPhase.BeforeAtomicCommit);
        var store = new InMemoryProcessDurableStore(crash.ShouldCrash);
        var host = new AsyncRecordingHost(fixture.OperationResult);
        var runtime = AsyncRuntime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc), fixture.Plan, fixture.Start);
        var continuation = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint.ContinuationIdentity;

        var activated = await runtime.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc), fixture.Plan, continuation, fixture.Activation);

        Assert.True(crash.IsComplete);
        Assert.Equal(crashAfterCommit ? ProcessDurableRuntimeDisposition.Replayed : ProcessDurableRuntimeDisposition.Applied,
            activated.Disposition);
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(activated.Snapshot).Checkpoint;
        Assert.Equal(fixture.OperationResult, Assert.Single(checkpoint.Operations).Result);
        Assert.Single(checkpoint.Activations);
        Assert.Single(checkpoint.Emissions);
        Assert.Single(checkpoint.DurableOperations);
        Assert.Equal(1, host.RelationCalls);

        // A new worker object must recover from stored evidence, not an activation-local cache.
        var restartedHost = new AsyncRecordingHost(fixture.OperationResult);
        var restarted = AsyncRuntime(store, fixture, restartedHost);
        var replay = await restarted.ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc), fixture.Plan, continuation, fixture.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Equal(0, restartedHost.RelationCalls);
        Assert.Equal(0, restarted.RetainedInstanceGateCount);
    }

    [Fact]
    public async Task AsyncHost_CancellationAfterAwaitDoesNotCommitPartialEvidence_AndCanRetry()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/async-cancellation", semanticVariant: "async-cancellation");
        var store = new InMemoryProcessDurableStore();
        using var cancellation = new CancellationTokenSource();
        var host = new AsyncRecordingHost(fixture.OperationResult, cancellation.Cancel);
        var runtime = AsyncRuntime(store, fixture, host);
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc), fixture.Plan, fixture.Start);
        var continuation = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot).Checkpoint.ContinuationIdentity;
        var context = OperationContext.Create(
            timeProvider: new FixedTimeProvider(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            cancellationToken: cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ActivateAsync(
            context, fixture.Plan, continuation, fixture.Activation));

        var retained = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc), continuation.ProcessInstanceId));
        Assert.Empty(retained.Checkpoint.Operations);
        Assert.Empty(retained.Checkpoint.Activations);
        Assert.Empty(retained.Checkpoint.Emissions);
        Assert.Equal(0, runtime.RetainedInstanceGateCount);
        var recoveredHost = new AsyncRecordingHost(fixture.OperationResult);
        var recovered = await AsyncRuntime(store, fixture, recoveredHost).ActivateAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc), fixture.Plan, continuation, fixture.Activation);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, recovered.Disposition);
        Assert.Equal(1, recoveredHost.RelationCalls);
    }

    [Theory]
    [InlineData(ProcessStoreMutationDisposition.StaleFence, ProcessDurableRuntimeDisposition.StaleFence)]
    [InlineData(ProcessStoreMutationDisposition.LeaseExpired, ProcessDurableRuntimeDisposition.LeaseExpired)]
    public async Task ActivationHost_ReceivesAcquiredLease_AndFenceRejectionLeavesCheckpointUnchanged(
        ProcessStoreMutationDisposition rejection, ProcessDurableRuntimeDisposition expected)
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime/fenced-host", semanticVariant: "fenced-host");
        var store = new InMemoryProcessDurableStore();
        ProcessDurableStoreSnapshot? acquired = null;
        var host = new AsyncRecordingHost(fixture.OperationResult, () => throw new ProcessWorkerFenceException(rejection));
        var runtime = new ProcessDurableRuntime(store, activationHostFactory: snapshot =>
        {
            acquired = snapshot;
            Assert.NotNull(snapshot.WorkerLease);
            return host;
        }, options: new("worker/fenced", WorkerLease),
            bindingResolver: new BindingResolver(fixture.DurableOperation.Binding));
        var initialized = await runtime.InitializeAsync(
            Context(ProcessDurabilityTestFixture.AcceptedAtUtc), fixture.Plan, fixture.Start);
        Assert.Null(acquired);
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(initialized.Snapshot);
        var result = await runtime.ActivateAsync(Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Plan, before.Checkpoint.ContinuationIdentity, fixture.Activation);
        Assert.Equal(expected, result.Disposition);
        Assert.NotNull(acquired);
        Assert.Equal(before.Checkpoint.ContinuationIdentity, acquired.Checkpoint.ContinuationIdentity);
        var retained = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc), before.Checkpoint.ContinuationIdentity.ProcessInstanceId));
        Assert.Equal(before.Checkpoint, retained.Checkpoint);
        Assert.Empty(retained.Checkpoint.Operations);
        Assert.Empty(retained.Checkpoint.Activations);
        Assert.Equal(0, runtime.RetainedInstanceGateCount);
    }

    static ProcessDurableRuntime AsyncRuntime(
        IProcessDurableStore store, ProcessDurabilityTestFixture fixture, IAsyncProcessReferenceHost host) =>
        new(store, host, new("worker/durable-runtime-tests", WorkerLease),
            new BindingResolver(fixture.DurableOperation.Binding));

    sealed class AsyncRecordingHost(ProcessOperationResult result, Action? afterAwait = null) : IAsyncProcessReferenceHost
    {
        internal int RelationCalls { get; private set; }

        public async ValueTask<ProcessOperationResult> EvaluateRelationAsync(
            OperationContext context, ProcessRelationEvaluation evaluation)
        {
            context.ThrowIfCancellationRequested();
            RelationCalls++;
            await Task.Yield();
            afterAwait?.Invoke();
            return result;
        }

        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(
            OperationContext context, ProcessTransitionInvocation invocation) => throw new InvalidOperationException("Unexpected Transition.");

        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(
            OperationContext context, ProcessSignalTargetResolution resolution) => throw new InvalidOperationException("Unexpected Signal.");
    }
}
