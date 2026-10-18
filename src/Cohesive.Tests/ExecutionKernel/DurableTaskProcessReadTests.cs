using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DurableTaskProcessReadTests
{
    static readonly DateTimeOffset Started = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
    static readonly ValueContract Text = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public async Task Restart_during_backoff_replays_prior_failure_without_resetting_attempt_budget()
    {
        var operation = Operation(new(maxAttempts: 2, retryDelay: TimeSpan.FromSeconds(5),
            retryableFailureCodes: ["busy"], progress: ProcessReadProgress.Durable));
        var history = new History(_ => Failure("busy")) { InterruptAfterEvents = 1 };
        await Assert.ThrowsAsync<WorkerInterrupted>(() => history.Run(operation));
        Assert.Equal(1, history.PhysicalReads);
        history.Restart();
        var result = await history.Run(operation);
        Assert.Equal("busy", result.Failure!.Code);
        Assert.Equal(2, history.PhysicalReads);
        Assert.Single(history.Delays);
        history.Restart();
        Assert.Equal(result, await history.Run(operation));
        Assert.Equal(2, history.PhysicalReads);
    }

    [Fact]
    public async Task Restart_reuses_completed_observation_even_if_database_changes()
    {
        var history = new History(_ => Success("original"));
        var operation = Operation(new(progress: ProcessReadProgress.Durable));
        var result = await history.Run(operation);
        history.Restart();
        history.Read = _ => Success("changed");
        Assert.Equal(result, await history.Run(operation));
        Assert.Equal(1, history.PhysicalReads);
    }

    [Fact]
    public async Task Original_deadline_survives_restart_and_caps_backoff()
    {
        var operation = Operation(new(timeout: TimeSpan.FromSeconds(3), maxAttempts: 3,
            retryDelay: TimeSpan.FromSeconds(10), retryableFailureCodes: ["busy"], progress: ProcessReadProgress.Durable));
        var history = new History(_ => Failure("busy")) { InterruptAfterEvents = 1 };
        await Assert.ThrowsAsync<WorkerInterrupted>(() => history.Run(operation));
        history.Restart();
        var result = await history.Run(operation);
        Assert.Equal(DurableTaskProcessReadExecution.DeadlineExceeded, result.Failure!.Code);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(history.Delays));
        Assert.Equal(1, history.PhysicalReads);
        Assert.Equal(Started.AddSeconds(3), Assert.Single(history.Operations).ReadDeadlineUtc);
    }

    [Fact]
    public async Task Every_retry_carries_same_absolute_deadline()
    {
        var history = new History(n => n < 3 ? Failure("busy") : Success("done"));
        var result = await history.Run(Operation(new(timeout: TimeSpan.FromSeconds(20), maxAttempts: 3,
            retryDelay: TimeSpan.FromSeconds(2), retryableFailureCodes: ["busy"], progress: ProcessReadProgress.Durable)));
        Assert.True(result.IsSuccessful);
        Assert.Equal(3, history.Operations.Count);
        Assert.All(history.Operations, op => Assert.Equal(Started.AddSeconds(20), op.ReadDeadlineUtc));
    }

    [Fact]
    public async Task Unclassified_failure_and_exception_do_not_retry()
    {
        var operation = Operation(new(maxAttempts: 3, retryableFailureCodes: ["busy"], progress: ProcessReadProgress.Durable));
        var history = new History(_ => Failure("authorization.denied"));
        Assert.Equal("authorization.denied", (await history.Run(operation)).Failure!.Code);
        Assert.Equal(1, history.PhysicalReads);
        history = new History(_ => throw new IOException("unclassified"));
        await Assert.ThrowsAsync<IOException>(() => history.Run(operation));
        Assert.Equal(1, history.PhysicalReads);
    }

    [Fact]
    public async Task Termination_during_attempt_prevents_later_retry_dispatch()
    {
        var pending = new TaskCompletionSource<ProcessOperationResult>();
        var reads = 0;
        var operation = Operation(new(maxAttempts: 3, retryableFailureCodes: ["busy"], progress: ProcessReadProgress.Durable));
        await Assert.ThrowsAsync<WorkerInterrupted>(() => DurableTaskProcessReadExecution.ExecuteAsync(operation,
            _ => { reads++; return pending.Task; },
            (_, _) => throw new InvalidOperationException("No backoff after termination."), () => Started,
            _ => throw new WorkerInterrupted()));
        pending.SetResult(Failure("busy"));
        await Task.Yield();
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Termination_during_backoff_cancels_timer_without_dispatching_retry()
    {
        var pending = new TaskCompletionSource();
        var reads = 0;
        CancellationToken timerToken = default;
        var operation = Operation(new(maxAttempts: 3, retryDelay: TimeSpan.FromSeconds(1),
            retryableFailureCodes: ["busy"], progress: ProcessReadProgress.Durable));
        await Assert.ThrowsAsync<WorkerInterrupted>(() => DurableTaskProcessReadExecution.ExecuteAsync(operation,
            _ => { reads++; return Task.FromResult(Failure("busy")); },
            (_, token) => { timerToken = token; return pending.Task; }, () => Started,
            work => work.IsCompleted ? Task.CompletedTask : throw new WorkerInterrupted()));
        Assert.True(timerToken.IsCancellationRequested);
        pending.SetResult();
        await Task.Yield();
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Activity_serialization_preserves_original_deadline_and_durable_policy()
    {
        var original = Operation(new(timeout: TimeSpan.FromSeconds(2), progress: ProcessReadProgress.Durable));
        var operation = new DurableTaskProcessHostOperation(original.Kind, relationQuery: original.RelationQuery,
            readDeadlineUtc: Started.AddSeconds(2));
        var converter = DurableTaskProcessDataConverter.Create();
        var restored = Assert.IsType<DurableTaskProcessHostOperation>(
            converter.Deserialize(converter.Serialize(operation), typeof(DurableTaskProcessHostOperation)));
        Assert.Equal(operation.ReadDeadlineUtc, restored.ReadDeadlineUtc);
        Assert.Equal(ProcessReadProgress.Durable, restored.RelationQuery!.Policy!.Progress);
        Assert.Equal(operation.RelationQuery!.Definition, restored.RelationQuery.Definition);
    }

    [Fact]
    public async Task Expired_queued_attempt_does_no_database_io()
    {
        var original = Operation(new(timeout: TimeSpan.FromSeconds(2), progress: ProcessReadProgress.Durable));
        var operation = new DurableTaskProcessHostOperation(original.Kind, relationQuery: original.RelationQuery,
            readDeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(-1));
        var host = new Host((_, _) => throw new InvalidOperationException("Must not read."));
        var result = await DurableTaskProcessReadExecution.EvaluateAttemptAsync(OperationContext.Create(), operation, host);
        Assert.Equal(DurableTaskProcessReadExecution.DeadlineExceeded, result.Failure!.Code);
    }

    [Fact]
    public async Task Deadline_cancels_and_waits_for_running_read_to_quiesce()
    {
        var original = Operation(new(timeout: TimeSpan.FromSeconds(1), progress: ProcessReadProgress.Durable));
        var operation = new DurableTaskProcessHostOperation(original.Kind, relationQuery: original.RelationQuery,
            readDeadlineUtc: DateTimeOffset.UtcNow.AddMilliseconds(100));
        var exited = false;
        var host = new Host(async (context, _) =>
        {
            try { await Task.Delay(Timeout.Infinite, context.CancellationToken); return Success("never"); }
            finally { exited = true; }
        });
        var result = await DurableTaskProcessReadExecution.EvaluateAttemptAsync(OperationContext.Create(), operation, host);
        Assert.True(exited);
        Assert.Equal(DurableTaskProcessReadExecution.DeadlineExceeded, result.Failure!.Code);
    }

    [Fact]
    public async Task Worker_shutdown_is_not_converted_into_deadline_failure()
    {
        using var shutdown = new CancellationTokenSource();
        var original = Operation(new(timeout: TimeSpan.FromSeconds(10), progress: ProcessReadProgress.Durable));
        var operation = new DurableTaskProcessHostOperation(original.Kind, relationQuery: original.RelationQuery,
            readDeadlineUtc: DateTimeOffset.UtcNow.AddSeconds(10));
        var host = new Host((context, _) => { shutdown.Cancel(); context.ThrowIfCancellationRequested(); return ValueTask.FromResult(Success("never")); });
        await Assert.ThrowsAsync<OperationCanceledException>(() => DurableTaskProcessReadExecution.EvaluateAttemptAsync(
            OperationContext.Create().WithCancellationToken(shutdown.Token), operation, host).AsTask());
    }

    static DurableTaskProcessHostOperation Operation(ProcessReadPolicy policy) => new(
        DurableTaskProcessHostOperationKind.RelationQuery, relationQuery: new(
            new(new("inventory/read"), new("1"), new(ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization, new string('a', 64))),
            PortableValue.Concrete(Text, ObservationValue.FromString("sku")),
            new(new("instance"), new("attempt")), new("activation"), new("token"), new("read"), 1, Started,
            new(new("test", "tenant"), new("correlation"),
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
                new(new("test"), new("read"), DocumentOrigin.Generated)), Policy: policy));
    static ProcessOperationResult Failure(string code) => ProcessOperationResult.Failed(new(code, DiagnosticSeverity.Error, "Unavailable."));
    static ProcessOperationResult Success(string value) => ProcessOperationResult.Completed(PortableValue.Concrete(Text, ObservationValue.FromString(value)));

    sealed class WorkerInterrupted : Exception;

    // Models persisted activity/timer history. A fresh interpreter replays it from the original logical clock.
    sealed class History(Func<int, ProcessOperationResult> read)
    {
        readonly List<(DurableTaskProcessHostOperation? Operation, TimeSpan? Delay, ProcessOperationResult? Result, DateTimeOffset At)> events = [];
        int cursor;
        DateTimeOffset now = Started;
        internal Func<int, ProcessOperationResult> Read = read;
        internal int PhysicalReads;
        internal int InterruptAfterEvents = int.MaxValue;
        internal List<TimeSpan> Delays = [];
        internal List<DurableTaskProcessHostOperation> Operations = [];
        internal void Restart() { cursor = 0; now = Started; InterruptAfterEvents = int.MaxValue; }
        internal Task<ProcessOperationResult> Run(DurableTaskProcessHostOperation op) =>
            DurableTaskProcessReadExecution.ExecuteAsync(op, Execute, Timer, () => now);
        Task<ProcessOperationResult> Execute(DurableTaskProcessHostOperation op)
        {
            if (cursor == InterruptAfterEvents) throw new WorkerInterrupted();
            if (cursor < events.Count)
            {
                var e = events[cursor++]; Assert.Equal(e.Operation, op); now = e.At;
                return Task.FromResult(e.Result!);
            }
            var result = Read(++PhysicalReads); Operations.Add(op);
            events.Add((op, null, result, now)); cursor++;
            return Task.FromResult(result);
        }
        Task Timer(TimeSpan delay, CancellationToken token)
        {
            if (cursor == InterruptAfterEvents) throw new WorkerInterrupted();
            if (cursor < events.Count)
            {
                var e = events[cursor++]; Assert.Equal(e.Delay, delay); now = e.At;
                return Task.CompletedTask;
            }
            now += delay; Delays.Add(delay); events.Add((null, delay, null, now)); cursor++;
            return Task.CompletedTask;
        }
    }
    sealed class Host(Func<OperationContext, ProcessRelationEvaluation, ValueTask<ProcessOperationResult>> read) : IAsyncProcessReferenceHost
    {
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation) => read(context, evaluation);
        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation) => throw new InvalidOperationException();
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) => throw new InvalidOperationException();
    }
}
