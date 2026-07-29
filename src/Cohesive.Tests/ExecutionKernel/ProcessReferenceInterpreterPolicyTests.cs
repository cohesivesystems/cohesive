using System.Collections.Immutable;
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
            ProcessInputAdmissionDisposition.TerminalUnconsumed,
            Assert.Single(decision.State.InputReceipts).Disposition);
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

    static void AssertConflict(ProcessActivationDecision decision, EmissionId emission)
    {
        Assert.Empty(decision.State.BufferedInputs);
        Assert.DoesNotContain(decision.State.InputReceipts, receipt => receipt.Emission == emission);
        Assert.Equal(
            ProcessInputAdmissionDisposition.IdentityConflict,
            Assert.Single(decision.InputAdmissions).Disposition);
        Assert.Contains(
            decision.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.InputIdentityConflict);
        Assert.True(Assert.Single(decision.State.Waits).Active);
    }

    static (TokenId Id, ExecutionNodeId Node, ExecutionTokenDisposition Disposition, long Step) TokenProjection(
        ProcessTokenState token) => (token.Id, token.Node, token.Disposition, token.Step);

    static (string Registration, TokenId Token, ExecutionNodeId Node, ProcessWaitKind Kind, bool Active) WaitProjection(
        ProcessWaitState wait) => (wait.RegistrationId, wait.Token, wait.Node, wait.Kind, wait.Active);

    static (
        int Sequence,
        ProcessTraceEventKind Kind,
        TokenId Token,
        ExecutionNodeId Node,
        ExecutionNodeId? BranchOrClause,
        EmissionId? Emission,
        string? Detail) TraceProjection(ProcessTraceEvent item) =>
        (item.Sequence, item.Kind, item.Token, item.Node, item.BranchOrClause, item.Emission, item.Detail);

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
        ProcessCancellationIntent? cancellation = null) => new(
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
        cancellation);

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
