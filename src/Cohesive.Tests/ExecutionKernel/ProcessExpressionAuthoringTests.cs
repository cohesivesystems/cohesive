using System.Text;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessExpressionAuthoringTests
{
    const string SourceReference = "tests/ari-221/process-expression-authoring";

    [Fact]
    public void SequentialCollectionExpression_LowersToEquivalentCanonicalIrWithoutExplicitEntryOrEdges()
    {
        var expression = CreateExpressionProcess();
        var lowLevel = CreateEquivalentLowLevelProcess();

        Assert.True(expression.IsValid, Format(expression.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(lowLevel.Definition, expression.Definition);
        Assert.Equal(lowLevel.Document.Metadata.Fingerprint, expression.Document.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(expression.Document));
        Assert.Equal(Ids.EvaluateRelation, expression.Definition.Entry);
        Assert.Equal(7, expression.Definition.Nodes.Length);
        Assert.Contains(expression.Definition.Nodes, static node => node.Id == Ids.ExplicitSignal);

        var mappedReferences = expression.Document.Metadata.SourceMap.Entries
            .Select(static entry => entry.Reference)
            .ToArray();
        Assert.Contains(
            mappedReferences,
            reference => reference.StartsWith(
                ProcessAuthoringIdentities.ConventionAuthority,
                StringComparison.Ordinal));
        Assert.Contains(
            mappedReferences,
            reference => reference.StartsWith(
                $"{ProcessAuthoring.ExpressionProducer}#explicit",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ExpressionAuthoredDocument_StrictlyRoundTripsWithoutAuthoringState()
    {
        var authored = CreateExpressionProcess();
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
        Assert.DoesNotContain(
            restoredDocument.Definition.EnumerateObject(),
            static property => property.Name.Contains("delegate", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("expressionTree", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExpressionSequence_RejectsMissingMisplacedAndForeignTerminalOperations()
    {
        Assert.Throws<InvalidOperationException>(() => ProcessAuthoring.Create<string, string>(
            Metadata(),
            process => process.Return(new("explicit-return"), process.Input.Value)));

        Assert.Throws<ArgumentException>(() => ProcessAuthoring.CreateExpression<string, string>(
            Metadata(),
            process => [process.DurableCut()]));

        Assert.Throws<ArgumentException>(() => ProcessAuthoring.CreateExpression<string, string>(
            Metadata(),
            process => [process.Return(process.Input.Value), process.DurableCut()]));

        ProcessExpression<string, string>? foreign = null;
        _ = Assert.Throws<ArgumentException>(() => ProcessAuthoring.CreateExpression<string, string>(
            Metadata(),
            process =>
            {
                foreign = process.Return(process.Input.Value);
                return [];
            }));
        Assert.NotNull(foreign);
        Assert.Throws<ArgumentException>(() => ProcessAuthoring.CreateExpression<string, string>(
            Metadata(),
            _ => [foreign]));
    }

    [Fact]
    public void InvalidExpressionAuthoredValues_ReportConventionAndExplicitIdentitySources()
    {
        var stringContract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        var missingBinding = Expr.BoundValue(new("missing-binding"));
        var conventional = ProcessAuthoring.CreateExpression<string, string>(
            Metadata(),
            process =>
            [
                process.Return(process.CanonicalValue<string>(missingBinding, stringContract))
            ]);
        var explicitIdentity = ProcessAuthoring.CreateExpression<string, string>(
            Metadata(),
            process =>
            [
                process.Return(
                    process.CanonicalValue<string>(missingBinding, stringContract),
                    id: new("explicit/invalid-return"))
            ]);

        Assert.False(conventional.IsValid);
        Assert.False(explicitIdentity.IsValid);
        Assert.Contains(
            conventional.Validation.Diagnostics.SelectMany(static diagnostic =>
                diagnostic.Evidence?.SourceReferences ?? []),
            reference => reference.StartsWith(
                ProcessAuthoringIdentities.ConventionAuthority,
                StringComparison.Ordinal));
        Assert.Contains(
            explicitIdentity.Validation.Diagnostics.SelectMany(static diagnostic =>
                diagnostic.Evidence?.SourceReferences ?? []),
            reference => reference.StartsWith(
                $"{ProcessAuthoring.ExpressionProducer}#explicit",
                StringComparison.Ordinal));
    }

    static Process<string, string> CreateExpressionProcess() =>
        ProcessAuthoring.CreateExpression<string, string>(
            Metadata(),
            process =>
            {
                var relationOutput = process.Output<string>("relation-result");
                var transitionOutput = process.Output<string>("transition-result");
                var requestOutput = process.Output<string>("request-result");
                var target = process.Constant("target");
                return
                [
                    process.EvaluateRelation(
                        DefinitionReference("relation/review"),
                        process.Input.Value,
                        relationOutput),
                    process.InvokeTransition(
                        DefinitionReference("transition/review"),
                        process.Input.Value,
                        relationOutput.Value,
                        transitionOutput),
                    process.Request(
                        new RequestContractReference(DefinitionReference("request/review")),
                        transitionOutput.Value,
                        [process.RequestOutcome(new("completed"), requestOutput)]),
                    process.EmitEvent(
                        new DomainEventContractReference(DefinitionReference("event/reviewed")),
                        requestOutput.Value),
                    process.SendSignal(
                        new SignalContractReference(DefinitionReference("signal/review")),
                        target,
                        requestOutput.Value,
                        id: Ids.ExplicitSignal),
                    process.DurableCut(),
                    process.Return(requestOutput.Value)
                ];
            });

    static Process<string, string> CreateEquivalentLowLevelProcess() =>
        ProcessAuthoring.Create<string, string>(
            new(
                Ids.Definition,
                Ids.Revision,
                Ids.EvaluateRelation,
                ProcessRecoveryPolicy.ContinueAttempt,
                Provenance()),
            process =>
            {
                var relationOutput = process.Output<string>(Binding("relation-result"));
                var transitionOutput = process.Output<string>(Binding("transition-result"));
                var requestOutput = process.Output<string>(Binding("request-result"));
                var target = process.Constant("target");
                var requestBranch = ProcessAuthoringIdentities.NodeFor(
                    new(["request", Ids.Request.Value, "outcomes", "completed"]));

                process.EvaluateRelation(
                    Ids.EvaluateRelation,
                    DefinitionReference("relation/review"),
                    process.Input.Value,
                    process.Continuation(
                        process.Edge(Ids.EvaluateRelation, "next", Ids.InvokeTransition),
                        relationOutput));
                process.InvokeTransition(
                    Ids.InvokeTransition,
                    DefinitionReference("transition/review"),
                    process.Input.Value,
                    relationOutput.Value,
                    process.Continuation(
                        process.Edge(Ids.InvokeTransition, "next", Ids.Request),
                        transitionOutput));
                process.Request(
                    Ids.Request,
                    new RequestContractReference(DefinitionReference("request/review")),
                    transitionOutput.Value,
                    [
                        process.RequestOutcome(
                            requestBranch,
                            new("completed"),
                            process.Continuation(
                                process.Edge(requestBranch, "next", Ids.EmitEvent),
                                requestOutput))
                    ]);
                process.EmitEvent(
                    Ids.EmitEvent,
                    new DomainEventContractReference(DefinitionReference("event/reviewed")),
                    requestOutput.Value,
                    process.Edge(Ids.EmitEvent, "next", Ids.ExplicitSignal));
                process.SendSignal(
                    Ids.ExplicitSignal,
                    new SignalContractReference(DefinitionReference("signal/review")),
                    target,
                    requestOutput.Value,
                    process.Edge(Ids.ExplicitSignal, "next", Ids.DurableCut));
                process.DurableCut(
                    Ids.DurableCut,
                    process.Edge(Ids.DurableCut, "next", Ids.Return));
                process.Return(Ids.Return, requestOutput.Value);
            });

    static ProcessAuthoringMetadata Metadata() => new(
        Ids.Definition,
        Ids.Revision,
        ProcessRecoveryPolicy.ContinueAttempt,
        Provenance(),
        displayName: "Expression-authored review");

    static ValueBindingId Binding(string name) => ProcessAuthoringIdentities.BindingFor(
        ProcessAuthoringIdentities.NodeFor(new(["body", "bindings", name])),
        "value");

    static ExecutionNodeId Node(int index, string role) => ProcessAuthoringIdentities.NodeFor(
        new(["body", "steps", index.ToString(System.Globalization.CultureInfo.InvariantCulture), role]));

    static ExecutionDefinitionReference DefinitionReference(string definitionId) => new(
        new(definitionId),
        Ids.Revision,
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('1', 64)));

    static ExecutionProvenance Provenance() => new(
        new(ProcessAuthoring.ExpressionProducer, "1"),
        new(SourceReference),
        DocumentOrigin.User);

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    static class Ids
    {
        public static readonly ExecutionDefinitionId Definition = new("process/expression-review");
        public static readonly ExecutionRevisionId Revision = new("revision/1");
        public static readonly ExecutionNodeId EvaluateRelation = Node(0, "evaluate-relation");
        public static readonly ExecutionNodeId InvokeTransition = Node(1, "invoke-transition");
        public static readonly ExecutionNodeId Request = Node(2, "request");
        public static readonly ExecutionNodeId EmitEvent = Node(3, "emit-event");
        public static readonly ExecutionNodeId ExplicitSignal = new("signal/review");
        public static readonly ExecutionNodeId DurableCut = Node(5, "durable-cut");
        public static readonly ExecutionNodeId Return = Node(6, "return");
    }
}
