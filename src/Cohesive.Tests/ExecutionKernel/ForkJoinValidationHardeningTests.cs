using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ForkJoinValidationHardeningTests
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void Validate_JoinRejectsReachableIngressOutsideReciprocalForkLineage()
    {
        var definition = Definition(
            "choose",
            [
                new ChoiceProcessNode(
                    new("choose"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [
                        new(
                            new("choose/fork"),
                            Expr.Eq(Expr.BoundValue(ProcessBindingIds.Input), Expr.Const("fork")),
                            Edge("edge/choose-fork", "fork"))
                    ],
                    new(new("choose/rogue"), Edge("edge/choose-rogue", "rogue"))),
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/a"), Edge("edge/fork-a", "branch-a")),
                        new(new("branch/b"), Edge("edge/fork-b", "branch-b"))
                    ],
                    new("join")),
                new DurableCutProcessNode(new("branch-a"), Edge("edge/a-join", "join")),
                new DurableCutProcessNode(new("branch-b"), Edge("edge/b-join", "join")),
                new DurableCutProcessNode(new("rogue"), Edge("edge/rogue-join", "join")),
                new JoinProcessNode(new("join"), new("fork"), JoinPolicy(ProcessJoinMode.All), Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        var diagnostic = Assert.Single(validation.Diagnostics.Where(
            static item => item.Code == ProcessDefinitionDiagnosticCodes.JoinIngressNotOwned));
        Assert.Equal("edge/rogue-join", diagnostic.Evidence?.Subject);
    }

    [Fact]
    public void Validate_JoinRejectsExternalPathMergedIntoOwnedBranchUpstream()
    {
        var definition = Definition(
            "choose",
            [
                new ChoiceProcessNode(
                    new("choose"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [
                        new(
                            new("choose/fork"),
                            Expr.Eq(Expr.BoundValue(ProcessBindingIds.Input), Expr.Const("fork")),
                            Edge("edge/choose-fork", "fork"))
                    ],
                    new(new("choose/rogue"), Edge("edge/choose-rogue", "rogue"))),
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/shared"), Edge("edge/fork-shared", "shared")),
                        new(new("branch/direct"), Edge("edge/fork-direct", "direct"))
                    ],
                    new("join")),
                new DurableCutProcessNode(new("rogue"), Edge("edge/rogue-shared", "shared")),
                new DurableCutProcessNode(new("shared"), Edge("edge/shared-join", "join")),
                new DurableCutProcessNode(new("direct"), Edge("edge/direct-join", "join")),
                new JoinProcessNode(new("join"), new("fork"), JoinPolicy(ProcessJoinMode.All), Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        var diagnostic = Assert.Single(validation.Diagnostics.Where(
            static item => item.Code == ProcessDefinitionDiagnosticCodes.JoinIngressNotOwned));
        Assert.Equal("edge/rogue-shared", diagnostic.Evidence?.Subject);
    }

    [Fact]
    public void Validate_ForkBranchCannotPassThroughForeignJoin()
    {
        var definition = Definition(
            "outer-fork",
            [
                new ForkProcessNode(
                    new("outer-fork"),
                    [
                        new(new("outer/nested"), Edge("edge/outer-nested", "inner-fork")),
                        new(new("outer/direct"), Edge("edge/outer-direct", "outer-direct"))
                    ],
                    new("outer-join")),
                new DurableCutProcessNode(new("outer-direct"), Edge("edge/outer-direct-join", "outer-join")),
                new ForkProcessNode(
                    new("inner-fork"),
                    [
                        new(new("inner/a"), Edge("edge/inner-a", "inner-a")),
                        new(new("inner/b"), Edge("edge/inner-b", "inner-b"))
                    ],
                    new("inner-join")),
                new DurableCutProcessNode(new("inner-a"), Edge("edge/inner-a-join", "inner-join")),
                new DurableCutProcessNode(new("inner-b"), Edge("edge/inner-b-join", "inner-join")),
                new JoinProcessNode(new("inner-join"), new("inner-fork"), JoinPolicy(ProcessJoinMode.All), Edge("edge/inner-outer", "outer-join")),
                new JoinProcessNode(new("outer-join"), new("outer-fork"), JoinPolicy(ProcessJoinMode.All), Edge("edge/outer-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        var diagnostic = Assert.Single(validation.Diagnostics.Where(
            static item => item.Code == ProcessDefinitionDiagnosticCodes.ForkBranchDoesNotConverge));
        Assert.Equal("outer/nested", diagnostic.Evidence?.Subject);
        Assert.Contains("foreign Join 'inner-join'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AllJoinPublishesBindingsGuaranteedByEveryCompletedBranch()
    {
        var transition = TransitionReference();
        var definition = BindingJoinDefinition(ProcessJoinMode.All, transition);
        var context = new ProcessDefinitionValidationContext(
            [new(transition, ProcessDefinitionLinkKind.Transition, StringContract, StringContract)]);

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
    }

    [Theory]
    [InlineData(ProcessJoinMode.Any)]
    [InlineData(ProcessJoinMode.RequiredCount)]
    public void Validate_PartialJoinDoesNotPublishBranchLocalBindings(ProcessJoinMode mode)
    {
        var transition = TransitionReference();
        var definition = BindingJoinDefinition(mode, transition);
        var context = new ProcessDefinitionValidationContext(
            [new(transition, ProcessDefinitionLinkKind.Transition, StringContract, StringContract)]);

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        Assert.Equal(
            2,
            validation.Diagnostics.Count(static item => item.Code == ExprAnalysisDiagnosticCodes.BindingNotVisible));
    }

    [Fact]
    public void Validate_AllJoinDoesNotPublishBindingMissingOnOnePathWithinBranch()
    {
        var transition = TransitionReference();
        ValueBindingId conditional = new("conditional");
        var definition = Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/conditional"), Edge("edge/fork-choice", "choice")),
                        new(new("branch/direct"), Edge("edge/fork-direct", "direct"))
                    ],
                    new("join")),
                new ChoiceProcessNode(
                    new("choice"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [
                        new(
                            new("choice/produce"),
                            Expr.Eq(Expr.BoundValue(ProcessBindingIds.Input), Expr.Const("produce")),
                            Edge("edge/choice-produce", "produce"))
                    ],
                    new(new("choice/skip"), Edge("edge/choice-skip", "skip"))),
                Invoke("produce", transition, Expr.Const("subject"), Expr.Const("input"), "edge/produce-join", "join", conditional),
                new DurableCutProcessNode(new("skip"), Edge("edge/skip-join", "join")),
                new DurableCutProcessNode(new("direct"), Edge("edge/direct-join", "join")),
                new JoinProcessNode(new("join"), new("fork"), JoinPolicy(ProcessJoinMode.All), Edge("edge/join-consume", "consume")),
                Invoke("consume", transition, Expr.BoundValue(conditional), Expr.Const("input"), "edge/consume-return", "return"),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var context = new ProcessDefinitionValidationContext(
            [new(transition, ProcessDefinitionLinkKind.Transition, StringContract, StringContract)]);

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        Assert.Contains(
            validation.Diagnostics,
            static item => item.Code == ExprAnalysisDiagnosticCodes.BindingNotVisible);
    }

    [Fact]
    public void Validate_DurableRecurrenceWithStructuralJoinExitIsValid()
    {
        var definition = Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/recurrent"), Edge("edge/fork-decide", "decide")),
                        new(new("branch/direct"), Edge("edge/fork-direct", "direct"))
                    ],
                    new("join")),
                new ChoiceProcessNode(
                    new("decide"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [
                        new(
                            new("decide/exit"),
                            Expr.Eq(Expr.BoundValue(ProcessBindingIds.Input), Expr.Const("exit")),
                            Edge("edge/decide-join", "join"))
                    ],
                    new(new("decide/recur"), Edge("edge/decide-cut", "cut"))),
                new DurableCutProcessNode(new("cut"), Edge("edge/cut-decide", "decide")),
                new DurableCutProcessNode(new("direct"), Edge("edge/direct-join", "join")),
                new JoinProcessNode(new("join"), new("fork"), JoinPolicy(ProcessJoinMode.All), Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
    }

    [Fact]
    public void Validate_ClosedDurableRecurrenceWithinForkBranchIsRejected()
    {
        var definition = Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/recurrent"), Edge("edge/fork-cut-a", "cut-a")),
                        new(new("branch/direct"), Edge("edge/fork-direct", "direct"))
                    ],
                    new("join")),
                new DurableCutProcessNode(new("cut-a"), Edge("edge/cut-a-b", "cut-b")),
                new DurableCutProcessNode(new("cut-b"), Edge("edge/cut-b-a", "cut-a")),
                new DurableCutProcessNode(new("direct"), Edge("edge/direct-join", "join")),
                new JoinProcessNode(new("join"), new("fork"), JoinPolicy(ProcessJoinMode.All), Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        var diagnostic = Assert.Single(validation.Diagnostics.Where(
            static item => item.Code == ProcessDefinitionDiagnosticCodes.ForkBranchDoesNotConverge));
        Assert.Equal("branch/recurrent", diagnostic.Evidence?.Subject);
        Assert.Contains("no structural exit", diagnostic.Message, StringComparison.Ordinal);
    }

    static CanonicalProcessDefinition BindingJoinDefinition(
        ProcessJoinMode mode,
        ExecutionDefinitionReference transition)
    {
        ValueBindingId left = new("branch.left");
        ValueBindingId right = new("branch.right");
        return Definition(
            "fork",
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/left"), Edge("edge/fork-left", "left")),
                        new(new("branch/right"), Edge("edge/fork-right", "right"))
                    ],
                    new("join")),
                Invoke("left", transition, Expr.Const("subject"), Expr.Const("left"), "edge/left-join", "join", left),
                Invoke("right", transition, Expr.Const("subject"), Expr.Const("right"), "edge/right-join", "join", right),
                new JoinProcessNode(new("join"), new("fork"), JoinPolicy(mode), Edge("edge/join-consume", "consume")),
                Invoke("consume", transition, Expr.BoundValue(left), Expr.BoundValue(right), "edge/consume-return", "return"),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
    }

    static InvokeTransitionProcessNode Invoke(
        string id,
        ExecutionDefinitionReference transition,
        Expr subject,
        Expr input,
        string edgeId,
        string target,
        ValueBindingId? output = null) => new(
            new(id),
            transition,
            subject,
            input,
            new(
                Edge(edgeId, target),
                output is { } binding ? new(binding, StringContract) : null));

    static ProcessJoinPolicy JoinPolicy(ProcessJoinMode mode) => new(
        mode,
        mode == ProcessJoinMode.RequiredCount ? 1 : 0,
        ProcessJoinFailurePolicy.FailFast,
        ProcessJoinCancellationPolicy.AwaitRemaining,
        ProcessJoinCompletionOrder.Unobservable,
        ProcessJoinTieBreak.BranchIdentity);

    static CanonicalProcessDefinition Definition(
        string entry,
        ImmutableArray<CanonicalProcessNode> nodes) => new(
            StringContract,
            StringContract,
            new(entry),
            nodes,
            ProcessRecoveryPolicy.ContinueAttempt);

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static ExecutionDefinitionReference TransitionReference() => new(
        new("transition/fork-join-test"),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('1', 64)));

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static item => $"{item.Code} {item.Location}: {item.Message}"));
}
