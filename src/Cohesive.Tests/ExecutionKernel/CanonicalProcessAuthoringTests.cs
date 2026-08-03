using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;
using CanonicalProcessNode = Cohesive.Processes.IR.ProcessNode;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class CanonicalProcessAuthoringTests
{
    const string SourceReference = "tests/ari-170/canonical-process-authoring";

    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract BooleanContract = new(new ScalarTypeRef(ScalarTypeKind.Bool));
    static readonly ValueContract DecisionInputContract = new(new ObjectTypeRef(
    [
        new(nameof(DecisionInput.Approved), BooleanContract.Type!),
        new(nameof(DecisionInput.Outcome), StringContract.Type!)
    ]));
    static readonly ProcessChildOutcomeMapping ChildOutcomeMapping = new(
        new("completed"),
        new("failed"),
        new("cancelled"),
        new("terminated"));

    [Fact]
    public void TypedCSharpAuthoring_LowersToEquivalentDirectCanonicalIrDeterministically()
    {
        var first = CreateDecisionProcess();
        var second = CreateDecisionProcess();
        var directDefinition = DecisionDefinition();
        var directDocument = ProcessDefinitionDocuments.Create(
            Identities.Definition,
            Identities.Revision,
            directDefinition,
            Provenance());

        Assert.True(first.IsValid, Format(first.Validation));
        Assert.Equal(DecisionInputContract, first.Definition.Input);
        Assert.Equal(StringContract, first.Definition.Result);
        Assert.Equal(directDefinition, first.Definition);
        Assert.Equal(directDefinition.GetHashCode(), first.Definition.GetHashCode());
        Assert.Equal(first.Definition, second.Definition);
        Assert.Equal(first.Document.Metadata.Fingerprint, second.Document.Metadata.Fingerprint);
        Assert.Equal(first.Document.Metadata.Fingerprint, directDocument.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(directDocument),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(first.Document));
        Assert.Equal(first.Document.Metadata.SourceMap, second.Document.Metadata.SourceMap);
        Assert.DoesNotContain(
            first.Document.Metadata.SourceMap.Entries,
            static entry => entry.Reference.Contains("#unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void NonSemanticSourceAttribution_DoesNotChangeCompilationOrReferenceMeaning()
    {
        var first = CreateDecisionProcess("tests/ari-205/producer-a");
        var second = CreateDecisionProcess("tests/ari-205/producer-b");

        Assert.NotEqual(first.Document.Metadata.Provenance, second.Document.Metadata.Provenance);
        Assert.NotEqual(first.Document.Metadata.SourceMap, second.Document.Metadata.SourceMap);
        Assert.Equal(first.Document.Metadata.Fingerprint, second.Document.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(first.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(second.Document));

        var firstCompilation = first.Compile(new ProcessDefinitionValidationContext());
        var secondCompilation = second.Compile(new ProcessDefinitionValidationContext());
        Assert.True(firstCompilation.IsSuccessful, Format(firstCompilation.Validation));
        Assert.True(secondCompilation.IsSuccessful, Format(secondCompilation.Validation));
        Assert.Equivalent(firstCompilation.Validation, secondCompilation.Validation, strict: true);
        var firstPlan = Assert.IsType<CompiledProcessPlan>(firstCompilation.Plan);
        var secondPlan = Assert.IsType<CompiledProcessPlan>(secondCompilation.Plan);
        Assert.Equal(firstPlan.DefinitionReference, secondPlan.DefinitionReference);
        Assert.Equal(firstPlan.Definition, secondPlan.Definition);
        Assert.Equivalent(firstPlan.Options, secondPlan.Options, strict: true);
        Assert.Equivalent(firstPlan.EffectSummary, secondPlan.EffectSummary, strict: true);

        var firstDecision = ProcessReferenceInterpreter.Activate(
            firstPlan,
            ProcessReferenceInterpreter.Create(
                firstPlan,
                ContinuationIdentity(),
                DecisionInputValue(approved: true, outcome: "accepted")),
            Activation(
                "activation/ari-205/source-attribution",
                ProcessActivationCause.Start,
                first.Document.Metadata.Provenance),
            RejectingHost.Instance);
        var secondDecision = ProcessReferenceInterpreter.Activate(
            secondPlan,
            ProcessReferenceInterpreter.Create(
                secondPlan,
                ContinuationIdentity(),
                DecisionInputValue(approved: true, outcome: "accepted")),
            Activation(
                "activation/ari-205/source-attribution",
                ProcessActivationCause.Start,
                second.Document.Metadata.Provenance),
            RejectingHost.Instance);

        Assert.Equal(firstDecision.Disposition, secondDecision.Disposition);
        Assert.Equivalent(firstDecision.State, secondDecision.State, strict: true);
        Assert.NotEqual(
            firstDecision.Evidence.Trace.SelectMany(static item => item.SourceReferences),
            secondDecision.Evidence.Trace.SelectMany(static item => item.SourceReferences));
        Assert.Equivalent(
            firstDecision.Evidence with
            {
                Trace = [.. firstDecision.Evidence.Trace.Select(static item => item with { SourceReferences = [] })]
            },
            secondDecision.Evidence with
            {
                Trace = [.. secondDecision.Evidence.Trace.Select(static item => item with { SourceReferences = [] })]
            },
            strict: true);
        Assert.Empty(firstDecision.Diagnostics);
        Assert.Empty(secondDecision.Diagnostics);
    }

    [Fact]
    public void AuthoredDocument_StrictRoundTripCompilesAndReferenceInterpretsWithoutProducerAssemblyState()
    {
        var authored = CreateDecisionProcess();
        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(authored.Document);

        var validation = ProcessDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, Format(validation));
        Assert.NotNull(restoredDocument);
        Assert.NotNull(restoredDefinition);
        Assert.Equal(authored.Document, restoredDocument);
        Assert.Equal(authored.Definition, restoredDefinition);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument));
        Assert.Equal(authored.Document.Metadata.Fingerprint, restoredDocument.Metadata.Fingerprint);
        Assert.Equal(authored.Document.Metadata.SourceMap, restoredDocument.Metadata.SourceMap);

        var compilation = ProcessStaticCompiler.Compile(
            restoredDocument,
            new ProcessDefinitionValidationContext());

        Assert.True(compilation.IsSuccessful, Format(compilation.Validation));
        var plan = Assert.IsType<CompiledProcessPlan>(compilation.Plan);
        Assert.Equal(authored.Reference, plan.DefinitionReference);
        Assert.Equal(authored.Definition, plan.Definition);

        var initial = ProcessReferenceInterpreter.Create(
            plan,
            ContinuationIdentity(),
            DecisionInputValue(approved: true, outcome: "accepted"));
        var cut = ProcessReferenceInterpreter.Activate(
            plan,
            initial,
            Activation("activation/decision/start", ProcessActivationCause.Start),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.DurableCut, cut.Disposition);
        Assert.Equal(DecisionIds.Cut, cut.Evidence.SafePointNode);

        var completed = ProcessReferenceInterpreter.Activate(
            plan,
            cut.State,
            Activation("activation/decision/continue", ProcessActivationCause.Continue),
            RejectingHost.Instance);

        Assert.Equal(ProcessActivationDisposition.Completed, completed.Disposition);
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, completed.State.Terminal.Kind);
        Assert.Equal(
            PortableValue.Concrete(StringContract, ObservationValue.FromString("accepted")),
            completed.State.Terminal.Detail?.Value);
    }

    [Fact]
    public void TypedAuthoring_SourceMapsAuthoredExpressionsAndValidationDiagnostics()
    {
        var authored = ProcessAuthoring.Create<string, string>(
            Metadata(),
            process =>
            {
                var invalidResult = process.CanonicalValue<string>(Expr.Const(true), StringContract);
                process.Return(Identities.Return, invalidResult);
            });
        var paths = authored.Document.Metadata.SourceMap.Entries
            .Select(static entry => entry.SemanticPath!.Value.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var mismatch = Assert.Single(
            authored.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.ResultTypeMismatch);

        Assert.False(authored.IsValid);
        Assert.Contains("/nodes/0", paths);
        Assert.Contains("/nodes/0/result", paths);
        Assert.Equal("/definition/nodes/0/result", mismatch.Location);
        Assert.NotNull(mismatch.Evidence);
        Assert.NotEmpty(mismatch.Evidence.SourceReferences);
        Assert.All(
            mismatch.Evidence.SourceReferences,
            reference => Assert.StartsWith(SourceReference, reference, StringComparison.Ordinal));
        Assert.Contains(
            mismatch.Evidence.SourceReferences,
            static reference => reference.Contains(
                nameof(TypedAuthoring_SourceMapsAuthoredExpressionsAndValidationDiagnostics),
                StringComparison.Ordinal));

        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(authored.Document);
        var roundTripValidation = ProcessDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);

        Assert.False(roundTripValidation.IsValid);
        Assert.NotNull(restoredDocument);
        Assert.Null(restoredDefinition);
        Assert.NotEmpty(restoredDocument.Metadata.Diagnostics);
        Assert.Contains(
            restoredDocument.Metadata.Diagnostics,
            static diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        var restoredMismatch = Assert.Single(
            roundTripValidation.Diagnostics,
            static diagnostic => diagnostic.Code == ExprAnalysisDiagnosticCodes.ResultTypeMismatch);
        Assert.NotNull(restoredMismatch.Evidence);
        Assert.Contains(
            restoredMismatch.Evidence.SourceReferences,
            static reference => reference.Contains(
                nameof(TypedAuthoring_SourceMapsAuthoredExpressionsAndValidationDiagnostics),
                StringComparison.Ordinal));
    }

    [Fact]
    public void TypedAuthoring_LinkDiagnosticsRetainTheAuthoredSourceReference()
    {
        var relation = new ExecutionNodeId("relation");
        var authored = ProcessAuthoring.Create<string, string>(
            new(
                new("process/unresolved-link"),
                Identities.Revision,
                relation,
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance()),
            process =>
            {
                var next = process.Edge(relation, "complete", Identities.Return);
                process.EvaluateRelation(
                    relation,
                    DefinitionReference("relation/unresolved"),
                    process.Input.Value,
                    process.Continuation(next));
                process.Return(Identities.Return, process.Input.Value);
            });

        Assert.True(authored.IsValid, Format(authored.Validation));

        var compilation = authored.Compile(new ProcessDefinitionValidationContext());
        var unresolved = Assert.Single(
            compilation.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == ProcessDefinitionDiagnosticCodes.DefinitionReferenceUnresolved);

        Assert.False(compilation.IsSuccessful);
        Assert.EndsWith("/relation", unresolved.Location, StringComparison.Ordinal);
        Assert.NotNull(unresolved.Evidence);
        Assert.Contains(
            unresolved.Evidence.SourceReferences,
            static reference => reference.Contains(
                nameof(TypedAuthoring_LinkDiagnosticsRetainTheAuthoredSourceReference),
                StringComparison.Ordinal));
    }

    [Fact]
    public void TypedHandle_RetainsOnlyCanonicalDocumentAndValidationAuthority()
    {
        var authored = CreateEchoProcess();
        var handleType = authored.GetType();
        var fields = handleType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var properties = handleType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.DoesNotContain(fields, static field => typeof(Delegate).IsAssignableFrom(field.FieldType));
        Assert.DoesNotContain(properties, static property => typeof(Delegate).IsAssignableFrom(property.PropertyType));
        Assert.DoesNotContain(fields, static field => IsLegacyAuthority(field.FieldType));
        Assert.DoesNotContain(properties, static property => IsLegacyAuthority(property.PropertyType));
        Assert.Contains(properties, static property => property.Name == nameof(authored.Document)
            && property.PropertyType == typeof(ExecutionDefinitionDocument));
        Assert.Contains(properties, static property => property.Name == nameof(authored.Definition)
            && property.PropertyType == typeof(CanonicalProcessDefinition));
    }

    [Fact]
    public void TypedFieldSelector_RejectsArbitraryMethodCalls()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ProcessAuthoring.Create<SelectorInput, string>(
                SelectorMetadata("method"),
                process =>
                {
                    var invalid = process.Input.Field(static input => input.Value.ToUpperInvariant());
                    process.Return(Identities.Return, invalid);
                }));

        Assert.Contains("member path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedFieldSelector_RejectsCapturedComputation()
    {
        var suffix = "-captured";

        var exception = Assert.Throws<ArgumentException>(() =>
            ProcessAuthoring.Create<SelectorInput, string>(
                SelectorMetadata("capture"),
                process =>
                {
                    var invalid = process.Input.Field(input => input.Value + suffix);
                    process.Return(Identities.Return, invalid);
                }));

        Assert.Contains("member path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedAuthoring_RejectsEveryAsyncVoidConfigurationDelegateBeforeInvocation()
    {
        var synchronousInvoked = false;
        Action<ProcessBuilder<string, string>> asyncConfiguration = async process =>
        {
            await Task.Yield();
            process.Return(Identities.Return, process.Input.Value);
        };
        Action<ProcessBuilder<string, string>> synchronousConfiguration = process =>
        {
            synchronousInvoked = true;
            process.Return(Identities.Return, process.Input.Value);
        };

        var exception = Assert.Throws<ArgumentException>(() =>
            ProcessAuthoring.Create(Metadata(), asyncConfiguration + synchronousConfiguration));

        Assert.False(synchronousInvoked);
        Assert.Contains("cannot be async", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedFieldSelector_PreservesNestedOptionalNullableAndSerializedOccurrenceContract()
    {
        ProcessValue<string?>? selected = null;
        var authored = ProcessAuthoring.Create<NestedSelectorInput, string>(
            new(
                new("process/selector/contract"),
                Identities.Revision,
                new("match"),
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance()),
            process =>
            {
                selected = process.Input
                    .Field(static input => input.Nested)
                    .Field(static nested => nested!.DisplayName);
                var complete = process.Constant(false);
                var result = process.Constant("done");
                var matchNext = process.Edge(new("match"), "matched", new("repeat"));
                var matchFallbackNext = process.Edge(new("match"), "fallback", new("repeat"));
                var matchCase = process.MatchCase(
                    new("match/known"),
                    selected,
                    "known",
                    matchNext);
                process.Match(
                    new("match"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    selected,
                    [matchCase],
                    process.Fallback(new("match/fallback"), matchFallbackNext));
                var next = process.Edge(new("repeat"), "complete", Identities.Return);
                process.RepeatAcrossActivation(
                    new("repeat"),
                    complete,
                    selected,
                    new ProcessRecurrencePolicy(
                        maximumOccurrences: 3,
                        maximumUnchangedProgressOccurrences: 1),
                    next,
                    next,
                    next,
                    next);
                process.Return(Identities.Return, result);
            });

        Assert.NotNull(selected);
        Assert.Equal(new ScalarTypeRef(ScalarTypeKind.String), selected.Contract.Type);
        Assert.Equal(FieldPresence.Optional, selected.Contract.Presence);
        Assert.Equal(FieldNullability.Nullable, selected.Contract.Nullability);
        var field = Assert.IsType<FieldExpr>(selected.Expression);
        Assert.Equal(ProcessBindingIds.Input, field.Binding);
        Assert.Equal("nested_value.display_name", field.Path.ToString());
        var match = Assert.Single(authored.Definition.Nodes.OfType<MatchProcessNode>());
        Assert.Equal(selected.Contract, Assert.Single(match.Cases).Pattern.Contract);
        var recurrence = Assert.Single(authored.Definition.Nodes.OfType<RepeatAcrossActivationProcessNode>());
        Assert.Equal(selected.Contract, recurrence.ProgressContract);
    }

    [Fact]
    public void ExplicitOccurrenceContracts_PreserveTopLevelAndOutputNullableReferences()
    {
        var nullableString = new ValueContract(
            new ScalarTypeRef(ScalarTypeKind.String),
            nullability: FieldNullability.Nullable);
        var authored = ProcessAuthoring.Create<string?, string?>(
            new(
                new("process/nullable"),
                Identities.Revision,
                new("evaluate"),
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance()),
            nullableString,
            nullableString,
            process =>
            {
                var output = process.Output<string?>(new("relation.output"), nullableString);
                var next = process.Edge(new("evaluate"), "complete", Identities.Return);
                process.EvaluateRelation(
                    new("evaluate"),
                    DefinitionReference("relation/nullable"),
                    process.Input.Value,
                    process.Continuation(next, output));
                process.Return(Identities.Return, process.Input.Value);
            });

        Assert.Equal(nullableString, authored.Definition.Input);
        Assert.Equal(nullableString, authored.Definition.Result);
        var relation = Assert.Single(authored.Definition.Nodes.OfType<EvaluateRelationProcessNode>());
        Assert.Equal(nullableString, relation.Continuation.Output?.Contract);
    }

    [Fact]
    public void DerivedIdentities_AreDeterministicAndRejectAmbiguousRolePaths()
    {
        ExecutionNodeId owner = new("owner/path");

        Assert.Equal("owner/path/next/edge", ProcessAuthoringIdentities.EdgeFor(owner, "next").Value);
        Assert.Equal("owner/path/result/binding", ProcessAuthoringIdentities.BindingFor(owner, "result").Value);
        Assert.Equal(
            "owner/path/inbound/request-obligation",
            ProcessAuthoringIdentities.RequestObligationFor(owner, "inbound").Value);
        Assert.Throws<ArgumentException>(() => ProcessAuthoringIdentities.EdgeFor(new("owner"), "path/next"));
        Assert.Throws<ArgumentException>(() => ProcessAuthoringIdentities.BindingFor(new("owner"), "path/result"));
        Assert.Throws<ArgumentException>(() => ProcessAuthoringIdentities.RequestObligationFor(new("owner"), "path/inbound"));

        var sameRoleIdentities = new[]
        {
            ProcessAuthoringIdentities.EdgeFor(owner, "result").Value,
            ProcessAuthoringIdentities.BindingFor(owner, "result").Value,
            ProcessAuthoringIdentities.RequestObligationFor(owner, "result").Value
        };
        Assert.Equal(sameRoleIdentities.Length, sameRoleIdentities.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(owner.Value, sameRoleIdentities);

        var authored = CreateDecisionProcess();
        var explicitNodes = authored.Definition.Nodes
            .Select(static node => node.Id.Value)
            .ToHashSet(StringComparer.Ordinal);
        var choice = Assert.Single(authored.Definition.Nodes.OfType<ChoiceProcessNode>());
        var cut = Assert.Single(authored.Definition.Nodes.OfType<DurableCutProcessNode>());
        string[] conventionalEdges =
        [
            Assert.Single(choice.Cases).Next.Id.Value,
            Assert.IsType<ProcessFallback>(choice.Fallback).Next.Id.Value,
            cut.Resume.Id.Value
        ];
        Assert.Equal(conventionalEdges.Length, conventionalEdges.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(conventionalEdges, explicitNodes.Contains);
    }

    [Fact]
    public void TypedBuilder_CoversTheClosedEighteenNodeUnionAndNestedConstructs()
    {
        var authored = CreateClosedUnionProcess();
        Assert.True(authored.IsValid, Format(authored.Validation));
        var expectedTypes = typeof(CanonicalProcessNode)
            .GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false)
            .Select(static attribute => attribute.DerivedType)
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        var actualTypes = authored.Definition.Nodes
            .Select(static node => node.GetType())
            .Distinct()
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(18, expectedTypes.Length);
        Assert.Equal(expectedTypes, actualTypes);
        Assert.Equal(18, authored.Definition.Nodes.Length);

        var request = Assert.Single(authored.Definition.Nodes.OfType<RequestProcessNode>());
        var choice = Assert.Single(authored.Definition.Nodes.OfType<ChoiceProcessNode>());
        var match = Assert.Single(authored.Definition.Nodes.OfType<MatchProcessNode>());
        var fork = Assert.Single(authored.Definition.Nodes.OfType<ForkProcessNode>());
        var awaitMatch = Assert.Single(authored.Definition.Nodes.OfType<AwaitMatchProcessNode>());
        var interaction = Assert.Single(awaitMatch.Clauses.OfType<ProcessAwaitInteractionClause>());

        Assert.Single(request.Outcomes);
        Assert.NotNull(request.Outcomes[0].Continuation.Output);
        Assert.Single(choice.Cases);
        Assert.NotNull(choice.Fallback);
        Assert.Single(match.Cases);
        Assert.NotNull(match.Fallback);
        Assert.Equal(2, fork.Branches.Length);
        Assert.Equal(2, awaitMatch.Clauses.Length);
        Assert.NotNull(interaction.RequestObligation);
        Assert.NotNull(interaction.Guard);
    }

    static Process<string, string> CreateEchoProcess() =>
        ProcessAuthoring.Create<string, string>(
            Metadata(),
            process => process.Return(Identities.Return, process.Input.Value));

    static Process<DecisionInput, string> CreateDecisionProcess(string sourceReference = SourceReference) =>
        ProcessAuthoring.Create<DecisionInput, string>(
            new(
                DecisionIds.Definition,
                Identities.Revision,
                DecisionIds.Choice,
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance(sourceReference),
                displayName: "Review Decision"),
            process =>
            {
                var approved = process.Input.Field(static input => input.Approved);
                var outcome = process.Input.Field(static input => input.Outcome);
                var approvedCase = process.ChoiceCase(
                    DecisionIds.ApprovedCase,
                    approved,
                    process.Edge(DecisionIds.Choice, "approved", DecisionIds.Cut));
                var fallback = process.Fallback(
                    DecisionIds.Fallback,
                    process.Edge(DecisionIds.Choice, "fallback", DecisionIds.Fail));

                process.Choice(
                    DecisionIds.Choice,
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [approvedCase],
                    fallback);
                process.DurableCut(
                    DecisionIds.Cut,
                    process.Edge(DecisionIds.Cut, "resume", DecisionIds.Return));
                process.Return(DecisionIds.Return, outcome);
                process.Fail(DecisionIds.Fail, process.Constant("rejected"));
            });

    static CanonicalProcessDefinition DecisionDefinition() => new(
        DecisionInputContract,
        StringContract,
        DecisionIds.Choice,
        [
            new ChoiceProcessNode(
                DecisionIds.Choice,
                CaseSelection.OrderedFirstMatch,
                BranchCompleteness.Fallback,
                [
                    new ProcessChoiceCase(
                        DecisionIds.ApprovedCase,
                        Expr.Field(ProcessBindingIds.Input, nameof(DecisionInput.Approved)),
                        new(
                            ProcessAuthoringIdentities.EdgeFor(DecisionIds.Choice, "approved"),
                            DecisionIds.Cut))
                ],
                new ProcessFallback(
                    DecisionIds.Fallback,
                    new(
                        ProcessAuthoringIdentities.EdgeFor(DecisionIds.Choice, "fallback"),
                        DecisionIds.Fail))),
            new DurableCutProcessNode(
                DecisionIds.Cut,
                new(
                    ProcessAuthoringIdentities.EdgeFor(DecisionIds.Cut, "resume"),
                    DecisionIds.Return)),
            new ReturnProcessNode(
                DecisionIds.Return,
                Expr.Field(ProcessBindingIds.Input, nameof(DecisionInput.Outcome))),
            new FailProcessNode(DecisionIds.Fail, Expr.Const("rejected"))
        ],
        ProcessRecoveryPolicy.ContinueAttempt);

    static Process<string, string> CreateClosedUnionProcess() =>
        ProcessAuthoring.Create<string, string>(
            new(
                new("process/closed-union"),
                Identities.Revision,
                new("invoke-transition"),
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance()),
            process =>
            {
                var text = process.Constant("value");
                var target = process.Constant("target");
                var condition = process.Constant(true);
                var instantContract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Instant));
                var instant = process.CanonicalValue<DateTimeOffset>(
                    new LiteralExpr(
                        new ScalarTypeRef(ScalarTypeKind.Instant),
                        ObservationValue.FromString("2026-07-29T12:00:00.0000000+00:00")),
                    instantContract);
                var partitions = process.Constant<string[]>(["partition-a"]);
                var transitionOutput = process.Output<string>(new ExecutionNodeId("invoke-transition"), "result");
                var relationOutput = process.Output<string>(new ExecutionNodeId("evaluate-relation"), "result");
                var requestOutput = process.Output<string>(new ExecutionNodeId("request"), "result");
                var awaitInput = process.Output<string>(new ExecutionNodeId("await-match"), "request-input");
                var partition = process.Output<string>(new ExecutionNodeId("for-each-partition"), "partition");
                var requestObligation = process.RequestObligation(new ExecutionNodeId("await-match"), "request");

                var invokeNext = process.Edge(new("invoke-transition"), "complete", new("evaluate-relation"));
                var relationNext = process.Edge(new("evaluate-relation"), "complete", new("request"));
                var requestNext = process.Edge(new("request"), "completed", new("emit-event"));
                var emitNext = process.Edge(new("emit-event"), "complete", new("send-signal"));
                var signalNext = process.Edge(new("send-signal"), "complete", new("choice"));
                var choiceCaseNext = process.Edge(new("choice"), "case", new("match"));
                var choiceFallbackNext = process.Edge(new("choice"), "fallback", new("match"));
                var matchCaseNext = process.Edge(new("match"), "case", new("fork"));
                var matchFallbackNext = process.Edge(new("match"), "fallback", new("fork"));
                var timerBranchNext = process.Edge(new("fork"), "timer", new("timer"));
                var cutBranchNext = process.Edge(new("fork"), "cut", new("durable-cut"));
                var timerNext = process.Edge(new("timer"), "complete", new("join"));
                var cutNext = process.Edge(new("durable-cut"), "resume", new("join"));
                var joinNext = process.Edge(new("join"), "complete", new("await-match"));
                var awaitRequestNext = process.Edge(new("await-match"), "request", new("reply"));
                var awaitTimerNext = process.Edge(new("await-match"), "timer", new("invoke-process"));
                var replyNext = process.Edge(new("reply"), "complete", new("invoke-process"));
                var invokeProcessNext = process.Edge(new("invoke-process"), "completed", new("for-each-partition"));
                var partitionCompleted = process.Edge(new("for-each-partition"), "completed", new("repeat-across-activation"));
                var partitionFailed = process.Edge(new("for-each-partition"), "failed", new("repeat-across-activation"));
                var recurrenceRepeat = process.Edge(
                    new("repeat-across-activation"),
                    "repeat",
                    new("repeat-across-activation"));
                var recurrenceCompleted = process.Edge(new("repeat-across-activation"), "completed", new("return"));
                var recurrenceExhausted = process.Edge(new("repeat-across-activation"), "exhausted", new("fail"));
                var recurrenceStalled = process.Edge(new("repeat-across-activation"), "stalled", new("fail"));

                var requestOutcome = process.RequestOutcome(
                    new("request/completed"),
                    new("completed"),
                    process.Continuation(requestNext, requestOutput));
                var childOutcome = process.RequestOutcome(
                    new("invoke-process/completed"),
                    new("completed"),
                    process.Continuation(invokeProcessNext));
                var choiceCase = process.ChoiceCase(new("choice/case"), condition, choiceCaseNext);
                var choiceFallback = process.Fallback(new("choice/fallback"), choiceFallbackNext);
                var matchCase = process.MatchCase(new("match/case"), requestOutput.Value, "value", matchCaseNext);
                var matchFallback = process.Fallback(new("match/fallback"), matchFallbackNext);
                var timerBranch = process.ForkBranch(new("fork/timer"), timerBranchNext);
                var cutBranch = process.ForkBranch(new("fork/cut"), cutBranchNext);
                var awaitInteraction = process.AwaitInteractionClause(
                    new("await/request"),
                    new RequestContractReference(DefinitionReference("request/await")),
                    awaitInput,
                    requestObligation,
                    condition,
                    priority: 10,
                    process.Continuation(awaitRequestNext));
                var awaitTimer = process.AwaitTimerClause(
                    new("await/timer"),
                    instant,
                    priority: 0,
                    process.Continuation(awaitTimerNext));

                process.InvokeTransition(
                    new("invoke-transition"),
                    DefinitionReference("transition/review"),
                    target,
                    text,
                    process.Continuation(invokeNext, transitionOutput));
                process.EvaluateRelation(
                    new("evaluate-relation"),
                    DefinitionReference("relation/review"),
                    transitionOutput.Value,
                    process.Continuation(relationNext, relationOutput));
                process.Request(
                    new("request"),
                    new RequestContractReference(DefinitionReference("request/review")),
                    relationOutput.Value,
                    [requestOutcome]);
                process.EmitEvent(
                    new("emit-event"),
                    new DomainEventContractReference(DefinitionReference("event/reviewed")),
                    requestOutput.Value,
                    emitNext);
                process.SendSignal(
                    new("send-signal"),
                    new SignalContractReference(DefinitionReference("signal/review")),
                    target,
                    requestOutput.Value,
                    signalNext);
                process.Choice(
                    new("choice"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    [choiceCase],
                    choiceFallback);
                process.Match(
                    new("match"),
                    CaseSelection.OrderedFirstMatch,
                    BranchCompleteness.Fallback,
                    requestOutput.Value,
                    [matchCase],
                    matchFallback);
                process.Fork(new("fork"), [timerBranch, cutBranch], new("join"));
                process.Timer(new("timer"), instant, timerNext);
                process.DurableCut(new("durable-cut"), cutNext);
                process.Join(new("join"), new("fork"), JoinPolicy(), joinNext);
                process.AwaitMatch(
                    new("await-match"),
                    ProcessAwaitArbitration.ExclusivePriorityThenClauseId,
                    [awaitInteraction, awaitTimer],
                    ProcessAwaitInputDisposition.Observe,
                    ProcessAwaitInputDisposition.Reject,
                    ProcessAwaitInputDisposition.ReusePriorDisposition,
                    ProcessAwaitMissingTargetDisposition.DeadLetter,
                    TimeSpan.FromDays(7));
                process.Reply(
                    new("reply"),
                    new ReplyContractReference(DefinitionReference("reply/review")),
                    requestObligation,
                    awaitInput.Value,
                    replyNext);
                process.InvokeProcess(
                    new("invoke-process"),
                    DefinitionReference("process/child"),
                    new RequestContractReference(DefinitionReference("request/child")),
                    ChildOutcomeMapping,
                    text,
                    ProcessChildPurpose.Compensation,
                    ProcessChildCancellationPolicy.Propagate,
                    [childOutcome]);
                process.ForEachPartition(
                    new("for-each-partition"),
                    partitions,
                    partition,
                    partition.Value,
                    DefinitionReference("process/partition-child"),
                    new RequestContractReference(DefinitionReference("request/partition-child")),
                    ChildOutcomeMapping,
                    partition.Value,
                    new ProcessWorkLimits(
                        maximumItems: 10,
                        maximumStartsPerActivation: 2,
                        maximumParallelism: 2),
                    ProcessPartitionFailurePolicy.FailFast,
                    capacityIdentity: null,
                    capacityDomains: [],
                    ProcessChildCancellationPolicy.Propagate,
                    partitionCompleted,
                    partitionFailed);
                process.RepeatAcrossActivation(
                    new("repeat-across-activation"),
                    condition,
                    text,
                    new ProcessRecurrencePolicy(
                        maximumOccurrences: 10,
                        maximumUnchangedProgressOccurrences: 2),
                    recurrenceRepeat,
                    recurrenceCompleted,
                    recurrenceExhausted,
                    recurrenceStalled);
                process.Return(new("return"), text);
                process.Fail(new("fail"), text);
            });

    static ProcessJoinPolicy JoinPolicy() => new(
        ProcessJoinMode.All,
        requiredCount: 0,
        ProcessJoinFailurePolicy.FailFast,
        ProcessJoinCancellationPolicy.AwaitRemaining,
        ProcessJoinCompletionOrder.Unobservable,
        ProcessJoinTieBreak.BranchIdentity);

    static ProcessAuthoringMetadata Metadata() => new(
        Identities.Definition,
        Identities.Revision,
        Identities.Return,
        ProcessRecoveryPolicy.ContinueAttempt,
        Provenance(),
        displayName: "Echo Process");

    static ProcessAuthoringMetadata SelectorMetadata(string suffix) => new(
        new($"process/selector/{suffix}"),
        Identities.Revision,
        Identities.Return,
        ProcessRecoveryPolicy.ContinueAttempt,
        Provenance());

    static ExecutionDefinitionReference DefinitionReference(string definitionId) => new(
        new(definitionId),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('1', 64)));

    static ExecutionProvenance Provenance(string sourceReference = SourceReference) => new(
        new(ProcessAuthoring.Producer, "1"),
        new(sourceReference),
        DocumentOrigin.User);

    static ProcessContinuationIdentity ContinuationIdentity() => new(
        new("process-instance/canonical-authoring"),
        new("process-attempt/1"));

    static ProcessActivation Activation(
        string id,
        ProcessActivationCause cause,
        ExecutionProvenance? provenance = null) => new(
        new(id),
        cause,
        new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
        new(
            new("authority/tests", "tenant/cohesive"),
            new("correlation/canonical-authoring"),
            new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            provenance ?? Provenance()));

    static PortableValue DecisionInputValue(bool approved, string outcome) => PortableValue.Concrete(
        DecisionInputContract,
        ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            [nameof(CanonicalProcessAuthoringTests.DecisionInput.Approved)] = ObservationValue.FromBool(approved),
            [nameof(CanonicalProcessAuthoringTests.DecisionInput.Outcome)] = ObservationValue.FromString(outcome)
        }));

    static bool IsLegacyAuthority(Type type)
    {
        var typeNamespace = type.Namespace ?? string.Empty;
        return typeNamespace.StartsWith("Cohesive.Processes.Model", StringComparison.Ordinal)
            || typeNamespace.StartsWith("Cohesive.Processes.Runtime", StringComparison.Ordinal)
            || type == typeof(ProcessBuilder<,>)
            || type.Name.Contains("ProcessExecutionContext", StringComparison.Ordinal)
            || type.Name.Contains("ProcessCheckpoint", StringComparison.Ordinal);
    }

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Severity}:{diagnostic.Code}:{diagnostic.Location}:{diagnostic.Message}"));

    sealed record SelectorInput(string Value);

    sealed record DecisionInput(bool Approved, string Outcome);

    sealed record NestedSelectorInput(
        [property: JsonPropertyName("nested_value")] NestedSelectorValue? Nested);

    sealed record NestedSelectorValue(
        [property: JsonPropertyName("display_name")] string? DisplayName);

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

    static class Identities
    {
        public static readonly ExecutionDefinitionId Definition = new("process/echo");
        public static readonly ExecutionRevisionId Revision = new("revision/1");
        public static readonly ExecutionNodeId Return = new("return");
    }

    static class DecisionIds
    {
        public static readonly ExecutionDefinitionId Definition = new("process/review-decision");
        public static readonly ExecutionNodeId Choice = new("choice");
        public static readonly ExecutionNodeId ApprovedCase = new("choice/approved");
        public static readonly ExecutionNodeId Fallback = new("choice/fallback");
        public static readonly ExecutionNodeId Cut = new("durable-cut");
        public static readonly ExecutionNodeId Return = new("return");
        public static readonly ExecutionNodeId Fail = new("fail");
    }
}
