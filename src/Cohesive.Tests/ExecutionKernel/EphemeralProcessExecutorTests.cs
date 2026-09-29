using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class EphemeralProcessExecutorTests
{
    static readonly ValueContract Text = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ExecutionProvenance Provenance = new(new("ephemeral-tests", "1"), new("tests/ephemeral"), DocumentOrigin.Generated);
    static readonly ExecutionDefinitionReference Transition = new(new("approve"), new("1"),
        new(ExecutionDefinitionFingerprinter.Algorithm, ExecutionDefinitionFingerprinter.Canonicalization, new string('a', 64)));

    [Fact]
    public async Task SequentialMutationExecutesOnceAndReturnsCanonicalEvidence()
    {
        var executor = new EphemeralProcessExecutor(Plan(mutation: true));
        Assert.True(executor.Realization.IsRealizable);
        Assert.Equal(ProcessExecutionLifetime.Ephemeral, executor.Realization.Inventory.Lifetime);
        Assert.DoesNotContain(executor.Realization.Inventory.Requirements, r => r.Key == ProcessInterpreterGuarantees.LifecycleControl);
        var host = new Host();
        var result = await Execute(executor, host);
        Assert.Equal(ProcessActivationDisposition.Completed, result.Decision.Disposition);
        Assert.Equal(1, host.Writes);
        Assert.Equal(Value("done"), result.Decision.State.Terminal.Detail?.Value);
        Assert.Empty(result.Decision.Emissions);
        Assert.True(ProcessExecutionTraceProjector.Project(result.Decision).IsSuccessful);
        // Reusing a prepared executor does not share results or imply durable deduplication.
        await Execute(executor, host);
        Assert.Equal(2, host.Writes);
    }

    [Fact]
    public void UnsupportedDemandsAreRejectedBeforeAnyExecution()
    {
        var waiting = Plan(cut: true);
        Assert.Equal("processes.ephemeral.constructUnsupported", Assert.Single(EphemeralProcessExecutor.Validate(waiting).Diagnostics).Code);
        Assert.Throws<ArgumentException>(() => new EphemeralProcessExecutor(waiting));
        var atomic = Plan(atomic: true);
        Assert.Equal("processes.ephemeral.atomicScopeUnsupported", Assert.Single(EphemeralProcessExecutor.Validate(atomic).Diagnostics).Code);
        Assert.Throws<ArgumentException>(() => new EphemeralProcessExecutor(atomic));
    }

    [Fact]
    public async Task CancellationBeforeInvocationDoesNotWrite()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var host = new Host();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(new(Plan(mutation: true)), host,
            OperationContext.Create(cancellationToken: cancellation.Token)));
        Assert.Equal(0, host.Writes);
    }

    [Fact]
    public async Task CancellationAfterCommitDoesNotRollbackOrRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new Host { AfterWrite = cancellation.Cancel };
        var interrupted = await Assert.ThrowsAsync<EphemeralProcessInterruptedException>(() => Execute(new(Plan(mutation: true)), host,
            OperationContext.Create(cancellationToken: cancellation.Token)));
        Assert.Equal(1, host.Writes);
        Assert.Null(interrupted.Evidence.InterruptedOperation);
        Assert.Equal(Value("approved"), Assert.Single(interrupted.Evidence.CompletedOperations).Value.Value);
    }

    [Fact]
    public async Task DeadlineCancelsHostAndWaitsForItsExit()
    {
        var host = new Host { WaitForCancellation = true };
        var interrupted = await Assert.ThrowsAsync<EphemeralProcessInterruptedException>(() => Execute(new(Plan(mutation: true)), host,
            timeout: TimeSpan.FromMilliseconds(20)));
        Assert.Equal(new ExecutionNodeId("write"), interrupted.Evidence.InterruptedOperation);
        Assert.Empty(interrupted.Evidence.CompletedOperations);
        Assert.True(host.Exited);
        Assert.Equal(0, host.Writes);
    }

    static async Task<EphemeralProcessResult> Execute(EphemeralProcessExecutor executor, Host host,
        OperationContext? context = null, TimeSpan? timeout = null) => await executor.ExecuteAsync(
            context ?? OperationContext.Create(), new(new("instance/1"), new("attempt/1")), Value("input"),
            new(new("tests", "tenant"), new("correlation/1"),
                new(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal), Provenance),
            host, timeout ?? TimeSpan.FromSeconds(5));

    static PortableValue Value(string value) => PortableValue.Concrete(Text, ObservationValue.FromString(value));

    static CompiledProcessPlan Plan(bool mutation = false, bool cut = false, bool atomic = false)
    {
        var nodes = new List<ProcessNode>();
        if (mutation)
            nodes.Add(new InvokeTransitionProcessNode(new("write"), Transition, Expr.Const("proposal/1"),
                Expr.BoundValue(ProcessBindingIds.Input), new(new(new("next"), new("return")))));
        if (cut)
            nodes.Add(new DurableCutProcessNode(new("cut"), new(new("resume"), new("return"))));
        nodes.Add(new ReturnProcessNode(new("return"), Expr.Const("done")));
        var document = ProcessDefinitionDocuments.Create(new("ephemeral-example"), new("1"),
            new(Text, Text, nodes[0].Id, [.. nodes], ProcessRecoveryPolicy.ContinueAttempt), Provenance);
        var compilation = ProcessStaticCompiler.Compile(document,
            new(definitions: [new(Transition, ProcessDefinitionLinkKind.Transition, Text, Text)]),
            new(atomic ? ProcessAtomicScopeDemand.WholeDefinition : ProcessAtomicScopeDemand.None));
        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Validation.Diagnostics));
        return compilation.Plan!;
    }

    sealed class Host : IAsyncProcessReferenceHost
    {
        public int Writes { get; private set; }
        public Action? AfterWrite { get; init; }
        public bool WaitForCancellation { get; init; }
        public bool Exited { get; private set; }
        public async ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation)
        {
            try
            {
                if (WaitForCancellation)
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                Writes++;
                AfterWrite?.Invoke();
                return ProcessOperationResult.Completed(Value("approved"));
            }
            finally { Exited = true; }
        }
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation) => throw new InvalidOperationException();
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) => throw new InvalidOperationException();
    }
}
