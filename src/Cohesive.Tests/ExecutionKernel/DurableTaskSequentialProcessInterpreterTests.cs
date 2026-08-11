using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Processes.Runtime;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DurableTaskSequentialProcessInterpreterTests
{
    static readonly DateTimeOffset StartedAtUtc = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract InstantContract = new(new ScalarTypeRef(ScalarTypeKind.Instant));
    static readonly ValueContract StringCollectionContract = new(
        new ScalarTypeRef(ScalarTypeKind.String),
        cardinality: FieldCardinality.Many);
    static readonly ProcessChildOutcomeMapping ChildOutcomeMapping = new(
        new("completed"),
        new("failed"),
        new("cancelled"),
        new("terminated"));

    [Fact]
    public async Task SequentialHostOperations_AreDifferentiallyConformantAndReplayStable()
    {
        var transition = DefinitionReference("transition/orders/approve", '1');
        var relation = DefinitionReference("relation/orders/summary", '2');
        var plan = Compile(
            Definition(
                "transition",
                [
                    new InvokeTransitionProcessNode(
                        new("transition"),
                        transition,
                        Expr.Const("order/42"),
                        Expr.BoundValue(ProcessBindingIds.Input),
                        new(Edge("edge/transition-relation", "relation"))),
                    new EvaluateRelationProcessNode(
                        new("relation"),
                        relation,
                        Expr.BoundValue(ProcessBindingIds.Input),
                        new(Edge("edge/relation-return", "return"))),
                    new ReturnProcessNode(new("return"), Expr.Const("completed"))
                ]),
            definitions:
            [
                new(transition, ProcessDefinitionLinkKind.Transition, StringContract, StringContract),
                new(relation, ProcessDefinitionLinkKind.RelationQuery, StringContract, StringContract)
            ]);
        var start = Start(plan, "command");
        List<DurableTaskProcessHostOperation> scheduled = [];

        var actual = await Run(plan, start, operation =>
        {
            scheduled.Add(operation);
            return Task.FromResult(operation.Kind switch
            {
                DurableTaskProcessHostOperationKind.Transition =>
                    ProcessOperationResult.Completed(operation.Transition!.Input),
                DurableTaskProcessHostOperationKind.RelationQuery =>
                    ProcessOperationResult.Completed(operation.RelationQuery!.Input),
                _ => throw new ArgumentOutOfRangeException()
            });
        });

        var initial = ProcessReferenceInterpreter.Create(plan, start.Receipt);
        var expected = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation(initial, ProcessActivationCause.Start, start),
            new EchoHost());

        Assert.Equal(ProcessActivationDisposition.Completed, actual.Disposition);
        Assert.Equal(
            [DurableTaskProcessHostOperationKind.Transition, DurableTaskProcessHostOperationKind.RelationQuery],
            scheduled.Select(static operation => operation.Kind));
        Assert.Equal(Serialize(expected.State), Serialize(actual.State));
        Assert.Equal(Serialize(expected.Evidence), Serialize(Assert.Single(actual.Evidence)));
        var expectedTrace = ProcessExecutionTraceProjector.Project(expected);
        Assert.True(expectedTrace.IsSuccessful);
        Assert.Equal(Serialize(expectedTrace.Trace), Serialize(Assert.Single(actual.Traces)));
        var converter = DurableTaskProcessDataConverter.Create();
        foreach (var operation in scheduled)
        {
            var restored = Assert.IsType<DurableTaskProcessHostOperation>(
                converter.Deserialize(converter.Serialize(operation), typeof(DurableTaskProcessHostOperation)));
            Assert.Equal(Serialize(operation), Serialize(restored));
        }

        List<DurableTaskProcessHostOperation> replayed = [];
        var replay = await Run(plan, start, operation =>
        {
            replayed.Add(operation);
            return Task.FromResult(operation.Kind == DurableTaskProcessHostOperationKind.Transition
                ? ProcessOperationResult.Completed(operation.Transition!.Input)
                : ProcessOperationResult.Completed(operation.RelationQuery!.Input));
        });
        Assert.Equal(scheduled.Select(Serialize), replayed.Select(Serialize));
        Assert.Equal(Serialize(actual), Serialize(replay));
        Assert.Equal(
            Serialize(DurableTaskProcessStatus.Project(actual)),
            Serialize(DurableTaskProcessStatus.Project(replay)));
    }

    [Fact]
    public async Task CustomStatusProjection_RetainsCanonicalRuntimeEvidenceWithoutProcessValues()
    {
        const string PrivateInput = "private-input-ari-314";
        const string PrivateOutput = "private-output-ari-314";
        var dueAtUtc = StartedAtUtc.AddMinutes(5);
        var plan = Compile(
            Definition(
                "timer",
                [
                    new TimerProcessNode(
                        new("timer"),
                        Expr.Const(ObservationValue.FromDateTimeOffset(dueAtUtc)),
                        Edge("edge/timer-return", "return")),
                    new ReturnProcessNode(new("return"), Expr.Const(PrivateOutput))
                ]),
            definitionId: "process/durable-task-safe-custom-status");
        var start = Start(plan, PrivateInput, "instance/safe-custom-status");
        var now = StartedAtUtc;
        var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingResult = new TaskCompletionSource<DurableTaskSequentialProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            (delay, cancellationToken) => timer.Task,
            () => now,
            result =>
            {
                if (result.State.Waits.Any(static wait => wait.Active))
                {
                    waitingResult.TrySetResult(result);
                }
            });

        var waiting = await waitingResult.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var status = DurableTaskProcessStatus.Project(waiting);
        var wait = Assert.Single(status.Runtime.Waits);
        var token = Assert.Single(status.Runtime.Tokens);
        Assert.Equal(plan.DefinitionReference, status.Definition);
        Assert.Equal(start.Receipt.Request.InitialContinuation.ProcessInstanceId, status.ProcessInstanceId);
        Assert.Equal(waiting.Control.Revision, status.ControlRevision);
        Assert.Equal(ProcessControlMode.Running, status.ControlMode);
        Assert.Equal(token.TokenId, wait.TokenId);
        Assert.Equal(new ExecutionNodeId("timer"), wait.Node);
        Assert.Equal(dueAtUtc, wait.DeadlineUtc);
        Assert.Equal(waiting.State.CompletedActivationCount, status.Runtime.Progress?.Completed);

        var converter = DurableTaskProcessDataConverter.Create();
        var waitingJson = converter.Serialize(status)!;
        Assert.Contains(PrivateInput, Serialize(waiting));
        Assert.DoesNotContain(PrivateInput, waitingJson, StringComparison.Ordinal);
        Assert.DoesNotContain(NormalizedExecutionTrace.CurrentSchemaVersion.Value, waitingJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Receipts", waitingJson, StringComparison.Ordinal);
        Assert.DoesNotContain("BufferedInputs", waitingJson, StringComparison.Ordinal);
        var restored = Assert.IsType<ExecutionStatus>(
            converter.Deserialize(waitingJson, typeof(ExecutionStatus)));
        Assert.Equal(Serialize(status), Serialize(restored));

        now = dueAtUtc;
        timer.SetResult();
        var completed = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        var completedStatus = DurableTaskProcessStatus.Project(completed);
        var completedJson = converter.Serialize(completedStatus)!;
        Assert.Contains(PrivateOutput, Serialize(completed));
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, completedStatus.TerminalOutcome.Kind);
        Assert.Equal(ExecutionStatusDisclosure.Redacted, completedStatus.TerminalOutcome.Detail?.Disclosure);
        Assert.DoesNotContain(PrivateInput, completedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateOutput, completedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(NormalizedExecutionTrace.CurrentSchemaVersion.Value, completedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomStatusProjection_RedactsAuthoredFailureDetail()
    {
        const string PrivateFailure = "private-authored-failure-ari-314";
        var plan = Compile(
            Definition(
                "fail",
                [new FailProcessNode(new("fail"), Expr.Const(PrivateFailure))]),
            definitionId: "process/durable-task-safe-failure-status");

        var result = await Run(plan, Start(plan, "private-failure-input-ari-314"), UnexpectedOperation);
        var status = DurableTaskProcessStatus.Project(result);
        var serialized = DurableTaskProcessDataConverter.Create().Serialize(status)!;

        Assert.Equal(ProcessActivationDisposition.Failed, result.Disposition);
        Assert.Equal(ExecutionTerminalOutcomeKind.Failed, status.TerminalOutcome.Kind);
        Assert.Equal(ExecutionHealthStatus.Unhealthy, status.Runtime.Health);
        Assert.Equal(ExecutionStatusDisclosure.Redacted, status.TerminalOutcome.Detail?.Disclosure);
        Assert.Contains(PrivateFailure, Serialize(result));
        Assert.DoesNotContain(PrivateFailure, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-failure-input-ari-314", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DurableCut_UsesTwoCanonicalActivationsWithoutChangingContinuationIdentity()
    {
        var plan = Compile(Definition(
            "cut",
            [
                new DurableCutProcessNode(new("cut"), Edge("edge/cut-return", "return")),
                new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))
            ]));
        var start = Start(plan, "input");
        var cuts = 0;

        var actual = await DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            (delay, cancellationToken) =>
            {
                cuts++;
                return Task.CompletedTask;
            },
            () => StartedAtUtc.AddMinutes(1));

        var initial = ProcessReferenceInterpreter.Create(plan, start.Receipt);
        var first = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation(initial, ProcessActivationCause.Start, start),
            RejectingHost.Instance);
        var second = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            Activation(first.State, ProcessActivationCause.Continue, start, StartedAtUtc.AddMinutes(1)),
            RejectingHost.Instance);

        Assert.Equal(1, cuts);
        Assert.Equal(2, actual.State.CompletedActivationCount);
        Assert.Equal(start.Receipt.Request.InitialContinuation, actual.State.Continuation);
        Assert.Equal(Serialize(second.State), Serialize(actual.State));
        Assert.Equal(
            new[] { Serialize(first.Evidence), Serialize(second.Evidence) },
            actual.Evidence.Select(Serialize));
    }

    [Fact]
    public async Task Timer_UsesThePersistedCanonicalDeadlineAndResumesWhenTheDurableTimerFires()
    {
        var dueAtUtc = StartedAtUtc.AddMinutes(5);
        var plan = CompileTimerPlan(dueAtUtc, "process/durable-task-timer");
        var start = Start(plan, "timer", "instance/timer");
        var now = StartedAtUtc;
        var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduled = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<DurableTaskSequentialProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            (delay, cancellationToken) =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                scheduled.SetResult(delay);
                return timer.Task;
            },
            () => now,
            result => observed.TrySetResult(result));

        Assert.Equal(TimeSpan.FromMinutes(5), await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(execution.IsCompleted);
        var wait = Assert.Single((await observed.Task.WaitAsync(TimeSpan.FromSeconds(5))).State.Waits);
        Assert.Equal(ProcessWaitKind.Timer, wait.Kind);
        Assert.Equal(dueAtUtc, Assert.Single(wait.Timers).DueAtUtc);

        now = dueAtUtc;
        timer.SetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        var initial = ProcessReferenceInterpreter.Create(plan, start.Receipt);
        var registered = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation(initial, ProcessActivationCause.Start, start),
            RejectingHost.Instance);
        var expected = ProcessReferenceInterpreter.Activate(
            plan,
            registered.State,
            Activation(registered.State, ProcessActivationCause.Timer, start, dueAtUtc),
            RejectingHost.Instance);
        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(Serialize(expected.State), Serialize(result.State));
        Assert.Equal(
            [Serialize(registered.Evidence), Serialize(expected.Evidence)],
            result.Evidence.Select(Serialize));
    }

    [Fact]
    public async Task Timer_EarlyPhysicalWakeRemainsQuiescentAndReschedulesTheSameCanonicalWait()
    {
        var dueAtUtc = StartedAtUtc.AddMinutes(5);
        var plan = CompileTimerPlan(dueAtUtc, "process/durable-task-timer-early");
        var now = StartedAtUtc;
        ConcurrentQueue<ScheduledTimer> scheduled = [];

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            Start(plan, "timer", "instance/timer-early"),
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            (delay, cancellationToken) =>
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                scheduled.Enqueue(new(delay, cancellationToken, completion));
                return completion.Task;
            },
            () => now);

        await WaitUntilAsync(() => scheduled.Count == 1);
        Assert.True(scheduled.TryDequeue(out var early));
        Assert.Equal(TimeSpan.FromMinutes(5), early.Delay);
        early.Completion.SetResult();

        await WaitUntilAsync(() => scheduled.Count == 1);
        Assert.True(scheduled.TryDequeue(out var due));
        Assert.Equal(TimeSpan.FromMinutes(5), due.Delay);
        Assert.False(due.CancellationToken.IsCancellationRequested);
        Assert.False(execution.IsCompleted);

        now = dueAtUtc;
        due.Completion.SetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(3, result.State.CompletedActivationCount);
        Assert.Equal(
            [
                ProcessActivationCause.Start,
                ProcessActivationCause.Timer,
                ProcessActivationCause.Timer
            ],
            result.Evidence.Select(static evidence => evidence.Cause));
        Assert.Single(
            result.Evidence.SelectMany(static evidence => evidence.Trace),
            static trace => trace.Kind == ProcessTraceEventKind.WaitResolved);
    }

    [Fact]
    public async Task LifecycleControl_PauseInspectReplayFenceAndContinueAreCanonicalAtTheDurableCut()
    {
        var dueAtUtc = StartedAtUtc.AddMinutes(5);
        var plan = CompileTimerPlan(dueAtUtc, "process/durable-task-control-pause");
        var start = Start(plan, "control", "instance/control-pause");
        var now = StartedAtUtc;
        var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timerScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controls = Channel.CreateUnbounded<ProcessControlCommand>();
        ConcurrentQueue<DurableTaskSequentialProcessResult> observations = [];

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            (delay, cancellationToken) =>
            {
                timerScheduled.TrySetResult();
                return timer.Task;
            },
            () => now,
            observations.Enqueue,
            waitForControl: () => controls.Reader.ReadAsync().AsTask());

        await timerScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await WaitForObservationAsync(
            observations,
            static result => result.Control.Mode == ProcessControlMode.Running
                && result.Control.CurrentAttempt.Phase == ProcessControlExecutionPhase.AtSafePoint);

        now = now.AddSeconds(1);
        var pause = Pause(start, running.Control, "control/pause", now);
        await controls.Writer.WriteAsync(pause);
        var paused = await WaitForObservationAsync(
            observations,
            result => result.LatestControlDecision?.Receipt?.Command.Context.CommandId
                    == pause.Context.CommandId
                && result.Control.Mode == ProcessControlMode.Paused);
        var pausedStatus = DurableTaskProcessStatus.Project(paused);
        Assert.Equal(ProcessControlMode.Paused, pausedStatus.ControlMode);
        Assert.Single(pausedStatus.Runtime.Waits);

        now = now.AddSeconds(1);
        await controls.Writer.WriteAsync(pause);
        var replayed = await WaitForObservationAsync(
            observations,
            result => result.LatestControlDecision?.Disposition == ProcessControlDecisionDisposition.Replayed
                && result.LatestControlDecision.Receipt?.Command.Context.CommandId == pause.Context.CommandId);
        Assert.Equal(Serialize(paused.Control), Serialize(replayed.Control));

        now = now.AddSeconds(1);
        var staleContinue = Continue(
            start,
            paused.Control,
            "control/continue-stale",
            now,
            new(
                new(paused.Control.ProcessInstanceId, paused.Control.CurrentAttempt.AttemptId),
                running.Control.Revision));
        await controls.Writer.WriteAsync(staleContinue);
        var fenced = await WaitForObservationAsync(
            observations,
            result => result.LatestControlDecision?.Disposition
                == ProcessControlDecisionDisposition.StaleRevision);
        Assert.Equal(ProcessControlDiagnosticCodes.StaleRevision, Assert.Single(
            fenced.LatestControlDecision!.Diagnostics).Code);
        Assert.Equal(ProcessControlMode.Paused, fenced.Control.Mode);

        now = now.AddSeconds(1);
        var unauthorized = new ContinueProcessCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            new(
                new("control/continue-unauthorized"),
                new("idempotency/control/continue-unauthorized"),
                paused.Control.ProcessInstanceId,
                new(
                    "test-runner",
                    new("authority/other", "tenant/cohesive"),
                    "authorization/other"),
                now,
                Provenance()),
            Expectation(paused.Control));
        await controls.Writer.WriteAsync(unauthorized);
        var unauthorizedResult = await WaitForObservationAsync(
            observations,
            result => result.LatestControlDecision?.Disposition
                == ProcessControlDecisionDisposition.Unauthorized);
        Assert.Equal(ProcessControlDiagnosticCodes.AuthorityMismatch, Assert.Single(
            unauthorizedResult.LatestControlDecision!.Diagnostics).Code);
        Assert.Equal(Serialize(paused.Control), Serialize(unauthorizedResult.Control));

        now = now.AddSeconds(1);
        var inspect = Inspect(start, paused.Control, "control/inspect", now);
        await controls.Writer.WriteAsync(inspect);
        var inspected = await WaitForObservationAsync(
            observations,
            result => result.LatestControlDecision?.Disposition
                == ProcessControlDecisionDisposition.Inspected);
        Assert.Equal(Serialize(paused.Control), Serialize(inspected.Control));

        timer.SetResult();
        await Task.Delay(25);
        Assert.False(execution.IsCompleted);

        now = dueAtUtc;
        var @continue = Continue(start, paused.Control, "control/continue", now);
        await controls.Writer.WriteAsync(@continue);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(ProcessControlMode.Running, result.Control.Mode);
        Assert.Equal(start.Receipt.Request.InitialContinuation, result.State.Continuation);
    }

    [Fact]
    public async Task LifecycleControl_PauseDuringHostWorkDefersAndStopsAtTheExactSafePoint()
    {
        var transition = DefinitionReference("transition/control/active", '8');
        var plan = Compile(
            Definition(
                "transition",
                [
                    new InvokeTransitionProcessNode(
                        new("transition"),
                        transition,
                        Expr.Const("entity/1"),
                        Expr.BoundValue(ProcessBindingIds.Input),
                        new(Edge("edge/transition-cut", "cut"))),
                    new DurableCutProcessNode(new("cut"), Edge("edge/cut-return", "return")),
                    new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))
                ]),
            definitions:
            [new(transition, ProcessDefinitionLinkKind.Transition, StringContract, StringContract)],
            definitionId: "process/durable-task-control-active-pause");
        var start = Start(plan, "active", "instance/control-active-pause");
        var now = StartedAtUtc;
        var activity = new TaskCompletionSource<ProcessOperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var controls = Channel.CreateUnbounded<ProcessControlCommand>();
        ConcurrentQueue<DurableTaskSequentialProcessResult> observations = [];

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            _ => activity.Task,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            (delay, cancellationToken) => Task.CompletedTask,
            () => now,
            observations.Enqueue,
            waitForControl: () => controls.Reader.ReadAsync().AsTask());

        var active = await WaitForObservationAsync(
            observations,
            static result => result.Control.CurrentAttempt.Phase == ProcessControlExecutionPhase.InActivation);
        var activeStatus = DurableTaskProcessStatus.Project(active);
        var activeActivation = Assert.IsType<ExecutionActivationStatus>(activeStatus.ActiveActivation);
        Assert.Equal(active.Control.CurrentAttempt.AttemptId, activeActivation.AttemptId);
        now = now.AddSeconds(1);
        var pause = Pause(start, active.Control, "control/pause-active", now);
        await controls.Writer.WriteAsync(pause);
        var deferred = await WaitForObservationAsync(
            observations,
            result => result.LatestControlDecision?.Receipt?.Command.Context.CommandId
                    == pause.Context.CommandId
                && result.LatestControlDecision.Disposition
                    == ProcessControlDecisionDisposition.DeferredToSafePoint);
        Assert.Equal(ProcessControlMode.PauseRequested, deferred.Control.Mode);
        Assert.False(execution.IsCompleted);

        now = now.AddSeconds(1);
        activity.SetResult(ProcessOperationResult.Completed(StringValue("active")));
        var paused = await WaitForObservationAsync(
            observations,
            static result => result.Control.Mode == ProcessControlMode.Paused
                && result.Control.CurrentAttempt.Phase == ProcessControlExecutionPhase.AtSafePoint);
        Assert.Equal(ProcessActivationDisposition.DurableCut, paused.Disposition);
        Assert.False(execution.IsCompleted);

        now = now.AddSeconds(1);
        await controls.Writer.WriteAsync(Continue(start, paused.Control, "control/continue-active", now));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(ProcessControlMode.Running, result.Control.Mode);
    }

    [Fact]
    public async Task LifecycleControl_RestartAttemptReplacesCanonicalLineageAndAbandonsOldTimer()
    {
        var dueAtUtc = StartedAtUtc.AddMinutes(5);
        var plan = Compile(
            Definition(
                "timer",
                [
                    new TimerProcessNode(
                        new("timer"),
                        Expr.Const(dueAtUtc),
                        Edge("edge/timer-return", "return")),
                    new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))
                ],
                ProcessRecoveryPolicy.RestartAttempt),
            definitionId: "process/durable-task-control-restart");
        var start = Start(plan, "restart", "instance/control-restart");
        var now = StartedAtUtc;
        var controls = Channel.CreateUnbounded<ProcessControlCommand>();
        ConcurrentQueue<ScheduledTimer> timers = [];
        ConcurrentQueue<DurableTaskSequentialProcessResult> observations = [];

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            (delay, cancellationToken) =>
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                timers.Enqueue(new(delay, cancellationToken, completion));
                return completion.Task;
            },
            () => now,
            observations.Enqueue,
            waitForControl: () => controls.Reader.ReadAsync().AsTask());

        await WaitUntilAsync(() => timers.Count == 1);
        var running = await WaitForObservationAsync(
            observations,
            static result => result.Control.CurrentAttempt.Phase == ProcessControlExecutionPhase.AtSafePoint);
        now = now.AddSeconds(1);
        var restart = Restart(start, running.Control, "control/restart", now, new("process-attempt/2"));
        await controls.Writer.WriteAsync(restart);

        var restarted = await WaitForObservationAsync(
            observations,
            result => result.LatestControlDecision?.Receipt?.Command.Context.CommandId
                == restart.Context.CommandId);
        Assert.Equal(ProcessControlDecisionDisposition.Applied, restarted.LatestControlDecision?.Disposition);
        Assert.Equal(new ProcessAttemptId("process-attempt/2"), restarted.Control.CurrentAttempt.AttemptId);

        await WaitUntilAsync(() => timers.Count == 2 || execution.IsCompleted);
        Assert.False(
            execution.IsCompleted,
            execution.Exception?.GetBaseException().ToString() ?? "Restart execution completed before scheduling the replacement timer.");
        Assert.True(timers.TryDequeue(out var abandoned));
        Assert.True(abandoned.CancellationToken.IsCancellationRequested);
        Assert.True(timers.TryDequeue(out var replacement));
        Assert.Equal(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1), replacement.Delay);
        now = dueAtUtc;
        replacement.Completion.SetResult();

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(new ProcessAttemptId("process-attempt/2"), result.State.Continuation.ProcessAttemptId);
        Assert.Equal(
            [ProcessControlAttemptDisposition.Abandoned, ProcessControlAttemptDisposition.Current],
            result.Control.Attempts.Select(static attempt => attempt.Disposition));
        Assert.Equal(restart.Context.CommandId, result.Control.Attempts[0].Closure?.CommandId);
        Assert.Equal(
            [
                start.Receipt.Request.InitialContinuation.ProcessAttemptId,
                new ProcessAttemptId("process-attempt/2"),
                new ProcessAttemptId("process-attempt/2")
            ],
            result.Traces.Select(static trace => trace.Continuation!.ProcessAttemptId));
    }

    [Fact]
    public async Task LifecycleControl_CancelAndTerminateRemainDistinctFromTransportCancellation()
    {
        var cancelled = await RunTerminalControlAsync(terminate: false);
        Assert.Equal(ProcessControlMode.Cancelled, cancelled.Result.Control.Mode);
        Assert.Equal(ProcessActivationDisposition.Cancelled, cancelled.Result.Disposition);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, cancelled.Result.State.Terminal.Kind);
        Assert.True(cancelled.TimerCancellation.IsCancellationRequested);
        var cancelledStatus = DurableTaskProcessStatus.Project(cancelled.Result);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, cancelledStatus.TerminalOutcome.Kind);
        Assert.Equal(ExecutionStatusDisclosure.Redacted, cancelledStatus.TerminalOutcome.Detail?.Disclosure);
        var cancelledStatusJson = DurableTaskProcessDataConverter.Create().Serialize(cancelledStatus)!;
        Assert.Contains("operator.cancel", Serialize(cancelled.Result));
        Assert.DoesNotContain("operator.cancel", cancelledStatusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("control/cancel", cancelledStatusJson, StringComparison.Ordinal);
        Assert.Equal(2, cancelled.Result.Traces.Length);
        Assert.Equal(cancelled.Result.Evidence[^1].Activation, cancelled.Result.Traces[^1].Activation);
        Assert.Equal("cancelled", cancelled.Result.Traces[^1].Disposition);

        var terminated = await RunTerminalControlAsync(terminate: true);
        Assert.Equal(ProcessControlMode.Terminated, terminated.Result.Control.Mode);
        Assert.Equal(ProcessActivationDisposition.DurableCut, terminated.Result.Disposition);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, terminated.Result.State.Terminal.Kind);
        Assert.True(terminated.TimerCancellation.IsCancellationRequested);
        var terminatedStatus = DurableTaskProcessStatus.Project(terminated.Result);
        Assert.Equal(ExecutionTerminalOutcomeKind.Terminated, terminatedStatus.TerminalOutcome.Kind);
        Assert.Equal(ExecutionStatusDisclosure.Redacted, terminatedStatus.TerminalOutcome.Detail?.Disclosure);
    }

    [Fact]
    public async Task Timer_ForkWinnerCancelsThePhysicalProjectionOfTheClosedCanonicalWait()
    {
        var firstDueAtUtc = StartedAtUtc.AddMinutes(1);
        var secondDueAtUtc = StartedAtUtc.AddMinutes(2);
        var plan = CompileForkTimerPlan(firstDueAtUtc, secondDueAtUtc);
        var start = Start(plan, "timer-fork", "instance/timer-fork");
        var initial = ProcessReferenceInterpreter.Create(plan, start.Receipt);
        var forked = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation(initial, ProcessActivationCause.Start, start),
            RejectingHost.Instance);
        Assert.Equal(ProcessActivationDisposition.DurableCut, forked.Disposition);
        var resumed = start.ContinueFrom(new(
            forked.Disposition,
            forked.State,
            ControlAfter(start, plan, initial, forked),
            null,
            forked.Emissions,
            forked.InputAdmissions,
            forked.Diagnostics,
            [forked.Evidence]));
        var now = StartedAtUtc;
        var continueAsNewCount = 0;
        ConcurrentQueue<ScheduledTimer> scheduled = [];

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            resumed,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            (delay, cancellationToken) =>
            {
                if (delay == TimeSpan.Zero)
                {
                    return Task.CompletedTask;
                }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                scheduled.Enqueue(new(delay, cancellationToken, completion));
                return completion.Task;
            },
            () => now,
            continueAsNew: next =>
            {
                Interlocked.Increment(ref continueAsNewCount);
                return Task.CompletedTask;
            });

        await WaitUntilAsync(() => scheduled.Count == 2);
        var timers = scheduled.OrderBy(static timer => timer.Delay).ToArray();
        Assert.Equal(
            [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)],
            timers.Select(static timer => timer.Delay));

        now = firstDueAtUtc;
        timers[0].Completion.SetResult();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(0, continueAsNewCount);
        Assert.False(timers[0].CancellationToken.IsCancellationRequested);
        Assert.True(timers[1].CancellationToken.IsCancellationRequested);
        var timerWaits = result.State.Waits.Where(static wait => wait.Kind == ProcessWaitKind.Timer).ToArray();
        Assert.Equal(2, timerWaits.Length);
        Assert.All(timerWaits, static wait => Assert.False(wait.Active));
        Assert.Single(
            result.Evidence.SelectMany(static evidence => evidence.Trace),
            static trace => trace.Kind == ProcessTraceEventKind.WaitResolved
                && trace.Node.Value.StartsWith("timer/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(10, 0, "clause/interaction", true)]
    [InlineData(0, 10, "clause/timer", false)]
    [InlineData(10, 10, "clause/interaction", true)]
    public async Task AwaitMatch_InteractionAndTimerRaceUsesCanonicalPriorityAndClauseTieBreak(
        int interactionPriority,
        int timerPriority,
        string expectedWinner,
        bool completeTimerFirst)
    {
        var dueAtUtc = StartedAtUtc.AddMinutes(5);
        var fixture = CompileAwaitMatchPlan(
            interactionPriority,
            timerPriority,
            $"process/durable-task-await-match-{interactionPriority}-{timerPriority}-{completeTimerFirst}");
        var start = Start(fixture.Plan, InstantValue(dueAtUtc), "instance/await-match-race");
        var now = StartedAtUtc;
        var timer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new TaskCompletionSource<ProcessActivationInput>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timerScheduled = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interactionWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitRegistered = new TaskCompletionSource<DurableTaskSequentialProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            fixture.Plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            () =>
            {
                interactionWaitStarted.TrySetResult();
                return interaction.Task;
            },
            (delay, cancellationToken) =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                timerScheduled.TrySetResult(delay);
                return timer.Task;
            },
            () => now,
            result =>
            {
                if (result.State.Waits.Any(static wait =>
                        wait.Active && wait.Kind == ProcessWaitKind.AwaitMatch))
                {
                    waitRegistered.TrySetResult(result);
                }
            });

        Assert.Equal(TimeSpan.FromMinutes(5), await timerScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await interactionWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var registeredResult = await waitRegistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var input = AwaitMatchInput(fixture, registeredResult, "emission/await-match-race");
        now = dueAtUtc;
        if (completeTimerFirst)
        {
            timer.SetResult();
            interaction.SetResult(input);
        }
        else
        {
            interaction.SetResult(input);
            timer.SetResult();
        }

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        var initial = ProcessReferenceInterpreter.Create(fixture.Plan, start.Receipt);
        var registered = ProcessReferenceInterpreter.Activate(
            fixture.Plan,
            initial,
            Activation(initial, ProcessActivationCause.Start, start),
            RejectingHost.Instance);
        var expected = ProcessReferenceInterpreter.Activate(
            fixture.Plan,
            registered.State,
            Activation(
                registered.State,
                ProcessActivationCause.Interaction,
                start,
                dueAtUtc,
                [input]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(new ExecutionNodeId(expectedWinner), Assert.Single(result.State.Waits).WinnerClause);
        Assert.Equal(Serialize(expected.State), Serialize(result.State));
        Assert.Equal(
            [Serialize(registered.Evidence), Serialize(expected.Evidence)],
            result.Evidence.Select(Serialize));
        var admission = Assert.Single(result.InputAdmissions);
        Assert.Equal(
            expectedWinner == "clause/interaction"
                ? ProcessInputAdmissionDisposition.Consumed
                : ProcessInputAdmissionDisposition.Observed,
            admission.Disposition);
    }

    [Fact]
    public async Task AwaitMatch_MultipleDueTimersUseCanonicalPriorityIndependentOfPhysicalCompletion()
    {
        var dueAtUtc = StartedAtUtc.AddMinutes(5);
        var plan = CompileAwaitMatchTimerPlan(dueAtUtc);
        var now = StartedAtUtc;
        ConcurrentQueue<ScheduledTimer> scheduled = [];

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            Start(plan, "timers", "instance/await-match-timers"),
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            (delay, cancellationToken) =>
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                scheduled.Enqueue(new(delay, cancellationToken, completion));
                return completion.Task;
            },
            () => now);

        await WaitUntilAsync(() => scheduled.Count == 2);
        var timers = scheduled.ToArray();
        Assert.All(timers, timer => Assert.Equal(TimeSpan.FromMinutes(5), timer.Delay));
        now = dueAtUtc;
        foreach (var timer in timers)
        {
            timer.Completion.SetResult();
        }

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(new ExecutionNodeId("clause/high"), Assert.Single(result.State.Waits).WinnerClause);
        Assert.Equal(StringValue("high"), result.State.Terminal.Detail?.Value);
    }

    [Fact]
    public async Task AwaitMatch_QueuedInputBeforeRegistrationRetainsCanonicalEarlyEvidence()
    {
        var dueAtUtc = StartedAtUtc.AddMinutes(5);
        var fixture = CompileAwaitMatchPlan(
            interactionPriority: 10,
            timerPriority: 0,
            "process/durable-task-await-match-early");
        var start = Start(fixture.Plan, InstantValue(dueAtUtc), "instance/await-match-early");
        var initial = ProcessReferenceInterpreter.Create(fixture.Plan, start.Receipt);
        var token = Assert.Single(initial.Tokens);
        var input = AwaitMatchInput(
            fixture,
            initial.Continuation,
            token.Id,
            waitRegistrationId: null,
            "emission/await-match-early");
        var scheduledTimers = 0;

        var result = await DurableTaskSequentialProcessInterpreter.RunAsync(
            fixture.Plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            () => Task.FromResult(input),
            (delay, cancellationToken) =>
            {
                Interlocked.Increment(ref scheduledTimers);
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            () => StartedAtUtc);

        var expectedFirst = ProcessReferenceInterpreter.Activate(
            fixture.Plan,
            initial,
            Activation(
                initial,
                ProcessActivationCause.Start,
                start,
                inputs: [input]),
            RejectingHost.Instance);
        var expected = ProcessReferenceInterpreter.Activate(
            fixture.Plan,
            expectedFirst.State,
            Activation(expectedFirst.State, ProcessActivationCause.Continue, start),
            RejectingHost.Instance);

        Assert.Equal(0, scheduledTimers);
        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(Serialize(expected.State), Serialize(result.State));
        Assert.Contains(
            result.Evidence.SelectMany(static evidence => evidence.Trace),
            static trace => trace.Kind == ProcessTraceEventKind.InputAdmitted
                && trace.InputReason == ProcessInputAdmissionReason.Early);
    }

    [Fact]
    public async Task AwaitMatch_MissingDuplicateAndStaleInputsRetainAuthoredCanonicalDispositions()
    {
        var dueAtUtc = StartedAtUtc.AddDays(1);
        var fixture = CompileAwaitMatchPlan(
            interactionPriority: 10,
            timerPriority: 0,
            "process/durable-task-await-match-input-policy");
        var start = Start(fixture.Plan, InstantValue(dueAtUtc), "instance/await-match-input-policy");
        ConcurrentQueue<TaskCompletionSource<ProcessActivationInput>> waiters = [];
        DurableTaskSequentialProcessResult? observed = null;

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            fixture.Plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            () =>
            {
                var waiter = new TaskCompletionSource<ProcessActivationInput>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Enqueue(waiter);
                return waiter.Task;
            },
            (delay, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            () => StartedAtUtc,
            result => observed = result);

        await WaitUntilAsync(() => observed?.State.Waits.Any(static wait => wait.Active) == true
            && waiters.Count == 1);
        var waiting = observed!;
        var incompatible = AwaitMatchInput(
            fixture,
            waiting,
            "emission/await-match-incompatible",
            fixture.AlternateEventContract);
        Assert.True(waiters.TryDequeue(out var missingWaiter));
        missingWaiter.SetResult(incompatible);

        await WaitUntilAsync(() => observed?.InputAdmissions.Length == 1 && waiters.Count == 1);
        Assert.True(waiters.TryDequeue(out var duplicateWaiter));
        duplicateWaiter.SetResult(incompatible);

        await WaitUntilAsync(() => observed?.InputAdmissions.Length == 2 && waiters.Count == 1);
        var activeWait = Assert.Single(observed!.State.Waits, static wait => wait.Active);
        var stale = AwaitMatchInput(
            fixture,
            new(
                observed.State.Continuation.ProcessInstanceId,
                new("process-attempt/stale")),
            activeWait.Token,
            activeWait.RegistrationId,
            "emission/await-match-stale");
        Assert.True(waiters.TryDequeue(out var staleWaiter));
        staleWaiter.SetResult(stale);

        await WaitUntilAsync(() => observed?.InputAdmissions.Length == 3 && waiters.Count == 1);
        var accepted = AwaitMatchInput(
            fixture,
            observed!,
            "emission/await-match-accepted");
        Assert.True(waiters.TryDequeue(out var acceptedWaiter));
        acceptedWaiter.SetResult(accepted);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(
            [
                ProcessInputAdmissionDisposition.DeadLettered,
                ProcessInputAdmissionDisposition.DeadLettered,
                ProcessInputAdmissionDisposition.Rejected,
                ProcessInputAdmissionDisposition.Consumed
            ],
            result.InputAdmissions.Select(static admission => admission.Disposition));
        Assert.Equal(
            [
                ProcessInputAdmissionReason.MissingTarget,
                ProcessInputAdmissionReason.Duplicate,
                ProcessInputAdmissionReason.Stale,
                ProcessInputAdmissionReason.Consumed
            ],
            result.InputAdmissions.Select(static admission => admission.Reason));
    }

    [Fact]
    public async Task Signal_TargetResolutionAndDeliveryPreserveTheExactCanonicalEnvelope()
    {
        var fixture = CompileSignalFixture("process/durable-task-signal-exact");
        var receiverStart = Start(fixture.Receiver, "receiver", "instance/signal-exact-receiver");
        var receiverInitial = ProcessReferenceInterpreter.Create(fixture.Receiver, receiverStart.Receipt);
        var target = new ProcessTokenInteractionTarget(
            receiverInitial.Continuation,
            Assert.Single(receiverInitial.Tokens).Id);
        var senderStart = Start(fixture.Sender, "payload", "instance/signal-exact-sender");
        List<ProcessSignalTargetResolution> resolutions = [];
        List<SignalEnvelope> deliveries = [];

        var actual = await DurableTaskSequentialProcessInterpreter.RunAsync(
            fixture.Sender,
            senderStart,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            TestDurableTimer,
            () => StartedAtUtc,
            resolveSignalTarget: resolution =>
            {
                resolutions.Add(resolution);
                return Task.FromResult(ProcessSignalTargetResult.Resolved(target));
            },
            deliverSignal: signal =>
            {
                deliveries.Add(signal);
                return Task.CompletedTask;
            });

        var initial = ProcessReferenceInterpreter.Create(fixture.Sender, senderStart.Receipt);
        var expected = ProcessReferenceInterpreter.Activate(
            fixture.Sender,
            initial,
            Activation(initial, ProcessActivationCause.Start, senderStart),
            new FixedSignalTargetHost(target));

        Assert.Equal(ProcessActivationDisposition.Completed, actual.Disposition);
        Assert.Equal(Serialize(expected.State), Serialize(actual.State));
        Assert.Equal(Serialize(expected.Evidence), Serialize(Assert.Single(actual.Evidence)));
        Assert.Equal(expected.Emissions.Select(Serialize), actual.Emissions.Select(Serialize));
        var resolution = Assert.Single(resolutions);
        Assert.Equal("route/process", resolution.Value.Value?.GetRequiredString());
        Assert.Equal(senderStart.Receipt.Request.InitialContinuation, resolution.Continuation);
        Assert.Equal(new ExecutionNodeId("signal"), resolution.Node);
        var signal = Assert.Single(deliveries);
        Assert.Equal(Assert.Single(actual.Emissions), signal);
        Assert.Equal(fixture.Contract, signal.Contract);
        Assert.Equal(StringValue("payload"), signal.Payload);
        Assert.Equal(target, signal.Target);
        Assert.Equal(senderStart.ActivationContext.CorrelationId, signal.Context.CorrelationId);
        Assert.Equal(senderStart.ActivationContext.Delivery, signal.Context.Delivery);
        Assert.Equal(senderStart.ActivationContext.Provenance, signal.Context.Provenance);
        var origin = Assert.IsType<ProcessInteractionOrigin>(signal.Context.Origin);
        Assert.Equal(fixture.Sender.DefinitionReference, origin.Definition);
        Assert.Equal(senderStart.Receipt.Request.InitialContinuation, origin.Continuation);
        Assert.Equal(new ExecutionNodeId("signal"), origin.Node);
    }

    [Fact]
    public async Task Signal_RecipientRetainsMissingStaleDuplicateAndConsumedCanonicalDispositions()
    {
        var fixture = CompileSignalFixture("process/durable-task-signal-admission");
        var receiverStart = Start(fixture.Receiver, "receiver", "instance/signal-admission-receiver");
        var receiverInitial = ProcessReferenceInterpreter.Create(fixture.Receiver, receiverStart.Receipt);
        var receiverToken = Assert.Single(receiverInitial.Tokens).Id;
        var correct = new ProcessTokenInteractionTarget(receiverInitial.Continuation, receiverToken);
        var missing = new ProcessTokenInteractionTarget(receiverInitial.Continuation, new("token/missing"));
        var stale = new ProcessTokenInteractionTarget(
            new(receiverInitial.Continuation.ProcessInstanceId, new("process-attempt/stale")),
            receiverToken);
        var missingSignal = await EmitSignalAsync(fixture.Sender, "sender/missing", missing);
        var staleSignal = await EmitSignalAsync(fixture.Sender, "sender/stale", stale);
        var first = await EmitSignalAsync(fixture.Sender, "sender/first", correct);
        var second = await EmitSignalAsync(fixture.Sender, "sender/second", correct);
        Queue<ProcessActivationInput> interactions = new([
            new(missing, missingSignal),
            new(stale, staleSignal),
            new(correct, first),
            new(correct, first),
            new(correct, second)
        ]);

        var actual = await DurableTaskSequentialProcessInterpreter.RunAsync(
            fixture.Receiver,
            receiverStart,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            () => Task.FromResult(interactions.Dequeue()),
            TestDurableTimer,
            () => StartedAtUtc.AddMinutes(1));

        var firstDecision = Activate(receiverInitial, ProcessActivationCause.Start, new(missing, missingSignal));
        var staleDecision = Activate(firstDecision.State, ProcessActivationCause.Interaction, new(stale, staleSignal));
        var firstConsumed = Activate(staleDecision.State, ProcessActivationCause.Interaction, new(correct, first));
        var continued = Activate(firstConsumed.State, ProcessActivationCause.Continue);
        var duplicate = Activate(continued.State, ProcessActivationCause.Interaction, new(correct, first));
        var secondConsumed = Activate(duplicate.State, ProcessActivationCause.Interaction, new(correct, second));
        var expected = new[]
        {
            firstDecision,
            staleDecision,
            firstConsumed,
            continued,
            duplicate,
            secondConsumed
        };

        Assert.Equal(ProcessActivationDisposition.Completed, actual.Disposition);
        Assert.Empty(interactions);
        Assert.Equal(Serialize(secondConsumed.State), Serialize(actual.State));
        Assert.Equal(expected.Select(static decision => Serialize(decision.Evidence)), actual.Evidence.Select(Serialize));
        Assert.Equal(
            expected.SelectMany(static decision => decision.InputAdmissions).Select(Serialize),
            actual.InputAdmissions.Select(Serialize));
        Assert.Equal(
            [
                ProcessInputAdmissionReason.MissingTarget,
                ProcessInputAdmissionReason.Stale,
                ProcessInputAdmissionReason.Consumed,
                ProcessInputAdmissionReason.Duplicate,
                ProcessInputAdmissionReason.Consumed
            ],
            actual.InputAdmissions.Select(static admission => admission.Reason));
        var waits = actual.State.Waits
            .Where(static wait => wait.Kind == ProcessWaitKind.AwaitMatch)
            .OrderBy(static wait => wait.Node.Value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, waits.Length);
        Assert.Equal(first.Context.EmissionId, waits[0].WinnerInput);
        Assert.Equal(second.Context.EmissionId, waits[1].WinnerInput);

        ProcessActivationDecision Activate(
            ProcessContinuationState state,
            ProcessActivationCause cause,
            ProcessActivationInput? input = null) => ProcessReferenceInterpreter.Activate(
            fixture.Receiver,
            state,
            Activation(
                state,
                cause,
                receiverStart,
                cause == ProcessActivationCause.Start ? StartedAtUtc : StartedAtUtc.AddMinutes(1),
                input is null ? [] : [input]),
            RejectingHost.Instance);
    }

    [Fact]
    public async Task SignalDelivery_FailsClosedForActivationLocalAndNonProcessTargets()
    {
        var fixture = CompileSignalFixture("process/durable-task-signal-delivery-boundary");
        var receiverStart = Start(fixture.Receiver, "receiver", "instance/signal-delivery-boundary");
        var receiverInitial = ProcessReferenceInterpreter.Create(fixture.Receiver, receiverStart.Receipt);
        var processTarget = new ProcessTokenInteractionTarget(
            receiverInitial.Continuation,
            Assert.Single(receiverInitial.Tokens).Id);
        var signal = await EmitSignalAsync(fixture.Sender, "sender/delivery-boundary", processTarget);
        var local = Copy(
            new(
                InteractionDurabilityDemand.ActivationLocal,
                InteractionVisibilityDemand.ActivationLocal),
            processTarget);
        var transitionTarget = new TransitionInteractionTarget(
            DefinitionReference("transition/signal-delivery-boundary", '8'),
            new("continuation/signal"),
            new(new("entity/order"), new("order/42")));
        var nonProcess = Copy(signal.Context.Delivery, transitionTarget);

        var localFailure = Assert.Throws<InvalidOperationException>(() =>
            DurableTaskSequentialProcessOrchestrator.RequireDurableProcessSignalTarget(local));
        Assert.Contains("activation-local", localFailure.Message, StringComparison.Ordinal);
        var targetFailure = Assert.Throws<InvalidOperationException>(() =>
            DurableTaskSequentialProcessOrchestrator.RequireDurableProcessSignalTarget(nonProcess));
        Assert.Contains(nameof(TransitionInteractionTarget), targetFailure.Message, StringComparison.Ordinal);

        SignalEnvelope Copy(InteractionDeliveryRequirements delivery, InteractionTarget target) => new(
            signal.SchemaVersion,
            new(
                signal.Context.EmissionId,
                signal.Context.Origin,
                signal.Context.CorrelationId,
                signal.Context.CausationId,
                signal.Context.AuthorityScope,
                signal.Context.IdempotencyKey,
                signal.Context.Ordering,
                delivery,
                signal.Context.Provenance),
            signal.Contract,
            signal.Payload,
            target);
    }

    [Fact]
    public async Task Request_WaitsForExactReplyAndPreservesTheCanonicalObligation()
    {
        var (plan, replyContract) = CompileRequestPlan(
            "process/durable-task-sequential-request");
        var start = Start(plan, "review/42");
        DurableTaskSequentialProcessResult? observed = null;

        var actual = await DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            () =>
            {
                var requested = Assert.IsType<RequestEnvelope>(Assert.Single(observed!.Emissions));
                var token = Assert.Single(observed.State.Tokens);
                var reply = new ReplyEnvelope(
                    InteractionEnvelope.CurrentSchemaVersion,
                    IncomingContext(
                        plan,
                        observed.State.Continuation,
                        token.Id,
                        "emission/reply",
                        requested.Context.EmissionId),
                    replyContract,
                    requested.Context.EmissionId,
                    new RequestResultOutcome(new("accepted"), StringValue("accepted")));
                return Task.FromResult(new ProcessActivationInput(
                    new(observed.State.Continuation, token.Id),
                    reply));
            },
            (delay, cancellationToken) => Task.CompletedTask,
            () => StartedAtUtc.AddMinutes(1),
            result => observed = result);

        Assert.Equal(ProcessActivationDisposition.Completed, actual.Disposition);
        Assert.Equal(2, actual.State.CompletedActivationCount);
        Assert.Empty(actual.State.OutstandingRequests);
        Assert.Equal(StringValue("accepted"), actual.State.Terminal.Detail?.Value);
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(actual.Emissions));
        Assert.Equal(request.Context.EmissionId, Assert.Single(observed!.Evidence[0].Trace
            .Where(static item => item.Kind == ProcessTraceEventKind.InteractionEmitted)).Emission);
        Assert.Contains(actual.InputAdmissions, static receipt =>
            receipt.Disposition == ProcessInputAdmissionDisposition.Consumed);
    }

    [Fact]
    public async Task ForkJoin_BoundRequestsAreInFlightTogetherAndCanonicalSelectionRemainsAuthoritative()
    {
        var fixture = CompileForkRequestPlan();
        var start = Start(fixture.Plan, "fork-input", "instance/fork-join");
        ConcurrentDictionary<EmissionId, TaskCompletionSource<DurableTaskDurableOperationAttemptResult>> pending = [];

        var execution = RunBoundRequests(
            fixture.Plan,
            start,
            fixture.Binding,
            invocation =>
            {
                var completion = new TaskCompletionSource<DurableTaskDurableOperationAttemptResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.True(pending.TryAdd(invocation.Request.Context.EmissionId, completion));
                return completion.Task;
            });

        for (var attempt = 0; attempt < 100 && pending.Count != 2; attempt++)
        {
            await Task.Delay(10);
        }
        Assert.Equal(2, pending.Count);
        Assert.False(execution.IsCompleted);

        foreach (var completion in pending.OrderByDescending(static pair => pair.Key.Value, StringComparer.Ordinal))
        {
            completion.Value.SetResult(new(
                new DurableOperationOutcomeObservation(
                    new RequestResultOutcome(new("accepted"), StringValue("accepted"))),
                deadlineElapsed: false));
        }

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(2, result.DurableOperations.Length);
        Assert.All(result.DurableOperations, static operation =>
            Assert.Equal(DurableOperationStatus.Dispositioned, operation.State.Status));
        var fork = Assert.Single(result.State.Forks);
        Assert.True(fork.Resolved);
        Assert.True(fork.SelectedBranches.SequenceEqual([
            new ExecutionNodeId("branch/a"),
            new ExecutionNodeId("branch/b")
        ]));
        Assert.Contains(
            result.Evidence.SelectMany(static item => item.Trace),
            static trace => trace.Kind == ProcessTraceEventKind.ForkCreated);
        Assert.Contains(
            result.Evidence.SelectMany(static item => item.Trace),
            static trace => trace.Kind == ProcessTraceEventKind.JoinResolved);
    }

    [Fact]
    public async Task ChildRequest_UsesChildExecutorAndAdmitsOnlyExactTerminalLineage()
    {
        var fixture = CompileChildParentPlan();
        var start = Start(fixture.Parent, "child-input", "instance/child-parent");
        ProcessChildRequestTarget? scheduledTarget = null;

        var result = await RunChildRequest(
            fixture.Parent,
            start,
            fixture.Binding,
            invocation =>
            {
                var request = invocation.Request;
                var target = Assert.IsType<ProcessChildRequestTarget>(request.ChildTarget);
                scheduledTarget = target;
                var outcome = new RequestResultOutcome(
                    ChildOutcomeMapping.Completed,
                    StringValue("child-completed"));
                var origin = new ProcessInteractionOrigin(
                    target.Definition,
                    new("return"),
                    target.Continuation,
                    new("activation/child-terminal"),
                    new("token/child-terminal"),
                    outcome: new("return"));
                return Task.FromResult(new DurableTaskDurableOperationAttemptResult(
                    new DurableOperationOutcomeObservation(outcome, replyOrigin: origin),
                    deadlineElapsed: false));
            });

        var child = Assert.Single(result.State.Children);
        Assert.Equal(fixture.Child.DefinitionReference, scheduledTarget!.Definition);
        Assert.Equal(child.Continuation, scheduledTarget.Continuation);
        Assert.Equal(ProcessChildCancellationPolicy.Propagate, child.Cancellation);
        Assert.Equal(ProcessChildDisposition.Completed, child.Disposition);
        Assert.Equal(ChildOutcomeMapping.Completed, child.TerminalOutcome);
        Assert.Equal(StringValue("child-completed"), child.Result);
        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
    }

    [Fact]
    public async Task ForkJoin_PropagatesExactCancellationAndWaitsForTheCancelledChildToClose()
    {
        var fixture = CompileForkChildPlan(ProcessChildCancellationPolicy.Propagate);
        var start = Start(fixture.Parent, "child-input", "instance/fork-child-propagate");
        ConcurrentDictionary<EmissionId, PendingChild> pending = [];
        var dispatched = new TaskCompletionSource<ProcessChildCancellationIntent>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            fixture.Parent,
            start,
            new BindingResolver(fixture.Binding),
            UnexpectedOperation,
            UnexpectedDurableOperation,
            invocation =>
            {
                var target = Assert.IsType<ProcessChildRequestTarget>(invocation.Request.ChildTarget);
                var completion = new TaskCompletionSource<DurableTaskDurableOperationAttemptResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.True(pending.TryAdd(
                    invocation.Request.Context.EmissionId,
                    new(target, completion)));
                return completion.Task;
            },
            UnexpectedReconciliation,
            UnexpectedInteraction,
            TestDurableTimer,
            () => StartedAtUtc.AddMinutes(1),
            dispatchChildCancellation: intent =>
            {
                dispatched.SetResult(intent);
                return Task.CompletedTask;
            });

        await WaitUntilAsync(() => pending.Count == 2);
        var winner = pending.OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal).First();
        var loser = pending.Single(pair => pair.Key != winner.Key);
        winner.Value.Completion.SetResult(CompletedChild(winner.Key, winner.Value.Target));

        var intent = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(execution.IsCompleted);
        Assert.Equal(fixture.Parent.DefinitionReference, intent.ParentDefinition);
        Assert.Equal(start.Receipt.Request.InitialContinuation, intent.ParentContinuation);
        Assert.Equal(loser.Key, intent.RequestEmission);
        Assert.Equal(fixture.Child.DefinitionReference, intent.ChildDefinition);
        Assert.Equal(loser.Value.Target.Continuation, intent.ChildContinuation);

        loser.Value.Completion.SetResult(CancelledChild(loser.Key, loser.Value.Target));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(ProcessChildDisposition.Completed, result.State.Children.Single(
            child => child.RequestEmission == winner.Key).Disposition);
        Assert.Equal(ProcessChildDisposition.CancellationRequested, result.State.Children.Single(
            child => child.RequestEmission == loser.Key).Disposition);
        var losingOperation = result.DurableOperations.Single(operation => operation.State.OperationId == loser.Key);
        Assert.Equal(DurableTaskDurableOperationDisposition.ResultDispositioned, losingOperation.Disposition);
        Assert.Equal(DurableOperationResultArrival.Late, losingOperation.State.Admission?.Arrival);
        Assert.Equal(DurableOperationAdmissionDisposition.Observed, losingOperation.State.Admission?.Disposition);
    }

    [Fact]
    public async Task ForkJoin_DetachesTheLosingChildWithoutDispatchingCancellationOrAwaitingIt()
    {
        var fixture = CompileForkChildPlan(ProcessChildCancellationPolicy.Detach);
        var start = Start(fixture.Parent, "child-input", "instance/fork-child-detach");
        ConcurrentDictionary<EmissionId, PendingChild> pending = [];
        var cancellationDispatches = 0;

        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            fixture.Parent,
            start,
            new BindingResolver(fixture.Binding),
            UnexpectedOperation,
            UnexpectedDurableOperation,
            invocation =>
            {
                var target = Assert.IsType<ProcessChildRequestTarget>(invocation.Request.ChildTarget);
                var completion = new TaskCompletionSource<DurableTaskDurableOperationAttemptResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.True(pending.TryAdd(
                    invocation.Request.Context.EmissionId,
                    new(target, completion)));
                return completion.Task;
            },
            UnexpectedReconciliation,
            UnexpectedInteraction,
            TestDurableTimer,
            () => StartedAtUtc.AddMinutes(1),
            dispatchChildCancellation: intent =>
            {
                Interlocked.Increment(ref cancellationDispatches);
                return Task.CompletedTask;
            });

        await WaitUntilAsync(() => pending.Count == 2);
        var winner = pending.OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal).First();
        var loser = pending.Single(pair => pair.Key != winner.Key);
        winner.Value.Completion.SetResult(CompletedChild(winner.Key, winner.Value.Target));

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(0, cancellationDispatches);
        Assert.Equal(ProcessChildDisposition.Detached, result.State.Children.Single(
            child => child.RequestEmission == loser.Key).Disposition);
        Assert.DoesNotContain(result.DurableOperations, operation => operation.State.OperationId == loser.Key);

        loser.Value.Completion.SetResult(CancelledChild(loser.Key, loser.Value.Target));
    }

    [Fact]
    public async Task ChildOrchestration_AppliesTheExactParentCancellationAtItsNextSafePoint()
    {
        var (child, _) = CompileRequestPlan("process/durable-task-child-cancellation-receiver");
        var start = Start(child, "waiting", "instance/child-cancellation-receiver");
        var intent = new ProcessChildCancellationIntent(
            "intent/parent-cancel",
            DefinitionReference("process/parent", '4'),
            new(new("instance/parent"), new("attempt/parent")),
            new("token/owner"),
            new("token/child"),
            new("child"),
            "child/registration",
            new("emission/child-request"),
            child.DefinitionReference,
            start.Receipt.Request.InitialContinuation,
            ProcessChildPurpose.Work);
        var neverInteracts = new TaskCompletionSource<ProcessActivationInput>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        List<ExecutionStatus> statuses = [];

        var result = await DurableTaskSequentialProcessInterpreter.RunAsync(
            child,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            () => neverInteracts.Task,
            TestDurableTimer,
            () => StartedAtUtc.AddMinutes(1),
            result => statuses.Add(DurableTaskProcessStatus.Project(result)),
            waitForChildCancellation: () => Task.FromResult(intent));

        Assert.Equal(ProcessActivationDisposition.Cancelled, result.Disposition);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, result.State.Terminal.Kind);
        Assert.Equal(ProcessControlMode.Cancelled, result.Control.Mode);
        var receipt = Assert.Single(result.Control.Receipts);
        Assert.Equal(intent.IntentId, receipt.Command.Context.CommandId.Value);
        Assert.NotEmpty(statuses);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, statuses[^1].TerminalOutcome.Kind);
        Assert.Contains(
            result.Evidence.SelectMany(static item => item.Trace),
            static trace => trace.Kind == ProcessTraceEventKind.CancellationApplied);
        var converter = DurableTaskProcessDataConverter.Create();
        var restored = Assert.IsType<ProcessChildCancellationIntent>(converter.Deserialize(
            converter.Serialize(intent),
            typeof(ProcessChildCancellationIntent)));
        Assert.Equal(Serialize(intent), Serialize(restored));
    }

    [Fact]
    public async Task ForEachPartition_EnforcesParallelismAndActivationStartBoundsBeforeSchedulingChildren()
    {
        var fixture = CompilePartitionParentPlan();
        var start = Start(
            fixture.Parent,
            CollectionValue("partition/c", "partition/a", "partition/b"),
            "instance/partition-parent");
        ConcurrentDictionary<EmissionId, TaskCompletionSource<DurableTaskDurableOperationAttemptResult>> pending = [];
        ConcurrentDictionary<EmissionId, ProcessChildRequestTarget> scheduled = [];

        var execution = RunChildRequest(
            fixture.Parent,
            start,
            fixture.Binding,
            invocation =>
            {
                var target = Assert.IsType<ProcessChildRequestTarget>(invocation.Request.ChildTarget);
                Assert.True(scheduled.TryAdd(invocation.Request.Context.EmissionId, target));
                var completion = new TaskCompletionSource<DurableTaskDurableOperationAttemptResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.True(pending.TryAdd(invocation.Request.Context.EmissionId, completion));
                return completion.Task;
            });

        for (var attempt = 0; attempt < 100 && pending.Count != 2; attempt++)
        {
            await Task.Yield();
        }
        Assert.Equal(2, pending.Count);
        Assert.Equal(2, scheduled.Count);

        var firstTarget = scheduled.Single(static pair => pair.Value.ProgressIdentity == "partition/a");
        var first = new KeyValuePair<EmissionId, TaskCompletionSource<DurableTaskDurableOperationAttemptResult>>(
            firstTarget.Key,
            pending[firstTarget.Key]);
        first.Value.SetResult(CompletedChild(first.Key, firstTarget.Value));

        for (var attempt = 0; attempt < 100 && pending.Count != 3 && !execution.IsCompleted; attempt++)
        {
            await Task.Delay(10);
        }
        if (execution.IsCompleted)
        {
            _ = await execution;
        }
        Assert.Equal(3, pending.Count);
        Assert.Equal(3, scheduled.Count);

        foreach (var candidate in pending.Where(candidate => !ReferenceEquals(candidate.Value, first.Value)))
        {
            candidate.Value.SetResult(CompletedChild(candidate.Key, scheduled[candidate.Key]));
        }

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(3, result.DurableOperations.Length);
        Assert.All(result.State.Children, static child =>
            Assert.Equal(ProcessChildDisposition.Completed, child.Disposition));
        Assert.Equal(
            ["partition/a", "partition/b", "partition/c"],
            result.State.Children.Select(static child => child.ProgressIdentity).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ForEachPartition_RejectsAnOverBoundWorkSetBeforeAnySubOrchestrationIsScheduled()
    {
        var fixture = CompilePartitionParentPlan();
        var start = Start(
            fixture.Parent,
            CollectionValue("partition/a", "partition/b", "partition/c", "partition/d"),
            "instance/partition-over-bound");
        var scheduled = 0;

        var result = await RunChildRequest(
            fixture.Parent,
            start,
            fixture.Binding,
            invocation =>
            {
                scheduled++;
                return Task.FromResult(CompletedChild(
                    invocation.Request.Context.EmissionId,
                    Assert.IsType<ProcessChildRequestTarget>(invocation.Request.ChildTarget)));
            });

        Assert.Equal(0, scheduled);
        Assert.Equal(ProcessActivationDisposition.Failed, result.Disposition);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.ContinuationInvalid
                                 && diagnostic.Message.Contains("explicit maximum of 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatAcrossActivation_ContinuesAsNewWithExactBoundedCanonicalState()
    {
        var plan = CompileRecurrencePlan();
        var current = Start(plan, "input", "instance/recurrence");
        List<DurableTaskSequentialProcessStart> rollovers = [];
        List<ExecutionStatus> rolloverStatuses = [];
        DurableTaskSequentialProcessResult result;

        do
        {
            DurableTaskSequentialProcessStart? next = null;
            result = await DurableTaskSequentialProcessInterpreter.RunAsync(
                plan,
                current,
                EmptyDurableRequestBindingResolver.Instance,
                UnexpectedOperation,
                UnexpectedDurableOperation,
                UnexpectedChildProcess,
                UnexpectedReconciliation,
                UnexpectedInteraction,
                (delay, cancellationToken) => Task.CompletedTask,
                () => StartedAtUtc.AddMinutes(rollovers.Count + 1),
                continueAsNew: resumed =>
                {
                    next = resumed;
                    return Task.CompletedTask;
                });
            if (next is null)
            {
                break;
            }
            rolloverStatuses.Add(DurableTaskProcessStatus.Project(result));
            rollovers.Add(next);
            current = next;
        }
        while (true);

        Assert.Equal(2, rollovers.Count);
        Assert.Equal(2, rolloverStatuses.Count);
        Assert.All(rolloverStatuses, status =>
        {
            Assert.Equal(current.Receipt.Request.InitialContinuation.ProcessInstanceId, status.ProcessInstanceId);
            Assert.Equal(ProcessControlMode.Running, status.ControlMode);
            Assert.Equal(ExecutionTerminalOutcomeKind.None, status.TerminalOutcome.Kind);
        });
        Assert.All(rollovers, static rollover => Assert.NotNull(rollover.Resume));
        Assert.Equal(
            [1, 2],
            rollovers.Select(static rollover => rollover.Resume!.Result.Traces.Length));
        Assert.Equal(ProcessActivationDisposition.Completed, result.Disposition);
        Assert.Equal(StringValue("exhausted"), result.State.Terminal.Detail?.Value);
        Assert.Equal(3, result.State.CompletedActivationCount);
        var recurrence = Assert.Single(result.State.Recurrences);
        Assert.False(recurrence.Active);
        Assert.Equal(2, recurrence.RepeatCount);
        Assert.Equal(3, result.Traces.Length);
        Assert.Equal(
            result.Evidence.Select(static evidence => evidence.Activation),
            result.Traces.Select(static trace => trace.Activation));
        var legacyResult = new DurableTaskSequentialProcessResult(
            result.Disposition,
            result.State,
            result.Control,
            result.LatestControlDecision,
            result.Emissions,
            result.InputAdmissions,
            result.Diagnostics,
            result.Evidence,
            result.DurableOperations);
        Assert.Empty(legacyResult.Traces);
        var reordered = Assert.Throws<ArgumentException>(() => new DurableTaskSequentialProcessResult(
            result.Disposition,
            result.State,
            result.Control,
            result.LatestControlDecision,
            result.Emissions,
            result.InputAdmissions,
            result.Diagnostics,
            result.Evidence,
            result.DurableOperations,
            result.Traces.Reverse().ToImmutableArray()));
        Assert.Contains("ordered canonical activation evidence", reordered.Message, StringComparison.Ordinal);

        var converter = DurableTaskProcessDataConverter.Create();
        var restored = Assert.IsType<DurableTaskSequentialProcessStart>(converter.Deserialize(
            converter.Serialize(rollovers[1]),
            typeof(DurableTaskSequentialProcessStart)));
        Assert.Equal(Serialize(rollovers[1]), Serialize(restored));
    }

    [Fact]
    public async Task DurableRequest_AutomaticallyDispatchesAndAdmitsTheExactCanonicalReply()
    {
        var (plan, replyContract) = CompileRequestPlan("process/durable-task-durable-request");
        var binding = Binding(plan, replyContract);
        var start = Start(plan, "review/42");
        List<DurableOperationInvocation> invocations = [];

        var actual = await RunDurableRequest(
            plan,
            start,
            binding,
            invocation =>
            {
                invocations.Add(invocation);
                return new(
                    new DurableOperationOutcomeObservation(
                        new RequestResultOutcome(new("accepted"), StringValue("accepted"))),
                    deadlineElapsed: false);
            });

        Assert.Equal(ProcessActivationDisposition.Completed, actual.Disposition);
        Assert.Equal(StringValue("accepted"), actual.State.Terminal.Detail?.Value);
        var operation = Assert.Single(actual.DurableOperations).State;
        Assert.Equal(DurableOperationStatus.Dispositioned, operation.Status);
        Assert.Equal(Assert.Single(actual.Emissions).Context.EmissionId, operation.OperationId);
        Assert.Equal(DurableOperationIdentities.Attempt(operation.OperationId, 1), Assert.Single(invocations).AttemptId);
        Assert.Equal(
            DurableOperationIdentities.Reply(operation.OperationId),
            Assert.Single(actual.InputAdmissions).Emission);
        var converter = DurableTaskProcessDataConverter.Create();
        var restoredInvocation = Assert.IsType<DurableOperationInvocation>(converter.Deserialize(
            converter.Serialize(Assert.Single(invocations)),
            typeof(DurableOperationInvocation)));
        Assert.Equal(Serialize(Assert.Single(invocations)), Serialize(restoredInvocation));

        var replayInvocations = new List<DurableOperationInvocation>();
        var replay = await RunDurableRequest(
            plan,
            start,
            binding,
            invocation =>
            {
                replayInvocations.Add(invocation);
                return new(
                    new DurableOperationOutcomeObservation(
                        new RequestResultOutcome(new("accepted"), StringValue("accepted"))),
                    deadlineElapsed: false);
            });
        Assert.Equal(Serialize(actual), Serialize(replay));
        Assert.Equal(Serialize(Assert.Single(invocations)), Serialize(Assert.Single(replayInvocations)));
    }

    [Fact]
    public async Task DurableRequest_UsesCanonicalBoundedRetryAndStableLogicalIdentity()
    {
        var (plan, replyContract) = CompileRequestPlan("process/durable-task-durable-retry");
        var binding = Binding(plan, replyContract, maxAttempts: 2);
        var start = Start(plan, "review/retry");
        List<DurableOperationInvocation> invocations = [];

        var actual = await RunDurableRequest(
            plan,
            start,
            binding,
            invocation =>
            {
                invocations.Add(invocation);
                return invocation.AttemptOrdinal == 1
                    ? new(
                        new DurableOperationFailureObservation(new(
                            DurableOperationFailurePhase.PreCall,
                            DurableOperationEffectEvidence.NotExecuted,
                            DurableOperationFailureDisposition.Retryable,
                            "tests.retry")),
                        deadlineElapsed: false)
                    : new(
                        new DurableOperationOutcomeObservation(
                            new RequestResultOutcome(new("accepted"), StringValue("accepted"))),
                        deadlineElapsed: false);
            });

        var operation = Assert.Single(actual.DurableOperations).State;
        Assert.Equal(DurableOperationStatus.Dispositioned, operation.Status);
        Assert.Equal(2, invocations.Count);
        Assert.All(invocations, invocation => Assert.Equal(operation.OperationId, invocation.Request.Context.EmissionId));
        Assert.All(invocations, invocation => Assert.Equal(operation.DeduplicationKey, invocation.DeduplicationKey));
        Assert.Equal(DurableOperationIdentities.Attempt(operation.OperationId, 1), invocations[0].AttemptId);
        Assert.Equal(DurableOperationIdentities.Attempt(operation.OperationId, 2), invocations[1].AttemptId);
    }

    [Fact]
    public async Task DurableRequest_ReconcilesAmbiguousDispatchBeforeAdmittingReply()
    {
        var (plan, replyContract) = CompileRequestPlan("process/durable-task-durable-reconcile");
        var binding = Binding(plan, replyContract);
        var start = Start(plan, "review/reconcile");
        DurableOperationState? reconciledState = null;

        var actual = await RunDurableRequest(
            plan,
            start,
            binding,
            invocation => new(
                new DurableOperationFailureObservation(new(
                    DurableOperationFailurePhase.InCall,
                    DurableOperationEffectEvidence.Ambiguous,
                    DurableOperationFailureDisposition.Retryable,
                    "tests.ambiguous")),
                deadlineElapsed: false),
            operation =>
            {
                reconciledState = operation;
                return new(
                    new DurableOperationReconciledOutcome(
                        new RequestResultOutcome(new("accepted"), StringValue("accepted"))),
                    deadlineElapsed: false);
            });

        var final = Assert.Single(actual.DurableOperations).State;
        Assert.Equal(DurableOperationStatus.ReconciliationRequired, reconciledState!.Status);
        Assert.Equal(DurableOperationStatus.Dispositioned, final.Status);
        Assert.Single(final.Reconciliations);
        Assert.Equal(
            DurableOperationRecoveryRequirement.Reconcile,
            final.Acknowledgement!.RecoveryIdentity!.Requirement);
    }

    [Fact]
    public async Task DurableRequest_UnresolvedReconciliationFailsClosedWithExactEscalationIntent()
    {
        var (plan, replyContract) = CompileRequestPlan("process/durable-task-durable-escalation");
        var binding = Binding(plan, replyContract);
        var start = Start(plan, "review/escalate");

        var actual = await RunDurableRequest(
            plan,
            start,
            binding,
            invocation => new(
                new DurableOperationFailureObservation(new(
                    DurableOperationFailurePhase.InCall,
                    DurableOperationEffectEvidence.Ambiguous,
                    DurableOperationFailureDisposition.Retryable,
                    "tests.ambiguous")),
                deadlineElapsed: false),
            operation => new(new DurableOperationUnresolved(), deadlineElapsed: false));

        Assert.Equal(ProcessActivationDisposition.DurableCut, actual.Disposition);
        var operationResult = Assert.Single(actual.DurableOperations);
        var operation = operationResult.State;
        Assert.Equal(DurableTaskDurableOperationDisposition.RecoveryRequired, operationResult.Disposition);
        Assert.Equal(DurableOperationStatus.EscalationRequired, operation.Status);
        var intent = Assert.IsType<DurableOperationRecoveryIntent>(
            DurableOperationReferenceExecutor.GetRecoveryIntent(operation));
        Assert.Equal(binding.EscalationTarget, intent.Target);
        Assert.Equal(operation.OperationId, intent.Identity.OperationId);
    }

    [Fact]
    public async Task DurableRequest_RejectsAtLeastOnceActivityDispatchWithoutTargetIdempotencyEvidence()
    {
        var (plan, replyContract) = CompileRequestPlan("process/durable-task-durable-no-idempotency");
        var binding = Binding(
            plan,
            replyContract,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RunDurableRequest(
            plan,
            Start(plan, "review/unsafe"),
            binding,
            invocation => throw new InvalidOperationException("Dispatch must not run.")));

        Assert.Contains("at-least-once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DurableRequest_DeadlineFailsClosedWithoutFabricatingTypedTimeoutEvidence()
    {
        var fixture = DurableOperationTestFixture.Create(timeoutAfter: TimeSpan.FromMinutes(1));
        var now = DurableOperationTestFixture.CreatedAtUtc;
        var neverCompletes = new TaskCompletionSource<DurableTaskDurableOperationAttemptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await DurableTaskDurableOperationInterpreter.RunAsync(
            fixture.Catalog,
            fixture.Request(),
            fixture.Binding,
            invocation => neverCompletes.Task,
            UnexpectedReconciliation,
            (delay, cancellationToken) =>
            {
                now = now.Add(delay);
                return Task.CompletedTask;
            },
            () => now);

        Assert.Equal(DurableTaskDurableOperationDisposition.DeadlineElapsed, result.Disposition);
        Assert.Null(result.State.Acknowledgement);
        Assert.Equal(DurableOperationStatus.Dispatched, result.State.Status);
        Assert.Equal(fixture.Binding.TimeoutAfter, now - result.State.CreatedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DurableRequest_CrashCutsReplayOneLogicalTargetEffect(int crashCutValue)
    {
        var crashCut = (DurableTaskDurableOperationCut)crashCutValue;
        var (plan, replyContract) = CompileRequestPlan($"process/durable-task-cut-{crashCut}");
        var binding = Binding(plan, replyContract);
        var start = Start(plan, "review/crash");
        var initial = ProcessReferenceInterpreter.Create(plan, start.Receipt);
        var first = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation(initial, ProcessActivationCause.Start, start),
            RejectingHost.Instance);
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(first.Emissions));
        Dictionary<OperationAttemptId, DurableTaskDurableOperationAttemptResult> activityHistory = [];
        var targetEffects = 0;
        var crashed = false;

        Task<DurableTaskDurableOperationAttemptResult> Execute(DurableOperationInvocation invocation)
        {
            if (!activityHistory.TryGetValue(invocation.AttemptId, out var result))
            {
                targetEffects++;
                result = new(
                    new DurableOperationOutcomeObservation(
                        new RequestResultOutcome(new("accepted"), StringValue("accepted"))),
                    deadlineElapsed: false);
                activityHistory.Add(invocation.AttemptId, result);
            }
            return Task.FromResult(result);
        }

        async Task RunOnce()
        {
            _ = await DurableTaskDurableOperationInterpreter.RunAsync(
                plan.ValidationContext.InteractionContracts!,
                request,
                binding,
                Execute,
                UnexpectedReconciliation,
                (delay, cancellationToken) => Task.CompletedTask,
                () => StartedAtUtc,
                (cut, operation) =>
                {
                    if (!crashed && cut == crashCut)
                    {
                        crashed = true;
                        throw new SimulatedCrashException();
                    }
                    return Task.CompletedTask;
                });
        }

        await Assert.ThrowsAsync<SimulatedCrashException>(RunOnce);
        await RunOnce();

        Assert.Equal(1, targetEffects);
        Assert.Single(activityHistory);
    }

    [Fact]
    public void ExecutableQualification_AdmitsTimerAwaitMatchAndSignalButRejectsEmitEvent()
    {
        var timerPlan = CompileTimerPlan(
            StartedAtUtc.AddMinutes(1),
            "process/durable-task-catalog-timer");
        var awaitMatchPlan = CompileAwaitMatchTimerPlan(StartedAtUtc.AddMinutes(1));
        var signalPlan = CompileSignalFixture("process/durable-task-catalog-signal").Sender;
        var admitted = new DurableTaskSequentialProcessPlanCatalog([
            Physical(timerPlan),
            Physical(awaitMatchPlan),
            Physical(signalPlan)
        ]);

        Assert.Equal(3, admitted.Count);

        var eventDocument = InteractionDocument(
            "interaction/event/durable-task-catalog-unsupported",
            new DomainEventContractDefinition(new(StringContract, new("catalog-event/v1"))));
        DomainEventContractReference eventContract = new(Reference(eventDocument));
        var emitPlan = Compile(Definition(
            "emit",
            [
                new EmitEventProcessNode(
                    new("emit"),
                    eventContract,
                    Expr.Const("event"),
                    Edge("edge/emit-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]),
            Catalog(eventDocument),
            definitionId: "process/durable-task-catalog-emit-event");

        var rejected = DurableTaskProcessRealizationCompiler.CompileExecutable(emitPlan);

        Assert.False(rejected.IsSuccessful);
        Assert.Null(rejected.Plan);
        var diagnostic = Assert.Single(
            rejected.Realization.Diagnostics,
            static candidate =>
                candidate.Code == ProcessInterpreterRealizationDiagnosticCodes.RequirementUnavailable
                && candidate.Requirement == ProcessInterpreterRequirementKey.ForConstruct(ProcessWireNames.EmitEventNode));
        Assert.Equal(new ExecutionNodeId("emit"), Assert.Single(diagnostic.Nodes));
    }

    [Fact]
    public void PlanCatalog_RequiresOneFingerprintForEachDefinitionRevisionAndExactLookup()
    {
        var first = Compile(
            Definition("return", [new ReturnProcessNode(new("return"), Expr.Const("first"))]),
            definitionId: "process/durable-task-catalog-conflict");
        var conflicting = Compile(
            Definition("return", [new ReturnProcessNode(new("return"), Expr.Const("conflicting"))]),
            definitionId: "process/durable-task-catalog-conflict");

        var conflict = Assert.Throws<ArgumentException>(() =>
            new DurableTaskSequentialProcessPlanCatalog([Physical(first), Physical(conflicting)]));
        Assert.Contains("conflicting fingerprints", conflict.Message, StringComparison.Ordinal);

        var catalog = new DurableTaskSequentialProcessPlanCatalog([Physical(first)]);
        var unknownFingerprint = new ExecutionDefinitionReference(
            first.DefinitionReference.DefinitionId,
            first.DefinitionReference.RevisionId,
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                new string('f', 64)));
        Assert.Throws<KeyNotFoundException>(() => catalog.GetExact(unknownFingerprint));
    }

    [Fact]
    public async Task PortableSdkConverter_RoundTripsStartContinuationAndEvidence()
    {
        var plan = Compile(Definition(
            "return",
            [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))]));
        var start = Start(plan, "portable");
        var result = await Run(plan, start, UnexpectedOperation);
        var initial = ProcessReferenceInterpreter.Create(plan, start.Receipt);
        var signalTarget = ProcessSignalTargetResult.Resolved(new ProcessTokenInteractionTarget(
            initial.Continuation,
            Assert.Single(initial.Tokens).Id));
        ProcessControlCommand controlCommand = Pause(
            start,
            start.Receipt.CreateInitialState(),
            "control/portable",
            StartedAtUtc);
        var converter = DurableTaskProcessDataConverter.Create();

        var restoredStart = Assert.IsType<DurableTaskSequentialProcessStart>(
            converter.Deserialize(converter.Serialize(start), typeof(DurableTaskSequentialProcessStart)));
        var restoredResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            converter.Deserialize(converter.Serialize(result), typeof(DurableTaskSequentialProcessResult)));
        var restoredSignalTarget = Assert.IsType<ProcessSignalTargetResult>(
            converter.Deserialize(
                converter.Serialize(signalTarget),
                typeof(ProcessSignalTargetResult)));
        var restoredControl = Assert.IsType<PauseProcessCommand>(
            converter.Deserialize(
                converter.Serialize(controlCommand),
                typeof(ProcessControlCommand)));

        Assert.Equal(Serialize(start), Serialize(restoredStart));
        Assert.Equal(Serialize(result), Serialize(restoredResult));
        Assert.Single(restoredResult.Traces);
        Assert.Equal(Serialize(signalTarget), Serialize(restoredSignalTarget));
        Assert.Equal(Serialize(controlCommand), Serialize(restoredControl));
    }

    [Fact]
    public void TraceRetention_FailsClosedWithCanonicalProjectionDiagnostics()
    {
        var failure = ExecutionTraceProjectionResult.Failure(
        [
            new(
                ExecutionTraceDiagnosticCodes.DefinitionMismatch,
                DiagnosticSeverity.Error,
                "Process trace evidence and replacement state must name the same exact definition.")
        ]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            DurableTaskSequentialProcessInterpreter.RequireTrace(failure));

        Assert.Contains(ExecutionTraceDiagnosticCodes.DefinitionMismatch, exception.Message, StringComparison.Ordinal);
        Assert.Contains("same exact definition", exception.Message, StringComparison.Ordinal);
    }

    [DurableTaskSchedulerFact]
    public async Task SchedulerEmulator_ProvesCanonicalLifecycleControlAndAttemptLineage()
    {
        var connectionString = Environment.GetEnvironmentVariable("DURABLE_TASK_SCHEDULER_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "The Durable Task Scheduler connection string disappeared after test discovery.");
        var timerPlan = CompileInputTimerPlan(
            "process/durable-task-scheduler-control",
            ProcessRecoveryPolicy.ContinueAttempt);
        var restartPlan = CompileInputTimerPlan(
            "process/durable-task-scheduler-control-restart",
            ProcessRecoveryPolicy.RestartAttempt);
        var catalog = new DurableTaskSequentialProcessPlanCatalog([
            Physical(timerPlan),
            Physical(restartPlan)
        ]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var worker = SchedulerHost(connectionString, catalog, RejectingHost.Instance);
        await worker.StartAsync(timeout.Token);
        var client = worker.Services.GetRequiredService<DurableTaskClient>();
        var dueAtUtc = DateTimeOffset.UtcNow.AddMinutes(2);

        var controlledStart = Start(
            timerPlan,
            InstantValue(dueAtUtc),
            "instance/scheduler-lifecycle-control");
        var controlledSchedule = await client.ScheduleCohesiveProcessAsync(controlledStart, timeout.Token);
        var running = await WaitForActiveWait(
            client,
            controlledSchedule.InstanceId,
            ProcessWaitKind.Timer,
            timeout.Token);
        var scheduledMetadata = await client.GetInstanceAsync(
            controlledSchedule.InstanceId,
            getInputsAndOutputs: false,
            timeout.Token);
        Assert.NotNull(scheduledMetadata);
        var expectedTags = DurableTaskProcessTags.Create(controlledStart.Receipt);
        Assert.Equal(expectedTags.Count, scheduledMetadata.Tags.Count);
        Assert.All(expectedTags, expected =>
            Assert.Equal(expected.Value, scheduledMetadata.Tags[expected.Key]));
        var executionRepository = new DurableTaskProcessExecutionRepository(client);
        var observed = await executionRepository.GetAsync(
            OperationContext.Create(cancellationToken: timeout.Token),
            controlledStart.Receipt.Request.Context.Authorization.AuthorityScope,
            controlledStart.Receipt.Request.InitialContinuation.ProcessInstanceId);
        Assert.NotNull(observed);
        Assert.Equal(ProcessExecutionStatus.Waiting, observed.Status);
        Assert.Equal(Serialize(running), Serialize(observed.RuntimeStatus));
        Assert.Null(observed.Parameters);
        Assert.Null(observed.Output);

        var pause = Pause(controlledStart, running, "scheduler-control/pause", DateTimeOffset.UtcNow);
        await client.RaiseCohesiveProcessControlAsync(controlledStart, pause, timeout.Token);
        var paused = await WaitForControlStatus(
            client,
            controlledSchedule.InstanceId,
            status => status.ControlMode == ProcessControlMode.Paused
                && status.ControlRevision != running.ControlRevision,
            timeout.Token);

        var inspect = Inspect(controlledStart, paused, "scheduler-control/inspect", DateTimeOffset.UtcNow);
        await client.RaiseCohesiveProcessControlAsync(controlledStart, inspect, timeout.Token);

        await client.RaiseCohesiveProcessControlAsync(controlledStart, pause, timeout.Token);

        var @continue = Continue(
            controlledStart,
            paused,
            "scheduler-control/continue",
            DateTimeOffset.UtcNow);
        await client.RaiseCohesiveProcessControlAsync(controlledStart, @continue, timeout.Token);
        var continued = await WaitForControlStatus(
            client,
            controlledSchedule.InstanceId,
            status => status.ControlMode == ProcessControlMode.Running
                && status.ControlRevision != paused.ControlRevision,
            timeout.Token);
        var cancel = new CancelProcessCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            ControlContext(controlledStart, "scheduler-control/cancel", DateTimeOffset.UtcNow),
            Expectation(continued),
            new("scheduler.cancel"));
        await client.RaiseCohesiveProcessControlAsync(controlledStart, cancel, timeout.Token);
        var cancelledInstance = await client.WaitForInstanceCompletionAsync(
            controlledSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        var cancelled = Assert.IsType<DurableTaskSequentialProcessResult>(
            cancelledInstance.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(ProcessControlMode.Cancelled, cancelled.Control.Mode);
        Assert.Equal(ProcessActivationDisposition.Cancelled, cancelled.Disposition);
        var cancelledObservation = await executionRepository.GetAsync(
            OperationContext.Create(cancellationToken: timeout.Token),
            controlledSchedule.InstanceId);
        Assert.NotNull(cancelledObservation);
        Assert.Equal(ProcessExecutionStatus.Cancelled, cancelledObservation.Status);
        Assert.Equal(
            ExecutionTerminalOutcomeKind.Cancelled,
            cancelledObservation.RuntimeStatus?.TerminalOutcome.Kind);

        var restartStart = Start(
            restartPlan,
            InstantValue(dueAtUtc),
            "instance/scheduler-lifecycle-restart");
        var restartSchedule = await client.ScheduleCohesiveProcessAsync(restartStart, timeout.Token);
        var restartRunning = await WaitForActiveWait(
            client,
            restartSchedule.InstanceId,
            ProcessWaitKind.Timer,
            timeout.Token);
        var restart = Restart(
            restartStart,
            restartRunning,
            "scheduler-control/restart",
            DateTimeOffset.UtcNow,
            new("process-attempt/2"));
        await client.RaiseCohesiveProcessControlAsync(restartStart, restart, timeout.Token);
        var replacement = await WaitForControlStatus(
            client,
            restartSchedule.InstanceId,
            static status => status.CurrentAttemptId == new ProcessAttemptId("process-attempt/2")
                && status.ControlMode == ProcessControlMode.Running,
            timeout.Token);
        var terminate = new TerminateProcessCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            ControlContext(restartStart, "scheduler-control/terminate", DateTimeOffset.UtcNow),
            Expectation(replacement),
            new("scheduler.terminate"),
            ProcessAttemptCleanupRequirement.RetainEvidence);
        await client.RaiseCohesiveProcessControlAsync(restartStart, terminate, timeout.Token);
        var terminatedInstance = await client.WaitForInstanceCompletionAsync(
            restartSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        var terminated = Assert.IsType<DurableTaskSequentialProcessResult>(
            terminatedInstance.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(ProcessControlMode.Terminated, terminated.Control.Mode);
        Assert.Equal(
            [ProcessControlAttemptDisposition.Abandoned, ProcessControlAttemptDisposition.Terminated],
            terminated.Control.Attempts.Select(static attempt => attempt.Disposition));
    }

    [DurableTaskSchedulerFact]
    public async Task SchedulerEmulator_ProvesCompletionFailureDuplicateStartAndWorkerRestart()
    {
        var connectionString = Environment.GetEnvironmentVariable("DURABLE_TASK_SCHEDULER_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "The Durable Task Scheduler connection string disappeared after test discovery.");
        var completedPlan = Compile(
            Definition(
                "return",
                [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))]),
            definitionId: "process/durable-task-scheduler-completed");
        var failedPlan = Compile(
            Definition(
                "fail",
                [new FailProcessNode(new("fail"), Expr.Const("authored-failure"))]),
            definitionId: "process/durable-task-scheduler-failed");
        var transition = DefinitionReference("transition/durable-task-scheduler-restart", '3');
        var (restartPlan, replyContract) = CompileRequestPlan(
            "process/durable-task-scheduler-restart",
            transition);
        var (durableRequestPlan, durableReplyContract) = CompileRequestPlan(
            "process/durable-task-scheduler-durable-request");
        var durableBinding = Binding(durableRequestPlan, durableReplyContract);
        var childFixture = CompileChildParentPlan();
        var forkChildFixture = CompileSchedulerForkChildPlan();
        var recurrencePlan = CompileRecurrencePlan();
        var timerPlan = CompileInputTimerPlan();
        var awaitMatchFixture = CompileAwaitMatchPlan(
            interactionPriority: 10,
            timerPriority: 0,
            "process/durable-task-scheduler-await-match");
        var signalFixture = CompileSignalFixture(
            "process/durable-task-scheduler-signal",
            receiveTwice: false);
        var selfSignalFixture = CompileSelfSignalFixture("process/durable-task-scheduler-self-signal");
        var catalog = new DurableTaskSequentialProcessPlanCatalog([
            Physical(completedPlan),
            Physical(failedPlan),
            Physical(restartPlan),
            Physical(durableRequestPlan),
            Physical(childFixture.Parent),
            Physical(childFixture.Child),
            Physical(forkChildFixture.Parent),
            Physical(forkChildFixture.FastChild),
            Physical(forkChildFixture.SlowChild),
            Physical(recurrencePlan),
            Physical(timerPlan),
            Physical(awaitMatchFixture.Plan),
            Physical(signalFixture.Sender),
            Physical(signalFixture.Receiver),
            Physical(selfSignalFixture.Plan)
        ], new BindingResolver(durableBinding, childFixture.Binding, forkChildFixture.Binding));
        var operations = new CountingEchoHost();
        var durableOperations = new CountingDurableOperationAdapter(durableBinding.Request);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var firstWorker = SchedulerHost(connectionString, catalog, operations, durableOperations);
        var workerOptions = firstWorker.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<DurableTaskWorkerOptions>>()
            .Get(Microsoft.Extensions.Options.Options.DefaultName);
        Assert.Equal(DurableTaskProcessDataConverter.Create().GetType(), workerOptions.DataConverter.GetType());
        await firstWorker.StartAsync(timeout.Token);
        var firstClient = firstWorker.Services.GetRequiredService<DurableTaskClient>();

        var completedStart = Start(completedPlan, "completed", "instance/completed");
        var scheduled = await firstClient.ScheduleCohesiveProcessAsync(completedStart, timeout.Token);
        var completed = await firstClient.WaitForInstanceCompletionAsync(
            scheduled.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.True(
            completed.RuntimeStatus == OrchestrationRuntimeStatus.Completed,
            completed.FailureDetails?.ToString());
        Assert.Equal(
            ProcessActivationDisposition.Completed,
            completed.ReadOutputAs<DurableTaskSequentialProcessResult>()!.Disposition);
        var completedTraceRead = await new DurableTaskProcessExecutionRepository(firstClient).GetTracesAsync(
            OperationContext.Create(cancellationToken: timeout.Token),
            scheduled.InstanceId);
        Assert.Equal(ProcessExecutionTraceReadState.Available, completedTraceRead.State);
        var completedTraces = Assert.IsType<ProcessExecutionTraceArtifact>(completedTraceRead.Artifact);
        Assert.True(completedTraces.IsComplete);
        Assert.NotEmpty(completedTraces.Traces);
        var completedExplanation = await new DurableTaskProcessExecutionExplainRepository(
            new(firstClient),
            catalog).GetExplainAsync(
                OperationContext.Create(cancellationToken: timeout.Token),
                scheduled.InstanceId);
        Assert.NotNull(completedExplanation);
        Assert.Equal(completedPlan.DefinitionReference, completedExplanation.Definition.Definition);
        Assert.NotNull(completedExplanation.Trace);

        var duplicate = await firstClient.ScheduleCohesiveProcessAsync(completedStart, timeout.Token);
        Assert.True(duplicate.Replayed);
        Assert.Equal(scheduled.InstanceId, duplicate.InstanceId);

        var signalReceiverStart = Start(
            signalFixture.Receiver,
            "receiver",
            "instance/scheduler-signal-receiver");
        var signalReceiverSchedule = await firstClient.ScheduleCohesiveProcessAsync(
            signalReceiverStart,
            timeout.Token);
        var signalReceiverWaiting = await WaitForActiveWait(
            firstClient,
            signalReceiverSchedule.InstanceId,
            ProcessWaitKind.AwaitMatch,
            timeout.Token);
        var signalReceiverToken = Assert.Single(signalReceiverWaiting.Runtime.Tokens).TokenId;
        var signalReceiverContinuation = new ProcessContinuationIdentity(
            signalReceiverWaiting.ProcessInstanceId,
            signalReceiverWaiting.CurrentAttemptId);
        operations.RegisterSignalTarget(
            "route/process",
            new(signalReceiverContinuation, signalReceiverToken));
        var firstSignalStart = Start(
            signalFixture.Sender,
            "first",
            "instance/scheduler-signal-sender-first");
        var firstSignalSchedule = await firstClient.ScheduleCohesiveProcessAsync(firstSignalStart, timeout.Token);
        var firstSignalCompleted = await firstClient.WaitForInstanceCompletionAsync(
            firstSignalSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.True(
            firstSignalCompleted.RuntimeStatus == OrchestrationRuntimeStatus.Completed,
            firstSignalCompleted.FailureDetails?.ToString());
        var firstSignalResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            firstSignalCompleted.ReadOutputAs<DurableTaskSequentialProcessResult>());
        var firstSignal = Assert.IsType<SignalEnvelope>(Assert.Single(firstSignalResult.Emissions));
        Assert.Equal(signalFixture.Contract, firstSignal.Contract);
        var signalReceiverCompleted = await firstClient.GetInstanceAsync(
            signalReceiverSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        for (var attempt = 0;
            attempt < 100 && signalReceiverCompleted?.RuntimeStatus == OrchestrationRuntimeStatus.Running;
            attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            signalReceiverCompleted = await firstClient.GetInstanceAsync(
                signalReceiverSchedule.InstanceId,
                getInputsAndOutputs: true,
                timeout.Token);
        }
        Assert.True(
            signalReceiverCompleted?.RuntimeStatus == OrchestrationRuntimeStatus.Completed,
            $"Signal receiver status: {signalReceiverCompleted?.RuntimeStatus}; "
            + $"custom status: {signalReceiverCompleted?.SerializedCustomStatus}; "
            + $"failure: {signalReceiverCompleted?.FailureDetails}.");
        Assert.NotNull(signalReceiverCompleted);
        var signalReceiverResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            signalReceiverCompleted.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(ProcessActivationDisposition.Completed, signalReceiverResult.Disposition);
        Assert.Equal(1, signalReceiverResult.InputAdmissions.Count(static admission =>
            admission.Disposition == ProcessInputAdmissionDisposition.Consumed));
        Assert.Contains(signalReceiverResult.InputAdmissions, admission =>
            admission.Input.Envelope == firstSignal);
        var firstSignalReplay = await firstClient.ScheduleCohesiveProcessAsync(firstSignalStart, timeout.Token);
        Assert.True(firstSignalReplay.Replayed);
        var signalReceiverAfterReplay = await firstClient.GetInstanceAsync(
            signalReceiverSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.Equal(OrchestrationRuntimeStatus.Completed, signalReceiverAfterReplay?.RuntimeStatus);
        var signalReceiverReplayResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            signalReceiverAfterReplay!.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(
            signalReceiverResult.InputAdmissions.Select(Serialize),
            signalReceiverReplayResult.InputAdmissions.Select(Serialize));

        const string selfSignalInstance = "instance/scheduler-self-signal";
        var selfSignalStart = Start(
            selfSignalFixture.Plan,
            "self",
            selfSignalInstance);
        var selfSignalInitial = ProcessReferenceInterpreter.Create(
            selfSignalFixture.Plan,
            selfSignalStart.Receipt);
        var selfSignalRegistered = ProcessReferenceInterpreter.Activate(
            selfSignalFixture.Plan,
            selfSignalInitial,
            Activation(selfSignalInitial, ProcessActivationCause.Start, selfSignalStart),
            RejectingHost.Instance);
        var selfSignalWait = Assert.Single(selfSignalRegistered.State.Waits, static wait =>
            wait.Active && wait.Kind == ProcessWaitKind.AwaitMatch);
        operations.RegisterSignalTarget(
            "route/self",
            new(selfSignalRegistered.State.Continuation, selfSignalWait.Token));
        var selfSignalSchedule = await firstClient.ScheduleCohesiveProcessAsync(selfSignalStart, timeout.Token);
        var selfSignalCompleted = await firstClient.WaitForInstanceCompletionAsync(
            selfSignalSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.True(
            selfSignalCompleted.RuntimeStatus == OrchestrationRuntimeStatus.Completed,
            selfSignalCompleted.FailureDetails?.ToString());
        var selfSignalResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            selfSignalCompleted.ReadOutputAs<DurableTaskSequentialProcessResult>());
        var selfSignal = Assert.IsType<SignalEnvelope>(Assert.Single(selfSignalResult.Emissions));
        Assert.Equal(selfSignalFixture.Contract, selfSignal.Contract);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            Assert.Single(selfSignalResult.InputAdmissions).Disposition);
        Assert.Equal(2, Assert.Single(selfSignalResult.State.Forks).SelectedBranches.Length);

        var durableStart = Start(
            durableRequestPlan,
            "durable-request",
            "instance/durable-request");
        var durableSchedule = await firstClient.ScheduleCohesiveProcessAsync(durableStart, timeout.Token);
        var durableCompleted = await firstClient.WaitForInstanceCompletionAsync(
            durableSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.Equal(OrchestrationRuntimeStatus.Completed, durableCompleted.RuntimeStatus);
        var durableResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            durableCompleted.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(
            DurableOperationStatus.Dispositioned,
            Assert.Single(durableResult.DurableOperations).State.Status);
        Assert.Single(durableOperations.Invocations);

        var childStart = Start(childFixture.Parent, "child", "instance/scheduler-child-parent");
        var childSchedule = await firstClient.ScheduleCohesiveProcessAsync(childStart, timeout.Token);
        var childCompleted = await firstClient.WaitForInstanceCompletionAsync(
            childSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.True(
            childCompleted.RuntimeStatus == OrchestrationRuntimeStatus.Completed,
            childCompleted.FailureDetails?.ToString());
        var childResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            childCompleted.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(ProcessActivationDisposition.Completed, childResult.Disposition);
        Assert.Equal(ProcessChildDisposition.Completed, Assert.Single(childResult.State.Children).Disposition);
        Assert.Equal(
            DurableOperationStatus.Dispositioned,
            Assert.Single(childResult.DurableOperations).State.Status);
        Assert.Single(durableOperations.Invocations);

        var forkChildStart = Start(
            forkChildFixture.Parent,
            "fork-child",
            "instance/scheduler-fork-child-parent");
        var forkChildSchedule = await firstClient.ScheduleCohesiveProcessAsync(forkChildStart, timeout.Token);
        var forkChildCompleted = await firstClient.WaitForInstanceCompletionAsync(
            forkChildSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.True(
            forkChildCompleted.RuntimeStatus == OrchestrationRuntimeStatus.Completed,
            forkChildCompleted.FailureDetails?.ToString());
        var forkChildResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            forkChildCompleted.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(ProcessActivationDisposition.Completed, forkChildResult.Disposition);
        Assert.Equal(2, forkChildResult.DurableOperations.Length);
        Assert.Contains(forkChildResult.State.Children, static child =>
            child.Disposition == ProcessChildDisposition.Completed);
        var cancelledChild = Assert.Single(forkChildResult.State.Children, static child =>
            child.Disposition == ProcessChildDisposition.CancellationRequested);
        var cancelledChildInstance = await firstClient.GetInstanceAsync(
            DurableTaskSequentialProcessIdentities.OrchestrationInstance(
                forkChildStart.ActivationContext.AuthorityScope,
                cancelledChild.Continuation.ProcessInstanceId),
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.Equal(OrchestrationRuntimeStatus.Completed, cancelledChildInstance?.RuntimeStatus);
        Assert.Equal(
            ProcessActivationDisposition.Cancelled,
            cancelledChildInstance?.ReadOutputAs<DurableTaskSequentialProcessResult>()?.Disposition);

        var recurrenceStart = Start(recurrencePlan, "recurrence", "instance/scheduler-recurrence");
        var recurrenceSchedule = await firstClient.ScheduleCohesiveProcessAsync(recurrenceStart, timeout.Token);
        var recurrenceCompleted = await firstClient.WaitForInstanceCompletionAsync(
            recurrenceSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.True(
            recurrenceCompleted.RuntimeStatus == OrchestrationRuntimeStatus.Completed,
            recurrenceCompleted.FailureDetails?.ToString());
        var recurrenceResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            recurrenceCompleted.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(3, recurrenceResult.State.CompletedActivationCount);
        Assert.Equal(StringValue("exhausted"), recurrenceResult.State.Terminal.Detail?.Value);
        var recurrenceDuplicate = await firstClient.ScheduleCohesiveProcessAsync(recurrenceStart, timeout.Token);
        Assert.True(recurrenceDuplicate.Replayed);

        var timerDueAtUtc = DateTimeOffset.UtcNow.AddSeconds(5);
        var timerStart = Start(
            timerPlan,
            InstantValue(timerDueAtUtc),
            "instance/scheduler-timer-restart");
        var timerSchedule = await firstClient.ScheduleCohesiveProcessAsync(timerStart, timeout.Token);
        var timerWaiting = await WaitForActiveWait(
            firstClient,
            timerSchedule.InstanceId,
            ProcessWaitKind.Timer,
            timeout.Token);
        Assert.Equal(
            timerDueAtUtc,
            Assert.Single(timerWaiting.Runtime.Waits).DeadlineUtc);

        var awaitMatchDueAtUtc = DateTimeOffset.UtcNow.AddMinutes(1);
        var awaitMatchStart = Start(
            awaitMatchFixture.Plan,
            InstantValue(awaitMatchDueAtUtc),
            "instance/scheduler-await-match-restart");
        var awaitMatchSchedule = await firstClient.ScheduleCohesiveProcessAsync(
            awaitMatchStart,
            timeout.Token);
        var awaitMatchWaiting = await WaitForActiveWait(
            firstClient,
            awaitMatchSchedule.InstanceId,
            ProcessWaitKind.AwaitMatch,
            timeout.Token);

        var restartStart = Start(restartPlan, "restart", "instance/restart");
        var restartSchedule = await firstClient.ScheduleCohesiveProcessAsync(restartStart, timeout.Token);
        var waiting = await WaitForOutstandingRequest(
            firstClient,
            restartSchedule.InstanceId,
            timeout.Token);
        var firstInvocation = Assert.Single(operations.Transitions);
        Assert.Equal(restartPlan.DefinitionReference, firstInvocation.Process);
        Assert.Equal(transition, firstInvocation.Definition);

        await firstWorker.StopAsync(timeout.Token);

        using var recoveredWorker = SchedulerHost(connectionString, catalog, operations, durableOperations);
        await recoveredWorker.StartAsync(timeout.Token);
        var recoveredClient = recoveredWorker.Services.GetRequiredService<DurableTaskClient>();
        var waitingContinuation = new ProcessContinuationIdentity(
            waiting.ProcessInstanceId,
            waiting.CurrentAttemptId);
        var waitingToken = Assert.Single(waiting.Runtime.Tokens).TokenId;
        var canonicalInitial = ProcessReferenceInterpreter.Create(restartPlan, restartStart.Receipt);
        var canonicalWaiting = ProcessReferenceInterpreter.Activate(
            restartPlan,
            canonicalInitial,
            Activation(canonicalInitial, ProcessActivationCause.Start, restartStart),
            new EchoHost());
        var requested = Assert.IsType<RequestEnvelope>(Assert.Single(canonicalWaiting.Emissions));
        var reply = new ReplyEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(
                restartPlan,
                waitingContinuation,
                waitingToken,
                "emission/restart-reply",
                requested.Context.EmissionId),
            replyContract,
            requested.Context.EmissionId,
            new RequestResultOutcome(new("accepted"), StringValue("accepted")));
        await recoveredClient.RaiseCohesiveProcessInteractionAsync(
            restartStart,
            new(
                new(waitingContinuation, waitingToken),
                reply),
            timeout.Token);
        var recovered = await recoveredClient.WaitForInstanceCompletionAsync(
            restartSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.Equal(OrchestrationRuntimeStatus.Completed, recovered.RuntimeStatus);
        var recoveredResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            recovered.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(ProcessActivationDisposition.Completed, recoveredResult.Disposition);
        Assert.Equal(StringValue("accepted"), recoveredResult.State.Terminal.Detail?.Value);
        Assert.All(
            recoveredResult.Evidence,
            item => Assert.Equal(restartPlan.DefinitionReference, item.Definition));
        Assert.Single(operations.Transitions);

        var awaitMatchInput = AwaitMatchInput(
            awaitMatchFixture,
            awaitMatchWaiting,
            "emission/scheduler-await-match");
        await recoveredClient.RaiseCohesiveProcessInteractionAsync(
            awaitMatchStart,
            awaitMatchInput,
            timeout.Token);
        var awaitMatchRecovered = await recoveredClient.WaitForInstanceCompletionAsync(
            awaitMatchSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.Equal(OrchestrationRuntimeStatus.Completed, awaitMatchRecovered.RuntimeStatus);
        var awaitMatchResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            awaitMatchRecovered.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(ProcessActivationDisposition.Completed, awaitMatchResult.Disposition);
        Assert.Equal(
            new ExecutionNodeId("clause/interaction"),
            Assert.Single(awaitMatchResult.State.Waits).WinnerClause);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            Assert.Single(awaitMatchResult.InputAdmissions).Disposition);

        var awaitMatchTimerDueAtUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        var awaitMatchTimerStart = Start(
            awaitMatchFixture.Plan,
            InstantValue(awaitMatchTimerDueAtUtc),
            "instance/scheduler-await-match-timer");
        var awaitMatchTimerSchedule = await recoveredClient.ScheduleCohesiveProcessAsync(
            awaitMatchTimerStart,
            timeout.Token);
        var awaitMatchTimerCompleted = await recoveredClient.WaitForInstanceCompletionAsync(
            awaitMatchTimerSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.Equal(OrchestrationRuntimeStatus.Completed, awaitMatchTimerCompleted.RuntimeStatus);
        var awaitMatchTimerResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            awaitMatchTimerCompleted.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(
            new ExecutionNodeId("clause/timer"),
            Assert.Single(awaitMatchTimerResult.State.Waits).WinnerClause);
        Assert.Empty(awaitMatchTimerResult.InputAdmissions);

        var timerRecovered = await recoveredClient.WaitForInstanceCompletionAsync(
            timerSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.Equal(OrchestrationRuntimeStatus.Completed, timerRecovered.RuntimeStatus);
        var timerResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            timerRecovered.ReadOutputAs<DurableTaskSequentialProcessResult>());
        Assert.Equal(ProcessActivationDisposition.Completed, timerResult.Disposition);
        var timerWait = Assert.Single(timerResult.State.Waits);
        Assert.False(timerWait.Active);
        Assert.Equal(timerDueAtUtc, Assert.Single(timerWait.Timers).DueAtUtc);
        Assert.Equal(
            [ProcessActivationCause.Start, ProcessActivationCause.Timer],
            timerResult.Evidence.Select(static evidence => evidence.Cause));

        var failedStart = Start(failedPlan, "failed", "instance/failed");
        var failedSchedule = await recoveredClient.ScheduleCohesiveProcessAsync(failedStart, timeout.Token);
        var failed = await recoveredClient.WaitForInstanceCompletionAsync(
            failedSchedule.InstanceId,
            getInputsAndOutputs: true,
            timeout.Token);
        Assert.Equal(OrchestrationRuntimeStatus.Failed, failed.RuntimeStatus);
        Assert.Contains(nameof(DurableTaskProcessFailedException), failed.FailureDetails?.ErrorType);

        await recoveredWorker.StopAsync(timeout.Token);
    }

    static Microsoft.Extensions.Hosting.IHost SchedulerHost(
        string connectionString,
        DurableTaskSequentialProcessPlanCatalog catalog,
        IProcessReferenceHost processHost,
        IDurableOperationAdapter? durableOperationAdapter = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(processHost);
        if (durableOperationAdapter is not null)
        {
            builder.Services.AddSingleton<IDurableOperationAdapterResolver>(
                new AdapterResolver(durableOperationAdapter));
        }
        builder.Services.AddDurableTaskWorker(worker =>
        {
            worker.AddCohesiveSequentialProcesses(catalog);
            worker.UseDurableTaskScheduler(connectionString);
        });
        builder.Services.AddDurableTaskClient(client => client.UseDurableTaskScheduler(connectionString));
        return builder.Build();
    }

    static async Task<ExecutionStatus> WaitForOutstandingRequest(
        DurableTaskClient client,
        string instanceId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var instance = await client.GetInstanceAsync(
                instanceId,
                getInputsAndOutputs: true,
                cancellationToken);
            var status = instance?.ReadCustomStatusAs<ExecutionStatus>();
            if (status is not null && !status.Runtime.Waits.IsEmpty)
            {
                return status;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        var retained = await client.GetInstanceAsync(
            instanceId,
            getInputsAndOutputs: true,
            cancellationToken);
        throw new InvalidOperationException(
            $"Durable Task instance '{instanceId}' did not expose a canonical outstanding Request. "
            + $"Runtime status: {retained?.RuntimeStatus}; custom status: {retained?.SerializedCustomStatus}; "
            + $"failure: {retained?.FailureDetails}.");
    }

    static async Task<ExecutionStatus> WaitForActiveWait(
        DurableTaskClient client,
        string instanceId,
        ProcessWaitKind kind,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var instance = await client.GetInstanceAsync(
                instanceId,
                getInputsAndOutputs: true,
                cancellationToken);
            var status = instance?.ReadCustomStatusAs<ExecutionStatus>();
            if (status is not null && !status.Runtime.Waits.IsEmpty)
            {
                return status;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        var retained = await client.GetInstanceAsync(
            instanceId,
            getInputsAndOutputs: true,
            cancellationToken);
        throw new InvalidOperationException(
            $"Durable Task instance '{instanceId}' did not expose a canonical active {kind} wait. "
            + $"Runtime status: {retained?.RuntimeStatus}; custom status: {retained?.SerializedCustomStatus}; "
            + $"failure: {retained?.FailureDetails}.");
    }

    static async Task<ExecutionStatus> WaitForControlStatus(
        DurableTaskClient client,
        string instanceId,
        Func<ExecutionStatus, bool> predicate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var instance = await client.GetInstanceAsync(
                instanceId,
                getInputsAndOutputs: true,
                cancellationToken);
            var status = instance?.ReadCustomStatusAs<ExecutionStatus>();
            if (status is not null && predicate(status))
            {
                return status;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        var retained = await client.GetInstanceAsync(
            instanceId,
            getInputsAndOutputs: true,
            cancellationToken);
        throw new InvalidOperationException(
            $"Durable Task instance '{instanceId}' did not expose the expected canonical control status. "
            + $"Runtime status: {retained?.RuntimeStatus}; custom status: {retained?.SerializedCustomStatus}; "
            + $"failure: {retained?.FailureDetails}.");
    }

    static Task<DurableTaskSequentialProcessResult> Run(
        CompiledProcessPlan plan,
        DurableTaskSequentialProcessStart start,
        Func<DurableTaskProcessHostOperation, Task<ProcessOperationResult>> executeOperation) =>
        DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            executeOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            TestDurableTimer,
            () => StartedAtUtc.AddMinutes(1));

    static Task<DurableTaskSequentialProcessResult> RunDurableRequest(
        CompiledProcessPlan plan,
        DurableTaskSequentialProcessStart start,
        DurableRequestBinding binding,
        Func<DurableOperationInvocation, DurableTaskDurableOperationAttemptResult> execute,
        Func<DurableOperationState, DurableTaskDurableOperationReconciliationResult>? reconcile = null) =>
        DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            new BindingResolver(binding),
            UnexpectedOperation,
            invocation => Task.FromResult(execute(invocation)),
            UnexpectedChildProcess,
            operation => Task.FromResult((reconcile ?? (static state =>
                throw new InvalidOperationException("Unexpected durable Request reconciliation.")))(operation)),
            UnexpectedInteraction,
            (delay, cancellationToken) => Task.CompletedTask,
            () => StartedAtUtc);

    static Task<DurableTaskSequentialProcessResult> RunBoundRequests(
        CompiledProcessPlan plan,
        DurableTaskSequentialProcessStart start,
        DurableRequestBinding binding,
        Func<DurableOperationInvocation, Task<DurableTaskDurableOperationAttemptResult>> execute) =>
        DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            new BindingResolver(binding),
            UnexpectedOperation,
            execute,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            TestDurableTimer,
            () => StartedAtUtc.AddMinutes(1));

    static Task<DurableTaskSequentialProcessResult> RunChildRequest(
        CompiledProcessPlan plan,
        DurableTaskSequentialProcessStart start,
        DurableRequestBinding binding,
        Func<DurableOperationInvocation, Task<DurableTaskDurableOperationAttemptResult>> execute) =>
        DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            new BindingResolver(binding),
            UnexpectedOperation,
            UnexpectedDurableOperation,
            execute,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            TestDurableTimer,
            () => StartedAtUtc.AddMinutes(1));

    static Task TestDurableTimer(TimeSpan delay, CancellationToken cancellationToken) =>
        delay == TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    static DurableRequestBinding Binding(
        CompiledProcessPlan plan,
        ReplyContractReference reply,
        int maxAttempts = 2,
        DurableOperationIdempotencyEvidence idempotencyEvidence =
            DurableOperationIdempotencyEvidence.TargetDeduplication)
    {
        var request = Assert.IsType<RequestProcessNode>(plan.GetNode(new("request"))).Contract;
        return new(
            request,
            [new(new("accepted"), reply)],
            maxAttempts,
            TimeSpan.FromMinutes(5),
            timeoutAfter: null,
            idempotencyEvidence,
            reconciliationTarget: new(
                DefinitionReference("process/reconcile", '7'),
                new("node/reconcile")),
            escalationTarget: new(
                DefinitionReference("process/escalate", '8'),
                new("node/escalate")));
    }

    static Task<ProcessOperationResult> UnexpectedOperation(DurableTaskProcessHostOperation operation) =>
        throw new InvalidOperationException($"Unexpected host operation '{operation.Kind}'.");

    static Task<DurableTaskDurableOperationAttemptResult> UnexpectedDurableOperation(
        DurableOperationInvocation invocation) =>
        throw new InvalidOperationException(
            $"Unexpected durable operation '{invocation.Request.Context.EmissionId.Value}'.");

    static Task<DurableTaskDurableOperationAttemptResult> UnexpectedChildProcess(
        DurableOperationInvocation invocation) =>
        throw new InvalidOperationException(
            $"Unexpected child Process '{invocation.Request.Context.EmissionId.Value}'.");

    static Task<DurableTaskDurableOperationReconciliationResult> UnexpectedReconciliation(
        DurableOperationState operation) =>
        throw new InvalidOperationException($"Unexpected reconciliation '{operation.OperationId.Value}'.");

    static Task<ProcessActivationInput> UnexpectedInteraction() =>
        throw new InvalidOperationException("Unexpected Process interaction wait.");

    static (CompiledProcessPlan Plan, ReplyContractReference Reply) CompileRequestPlan(
        string definitionId,
        ExecutionDefinitionReference? transition = null)
    {
        var requestDocument = InteractionDocument(
            $"interaction/request/{definitionId}",
            new RequestContractDefinition(
                new(StringContract, new("request/v1")),
                new RequestResponseObligation(
                    [new RequestResultDefinition(new("accepted"), new(StringContract, new("accepted/v1")))],
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestResultDisposition.Observe,
                    RequestResultDisposition.Reject,
                    RequestResultDisposition.ReusePriorDisposition,
                    RequestRetrySemantics.StableIdentity,
                    RequestResolutionSemantics.Reconcile,
                    RequestResolutionSemantics.Escalate,
                    TimeSpan.FromDays(30))));
        RequestContractReference requestContract = new(Reference(requestDocument));
        var replyDocument = InteractionDocument(
            $"interaction/reply/{definitionId}",
            new ReplyContractDefinition(requestContract, new("accepted")));
        ReplyContractReference replyContract = new(Reference(replyDocument));
        ImmutableArray<ProcessNode> nodes = transition is null
            ? [
                RequestNode(requestContract),
                new ReturnProcessNode(new("return"), Expr.BoundValue(new("request.accepted")))
            ]
            : [
                new InvokeTransitionProcessNode(
                    new("transition"),
                    transition,
                    Expr.Const("subject/restart"),
                    Expr.BoundValue(ProcessBindingIds.Input),
                    new(Edge("edge/transition-request", "request"))),
                RequestNode(requestContract),
                new ReturnProcessNode(new("return"), Expr.BoundValue(new("request.accepted")))
            ];
        ImmutableArray<ProcessDefinitionLink> definitions = transition is null
            ? []
            : [new(transition, ProcessDefinitionLinkKind.Transition, StringContract, StringContract)];
        return (
            Compile(
                Definition(transition is null ? "request" : "transition", nodes),
                Catalog(requestDocument, replyDocument),
                definitions,
                definitionId),
            replyContract);
    }

    static RequestProcessNode RequestNode(RequestContractReference requestContract) => new(
        new("request"),
        requestContract,
        Expr.BoundValue(ProcessBindingIds.Input),
        [new(
            new("outcome/accepted"),
            new("accepted"),
            new(
                Edge("edge/accepted-return", "return"),
                new(new("request.accepted"), StringContract)))]);

    static ForkRequestFixture CompileForkRequestPlan()
    {
        var interactions = RequestContracts("fork", "accepted");
        var plan = Compile(
            Definition(
                "fork",
                [
                    new ForkProcessNode(
                        new("fork"),
                        [
                            new(new("branch/a"), Edge("edge/fork-a", "request/a")),
                            new(new("branch/b"), Edge("edge/fork-b", "request/b"))
                        ],
                        new("join")),
                    ForkRequest("request/a", interactions.Request, "edge/request-a-join"),
                    ForkRequest("request/b", interactions.Request, "edge/request-b-join"),
                    new JoinProcessNode(
                        new("join"),
                        new("fork"),
                        new(
                            ProcessJoinMode.All,
                            requiredCount: 0,
                            ProcessJoinFailurePolicy.FailFast,
                            ProcessJoinCancellationPolicy.AwaitRemaining,
                            ProcessJoinCompletionOrder.Unobservable,
                            ProcessJoinTieBreak.BranchIdentity),
                        Edge("edge/join-return", "return")),
                    new ReturnProcessNode(new("return"), Expr.Const("joined"))
                ]),
            interactions.Catalog,
            definitionId: "process/durable-task-fork-join");
        return new(plan, interactions.Binding);
    }

    static CompiledProcessPlan CompileRecurrencePlan() => Compile(
        Definition(
            "repeat",
            [
                new RepeatAcrossActivationProcessNode(
                    new("repeat"),
                    Expr.Const(true),
                    Expr.Const("unchanged"),
                    StringContract,
                    new(maximumOccurrences: 2, maximumUnchangedProgressOccurrences: 1),
                    Edge("edge/repeat", "repeat"),
                    Edge("edge/completed", "completed"),
                    Edge("edge/exhausted", "exhausted"),
                    Edge("edge/stalled", "stalled")),
                new ReturnProcessNode(new("completed"), Expr.Const("completed")),
                new ReturnProcessNode(new("exhausted"), Expr.Const("exhausted")),
                new FailProcessNode(new("stalled"), Expr.Const("stalled"))
            ]),
        definitionId: "process/durable-task-recurrence");

    static CompiledProcessPlan CompileTimerPlan(DateTimeOffset dueAtUtc, string definitionId) => Compile(
        Definition(
            "timer",
            [
                new TimerProcessNode(
                    new("timer"),
                    Expr.Const(ObservationValue.FromDateTimeOffset(dueAtUtc)),
                    Edge("edge/timer-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]),
        definitionId: definitionId);

    static CompiledProcessPlan CompileInputTimerPlan(
        string definitionId = "process/durable-task-scheduler-timer",
        ProcessRecoveryPolicy recoveryPolicy = ProcessRecoveryPolicy.ContinueAttempt) => Compile(
        Definition(
            InstantContract,
            "timer",
            [
                new TimerProcessNode(
                    new("timer"),
                    Expr.BoundValue(ProcessBindingIds.Input),
                    Edge("edge/timer-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ],
            recoveryPolicy),
        definitionId: definitionId);

    static CompiledProcessPlan CompileForkTimerPlan(
        DateTimeOffset firstDueAtUtc,
        DateTimeOffset secondDueAtUtc) => Compile(
        Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/a"), Edge("edge/fork-a", "timer/a")),
                        new(new("branch/b"), Edge("edge/fork-b", "cut/b"))
                    ],
                    new("join")),
                new TimerProcessNode(
                    new("timer/a"),
                    Expr.Const(ObservationValue.FromDateTimeOffset(firstDueAtUtc)),
                    Edge("edge/timer-a-join", "join")),
                new DurableCutProcessNode(
                    new("cut/b"),
                    Edge("edge/cut-b-timer", "timer/b")),
                new TimerProcessNode(
                    new("timer/b"),
                    Expr.Const(ObservationValue.FromDateTimeOffset(secondDueAtUtc)),
                    Edge("edge/timer-b-join", "join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    new(
                        ProcessJoinMode.Any,
                        requiredCount: 0,
                        ProcessJoinFailurePolicy.FailFast,
                        ProcessJoinCancellationPolicy.CancelRemaining,
                        ProcessJoinCompletionOrder.Unobservable,
                        ProcessJoinTieBreak.BranchIdentity),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]),
        definitionId: "process/durable-task-fork-timer");

    static AwaitMatchFixture CompileAwaitMatchPlan(
        int interactionPriority,
        int timerPriority,
        string definitionId)
    {
        var eventDocument = InteractionDocument(
            $"interaction/event/{definitionId}",
            new DomainEventContractDefinition(new(StringContract, new("await-match-event/v1"))));
        var alternateEventDocument = InteractionDocument(
            $"interaction/event/{definitionId}/alternate",
            new DomainEventContractDefinition(new(StringContract, new("await-match-event/v1"))));
        DomainEventContractReference eventContract = new(Reference(eventDocument));
        DomainEventContractReference alternateEventContract = new(Reference(alternateEventDocument));
        var plan = Compile(
            Definition(
                InstantContract,
                "await",
                [
                    new AwaitMatchProcessNode(
                        new("await"),
                        ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                        [
                            new ProcessAwaitInteractionClause(
                                new("clause/interaction"),
                                eventContract,
                                new(new("await.interaction"), StringContract),
                                requestObligation: null,
                                guard: null,
                                interactionPriority,
                                new(Edge("edge/interaction-return", "return/interaction"))),
                            new ProcessAwaitTimerClause(
                                new("clause/timer"),
                                Expr.BoundValue(ProcessBindingIds.Input),
                                timerPriority,
                                new(Edge("edge/timer-return", "return/timer")))
                        ],
                        ProcessAwaitInputDisposition.Observe,
                        ProcessAwaitInputDisposition.Reject,
                        ProcessAwaitInputDisposition.ReusePriorDisposition,
                        ProcessAwaitMissingTargetDisposition.DeadLetter,
                        TimeSpan.FromDays(1)),
                    new ReturnProcessNode(new("return/interaction"), Expr.Const("interaction")),
                    new ReturnProcessNode(new("return/timer"), Expr.Const("timer"))
                ]),
            Catalog(eventDocument, alternateEventDocument),
            definitionId: definitionId);
        return new(plan, eventContract, alternateEventContract);
    }

    static SignalFixture CompileSignalFixture(string definitionId, bool receiveTwice = true)
    {
        var signalDocument = InteractionDocument(
            $"interaction/signal/{definitionId}",
            new SignalContractDefinition(new(StringContract, new("signal-payload/v1"))));
        SignalContractReference signalContract = new(Reference(signalDocument));
        var contracts = Catalog(signalDocument);
        var sender = Compile(
            Definition(
                "signal",
                [
                    new SendSignalProcessNode(
                        new("signal"),
                        signalContract,
                        Expr.Const("route/process"),
                        Expr.BoundValue(ProcessBindingIds.Input),
                        Edge("edge/signal-return", "return")),
                    new ReturnProcessNode(new("return"), Expr.Const("sent"))
                ]),
            contracts,
            definitionId: $"{definitionId}/sender");
        ImmutableArray<ProcessNode> receiverNodes = receiveTwice
            ? [
                AwaitSignal("await/one", "one", "cut"),
                new DurableCutProcessNode(new("cut"), Edge("edge/cut-await-two", "await/two")),
                AwaitSignal("await/two", "two", "return"),
                new ReturnProcessNode(new("return"), Expr.Const("received"))
            ]
            : [
                AwaitSignal("await/one", "one", "return"),
                new ReturnProcessNode(new("return"), Expr.Const("received"))
            ];
        var receiver = Compile(
            Definition(
                "await/one",
                receiverNodes),
            contracts,
            definitionId: $"{definitionId}/receiver");
        return new(sender, receiver, signalContract);

        AwaitMatchProcessNode AwaitSignal(string node, string suffix, string next) => new(
            new(node),
            ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
            [new ProcessAwaitInteractionClause(
                new($"clause/{suffix}"),
                signalContract,
                new(new($"signal.{suffix}"), StringContract),
                requestObligation: null,
                guard: null,
                priority: 0,
                new(Edge($"edge/{node}-{next}", next)))],
            ProcessAwaitInputDisposition.Reject,
            ProcessAwaitInputDisposition.Reject,
            ProcessAwaitInputDisposition.ReusePriorDisposition,
            ProcessAwaitMissingTargetDisposition.Observe,
            TimeSpan.FromDays(1));
    }

    static SelfSignalFixture CompileSelfSignalFixture(string definitionId)
    {
        var signalDocument = InteractionDocument(
            $"interaction/signal/{definitionId}",
            new SignalContractDefinition(new(StringContract, new("self-signal-payload/v1"))));
        SignalContractReference signalContract = new(Reference(signalDocument));
        var plan = Compile(
            Definition(
                "fork",
                [
                    new ForkProcessNode(
                        new("fork"),
                        [
                            new(new("branch/wait"), Edge("edge/fork-wait", "await")),
                            new(new("branch/signal"), Edge("edge/fork-signal", "cut/signal"))
                        ],
                        new("join")),
                    new AwaitMatchProcessNode(
                        new("await"),
                        ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                        [new ProcessAwaitInteractionClause(
                            new("clause/signal"),
                            signalContract,
                            new(new("signal.input"), StringContract),
                            requestObligation: null,
                            guard: null,
                            priority: 0,
                            new(Edge("edge/await-join", "join")))],
                        ProcessAwaitInputDisposition.Reject,
                        ProcessAwaitInputDisposition.Reject,
                        ProcessAwaitInputDisposition.ReusePriorDisposition,
                        ProcessAwaitMissingTargetDisposition.Observe,
                        TimeSpan.FromDays(1)),
                    new DurableCutProcessNode(
                        new("cut/signal"),
                        Edge("edge/cut-signal", "signal")),
                    new SendSignalProcessNode(
                        new("signal"),
                        signalContract,
                        Expr.Const("route/self"),
                        Expr.BoundValue(ProcessBindingIds.Input),
                        Edge("edge/signal-join", "join")),
                    new JoinProcessNode(
                        new("join"),
                        new("fork"),
                        new(
                            ProcessJoinMode.All,
                            requiredCount: 0,
                            ProcessJoinFailurePolicy.FailFast,
                            ProcessJoinCancellationPolicy.AwaitRemaining,
                            ProcessJoinCompletionOrder.Unobservable,
                            ProcessJoinTieBreak.BranchIdentity),
                        Edge("edge/join-return", "return")),
                    new ReturnProcessNode(new("return"), Expr.Const("self-signalled"))
                ]),
            Catalog(signalDocument),
            definitionId: definitionId);
        return new(plan, signalContract);
    }

    static CompiledProcessPlan CompileAwaitMatchTimerPlan(DateTimeOffset dueAtUtc) => Compile(
        Definition(
            "await",
            [
                new AwaitMatchProcessNode(
                    new("await"),
                    ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                    [
                        new ProcessAwaitTimerClause(
                            new("clause/low"),
                            Expr.Const(ObservationValue.FromDateTimeOffset(dueAtUtc)),
                            0,
                            new(Edge("edge/low-return", "return/low"))),
                        new ProcessAwaitTimerClause(
                            new("clause/high"),
                            Expr.Const(ObservationValue.FromDateTimeOffset(dueAtUtc)),
                            10,
                            new(Edge("edge/high-return", "return/high")))
                    ],
                    ProcessAwaitInputDisposition.Observe,
                    ProcessAwaitInputDisposition.Reject,
                    ProcessAwaitInputDisposition.ReusePriorDisposition,
                    ProcessAwaitMissingTargetDisposition.DeadLetter,
                    TimeSpan.FromDays(1)),
                new ReturnProcessNode(new("return/low"), Expr.Const("low")),
                new ReturnProcessNode(new("return/high"), Expr.Const("high"))
            ]),
        definitionId: "process/durable-task-await-match-timers");

    static RequestProcessNode ForkRequest(
        string id,
        RequestContractReference request,
        string edge) => new(
        new(id),
        request,
        Expr.BoundValue(ProcessBindingIds.Input),
        [new(
            new($"outcome/{id}"),
            new("accepted"),
            new(Edge(edge, "join")))]);

    static ChildProcessFixture CompileChildParentPlan()
    {
        var child = Compile(
            Definition("return", [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))]),
            definitionId: "process/durable-task-child");
        var interactions = RequestContracts(
            "child",
            "completed",
            "failed",
            "cancelled",
            "terminated");
        var parent = Compile(
            Definition(
                "child",
                [
                    new InvokeProcessProcessNode(
                        new("child"),
                        child.DefinitionReference,
                        interactions.Request,
                        ChildOutcomeMapping,
                        Expr.BoundValue(ProcessBindingIds.Input),
                        ProcessChildPurpose.Work,
                        ProcessChildCancellationPolicy.Propagate,
                        ChildOutcomes()),
                    new ReturnProcessNode(new("completed"), Expr.Const("parent-completed")),
                    new FailProcessNode(new("failed"), Expr.Const("child-failed")),
                    new FailProcessNode(new("cancelled"), Expr.Const("child-cancelled")),
                    new FailProcessNode(new("terminated"), Expr.Const("child-terminated"))
                ]),
            interactions.Catalog,
            [new(
                child.DefinitionReference,
                ProcessDefinitionLinkKind.Process,
                child.Definition.Input,
                child.Definition.Result,
                [],
                child.Definition.RecoveryPolicy)],
            "process/durable-task-child-parent");
        return new(parent, child, interactions.Binding);
    }

    static ChildProcessFixture CompileForkChildPlan(ProcessChildCancellationPolicy cancellation)
    {
        var child = Compile(
            Definition("return", [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))]),
            definitionId: $"process/durable-task-fork-child-{cancellation.ToString().ToLowerInvariant()}");
        var interactions = RequestContracts(
            $"fork-child-{cancellation.ToString().ToLowerInvariant()}",
            "completed",
            "failed",
            "cancelled",
            "terminated");
        var childLink = new ProcessDefinitionLink(
            child.DefinitionReference,
            ProcessDefinitionLinkKind.Process,
            child.Definition.Input,
            child.Definition.Result,
            [],
            child.Definition.RecoveryPolicy);
        var parent = Compile(
            Definition(
                "fork",
                [
                    new ForkProcessNode(
                        new("fork"),
                        [
                            new(new("branch/a"), Edge("edge/fork-a", "child/a")),
                            new(new("branch/b"), Edge("edge/fork-b", "child/b"))
                        ],
                        new("join")),
                    ForkChild("child/a"),
                    ForkChild("child/b"),
                    new JoinProcessNode(
                        new("join"),
                        new("fork"),
                        new(
                            ProcessJoinMode.Any,
                            requiredCount: 0,
                            ProcessJoinFailurePolicy.FailFast,
                            ProcessJoinCancellationPolicy.CancelRemaining,
                            ProcessJoinCompletionOrder.Unobservable,
                            ProcessJoinTieBreak.BranchIdentity),
                        Edge("edge/join-return", "return")),
                    new ReturnProcessNode(new("return"), Expr.Const("joined"))
                ]),
            interactions.Catalog,
            [childLink],
            $"process/durable-task-fork-child-parent-{cancellation.ToString().ToLowerInvariant()}");
        return new(parent, child, interactions.Binding);

        InvokeProcessProcessNode ForkChild(string id) => new(
            new(id),
            child.DefinitionReference,
            interactions.Request,
            ChildOutcomeMapping,
            Expr.BoundValue(ProcessBindingIds.Input),
            ProcessChildPurpose.Work,
            cancellation,
            [
                new(new($"outcome/{id}/completed"), ChildOutcomeMapping.Completed, new(Edge($"edge/{id}/join", "join"))),
                new(new($"outcome/{id}/failed"), ChildOutcomeMapping.Failed, new(Edge($"edge/{id}/failed", "join"))),
                new(new($"outcome/{id}/cancelled"), ChildOutcomeMapping.Cancelled, new(Edge($"edge/{id}/cancelled", "join"))),
                new(new($"outcome/{id}/terminated"), ChildOutcomeMapping.Terminated, new(Edge($"edge/{id}/terminated", "join")))
            ]);
    }

    static SchedulerForkChildFixture CompileSchedulerForkChildPlan()
    {
        var fastChild = Compile(
            Definition("return", [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))]),
            definitionId: "process/durable-task-scheduler-fast-child");
        var (slowChild, _) = CompileRequestPlan("process/durable-task-scheduler-slow-child");
        var interactions = RequestContracts(
            "scheduler-fork-child",
            "completed",
            "failed",
            "cancelled",
            "terminated");
        var parent = Compile(
            Definition(
                "fork",
                [
                    new ForkProcessNode(
                        new("fork"),
                        [
                            new(new("branch/fast"), Edge("edge/fork-fast", "child/fast")),
                            new(new("branch/slow"), Edge("edge/fork-slow", "child/slow"))
                        ],
                        new("join")),
                    Child("child/fast", fastChild.DefinitionReference),
                    Child("child/slow", slowChild.DefinitionReference),
                    new JoinProcessNode(
                        new("join"),
                        new("fork"),
                        new(
                            ProcessJoinMode.Any,
                            requiredCount: 0,
                            ProcessJoinFailurePolicy.FailFast,
                            ProcessJoinCancellationPolicy.CancelRemaining,
                            ProcessJoinCompletionOrder.Unobservable,
                            ProcessJoinTieBreak.BranchIdentity),
                        Edge("edge/join-return", "return")),
                    new ReturnProcessNode(new("return"), Expr.Const("joined"))
                ]),
            interactions.Catalog,
            [Link(fastChild), Link(slowChild)],
            "process/durable-task-scheduler-fork-child-parent");
        return new(parent, fastChild, slowChild, interactions.Binding);

        InvokeProcessProcessNode Child(string id, ExecutionDefinitionReference definition) => new(
            new(id),
            definition,
            interactions.Request,
            ChildOutcomeMapping,
            Expr.BoundValue(ProcessBindingIds.Input),
            ProcessChildPurpose.Work,
            ProcessChildCancellationPolicy.Propagate,
            [
                new(new($"outcome/{id}/completed"), ChildOutcomeMapping.Completed, new(Edge($"edge/{id}/completed", "join"))),
                new(new($"outcome/{id}/failed"), ChildOutcomeMapping.Failed, new(Edge($"edge/{id}/failed", "join"))),
                new(new($"outcome/{id}/cancelled"), ChildOutcomeMapping.Cancelled, new(Edge($"edge/{id}/cancelled", "join"))),
                new(new($"outcome/{id}/terminated"), ChildOutcomeMapping.Terminated, new(Edge($"edge/{id}/terminated", "join")))
            ]);

        static ProcessDefinitionLink Link(CompiledProcessPlan child) => new(
            child.DefinitionReference,
            ProcessDefinitionLinkKind.Process,
            child.Definition.Input,
            child.Definition.Result,
            [],
            child.Definition.RecoveryPolicy);
    }

    static ChildProcessFixture CompilePartitionParentPlan()
    {
        var child = Compile(
            Definition("return", [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))]),
            definitionId: "process/durable-task-partition-child");
        var interactions = RequestContracts(
            "partition-child",
            "completed",
            "failed",
            "cancelled",
            "terminated");
        ValueBindingId partitionBinding = new("partition.item");
        var parent = Compile(
            Definition(
                StringCollectionContract,
                "partitions",
                [
                    new ForEachPartitionProcessNode(
                        new("partitions"),
                        Expr.BoundValue(ProcessBindingIds.Input),
                        new(partitionBinding, StringContract),
                        Expr.BoundValue(partitionBinding),
                        child.DefinitionReference,
                        interactions.Request,
                        ChildOutcomeMapping,
                        Expr.BoundValue(partitionBinding),
                        new(
                            maximumItems: 3,
                            maximumStartsPerActivation: 2,
                            maximumParallelism: 2),
                        ProcessPartitionFailurePolicy.FailFast,
                        capacityIdentity: null,
                        capacityDomains: [],
                        ProcessChildCancellationPolicy.Propagate,
                        Edge("edge/partitions-completed", "completed"),
                        Edge("edge/partitions-failed", "failed")),
                    new ReturnProcessNode(new("completed"), Expr.Const("partitions-completed")),
                    new FailProcessNode(new("failed"), Expr.Const("partitions-failed"))
                ]),
            interactions.Catalog,
            [new(
                child.DefinitionReference,
                ProcessDefinitionLinkKind.Process,
                child.Definition.Input,
                child.Definition.Result,
                [],
                child.Definition.RecoveryPolicy)],
            "process/durable-task-partition-parent");
        return new(parent, child, interactions.Binding);
    }

    static ImmutableArray<ProcessRequestOutcomeBranch> ChildOutcomes() =>
    [
        ChildOutcome("completed"),
        ChildOutcome("failed"),
        ChildOutcome("cancelled"),
        ChildOutcome("terminated")
    ];

    static ProcessRequestOutcomeBranch ChildOutcome(string outcome) => new(
        new($"outcome/{outcome}"),
        new(outcome),
        new(Edge($"edge/{outcome}", outcome)));

    static RequestContractFixture RequestContracts(string name, params string[] outcomes)
    {
        var requestDocument = InteractionDocument(
            $"interaction/request/durable-task-{name}",
            new RequestContractDefinition(
                new(StringContract, new("request/v1")),
                new RequestResponseObligation(
                    [.. outcomes.Select(outcome => outcome is "failed" or "cancelled" or "terminated"
                        ? (RequestTerminalOutcomeDefinition)new RequestFailureDefinition(
                            new(outcome),
                            new(StringContract, new($"{outcome}/v1")))
                        : new RequestResultDefinition(
                            new(outcome),
                            new(StringContract, new($"{outcome}/v1"))))],
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestResultDisposition.Observe,
                    RequestResultDisposition.Reject,
                    RequestResultDisposition.ReusePriorDisposition,
                    RequestRetrySemantics.StableIdentity,
                    RequestResolutionSemantics.Reconcile,
                    RequestResolutionSemantics.Escalate,
                    TimeSpan.FromDays(30))));
        RequestContractReference request = new(Reference(requestDocument));
        var replyDocuments = outcomes.Select(outcome => InteractionDocument(
                $"interaction/reply/durable-task-{name}-{outcome}",
                new ReplyContractDefinition(request, new(outcome))))
            .ToArray();
        var replies = replyDocuments
            .Select(static document => new ReplyContractReference(Reference(document)))
            .ToArray();
        var catalog = Catalog([requestDocument, .. replyDocuments]);
        var binding = new DurableRequestBinding(
            request,
            [.. outcomes.Select((outcome, index) => new DurableReplyBinding(new(outcome), replies[index]))],
            maxAttempts: 2,
            claimLease: TimeSpan.FromMinutes(5),
            timeoutAfter: null,
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            reconciliationTarget: new(
                DefinitionReference($"process/reconcile/{name}", '7'),
                new("node/reconcile")),
            escalationTarget: new(
                DefinitionReference($"process/escalate/{name}", '8'),
                new("node/escalate")));
        return new(request, catalog, binding);
    }

    static DurableTaskDurableOperationAttemptResult CompletedChild(
        EmissionId request,
        ProcessChildRequestTarget target)
    {
        var outcome = new RequestResultOutcome(ChildOutcomeMapping.Completed, StringValue(target.ProgressIdentity ?? "done"));
        return new(
            new DurableOperationOutcomeObservation(
                outcome,
                replyOrigin: new ProcessInteractionOrigin(
                    target.Definition,
                    new("return"),
                    target.Continuation,
                    new($"activation/{request.Value}"),
                    new($"token/{request.Value}"),
                    outcome: new("return"))),
            deadlineElapsed: false);
    }

    static DurableTaskDurableOperationAttemptResult CancelledChild(
        EmissionId request,
        ProcessChildRequestTarget target)
    {
        var outcome = new RequestFailureOutcome(ChildOutcomeMapping.Cancelled, StringValue("cancelled"));
        return new(
            new DurableOperationOutcomeObservation(
                outcome,
                replyOrigin: new ProcessInteractionOrigin(
                    target.Definition,
                    new("cancelled"),
                    target.Continuation,
                    new($"activation/{request.Value}/cancelled"),
                    new($"token/{request.Value}/cancelled"),
                    outcome: new("cancelled"))),
            deadlineElapsed: false);
    }

    static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 100 && !predicate(); attempt++)
        {
            await Task.Delay(10);
        }
        Assert.True(predicate(), "The expected asynchronous condition was not observed before the test deadline.");
    }

    static DurableTaskProcessRealizationPlan Physical(CompiledProcessPlan plan)
    {
        var result = DurableTaskProcessRealizationCompiler.CompileExecutable(plan);
        Assert.True(result.IsSuccessful, Format(result.Realization.Diagnostics));
        return Assert.IsType<DurableTaskProcessRealizationPlan>(result.Plan);
    }

    static CompiledProcessPlan Compile(
        CanonicalProcessDefinition definition,
        InteractionContractCatalog? contracts = null,
        ImmutableArray<ProcessDefinitionLink> definitions = default,
        string definitionId = "process/durable-task-sequential-tests")
    {
        var document = ProcessDefinitionDocuments.Create(
            new(definitionId),
            new("revision/1"),
            definition,
            Provenance());
        var result = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext(
                definitions: definitions.IsDefault ? null : definitions,
                interactionContracts: contracts));
        Assert.True(result.IsSuccessful, Format(result.Validation.Diagnostics));
        return Assert.IsType<CompiledProcessPlan>(result.Plan);
    }

    static CanonicalProcessDefinition Definition(
        string entry,
        ImmutableArray<ProcessNode> nodes,
        ProcessRecoveryPolicy recoveryPolicy = ProcessRecoveryPolicy.ContinueAttempt) => new(
        StringContract,
        StringContract,
        new(entry),
        nodes,
        recoveryPolicy);

    static CanonicalProcessDefinition Definition(
        ValueContract input,
        string entry,
        ImmutableArray<ProcessNode> nodes,
        ProcessRecoveryPolicy recoveryPolicy = ProcessRecoveryPolicy.ContinueAttempt) => new(
        input,
        StringContract,
        new(entry),
        nodes,
        recoveryPolicy);

    static DurableTaskSequentialProcessStart Start(
        CompiledProcessPlan plan,
        string input,
        string instance = "process-instance/durable-task-sequential-tests") =>
        Start(plan, StringValue(input), instance);

    static DurableTaskSequentialProcessStart Start(
        CompiledProcessPlan plan,
        PortableValue input,
        string instance)
    {
        var continuation = new ProcessContinuationIdentity(
            new(instance),
            new("process-attempt/1"));
        var scope = new InteractionAuthorityScope("authority/tests", "tenant/cohesive");
        var context = new ProcessControlCommandContext(
            new("command/start"),
            new("idempotency/start"),
            continuation.ProcessInstanceId,
            new("test-runner", scope, "authorization/tests"),
            StartedAtUtc,
            Provenance());
        var request = new ProcessStartRequest(
            ProcessStartRequest.CurrentSchemaVersion,
            plan.DefinitionReference,
            context,
            continuation,
            input);
        return new(
            new ProcessStartReceipt(request, StartedAtUtc),
            new ProcessActivationContext(
                scope,
                new("correlation/durable-task-sequential-tests"),
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                Provenance()));
    }

    static ProcessActivation Activation(
        ProcessContinuationState state,
        ProcessActivationCause cause,
        DurableTaskSequentialProcessStart start,
        DateTimeOffset? observedAtUtc = null,
        ImmutableArray<ProcessActivationInput> inputs = default) => new(
        DurableTaskSequentialProcessIdentities.Activation(state),
        cause,
        observedAtUtc ?? start.Receipt.AcceptedAtUtc,
        start.ActivationContext,
        inputs);

    static ProcessControlState ControlAfter(
        DurableTaskSequentialProcessStart start,
        CompiledProcessPlan plan,
        ProcessContinuationState before,
        ProcessActivationDecision decision)
    {
        var contracts = plan.ValidationContext.InteractionContracts;
        if (contracts is null)
        {
            var validation = InteractionContractCatalog.TryCreate([], out contracts);
            Assert.True(validation.IsValid, Format(validation.Diagnostics));
        }
        var executor = new ProcessControlReferenceExecutor(Assert.IsType<InteractionContractCatalog>(contracts));
        var initial = start.Receipt.CreateInitialState();
        var activationId = DurableTaskSequentialProcessIdentities.Activation(before);
        var begun = executor.BeginActivation(
            initial,
            new(
                new(new(initial.ProcessInstanceId, initial.CurrentAttempt.AttemptId), initial.Revision),
                activationId,
                start.Receipt.AcceptedAtUtc));
        var node = decision.Evidence.SafePointNode
            ?? (decision.Evidence.Trace.IsEmpty ? plan.Definition.Entry : decision.Evidence.Trace[^1].Node);
        return executor.ReachSafePoint(
            begun.State,
            new(
                DurableTaskSequentialProcessIdentities.SafePoint(before, activationId, node),
                new(
                    new(begun.State.ProcessInstanceId, begun.State.CurrentAttempt.AttemptId),
                    begun.State.Revision),
                activationId,
                node,
                start.Receipt.AcceptedAtUtc)).State;
    }

    static ProcessControlExpectation Expectation(ProcessControlState state) => new(
        new(state.ProcessInstanceId, state.CurrentAttempt.AttemptId),
        state.Revision);

    static ProcessControlExpectation Expectation(ExecutionStatus status) => new(
        new(status.ProcessInstanceId, status.CurrentAttemptId),
        status.ControlRevision);

    static ProcessControlCommandContext ControlContext(
        DurableTaskSequentialProcessStart start,
        string id,
        DateTimeOffset issuedAtUtc) => new(
        new(id),
        new($"idempotency/{id}"),
        start.Receipt.Request.InitialContinuation.ProcessInstanceId,
        start.Receipt.Request.Context.Authorization,
        issuedAtUtc,
        Provenance());

    static PauseProcessCommand Pause(
        DurableTaskSequentialProcessStart start,
        ProcessControlState state,
        string id,
        DateTimeOffset issuedAtUtc) => new(
        ProcessControlCommand.CurrentSchemaVersion,
        ControlContext(start, id, issuedAtUtc),
        Expectation(state));

    static PauseProcessCommand Pause(
        DurableTaskSequentialProcessStart start,
        ExecutionStatus status,
        string id,
        DateTimeOffset issuedAtUtc) => new(
        ProcessControlCommand.CurrentSchemaVersion,
        ControlContext(start, id, issuedAtUtc),
        Expectation(status));

    static InspectProcessCommand Inspect(
        DurableTaskSequentialProcessStart start,
        ProcessControlState state,
        string id,
        DateTimeOffset issuedAtUtc) => new(
        ProcessControlCommand.CurrentSchemaVersion,
        ControlContext(start, id, issuedAtUtc),
        Expectation(state));

    static InspectProcessCommand Inspect(
        DurableTaskSequentialProcessStart start,
        ExecutionStatus status,
        string id,
        DateTimeOffset issuedAtUtc) => new(
        ProcessControlCommand.CurrentSchemaVersion,
        ControlContext(start, id, issuedAtUtc),
        Expectation(status));

    static ContinueProcessCommand Continue(
        DurableTaskSequentialProcessStart start,
        ProcessControlState state,
        string id,
        DateTimeOffset issuedAtUtc,
        ProcessControlExpectation? expectation = null) => new(
        ProcessControlCommand.CurrentSchemaVersion,
        ControlContext(start, id, issuedAtUtc),
        expectation ?? Expectation(state));

    static ContinueProcessCommand Continue(
        DurableTaskSequentialProcessStart start,
        ExecutionStatus status,
        string id,
        DateTimeOffset issuedAtUtc) => new(
        ProcessControlCommand.CurrentSchemaVersion,
        ControlContext(start, id, issuedAtUtc),
        Expectation(status));

    static RestartProcessAttemptCommand Restart(
        DurableTaskSequentialProcessStart start,
        ProcessControlState state,
        string id,
        DateTimeOffset issuedAtUtc,
        ProcessAttemptId replacementAttempt) => new(
        ProcessControlCommand.CurrentSchemaVersion,
        ControlContext(start, id, issuedAtUtc),
        Expectation(state),
        new(
            replacementAttempt,
            ProcessAttemptCleanupRequirement.RetainEvidence,
            new("operator.restart")));

    static RestartProcessAttemptCommand Restart(
        DurableTaskSequentialProcessStart start,
        ExecutionStatus status,
        string id,
        DateTimeOffset issuedAtUtc,
        ProcessAttemptId replacementAttempt) => new(
        ProcessControlCommand.CurrentSchemaVersion,
        ControlContext(start, id, issuedAtUtc),
        Expectation(status),
        new(
            replacementAttempt,
            ProcessAttemptCleanupRequirement.RetainEvidence,
            new("operator.restart")));

    static async Task<DurableTaskSequentialProcessResult> WaitForObservationAsync(
        ConcurrentQueue<DurableTaskSequentialProcessResult> observations,
        Func<DurableTaskSequentialProcessResult, bool> predicate)
    {
        await WaitUntilAsync(() => observations.Any(predicate));
        return observations.Last(predicate);
    }

    static async Task<(DurableTaskSequentialProcessResult Result, CancellationToken TimerCancellation)>
        RunTerminalControlAsync(bool terminate)
    {
        var plan = CompileTimerPlan(
            StartedAtUtc.AddMinutes(5),
            terminate ? "process/durable-task-control-terminate" : "process/durable-task-control-cancel");
        var start = Start(
            plan,
            "terminal-control",
            terminate ? "instance/control-terminate" : "instance/control-cancel");
        var now = StartedAtUtc;
        var controls = Channel.CreateUnbounded<ProcessControlCommand>();
        ConcurrentQueue<DurableTaskSequentialProcessResult> observations = [];
        var scheduled = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            (delay, cancellationToken) =>
            {
                scheduled.TrySetResult(cancellationToken);
                return never.Task;
            },
            () => now,
            observations.Enqueue,
            waitForControl: () => controls.Reader.ReadAsync().AsTask());

        var timerCancellation = await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await WaitForObservationAsync(
            observations,
            static result => result.Control.CurrentAttempt.Phase == ProcessControlExecutionPhase.AtSafePoint);
        now = now.AddSeconds(1);
        ProcessControlCommand command = terminate
            ? new TerminateProcessCommand(
                ProcessControlCommand.CurrentSchemaVersion,
                ControlContext(start, "control/terminate", now),
                Expectation(running.Control),
                new("operator.terminate"),
                ProcessAttemptCleanupRequirement.RetainEvidence)
            : new CancelProcessCommand(
                ProcessControlCommand.CurrentSchemaVersion,
                ControlContext(start, "control/cancel", now),
                Expectation(running.Control),
                new("operator.cancel"));
        await controls.Writer.WriteAsync(command);
        return (await execution.WaitAsync(TimeSpan.FromSeconds(5)), timerCancellation);
    }

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static ExecutionDefinitionReference DefinitionReference(string id, char digit) => new(
        new(id),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(digit, 64)));

    static ExecutionDefinitionDocument InteractionDocument(
        string id,
        InteractionContractDefinition definition) =>
        InteractionContractDocuments.Create(new(id), new("revision/1"), definition, Provenance());

    static InteractionContractCatalog Catalog(params ExecutionDefinitionDocument[] documents)
    {
        var validation = InteractionContractCatalog.TryCreate(documents, out var catalog);
        Assert.True(validation.IsValid, Format(validation.Diagnostics));
        return Assert.IsType<InteractionContractCatalog>(catalog);
    }

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static InteractionEnvelopeContext IncomingContext(
        CompiledProcessPlan plan,
        ProcessContinuationIdentity continuation,
        TokenId token,
        string emission,
        EmissionId causation) => new(
        new(emission),
        new ProcessInteractionOrigin(
            plan.DefinitionReference,
            new("source/reply"),
            continuation,
            new("activation/reply-source"),
            token),
        new("correlation/durable-task-sequential-tests"),
        causation,
        new("authority/tests", "tenant/cohesive"),
        new($"idempotency/{emission}"),
        ordering: null,
        new(
            InteractionDurabilityDemand.Durable,
            InteractionVisibilityDemand.AfterOriginCommit),
        Provenance());

    static PortableValue StringValue(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    static PortableValue InstantValue(DateTimeOffset value) =>
        PortableValue.Concrete(InstantContract, ObservationValue.FromDateTimeOffset(value));

    static ProcessActivationInput AwaitMatchInput(
        AwaitMatchFixture fixture,
        DurableTaskSequentialProcessResult waiting,
        string emission,
        DomainEventContractReference? contract = null)
    {
        var token = Assert.Single(waiting.State.Tokens);
        var wait = Assert.Single(waiting.State.Waits, static candidate => candidate.Active);
        return AwaitMatchInput(
            fixture,
            waiting.State.Continuation,
            token.Id,
            wait.RegistrationId,
            emission,
            contract);
    }

    static ProcessActivationInput AwaitMatchInput(
        AwaitMatchFixture fixture,
        ExecutionStatus waiting,
        string emission,
        DomainEventContractReference? contract = null)
    {
        var token = Assert.Single(waiting.Runtime.Tokens).TokenId;
        var continuation = new ProcessContinuationIdentity(
            waiting.ProcessInstanceId,
            waiting.CurrentAttemptId);
        return AwaitMatchInput(
            fixture,
            continuation,
            token,
            waitRegistrationId: null,
            emission,
            contract);
    }

    static ProcessActivationInput AwaitMatchInput(
        AwaitMatchFixture fixture,
        ProcessContinuationIdentity continuation,
        TokenId token,
        ProcessWaitRegistrationId? waitRegistrationId,
        string emission,
        DomainEventContractReference? contract = null)
    {
        var target = new ProcessTokenInteractionTarget(continuation, token, waitRegistrationId);
        var envelope = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(
                fixture.Plan,
                continuation,
                token,
                emission,
                new($"causation/{emission}")),
            contract ?? fixture.EventContract,
            StringValue("event"));
        return new(target, envelope);
    }

    static async Task<SignalEnvelope> EmitSignalAsync(
        CompiledProcessPlan sender,
        string senderInstance,
        InteractionTarget target)
    {
        SignalEnvelope? delivered = null;
        _ = await DurableTaskSequentialProcessInterpreter.RunAsync(
            sender,
            Start(sender, "signal", senderInstance),
            EmptyDurableRequestBindingResolver.Instance,
            UnexpectedOperation,
            UnexpectedDurableOperation,
            UnexpectedChildProcess,
            UnexpectedReconciliation,
            UnexpectedInteraction,
            TestDurableTimer,
            () => StartedAtUtc,
            resolveSignalTarget: resolution => Task.FromResult(ProcessSignalTargetResult.Resolved(target)),
            deliverSignal: signal =>
            {
                delivered = signal;
                return Task.CompletedTask;
            });
        return Assert.IsType<SignalEnvelope>(delivered);
    }

    static PortableValue CollectionValue(params string[] values) => PortableValue.Concrete(
        StringCollectionContract,
        ObservationValue.FromImmutableArray(
            [.. values.Select(ObservationValue.FromString)]));

    static ExecutionProvenance Provenance() => new(
        new("durable-task-sequential-tests", "1"),
        new("tests/execution-kernel/durable-task-sequential"),
        DocumentOrigin.Generated);

    static string Serialize<T>(T value) => DurableTaskProcessDataConverter.Create().Serialize(value)!;

    static string Format(IEnumerable<DocumentValidationDiagnostic> diagnostics) => string.Join(
        Environment.NewLine,
        diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    static string Format(IEnumerable<ProcessInterpreterRealizationDiagnostic> diagnostics) => string.Join(
        Environment.NewLine,
        diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    sealed class EchoHost : IProcessReferenceHost
    {
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            ProcessOperationResult.Completed(invocation.Input);

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            ProcessOperationResult.Completed(evaluation.Input);

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException("Unexpected Signal target resolution.");
    }

    sealed class CountingEchoHost : IProcessReferenceHost
    {
        readonly ConcurrentQueue<ProcessTransitionInvocation> transitions = [];
        readonly ConcurrentDictionary<string, ProcessTokenInteractionTarget> signalTargets =
            new(StringComparer.Ordinal);

        internal IReadOnlyCollection<ProcessTransitionInvocation> Transitions => transitions.ToArray();

        internal void RegisterSignalTarget(string route, ProcessTokenInteractionTarget target)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(route);
            ArgumentNullException.ThrowIfNull(target);
            signalTargets[route] = target;
        }

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation)
        {
            transitions.Enqueue(invocation);
            return ProcessOperationResult.Completed(invocation.Input);
        }

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            ProcessOperationResult.Completed(evaluation.Input);

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution)
        {
            var route = resolution.Value.Value?.GetRequiredString()
                ?? throw new InvalidOperationException("A Scheduler Signal route must be a concrete string.");
            return signalTargets.TryGetValue(route, out var target)
                ? ProcessSignalTargetResult.Resolved(target)
                : throw new InvalidOperationException($"No Scheduler Signal target is registered for '{route}'.");
        }
    }

    sealed class BindingResolver : IDurableRequestBindingResolver
    {
        readonly ImmutableArray<DurableRequestBinding> bindings;

        internal BindingResolver(params DurableRequestBinding[] bindings)
        {
            ArgumentNullException.ThrowIfNull(bindings);
            this.bindings = [.. bindings];
        }

        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? resolved)
        {
            ArgumentNullException.ThrowIfNull(request);
            resolved = bindings.SingleOrDefault(binding => request.Contract == binding.Request);
            return resolved is not null;
        }
    }

    sealed class AdapterResolver(IDurableOperationAdapter adapter) : IDurableOperationAdapterResolver
    {
        public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? resolved)
        {
            ArgumentNullException.ThrowIfNull(request);
            resolved = adapter.Capabilities.Supports(request.Contract) ? adapter : null;
            return resolved is not null;
        }
    }

    sealed class CountingDurableOperationAdapter(RequestContractReference request) : IDurableOperationAdapter
    {
        readonly ConcurrentQueue<DurableOperationInvocation> invocations = [];

        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            [request]);

        internal IReadOnlyCollection<DurableOperationInvocation> Invocations => invocations.ToArray();

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            context.ThrowIfCancellationRequested();
            invocations.Enqueue(invocation);
            return ValueTask.FromResult<DurableOperationAttemptObservation>(
                new DurableOperationOutcomeObservation(
                    new RequestResultOutcome(new("accepted"), StringValue("accepted"))));
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request) =>
            throw new InvalidOperationException("Unexpected Scheduler-emulator reconciliation.");
    }

    sealed class SimulatedCrashException : Exception;

    sealed record ForkRequestFixture(
        CompiledProcessPlan Plan,
        DurableRequestBinding Binding);

    sealed record ChildProcessFixture(
        CompiledProcessPlan Parent,
        CompiledProcessPlan Child,
        DurableRequestBinding Binding);

    sealed record PendingChild(
        ProcessChildRequestTarget Target,
        TaskCompletionSource<DurableTaskDurableOperationAttemptResult> Completion);

    sealed record ScheduledTimer(
        TimeSpan Delay,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion);

    sealed record AwaitMatchFixture(
        CompiledProcessPlan Plan,
        DomainEventContractReference EventContract,
        DomainEventContractReference AlternateEventContract);

    sealed record SignalFixture(
        CompiledProcessPlan Sender,
        CompiledProcessPlan Receiver,
        SignalContractReference Contract);

    sealed record SelfSignalFixture(
        CompiledProcessPlan Plan,
        SignalContractReference Contract);

    sealed record SchedulerForkChildFixture(
        CompiledProcessPlan Parent,
        CompiledProcessPlan FastChild,
        CompiledProcessPlan SlowChild,
        DurableRequestBinding Binding);

    sealed record RequestContractFixture(
        RequestContractReference Request,
        InteractionContractCatalog Catalog,
        DurableRequestBinding Binding);

    sealed class RejectingHost : IProcessReferenceHost
    {
        internal static RejectingHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException("Unexpected Transition invocation.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException("Unexpected Relation evaluation.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException("Unexpected Signal target resolution.");
    }

    sealed class FixedSignalTargetHost(InteractionTarget target) : IProcessReferenceHost
    {
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException("Unexpected Transition invocation.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException("Unexpected Relation evaluation.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            ProcessSignalTargetResult.Resolved(target);
    }

    sealed class DurableTaskSchedulerFactAttribute : FactAttribute
    {
        public DurableTaskSchedulerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("DURABLE_TASK_SCHEDULER_CONNECTION_STRING")))
            {
                Skip = "Set DURABLE_TASK_SCHEDULER_CONNECTION_STRING or run eng/test-durable-task-integration.sh.";
            }
        }
    }
}
