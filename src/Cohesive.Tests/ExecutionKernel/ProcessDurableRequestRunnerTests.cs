using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed partial class ProcessDurableRuntimeTests
{
    [Fact]
    public async Task RequestRunnerResumesAdmittedReplyWithoutRepeatingDispatch()
    {
        var fixture = ProcessDurabilityTestFixture.Create(definitionId: "process/request-runner", semanticVariant: "runner");
        var store = new InMemoryProcessDurableStore();
        var host = new RecordingHost(fixture.OperationResult);
        var adapter = new RunnerAdapter(fixture.Request.Contract);
        var runtime = new ProcessDurableRuntime(store, host, new("runner", WorkerLease),
            bindingResolver: new BindingResolver(fixture.DurableOperation.Binding), operationAdapterResolver: new SingleAdapterResolver(adapter));
        var context = Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1));
        var bounded = await runtime.RunRequestsAsync(context, fixture.Plan, fixture.Start, maximumCycles: 1);
        Assert.Equal(ProcessDurableRuntimeDisposition.Unsupported, bounded.Disposition);
        Assert.Equal(1, adapter.Calls);
        var completed = await runtime.RunRequestsAsync(context, fixture.Plan, fixture.Start);
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, completed.Snapshot!.Checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(1, adapter.Calls);
        Assert.All(completed.Snapshot.Checkpoint.Inbox, reply => Assert.NotNull(reply.Receipt));
        var replay = await runtime.RunRequestsAsync(context, fixture.Plan, fixture.Start);
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, replay.Snapshot!.Checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(1, host.RelationCalls);
    }

    sealed class RunnerAdapter(RequestContractReference contract) : IDurableOperationAdapter
    {
        internal int Calls;
        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication, DurableOperationReconciliationCapability.Supported, [contract]);
        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(OperationContext context, DurableOperationInvocation invocation)
        {
            Calls++;
            return ValueTask.FromResult<DurableOperationAttemptObservation>(new DurableOperationOutcomeObservation(
                new RequestResultOutcome(new("result"), ProcessDurabilityTestFixture.StringValue("accepted"))));
        }
        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(OperationContext context, DurableOperationReconciliationRequest request) =>
            throw new InvalidOperationException("Confirmed success must not reconcile.");
    }
}
