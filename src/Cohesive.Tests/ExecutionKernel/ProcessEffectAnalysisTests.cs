using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessEffectAnalysisTests
{
    static readonly ValueContract StringContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void HostOperations_ConservativelyDeriveExternalInteractionAndBlockWholeDefinitionAtomicity()
    {
        var transition = Reference("transition/effectful-host-operation", 'a');
        var relation = Reference("relation/effectful-host-operation", 'b');
        var definition = new CanonicalProcessDefinition(
            StringContract,
            StringContract,
            new("mutate"),
            [
                new InvokeTransitionProcessNode(
                    new("mutate"),
                    transition,
                    Expr.Const("subject/1"),
                    Expr.Const("command"),
                    new(Edge("edge/mutate-observe", "observe"))),
                new EvaluateRelationProcessNode(
                    new("observe"),
                    relation,
                    Expr.Const("query"),
                    new(Edge("edge/observe-return", "return"))),
                new ReturnProcessNode(new("return"), Expr.Const("done"))
            ],
            ProcessRecoveryPolicy.ContinueAttempt);
        var document = ProcessDefinitionDocuments.Create(
            new("process/conservative-host-effects"),
            new("revision/1"),
            definition,
            Provenance(),
            sourceMap: new(
            [
                new("src/HostEffects.cs:10", new(["nodes", "0"])),
                new("src/HostEffects.cs:20", new(["nodes", "1"]))
            ]));
        var context = new ProcessDefinitionValidationContext(
            definitions:
            [
                new(transition, ProcessDefinitionLinkKind.Transition, StringContract, StringContract),
                new(relation, ProcessDefinitionLinkKind.RelationQuery, StringContract, StringContract)
            ]);

        var ordinary = ProcessStaticCompiler.Compile(document, context);
        var plan = Assert.IsType<CompiledProcessPlan>(ordinary.Plan);

        Assert.True(ordinary.IsSuccessful, FormatDiagnostics(ordinary.Validation));
        Assert.Equal(
            [ProcessEffectKind.AggregateMutation, ProcessEffectKind.ExternalInteraction],
            plan.EffectSummary.Effects
                .Where(static effect => effect.Node == new ExecutionNodeId("mutate"))
                .Select(static effect => effect.Kind));
        Assert.Equal(
            [ProcessEffectKind.Observation, ProcessEffectKind.ExternalInteraction],
            plan.EffectSummary.Effects
                .Where(static effect => effect.Node == new ExecutionNodeId("observe"))
                .Select(static effect => effect.Kind));

        var atomic = ProcessStaticCompiler.Compile(
            document,
            context,
            new(ProcessAtomicScopeDemand.WholeDefinition));

        Assert.False(atomic.IsSuccessful);
        Assert.Null(atomic.Plan);
        var diagnostics = atomic.Validation.Diagnostics
            .Where(static diagnostic => diagnostic.Code
                == ProcessCompilationDiagnosticCodes.AtomicScopeContainsExternalInteraction)
            .ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.Collection(
            diagnostics,
            diagnostic =>
            {
                Assert.Equal("/definition/nodes/0", diagnostic.Location);
                Assert.Equal(["src/HostEffects.cs:10"], diagnostic.Evidence?.SourceReferences);
                Assert.Contains(transition.DefinitionId.Value, diagnostic.Evidence?.Observed);
            },
            diagnostic =>
            {
                Assert.Equal("/definition/nodes/1", diagnostic.Location);
                Assert.Equal(["src/HostEffects.cs:20"], diagnostic.Evidence?.SourceReferences);
                Assert.Contains(relation.DefinitionId.Value, diagnostic.Evidence?.Observed);
            });
    }

    static ProcessEdge Edge(string id, string target) => new(new(id), new(target));

    static ExecutionDefinitionReference Reference(string id, char fingerprintDigit) => new(
        new(id),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string(fingerprintDigit, 64)));

    static ExecutionProvenance Provenance() => new(
        new("process-effect-analysis-tests", "1"),
        new("tests/execution-kernel/process-effect-analysis"),
        DocumentOrigin.Generated);

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
