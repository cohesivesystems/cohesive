using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessClosureCompilationTests
{
    static readonly ValueContract Contract = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ExecutionProvenance Provenance = new(new("closure-tests", "1"), new("tests/process-closure"), DocumentOrigin.Generated);
    static readonly ExecutionDefinitionDocument Request = InteractionContractDocuments.Create(
        new("request/child"), new("1"), new RequestContractDefinition(
            new(Contract, new("schema/1")),
            new RequestResponseObligation(
                [new RequestResultDefinition(new("approved"), new(Contract, new("schema/1"))),
                 new RequestFailureDefinition(new("failed"), new(Contract, new("schema/1"))),
                 new RequestFailureDefinition(new("cancelled"), new(Contract, new("schema/1"))),
                 new RequestFailureDefinition(new("terminated"), new(Contract, new("schema/1")))],
                RequestOptionalTerminalSemantics.Unsupported, RequestOptionalTerminalSemantics.Unsupported,
                RequestResultDisposition.Reject, RequestResultDisposition.Reject,
                RequestResultDisposition.ReusePriorDisposition, RequestRetrySemantics.Never,
                RequestResolutionSemantics.TerminalFailure, RequestResolutionSemantics.TerminalFailure,
                TimeSpan.FromDays(7))), Provenance);

    [Fact]
    public void Diamond_ReusesOneChildAndExcludesUnselectedDocuments()
    {
        var leaf = Document("leaf");
        var left = Document("left", Reference(leaf));
        var right = Document("right", Reference(leaf));
        var root = Document("root", Reference(left), Reference(right));
        var unused = Document("unused");
        var result = Compile([Reference(root), Reference(left), Reference(root)], [unused, root, right, leaf, left]);
        Assert.True(result.IsSuccessful, Format(result.Validation));
        Assert.Equal(new[] { "leaf", "left", "right", "root" }, result.Plans.Select(plan => plan.DefinitionReference.DefinitionId.Value));
        var compiledLeaf = result.Plans[0];
        Assert.Same(compiledLeaf.DefinitionLink, result.Plans[1].ValidationContext.DefinitionLinks.Single());
        Assert.Equal(new[] { Reference(left), Reference(right) }, root.GetDefinition<ProcessDefinition>().GetProcessDependencies());
    }

    [Fact]
    public void MissingChild_ReturnsResolutionEvidenceWithoutPartialPlans()
    {
        var leaf = Document("leaf");
        var root = Document("root", Reference(leaf));
        var result = Compile([Reference(root)], [root]);
        Assert.False(result.IsSuccessful);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Validation.Diagnostics, d => d.Code == ExecutionDefinitionDiagnosticCodes.DefinitionIdentityUnknown);
    }

    [Fact]
    public void DifferentChildRevisionPayload_RejectsFingerprintMismatch()
    {
        var leaf = Document("leaf");
        var root = Document("root", Reference(leaf));
        var replacement = ProcessDefinitionDocuments.Create(new("leaf"), new("1"),
            new(Contract, Contract, new("return"), [new ReturnProcessNode(new("return"), Expr.Const("changed"))], ProcessRecoveryPolicy.ContinueAttempt), Provenance);
        var result = Compile([Reference(root)], [root, replacement]);
        Assert.False(result.IsSuccessful);
        Assert.Empty(result.Plans);
        Assert.Contains(result.Validation.Diagnostics, d => d.Code == ExecutionDefinitionDiagnosticCodes.FingerprintIncompatible);
    }

    [Fact]
    public void InvalidChild_StillPassesCanonicalSemanticValidation()
    {
        var leaf = ProcessDefinitionDocuments.Create(new("leaf"), new("1"),
            new(Contract, Contract, new("missing"), [], ProcessRecoveryPolicy.ContinueAttempt), Provenance);
        var root = Document("root", Reference(leaf));
        var result = Compile([Reference(root)], [root, leaf]);
        Assert.False(result.IsSuccessful);
        Assert.Empty(result.Plans);
        Assert.NotEmpty(result.Validation.Diagnostics);
    }

    [Fact]
    public void Closure_MatchesExplicitBottomUpPlansAndPreservesCanonicalPayloads()
    {
        var leaf = Document("leaf");
        var parent = Document("parent", Reference(leaf), Reference(leaf));
        var closure = Compile([Reference(parent)], [parent, leaf]);
        Assert.True(closure.IsSuccessful, Format(closure.Validation));
        Assert.Single(parent.GetDefinition<ProcessDefinition>().GetProcessDependencies());
        InteractionContractCatalog.TryCreate([Request], out var interactions);
        var explicitLeaf = ProcessStaticCompiler.Compile(leaf, new(interactionContracts: interactions));
        var explicitParent = ProcessStaticCompiler.Compile(parent,
            new([explicitLeaf.Plan!.DefinitionLink], interactionContracts: interactions));
        Assert.True(explicitParent.IsSuccessful, Format(explicitParent.Validation));
        Assert.Equal(explicitParent.Plan!.Definition, closure.Plans[1].Definition);
        Assert.Equal(explicitParent.Plan.DefinitionLink, closure.Plans[1].DefinitionLink);
        Assert.Equal(ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(explicitParent.Plan.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(closure.Plans[1].Document));
    }

    [Fact]
    public void ExternalProcessLinksCannotCompeteWithCanonicalDependencyAuthority()
    {
        var leaf = Document("leaf");
        var compiled = ProcessStaticCompiler.Compile(leaf, new());
        ExecutionDefinitionDocumentCatalog.TryCreate([leaf], out var catalog);
        Assert.Throws<ArgumentException>(() => ProcessStaticCompiler.CompileClosure(
            [Reference(leaf)], catalog!, new([compiled.Plan!.DefinitionLink])));
    }

    [Fact]
    public void WrongDocumentKind_ReturnsCanonicalKindDiagnostic()
    {
        var result = Compile([Reference(Request)], [Request]);
        Assert.False(result.IsSuccessful);
        Assert.Equal(Reference(Request), result.FailedDefinition);
        Assert.Contains(result.Validation.Diagnostics, d => d.Code == ProcessDefinitionDocumentDiagnosticCodes.KindMismatch);
    }

    static ProcessClosureCompilationResult Compile(ExecutionDefinitionReference[] roots, ExecutionDefinitionDocument[] documents)
    {
        var validation = ExecutionDefinitionDocumentCatalog.TryCreate(documents, out var catalog);
        Assert.True(validation.IsValid, Format(validation));
        validation = InteractionContractCatalog.TryCreate([Request], out var interactions);
        Assert.True(validation.IsValid, Format(validation));
        return ProcessStaticCompiler.CompileClosure(roots, catalog!, new(interactionContracts: interactions));
    }

    static ExecutionDefinitionDocument Document(string id, params ExecutionDefinitionReference[] children)
    {
        var nodes = new List<ProcessNode>();
        for (var index = 0; index < children.Length; index++)
        {
            var next = index + 1 < children.Length ? $"child-{index + 1}" : "return";
            nodes.Add(new InvokeProcessProcessNode(new($"child-{index}"), children[index], new(Reference(Request)),
                new(new("approved"), new("failed"), new("cancelled"), new("terminated")),
                Expr.BoundValue(ProcessBindingIds.Input), ProcessChildPurpose.Work, ProcessChildCancellationPolicy.Propagate,
                [new(new($"approved-{index}"), new("approved"), new(new(new($"edge-approved-{index}"), new(next)))),
                 new(new($"failed-{index}"), new("failed"), new(new(new($"edge-failed-{index}"), new(next)))),
                 new(new($"cancelled-{index}"), new("cancelled"), new(new(new($"edge-cancelled-{index}"), new(next)))),
                 new(new($"terminated-{index}"), new("terminated"), new(new(new($"edge-terminated-{index}"), new(next))))]));
        }
        nodes.Add(new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input)));
        return ProcessDefinitionDocuments.Create(new(id), new("1"),
            new(Contract, Contract, new(children.Length == 0 ? "return" : "child-0"), [.. nodes], ProcessRecoveryPolicy.ContinueAttempt), Provenance);
    }

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) => new(
        document.Metadata.DefinitionId, document.Metadata.RevisionId, document.Metadata.Fingerprint);
    static string Format(DocumentValidationResult validation) => string.Join("\n", validation.Diagnostics.Select(d => $"{d.Code}: {d.Message}"));
}
