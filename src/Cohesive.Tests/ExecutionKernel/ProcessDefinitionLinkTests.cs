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

    static ExecutionProvenance Provenance() => new(
        new("process-link-tests", "1"),
        new("tests/execution-kernel/process-linking"),
        DocumentOrigin.Generated);

    static string FormatDiagnostics(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
