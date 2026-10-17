using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessReadPolicyTests
{
    static readonly ValueContract Text = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ExecutionProvenance Provenance = new(new("tests"), new("read-policy"), DocumentOrigin.Generated);
    static readonly ExecutionDefinitionReference Read = new(new("read"), new("1"),
        new(ExecutionDefinitionFingerprinter.Algorithm, ExecutionDefinitionFingerprinter.Canonicalization, new string('a', 64)));

    [Fact]
    public async Task Classified_failures_retry_within_budget_and_materialize_once()
    {
        var host = new Host((_, count) => ValueTask.FromResult(count < 3 ? Failure("storage.busy") : Success()));
        var result = await Execute(new(maxAttempts: 3, retryableFailureCodes: ["storage.busy"]), host);
        Assert.Equal(3, host.Calls);
        Assert.Equal(ProcessActivationDisposition.Completed, result.Decision.Disposition);
        Assert.Single(result.Evidence.CompletedOperations);
    }

    [Fact]
    public async Task Successful_observation_is_not_retried()
    {
        var host = new Host((_, _) => ValueTask.FromResult(Success()));
        await Execute(new(maxAttempts: 3, retryableFailureCodes: ["storage.busy"]), host);
        Assert.Equal(1, host.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_stops_backoff_before_another_read()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new Host((_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(Failure("storage.busy"));
        });
        await Assert.ThrowsAsync<EphemeralProcessInterruptedException>(() => Execute(
            new(maxAttempts: 3, retryDelay: TimeSpan.FromMinutes(1), retryableFailureCodes: ["storage.busy"]),
            host, OperationContext.Create().WithCancellationToken(cancellation.Token)));
        Assert.Equal(1, host.Calls);
    }

    [Fact]
    public async Task Exhaustion_retains_the_last_structured_failure()
    {
        var host = new Host((_, _) => ValueTask.FromResult(Failure("storage.busy")));
        var result = await Execute(new(maxAttempts: 2, retryableFailureCodes: ["storage.busy"]), host);
        Assert.Equal(2, host.Calls);
        Assert.Equal(ProcessActivationDisposition.Failed, result.Decision.Disposition);
        Assert.Equal("storage.busy", Assert.Single(result.Evidence.CompletedOperations).Value.Failure!.Code);
    }

    [Fact]
    public async Task Unselected_failures_do_not_retry()
    {
        var host = new Host((_, _) => ValueTask.FromResult(Failure("authorization.denied")));
        await Execute(new(maxAttempts: 3, retryableFailureCodes: ["storage.busy"]), host);
        Assert.Equal(1, host.Calls);
    }

    [Fact]
    public async Task Exceptions_do_not_retry()
    {
        var host = new Host((_, _) => throw new IOException("physical failure"));
        await Assert.ThrowsAsync<EphemeralProcessExecutionException>(() => Execute(
            new(maxAttempts: 3, retryableFailureCodes: ["storage.busy"]), host));
        Assert.Equal(1, host.Calls);
    }

    [Fact]
    public async Task Deadline_cancels_and_waits_for_host_exit()
    {
        var exited = false;
        var host = new Host(async (context, _) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken); return Success(); }
            finally { exited = true; }
        });
        await Assert.ThrowsAsync<EphemeralProcessInterruptedException>(() => Execute(new(timeout: TimeSpan.FromMilliseconds(50)), host));
        Assert.True(exited);
        Assert.Equal(1, host.Calls);
    }

    [Theory]
    [InlineData(ProcessReadProgress.ActivationLocal)]
    [InlineData(ProcessReadProgress.Durable)]
    public void Policy_round_trips_as_canonical_process_data(ProcessReadProgress progress)
    {
        var original = Plan(new(timeout: TimeSpan.FromSeconds(1), maxAttempts: 2,
            retryableFailureCodes: ["storage.busy"], progress: progress)).Document;
        Assert.True(ExecutionDefinitionJsonSerializer.TryDeserialize(
            ExecutionDefinitionJsonSerializer.Serialize(original), out var restored).IsValid);
        var recompiled = ProcessStaticCompiler.Compile(restored!,
            new(definitions: [new(Read, ProcessDefinitionLinkKind.RelationQuery, Text, Text)]));
        Assert.True(recompiled.IsSuccessful);
        var policy = Assert.Single(recompiled.Plan!.Definition.Nodes.OfType<EvaluateRelationProcessNode>()).Policy!;
        Assert.Equal(2, policy.MaxAttempts);
        Assert.Equal(progress, policy.Progress);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.Timeout);
        Assert.Equal(original.Metadata.Fingerprint, restored!.Metadata.Fingerprint);
    }

    [Fact]
    public void Synchronous_host_cannot_silently_ignore_policy()
    {
        var plan = Plan(new(timeout: TimeSpan.FromSeconds(1)));
        var state = ProcessReferenceInterpreter.Create(plan, new(new("instance"), new("attempt")),
            PortableValue.Concrete(Text, ObservationValue.FromString("key")));
        var activation = new ProcessActivation(new("activation"), ProcessActivationCause.Start, DateTimeOffset.UtcNow,
            new(new("test", "tenant"), new("correlation"),
                new(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal), Provenance));
        var host = new UnsupportedHost();
        var error = Assert.Throws<ArgumentException>(() => ProcessReferenceInterpreter.Activate(plan, state, activation, host));
        Assert.Contains("processes.read.policy.unsupported", error.Message);
        Assert.Equal(0, host.Calls);
    }

    sealed class UnsupportedHost : IProcessReferenceHost
    {
        internal int Calls;
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) { Calls++; return Success(); }
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => throw new InvalidOperationException();
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new InvalidOperationException();
    }

    [Fact]
    public void Ephemeral_executor_rejects_durable_progress_during_setup()
    {
        var plan = Plan(new(progress: ProcessReadProgress.Durable));
        Assert.False(EphemeralProcessExecutor.Validate(plan).IsValid);
        Assert.Throws<ArgumentException>(() => new EphemeralProcessExecutor(plan));
    }

    [Fact]
    public void Invalid_retry_policy_fails_during_authoring()
    {
        Assert.Throws<ArgumentException>(() => new ProcessReadPolicy(maxAttempts: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessReadPolicy(timeout: TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new ProcessReadPolicy(retryableFailureCodes: ["busy", "busy"]));
    }

    static ProcessOperationResult Failure(string code) => ProcessOperationResult.Failed(new(code, DiagnosticSeverity.Error, "Read unavailable."));
    static ProcessOperationResult Success() => ProcessOperationResult.Completed(PortableValue.Concrete(Text, ObservationValue.FromString("ok")));
    static CompiledProcessPlan Plan(ProcessReadPolicy policy)
    {
        var document = ProcessDefinitionDocuments.Create(new("read-policy"), new("1"), new(Text, Text, new("read"), [
            new EvaluateRelationProcessNode(new("read"), Read, Expr.BoundValue(ProcessBindingIds.Input),
                new(new(new("next"), new("return"))), policy),
            new ReturnProcessNode(new("return"), Expr.Const("done"))], ProcessRecoveryPolicy.ContinueAttempt), Provenance);
        var compilation = ProcessStaticCompiler.Compile(document,
            new(definitions: [new(Read, ProcessDefinitionLinkKind.RelationQuery, Text, Text)]));
        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Validation.Diagnostics));
        return compilation.Plan!;
    }

    static async Task<EphemeralProcessResult> Execute(ProcessReadPolicy policy, Host host, OperationContext? context = null)
    {
        var executor = new EphemeralProcessExecutor(Plan(policy));
        return await executor.ExecuteAsync(context ?? OperationContext.Create(), new(new("instance"), new("attempt")),
            PortableValue.Concrete(Text, ObservationValue.FromString("key")),
            new(new("test", "tenant"), new("correlation"),
                new(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal), Provenance),
            host, TimeSpan.FromSeconds(5));
    }
    sealed class Host(Func<OperationContext, int, ValueTask<ProcessOperationResult>> read) : IAsyncProcessReferenceHost
    {
        internal int Calls;
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation) => read(context, ++Calls);
        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation) => throw new InvalidOperationException();
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) => throw new InvalidOperationException();
    }
}
