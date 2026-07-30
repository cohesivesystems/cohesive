using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using Cohesive.Transitions.IR;
using CanonicalTransitionDefinition = Cohesive.Transitions.IR.TransitionDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessDefinitionLinkTests
{
    static readonly ValueContract InputContract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract ObservationContract = new(new JsonTypeRef(JsonTypeKind.Object));
    static readonly ValueContract OutcomeContract = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void TryCreateTransition_DerivesExactReferenceAndContractsFromValidatedDocument()
    {
        var document = TransitionDefinitionDocuments.Create(
            new("transition/review"),
            new("revision/1"),
            Definition(),
            Provenance());

        var validation = ProcessDefinitionLink.TryCreateTransition(document, out var link);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        var exactLink = Assert.IsType<ProcessDefinitionLink>(link);
        Assert.Equal(
            new ExecutionDefinitionReference(
                document.Metadata.DefinitionId,
                document.Metadata.RevisionId,
                document.Metadata.Fingerprint),
            exactLink.Definition);
        Assert.Equal(ProcessDefinitionLinkKind.Transition, exactLink.Kind);
        Assert.Equal(InputContract, exactLink.Input);
        Assert.Equal(OutcomeContract, exactLink.Result);

        var context = new ProcessDefinitionValidationContext([exactLink]);
        Assert.True(context.TryResolve(exactLink.Definition, out var resolved));
        Assert.Same(exactLink, resolved);
    }

    [Fact]
    public void TryCreateTransition_RejectsAValidDocumentFromAnotherSemanticBlock()
    {
        var document = InteractionContractDocuments.Create(
            new("interaction/reviewed"),
            new("revision/1"),
            new DomainEventContractDefinition(
                new(InputContract, new("schema/v1"))),
            Provenance());

        var validation = ProcessDefinitionLink.TryCreateTransition(document, out var link);

        Assert.False(validation.IsValid);
        Assert.Null(link);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == TransitionDefinitionDocumentDiagnosticCodes.KindMismatch);
    }

    [Fact]
    public void TryCreateProcess_WithSemanticExtensions_DoesNotClaimCompleteDependencyEvidence()
    {
        var extension = new ExecutionDefinitionExtension(
            new("example.process-extension"),
            new("example-process-extension/v1"),
            PortableValue.Concrete(InputContract, ObservationValue.FromString("enabled")));
        var document = ProcessDefinitionDocuments.Create(
            new("process/extended"),
            new("revision/1"),
            ProcessDefinition(InputContract),
            Provenance(),
            extensions: [extension]);

        var validation = ProcessDefinitionLink.TryCreateProcess(document, out var link);

        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        var exactLink = Assert.IsType<ProcessDefinitionLink>(link);
        Assert.False(exactLink.HasCompleteProcessDependencyEvidence);
        Assert.Empty(exactLink.ProcessDependencies);
    }

    [Fact]
    public void TryCreateProcess_WithShapeGraph_DerivesLinkForNamedContracts()
    {
        TypeId payloadType = new("process/payload");
        var graph = new ShapeGraph(
            new("process-link-tests"),
            [],
            [
                new TypeDefinition.Structural(
                    payloadType,
                    [new(new("value"), new ScalarTypeRef(ScalarTypeKind.String))])
            ]);
        var contract = new ValueContract(new NamedTypeRef(payloadType));
        var document = ProcessDefinitionDocuments.Create(
            new("process/named-contract"),
            new("revision/1"),
            ProcessDefinition(contract),
            Provenance());

        var graphless = ProcessDefinitionLink.TryCreateProcess(document, out _);
        var validation = ProcessDefinitionLink.TryCreateProcess(document, graph, out var link);

        Assert.False(graphless.IsValid);
        Assert.True(validation.IsValid, FormatDiagnostics(validation));
        var exactLink = Assert.IsType<ProcessDefinitionLink>(link);
        Assert.Equal(contract, exactLink.Input);
        Assert.Equal(contract, exactLink.Result);
        Assert.True(exactLink.HasCompleteProcessDependencyEvidence);
        Assert.Equal(ProcessRecoveryPolicy.ContinueAttempt, exactLink.RecoveryPolicy);
    }

    [Fact]
    public void Constructor_RejectsProcessDependencyEvidenceForAnotherSemanticKind()
    {
        var definition = new ExecutionDefinitionReference(
            new("transition/review"),
            new("revision/1"),
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                new string('a', 64)));

        var exception = Assert.Throws<ArgumentException>(() => new ProcessDefinitionLink(
            definition,
            ProcessDefinitionLinkKind.Transition,
            InputContract,
            OutcomeContract,
            processDependencies: []));

        Assert.Equal("processDependencies", exception.ParamName);
    }

    static CanonicalTransitionDefinition Definition() => new(
        InputContract,
        ObservationContract,
        OutcomeContract,
        [],
        new(
            new("body"),
            [
                new OutcomeTransitionNode(
                    new("outcome"),
                    TransitionOutcomeDisposition.NoChange,
                    Expr.Const("reviewed"))
            ]));

    static Cohesive.Processes.IR.ProcessDefinition ProcessDefinition(ValueContract contract) => new(
        contract,
        contract,
        new("return"),
        [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))],
        ProcessRecoveryPolicy.ContinueAttempt);

    static ExecutionProvenance Provenance() => new(
        new("process-link-tests", "1"),
        new("tests/execution-kernel/process-linking"),
        DocumentOrigin.Generated);

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
