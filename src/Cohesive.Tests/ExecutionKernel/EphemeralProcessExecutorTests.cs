using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Authoring;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReceiptBindingIsDistinctFromDomainOutcomeAndMissingReceiptRetainsCommitEvidence(bool suppliesReceipt)
    {
        var receipt = Value("receipt/immutable-commit");
        var output = new ProcessOutputBinding(new("commit-receipt"), Text);
        var document = ProcessDefinitionDocuments.Create(new("receipt-example"), new("1"),
            new(Text, Text, new("write"), [
                new InvokeTransitionProcessNode(new("write"), Transition, Expr.Const("proposal/1"),
                    Expr.BoundValue(ProcessBindingIds.Input), new(new(new("next"), new("return")), new(new("domain-outcome"), Text)), output),
                new ReturnProcessNode(new("return"), Expr.BoundValue(output.Binding))
            ], ProcessRecoveryPolicy.ContinueAttempt), Provenance);
        var authored = ProcessAuthoring.Create<string, string>(
            new(new("receipt-example"), new("1"), new("write"), ProcessRecoveryPolicy.ContinueAttempt, Provenance), builder =>
            {
                var domain = builder.Output<string>(new("domain-outcome"));
                var committed = builder.Output<string>(output.Binding);
                builder.InvokeTransitionWithReceipt(new("write"), Transition, builder.Constant("proposal/1"),
                    builder.Input.Value, builder.Continuation(builder.Edge(new("next"), new("return")), domain), committed);
                builder.Return(new("return"), committed.Value);
            });
        Assert.True(authored.IsValid, string.Join("; ", authored.Validation.Diagnostics));
        Assert.Equal(document.Metadata.Fingerprint, authored.Document.Metadata.Fingerprint);
        Assert.NotEqual(new ProcessDefinitionLink(Transition, ProcessDefinitionLinkKind.Transition, Text, Text),
            new ProcessDefinitionLink(Transition, ProcessDefinitionLinkKind.Transition, Text, Text, receiptContract: Text));
        Assert.True(ProcessDefinitionDocuments.TryDeserialize(ExecutionDefinitionJsonSerializer.Serialize(document),
            out var restored, out _).IsValid);
        Assert.Equal(document.Metadata.Fingerprint, restored!.Metadata.Fingerprint);
        var unattested = ProcessStaticCompiler.Compile(document,
            new(definitions: [new(Transition, ProcessDefinitionLinkKind.Transition, Text, Text)]));
        Assert.False(unattested.IsSuccessful);
        Assert.Contains(unattested.Validation.Diagnostics, d => d.Code == ProcessDefinitionDiagnosticCodes.OutputContractMismatch);
        var compilation = ProcessStaticCompiler.Compile(document,
            new(definitions: [new(Transition, ProcessDefinitionLinkKind.Transition, Text, Text, receiptContract: Text)]));
        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Validation.Diagnostics));
        var host = new Host { Receipt = suppliesReceipt ? receipt : null };
        var result = await Execute(new(compilation.Plan!), host);
        Assert.Equal(1, host.Writes);
        Assert.Equal(Value("approved"), Assert.Single(result.Evidence.CompletedOperations).Value.Value);
        if (suppliesReceipt)
        {
            Assert.Equal(ProcessActivationDisposition.Completed, result.Decision.Disposition);
            Assert.Equal(receipt, result.Decision.State.Terminal.Detail?.Value);
            Assert.True(ProcessContinuationValidator.Validate(compilation.Plan!, result.Decision.State).IsValid);
            Assert.True(ProcessExecutionTraceProjector.Project(result.Decision).IsSuccessful);
        }
        else
        {
            Assert.Equal(ProcessActivationDisposition.Failed, result.Decision.Disposition);
            Assert.Contains(result.Decision.Diagnostics, d => d.Code == ProcessExecutionDiagnosticCodes.ResultContractViolated);
        }
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
        var clock = new DeadlineClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host { WaitForCancellation = true, OnEntered = () => entered.SetResult() };
        var execution = Execute(new(Plan(mutation: true)), host,
            OperationContext.Create(timeProvider: clock), TimeSpan.FromMilliseconds(20));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(execution.IsCompleted);
        Assert.Equal(TimeSpan.FromMilliseconds(20), clock.DueTime);
        // Expire the registered deadline after the operation is in flight, independent of runner speed.
        clock.Expire();
        var interrupted = await Assert.ThrowsAsync<EphemeralProcessInterruptedException>(() => execution);
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

    sealed class DeadlineClock : TimeProvider
    {
        DeadlineTimer? timer;
        internal TimeSpan DueTime { get; private set; }
        internal void Expire() => timer!.Expire();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Null(timer);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            DueTime = dueTime;
            return timer = new DeadlineTimer(() => callback(state));
        }

        sealed class DeadlineTimer(Action callback) : ITimer
        {
            Action? pending = callback;
            internal void Expire() => Interlocked.Exchange(ref pending, null)?.Invoke();
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref pending, null);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    sealed class Host : IAsyncProcessReferenceHost
    {
        public int Writes { get; private set; }
        public Action? AfterWrite { get; init; }
        public PortableValue? Receipt { get; init; }
        public bool WaitForCancellation { get; init; }
        public Action? OnEntered { get; init; }
        public bool Exited { get; private set; }
        public async ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation)
        {
            try
            {
                OnEntered?.Invoke();
                if (WaitForCancellation)
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                Writes++;
                AfterWrite?.Invoke();
                var result = ProcessOperationResult.Completed(Value("approved"));
                return Receipt is null ? result : result.WithReceiptReference(Receipt);
            }
            finally { Exited = true; }
        }
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation) => throw new InvalidOperationException();
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) => throw new InvalidOperationException();
    }
}
