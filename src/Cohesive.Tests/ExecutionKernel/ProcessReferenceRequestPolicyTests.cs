using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessReferenceRequestPolicyTests
{
    static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    static readonly ValueContract StringContract =
        new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void RequestWait_BuffersAnUnrelatedInteractionForADownstreamAwait()
    {
        var requestDocument = RequestDocument(RequestResultDisposition.Observe);
        RequestContractReference requestContract = new(Reference(requestDocument));
        var replyDocument = InteractionDocument(
            "interaction/reply/request-policy-accepted",
            new ReplyContractDefinition(requestContract, new("accepted")));
        ReplyContractReference replyContract = new(Reference(replyDocument));
        var eventDocument = InteractionDocument(
            "interaction/event/request-policy-follow-up",
            new DomainEventContractDefinition(StringSchema("follow-up/v1")));
        DomainEventContractReference eventContract = new(Reference(eventDocument));
        ValueBindingId requestResult = new("request.result");
        ValueBindingId followUp = new("follow-up.event");
        var plan = Compile(
            Definition(
                "request",
                [
                    RequestNode(
                        requestContract,
                        new(
                            Edge("edge/request-await", "await"),
                            new(requestResult, StringContract))),
                    new AwaitMatchProcessNode(
                        new("await"),
                        ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                        [
                            new ProcessAwaitInteractionClause(
                                new("clause/follow-up"),
                                eventContract,
                                new(followUp, StringContract),
                                requestObligation: null,
                                guard: null,
                                priority: 0,
                                new(Edge("edge/await-return", "return")))
                        ],
                        ProcessAwaitInputDisposition.Observe,
                        ProcessAwaitInputDisposition.Reject,
                        ProcessAwaitInputDisposition.ReusePriorDisposition,
                        ProcessAwaitMissingTargetDisposition.DeadLetter,
                        TimeSpan.FromDays(7)),
                    new ReturnProcessNode(new("return"), Expr.BoundValue(followUp))
                ]),
            Catalog(requestDocument, replyDocument, eventDocument));
        var requested = Start(plan);
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(requested.Emissions));
        var token = Assert.Single(requested.State.Tokens);
        var target = new ProcessTokenInteractionTarget(requested.State.Continuation, token.Id);
        var followUpEvent = new DomainEventEnvelope(
            InteractionEnvelope.CurrentSchemaVersion,
            IncomingContext(
                plan,
                requested.State.Continuation,
                token.Id,
                "emission/follow-up"),
            eventContract,
            StringValue("follow-up-value"));

        var buffered = ProcessReferenceInterpreter.Activate(
            plan,
            requested.State,
            Activation(
                "activation/buffer-follow-up",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                [new(target, followUpEvent)]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Quiescent, buffered.Disposition);
        Assert.True(Assert.Single(buffered.State.Waits).Active);
        Assert.Equal(ProcessInputAdmissionDisposition.Buffered, Assert.Single(buffered.InputAdmissions).Disposition);
        Assert.Equal(ProcessInputAdmissionReason.Early, Assert.Single(buffered.InputAdmissions).Reason);
        Assert.Null(Assert.Single(buffered.InputAdmissions).WaitRegistrationId);
        Assert.Equal(followUpEvent.Context.EmissionId, Assert.Single(buffered.State.BufferedInputs).Input.Envelope.Context.EmissionId);
        Assert.Empty(buffered.Emissions);

        var reply = Reply(
            plan,
            buffered.State.Continuation,
            token.Id,
            request,
            replyContract,
            "emission/reply",
            "accepted-value");
        var resumed = ProcessReferenceInterpreter.Activate(
            plan,
            buffered.State,
            Activation(
                "activation/reply-and-await",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(2),
                [new(target, reply)]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, resumed.Disposition);
        Assert.Empty(resumed.State.BufferedInputs);
        Assert.Empty(resumed.State.OutstandingRequests);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            resumed.State.InputReceipts.Single(
                receipt => receipt.Emission == followUpEvent.Context.EmissionId).Disposition);
        Assert.Equal(
            ProcessInputAdmissionReason.Consumed,
            resumed.State.InputReceipts.Single(
                receipt => receipt.Emission == followUpEvent.Context.EmissionId).Reason);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            resumed.State.InputReceipts.Single(receipt => receipt.Emission == reply.Context.EmissionId).Disposition);
        Assert.Equal(
            ProcessInputAdmissionReason.Consumed,
            resumed.State.InputReceipts.Single(receipt => receipt.Emission == reply.Context.EmissionId).Reason);
        Assert.Equal(new ExecutionNodeId("return"), Assert.Single(resumed.State.Tokens).Node);
        Assert.Empty(resumed.Emissions);

        var completed = ProcessReferenceInterpreter.Activate(
            plan,
            resumed.State,
            Activation(
                "activation/complete",
                ProcessActivationCause.Continue,
                StartedAtUtc.AddMinutes(3)),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, completed.Disposition);
        Assert.Equal(StringValue("follow-up-value"), completed.State.Terminal.Detail?.Value);
    }

    [Theory]
    [InlineData(RequestResultDisposition.Observe, ProcessInputAdmissionDisposition.Observed)]
    [InlineData(RequestResultDisposition.ReusePriorDisposition, ProcessInputAdmissionDisposition.Consumed)]
    public void RequestWait_AppliesLatePolicyToSameActivationAndTombstoneReplies(
        RequestResultDisposition lateResult,
        ProcessInputAdmissionDisposition expectedLateDisposition)
    {
        var requestDocument = RequestDocument(lateResult);
        RequestContractReference requestContract = new(Reference(requestDocument));
        var replyDocument = InteractionDocument(
            "interaction/reply/request-policy-accepted",
            new ReplyContractDefinition(requestContract, new("accepted")));
        ReplyContractReference replyContract = new(Reference(replyDocument));
        ValueBindingId requestResult = new("request.result");
        var plan = Compile(
            Definition(
                "request",
                [
                    RequestNode(
                        requestContract,
                        new(
                            Edge("edge/request-cut", "cut"),
                            new(requestResult, StringContract))),
                    new DurableCutProcessNode(new("cut"), Edge("edge/cut-return", "return")),
                    new ReturnProcessNode(new("return"), Expr.BoundValue(requestResult))
                ]),
            Catalog(requestDocument, replyDocument));
        var requested = Start(plan);
        var request = Assert.IsType<RequestEnvelope>(Assert.Single(requested.Emissions));
        var token = Assert.Single(requested.State.Tokens);
        var target = new ProcessTokenInteractionTarget(requested.State.Continuation, token.Id);
        var winner = Reply(
            plan,
            requested.State.Continuation,
            token.Id,
            request,
            replyContract,
            "emission/a-winner",
            "winner-value");
        var sameActivationLoser = Reply(
            plan,
            requested.State.Continuation,
            token.Id,
            request,
            replyContract,
            "emission/z-loser",
            "loser-value");

        var resolved = ProcessReferenceInterpreter.Activate(
            plan,
            requested.State,
            Activation(
                "activation/two-replies",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(1),
                [new(target, sameActivationLoser), new(target, winner)]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, resolved.Disposition);
        Assert.Single(requested.Emissions);
        Assert.Empty(resolved.Emissions);
        Assert.Empty(resolved.Diagnostics);
        Assert.Single(resolved.State.Tokens);
        Assert.Empty(resolved.State.OutstandingRequests);
        Assert.Empty(resolved.State.BufferedInputs);
        Assert.Equal(2, resolved.InputAdmissions.Length);
        Assert.Single(resolved.State.Waits.Where(static wait => wait.WinnerInput is not null));
        var requestWait = Assert.Single(resolved.State.Waits, static wait => wait.Kind == ProcessWaitKind.Request);
        Assert.False(requestWait.Active);
        Assert.Equal(winner.Context.EmissionId, requestWait.WinnerInput);
        Assert.Equal(
            ProcessInputAdmissionDisposition.Consumed,
            resolved.State.InputReceipts.Single(receipt => receipt.Emission == winner.Context.EmissionId).Disposition);
        Assert.Equal(
            ProcessInputAdmissionReason.Consumed,
            resolved.State.InputReceipts.Single(receipt => receipt.Emission == winner.Context.EmissionId).Reason);
        Assert.Equal(
            expectedLateDisposition,
            resolved.State.InputReceipts.Single(
                receipt => receipt.Emission == sameActivationLoser.Context.EmissionId).Disposition);
        Assert.Equal(
            ProcessInputAdmissionReason.Late,
            resolved.State.InputReceipts.Single(
                receipt => receipt.Emission == sameActivationLoser.Context.EmissionId).Reason);
        Assert.Equal(requestWait.RegistrationId, resolved.State.InputReceipts.Single(
            receipt => receipt.Emission == sameActivationLoser.Context.EmissionId).WaitRegistrationId);

        var tombstoneArrival = Reply(
            plan,
            resolved.State.Continuation,
            token.Id,
            request,
            replyContract,
            "emission/zz-tombstone",
            "tombstone-value");
        var completed = ProcessReferenceInterpreter.Activate(
            plan,
            resolved.State,
            Activation(
                "activation/tombstone-reply",
                ProcessActivationCause.Interaction,
                StartedAtUtc.AddMinutes(2),
                [new(target, tombstoneArrival)]),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, completed.Disposition);
        Assert.Empty(completed.Emissions);
        Assert.Empty(completed.Diagnostics);
        Assert.Single(completed.State.Tokens);
        Assert.Equal(StringValue("winner-value"), completed.State.Terminal.Detail?.Value);
        Assert.Equal(expectedLateDisposition, Assert.Single(completed.InputAdmissions).Disposition);
        Assert.Equal(ProcessInputAdmissionReason.Late, Assert.Single(completed.InputAdmissions).Reason);
        Assert.Equal(requestWait.RegistrationId, Assert.Single(completed.InputAdmissions).WaitRegistrationId);
        Assert.Equal(winner.Context.EmissionId, completed.State.Waits.Single(
            static wait => wait.Kind == ProcessWaitKind.Request).WinnerInput);
    }

    static ProcessActivationDecision Start(CompiledProcessPlan plan)
    {
        var initial = ProcessReferenceInterpreter.Create(plan, Continuation(), StringValue("request-value"));
        var requested = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/request", ProcessActivationCause.Start, StartedAtUtc),
            RejectingHost.Instance);
        Assert.Equal(ProcessActivationDisposition.DurableCut, requested.Disposition);
        Assert.Single(requested.Emissions);
        Assert.Single(requested.State.OutstandingRequests);
        return requested;
    }

    static RequestProcessNode RequestNode(
        RequestContractReference requestContract,
        ProcessContinuation continuation) => new(
        new("request"),
        requestContract,
        Expr.BoundValue(ProcessBindingIds.Input),
        [new(new("outcome/accepted"), new("accepted"), continuation)]);

    static ExecutionDefinitionDocument RequestDocument(RequestResultDisposition lateResult) =>
        InteractionDocument(
            "interaction/request/request-policy",
            new RequestContractDefinition(
                StringSchema("request/v1"),
                new RequestResponseObligation(
                    [new RequestResultDefinition(new("accepted"), StringSchema("accepted/v1"))],
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestOptionalTerminalSemantics.Unsupported,
                    lateResult,
                    RequestResultDisposition.Reject,
                    RequestResultDisposition.ReusePriorDisposition,
                    RequestRetrySemantics.StableIdentity,
                    RequestResolutionSemantics.Reconcile,
                    RequestResolutionSemantics.Escalate,
                    TimeSpan.FromDays(30))));

    static ReplyEnvelope Reply(
        CompiledProcessPlan plan,
        ProcessContinuationIdentity continuation,
        TokenId token,
        RequestEnvelope request,
        ReplyContractReference replyContract,
        string emission,
        string value) => new(
        InteractionEnvelope.CurrentSchemaVersion,
        IncomingContext(plan, continuation, token, emission, request.Context.EmissionId),
        replyContract,
        request.Context.EmissionId,
        new RequestResultOutcome(new("accepted"), StringValue(value)));

    static CompiledProcessPlan Compile(
        CanonicalProcessDefinition definition,
        InteractionContractCatalog contracts)
    {
        var document = ProcessDefinitionDocuments.Create(
            new("process/reference-request-policy-tests"),
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
        params ReadOnlySpan<CanonicalProcessNode> nodes) => new(
        StringContract,
        StringContract,
        new(entry),
        [.. nodes],
        ProcessRecoveryPolicy.ContinueAttempt);

    static ProcessContinuationIdentity Continuation() => new(
        new("process-instance/reference-request-policy-tests"),
        new("process-attempt/1"));

    static ProcessActivation Activation(
        string id,
        ProcessActivationCause cause,
        DateTimeOffset observedAtUtc,
        ImmutableArray<ProcessActivationInput> inputs = default) => new(
        new(id),
        cause,
        observedAtUtc,
        new(
            new("authority/tests", "tenant/cohesive"),
            new("correlation/reference-request-policy-tests"),
            new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            Provenance()),
        inputs);

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
        new("correlation/reference-request-policy-tests"),
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

    static InteractionValueSchema StringSchema(string revision) =>
        new(StringContract, new(revision));

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static PortableValue StringValue(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    static ExecutionProvenance Provenance() => new(
        new("process-reference-request-policy-tests", "1"),
        new("tests/execution-kernel/process-reference-request-policy"),
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
