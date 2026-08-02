using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessReferenceInterpreterTests
{
    static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    static readonly ValueContract StringContract =
        new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void CompileAndActivate_ReturnProcess_CompletesWithPinnedDefinitionEvidence()
    {
        var plan = Compile(Definition(
            "return",
            [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))]));
        var state = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("accepted"));

        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            state,
            Activation("activation/start", ProcessActivationCause.Start),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, decision.State.Terminal.Kind);
        Assert.Equal(StringValue("accepted"), decision.State.Terminal.Detail?.Value);
        Assert.Equal(plan.DefinitionReference, decision.State.Definition);
        Assert.Equal(plan.DefinitionReference, decision.Evidence.Definition);
        Assert.Equal(1, decision.State.CompletedActivationCount);
        Assert.Collection(
            decision.State.Tokens,
            token =>
            {
                Assert.Equal(new ExecutionNodeId("return"), token.Node);
                Assert.Equal(ExecutionTokenDisposition.Completed, token.Disposition);
                Assert.Equal(1, token.Step);
            });
        Assert.Contains(
            decision.Evidence.Trace,
            static item => item.Kind == ProcessTraceEventKind.TerminalReached
                           && item.Node == new ExecutionNodeId("return"));
    }

    [Fact]
    public void DurableCut_EndsTheActivationAndResumesTheSameTokenOnContinue()
    {
        var plan = Compile(Definition(
            "cut",
            [
                new DurableCutProcessNode(new("cut"), Edge("edge/resume", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));

        var cut = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/cut", ProcessActivationCause.Start),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, cut.Disposition);
        Assert.Equal(new ExecutionNodeId("cut"), cut.Evidence.SafePointNode);
        var waiting = Assert.Single(cut.State.Tokens);
        Assert.Equal(ExecutionTokenDisposition.Waiting, waiting.Disposition);
        var wait = Assert.Single(cut.State.Waits);
        Assert.True(wait.Active);
        Assert.Equal(ProcessWaitKind.DurableCut, wait.Kind);

        var resumed = ProcessReferenceInterpreter.Activate(
            plan,
            cut.State,
            Activation("activation/continue", ProcessActivationCause.Continue, StartedAtUtc.AddMinutes(1)),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, resumed.Disposition);
        Assert.Equal(waiting.Id, Assert.Single(resumed.State.Tokens).Id);
        Assert.Equal(2, resumed.State.CompletedActivationCount);
        Assert.False(Assert.Single(resumed.State.Waits).Active);
        Assert.Equal(StringValue("done"), resumed.State.Terminal.Detail?.Value);
    }

    [Fact]
    public void ForkJoinAll_ProducesAStableMultiTokenContinuationAcrossReplay()
    {
        var plan = Compile(Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/zeta"), Edge("edge/fork-zeta", "join")),
                        new(new("branch/alpha"), Edge("edge/fork-alpha", "join"))
                    ],
                    new("join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    JoinAll(),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("joined"))
            ]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var activation = Activation("activation/fork", ProcessActivationCause.Start);

        var first = ProcessReferenceInterpreter.Activate(plan, initial, activation, RejectingHost.Instance);
        var replay = ProcessReferenceInterpreter.Activate(plan, initial, activation, RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, first.Disposition);
        Assert.Equal(3, first.State.Tokens.Length);
        Assert.All(first.State.Tokens, static token =>
            Assert.Equal(ExecutionTokenDisposition.Completed, token.Disposition));
        var fork = Assert.Single(first.State.Forks);
        Assert.True(fork.Resolved);
        Assert.Equal(
            [new ExecutionNodeId("branch/alpha"), new ExecutionNodeId("branch/zeta")],
            fork.SelectedBranches.AsEnumerable());
        Assert.Equal(
            first.State.Tokens.Select(static token => token.Id),
            replay.State.Tokens.Select(static token => token.Id));
        var replayFork = Assert.Single(replay.State.Forks);
        Assert.Equal(fork.RegistrationId, replayFork.RegistrationId);
        Assert.Equal(fork.Owner, replayFork.Owner);
        Assert.Equal(
            fork.Branches.Select(static branch => (branch.Branch, branch.Token, branch.Disposition)),
            replayFork.Branches.Select(static branch => (branch.Branch, branch.Token, branch.Disposition)));
        Assert.Equal(
            first.Evidence.Trace.Select(static item =>
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
                    item.WaitRegistrationId)),
            replay.Evidence.Trace.Select(static item =>
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
                    item.WaitRegistrationId)));
    }

    [Fact]
    public void ForkJoinAll_MergesTheBranchBindingUnionProvenByDefiniteFlow()
    {
        var relation = DefinitionReference("relation/branch-value", '4');
        ValueBindingId alpha = new("branch.alpha");
        ValueBindingId beta = new("branch.beta");
        var plan = Compile(
            Definition(
                "fork",
                [
                    new ForkProcessNode(
                        new("fork"),
                        [
                            new(new("branch/alpha"), Edge("edge/fork-alpha", "relation/alpha")),
                            new(new("branch/beta"), Edge("edge/fork-beta", "relation/beta"))
                        ],
                        new("join")),
                    new EvaluateRelationProcessNode(
                        new("relation/alpha"),
                        relation,
                        Expr.Const("alpha"),
                        new(
                            Edge("edge/alpha-join", "join"),
                            new(alpha, StringContract))),
                    new EvaluateRelationProcessNode(
                        new("relation/beta"),
                        relation,
                        Expr.Const("beta"),
                        new(
                            Edge("edge/beta-join", "join"),
                            new(beta, StringContract))),
                    new JoinProcessNode(
                        new("join"),
                        new("fork"),
                        JoinAll(),
                        Edge("edge/join-return", "return")),
                    new ReturnProcessNode(new("return"), Expr.BoundValue(alpha))
                ]),
            definitions:
            [new(relation, ProcessDefinitionLinkKind.RelationQuery, StringContract, StringContract)]);
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));

        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/fork-bindings", ProcessActivationCause.Start),
            new RecordingHost());

        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        var owner = decision.State.Tokens.Single(static token => token.ForkMembership is null);
        Assert.Equal(
            new Dictionary<ValueBindingId, PortableValue>
            {
                [ProcessBindingIds.Input] = StringValue("input"),
                [alpha] = StringValue("alpha"),
                [beta] = StringValue("beta")
            },
            owner.Bindings.ToDictionary(static binding => binding.Binding, static binding => binding.Value));
        Assert.Equal(StringValue("alpha"), decision.State.Terminal.Detail?.Value);
    }

    [Fact]
    public void Fork_FirstDurableBoundaryStopsTheWholeActivationAndPreservesOtherReadyTokens()
    {
        var plan = Compile(Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/alpha"), Edge("edge/fork-alpha", "cut/alpha")),
                        new(new("branch/beta"), Edge("edge/fork-beta", "cut/beta"))
                    ],
                    new("join")),
                new DurableCutProcessNode(new("cut/alpha"), Edge("edge/alpha-join", "join")),
                new DurableCutProcessNode(new("cut/beta"), Edge("edge/beta-join", "join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    JoinAll(),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("joined"))
            ]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));

        var cut = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/fork-cut", ProcessActivationCause.Start),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, cut.Disposition);
        Assert.True(
            cut.Evidence.SafePointNode == new ExecutionNodeId("cut/alpha")
            || cut.Evidence.SafePointNode == new ExecutionNodeId("cut/beta"));
        Assert.Equal(3, cut.State.Tokens.Length);
        Assert.Equal(
            1,
            cut.State.Tokens.Count(static token => token.Disposition == ExecutionTokenDisposition.Ready));
        Assert.Equal(
            2,
            cut.State.Tokens.Count(static token => token.Disposition == ExecutionTokenDisposition.Waiting));
        var wait = Assert.Single(cut.State.Waits);
        Assert.True(wait.Active);
        Assert.Equal(ProcessWaitKind.DurableCut, wait.Kind);
        Assert.Equal(cut.Evidence.SafePointNode, wait.Node);
        var fork = Assert.Single(cut.State.Forks);
        Assert.All(
            fork.Branches,
            branch => Assert.Equal(
                cut.State.Tokens.Single(token => token.Id == branch.Token).Disposition,
                branch.Disposition));

        var cancelled = ProcessReferenceInterpreter.Activate(
            plan,
            cut.State,
            Activation(
                "activation/fork-cancel",
                ProcessActivationCause.Control,
                StartedAtUtc.AddMinutes(1),
                new ProcessCancellationIntent(
                    cut.State.Continuation.ProcessAttemptId,
                    new("operator.cancel"))),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Cancelled, cancelled.Disposition);
        Assert.All(cancelled.State.Tokens, static token =>
            Assert.Equal(ExecutionTokenDisposition.Cancelled, token.Disposition));
        Assert.All(cancelled.State.Waits, static persistedWait => Assert.False(persistedWait.Active));
        Assert.All(Assert.Single(cancelled.State.Forks).Branches, static branch =>
            Assert.Equal(ExecutionTokenDisposition.Cancelled, branch.Disposition));
    }

    [Fact]
    public void TransitionAndRelationOperations_PreserveExactSubjectsWithoutOwningAggregateState()
    {
        var firstTransition = DefinitionReference("transition/account/first", '1');
        var secondTransition = DefinitionReference("transition/account/second", '2');
        var relation = DefinitionReference("relation/account-summary", '3');
        var plan = Compile(
            Definition(
                "transition/first",
                [
                    new InvokeTransitionProcessNode(
                        new("transition/first"),
                        firstTransition,
                        Expr.Const("account/first"),
                        Expr.BoundValue(ProcessBindingIds.Input),
                        new(Edge("edge/first-second", "transition/second"))),
                    new InvokeTransitionProcessNode(
                        new("transition/second"),
                        secondTransition,
                        Expr.Const("account/second"),
                        Expr.BoundValue(ProcessBindingIds.Input),
                        new(Edge("edge/second-query", "relation/query"))),
                    new EvaluateRelationProcessNode(
                        new("relation/query"),
                        relation,
                        Expr.BoundValue(ProcessBindingIds.Input),
                        new(Edge("edge/query-return", "return"))),
                    new ReturnProcessNode(new("return"), Expr.Const("coordinated"))
                ]),
            definitions:
            [
                new(firstTransition, ProcessDefinitionLinkKind.Transition, StringContract, StringContract),
                new(secondTransition, ProcessDefinitionLinkKind.Transition, StringContract, StringContract),
                new(relation, ProcessDefinitionLinkKind.RelationQuery, StringContract, StringContract)
            ]);
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("command"));
        var host = new RecordingHost();
        var activation = Activation("activation/operations", ProcessActivationCause.Start);

        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            activation,
            host);

        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.Equal(
            [firstTransition, secondTransition],
            host.Transitions.Select(static invocation => invocation.Definition));
        Assert.Equal(
            [ObservationValue.FromString("account/first"), ObservationValue.FromString("account/second")],
            host.Transitions.Select(static invocation => invocation.Subject.Value));
        var query = Assert.Single(host.Relations);
        Assert.Equal(relation, query.Definition);
        Assert.All(host.Transitions, static invocation => Assert.Equal(StringValue("command"), invocation.Input));
        Assert.Equal(StringValue("command"), query.Input);
        Assert.Equal([0L, 1L], host.Transitions.Select(static invocation => invocation.Occurrence));
        Assert.Equal(2L, query.Occurrence);
        Assert.All(host.Transitions, invocation =>
        {
            Assert.Equal(activation.ObservedAtUtc, invocation.ObservedAtUtc);
            Assert.Same(activation.Context, invocation.Context);
        });
        Assert.Same(activation.Context, query.Context);
        Assert.Collection(
            Assert.Single(decision.State.Tokens).Bindings,
            binding =>
            {
                Assert.Equal(ProcessBindingIds.Input, binding.Binding);
                Assert.Equal(StringValue("command"), binding.Value);
            });
        Assert.Equal(StringValue("coordinated"), decision.State.Terminal.Detail?.Value);
    }

    [Fact]
    public void HostResultContractViolationProducesTheDedicatedStructuredDiagnostic()
    {
        var relation = DefinitionReference("relation/invalid-result", '5');
        ValueBindingId result = new("relation.result");
        var plan = Compile(
            Definition(
                "relation",
                [
                    new EvaluateRelationProcessNode(
                        new("relation"),
                        relation,
                        Expr.BoundValue(ProcessBindingIds.Input),
                        new(
                            Edge("edge/relation-return", "return"),
                            new(result, StringContract))),
                    new ReturnProcessNode(new("return"), Expr.BoundValue(result))
                ]),
            definitions:
            [new(relation, ProcessDefinitionLinkKind.RelationQuery, StringContract, StringContract)]);
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));

        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/invalid-result", ProcessActivationCause.Start),
            InvalidResultHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Failed, decision.Disposition);
        Assert.Contains(
            decision.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.ResultContractViolated);
    }

    [Fact]
    public void Timer_RemainsQuiescentBeforeItsDeadlineAndResumesWhenDue()
    {
        var dueAtUtc = StartedAtUtc.AddMinutes(5);
        var plan = Compile(Definition(
            "timer",
            [
                new TimerProcessNode(
                    new("timer"),
                    Expr.Const(ObservationValue.FromDateTimeOffset(dueAtUtc)),
                    Edge("edge/timer-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("due"))
            ]));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));

        var registered = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/timer-register", ProcessActivationCause.Start),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, registered.Disposition);
        var wait = Assert.Single(registered.State.Waits);
        Assert.True(wait.Active);
        Assert.Equal(ProcessWaitKind.Timer, wait.Kind);
        Assert.Equal(dueAtUtc, Assert.Single(wait.Timers).DueAtUtc);

        var early = ProcessReferenceInterpreter.Activate(
            plan,
            registered.State,
            Activation(
                "activation/timer-early",
                ProcessActivationCause.Timer,
                dueAtUtc.AddTicks(-1)),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Quiescent, early.Disposition);
        Assert.True(Assert.Single(early.State.Waits).Active);
        Assert.Equal(ExecutionTokenDisposition.Waiting, Assert.Single(early.State.Tokens).Disposition);

        var due = ProcessReferenceInterpreter.Activate(
            plan,
            early.State,
            Activation("activation/timer-due", ProcessActivationCause.Timer, dueAtUtc),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, due.Disposition);
        Assert.False(Assert.Single(due.State.Waits).Active);
        Assert.Equal(StringValue("due"), due.State.Terminal.Detail?.Value);
        Assert.Contains(
            due.Evidence.Trace,
            static item => item.Kind == ProcessTraceEventKind.WaitResolved
                           && item.Node == new ExecutionNodeId("timer"));
    }

    [Fact]
    public void AwaitMatch_EarlyInputsChooseOneWinnerDeterministicallyAndRemainDeduplicated()
    {
        var lowDocument = InteractionDocument(
            "interaction/event/low-priority",
            new DomainEventContractDefinition(StringSchema("low/v1")));
        var highDocument = InteractionDocument(
            "interaction/event/high-priority",
            new DomainEventContractDefinition(StringSchema("high/v1")));
        var contracts = Catalog(lowDocument, highDocument);
        DomainEventContractReference lowContract = new(Reference(lowDocument));
        DomainEventContractReference highContract = new(Reference(highDocument));
        var plan = Compile(
            Definition(
                "await",
                [
                    new AwaitMatchProcessNode(
                        new("await"),
                        ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                        [
                            new ProcessAwaitInteractionClause(
                                new("clause/low"),
                                lowContract,
                                new(new("await.low"), StringContract),
                                requestObligation: null,
                                guard: null,
                                priority: 1,
                                new(Edge("edge/low", "return-low"))),
                            new ProcessAwaitInteractionClause(
                                new("clause/high"),
                                highContract,
                                new(new("await.high"), StringContract),
                                requestObligation: null,
                                guard: null,
                                priority: 10,
                                new(Edge("edge/high", "return-high")))
                        ],
                        ProcessAwaitInputDisposition.Observe,
                        ProcessAwaitInputDisposition.Reject,
                        ProcessAwaitInputDisposition.ReusePriorDisposition,
                        ProcessAwaitMissingTargetDisposition.DeadLetter,
                        TimeSpan.FromDays(7)),
                    new ReturnProcessNode(new("return-high"), Expr.Const("high")),
                    new ReturnProcessNode(new("return-low"), Expr.Const("low"))
                ]),
            contracts);
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var target = new ProcessTokenInteractionTarget(
            initial.Continuation,
            Assert.Single(initial.Tokens).Id);
        var low = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(plan, initial.Continuation, target.Token, "emission/a-low"),
            lowContract,
            StringValue("low-payload"));
        var high = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(plan, initial.Continuation, target.Token, "emission/z-high"),
            highContract,
            StringValue("high-payload"));
        var activation = Activation(
            "activation/await",
            ProcessActivationCause.Interaction,
            inputs:
            [
                new(target, high),
                new(target, low)
            ]);

        var first = ProcessReferenceInterpreter.Activate(plan, initial, activation, RejectingHost.Instance);
        var replay = ProcessReferenceInterpreter.Activate(plan, initial, activation, RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, first.Disposition);
        Assert.Empty(first.State.BufferedInputs);
        var wait = Assert.Single(first.State.Waits);
        Assert.False(wait.Active);
        Assert.Equal(new ExecutionNodeId("clause/high"), wait.WinnerClause);
        Assert.Equal(high.Context.EmissionId, wait.WinnerInput);
        var superseded = first.State.InputReceipts.Single(receipt => receipt.Emission == low.Context.EmissionId);
        Assert.Equal(ProcessInputAdmissionDisposition.Observed, superseded.Disposition);
        Assert.Equal(ProcessInputAdmissionReason.Superseded, superseded.Reason);
        var consumed = first.State.InputReceipts.Single(receipt => receipt.Emission == high.Context.EmissionId);
        Assert.Equal(ProcessInputAdmissionDisposition.Consumed, consumed.Disposition);
        Assert.Equal(ProcessInputAdmissionReason.Consumed, consumed.Reason);
        Assert.Equal(2, first.InputAdmissions.Length);
        Assert.Equal(
            [
                (low.Context.EmissionId, ProcessInputAdmissionDisposition.Observed, ProcessInputAdmissionReason.Superseded),
                (high.Context.EmissionId, ProcessInputAdmissionDisposition.Consumed, ProcessInputAdmissionReason.Consumed)
            ],
            first.InputAdmissions
                .OrderBy(static receipt => receipt.Emission.Value, StringComparer.Ordinal)
                .Select(static receipt => (receipt.Emission, receipt.Disposition, receipt.Reason)));
        Assert.Equal(new ExecutionNodeId("return-high"), Assert.Single(first.State.Tokens).Node);
        var replayWait = Assert.Single(replay.State.Waits);
        Assert.Equal(wait.RegistrationId, replayWait.RegistrationId);
        Assert.Equal(wait.WinnerClause, replayWait.WinnerClause);
        Assert.Equal(wait.WinnerInput, replayWait.WinnerInput);
        Assert.Equal(
            first.State.InputReceipts.Select(static receipt => (receipt.Emission, receipt.Disposition, receipt.Reason)),
            replay.State.InputReceipts.Select(static receipt => (receipt.Emission, receipt.Disposition, receipt.Reason)));

        var conflictingPayload = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            Activation(
                "activation/await-conflicting-payload",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs:
                [
                    new(
                        target,
                        new DomainEventEnvelope(
                            InteractionEnvelope.CurrentSchemaVersion,
                            high.Context,
                            high.Contract,
                            StringValue("different-payload")))
                ]),
            RejectingHost.Instance);

        Assert.Equal(
            ProcessInputAdmissionDisposition.IdentityConflict,
            Assert.Single(conflictingPayload.InputAdmissions).Disposition);
        Assert.Equal(
            ProcessInputAdmissionReason.IdentityConflict,
            Assert.Single(conflictingPayload.InputAdmissions).Reason);
        Assert.Contains(
            conflictingPayload.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.InputIdentityConflict);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            conflictingPayload.State.InputReceipts.Single(
                receipt => receipt.Emission == high.Context.EmissionId).Disposition);

        var conflictingTarget = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            Activation(
                "activation/await-conflicting-target",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs:
                [
                    new(
                        new ProcessTokenInteractionTarget(
                            first.State.Continuation,
                            new("process-token/conflicting")),
                        high)
                ]),
            RejectingHost.Instance);

        Assert.Equal(
            ProcessInputAdmissionDisposition.IdentityConflict,
            Assert.Single(conflictingTarget.InputAdmissions).Disposition);
        Assert.Equal(
            ProcessInputAdmissionReason.IdentityConflict,
            Assert.Single(conflictingTarget.InputAdmissions).Reason);

        var duplicate = ProcessReferenceInterpreter.Activate(
            plan,
            first.State,
            Activation(
                "activation/await-duplicate",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs: [new(target, high)]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, duplicate.Disposition);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            Assert.Single(duplicate.InputAdmissions).Disposition);
        Assert.Equal(ProcessInputAdmissionReason.Duplicate, Assert.Single(duplicate.InputAdmissions).Reason);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            duplicate.State.InputReceipts.Single(receipt => receipt.Emission == high.Context.EmissionId).Disposition);
        Assert.Equal(StringValue("high"), duplicate.State.Terminal.Detail?.Value);
    }

    [Fact]
    public void AwaitMatch_AppliesStaleAndMissingTargetPoliciesWithoutStrandingInputs()
    {
        var awaitedDocument = InteractionDocument(
            "interaction/event/awaited",
            new DomainEventContractDefinition(StringSchema("awaited/v1")));
        var unrelatedDocument = InteractionDocument(
            "interaction/event/unrelated",
            new DomainEventContractDefinition(StringSchema("unrelated/v1")));
        DomainEventContractReference awaitedContract = new(Reference(awaitedDocument));
        DomainEventContractReference unrelatedContract = new(Reference(unrelatedDocument));
        var plan = Compile(
            Definition(
                "await",
                [
                    new AwaitMatchProcessNode(
                        new("await"),
                        ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                        [
                            new ProcessAwaitInteractionClause(
                                new("clause/awaited"),
                                awaitedContract,
                                new(new("await.input"), StringContract),
                                requestObligation: null,
                                Expr.Const(false),
                                priority: 0,
                                new(Edge("edge/awaited-return", "return")))
                        ],
                        ProcessAwaitInputDisposition.Reject,
                        ProcessAwaitInputDisposition.Observe,
                        ProcessAwaitInputDisposition.ReusePriorDisposition,
                        ProcessAwaitMissingTargetDisposition.DeadLetter,
                        TimeSpan.FromDays(7)),
                    new ReturnProcessNode(new("return"), Expr.Const("unexpected"))
                ]),
            Catalog(awaitedDocument, unrelatedDocument));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var registered = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/await-register", ProcessActivationCause.Start),
            RejectingHost.Instance);
        var token = Assert.Single(registered.State.Tokens);
        var target = new ProcessTokenInteractionTarget(registered.State.Continuation, token.Id);
        var awaited = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(plan, registered.State.Continuation, token.Id, "emission/a-awaited"),
            awaitedContract,
            StringValue("guard-false"));
        var unrelated = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(plan, registered.State.Continuation, token.Id, "emission/z-unrelated"),
            unrelatedContract,
            StringValue("no-clause"));

        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            registered.State,
            Activation(
                "activation/await-policies",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs: [new(target, unrelated), new(target, awaited)]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Quiescent, decision.Disposition);
        Assert.True(Assert.Single(decision.State.Waits).Active);
        Assert.Empty(decision.State.BufferedInputs);
        Assert.Equal(2, decision.InputAdmissions.Length);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Observed,
            decision.InputAdmissions.Single(receipt => receipt.Emission == awaited.Context.EmissionId).Disposition);
        Assert.Equal(
            ProcessInputAdmissionReason.Stale,
            decision.InputAdmissions.Single(receipt => receipt.Emission == awaited.Context.EmissionId).Reason);
        Assert.Equal(
            ProcessInputAdmissionDisposition.DeadLettered,
            decision.InputAdmissions.Single(receipt => receipt.Emission == unrelated.Context.EmissionId).Disposition);
        Assert.Equal(
            ProcessInputAdmissionReason.MissingTarget,
            decision.InputAdmissions.Single(receipt => receipt.Emission == unrelated.Context.EmissionId).Reason);
    }

    [Fact]
    public void AwaitMatch_GuardEvaluationFailureReturnsStructuredFailedState()
    {
        ValueContract integer = new(new ScalarTypeRef(ScalarTypeKind.Int64));
        var eventDocument = InteractionDocument(
            "interaction/event/division-guard",
            new DomainEventContractDefinition(new(integer, new("division-guard/v1"))));
        DomainEventContractReference eventContract = new(Reference(eventDocument));
        ValueBindingId inputBinding = new("await.divisor");
        var plan = Compile(
            Definition(
                "await",
                [
                    new AwaitMatchProcessNode(
                        new("await"),
                        ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                        [
                            new ProcessAwaitInteractionClause(
                                new("clause/division"),
                                eventContract,
                                new(inputBinding, integer),
                                requestObligation: null,
                                Expr.Eq(
                                    Expr.Div(
                                        Expr.Const(ObservationValue.FromInt64(1)),
                                        Expr.BoundValue(inputBinding)),
                                    Expr.Const(ObservationValue.FromInt64(1))),
                                priority: 0,
                                new(Edge("edge/return", "return")))
                        ],
                        ProcessAwaitInputDisposition.Reject,
                        ProcessAwaitInputDisposition.Reject,
                        ProcessAwaitInputDisposition.ReusePriorDisposition,
                        ProcessAwaitMissingTargetDisposition.Reject,
                        TimeSpan.FromDays(1)),
                    new ReturnProcessNode(new("return"), Expr.Const("unexpected"))
                ]),
            Catalog(eventDocument));
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("input"));
        var registered = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/guard-register", ProcessActivationCause.Start),
            RejectingHost.Instance);
        var token = Assert.Single(registered.State.Tokens);
        var target = new ProcessTokenInteractionTarget(registered.State.Continuation, token.Id);
        var input = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(plan, registered.State.Continuation, token.Id, "emission/null-guard"),
            eventContract,
            PortableValue.Concrete(integer, ObservationValue.FromInt64(0)));

        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            registered.State,
            Activation(
                "activation/guard-failure",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs: [new(target, input)]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Failed, decision.Disposition);
        Assert.Equal(ExecutionTerminalOutcomeKind.Failed, decision.State.Terminal.Kind);
        Assert.Equal(ExecutionTokenDisposition.Failed, Assert.Single(decision.State.Tokens).Disposition);
        Assert.False(Assert.Single(decision.State.Waits).Active);
        Assert.Contains(
            decision.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.ExpressionFailed);
    }

    [Fact]
    public void Request_EmitsOneStableObligationAndAnExactReplyResumesItsOutcomeBranch()
    {
        var requestDefinition = new RequestContractDefinition(
            StringSchema("request/v1"),
            new RequestResponseObligation(
                [
                    new RequestResultDefinition(new("accepted"), StringSchema("accepted/v1")),
                    new RequestFailureDefinition(new("rejected"), StringSchema("rejected/v1"))
                ],
                RequestOptionalTerminalSemantics.Unsupported,
                RequestOptionalTerminalSemantics.Unsupported,
                RequestResultDisposition.Observe,
                RequestResultDisposition.Reject,
                RequestResultDisposition.ReusePriorDisposition,
                RequestRetrySemantics.StableIdentity,
                RequestResolutionSemantics.Reconcile,
                RequestResolutionSemantics.Escalate,
                TimeSpan.FromDays(30)));
        var requestDocument = InteractionDocument(
            "interaction/request/review",
            requestDefinition);
        RequestContractReference requestContract = new(Reference(requestDocument));
        var replyDocument = InteractionDocument(
            "interaction/reply/review-accepted",
            new ReplyContractDefinition(requestContract, new("accepted")));
        ReplyContractReference replyContract = new(Reference(replyDocument));
        var unrelatedRequestDocument = InteractionDocument(
            "interaction/request/unrelated",
            requestDefinition);
        RequestContractReference unrelatedRequestContract = new(Reference(unrelatedRequestDocument));
        var unrelatedReplyDocument = InteractionDocument(
            "interaction/reply/unrelated-accepted",
            new ReplyContractDefinition(unrelatedRequestContract, new("accepted")));
        ReplyContractReference unrelatedReplyContract = new(Reference(unrelatedReplyDocument));
        var contracts = Catalog(
            requestDocument,
            replyDocument,
            unrelatedRequestDocument,
            unrelatedReplyDocument);
        ValueBindingId accepted = new("request.accepted");
        ValueBindingId rejected = new("request.rejected");
        var plan = Compile(
            Definition(
                "request",
                [
                    new RequestProcessNode(
                        new("request"),
                        requestContract,
                        Expr.BoundValue(ProcessBindingIds.Input),
                        [
                            new(
                                new("outcome/accepted"),
                                new("accepted"),
                                new(
                                    Edge("edge/accepted", "return"),
                                    new(accepted, StringContract))),
                            new(
                                new("outcome/rejected"),
                                new("rejected"),
                                new(
                                    Edge("edge/rejected", "fail"),
                                    new(rejected, StringContract)))
                        ]),
                    new ReturnProcessNode(new("return"), Expr.BoundValue(accepted)),
                    new FailProcessNode(new("fail"), Expr.BoundValue(rejected))
                ]),
            contracts);
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("review-case/1"));

        var requestActivation = Activation("activation/request", ProcessActivationCause.Start);
        var requested = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            requestActivation,
            RejectingHost.Instance);
        var requestReplay = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            requestActivation,
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, requested.Disposition);
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(requested.Emissions));
        var token = Assert.Single(requested.State.Tokens);
        var requestWait = Assert.Single(requested.State.Waits);
        Assert.Equal(
            new ProcessTokenInteractionTarget(
                requested.State.Continuation,
                token.Id,
                requestWait.RegistrationId),
            request.ResponseTarget);
        Assert.Equal(request.Context.EmissionId, Assert.Single(requested.State.OutstandingRequests).Emission);
        var replayRequest = Assert.IsType<RequestEnvelope>(Assert.Single(requestReplay.Emissions));
        Assert.Equal(request.Context.EmissionId, replayRequest.Context.EmissionId);
        Assert.Equal(request.Context.IdempotencyKey, replayRequest.Context.IdempotencyKey);
        Assert.True(requestWait.Active);

        var cancelled = ProcessReferenceInterpreter.Activate(
            plan,
            requested.State,
            Activation(
                "activation/request-cancel",
                ProcessActivationCause.Control,
                StartedAtUtc.AddSeconds(30),
                new ProcessCancellationIntent(
                    requested.State.Continuation.ProcessAttemptId,
                    new("operator.cancel"))),
            RejectingHost.Instance);
        Assert.Equal(ProcessActivationDisposition.Cancelled, cancelled.Disposition);
        Assert.Empty(cancelled.State.OutstandingRequests);
        Assert.False(Assert.Single(cancelled.State.Waits).Active);

        var reply = new ReplyEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(
                plan,
                requested.State.Continuation,
                token.Id,
                "emission/reply-accepted",
                request.Context.EmissionId),
            replyContract,
            request.Context.EmissionId,
            new RequestResultOutcome(new("accepted"), StringValue("accepted-by-reviewer")));
        var invalidReply = new ReplyEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(
                plan,
                requested.State.Continuation,
                token.Id,
                "emission/a-reply-invalid",
                request.Context.EmissionId),
            unrelatedReplyContract,
            request.Context.EmissionId,
            new RequestResultOutcome(new("accepted"), StringValue("wrong-request")));
        var completed = ProcessReferenceInterpreter.Activate(
            plan,
            requested.State,
            Activation(
                "activation/reply",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                inputs:
                [
                    new(
                        new ProcessTokenInteractionTarget(requested.State.Continuation, token.Id),
                        invalidReply),
                    new(
                        new ProcessTokenInteractionTarget(requested.State.Continuation, token.Id),
                        reply)
                ]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, completed.Disposition);
        Assert.Empty(completed.State.OutstandingRequests);
        var completedWait = Assert.Single(completed.State.Waits);
        Assert.False(completedWait.Active);
        Assert.Equal(new ExecutionNodeId("outcome/accepted"), completedWait.WinnerClause);
        Assert.Equal(reply.Context.EmissionId, completedWait.WinnerInput);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            completed.State.InputReceipts.Single(receipt => receipt.Emission == reply.Context.EmissionId).Disposition);
        Assert.Equal(2, completed.InputAdmissions.Length);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Rejected,
            completed.InputAdmissions.Single(
                receipt => receipt.Emission == invalidReply.Context.EmissionId).Disposition);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            completed.InputAdmissions.Single(receipt => receipt.Emission == reply.Context.EmissionId).Disposition);
        Assert.Equal(StringValue("accepted-by-reviewer"), completed.State.Terminal.Detail?.Value);
    }

    [Fact]
    public void CancellationIntent_IsAppliedAtTheActivationSafePointBeforeNodeExecution()
    {
        var plan = Compile(Definition(
            "return",
            [new ReturnProcessNode(new("return"), Expr.Const("must-not-run"))]));
        var continuation = Continuation();
        var initial = ProcessReferenceInterpreter.Create(plan, continuation, StringValue("input"));
        var cancellation = new ProcessCancellationIntent(
            continuation.ProcessAttemptId,
            new("operator.cancel"));

        var decision = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation(
                "activation/cancel",
                ProcessActivationCause.Control,
                cancellation: cancellation),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Cancelled, decision.Disposition);
        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, decision.State.Terminal.Kind);
        Assert.Equal(ExecutionTokenDisposition.Cancelled, Assert.Single(decision.State.Tokens).Disposition);
        Assert.DoesNotContain(
            decision.Evidence.Trace,
            static item => item.Kind == ProcessTraceEventKind.NodeEntered);
        Assert.Contains(
            decision.Evidence.Trace,
            static item => item.Kind == ProcessTraceEventKind.CancellationApplied);
    }

    static CompiledProcessPlan Compile(
        CanonicalProcessDefinition definition,
        InteractionContractCatalog? contracts = null,
        ImmutableArray<ProcessDefinitionLink> definitions = default)
    {
        var document = ProcessDefinitionDocuments.Create(
            new("process/reference-interpreter-tests"),
            new("revision/1"),
            definition,
            Provenance());
        var result = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext(
                definitions: definitions.IsDefault ? null : definitions,
                interactionContracts: contracts));
        Assert.True(result.IsSuccessful, FormatDiagnostics(result.Validation));
        return Assert.IsType<CompiledProcessPlan>(result.Plan);
    }

    static CanonicalProcessDefinition Definition(
        string entry,
        params ReadOnlySpan<CanonicalProcessNode> nodes) => new(
        StringContract,
        StringContract,
        new(entry),
        [.. nodes],
        ProcessRecoveryPolicy.ContinueAttempt);

    static ProcessJoinPolicy JoinAll() => new(
        ProcessJoinMode.All,
        requiredCount: 0,
        ProcessJoinFailurePolicy.FailFast,
        ProcessJoinCancellationPolicy.AwaitRemaining,
        ProcessJoinCompletionOrder.Unobservable,
        ProcessJoinTieBreak.BranchIdentity);

    static ProcessContinuationIdentity Continuation() => new(
        new("process-instance/reference-interpreter-tests"),
        new("process-attempt/1"));

    static ProcessActivation Activation(
        string id,
        ProcessActivationCause cause,
        DateTimeOffset? observedAtUtc = null,
        ProcessCancellationIntent? cancellation = null,
        ImmutableArray<ProcessActivationInput> inputs = default) => new(
        new(id),
        cause,
        observedAtUtc ?? StartedAtUtc,
        new(
            new("authority/tests", "tenant/cohesive"),
            new("correlation/reference-interpreter-tests"),
            new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            Provenance()),
        inputs,
        cancellation: cancellation);

    static InteractionEnvelopeContext IncomingContext(
        CompiledProcessPlan plan,
        ProcessContinuationIdentity continuation,
        TokenId token,
        string emission,
        EmissionId? causation = null) => new(
        new(emission),
        new ProcessInteractionOrigin(
            plan.DefinitionReference,
            new("source/interaction"),
            continuation,
            new("activation/source"),
            token),
        new("correlation/reference-interpreter-tests"),
        causation,
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

    static ExecutionDefinitionReference DefinitionReference(
        string definition,
        char fingerprintDigit) => new(
        new(definition),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprintDigit, 64)));

    static InteractionValueSchema StringSchema(string revision) =>
        new(StringContract, new(revision));

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static PortableValue StringValue(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    static PortableValue BooleanValue(bool value) => PortableValue.Concrete(
        new(new ScalarTypeRef(ScalarTypeKind.Bool)),
        ObservationValue.FromBool(value));

    static ExecutionProvenance Provenance() => new(
        new("process-reference-interpreter-tests", "1"),
        new("tests/execution-kernel/process-reference-interpreter"),
        DocumentOrigin.Generated);

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

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

    sealed class RecordingHost : IProcessReferenceHost
    {
        public List<ProcessTransitionInvocation> Transitions { get; } = [];

        public List<ProcessRelationEvaluation> Relations { get; } = [];

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation)
        {
            Transitions.Add(invocation);
            return ProcessOperationResult.Completed(invocation.Input);
        }

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation)
        {
            Relations.Add(evaluation);
            return ProcessOperationResult.Completed(evaluation.Input);
        }

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }

    sealed class InvalidResultHost : IProcessReferenceHost
    {
        public static InvalidResultHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            ProcessOperationResult.Completed(BooleanValue(true));

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }
}
