using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessReferenceInterpreterPolicyTests
{
    static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 29, 18, 0, 0, TimeSpan.Zero);

    static readonly ValueContract StringContract =
        new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void CancellationWithInput_TerminalizesTheInputWithoutExecutingANode()
    {
        var eventDocument = InteractionDocument(
            "interaction/event/cancellation-policy",
            new DomainEventContractDefinition(StringSchema("event/cancellation-policy/v1")));
        DomainEventContractReference eventContract = new(Reference(eventDocument));
        var plan = Compile(
            Definition(
                "return",
                ProcessRecoveryPolicy.ContinueAttempt,
                new ReturnProcessNode(new("return"), Expr.Const("must-not-run"))),
            Catalog(eventDocument));
        var continuation = Continuation();
        var initial = ProcessReferenceInterpreter.Create(plan, continuation, StringValue("input"));
        var token = Assert.Single(initial.Tokens);
        var target = new ProcessTokenInteractionTarget(continuation, token.Id);
        var envelope = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(plan, continuation, token.Id, "emission/cancelled-input"),
            eventContract,
            StringValue("presented-before-cancellation"));

        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation(
                "activation/cancel-with-input",
                ProcessActivationCause.Control,
                inputs: [new(target, envelope)],
                cancellation: new(
                    continuation.ProcessAttemptId,
                    new("operator.cancel"))),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Cancelled, decision.Disposition);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, decision.State.Terminal.Kind);
        Assert.Equal(ExecutionTokenDisposition.Cancelled, Assert.Single(decision.State.Tokens).Disposition);
        Assert.Empty(decision.State.BufferedInputs);
        Assert.Equal(
            ProcessInputAdmissionDisposition.TerminalUnconsumed,
            Assert.Single(decision.InputAdmissions).Disposition);
        Assert.Equal(
            ProcessInputAdmissionReason.TerminalUnconsumed,
            Assert.Single(decision.InputAdmissions).Reason);
        Assert.Equal(
            ProcessInputAdmissionDisposition.TerminalUnconsumed,
            Assert.Single(decision.State.InputReceipts).Disposition);
        Assert.Equal(
            ProcessInputAdmissionReason.TerminalUnconsumed,
            Assert.Single(decision.State.InputReceipts).Reason);
        Assert.DoesNotContain(
            decision.Evidence.Trace,
            static item => item.Kind == ProcessTraceEventKind.NodeEntered);
        Assert.Contains(
            decision.Evidence.Trace,
            item => item.Kind == ProcessTraceEventKind.InputAdmitted
                    && item.Emission == envelope.Context.EmissionId);
        Assert.Contains(
            decision.Evidence.Trace,
            static item => item.Kind == ProcessTraceEventKind.CancellationApplied);
    }

    [Fact]
    public void ConflictingEmissionIdentity_IsOrderIndependentAndNeitherCandidateIsAdmitted()
    {
        var eventDocument = InteractionDocument(
            "interaction/event/conflicting-identity",
            new DomainEventContractDefinition(StringSchema("event/conflicting-identity/v1")));
        DomainEventContractReference eventContract = new(Reference(eventDocument));
        var plan = Compile(
            Definition(
                "await",
                ProcessRecoveryPolicy.ContinueAttempt,
                new AwaitMatchProcessNode(
                    new("await"),
                    ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                    [
                        new ProcessAwaitInteractionClause(
                            new("clause/event"),
                            eventContract,
                            new(new("await.event"), StringContract),
                            requestObligation: null,
                            guard: null,
                            priority: 0,
                            new(Edge("edge/event-return", "return")))
                    ],
                    ProcessAwaitInputDisposition.Observe,
                    ProcessAwaitInputDisposition.Reject,
                    ProcessAwaitInputDisposition.ReusePriorDisposition,
                    ProcessAwaitMissingTargetDisposition.Reject,
                    TimeSpan.FromDays(1)),
                new ReturnProcessNode(new("return"), Expr.Const("unexpected"))),
            Catalog(eventDocument));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var token = Assert.Single(initial.Tokens);
        var target = new ProcessTokenInteractionTarget(initial.Continuation, token.Id);
        var context = IncomingContext(
            plan,
            initial.Continuation,
            token.Id,
            "emission/conflicting-identity");
        var alpha = new ProcessActivationInput(
            target,
            new DomainEventEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                context,
                eventContract,
                StringValue("alpha")));
        var zeta = new ProcessActivationInput(
            target,
            new DomainEventEnvelope(
                InteractionEnvelope.CurrentSchemaVersion,
                context,
                eventContract,
                StringValue("zeta")));

        var forward = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation(
                "activation/conflicting-identity",
                ProcessActivationCause.Interaction,
                inputs: [alpha, zeta]),
            RejectingHost.Instance);
        var reverse = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation(
                "activation/conflicting-identity",
                ProcessActivationCause.Interaction,
                inputs: [zeta, alpha]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, forward.Disposition);
        Assert.Equal(ProcessActivationDisposition.DurableCut, reverse.Disposition);
        AssertConflict(forward, context.EmissionId);
        AssertConflict(reverse, context.EmissionId);
        Assert.Equal(forward.Disposition, reverse.Disposition);
        Assert.Equal(
            forward.State.Tokens.Select(TokenProjection),
            reverse.State.Tokens.Select(TokenProjection));
        Assert.Equal(
            forward.State.Waits.Select(WaitProjection),
            reverse.State.Waits.Select(WaitProjection));
        Assert.Equal(
            forward.Evidence.Trace.Select(TraceProjection),
            reverse.Evidence.Trace.Select(TraceProjection));
        Assert.Equal(
            InteractionEnvelopeJsonSerializer.GetCanonicalBytes(
                Assert.Single(forward.InputAdmissions).Input.Envelope),
            InteractionEnvelopeJsonSerializer.GetCanonicalBytes(
                Assert.Single(reverse.InputAdmissions).Input.Envelope));

        var repeated = ProcessReferenceInterpreter.Activate(
            plan,
            forward.State,
            Activation(
                "activation/conflicting-identity/repeated",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs: [zeta, alpha]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Quiescent, repeated.Disposition);
        AssertConflict(repeated, context.EmissionId);
    }

    [Fact]
    public void RestartAttempt_RejectsOldRecoveryAndCreatesACleanReplacementAttempt()
    {
        var plan = Compile(Definition(
            "cut",
            ProcessRecoveryPolicy.RestartAttempt,
            new DurableCutProcessNode(new("cut"), Edge("edge/cut-return", "return")),
            new ReturnProcessNode(new("return"), Expr.Const("done"))));
        var originalContinuation = Continuation();
        var initial = ProcessReferenceInterpreter.Create(
            plan,
            originalContinuation,
            StringValue("restart-input"));
        var oldRoot = Assert.Single(initial.Tokens).Id;
        var interrupted = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/restart/cut", ProcessActivationCause.Start),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, interrupted.Disposition);
        Assert.NotEmpty(interrupted.State.Waits);

        var rejected = ProcessReferenceInterpreter.Activate(
            plan,
            interrupted.State,
            Activation(
                "activation/restart/old-recovery",
                ProcessActivationCause.Recovery,
                StartedAtUtc.AddMinutes(1)),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Rejected, rejected.Disposition);
        Assert.Same(interrupted.State, rejected.State);
        Assert.Contains(
            rejected.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.RecoveryRequiresRestart);

        ProcessAttemptId replacementAttempt = new("process-attempt/2");
        var replacement = ProcessReferenceInterpreter.RestartAttempt(
            plan,
            interrupted.State,
            replacementAttempt);
        var replayedReplacement = ProcessReferenceInterpreter.RestartAttempt(
            plan,
            interrupted.State,
            replacementAttempt);
        var replacementRoot = Assert.Single(replacement.Tokens);

        Assert.Equal(originalContinuation.ProcessInstanceId, replacement.Continuation.ProcessInstanceId);
        Assert.Equal(replacementAttempt, replacement.Continuation.ProcessAttemptId);
        Assert.Equal(plan.DefinitionReference, replacement.Definition);
        Assert.Equal(0, replacement.CompletedActivationCount);
        Assert.NotEqual(oldRoot, replacementRoot.Id);
        Assert.Equal(replacementRoot.Id, Assert.Single(replayedReplacement.Tokens).Id);
        Assert.Equal(plan.Definition.Entry, replacementRoot.Node);
        Assert.Equal(ExecutionTokenDisposition.Ready, replacementRoot.Disposition);
        Assert.Equal(
            StringValue("restart-input"),
            Assert.Single(replacementRoot.Bindings).Value);
        Assert.Empty(replacement.Forks);
        Assert.Empty(replacement.Waits);
        Assert.Empty(replacement.BufferedInputs);
        Assert.Empty(replacement.InputReceipts);
        Assert.Empty(replacement.OutstandingRequests);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, replacement.Terminal.Kind);
    }

    [Fact]
    public void AnyCancelRemaining_SelectsTheCompletedBranchAndCancelsTheWaitingBranchDeterministically()
    {
        var dueAtUtc = StartedAtUtc.AddDays(1);
        var plan = Compile(Definition(
            "fork",
            ProcessRecoveryPolicy.ContinueAttempt,
            new ForkProcessNode(
                new("fork"),
                [
                    new(new("branch/alpha"), Edge("edge/fork-alpha", "join")),
                    new(new("branch/beta"), Edge("edge/fork-beta", "timer/beta"))
                ],
                new("join")),
            new TimerProcessNode(
                new("timer/beta"),
                Expr.Const(ObservationValue.FromDateTimeOffset(dueAtUtc)),
                Edge("edge/beta-join", "join")),
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
            new ReturnProcessNode(new("return"), Expr.Const("joined"))));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var first = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/any/start", ProcessActivationCause.Start),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, first.Disposition);

        var activation = Activation(
            "activation/any/resolve",
            ProcessActivationCause.Timer,
            StartedAtUtc.AddMinutes(1));
        var resolved = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            activation,
            RejectingHost.Instance);
        var replay = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            activation,
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, resolved.Disposition);
        var fork = Assert.Single(resolved.State.Forks);
        Assert.True(fork.Resolved);
        Assert.True(fork.SelectedBranches.SequenceEqual([new ExecutionNodeId("branch/alpha")]));
        Assert.Equal(
            ExecutionTokenDisposition.Completed,
            fork.Branches.Single(static branch => branch.Branch == new ExecutionNodeId("branch/alpha")).Disposition);
        Assert.Equal(
            ExecutionTokenDisposition.Cancelled,
            fork.Branches.Single(static branch => branch.Branch == new ExecutionNodeId("branch/beta")).Disposition);
        Assert.True(fork.SelectedBranches.SequenceEqual(Assert.Single(replay.State.Forks).SelectedBranches));
        Assert.Equal(
            resolved.State.Tokens.Select(TokenProjection),
            replay.State.Tokens.Select(TokenProjection));
    }

    [Fact]
    public void RequiredCountContinueRemaining_ResolvesTwoBranchesWithoutCancellingTheThird()
    {
        var dueAtUtc = StartedAtUtc.AddDays(1);
        var plan = Compile(Definition(
            "fork",
            ProcessRecoveryPolicy.ContinueAttempt,
            new ForkProcessNode(
                new("fork"),
                [
                    new(new("branch/alpha"), Edge("edge/fork-alpha", "join")),
                    new(new("branch/beta"), Edge("edge/fork-beta", "join")),
                    new(new("branch/gamma"), Edge("edge/fork-gamma", "timer/gamma"))
                ],
                new("join")),
            new TimerProcessNode(
                new("timer/gamma"),
                Expr.Const(ObservationValue.FromDateTimeOffset(dueAtUtc)),
                Edge("edge/gamma-join", "join")),
            new JoinProcessNode(
                new("join"),
                new("fork"),
                new(
                    ProcessJoinMode.RequiredCount,
                    requiredCount: 2,
                    ProcessJoinFailurePolicy.FailFast,
                    ProcessJoinCancellationPolicy.ContinueRemaining,
                    ProcessJoinCompletionOrder.Unobservable,
                    ProcessJoinTieBreak.BranchIdentity),
                Edge("edge/join-cut", "cut/after-join")),
            new DurableCutProcessNode(
                new("cut/after-join"),
                Edge("edge/cut-return", "return")),
            new ReturnProcessNode(new("return"), Expr.Const("joined"))));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var first = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/required/start", ProcessActivationCause.Start),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, first.Disposition);

        var activation = Activation(
            "activation/required/resolve",
            ProcessActivationCause.Timer,
            StartedAtUtc.AddMinutes(1));
        var resolved = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            activation,
            RejectingHost.Instance);
        var replay = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            activation,
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, resolved.Disposition);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, resolved.State.Terminal.Kind);
        var fork = Assert.Single(resolved.State.Forks);
        Assert.True(fork.Resolved);
        Assert.True(fork.SelectedBranches.SequenceEqual(
            [new ExecutionNodeId("branch/alpha"), new ExecutionNodeId("branch/beta")]));
        Assert.Equal(
            ExecutionTokenDisposition.Waiting,
            fork.Branches.Single(static branch => branch.Branch == new ExecutionNodeId("branch/gamma")).Disposition);
        Assert.Contains(
            resolved.State.Tokens,
            static token => token.ForkMembership?.Branch == new ExecutionNodeId("branch/gamma")
                            && token.Disposition == ExecutionTokenDisposition.Waiting);
        Assert.True(fork.SelectedBranches.SequenceEqual(Assert.Single(replay.State.Forks).SelectedBranches));
        Assert.Equal(
            resolved.State.Tokens.Select(TokenProjection),
            replay.State.Tokens.Select(TokenProjection));
    }

    [Fact]
    public void BoundedFork_RetainsPendingBranchesAndParallelismAcrossRestore()
    {
        var dueAtUtc = StartedAtUtc.AddDays(1);
        var plan = Compile(Definition(
            "fork",
            ProcessRecoveryPolicy.ContinueAttempt,
            new ForkProcessNode(
                new("fork"),
                [
                    new(new("branch/alpha"), Edge("edge/fork-alpha", "timer/alpha")),
                    new(new("branch/beta"), Edge("edge/fork-beta", "timer/beta")),
                    new(new("branch/delta"), Edge("edge/fork-delta", "timer/delta")),
                    new(new("branch/gamma"), Edge("edge/fork-gamma", "timer/gamma"))
                ],
                new("join"),
                new(
                    maximumItems: 4,
                    maximumStartsPerActivation: 4,
                    maximumParallelism: 2),
                capacityDomains: []),
            Timer("timer/alpha", "edge/alpha-join", dueAtUtc),
            Timer("timer/beta", "edge/beta-join", dueAtUtc),
            Timer("timer/delta", "edge/delta-join", dueAtUtc),
            Timer("timer/gamma", "edge/gamma-join", dueAtUtc),
            AllJoin(),
            new ReturnProcessNode(new("return"), Expr.Const("joined"))));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));

        var first = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/bounded/start", ProcessActivationCause.Start),
            RejectingHost.Instance);
        var firstFork = Assert.Single(first.State.Forks);

        Assert.Equal(ProcessActivationDisposition.DurableCut, first.Disposition);
        Assert.Equal(2, firstFork.Branches.Count(static branch => IsInFlight(branch.Disposition)));
        Assert.Equal(2, firstFork.Branches.Count(static branch =>
            branch.Disposition == ExecutionTokenDisposition.Pending));
        Assert.Equal(2, firstFork.AdmissionOperatingPoint.MaximumParallelism);

        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var json = JsonSerializer.Serialize(first.State, options);
        var restored = Assert.IsType<ProcessContinuationState>(
            JsonSerializer.Deserialize<ProcessContinuationState>(json, options));
        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.True(ProcessContinuationValidator.Validate(plan, restored).IsValid);

        var second = ProcessReferenceInterpreter.Activate(
            plan,
            restored,
            Activation(
                "activation/bounded/continue",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(1)),
            RejectingHost.Instance);
        var secondFork = Assert.Single(second.State.Forks);

        Assert.Equal(ProcessActivationDisposition.DurableCut, second.Disposition);
        Assert.Equal(2, secondFork.Branches.Count(static branch => IsInFlight(branch.Disposition)));
        Assert.Equal(2, secondFork.Branches.Count(static branch =>
            branch.Disposition == ExecutionTokenDisposition.Pending));
        Assert.True(ProcessContinuationValidator.Validate(plan, second.State).IsValid);
    }

    [Fact]
    public void ForkStartBudget_AdvancesOneBranchPerDurableActivationWithoutSpinning()
    {
        var plan = Compile(Definition(
            "fork",
            ProcessRecoveryPolicy.ContinueAttempt,
            new ForkProcessNode(
                new("fork"),
                [
                    new(new("branch/alpha"), Edge("edge/fork-alpha", "join")),
                    new(new("branch/beta"), Edge("edge/fork-beta", "join")),
                    new(new("branch/delta"), Edge("edge/fork-delta", "join")),
                    new(new("branch/gamma"), Edge("edge/fork-gamma", "join"))
                ],
                new("join"),
                new(
                    maximumItems: 4,
                    maximumStartsPerActivation: 1,
                    maximumParallelism: 4),
                capacityDomains: []),
            AllJoin(),
            new ReturnProcessNode(new("return"), Expr.Const("joined"))));
        var state = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));

        for (var activationIndex = 0; activationIndex < 4; activationIndex++)
        {
            var decision = ProcessReferenceInterpreter.Activate(
                plan,
                state,
                Activation(
                    $"activation/start-budget/{activationIndex}",
                    activationIndex == 0 ? ProcessActivationCause.Start : ProcessActivationCause.Continue,
                    StartedAtUtc.AddMinutes(activationIndex)),
                RejectingHost.Instance);
            state = decision.State;
            var fork = Assert.Single(state.Forks);

            Assert.Equal(activationIndex + 1, fork.Branches.Count(static branch =>
                branch.Disposition == ExecutionTokenDisposition.Completed));
            Assert.True(ProcessContinuationValidator.Validate(plan, state).IsValid);
            if (activationIndex < 3)
            {
                Assert.Equal(ProcessActivationDisposition.DurableCut, decision.Disposition);
                Assert.Contains(decision.Evidence.Trace, static trace =>
                    trace.Kind == ProcessTraceEventKind.ForkAdmissionChanged
                    && trace.Detail is not null
                    && trace.Detail.StartsWith("activation-boundary:", StringComparison.Ordinal));
            }
            else
            {
                Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
            }
        }
    }

    [Fact]
    public void ForkCapacityDomains_ComposeWithTheForkWideParallelismLimit()
    {
        var dueAtUtc = StartedAtUtc.AddDays(1);
        var plan = Compile(Definition(
            "fork",
            ProcessRecoveryPolicy.ContinueAttempt,
            new ForkProcessNode(
                new("fork"),
                [
                    new(new("branch/alpha"), Edge("edge/fork-alpha", "timer/alpha"), "resource/a"),
                    new(new("branch/beta"), Edge("edge/fork-beta", "timer/beta"), "resource/a"),
                    new(new("branch/delta"), Edge("edge/fork-delta", "timer/delta"), "resource/b"),
                    new(new("branch/gamma"), Edge("edge/fork-gamma", "timer/gamma"), "resource/b")
                ],
                new("join"),
                new(
                    maximumItems: 4,
                    maximumStartsPerActivation: 4,
                    maximumParallelism: 4),
                [new("resource/a", 1), new("resource/b", 1)]),
            Timer("timer/alpha", "edge/alpha-join", dueAtUtc),
            Timer("timer/beta", "edge/beta-join", dueAtUtc),
            Timer("timer/delta", "edge/delta-join", dueAtUtc),
            Timer("timer/gamma", "edge/gamma-join", dueAtUtc),
            AllJoin(),
            new ReturnProcessNode(new("return"), Expr.Const("joined"))));

        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input")),
            Activation("activation/capacity/start", ProcessActivationCause.Start),
            RejectingHost.Instance);
        var fork = Assert.Single(decision.State.Forks);
        var admitted = fork.Branches
            .Where(static branch => IsInFlight(branch.Disposition))
            .Select(static branch => branch.Branch.Value)
            .ToArray();

        Assert.Equal(["branch/alpha", "branch/delta"], admitted);
        Assert.Equal(2, fork.Branches.Count(static branch =>
            branch.Disposition == ExecutionTokenDisposition.Pending));
        Assert.True(ProcessContinuationValidator.Validate(plan, decision.State).IsValid);
    }

    [Fact]
    public void AnyCancelRemaining_CancelsPendingBranchesBeforeTheyStart()
    {
        var plan = Compile(Definition(
            "fork",
            ProcessRecoveryPolicy.ContinueAttempt,
            new ForkProcessNode(
                new("fork"),
                [
                    new(new("branch/alpha"), Edge("edge/fork-alpha", "join")),
                    new(new("branch/beta"), Edge("edge/fork-beta", "join")),
                    new(new("branch/gamma"), Edge("edge/fork-gamma", "join"))
                ],
                new("join"),
                new(
                    maximumItems: 3,
                    maximumStartsPerActivation: 3,
                    maximumParallelism: 1),
                capacityDomains: []),
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
            new ReturnProcessNode(new("return"), Expr.Const("joined"))));

        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input")),
            Activation("activation/any-pending/start", ProcessActivationCause.Start),
            RejectingHost.Instance);
        var fork = Assert.Single(decision.State.Forks);

        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.Equal(
            ExecutionTokenDisposition.Completed,
            fork.Branches.Single(static branch => branch.Branch == new ExecutionNodeId("branch/alpha")).Disposition);
        foreach (var branchId in new[] { "branch/beta", "branch/gamma" })
        {
            var branch = fork.Branches.Single(candidate => candidate.Branch == new ExecutionNodeId(branchId));
            var token = decision.State.Tokens.Single(candidate => candidate.Id == branch.Token);
            Assert.Equal(ExecutionTokenDisposition.Cancelled, branch.Disposition);
            Assert.Equal(0, token.Step);
        }
        Assert.Single(decision.Evidence.Trace, static trace =>
            trace.Kind == ProcessTraceEventKind.ForkAdmissionChanged
            && trace.BranchOrClause is not null);
    }

    [Fact]
    public void AdaptiveAdmission_LowersParallelismByDrainingAndRetainsTheAppliedRevision()
    {
        var dueAtUtc = StartedAtUtc.AddDays(1);
        var plan = Compile(Definition(
            "fork",
            ProcessRecoveryPolicy.ContinueAttempt,
            new ForkProcessNode(
                new("fork"),
                [
                    new(new("branch/alpha"), Edge("edge/fork-alpha", "timer/alpha")),
                    new(new("branch/beta"), Edge("edge/fork-beta", "timer/beta")),
                    new(new("branch/gamma"), Edge("edge/fork-gamma", "timer/gamma"))
                ],
                new("join"),
                new(
                    maximumItems: 3,
                    maximumStartsPerActivation: 3,
                    maximumParallelism: 3,
                    minimumParallelism: 1),
                capacityDomains: []),
            Timer("timer/alpha", "edge/alpha-join", dueAtUtc),
            Timer("timer/beta", "edge/beta-join", dueAtUtc),
            Timer("timer/gamma", "edge/gamma-join", dueAtUtc),
            AllJoin(),
            new ReturnProcessNode(new("return"), Expr.Const("joined"))));
        var firstPoint = new ProcessAdmissionOperatingPoint(
            new("fork"),
            maximumParallelism: 2,
            revision: 1,
            authority: "cohesive.control/test",
            evidenceReference: "control/actuation/1");
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var first = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation(
                "activation/adaptive/start",
                ProcessActivationCause.Start,
                admissionOperatingPoints: [firstPoint]),
            RejectingHost.Instance);

        var loweredPoint = new ProcessAdmissionOperatingPoint(
            new("fork"),
            maximumParallelism: 1,
            revision: 2,
            authority: "cohesive.control/test",
            evidenceReference: "control/actuation/2");
        var lowered = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            Activation(
                "activation/adaptive/lower",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(1),
                admissionOperatingPoints: [loweredPoint]),
            RejectingHost.Instance);
        var loweredFork = Assert.Single(lowered.State.Forks);

        Assert.Equal(loweredPoint, loweredFork.AdmissionOperatingPoint);
        Assert.Equal(2, loweredFork.Branches.Count(static branch => IsInFlight(branch.Disposition)));
        Assert.Single(loweredFork.Branches, static branch =>
            branch.Disposition == ExecutionTokenDisposition.Pending);
        Assert.True(ProcessContinuationValidator.Validate(plan, lowered.State).IsValid);

        var stale = ProcessReferenceInterpreter.Activate(
            plan,
            lowered.State,
            Activation(
                "activation/adaptive/stale",
                ProcessActivationCause.Control,
                StartedAtUtc.AddMinutes(2),
                admissionOperatingPoints: [firstPoint]),
            RejectingHost.Instance);
        Assert.Equal(ProcessActivationDisposition.Rejected, stale.Disposition);
        Assert.Same(lowered.State, stale.State);

        var drained = ProcessReferenceInterpreter.Activate(
            plan,
            lowered.State,
            Activation(
                "activation/adaptive/drain",
                ProcessActivationCause.Timer,
                dueAtUtc.AddMinutes(1)),
            RejectingHost.Instance);
        var drainedFork = Assert.Single(drained.State.Forks);

        Assert.Equal(loweredPoint, drainedFork.AdmissionOperatingPoint);
        Assert.Equal(1, drainedFork.Branches.Count(static branch => IsInFlight(branch.Disposition)));
        Assert.DoesNotContain(drainedFork.Branches, static branch =>
            branch.Disposition == ExecutionTokenDisposition.Pending);
        Assert.True(ProcessContinuationValidator.Validate(plan, drained.State).IsValid);
    }

    [Fact]
    public void RepeatedWait_UnscopedInputPrefersActiveOccurrenceWhileExactOldTargetRemainsLate()
    {
        var eventDocument = InteractionDocument(
            "interaction/event/repeated-wait",
            new DomainEventContractDefinition(StringSchema("event/repeated-wait/v1")));
        DomainEventContractReference eventContract = new(Reference(eventDocument));
        var plan = Compile(
            Definition(
                "await",
                ProcessRecoveryPolicy.ContinueAttempt,
                new AwaitMatchProcessNode(
                    new("await"),
                    ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                    [
                        new ProcessAwaitInteractionClause(
                            new("clause/repeat"),
                            eventContract,
                            new(new("await.repeat"), StringContract),
                            requestObligation: null,
                            guard: null,
                            priority: 0,
                            new(Edge("edge/repeat", "await")))
                    ],
                    ProcessAwaitInputDisposition.Observe,
                    ProcessAwaitInputDisposition.Reject,
                    ProcessAwaitInputDisposition.ReusePriorDisposition,
                    ProcessAwaitMissingTargetDisposition.Reject,
                    TimeSpan.FromDays(1))),
            Catalog(eventDocument));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var registered = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/repeated-wait/register", ProcessActivationCause.Start),
            RejectingHost.Instance);
        var token = Assert.Single(registered.State.Tokens);
        var firstWait = Assert.Single(registered.State.Waits);
        var unscopedTarget = new ProcessTokenInteractionTarget(registered.State.Continuation, token.Id);
        var firstInput = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(
                plan,
                registered.State.Continuation,
                token.Id,
                "emission/repeated-wait/first"),
            eventContract,
            StringValue("first"));
        var repeated = ProcessReferenceInterpreter.Activate(
            plan,
            registered.State,
            Activation(
                "activation/repeated-wait/advance",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs: [new(unscopedTarget, firstInput)]),
            RejectingHost.Instance);
        var activeWait = Assert.Single(repeated.State.Waits, static wait => wait.Active);

        Assert.NotEqual(firstWait.RegistrationId, activeWait.RegistrationId);
        Assert.False(repeated.State.Waits.Single(wait => wait.RegistrationId == firstWait.RegistrationId).Active);

        var exactOldInput = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(
                plan,
                repeated.State.Continuation,
                token.Id,
                "emission/repeated-wait/exact-old"),
            eventContract,
            StringValue("old"));
        var exactOld = ProcessReferenceInterpreter.Activate(
            plan,
            repeated.State,
            Activation(
                "activation/repeated-wait/exact-old",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(2),
                inputs:
                [
                    new(
                        new ProcessTokenInteractionTarget(
                            repeated.State.Continuation,
                            token.Id,
                            firstWait.RegistrationId),
                        exactOldInput)
                ]),
            RejectingHost.Instance);
        var exactOldReceipt = Assert.Single(exactOld.InputAdmissions);

        Assert.Equal(ProcessInputAdmissionDisposition.Observed, exactOldReceipt.Disposition);
        Assert.Equal(firstWait.RegistrationId, exactOldReceipt.WaitRegistrationId);
        Assert.Equal(
            firstWait.RegistrationId,
            exactOldReceipt.Target.WaitRegistrationId);
        Assert.Equal(
            activeWait.RegistrationId,
            Assert.Single(exactOld.State.Waits, static wait => wait.Active).RegistrationId);

        var unscopedInput = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(
                plan,
                repeated.State.Continuation,
                token.Id,
                "emission/repeated-wait/unscoped"),
            eventContract,
            StringValue("current"));
        var preferred = ProcessReferenceInterpreter.Activate(
            plan,
            repeated.State,
            Activation(
                "activation/repeated-wait/unscoped",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(2),
                inputs: [new(unscopedTarget, unscopedInput)]),
            RejectingHost.Instance);
        var preferredReceipt = Assert.Single(preferred.InputAdmissions);

        Assert.Equal(ProcessInputAdmissionDisposition.Consumed, preferredReceipt.Disposition);
        Assert.Equal(activeWait.RegistrationId, preferredReceipt.WaitRegistrationId);
        Assert.Null(preferredReceipt.Target.WaitRegistrationId);
        Assert.NotEqual(
            activeWait.RegistrationId,
            Assert.Single(preferred.State.Waits, static wait => wait.Active).RegistrationId);
    }

    [Fact]
    public void RepeatedWait_UnscopedInputRejectsAmbiguousInactiveOccurrencesWithoutChoosingOne()
    {
        var eventDocument = InteractionDocument(
            "interaction/event/ambiguous-repeated-wait",
            new DomainEventContractDefinition(StringSchema("event/ambiguous-repeated-wait/v1")));
        DomainEventContractReference eventContract = new(Reference(eventDocument));
        var plan = Compile(
            Definition(
                "await",
                ProcessRecoveryPolicy.ContinueAttempt,
                new AwaitMatchProcessNode(
                    new("await"),
                    ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                    [
                        new ProcessAwaitInteractionClause(
                            new("clause/repeat"),
                            eventContract,
                            new(new("await.repeat"), StringContract),
                            requestObligation: null,
                            guard: null,
                            priority: 0,
                            new(Edge("edge/repeat", "await")))
                    ],
                    ProcessAwaitInputDisposition.Observe,
                    ProcessAwaitInputDisposition.Reject,
                    ProcessAwaitInputDisposition.ReusePriorDisposition,
                    ProcessAwaitMissingTargetDisposition.Reject,
                    TimeSpan.FromDays(1))),
            Catalog(eventDocument));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var registered = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/ambiguous-wait/register", ProcessActivationCause.Start),
            RejectingHost.Instance);
        var token = Assert.Single(registered.State.Tokens);
        var firstInput = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(
                plan,
                registered.State.Continuation,
                token.Id,
                "emission/ambiguous-wait/first"),
            eventContract,
            StringValue("first"));
        var repeated = ProcessReferenceInterpreter.Activate(
            plan,
            registered.State,
            Activation(
                "activation/ambiguous-wait/advance",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs:
                [
                    new(
                        new ProcessTokenInteractionTarget(registered.State.Continuation, token.Id),
                        firstInput)
                ]),
            RejectingHost.Instance);
        Assert.Equal(2, repeated.State.Waits.Length);
        Assert.Single(repeated.State.Waits, static wait => !wait.Active);
        Assert.Single(repeated.State.Waits, static wait => wait.Active);
        var inactiveWaits = repeated.State.Waits
            .Select(wait => wait.Active
                ? NewWait(
                    wait.RegistrationId,
                    wait.Token,
                    wait.Node,
                    wait.Occurrence,
                    wait.Kind,
                    wait.RegisteredAtUtc,
                    wait.Timers,
                    active: false,
                    wait.WinnerClause,
                    wait.WinnerInput,
                    wait.ObligationEmission)
                : wait)
            .ToImmutableArray();
        var ambiguousState = NewContinuation(
            repeated.State.Definition,
            repeated.State.Continuation,
            repeated.State.CompletedActivationCount,
            repeated.State.Tokens,
            repeated.State.Forks,
            repeated.State.Children,
            repeated.State.Partitions,
            repeated.State.Recurrences,
            inactiveWaits,
            repeated.State.BufferedInputs,
            repeated.State.InputReceipts,
            repeated.State.OutstandingRequests,
            repeated.State.Terminal);
        var ambiguousInput = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(
                plan,
                ambiguousState.Continuation,
                token.Id,
                "emission/ambiguous-wait/unscoped"),
            eventContract,
            StringValue("ambiguous"));
        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            ambiguousState,
            Activation(
                "activation/ambiguous-wait/unscoped",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(2),
                inputs:
                [
                    new(
                        new ProcessTokenInteractionTarget(ambiguousState.Continuation, token.Id),
                        ambiguousInput)
                ]),
            RejectingHost.Instance);

        var receipt = Assert.Single(decision.InputAdmissions);
        Assert.Equal(ProcessInputAdmissionDisposition.MissingTarget, receipt.Disposition);
        Assert.Equal(ProcessInputAdmissionReason.MissingTarget, receipt.Reason);
        Assert.Null(receipt.Target.WaitRegistrationId);
        Assert.Null(receipt.WaitRegistrationId);
        Assert.Contains(decision.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessExecutionDiagnosticCodes.InputTargetAmbiguous);
        Assert.Equal(
            inactiveWaits.Select(WaitProjection),
            decision.State.Waits.Select(WaitProjection));
        Assert.All(decision.State.Waits, static wait => Assert.False(wait.Active));
        Assert.DoesNotContain(
            decision.Evidence.Trace,
            static trace => trace.Kind == ProcessTraceEventKind.WaitResolved);
        var admissionTrace = Assert.Single(decision.Evidence.Trace, trace =>
            trace.Kind == ProcessTraceEventKind.InputAdmitted
            && trace.Emission == ambiguousInput.Context.EmissionId);
        Assert.Equal("ambiguous-wait-occurrence:MissingTarget", admissionTrace.Detail);
        Assert.Equal(ProcessInputAdmissionDisposition.MissingTarget, admissionTrace.InputDisposition);
        Assert.Equal(ProcessInputAdmissionReason.MissingTarget, admissionTrace.InputReason);
        Assert.Null(admissionTrace.WaitRegistrationId);
    }

    static void AssertConflict(ProcessActivationDecision decision, EmissionId emission)
    {
        Assert.Empty(decision.State.BufferedInputs);
        Assert.DoesNotContain(decision.State.InputReceipts, receipt => receipt.Emission == emission);
        Assert.Equal(
            ProcessInputAdmissionDisposition.IdentityConflict,
            Assert.Single(decision.InputAdmissions).Disposition);
        Assert.Equal(
            ProcessInputAdmissionReason.IdentityConflict,
            Assert.Single(decision.InputAdmissions).Reason);
        Assert.Contains(
            decision.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.InputIdentityConflict);
        Assert.True(Assert.Single(decision.State.Waits).Active);
    }

    static (TokenId Id, ExecutionNodeId Node, ExecutionTokenDisposition Disposition, long Step) TokenProjection(
        ProcessTokenState token) => (token.Id, token.Node, token.Disposition, token.Step);

    static (ProcessWaitRegistrationId Registration, TokenId Token, ExecutionNodeId Node, ProcessWaitKind Kind, bool Active) WaitProjection(
        ProcessWaitState wait) => (wait.RegistrationId, wait.Token, wait.Node, wait.Kind, wait.Active);

    static (
        int Sequence,
        ProcessTraceEventKind Kind,
        TokenId Token,
        ExecutionNodeId Node,
        ExecutionNodeId? BranchOrClause,
        EmissionId? Emission,
        string? Detail,
        InteractionEnvelopeContentFingerprint? EmissionFingerprint,
        long? OperationOccurrence,
        ProcessInputAdmissionDisposition? InputDisposition,
        ProcessInputAdmissionReason? InputReason,
        ProcessWaitRegistrationId? WaitRegistrationId) TraceProjection(ProcessTraceEvent item) =>
        (item.Sequence,
            item.Kind,
            item.Token,
            item.Node,
            item.BranchOrClause,
            item.Emission,
            item.Detail,
            item.EmissionFingerprint,
            item.OperationOccurrence,
            item.InputDisposition,
            item.InputReason,
            item.WaitRegistrationId);

    static CompiledProcessPlan Compile(
        CanonicalProcessDefinition definition,
        InteractionContractCatalog? contracts = null)
    {
        var document = ProcessDefinitionDocuments.Create(
            new("process/reference-interpreter-policy-tests"),
            new("revision/1"),
            definition,
            Provenance());
        var result = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext(interactionContracts: contracts));
        Assert.True(result.IsSuccessful, FormatDiagnostics(result.Validation));
        return Assert.IsType<CompiledProcessPlan>(result.Plan);
    }

    static CanonicalProcessDefinition Definition(
        string entry,
        ProcessRecoveryPolicy recovery,
        params ReadOnlySpan<CanonicalProcessNode> nodes) => new(
        StringContract,
        StringContract,
        new(entry),
        [.. nodes],
        recovery);

    static ProcessContinuationIdentity Continuation() => new(
        new("process-instance/reference-interpreter-policy-tests"),
        new("process-attempt/1"));

    static ProcessActivation Activation(
        string id,
        ProcessActivationCause cause,
        DateTimeOffset? observedAtUtc = null,
        ImmutableArray<ProcessActivationInput> inputs = default,
        ProcessCancellationIntent? cancellation = null,
        ImmutableArray<ProcessAdmissionOperatingPoint> admissionOperatingPoints = default) => new(
        new(id),
        cause,
        observedAtUtc ?? StartedAtUtc,
        new(
            new("authority/tests", "tenant/cohesive"),
            new("correlation/reference-interpreter-policy-tests"),
            new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            Provenance()),
        inputs,
        cancellation,
        admissionOperatingPoints);

    static TimerProcessNode Timer(
        string id,
        string edge,
        DateTimeOffset dueAtUtc) => new(
        new(id),
        Expr.Const(ObservationValue.FromDateTimeOffset(dueAtUtc)),
        Edge(edge, "join"));

    static JoinProcessNode AllJoin() => new(
        new("join"),
        new("fork"),
        new(
            ProcessJoinMode.All,
            requiredCount: 0,
            ProcessJoinFailurePolicy.FailFast,
            ProcessJoinCancellationPolicy.AwaitRemaining,
            ProcessJoinCompletionOrder.Unobservable,
            ProcessJoinTieBreak.BranchIdentity),
        Edge("edge/join-return", "return"));

    static bool IsInFlight(ExecutionTokenDisposition disposition) => disposition is
        ExecutionTokenDisposition.Ready
        or ExecutionTokenDisposition.Active
        or ExecutionTokenDisposition.Waiting;

    static InteractionEnvelopeContext IncomingContext(
        CompiledProcessPlan plan,
        ProcessContinuationIdentity continuation,
        TokenId token,
        string emission) => new(
        new(emission),
        new ProcessInteractionOrigin(
            plan.DefinitionReference,
            new("source/interaction"),
            continuation,
            new("activation/source"),
            token),
        new("correlation/reference-interpreter-policy-tests"),
        causationId: null,
        new("authority/tests", "tenant/cohesive"),
        new($"idempotency/{emission}"),
        ordering: null,
        new(
            InteractionDurabilityDemand.Durable,
            InteractionVisibilityDemand.AfterOriginCommit),
        Provenance());

    static ExecutionDefinitionDocument InteractionDocument(
        string id,
        InteractionContractDefinition definition) =>
        InteractionContractDocuments.Create(
            new(id),
            new("revision/1"),
            definition,
            Provenance());

    static InteractionContractCatalog Catalog(params ExecutionDefinitionDocument[] documents)
    {
        var validation = InteractionContractCatalog.TryCreate(documents, out var catalog);
        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        return Assert.IsType<InteractionContractCatalog>(catalog);
    }

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static InteractionValueSchema StringSchema(string revision) =>
        new(StringContract, new(revision));

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static PortableValue StringValue(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    static ExecutionProvenance Provenance() => new(
        new("process-reference-interpreter-policy-tests", "1"),
        new("tests/execution-kernel/process-reference-interpreter-policy"),
        DocumentOrigin.Generated);

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessContinuationState NewContinuation(
        ExecutionDefinitionReference definition,
        ProcessContinuationIdentity continuation,
        long completedActivationCount,
        ImmutableArray<ProcessTokenState> tokens,
        ImmutableArray<ProcessForkState> forks,
        ImmutableArray<ProcessChildState> children,
        ImmutableArray<ProcessPartitionState> partitions,
        ImmutableArray<ProcessRecurrenceState> recurrences,
        ImmutableArray<ProcessWaitState> waits,
        ImmutableArray<ProcessBufferedInput> bufferedInputs,
        ImmutableArray<ProcessInputReceipt> inputReceipts,
        ImmutableArray<ProcessOutstandingRequest> outstandingRequests,
        ExecutionTerminalOutcome terminal);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern ProcessWaitState NewWait(
        ProcessWaitRegistrationId registrationId,
        TokenId token,
        ExecutionNodeId node,
        long occurrence,
        ProcessWaitKind kind,
        DateTimeOffset registeredAtUtc,
        ImmutableArray<ProcessTimerState> timers,
        bool active,
        ExecutionNodeId? winnerClause,
        EmissionId? winnerInput,
        EmissionId? obligationEmission);

    sealed class RejectingHost : IProcessReferenceHost
    {
        public static RejectingHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation evaluation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }
}
