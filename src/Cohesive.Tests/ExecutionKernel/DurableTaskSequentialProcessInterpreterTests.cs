using System.Collections.Concurrent;
using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
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
    public void PlanCatalog_RejectsConstructsOutsideTheExecutableSequentialSlice()
    {
        var timerPlan = Compile(Definition(
            "timer",
            [
                new TimerProcessNode(
                    new("timer"),
                    Expr.Const(ObservationValue.FromDateTimeOffset(StartedAtUtc.AddMinutes(1))),
                    Edge("edge/timer-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]));
        var physical = Physical(timerPlan);

        var exception = Assert.Throws<ArgumentException>(() =>
            new DurableTaskSequentialProcessPlanCatalog([physical]));

        Assert.Contains("timer:timer", exception.Message, StringComparison.Ordinal);
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
        var converter = DurableTaskProcessDataConverter.Create();

        var restoredStart = Assert.IsType<DurableTaskSequentialProcessStart>(
            converter.Deserialize(converter.Serialize(start), typeof(DurableTaskSequentialProcessStart)));
        var restoredResult = Assert.IsType<DurableTaskSequentialProcessResult>(
            converter.Deserialize(converter.Serialize(result), typeof(DurableTaskSequentialProcessResult)));

        Assert.Equal(Serialize(start), Serialize(restoredStart));
        Assert.Equal(Serialize(result), Serialize(restoredResult));
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
        var catalog = new DurableTaskSequentialProcessPlanCatalog([
            Physical(completedPlan),
            Physical(failedPlan),
            Physical(restartPlan),
            Physical(durableRequestPlan)
        ], new BindingResolver(durableBinding));
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

        var duplicate = await firstClient.ScheduleCohesiveProcessAsync(completedStart, timeout.Token);
        Assert.True(duplicate.Replayed);
        Assert.Equal(scheduled.InstanceId, duplicate.InstanceId);

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
        var requested = Assert.IsType<RequestEnvelope>(Assert.Single(waiting.Emissions));
        var token = Assert.Single(waiting.State.Tokens);
        var reply = new ReplyEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(
                restartPlan,
                waiting.State.Continuation,
                token.Id,
                "emission/restart-reply",
                requested.Context.EmissionId),
            replyContract,
            requested.Context.EmissionId,
            new RequestResultOutcome(new("accepted"), StringValue("accepted")));
        await recoveredClient.RaiseCohesiveProcessInteractionAsync(
            restartStart,
            new(
                new(waiting.State.Continuation, token.Id),
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

    static async Task<DurableTaskSequentialProcessResult> WaitForOutstandingRequest(
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
            var status = instance?.ReadCustomStatusAs<DurableTaskSequentialProcessResult>();
            if (status is not null && !status.State.OutstandingRequests.IsEmpty)
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
            UnexpectedReconciliation,
            UnexpectedInteraction,
            (delay, cancellationToken) => Task.CompletedTask,
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
            operation => Task.FromResult((reconcile ?? (static state =>
                throw new InvalidOperationException("Unexpected durable Request reconciliation.")))(operation)),
            UnexpectedInteraction,
            (delay, cancellationToken) => Task.CompletedTask,
            () => StartedAtUtc);

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

    static DurableTaskProcessRealizationPlan Physical(CompiledProcessPlan plan)
    {
        var result = DurableTaskProcessRealizationCompiler.Compile(plan);
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
        ImmutableArray<ProcessNode> nodes) => new(
        StringContract,
        StringContract,
        new(entry),
        nodes,
        ProcessRecoveryPolicy.ContinueAttempt);

    static DurableTaskSequentialProcessStart Start(
        CompiledProcessPlan plan,
        string input,
        string instance = "process-instance/durable-task-sequential-tests")
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
            StringValue(input));
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

        internal IReadOnlyCollection<ProcessTransitionInvocation> Transitions => transitions.ToArray();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation)
        {
            transitions.Enqueue(invocation);
            return ProcessOperationResult.Completed(invocation.Input);
        }

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            ProcessOperationResult.Completed(evaluation.Input);

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException("Unexpected Signal target resolution.");
    }

    sealed class BindingResolver(DurableRequestBinding binding) : IDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? resolved)
        {
            ArgumentNullException.ThrowIfNull(request);
            resolved = request.Contract == binding.Request ? binding : null;
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
