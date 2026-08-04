using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class CanonicalProcessIrTests
{
    static readonly ValueContract BooleanContract = new(new ScalarTypeRef(ScalarTypeKind.Bool));
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ProcessChildOutcomeMapping ChildOutcomeMapping = new(
        new("succeeded"),
        new("failed"),
        new("cancelled"),
        new("terminated"));

    [Fact]
    public void RepresentativeDirectIr_RoundTripsThroughSharedEnvelopeDeterministically()
    {
        var definition = RepresentativeDefinition();
        var document = CreateDocument(definition);
        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(document);

        var validation = ProcessDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        Assert.NotNull(restoredDocument);
        Assert.NotNull(restoredDefinition);
        Assert.Equal(ProcessDefinitionDocuments.Kind, document.Kind);
        Assert.Equal(document.Metadata.Fingerprint, restoredDocument.Metadata.Fingerprint);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument));
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(restoredDocument));
        Assert.Equal(definition.Nodes.Length, restoredDefinition.Nodes.Length);
        for (var index = 0; index < definition.Nodes.Length; index++)
        {
            if (definition.Nodes[index] is TimerProcessNode expectedTimer
                && restoredDefinition.Nodes[index] is TimerProcessNode actualTimer)
            {
                Assert.Equal(expectedTimer.Id, actualTimer.Id);
                Assert.Equal(expectedTimer.DueAt, actualTimer.DueAt);
                Assert.Equal(expectedTimer.Next, actualTimer.Next);
            }
            Assert.True(
                definition.Nodes[index].Equals(restoredDefinition.Nodes[index]),
                $"Node {index} ({definition.Nodes[index].GetType().Name}) did not preserve structural equality.");
        }
        Assert.Equal(definition, restoredDefinition);
        Assert.Equal(
            "e3e5f9db7e4a5f1de252aebbb87f6317eb0371a76e1a7078f193454e4e9950b7",
            document.Metadata.Fingerprint.Value);
    }

    [Fact]
    public void EveryClosedNodeVariant_RoundTripsWithItsStableWireDiscriminator()
    {
        var next = Edge("edge/next", "terminal");
        var continuation = new ProcessContinuation(next);
        CanonicalProcessNode[] nodes =
        [
            new InvokeTransitionProcessNode(
                new("invoke"),
                DefinitionReference("transition/review"),
                Expr.Const("case-1"),
                Expr.Const("approve"),
                continuation),
            new EvaluateRelationProcessNode(
                new("evaluate"),
                DefinitionReference("query/review"),
                Expr.Const("case-1"),
                continuation),
            new RequestProcessNode(
                new("request"),
                new(DefinitionReference("request/review")),
                Expr.Const("review"),
                [new(new("request/succeeded"), new("succeeded"), continuation)]),
            new EmitEventProcessNode(
                new("emit"),
                new(DefinitionReference("event/reviewed")),
                Expr.Const("reviewed"),
                next),
            new SendSignalProcessNode(
                new("signal"),
                new(DefinitionReference("signal/review")),
                Expr.Const("reviewer-1"),
                Expr.Const("review"),
                next),
            new ChoiceProcessNode(
                new("choice"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Exhaustive,
                [new(new("choice/yes"), Expr.Const(true), next)]),
            new MatchProcessNode(
                new("match"),
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Exhaustive,
                Expr.Const("approved"),
                StringContract,
                [new(new("match/approved"), ConcreteString("approved"), next)]),
            new ForkProcessNode(
                new("fork"),
                [new(new("fork/a"), next)],
                new("join")),
            new JoinProcessNode(
                new("join"),
                new("fork"),
                JoinPolicy(),
                next),
            new AwaitMatchProcessNode(
                new("await"),
                ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                [new ProcessAwaitTimerClause(new("await/timeout"), InstantExpression(), 0, continuation)],
                ProcessAwaitInputDisposition.Observe,
                ProcessAwaitInputDisposition.Reject,
                ProcessAwaitInputDisposition.ReusePriorDisposition,
                ProcessAwaitMissingTargetDisposition.DeadLetter,
                TimeSpan.FromDays(7)),
            new TimerProcessNode(new("timer"), InstantExpression(), next),
            new ReplyProcessNode(
                new("reply"),
                new(DefinitionReference("reply/reviewed")),
                new("request-1"),
                Expr.Const("approved"),
                next),
            new DurableCutProcessNode(new("cut"), next),
            new InvokeProcessProcessNode(
                new("invoke-process"),
                DefinitionReference("process/review"),
                new(DefinitionReference("request/process-review")),
                ChildOutcomeMapping,
                Expr.Const("review"),
                ProcessChildPurpose.Work,
                ProcessChildCancellationPolicy.Propagate,
                [new(new("invoke-process/succeeded"), new("succeeded"), continuation)]),
            new ForEachPartitionProcessNode(
                new("for-each-partition"),
                Expr.Const("partition-a"),
                new(new("partition"), StringContract),
                Expr.Const("partition-a"),
                DefinitionReference("process/review-partition"),
                new(DefinitionReference("request/process-review-partition")),
                ChildOutcomeMapping,
                Expr.Const("review"),
                new(maximumItems: 10, maximumStartsPerActivation: 2, maximumParallelism: 2),
                ProcessPartitionFailurePolicy.FailFast,
                capacityIdentity: null,
                capacityDomains: [],
                ProcessChildCancellationPolicy.Propagate,
                next,
                next),
            new RepeatAcrossActivationProcessNode(
                new("repeat"),
                Expr.Const(true),
                Expr.Const("progress"),
                StringContract,
                new(maximumOccurrences: 10, maximumUnchangedProgressOccurrences: 2),
                next,
                next,
                next,
                next),
            new ReturnProcessNode(new("return"), Expr.Const("approved")),
            new FailProcessNode(new("fail"), Expr.Const("rejected"))
        ];
        string[] discriminators =
        [
            ProcessWireNames.InvokeTransitionNode,
            ProcessWireNames.EvaluateRelationNode,
            ProcessWireNames.RequestNode,
            ProcessWireNames.EmitEventNode,
            ProcessWireNames.SendSignalNode,
            ProcessWireNames.ChoiceNode,
            ProcessWireNames.MatchNode,
            ProcessWireNames.ForkNode,
            ProcessWireNames.JoinNode,
            ProcessWireNames.AwaitMatchNode,
            ProcessWireNames.TimerNode,
            ProcessWireNames.ReplyNode,
            ProcessWireNames.DurableCutNode,
            ProcessWireNames.InvokeProcessNode,
            ProcessWireNames.ForEachPartitionNode,
            ProcessWireNames.RepeatAcrossActivationNode,
            ProcessWireNames.ReturnNode,
            ProcessWireNames.FailNode
        ];
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();

        for (var index = 0; index < nodes.Length; index++)
        {
            var json = JsonSerializer.Serialize<CanonicalProcessNode>(nodes[index], options);
            var restored = JsonSerializer.Deserialize<CanonicalProcessNode>(json, options);

            Assert.NotNull(restored);
            Assert.IsType(nodes[index].GetType(), restored);
            Assert.Contains(
                $"\"{ProcessWireNames.NodeDiscriminator}\":\"{discriminators[index]}\"",
                json,
                StringComparison.Ordinal);
            Assert.Equal(json, JsonSerializer.Serialize<CanonicalProcessNode>(restored, options));
        }
    }

    [Fact]
    public void EveryAwaitClauseVariant_RoundTripsWithItsStableWireDiscriminator()
    {
        ProcessAwaitClause[] clauses =
        [
            new ProcessAwaitInteractionClause(
                new("review"),
                new RequestContractReference(DefinitionReference("request/review")),
                new(new("review/input"), StringContract),
                new(new("review/request")),
                Expr.Const(true),
                10,
                new(Edge("edge/review", "terminal"))),
            new ProcessAwaitTimerClause(
                new("timeout"),
                InstantExpression(),
                0,
                new(Edge("edge/timeout", "terminal")))
        ];
        string[] discriminators =
        [
            ProcessWireNames.InteractionAwaitClause,
            ProcessWireNames.TimerAwaitClause
        ];
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();

        for (var index = 0; index < clauses.Length; index++)
        {
            var json = JsonSerializer.Serialize<ProcessAwaitClause>(clauses[index], options);
            var restored = JsonSerializer.Deserialize<ProcessAwaitClause>(json, options);

            Assert.NotNull(restored);
            Assert.IsType(clauses[index].GetType(), restored);
            Assert.Contains(
                $"\"{ProcessWireNames.AwaitClauseDiscriminator}\":\"{discriminators[index]}\"",
                json,
                StringComparison.Ordinal);
            Assert.Equal(json, JsonSerializer.Serialize<ProcessAwaitClause>(restored, options));
        }
    }

    [Fact]
    public void ForkAdmissionAndCapacitySetsNormalizeIntoOneCanonicalWireShape()
    {
        var first = new ForkProcessNode(
            new("fork"),
            [
                new(new("branch/beta"), Edge("edge/fork-beta", "join"), "resource/b"),
                new(new("branch/alpha"), Edge("edge/fork-alpha", "join"), "resource/a")
            ],
            new("join"),
            new(
                maximumItems: 2,
                maximumStartsPerActivation: 1,
                maximumParallelism: 2,
                minimumParallelism: 1),
            [new("resource/b", 1), new("resource/a", 1)]);
        var second = new ForkProcessNode(
            new("fork"),
            [
                new(new("branch/alpha"), Edge("edge/fork-alpha", "join"), "resource/a"),
                new(new("branch/beta"), Edge("edge/fork-beta", "join"), "resource/b")
            ],
            new("join"),
            new(
                maximumItems: 2,
                maximumStartsPerActivation: 1,
                maximumParallelism: 2,
                minimumParallelism: 1),
            [new("resource/a", 1), new("resource/b", 1)]);
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();

        var firstJson = JsonSerializer.Serialize<CanonicalProcessNode>(first, options);
        var secondJson = JsonSerializer.Serialize<CanonicalProcessNode>(second, options);
        var restored = Assert.IsType<ForkProcessNode>(
            JsonSerializer.Deserialize<CanonicalProcessNode>(firstJson, options));

        Assert.Equal(first, second);
        Assert.Equal(firstJson, secondJson);
        Assert.Equal(first, restored);
        Assert.Contains("\"maximumStartsPerActivation\":1", firstJson, StringComparison.Ordinal);
        Assert.Contains("\"capacityDomain\":\"resource/a\"", firstJson, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultForkAdmissionIsTheExplicitEagerFiniteSetConvention()
    {
        ImmutableArray<ProcessForkBranch> branches =
        [
            new(new("branch/alpha"), Edge("edge/fork-alpha", "join")),
            new(new("branch/beta"), Edge("edge/fork-beta", "join"))
        ];

        var conventional = new ForkProcessNode(new("fork"), branches, new("join"));
        var explicitPolicy = new ForkProcessNode(
            new("fork"),
            branches,
            new("join"),
            ProcessWorkLimits.EagerFiniteSet(branches.Length),
            capacityDomains: []);

        Assert.Equal(explicitPolicy, conventional);
        Assert.Equal(
            JsonSerializer.Serialize<CanonicalProcessNode>(explicitPolicy, ExecutionDefinitionJsonSerializer.CreateOptions()),
            JsonSerializer.Serialize<CanonicalProcessNode>(conventional, ExecutionDefinitionJsonSerializer.CreateOptions()));
    }

    [Fact]
    public void SetLikeGraphMembersNormalizeButOrderedCasesRemainSemantic()
    {
        var terminal = new ReturnProcessNode(new("terminal"), Expr.Const("done"));
        var cut = new DurableCutProcessNode(new("cut"), Edge("edge/resume", "terminal"));
        var orderedNodes = new CanonicalProcessDefinition(
            StringContract,
            StringContract,
            cut.Id,
            [cut, terminal],
            ProcessRecoveryPolicy.ContinueAttempt);
        var reversedNodes = new CanonicalProcessDefinition(
            StringContract,
            StringContract,
            cut.Id,
            [terminal, cut],
            ProcessRecoveryPolicy.ContinueAttempt);
        var firstChoice = ChoiceDefinition(firstCaseFirst: true);
        var reversedChoice = ChoiceDefinition(firstCaseFirst: false);

        Assert.Equal(orderedNodes, reversedNodes);
        Assert.Equal(Fingerprint(orderedNodes), Fingerprint(reversedNodes));
        Assert.Equal(["cut", "terminal"], orderedNodes.Nodes.Select(static node => node.Id.Value));
        Assert.NotEqual(Fingerprint(firstChoice), Fingerprint(reversedChoice));
    }

    [Fact]
    public void RequestOutcomesForkBranchesAndAwaitClauses_NormalizeAsSemanticSets()
    {
        var terminalA = new ProcessContinuation(Edge("edge/a", "terminal"));
        var terminalB = new ProcessContinuation(Edge("edge/b", "terminal"));
        var requestA = new ProcessRequestOutcomeBranch(new("request/a"), new("a"), terminalA);
        var requestB = new ProcessRequestOutcomeBranch(new("request/b"), new("b"), terminalB);
        var forkA = new ProcessForkBranch(new("fork/a"), Edge("edge/fork-a", "terminal"));
        var forkB = new ProcessForkBranch(new("fork/b"), Edge("edge/fork-b", "terminal"));
        ProcessAwaitClause awaitA = new ProcessAwaitTimerClause(
            new("await/a"),
            InstantExpression(),
            10,
            terminalA);
        ProcessAwaitClause awaitB = new ProcessAwaitTimerClause(
            new("await/b"),
            InstantExpression(),
            0,
            terminalB);

        var requestOrdered = new RequestProcessNode(
            new("request"),
            new(DefinitionReference("request/review")),
            Expr.Const("review"),
            [requestA, requestB]);
        var requestReversed = new RequestProcessNode(
            requestOrdered.Id,
            requestOrdered.Contract,
            requestOrdered.Payload,
            [requestB, requestA]);
        var forkOrdered = new ForkProcessNode(new("fork"), [forkA, forkB], new("join"));
        var forkReversed = new ForkProcessNode(forkOrdered.Id, [forkB, forkA], forkOrdered.Join);
        var awaitOrdered = Await([awaitA, awaitB]);
        var awaitReversed = Await([awaitB, awaitA]);
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();

        Assert.Equal(requestOrdered, requestReversed);
        Assert.Equal(forkOrdered, forkReversed);
        Assert.Equal(awaitOrdered, awaitReversed);
        Assert.Equal(
            JsonSerializer.Serialize<CanonicalProcessNode>(requestOrdered, options),
            JsonSerializer.Serialize<CanonicalProcessNode>(requestReversed, options));
        Assert.Equal(
            JsonSerializer.Serialize<CanonicalProcessNode>(forkOrdered, options),
            JsonSerializer.Serialize<CanonicalProcessNode>(forkReversed, options));
        Assert.Equal(
            JsonSerializer.Serialize<CanonicalProcessNode>(awaitOrdered, options),
            JsonSerializer.Serialize<CanonicalProcessNode>(awaitReversed, options));
    }

    [Fact]
    public void StableIdentitiesTypesPoliciesAndReferences_AreFingerprintBearing()
    {
        var baseline = MinimalDefinition(
            new DurableCutProcessNode(new("cut"), Edge("edge/resume", "terminal")),
            new ReturnProcessNode(new("terminal"), Expr.Const("done")));
        var changedEdge = MinimalDefinition(
            new DurableCutProcessNode(new("cut"), Edge("edge/resume-2", "terminal")),
            new ReturnProcessNode(new("terminal"), Expr.Const("done")));
        var changedNode = new CanonicalProcessDefinition(
            StringContract,
            StringContract,
            new("cut-2"),
            [
                new DurableCutProcessNode(new("cut-2"), Edge("edge/resume", "terminal")),
                new ReturnProcessNode(new("terminal"), Expr.Const("done"))
            ],
            ProcessRecoveryPolicy.ContinueAttempt);
        var changedType = new CanonicalProcessDefinition(
            BooleanContract,
            StringContract,
            baseline.Entry,
            baseline.Nodes,
            baseline.RecoveryPolicy);
        var changedPolicy = new CanonicalProcessDefinition(
            baseline.Input,
            baseline.Result,
            baseline.Entry,
            baseline.Nodes,
            ProcessRecoveryPolicy.RestartAttempt);
        var firstReference = ReferenceDefinition('1');
        var secondReference = ReferenceDefinition('2');
        ExecutionDefinitionFingerprint[] fingerprints =
        [
            Fingerprint(baseline),
            Fingerprint(changedEdge),
            Fingerprint(changedNode),
            Fingerprint(changedType),
            Fingerprint(changedPolicy),
            Fingerprint(firstReference),
            Fingerprint(secondReference)
        ];

        Assert.Equal(fingerprints.Length, fingerprints.Distinct().Count());
    }

    [Fact]
    public void ForEachPartition_FailureAndCapacitySemanticsNormalizeRoundTripAndBearFingerprints()
    {
        var baseline = PartitionDefinition(
            ProcessPartitionFailurePolicy.AwaitAll,
            [
                new("target/b", maximumParallelism: 2),
                new("target/a", maximumParallelism: 1)
            ]);
        var reordered = PartitionDefinition(
            ProcessPartitionFailurePolicy.AwaitAll,
            [
                new("target/a", maximumParallelism: 1),
                new("target/b", maximumParallelism: 2)
            ]);
        var changedFailure = PartitionDefinition(
            ProcessPartitionFailurePolicy.FailFast,
            [
                new("target/a", maximumParallelism: 1),
                new("target/b", maximumParallelism: 2)
            ]);
        var changedCapacity = PartitionDefinition(
            ProcessPartitionFailurePolicy.AwaitAll,
            [
                new("target/a", maximumParallelism: 2),
                new("target/b", maximumParallelism: 2)
            ]);

        Assert.Equal(baseline, reordered);
        Assert.Equal(Fingerprint(baseline), Fingerprint(reordered));
        Assert.NotEqual(Fingerprint(baseline), Fingerprint(changedFailure));
        Assert.NotEqual(Fingerprint(baseline), Fingerprint(changedCapacity));
        var baselineNode = Assert.Single(baseline.Nodes.OfType<ForEachPartitionProcessNode>());
        Assert.Equal(ProcessPartitionFailurePolicy.AwaitAll, baselineNode.Failure);
        Assert.Equal(["target/a", "target/b"], baselineNode.CapacityDomains.Select(static domain => domain.Identity));

        var options = ExecutionDefinitionJsonSerializer.CreateOptions();
        var json = JsonSerializer.Serialize<CanonicalProcessNode>(baselineNode, options);
        var restoredNode = Assert.IsType<ForEachPartitionProcessNode>(
            JsonSerializer.Deserialize<CanonicalProcessNode>(json, options));

        Assert.Equal(baselineNode, restoredNode);
        Assert.Equal(json, JsonSerializer.Serialize<CanonicalProcessNode>(restoredNode, options));
        Assert.Equal(ProcessPartitionFailurePolicy.AwaitAll, restoredNode.Failure);
        Assert.NotNull(restoredNode.CapacityIdentity);
        Assert.Equal(
            [("target/a", 1), ("target/b", 2)],
            restoredNode.CapacityDomains.Select(static domain =>
                (domain.Identity, domain.MaximumParallelism)));

        static CanonicalProcessDefinition PartitionDefinition(
            ProcessPartitionFailurePolicy failure,
            ImmutableArray<ProcessCapacityDomainLimit> capacityDomains)
        {
            var input = new ValueContract(
                new ScalarTypeRef(ScalarTypeKind.String),
                cardinality: FieldCardinality.Many);
            ProcessOutputBinding partition = new(new("partition"), StringContract);
            ForEachPartitionProcessNode node = new(
                new("partitions"),
                Expr.BoundValue(ProcessBindingIds.Input),
                partition,
                Expr.BoundValue(partition.Binding),
                DefinitionReference("process/partition-child"),
                new(DefinitionReference("request/partition-child")),
                ChildOutcomeMapping,
                Expr.BoundValue(partition.Binding),
                new(maximumItems: 10, maximumStartsPerActivation: 2, maximumParallelism: 2),
                failure,
                Expr.Const("target/a"),
                capacityDomains,
                ProcessChildCancellationPolicy.Propagate,
                Edge("edge/partitions-completed", "return"),
                Edge("edge/partitions-failed", "fail"));
            return new(
                input,
                StringContract,
                node.Id,
                [
                    node,
                    new ReturnProcessNode(new("return"), Expr.Const("done")),
                    new FailProcessNode(new("fail"), Expr.Const("failed"))
                ],
                ProcessRecoveryPolicy.ContinueAttempt);
        }
    }

    [Fact]
    public void Facade_RejectsUnknownAndMissingNodeDiscriminatorsAtTheExactDefinitionPath()
    {
        foreach (var rewrite in new Action<JsonObject>[]
                 {
                     static node => node[ProcessWireNames.NodeDiscriminator] = "opaqueHostCallback",
                     static node => Assert.True(node.Remove(ProcessWireNames.NodeDiscriminator))
                 })
        {
            var json = RewriteDefinitionNode(CreateDocument(MinimalDefinition()), rewrite);

            var validation = ProcessDefinitionDocuments.TryDeserialize(json, out var document, out var definition);

            Assert.NotNull(document);
            Assert.Null(definition);
            var diagnostic = Assert.Single(validation.Diagnostics);
            Assert.Equal(ProcessDefinitionDocumentDiagnosticCodes.DefinitionProjectionInvalid, diagnostic.Code);
            Assert.Equal("/definition/nodes/0", diagnostic.Location);
        }
    }

    [Fact]
    public void Facade_RejectsNonProcessKindWithoutProjectingDefinition()
    {
        var wrongKind = ExecutionDefinitionDocument.Create(
            new("transition"),
            new("process/review"),
            new("revision/1"),
            MinimalDefinition(),
            Provenance());

        var validation = ProcessDefinitionDocuments.Validate(wrongKind);

        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(ProcessDefinitionDocumentDiagnosticCodes.KindMismatch, diagnostic.Code);
        Assert.Equal("/kind", diagnostic.Location);
    }

    [Fact]
    public void CanonicalProcessIr_IsClosedPortableStateWithoutRuntimeAuthority()
    {
        var nodeTypes = DerivedTypes(typeof(CanonicalProcessNode));
        var clauseTypes = DerivedTypes(typeof(ProcessAwaitClause));

        Assert.Equal(18, nodeTypes.Length);
        Assert.Equal(
            [typeof(ProcessAwaitInteractionClause), typeof(ProcessAwaitTimerClause)],
            clauseTypes);
        Assert.All(nodeTypes, static type => Assert.True(type.IsSealed));
        Assert.All(clauseTypes, static type => Assert.True(type.IsSealed));
        Assert.Empty(FindRuntimeAuthority(typeof(CanonicalProcessDefinition)));
        Assert.Empty(FindPublicSettersInCanonicalProcessIr());
        Assert.DoesNotContain(
            typeof(CanonicalProcessDefinition).Assembly.GetReferencedAssemblies(),
            static reference => string.Equals(reference.Name, "Cohesive.Adapters.DurableTask", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(Cohesive.Transitions.IR.TransitionDefinition).Assembly.GetReferencedAssemblies(),
            static reference => string.Equals(reference.Name, "Cohesive.Processes", StringComparison.Ordinal));
    }

    static CanonicalProcessDefinition RepresentativeDefinition()
    {
        var terminal = new ReturnProcessNode(new("terminal"), Expr.Const("approved"));
        var timer = new TimerProcessNode(
            new("timer"),
            InstantExpression(),
            Edge("edge/timer-complete", terminal.Id.Value));
        var durableCut = new DurableCutProcessNode(
            new("durable-cut"),
            Edge("edge/resume", timer.Id.Value));
        var choice = new ChoiceProcessNode(
            new("choice"),
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Fallback,
            [new(new("choice/defer"), Expr.Const(true), Edge("edge/defer", durableCut.Id.Value))],
            new(new("choice/immediate"), Edge("edge/immediate", terminal.Id.Value)));
        return new(
            StringContract,
            StringContract,
            choice.Id,
            [terminal, choice, timer, durableCut],
            ProcessRecoveryPolicy.ContinueAttempt);
    }

    static CanonicalProcessDefinition MinimalDefinition(params CanonicalProcessNode[] nodes)
    {
        CanonicalProcessNode[] selected = nodes.Length == 0
            ? [new ReturnProcessNode(new("terminal"), Expr.Const("done"))]
            : nodes;
        return new(
            StringContract,
            StringContract,
            selected[0].Id,
            [.. selected],
            ProcessRecoveryPolicy.ContinueAttempt);
    }

    static CanonicalProcessDefinition ChoiceDefinition(bool firstCaseFirst)
    {
        var first = new ProcessChoiceCase(
            new("case/first"),
            Expr.Const(true),
            Edge("edge/first", "first"));
        var second = new ProcessChoiceCase(
            new("case/second"),
            Expr.Const(true),
            Edge("edge/second", "second"));
        var choice = new ChoiceProcessNode(
            new("choice"),
            CaseSelection.OrderedFirstMatch,
            BranchCompleteness.Exhaustive,
            firstCaseFirst ? [first, second] : [second, first]);
        return new(
            StringContract,
            StringContract,
            choice.Id,
            [
                choice,
                new ReturnProcessNode(new("first"), Expr.Const("first")),
                new ReturnProcessNode(new("second"), Expr.Const("second"))
            ],
            ProcessRecoveryPolicy.ContinueAttempt);
    }

    static CanonicalProcessDefinition ReferenceDefinition(char fingerprintDigit)
    {
        var invocation = new InvokeTransitionProcessNode(
            new("invoke"),
            DefinitionReference("transition/review", fingerprintDigit),
            Expr.Const("case-1"),
            Expr.Const("approve"),
            new(Edge("edge/complete", "terminal")));
        return new(
            StringContract,
            StringContract,
            invocation.Id,
            [invocation, new ReturnProcessNode(new("terminal"), Expr.Const("done"))],
            ProcessRecoveryPolicy.ContinueAttempt);
    }

    static ProcessJoinPolicy JoinPolicy() => new(
        ProcessJoinMode.All,
        requiredCount: 0,
        ProcessJoinFailurePolicy.FailFast,
        ProcessJoinCancellationPolicy.AwaitRemaining,
        ProcessJoinCompletionOrder.Unobservable,
        ProcessJoinTieBreak.BranchIdentity);

    static AwaitMatchProcessNode Await(ImmutableArray<ProcessAwaitClause> clauses) => new(
        new("await"),
        ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
        clauses,
        ProcessAwaitInputDisposition.Observe,
        ProcessAwaitInputDisposition.Reject,
        ProcessAwaitInputDisposition.ReusePriorDisposition,
        ProcessAwaitMissingTargetDisposition.DeadLetter,
        TimeSpan.FromDays(7));

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static PortableValue ConcreteString(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    static Expr InstantExpression() => new LiteralExpr(
        new ScalarTypeRef(ScalarTypeKind.Instant),
        ObservationValue.FromString("2026-07-29T12:00:00.0000000+00:00"));

    static ExecutionDefinitionDocument CreateDocument(CanonicalProcessDefinition definition) =>
        ProcessDefinitionDocuments.Create(
            new("process/review"),
            new("revision/1"),
            definition,
            Provenance());

    static ExecutionDefinitionFingerprint Fingerprint(CanonicalProcessDefinition definition) =>
        CreateDocument(definition).Metadata.Fingerprint;

    static ExecutionDefinitionReference DefinitionReference(
        string definitionId,
        char fingerprintDigit = '1') =>
        new(
            new(definitionId),
            new("revision/1"),
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                new string(fingerprintDigit, 64)));

    static ExecutionProvenance Provenance() =>
        new(
            new("direct-process-ir-tests", "1"),
            new("tests/execution-kernel/canonical-process-ir"),
            DocumentOrigin.Generated);

    static string RewriteDefinitionNode(
        ExecutionDefinitionDocument document,
        Action<JsonObject> rewrite)
    {
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();
        var root = JsonNode.Parse(ExecutionDefinitionJsonSerializer.Serialize(document))?.AsObject()
            ?? throw new InvalidOperationException("Failed to parse the execution-definition test document.");
        var definition = root["definition"]?.AsObject()
            ?? throw new InvalidOperationException("The execution-definition test document has no definition object.");
        var node = definition["nodes"]?[0]?.AsObject()
            ?? throw new InvalidOperationException("The Process definition has no first node object.");
        rewrite(node);

        using var parsedDefinition = JsonDocument.Parse(definition.ToJsonString(options));
        var fingerprint = ExecutionDefinitionFingerprinter.Compute(
            document.Metadata.SchemaVersion,
            document.Kind,
            parsedDefinition.RootElement,
            document.Extensions);
        var fingerprintNode = root["metadata"]?["fingerprint"]?.AsObject()
            ?? throw new InvalidOperationException("The execution-definition test document has no fingerprint object.");
        fingerprintNode["algorithm"] = fingerprint.Algorithm;
        fingerprintNode["canonicalization"] = fingerprint.Canonicalization;
        fingerprintNode["value"] = fingerprint.Value;
        return root.ToJsonString(options);
    }

    static string FormatDiagnostics(DocumentValidationResult validation) =>
        string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    static Type[] DerivedTypes(Type type) =>
        [.. type
            .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .Select(static attribute => attribute.DerivedType)];

    static IReadOnlyList<string> FindRuntimeAuthority(params Type[] roots)
    {
        Queue<Type> pending = new(roots);
        HashSet<Type> visited = [];
        List<string> forbidden = [];
        while (pending.TryDequeue(out var candidate))
        {
            var type = Nullable.GetUnderlyingType(candidate) ?? candidate;
            if (type.IsArray)
            {
                pending.Enqueue(type.GetElementType()!);
                continue;
            }
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                    pending.Enqueue(argument);
            }
            if (!visited.Add(type))
                continue;

            if (IsRuntimeAuthority(type))
            {
                forbidden.Add(type.FullName ?? type.Name);
                continue;
            }

            if (!(type.Namespace?.StartsWith("Cohesive.", StringComparison.Ordinal) ?? false))
                continue;

            foreach (var attribute in type.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false))
                pending.Enqueue(attribute.DerivedType);
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                pending.Enqueue(property.PropertyType);
        }

        forbidden.Sort(StringComparer.Ordinal);
        return forbidden;
    }

    static bool IsRuntimeAuthority(Type type)
    {
        var typeNamespace = type.Namespace ?? string.Empty;
        return typeof(Delegate).IsAssignableFrom(type)
            || type == typeof(Type)
            || typeof(IServiceProvider).IsAssignableFrom(type)
            || typeof(Stream).IsAssignableFrom(type)
            || typeof(Task).IsAssignableFrom(type)
            || type == typeof(ValueTask)
            || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)
            || type == typeof(CancellationToken)
            || typeNamespace.StartsWith("Cohesive.Adapters", StringComparison.Ordinal)
            || typeNamespace.StartsWith("Cohesive.Storage", StringComparison.Ordinal)
            || typeNamespace.StartsWith("Cohesive.Processes.Authoring", StringComparison.Ordinal)
            || typeNamespace.StartsWith("Cohesive.Processes.Model", StringComparison.Ordinal)
            || typeNamespace.StartsWith("Cohesive.Processes.Runtime", StringComparison.Ordinal)
            || typeNamespace.StartsWith("Cohesive.Transitions.Model", StringComparison.Ordinal)
            || typeNamespace.StartsWith("Cohesive.Relations.Execution", StringComparison.Ordinal);
    }

    static IReadOnlyList<string> FindPublicSettersInCanonicalProcessIr() =>
        [.. typeof(CanonicalProcessDefinition).Assembly
            .GetTypes()
            .Where(static type => string.Equals(
                type.Namespace,
                "Cohesive.Processes.IR",
                StringComparison.Ordinal)
                && (type.IsPublic || type.IsNestedPublic))
            .SelectMany(static type => type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(static property => property.SetMethod?.IsPublic == true)
                .Select(property => $"{type.FullName}.{property.Name}"))
            .Order(StringComparer.Ordinal)];
}
