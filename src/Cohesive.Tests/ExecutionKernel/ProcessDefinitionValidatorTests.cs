using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessDefinitionValidatorTests
{
    static readonly ValueContract BooleanContract = new(new ScalarTypeRef(ScalarTypeKind.Bool));
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract InputContract = new(new ObjectTypeRef(
    [
        new("caseId", new ScalarTypeRef(ScalarTypeKind.String)),
        new("result", new ScalarTypeRef(ScalarTypeKind.String))
    ]));

    [Fact]
    public void Validate_RepresentativeLinkedForkJoinAndAwaitGraph_IsValid()
    {
        var transition = DefinitionReference("transition/review");
        ValueBindingId outcome = new("review.outcome");
        var definition = Definition(
            entry: "invoke",
            nodes:
            [
                new InvokeTransitionProcessNode(
                    new("invoke"),
                    transition,
                    Expr.Field(ProcessBindingIds.Input, "caseId"),
                    Expr.Const("review"),
                    Continue("edge/invoke-fork", "fork", outcome, StringContract)),
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/timer"), Edge("edge/fork-timer", "timer")),
                        new(new("branch/await"), Edge("edge/fork-await", "await"))
                    ],
                    new("join")),
                new TimerProcessNode(
                    new("timer"),
                    Expr.Const(Instant()),
                    Edge("edge/timer-join", "join")),
                Await(
                    "await",
                    [
                        new ProcessAwaitTimerClause(
                            new("await/timeout"),
                            Expr.Const(Instant()),
                            0,
                            new(Edge("edge/await-join", "join")))
                    ]),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    JoinPolicy(),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.BoundValue(outcome))
            ],
            input: InputContract);
        var context = Context(
            new ProcessDefinitionLink(
                transition,
                ProcessDefinitionLinkKind.Transition,
                StringContract,
                StringContract));

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        Assert.Empty(validation.Diagnostics);
    }

    [Fact]
    public void Validate_DefaultAndDuplicateNodeIdentities_HaveExactLocations()
    {
        var definition = Definition(
            entry: "same",
            nodes:
            [
                new ReturnProcessNode(default, Expr.Const("default")),
                new ReturnProcessNode(new("same"), Expr.Const("first")),
                new ReturnProcessNode(new("same"), Expr.Const("second"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.NodeIdentityMissing, "/nodes/0/id");
        var duplicate = AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.NodeIdentityDuplicate, "/nodes/2/id");
        Assert.Contains("/nodes/1/id", duplicate.Evidence?.RelatedLocations ?? [], StringComparer.Ordinal);
    }

    [Fact]
    public void Validate_DefaultAndDuplicateEdgeIdentities_HaveExactLocations()
    {
        var definition = Definition(
            entry: "choice",
            nodes:
            [
                new ChoiceProcessNode(
                    new("choice"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [
                        new(
                            new("case/default"),
                            Expr.Const(true),
                            new(default, new("terminal"))),
                        new(
                            new("case/duplicate"),
                            Expr.Const(false),
                            Edge("edge/shared", "terminal"))
                    ],
                    new(new("fallback"), Edge("edge/shared", "terminal"))),
                new ReturnProcessNode(new("terminal"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EdgeIdentityMissing, "/nodes/0/cases/0/next/id");
        var duplicate = AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EdgeIdentityDuplicate, "/nodes/0/fallback/next/id");
        Assert.Contains("/nodes/0/cases/1/next/id", duplicate.Evidence?.RelatedLocations ?? [], StringComparer.Ordinal);
    }

    [Fact]
    public void Validate_DanglingEntryAndEdgeTarget_HaveExactLocations()
    {
        var definition = Definition(
            entry: "missing-entry",
            nodes:
            [
                new DurableCutProcessNode(
                    new("cut"),
                    Edge("edge/missing", "missing-target"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EntryUnresolved, "/entry");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EdgeTargetUnresolved, "/nodes/0/resume/target");
    }

    [Fact]
    public void Validate_UseBeforeProduceAndResultTypeMismatch_PreserveExpressionLocations()
    {
        ValueBindingId missing = new("missing");
        var useBeforeProduce = Definition(
            entry: "return",
            nodes: [new ReturnProcessNode(new("return"), Expr.BoundValue(missing))]);
        var typeMismatch = Definition(
            entry: "return",
            nodes: [new ReturnProcessNode(new("return"), Expr.Const(true))]);

        var bindingValidation = ProcessDefinitionValidator.Validate(useBeforeProduce);
        var typeValidation = ProcessDefinitionValidator.Validate(typeMismatch);

        AssertDiagnostic(
            bindingValidation,
            ExprAnalysisDiagnosticCodes.BindingNotVisible,
            "/nodes/0/result");
        AssertDiagnostic(
            typeValidation,
            ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
            "/nodes/0/result");
    }

    [Fact]
    public void Validate_LinkedInvocationInputAndOutputTypes_AreCheckedAtTheirOwningSites()
    {
        var transition = DefinitionReference("transition/review");
        var definition = Definition(
            entry: "invoke",
            nodes:
            [
                new InvokeTransitionProcessNode(
                    new("invoke"),
                    transition,
                    Expr.Const("case-1"),
                    Expr.Const(true),
                    Continue(
                        "edge/invoke-return",
                        "return",
                        new("review.outcome"),
                        BooleanContract)),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var context = Context(
            new ProcessDefinitionLink(
                transition,
                ProcessDefinitionLinkKind.Transition,
                StringContract,
                StringContract));

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        AssertDiagnostic(
            validation,
            ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
            "/nodes/0/input");
        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.OutputContractMismatch,
            "/nodes/0/continuation/output/contract");
    }

    [Fact]
    public void Validate_DuplicateBindingProducers_AreAttributableToBothContinuations()
    {
        var transition = DefinitionReference("transition/review");
        ValueBindingId shared = new("shared.result");
        var definition = Definition(
            entry: "choice",
            nodes:
            [
                new ChoiceProcessNode(
                    new("choice"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Exhaustive,
                    [
                        new(new("case/a"), Expr.Const(true), Edge("edge/choice-a", "invoke/a")),
                        new(new("case/b"), Expr.Const(false), Edge("edge/choice-b", "invoke/b"))
                    ]),
                Invocation("invoke/a", transition, "edge/a-return", "return", shared),
                Invocation("invoke/b", transition, "edge/b-return", "return", shared),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var context = Context(
            new ProcessDefinitionLink(
                transition,
                ProcessDefinitionLinkKind.Transition,
                StringContract,
                StringContract));

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        var duplicate = AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.BindingProducerDuplicate,
            "/nodes/2/continuation/output/binding");
        Assert.Contains(
            "/nodes/1/continuation/output/binding",
            duplicate.Evidence?.RelatedLocations ?? [],
            StringComparer.Ordinal);
    }

    [Fact]
    public void Validate_BindingProducedOnOnlyOneChoiceBranch_IsNotVisibleAfterMerge()
    {
        var transition = DefinitionReference("transition/review");
        ValueBindingId branchOnly = new("branch.outcome");
        var definition = Definition(
            entry: "choice",
            nodes:
            [
                new ChoiceProcessNode(
                    new("choice"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Exhaustive,
                    [
                        new(new("case/produce"), Expr.Const(true), Edge("edge/choice-produce", "produce")),
                        new(new("case/skip"), Expr.Const(false), Edge("edge/choice-return", "return"))
                    ]),
                Invocation("produce", transition, "edge/produce-return", "return", branchOnly),
                new ReturnProcessNode(new("return"), Expr.BoundValue(branchOnly))
            ]);
        var context = Context(
            new ProcessDefinitionLink(
                transition,
                ProcessDefinitionLinkKind.Transition,
                StringContract,
                StringContract));

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        AssertDiagnostic(
            validation,
            ExprAnalysisDiagnosticCodes.BindingNotVisible,
            "/nodes/2/result");
    }

    [Fact]
    public void Validate_BindingProducedByForkSibling_IsNotVisibleInAnotherSibling()
    {
        var transition = DefinitionReference("transition/review");
        ValueBindingId siblingOnly = new("sibling.outcome");
        var definition = Definition(
            entry: "fork",
            nodes:
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/produce"), Edge("edge/fork-produce", "produce")),
                        new(new("branch/consume"), Edge("edge/fork-consume", "consume"))
                    ],
                    new("join")),
                Invocation("produce", transition, "edge/produce-join", "join", siblingOnly),
                new InvokeTransitionProcessNode(
                    new("consume"),
                    transition,
                    Expr.Const("case-2"),
                    Expr.BoundValue(siblingOnly),
                    new(Edge("edge/consume-join", "join"))),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    JoinPolicy(),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var context = Context(
            new ProcessDefinitionLink(
                transition,
                ProcessDefinitionLinkKind.Transition,
                StringContract,
                StringContract));

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        AssertDiagnostic(
            validation,
            ExprAnalysisDiagnosticCodes.BindingNotVisible,
            "/nodes/0/input");
    }

    [Fact]
    public void Validate_RequestOutcomesMustBeExhaustiveAndUseCatalogOutcomeContracts()
    {
        var requestDocument = InteractionDocument(
            "interaction/request/review",
            new RequestContractDefinition(
                StringSchema(),
                new RequestResponseObligation(
                    [
                        new RequestResultDefinition(new("approved"), StringSchema()),
                        new RequestFailureDefinition(new("failed"), BooleanSchema())
                    ],
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestResultDisposition.Reject,
                    RequestResultDisposition.Reject,
                    RequestResultDisposition.ReusePriorDisposition,
                    RequestRetrySemantics.Never,
                    RequestResolutionSemantics.TerminalFailure,
                    RequestResolutionSemantics.TerminalFailure,
                    TimeSpan.FromDays(7))));
        var request = new RequestContractReference(Reference(requestDocument));
        var definition = Definition(
            entry: "request",
            nodes:
            [
                new RequestProcessNode(
                    new("request"),
                    request,
                    Expr.Const("review"),
                    [
                        new(
                            new("outcome/approved"),
                            new("approved"),
                            Continue(
                                "edge/approved-return",
                                "return",
                                new("request.approved"),
                                BooleanContract))
                    ]),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var context = new ProcessDefinitionValidationContext(
            interactionContracts: Catalog(requestDocument));

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.RequestOutcomeMissing,
            "/nodes/0/outcomes");
        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.OutputContractMismatch,
            "/nodes/0/outcomes/0/continuation/output/contract");
    }

    [Fact]
    public void Validate_ForEachPartitionRequiresSingleElementBindingContract()
    {
        var child = DefinitionReference("process/partition-child");
        var requestDocument = InteractionDocument(
            "interaction/request/partition-child",
            SingleOutcomeRequestDefinition());
        var request = new RequestContractReference(Reference(requestDocument));
        var manyStrings = new ValueContract(
            new ScalarTypeRef(ScalarTypeKind.String),
            cardinality: FieldCardinality.Many);
        var definition = Definition(
            entry: "partitions",
            input: manyStrings,
            nodes:
            [
                new ForEachPartitionProcessNode(
                    new("partitions"),
                    Expr.BoundValue(ProcessBindingIds.Input),
                    new(new("partition"), manyStrings),
                    Expr.Const("partition-a"),
                    child,
                    request,
                    ChildOutcomeMapping(),
                    Expr.Const("review"),
                    new(maximumItems: 10, maximumStartsPerActivation: 2, maximumParallelism: 2),
                    ProcessPartitionFailurePolicy.FailFast,
                    capacityIdentity: null,
                    capacityDomains: [],
                    ProcessChildCancellationPolicy.Propagate,
                    Edge("edge/completed", "return"),
                    Edge("edge/failed", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var context = new ProcessDefinitionValidationContext(
            definitions:
            [
                new ProcessDefinitionLink(
                    child,
                    ProcessDefinitionLinkKind.Process,
                    StringContract,
                    StringContract,
                    processDependencies: [],
                    recoveryPolicy: ProcessRecoveryPolicy.ContinueAttempt)
            ],
            interactionContracts: Catalog(requestDocument));

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.PartitionBindingCardinalityInvalid,
            "/nodes/0/partition/contract/cardinality");
    }

    [Fact]
    public void Validate_ForEachPartitionRequiresExplicitFailureAndConsistentCapacityDomains()
    {
        var child = DefinitionReference("process/capacity-bound-partition-child");
        var requestDocument = InteractionDocument(
            "interaction/request/capacity-bound-partition-child",
            SingleOutcomeRequestDefinition());
        var request = new RequestContractReference(Reference(requestDocument));
        var manyStrings = new ValueContract(
            new ScalarTypeRef(ScalarTypeKind.String),
            cardinality: FieldCardinality.Many);
        ProcessDefinitionValidationContext context = new(
            definitions:
            [
                new ProcessDefinitionLink(
                    child,
                    ProcessDefinitionLinkKind.Process,
                    StringContract,
                    StringContract,
                    processDependencies: [],
                    recoveryPolicy: ProcessRecoveryPolicy.ContinueAttempt)
            ],
            interactionContracts: Catalog(requestDocument));

        var unspecifiedFailure = ProcessDefinitionValidator.Validate(
            CapacityDefinition(
                ProcessPartitionFailurePolicy.Unspecified,
                capacityIdentity: null,
                capacityDomains: []),
            context);
        AssertDiagnostic(
            unspecifiedFailure,
            ProcessDefinitionDiagnosticCodes.EnumUnsupported,
            "/nodes/0/failure");

        var limitsWithoutIdentity = ProcessDefinitionValidator.Validate(
            CapacityDefinition(
                ProcessPartitionFailurePolicy.FailFast,
                capacityIdentity: null,
                capacityDomains: [new("target/a", maximumParallelism: 1)]),
            context);
        AssertDiagnostic(
            limitsWithoutIdentity,
            ProcessDefinitionDiagnosticCodes.PartitionCapacityInvalid,
            "/nodes/0/capacityDomains");

        var identityWithoutLimits = ProcessDefinitionValidator.Validate(
            CapacityDefinition(
                ProcessPartitionFailurePolicy.FailFast,
                capacityIdentity: Expr.Const("target/a"),
                capacityDomains: []),
            context);
        AssertDiagnostic(
            identityWithoutLimits,
            ProcessDefinitionDiagnosticCodes.PartitionCapacityInvalid,
            "/nodes/0/capacityDomains");

        var malformedLimits = ProcessDefinitionValidator.Validate(
            CapacityDefinition(
                ProcessPartitionFailurePolicy.AwaitAll,
                capacityIdentity: Expr.BoundValue(new("partition")),
                capacityDomains:
                [
                    new("target/b", maximumParallelism: 1),
                    new("target/a", maximumParallelism: 0),
                    new("target/a", maximumParallelism: 2)
                ]),
            context);
        AssertDiagnostic(
            malformedLimits,
            ProcessDefinitionDiagnosticCodes.PartitionCapacityInvalid,
            "/nodes/0/capacityDomains/0/maximumParallelism");
        AssertDiagnostic(
            malformedLimits,
            ProcessDefinitionDiagnosticCodes.PartitionCapacityInvalid,
            "/nodes/0/capacityDomains/1/identity");

        CanonicalProcessDefinition CapacityDefinition(
            ProcessPartitionFailurePolicy failure,
            Expr? capacityIdentity,
            ImmutableArray<ProcessCapacityDomainLimit> capacityDomains) => Definition(
            entry: "partitions",
            input: manyStrings,
            nodes:
            [
                new ForEachPartitionProcessNode(
                    new("partitions"),
                    Expr.BoundValue(ProcessBindingIds.Input),
                    new(new("partition"), StringContract),
                    Expr.BoundValue(new("partition")),
                    child,
                    request,
                    ChildOutcomeMapping(),
                    Expr.BoundValue(new("partition")),
                    new(maximumItems: 10, maximumStartsPerActivation: 2, maximumParallelism: 2),
                    failure,
                    capacityIdentity,
                    capacityDomains,
                    ProcessChildCancellationPolicy.Propagate,
                    Edge("edge/completed", "return"),
                    Edge("edge/failed", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
    }

    [Fact]
    public void Validate_ChildProcessResultMustMatchEveryRequestResultOutcome()
    {
        var child = DefinitionReference("process/review-child");
        var requestDocument = InteractionDocument(
            "interaction/request/review-child",
            SingleOutcomeRequestDefinition());
        var request = new RequestContractReference(Reference(requestDocument));
        var definition = Definition(
            entry: "child",
            nodes:
            [
                new InvokeProcessProcessNode(
                    new("child"),
                    child,
                    request,
                    ChildOutcomeMapping(),
                    Expr.Const("review"),
                    ProcessChildPurpose.Work,
                    ProcessChildCancellationPolicy.Propagate,
                    [
                        new(
                            new("child/approved"),
                            new("approved"),
                            new(Edge("edge/approved", "return"))),
                        new(
                            new("child/failed"),
                            new("failed"),
                            new(Edge("edge/failed", "return")))
                    ]),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var context = new ProcessDefinitionValidationContext(
            definitions:
            [
                new ProcessDefinitionLink(
                    child,
                    ProcessDefinitionLinkKind.Process,
                    StringContract,
                    BooleanContract,
                    processDependencies: [],
                    recoveryPolicy: ProcessRecoveryPolicy.ContinueAttempt)
            ],
            interactionContracts: Catalog(requestDocument));

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        var diagnostic = AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.ChildRequestResultContractMismatch,
            "/nodes/0/contract");
        Assert.Equal("child", diagnostic.Evidence?.Subject);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("terminated")]
    public void Validate_MappedChildTerminalFailuresMayCarryProtocolSpecificEvidenceContracts(
        string distinctOutcome)
    {
        var child = DefinitionReference("process/review-child-failure");
        var requestDocument = InteractionDocument(
            "interaction/request/review-child-failure",
            new RequestContractDefinition(
                StringSchema(),
                new(
                    [
                        new RequestResultDefinition(new("approved"), StringSchema()),
                        new RequestFailureDefinition(new("failed"), FailureSchema("failed")),
                        new RequestFailureDefinition(new("cancelled"), FailureSchema("cancelled")),
                        new RequestFailureDefinition(new("terminated"), FailureSchema("terminated"))
                    ],
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestOptionalTerminalSemantics.Unsupported,
                    RequestResultDisposition.Reject,
                    RequestResultDisposition.Reject,
                    RequestResultDisposition.ReusePriorDisposition,
                    RequestRetrySemantics.Never,
                    RequestResolutionSemantics.TerminalFailure,
                    RequestResolutionSemantics.TerminalFailure,
                    TimeSpan.FromDays(7))));
        var request = new RequestContractReference(Reference(requestDocument));
        var definition = Definition(
            entry: "child",
            nodes:
            [
                new InvokeProcessProcessNode(
                    new("child"),
                    child,
                    request,
                    ChildOutcomeMapping(),
                    Expr.Const("review"),
                    ProcessChildPurpose.Work,
                    ProcessChildCancellationPolicy.Propagate,
                    [
                        new(new("child/approved"), new("approved"), new(Edge("edge/approved", "return"))),
                        new(new("child/failed"), new("failed"), new(Edge("edge/failed", "return"))),
                        new(new("child/cancelled"), new("cancelled"), new(Edge("edge/cancelled", "return"))),
                        new(new("child/terminated"), new("terminated"), new(Edge("edge/terminated", "return")))
                    ]),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var context = new ProcessDefinitionValidationContext(
            definitions:
            [
                new ProcessDefinitionLink(
                    child,
                    ProcessDefinitionLinkKind.Process,
                    StringContract,
                    StringContract,
                    processDependencies: [],
                    recoveryPolicy: ProcessRecoveryPolicy.ContinueAttempt)
            ],
            interactionContracts: Catalog(requestDocument));

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));

        InteractionValueSchema FailureSchema(string outcome) =>
            string.Equals(outcome, distinctOutcome, StringComparison.Ordinal)
                ? BooleanSchema()
                : StringSchema();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_ChildProcessRequiresContinueAttemptRecoveryAndDocumentFacadeMapsProcessReference(
        bool partitioned)
    {
        var child = DefinitionReference(partitioned ? "process/partition-restarted" : "process/direct-restarted");
        var requestDocument = InteractionDocument(
            partitioned ? "interaction/request/partition-restarted" : "interaction/request/direct-restarted",
            SingleOutcomeRequestDefinition());
        var request = new RequestContractReference(Reference(requestDocument));
        CanonicalProcessNode childNode = partitioned
            ? new ForEachPartitionProcessNode(
                new("child"),
                Expr.BoundValue(ProcessBindingIds.Input),
                new(new("partition"), StringContract),
                Expr.BoundValue(new("partition")),
                child,
                request,
                ChildOutcomeMapping(),
                Expr.BoundValue(new("partition")),
                new(maximumItems: 2, maximumStartsPerActivation: 1, maximumParallelism: 1),
                ProcessPartitionFailurePolicy.FailFast,
                capacityIdentity: null,
                capacityDomains: [],
                ProcessChildCancellationPolicy.Propagate,
                Edge("edge/completed", "return"),
                Edge("edge/failed", "return"))
            : new InvokeProcessProcessNode(
                new("child"),
                child,
                request,
                ChildOutcomeMapping(),
                Expr.BoundValue(ProcessBindingIds.Input),
                ProcessChildPurpose.Work,
                ProcessChildCancellationPolicy.Propagate,
                [
                    new(
                        new("child/approved"),
                        new("approved"),
                        new(Edge("edge/approved", "return"))),
                    new(
                        new("child/failed"),
                        new("failed"),
                        new(Edge("edge/failed", "return")))
                ]);
        var input = partitioned
            ? new ValueContract(new ScalarTypeRef(ScalarTypeKind.String), cardinality: FieldCardinality.Many)
            : StringContract;
        var definition = Definition(
            entry: "child",
            input: input,
            nodes:
            [
                childNode,
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var context = new ProcessDefinitionValidationContext(
            definitions:
            [
                new ProcessDefinitionLink(
                    child,
                    ProcessDefinitionLinkKind.Process,
                    StringContract,
                    StringContract,
                    processDependencies: [],
                    recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt)
            ],
            interactionContracts: Catalog(requestDocument));

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.ChildRecoveryPolicyUnsupported,
            "/nodes/0/process");

        var sourceReference = partitioned
            ? "src/PartitionCoordinator.cs:31"
            : "src/DirectCoordinator.cs:17";
        var document = ProcessDefinitionDocuments.Create(
            new(partitioned ? "process/partition-parent" : "process/direct-parent"),
            new("revision/1"),
            definition,
            Provenance(),
            sourceMap: new(
            [
                new(
                    sourceReference,
                    new(["nodes", "0", "process"]),
                    "Child recovery contract")
            ]));

        var documentValidation = ProcessDefinitionDocuments.Validate(document, context);

        var diagnostic = AssertDiagnostic(
            documentValidation,
            ProcessDefinitionDiagnosticCodes.ChildRecoveryPolicyUnsupported,
            "/definition/nodes/0/process");
        Assert.Equal([sourceReference], diagnostic.Evidence?.SourceReferences);
    }

    [Fact]
    public void Validate_InvokeProcessWithMissingProcessReference_RemainsAChildInvocation()
    {
        var definition = Definition(
            entry: "child",
            nodes:
            [
                new InvokeProcessProcessNode(
                    new("child"),
                    process: null!,
                    new(DefinitionReference("interaction/request/child")),
                    ChildOutcomeMapping(),
                    Expr.Const("review"),
                    ProcessChildPurpose.Work,
                    ProcessChildCancellationPolicy.Propagate,
                    [
                        new(
                            new("child/completed"),
                            new("completed"),
                            new(Edge("edge/completed", "return")))
                    ]),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.DefinitionReferenceInvalid,
            "/nodes/0/process");
    }

    [Fact]
    public void Validate_RepeatAcrossActivationWithMissingEdge_ReturnsDiagnosticsWithoutThrowing()
    {
        var definition = Definition(
            entry: "repeat",
            nodes:
            [
                new RepeatAcrossActivationProcessNode(
                    new("repeat"),
                    Expr.Const(false),
                    Expr.Const("progress"),
                    StringContract,
                    new(maximumOccurrences: 10, maximumUnchangedProgressOccurrences: 2),
                    repeat: null!,
                    Edge("edge/completed", "return"),
                    Edge("edge/exhausted", "return"),
                    Edge("edge/stalled", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.RequiredMemberMissing,
            "/nodes/0/repeat");
    }

    [Fact]
    public void DocumentFacade_PrioritizesProvenRecursionOverMissingSiblingEvidence()
    {
        var child = DefinitionReference("process/child");
        var requestDocument = InteractionDocument(
            "interaction/request/child",
            SingleOutcomeRequestDefinition());
        var request = new RequestContractReference(Reference(requestDocument));
        var definition = Definition(
            entry: "child",
            nodes:
            [
                new InvokeProcessProcessNode(
                    new("child"),
                    child,
                    request,
                    ChildOutcomeMapping(),
                    Expr.Const("review"),
                    ProcessChildPurpose.Work,
                    ProcessChildCancellationPolicy.Propagate,
                    [
                        new(
                            new("child/approved"),
                            new("approved"),
                            new(Edge("edge/approved", "return"))),
                        new(
                            new("child/failed"),
                            new("failed"),
                            new(Edge("edge/failed", "return")))
                    ]),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var document = ProcessDefinitionDocuments.Create(
            new("process/root"),
            new("revision/1"),
            definition,
            Provenance());
        var context = new ProcessDefinitionValidationContext(
            definitions:
            [
                new ProcessDefinitionLink(
                    child,
                    ProcessDefinitionLinkKind.Process,
                    StringContract,
                    StringContract,
                    processDependencies:
                    [
                        DefinitionReference("process/aaa-missing"),
                        Reference(document)
                    ],
                    recoveryPolicy: ProcessRecoveryPolicy.ContinueAttempt)
            ],
            interactionContracts: Catalog(requestDocument));

        var validation = ProcessDefinitionDocuments.Validate(document, context);

        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.ProcessRecursionUnsupported,
            "/definition/nodes/0/process");
        Assert.DoesNotContain(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessDefinitionDiagnosticCodes.ProcessDependencyEvidenceMissing);
    }

    [Fact]
    public void DocumentFacade_RejectsDirectChildRecursionByStableDefinitionRevision()
    {
        var definition = Definition(
            entry: "child",
            nodes:
            [
                new InvokeProcessProcessNode(
                    new("child"),
                    DefinitionReference("process/self"),
                    new(DefinitionReference("interaction/request/self")),
                    ChildOutcomeMapping(),
                    Expr.Const("input"),
                    ProcessChildPurpose.Work,
                    ProcessChildCancellationPolicy.Propagate,
                    [
                        new(
                            new("child/completed"),
                            new("completed"),
                            new(Edge("edge/completed", "return")))
                    ]),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var document = ProcessDefinitionDocuments.Create(
            new("process/self"),
            new("revision/1"),
            definition,
            Provenance());

        var validation = ProcessDefinitionDocuments.Validate(document);

        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.ProcessRecursionUnsupported,
            "/definition/nodes/0/process");
    }

    [Fact]
    public void Validate_ForkAndJoinMustBeReciprocalAndEveryBranchMustConverge()
    {
        var definition = Definition(
            entry: "fork",
            nodes:
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/converges"), Edge("edge/fork-cut", "cut")),
                        new(new("branch/escapes"), Edge("edge/fork-escape", "escape"))
                    ],
                    new("join")),
                new DurableCutProcessNode(new("cut"), Edge("edge/cut-join", "join")),
                new ReturnProcessNode(new("escape"), Expr.Const("escaped")),
                new JoinProcessNode(
                    new("join"),
                    new("other-fork"),
                    JoinPolicy(),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.JoinForkUnresolved, "/nodes/3/fork");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.ForkJoinNotReciprocal, "/nodes/2/join");
        var convergence = AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.ForkBranchDoesNotConverge,
            "/nodes/2/branches/1/start");
        Assert.Equal("branch/escapes", convergence.Evidence?.Subject);
    }

    [Fact]
    public void Validate_MissingForkBranchEdge_RemainsDiagnosticOnly()
    {
        var definition = Definition(
            entry: "fork",
            nodes:
            [
                new ForkProcessNode(
                    new("fork"),
                    [new(new("branch/missing-start"), null!)],
                    new("join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    JoinPolicy(),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.RequiredMemberMissing,
            "/nodes/0/branches/0/start");
    }

    [Fact]
    public void Validate_JoinPoliciesRequireSupportedValuesAndCoherentThresholds()
    {
        var policy = new ProcessJoinPolicy(
            ProcessJoinMode.RequiredCount,
            3,
            ProcessJoinFailurePolicy.Unspecified,
            ProcessJoinCancellationPolicy.Unspecified,
            ProcessJoinCompletionOrder.Unspecified,
            ProcessJoinTieBreak.Unspecified);
        var definition = ForkJoinDefinition(policy);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.JoinRequiredCountInvalid, "/nodes/3/policy/requiredCount");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EnumUnsupported, "/nodes/3/policy/failure");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EnumUnsupported, "/nodes/3/policy/cancellation");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EnumUnsupported, "/nodes/3/policy/completionOrder");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EnumUnsupported, "/nodes/3/policy/tieBreak");
    }

    [Fact]
    public void Validate_JoinResultProjectionRequiresPartialModeAndEveryReciprocalBranch()
    {
        var definition = Definition(
            entry: "fork",
            nodes:
            [
                new ForkProcessNode(
                    new("fork"),
                    [
                        new(new("branch/a"), Edge("edge/fork-a", "a")),
                        new(new("branch/b"), Edge("edge/fork-b", "b"))
                    ],
                    new("join")),
                new DurableCutProcessNode(new("a"), Edge("edge/a-join", "join")),
                new DurableCutProcessNode(new("b"), Edge("edge/b-join", "join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    new ProcessJoinPolicy(
                        mode: ProcessJoinMode.All,
                        requiredCount: 0,
                        failure: ProcessJoinFailurePolicy.FailFast,
                        cancellation: ProcessJoinCancellationPolicy.AwaitRemaining,
                        completionOrder: ProcessJoinCompletionOrder.Unobservable,
                        tieBreak: ProcessJoinTieBreak.BranchIdentity),
                    Edge("edge/join-return", "return"),
                    new(
                        new(new("join.result"), StringContract),
                        StringContract,
                        [new(new("branch/a"), Expr.Const("a"))])),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.JoinResultProjectionInvalid,
            "/nodes/3/result");
    }

    [Fact]
    public void Validate_AwaitMatchRequiresClausesPoliciesAndPositiveRetention()
    {
        var definition = Definition(
            entry: "await",
            nodes:
            [
                new AwaitMatchProcessNode(
                    new("await"),
                    ProcessAwaitArbitration.Unspecified,
                    [],
                    ProcessAwaitInputDisposition.Unspecified,
                    ProcessAwaitInputDisposition.Unspecified,
                    ProcessAwaitInputDisposition.Unspecified,
                    ProcessAwaitMissingTargetDisposition.Unspecified,
                    TimeSpan.Zero)
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.AwaitClausesEmpty, "/nodes/0/clauses");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EnumUnsupported, "/nodes/0/arbitration");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EnumUnsupported, "/nodes/0/lateInput");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EnumUnsupported, "/nodes/0/staleInput");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EnumUnsupported, "/nodes/0/duplicateInput");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.EnumUnsupported, "/nodes/0/missingTarget");
        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.AwaitRetentionInvalid, "/nodes/0/retentionHorizon");
    }

    [Fact]
    public void Validate_AwaitMatchRejectsDuplicateClausesContractMismatchAndNonBooleanGuard()
    {
        var signalDocument = InteractionDocument(
            "interaction/signal/review",
            new SignalContractDefinition(StringSchema()));
        var signal = new SignalContractReference(Reference(signalDocument));
        var catalog = Catalog(signalDocument);
        var first = new ProcessAwaitInteractionClause(
            new("review"),
            signal,
            new(new("review.input"), BooleanContract),
            null,
            Expr.Const("not-boolean"),
            1,
            new(Edge("edge/review-return", "return")));
        var duplicate = new ProcessAwaitInteractionClause(
            new("review"),
            signal,
            new(new("review.duplicate"), StringContract),
            null,
            null,
            0,
            new(Edge("edge/duplicate-return", "return")));
        var definition = Definition(
            entry: "await",
            nodes:
            [
                Await("await", [first, duplicate]),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(
            definition,
            new ProcessDefinitionValidationContext(interactionContracts: catalog));

        var duplicateDiagnostic = AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.AwaitClauseIdentityDuplicate,
            "/nodes/0/clauses/1/id");
        Assert.Contains("/nodes/0/clauses/0/id", duplicateDiagnostic.Evidence?.RelatedLocations ?? [], StringComparer.Ordinal);
        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.AwaitInputContractMismatch,
            "/nodes/0/clauses/0/input/contract");
        AssertDiagnostic(
            validation,
            ExprAnalysisDiagnosticCodes.ResultCategoryMismatch,
            "/nodes/0/clauses/0/guard");
    }

    [Fact]
    public void Validate_ExternalDefinitionReferencesFailClosedWhenIncompleteUnresolvedOrWrongKind()
    {
        var unresolved = DefinitionReference("transition/unresolved");
        var wrongKind = DefinitionReference("relation/wrong-kind");
        var incompleteDefinition = Definition(
            entry: "invoke",
            nodes:
            [
                new InvokeTransitionProcessNode(
                    new("invoke"),
                    null!,
                    Expr.Const("case-1"),
                    Expr.Const("review"),
                    new(Edge("edge/invoke-return", "return"))),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var unresolvedDefinition = InvocationDefinition(unresolved);
        var wrongKindDefinition = InvocationDefinition(wrongKind);
        var emptyContext = new ProcessDefinitionValidationContext();
        var wrongKindContext = Context(
            new ProcessDefinitionLink(
                wrongKind,
                ProcessDefinitionLinkKind.RelationQuery,
                StringContract,
                StringContract));

        var incompleteValidation = ProcessDefinitionValidator.Validate(incompleteDefinition, emptyContext);
        var unresolvedValidation = ProcessDefinitionValidator.Validate(unresolvedDefinition, emptyContext);
        var wrongKindValidation = ProcessDefinitionValidator.Validate(wrongKindDefinition, wrongKindContext);

        AssertDiagnostic(incompleteValidation, ProcessDefinitionDiagnosticCodes.DefinitionReferenceInvalid, "/nodes/0/transition");
        AssertDiagnostic(unresolvedValidation, ProcessDefinitionDiagnosticCodes.DefinitionReferenceUnresolved, "/nodes/0/transition");
        AssertDiagnostic(wrongKindValidation, ProcessDefinitionDiagnosticCodes.DefinitionReferenceKindMismatch, "/nodes/0/transition");
    }

    [Fact]
    public void Validate_InteractionReferencesFailClosedWhenUnresolvedOrWrongKind()
    {
        var signalDocument = InteractionDocument(
            "interaction/signal/known",
            new SignalContractDefinition(StringSchema()));
        var eventDocument = InteractionDocument(
            "interaction/event/not-awaitable",
            new DomainEventContractDefinition(StringSchema()));
        var catalog = Catalog(signalDocument, eventDocument);
        var unresolvedSignal = new SignalContractReference(DefinitionReference("interaction/signal/missing"));
        var wrongKindEvent = new SignalContractReference(Reference(eventDocument));
        var unresolvedDefinition = AwaitInteractionDefinition(unresolvedSignal);
        var wrongKindDefinition = AwaitInteractionDefinition(wrongKindEvent);
        var context = new ProcessDefinitionValidationContext(interactionContracts: catalog);

        var unresolvedValidation = ProcessDefinitionValidator.Validate(unresolvedDefinition, context);
        var wrongKindValidation = ProcessDefinitionValidator.Validate(wrongKindDefinition, context);

        AssertDiagnostic(unresolvedValidation, ProcessDefinitionDiagnosticCodes.InteractionReferenceUnresolved, "/nodes/0/clauses/0/contract");
        AssertDiagnostic(wrongKindValidation, ProcessDefinitionDiagnosticCodes.InteractionReferenceKindMismatch, "/nodes/0/clauses/0/contract");
    }

    [Fact]
    public void Validate_AwaitedRequestObligationMustBeRetainedAndDefinitelyVisibleToReply()
    {
        var requestDocument = InteractionDocument(
            "interaction/request/review",
            SingleOutcomeRequestDefinition());
        var request = new RequestContractReference(Reference(requestDocument));
        var replyDocument = InteractionDocument(
            "interaction/reply/reviewed",
            new ReplyContractDefinition(request, new("approved")));
        var reply = new ReplyContractReference(Reference(replyDocument));
        RequestObligationBindingId obligation = new("review.request");
        var valid = Definition(
            entry: "await",
            nodes:
            [
                Await(
                    "await",
                    [
                        new ProcessAwaitInteractionClause(
                            new("request"),
                            request,
                            new(new("request.payload"), StringContract),
                            new(obligation),
                            null,
                            0,
                            new(Edge("edge/await-reply", "reply")))
                    ]),
                new ReplyProcessNode(
                    new("reply"),
                    reply,
                    obligation,
                    Expr.Const("approved"),
                    Edge("edge/reply-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var missingBinding = Definition(
            entry: "await",
            nodes:
            [
                Await(
                    "await",
                    [
                        new ProcessAwaitInteractionClause(
                            new("request"),
                            request,
                            new(new("request.payload"), StringContract),
                            null,
                            null,
                            0,
                            new(Edge("edge/await-reply", "reply")))
                    ]),
                new ReplyProcessNode(
                    new("reply"),
                    reply,
                    new("missing.request"),
                    Expr.Const("approved"),
                    Edge("edge/reply-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var context = new ProcessDefinitionValidationContext(
            interactionContracts: Catalog(requestDocument, replyDocument));

        var validResult = ProcessDefinitionValidator.Validate(valid, context);
        var missingResult = ProcessDefinitionValidator.Validate(missingBinding, context);

        Assert.True(validResult.IsValid, FormatDiagnostics(validResult));
        AssertDiagnostic(
            missingResult,
            ProcessDefinitionDiagnosticCodes.AwaitRequestObligationInvalid,
            "/nodes/0/clauses/0/requestObligation");
        AssertDiagnostic(
            missingResult,
            ProcessDefinitionDiagnosticCodes.ReplyRequestObligationUnresolved,
            "/nodes/1/request");
    }

    [Fact]
    public void Validate_ReplyMustDischargeTheExactAwaitedRequestContract()
    {
        var firstRequestDocument = InteractionDocument(
            "interaction/request/first",
            SingleOutcomeRequestDefinition());
        var secondRequestDocument = InteractionDocument(
            "interaction/request/second",
            SingleOutcomeRequestDefinition());
        var firstRequest = new RequestContractReference(Reference(firstRequestDocument));
        var secondRequest = new RequestContractReference(Reference(secondRequestDocument));
        var replyDocument = InteractionDocument(
            "interaction/reply/second",
            new ReplyContractDefinition(secondRequest, new("approved")));
        RequestObligationBindingId obligation = new("first.request");
        var definition = Definition(
            entry: "await",
            nodes:
            [
                Await(
                    "await",
                    [
                        new ProcessAwaitInteractionClause(
                            new("request"),
                            firstRequest,
                            new(new("request.payload"), StringContract),
                            new(obligation),
                            null,
                            0,
                            new(Edge("edge/await-reply", "reply")))
                    ]),
                new ReplyProcessNode(
                    new("reply"),
                    new(Reference(replyDocument)),
                    obligation,
                    Expr.Const("approved"),
                    Edge("edge/reply-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var context = new ProcessDefinitionValidationContext(
            interactionContracts: Catalog(firstRequestDocument, secondRequestDocument, replyDocument));

        var validation = ProcessDefinitionValidator.Validate(definition, context);

        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.ReplyRequestContractMismatch,
            "/nodes/1/request");
    }

    [Fact]
    public void Validate_FreeSelfAndMultiNodeCyclesAreRejected_ButDurableCutBreaksAnActivation()
    {
        var selfCycle = Definition(
            entry: "choice",
            nodes:
            [
                Choice("choice", Edge("edge/self", "choice"))
            ]);
        var multiNodeCycle = Definition(
            entry: "a",
            nodes:
            [
                Choice("a", Edge("edge/a-b", "b")),
                Choice("b", Edge("edge/b-a", "a"))
            ]);
        var durableRecurrence = Definition(
            entry: "cut",
            nodes:
            [
                new DurableCutProcessNode(new("cut"), Edge("edge/resume", "cut"))
            ]);

        var selfValidation = ProcessDefinitionValidator.Validate(selfCycle);
        var multiValidation = ProcessDefinitionValidator.Validate(multiNodeCycle);
        var durableValidation = ProcessDefinitionValidator.Validate(durableRecurrence);

        AssertDiagnostic(selfValidation, ProcessDefinitionDiagnosticCodes.FreeActivationCycle, "/nodes/0/cases/0/next");
        Assert.Contains(
            multiValidation.Diagnostics,
            diagnostic => diagnostic.Code == ProcessDefinitionDiagnosticCodes.FreeActivationCycle);
        Assert.True(durableValidation.IsValid, FormatDiagnostics(durableValidation));
    }

    [Fact]
    public void Validate_DurableBoundaryOnOneBranchCannotMaskAnotherInfiniteActivationPath()
    {
        var definition = Definition(
            entry: "choice",
            nodes:
            [
                new ChoiceProcessNode(
                    new("choice"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [new(new("loop"), Expr.Const(true), Edge("edge/loop", "choice"))],
                    new(new("stop"), Edge("edge/stop", "cut"))),
                new DurableCutProcessNode(new("cut"), Edge("edge/resume", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(validation, ProcessDefinitionDiagnosticCodes.FreeActivationCycle, "/nodes/0/cases/0/next");
    }

    [Fact]
    public void Validate_ExhaustiveChoiceRequiresProvenCoverage()
    {
        var disproven = Definition(
            entry: "choice",
            nodes:
            [
                new ChoiceProcessNode(
                    new("choice"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Exhaustive,
                    [new(new("never"), Expr.Const(false), Edge("edge/never", "return"))]),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);
        var unknown = Definition(
            entry: "choice",
            nodes:
            [
                new ChoiceProcessNode(
                    new("choice"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Exhaustive,
                    [new(new("dynamic"), Expr.Eq(Expr.BoundValue(ProcessBindingIds.Input), Expr.Const("go")), Edge("edge/go", "return"))]),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var disprovenValidation = ProcessDefinitionValidator.Validate(disproven);
        var unknownValidation = ProcessDefinitionValidator.Validate(unknown);

        AssertDiagnostic(
            disprovenValidation,
            ProcessDefinitionDiagnosticCodes.ExhaustivenessDisproven,
            "/nodes/0/completeness");
        AssertDiagnostic(
            unknownValidation,
            ProcessDefinitionDiagnosticCodes.ExhaustivenessUnknown,
            "/nodes/0/completeness");
    }

    [Fact]
    public void Validate_ExhaustiveMatchRequiresTheKnownValueToBeCovered()
    {
        var definition = Definition(
            entry: "match",
            nodes:
            [
                new MatchProcessNode(
                    new("match"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Exhaustive,
                    Expr.Const("rejected"),
                    StringContract,
                    [new(new("approved"), ConcreteString("approved"), Edge("edge/approved", "return"))]),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var validation = ProcessDefinitionValidator.Validate(definition);

        AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.ExhaustivenessDisproven,
            "/nodes/0/completeness");
    }

    [Fact]
    public void Validate_UnreachableNodesAndForkDeadEnds_AreAttributable()
    {
        var unreachable = Definition(
            entry: "return",
            nodes:
            [
                new ReturnProcessNode(new("return"), Expr.Const("done")),
                new ReturnProcessNode(new("unreachable"), Expr.Const("unused"))
            ]);
        var deadEnd = Definition(
            entry: "fork",
            nodes:
            [
                new ForkProcessNode(new("fork"), [], new("join")),
                new JoinProcessNode(
                    new("join"),
                    new("fork"),
                    JoinPolicy(),
                    Edge("edge/join-return", "return")),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ]);

        var unreachableValidation = ProcessDefinitionValidator.Validate(unreachable);
        var deadEndValidation = ProcessDefinitionValidator.Validate(deadEnd);

        AssertDiagnostic(unreachableValidation, ProcessDefinitionDiagnosticCodes.NodeUnreachable, "/nodes/1");
        AssertDiagnostic(deadEndValidation, ProcessDefinitionDiagnosticCodes.ForkBranchesEmpty, "/nodes/0/branches");
    }

    [Fact]
    public void DocumentFacade_PrefixesValidatorLocationAndResolvesTheDeepestSourceMap()
    {
        var definition = Definition(
            entry: "cut",
            nodes:
            [
                new DurableCutProcessNode(
                    new("cut"),
                    Edge("edge/cut-missing", "missing"))
            ]);
        var sourceMap = new ExecutionSourceMap(
        [
            new(
                "src/ReviewProcess.cs:42",
                new(["nodes", "0"]),
                "Invalid authored node")
        ]);
        var document = ProcessDefinitionDocuments.Create(
            new("process/review"),
            new("revision/1"),
            definition,
            Provenance(),
            sourceMap: sourceMap);

        var validation = ProcessDefinitionDocuments.Validate(document);

        var diagnostic = AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.EdgeTargetUnresolved,
            "/definition/nodes/0/resume/target");
        Assert.Equal("processValidation", diagnostic.Evidence?.Stage);
        Assert.Equal(["src/ReviewProcess.cs:42"], diagnostic.Evidence?.SourceReferences);
    }

    [Fact]
    public void DocumentFacade_PrefixesRelatedDefinitionLocationsWithThePrimaryDiagnostic()
    {
        var definition = Definition(
            entry: "same",
            nodes:
            [
                new ReturnProcessNode(new("same"), Expr.Const("first")),
                new ReturnProcessNode(new("same"), Expr.Const("second"))
            ]);
        var document = ProcessDefinitionDocuments.Create(
            new("process/duplicate-nodes"),
            new("revision/1"),
            definition,
            Provenance());

        var validation = ProcessDefinitionDocuments.Validate(document);

        var diagnostic = AssertDiagnostic(
            validation,
            ProcessDefinitionDiagnosticCodes.NodeIdentityDuplicate,
            "/definition/nodes/1/id");
        Assert.Equal(["/definition/nodes/0/id"], diagnostic.Evidence?.RelatedLocations);
    }

    static CanonicalProcessDefinition ForkJoinDefinition(ProcessJoinPolicy policy) => Definition(
        entry: "fork",
        nodes:
        [
            new ForkProcessNode(
                new("fork"),
                [
                    new(new("branch/a"), Edge("edge/fork-a", "a")),
                    new(new("branch/b"), Edge("edge/fork-b", "b"))
                ],
                new("join")),
            new DurableCutProcessNode(new("a"), Edge("edge/a-join", "join")),
            new DurableCutProcessNode(new("b"), Edge("edge/b-join", "join")),
            new JoinProcessNode(
                new("join"),
                new("fork"),
                policy,
                Edge("edge/join-return", "return")),
            new ReturnProcessNode(new("return"), Expr.Const("done"))
        ]);

    static CanonicalProcessDefinition InvocationDefinition(ExecutionDefinitionReference transition) => Definition(
        entry: "invoke",
        nodes:
        [
            Invocation("invoke", transition, "edge/invoke-return", "return", new("review.outcome")),
            new ReturnProcessNode(new("return"), Expr.Const("done"))
        ]);

    static CanonicalProcessDefinition AwaitInteractionDefinition(InteractionContractReference contract) => Definition(
        entry: "await",
        nodes:
        [
            Await(
                "await",
                [
                    new ProcessAwaitInteractionClause(
                        new("interaction"),
                        contract,
                        new(new("interaction.input"), StringContract),
                        null,
                        null,
                        0,
                        new(Edge("edge/interaction-return", "return")))
                ]),
            new ReturnProcessNode(new("return"), Expr.Const("done"))
        ]);

    static CanonicalProcessDefinition Definition(
        string entry,
        ImmutableArray<CanonicalProcessNode> nodes,
        ValueContract? input = null,
        ValueContract? result = null) => new(
            input ?? StringContract,
            result ?? StringContract,
            new(entry),
            nodes,
            ProcessRecoveryPolicy.ContinueAttempt);

    static InvokeTransitionProcessNode Invocation(
        string id,
        ExecutionDefinitionReference transition,
        string edgeId,
        string target,
        ValueBindingId output) => new(
            new(id),
            transition,
            Expr.Const("case-1"),
            Expr.Const("review"),
            Continue(edgeId, target, output, StringContract));

    static ChoiceProcessNode Choice(string id, ProcessEdge next) => new(
        new(id),
        CaseSelection.OrderedFirstMatch,
        BranchCompleteness.Exhaustive,
        [new(new($"{id}/case"), Expr.Const(true), next)]);

    static AwaitMatchProcessNode Await(
        string id,
        ImmutableArray<ProcessAwaitClause> clauses) => new(
            new(id),
            ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
            clauses,
            ProcessAwaitInputDisposition.Observe,
            ProcessAwaitInputDisposition.Reject,
            ProcessAwaitInputDisposition.ReusePriorDisposition,
            ProcessAwaitMissingTargetDisposition.DeadLetter,
            TimeSpan.FromDays(7));

    static ProcessContinuation Continue(
        string edgeId,
        string target,
        ValueBindingId output,
        ValueContract contract) => new(
            Edge(edgeId, target),
            new(output, contract));

    static ProcessJoinPolicy JoinPolicy() => new(
        ProcessJoinMode.All,
        0,
        ProcessJoinFailurePolicy.FailFast,
        ProcessJoinCancellationPolicy.AwaitRemaining,
        ProcessJoinCompletionOrder.Unobservable,
        ProcessJoinTieBreak.BranchIdentity);

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static ObservationValue Instant() => ObservationValue.FromDateTimeOffset(
        new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

    static ProcessDefinitionValidationContext Context(params ProcessDefinitionLink[] definitions) =>
        new(definitions);

    static ExecutionDefinitionReference DefinitionReference(
        string definitionId,
        char fingerprintDigit = '1') => new(
            new(definitionId),
            new("revision/1"),
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                new string(fingerprintDigit, 64)));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId,
        document.Metadata.RevisionId,
        document.Metadata.Fingerprint);

    static InteractionValueSchema StringSchema() => new(StringContract, new("schema/v1"));

    static InteractionValueSchema BooleanSchema() => new(BooleanContract, new("schema/v1"));

    static PortableValue ConcreteString(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    static RequestContractDefinition SingleOutcomeRequestDefinition() => new(
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

    static ProcessChildOutcomeMapping ChildOutcomeMapping() => new(
        new("approved"),
        new("failed"),
        new("cancelled"),
        new("terminated"));

    static ExecutionDefinitionDocument InteractionDocument(
        string id,
        InteractionContractDefinition definition) => InteractionContractDocuments.Create(
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

    static ExecutionProvenance Provenance() => new(
        new("process-validator-tests", "1"),
        new("tests/execution-kernel/process-validator"),
        DocumentOrigin.Generated);

    static DocumentValidationDiagnostic AssertDiagnostic(
        DocumentValidationResult validation,
        string code,
        string location)
    {
        var diagnostic = Assert.Single(
            validation.Diagnostics,
            candidate => string.Equals(candidate.Code, code, StringComparison.Ordinal)
                         && string.Equals(candidate.Location, location, StringComparison.Ordinal));
        return diagnostic;
    }

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

}
