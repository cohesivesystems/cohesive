using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessReferencePolicyValidationTests
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Theory]
    [InlineData(ProcessJoinCompletionOrder.Unobservable, ProcessJoinTieBreak.BranchIdentity, true)]
    [InlineData(ProcessJoinCompletionOrder.Observable, ProcessJoinTieBreak.BranchIdentity, true)]
    [InlineData(ProcessJoinCompletionOrder.Observable, ProcessJoinTieBreak.CompletionThenBranchIdentity, true)]
    [InlineData(ProcessJoinCompletionOrder.Unobservable, ProcessJoinTieBreak.CompletionThenBranchIdentity, false)]
    public void Validate_JoinCompletionOrderAndTieBreakMustBeSemanticallyCompatible(
        ProcessJoinCompletionOrder completionOrder,
        ProcessJoinTieBreak tieBreak,
        bool expectedValid)
    {
        var definition = Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/a"), Edge("edge/fork-a", "wait/a")),
                        new(new("branch/b"), Edge("edge/fork-b", "wait/b"))
                    ],
                    new("join")),
                new DurableCutProcessNode(new("wait/a"), Edge("edge/a-join", "join")),
                new DurableCutProcessNode(new("wait/b"), Edge("edge/b-join", "join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    JoinPolicy(completionOrder, tieBreak),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        if (expectedValid)
        {
            Assert.True(validation.IsValid, FormatDiagnostics(validation));
            Assert.DoesNotContain(
                validation.Diagnostics,
                static diagnostic => diagnostic.Code == ProcessDefinitionDiagnosticCodes.JoinCompletionPolicyInvalid);
            return;
        }

        var diagnostic = Assert.Single(
            validation.Diagnostics,
            static candidate => candidate.Code == ProcessDefinitionDiagnosticCodes.JoinCompletionPolicyInvalid);
        Assert.EndsWith("/policy/tieBreak", diagnostic.Location);
        Assert.Equal("join", diagnostic.Evidence?.Subject);
    }

    [Fact]
    public void Validate_PreForkRequestObligationCannotBeConsumedInsideBranch()
    {
        var interactions = RequestInteractions();
        RequestObligationBindingId obligation = new("review.request");
        var definition = Definition(
            "await",
            [
                AwaitRequest(
                    interactions.Request,
                    obligation,
                    Edge("edge/await-fork", "fork")),
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/reply"), Edge("edge/fork-reply", "reply/in-branch")),
                        new(new("branch/wait"), Edge("edge/fork-wait", "wait/other"))
                    ],
                    new("join")),
                new ReplyProcessNode(
                    new("reply/in-branch"),
                    interactions.Reply,
                    obligation,
                    Expr.Const("approved"),
                    Edge("edge/reply-join", "join")),
                new DurableCutProcessNode(new("wait/other"), Edge("edge/wait-join", "join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    JoinPolicy(ProcessJoinCompletionOrder.Unobservable, ProcessJoinTieBreak.BranchIdentity),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition, interactions.Context);

        var diagnostic = Assert.Single(
            validation.Diagnostics,
            static candidate => candidate.Code == ProcessDefinitionDiagnosticCodes.ReplyRequestObligationForked);
        Assert.Equal(obligation.Value, diagnostic.Evidence?.Subject);
        Assert.Contains("/request", diagnostic.Location, StringComparison.Ordinal);
        Assert.Contains(
            diagnostic.Evidence?.RelatedLocations ?? [],
            static location => location.EndsWith("/requestObligation/binding", StringComparison.Ordinal));
        Assert.Contains(
            diagnostic.Evidence?.RelatedLocations ?? [],
            static location => location.EndsWith("/nodes/1", StringComparison.Ordinal));
        Assert.False(validation.IsValid);
    }

    [Fact]
    public void Validate_PreForkRequestObligationMayBeConsumedAfterAllJoin()
    {
        var interactions = RequestInteractions();
        RequestObligationBindingId obligation = new("review.request");
        var definition = Definition(
            "await",
            [
                AwaitRequest(
                    interactions.Request,
                    obligation,
                    Edge("edge/await-fork", "fork")),
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/a"), Edge("edge/fork-a", "wait/a")),
                        new(new("branch/b"), Edge("edge/fork-b", "wait/b"))
                    ],
                    new("join")),
                new DurableCutProcessNode(new("wait/a"), Edge("edge/a-join", "join")),
                new DurableCutProcessNode(new("wait/b"), Edge("edge/b-join", "join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    JoinPolicy(ProcessJoinCompletionOrder.Unobservable, ProcessJoinTieBreak.BranchIdentity),
                    Edge("edge/join-reply", "reply/after-join")),
                new ReplyProcessNode(
                    new("reply/after-join"),
                    interactions.Reply,
                    obligation,
                    Expr.Const("approved"),
                    Edge("edge/reply-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition, interactions.Context);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        Assert.DoesNotContain(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessDefinitionDiagnosticCodes.ReplyRequestObligationForked);
    }

    static AwaitMatchProcessNode AwaitRequest(
        RequestContractReference request,
        RequestObligationBindingId obligation,
        ProcessEdge next) => new(
        new("await"),
        ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
        [
            new ProcessAwaitInteractionClause(
                new("clause/request"),
                request,
                new(new("request.payload"), StringContract),
                new(obligation),
                guard: null,
                priority: 0,
                new(next))
        ],
        ProcessAwaitInputDisposition.Observe,
        ProcessAwaitInputDisposition.Reject,
        ProcessAwaitInputDisposition.ReusePriorDisposition,
        ProcessAwaitMissingTargetDisposition.DeadLetter,
        TimeSpan.FromDays(7));

    static ProcessJoinPolicy JoinPolicy(
        ProcessJoinCompletionOrder completionOrder,
        ProcessJoinTieBreak tieBreak) => new(
        ProcessJoinMode.All,
        requiredCount: 0,
        ProcessJoinFailurePolicy.FailFast,
        ProcessJoinCancellationPolicy.AwaitRemaining,
        completionOrder,
        tieBreak);

    static CanonicalProcessDefinition Definition(
        string entry,
        ImmutableArray<CanonicalProcessNode> nodes) => new(
        StringContract,
        StringContract,
        new(entry),
        nodes,
        ProcessRecoveryPolicy.ContinueAttempt);

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static RequestInteractionFixture RequestInteractions()
    {
        var requestDocument = InteractionContractDocuments.Create(
            new("interaction/request/policy-validation"),
            new("revision/1"),
            RequestDefinition(),
            Provenance());
        var request = new RequestContractReference(Reference(requestDocument));
        var replyDocument = InteractionContractDocuments.Create(
            new("interaction/reply/policy-validation"),
            new("revision/1"),
            new ReplyContractDefinition(request, new("approved")),
            Provenance());
        var validation = InteractionContractCatalog.TryCreate(
            [requestDocument, replyDocument],
            out var catalog);
        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        return new(
            request,
            new(Reference(replyDocument)),
            new(interactionContracts: Assert.IsType<InteractionContractCatalog>(catalog)));
    }

    static RequestContractDefinition RequestDefinition() => new(
        StringSchema(),
        new RequestResponseObligation(
            [
                new RequestResultDefinition(new("approved"), StringSchema()),
                new RequestFailureDefinition(new("failed"), StringSchema())
            ],
            RequestOptionalTerminalSemantics.Unsupported,
            RequestOptionalTerminalSemantics.Unsupported,
            RequestResultDisposition.Reject,
            RequestResultDisposition.Reject,
            RequestResultDisposition.ReusePriorDisposition,
            RequestRetrySemantics.Never,
            RequestResolutionSemantics.TerminalFailure,
            RequestResolutionSemantics.TerminalFailure,
            TimeSpan.FromDays(7)));

    static InteractionValueSchema StringSchema() => new(StringContract, new("schema/v1"));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static ExecutionProvenance Provenance() => new(
        new("process-reference-policy-validation-tests", "1"),
        new("tests/execution-kernel/process-reference-policy-validation"),
        DocumentOrigin.Generated);

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    sealed record RequestInteractionFixture(
        RequestContractReference Request,
        ReplyContractReference Reply,
        ProcessDefinitionValidationContext Context);
}
