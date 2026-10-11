using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedNoncanonicalWire_PreservesDirectCompilationDiagnostics(bool unknownMember)
    {
        var source = Document("imported");
        var payload = JsonNode.Parse(source.Definition.GetRawText())!.AsObject();
        if (unknownMember)
            payload["futureMember"] = true;
        else
            Assert.True(payload.Remove("recoveryPolicy"));
        using var parsed = JsonDocument.Parse(payload.ToJsonString());
        var fingerprint = ExecutionDefinitionFingerprinter.Compute(source.Metadata.SchemaVersion,
            source.Kind, parsed.RootElement, source.Extensions);
        var imported = new ExecutionDefinitionDocument(source.Kind,
            new(source.Metadata.DefinitionId, source.Metadata.RevisionId, source.Metadata.SchemaVersion,
                fingerprint, source.Metadata.Provenance), parsed.RootElement, source.Extensions);
        var direct = ProcessStaticCompiler.Compile(imported, new());
        var closure = Compile([Reference(imported)], [imported]);
        Assert.False(direct.IsSuccessful);
        Assert.False(closure.IsSuccessful);
        Assert.Empty(closure.Plans);
        Assert.Equal(JsonSerializer.Serialize(direct.Validation.Diagnostics),
            JsonSerializer.Serialize(closure.Validation.Diagnostics));
    }

    [Theory]
    [InlineData(16_384)]
    [InlineData(65_536)]
    public void LargeClosure_ReusesProjectionWithoutRetainingValidation(int payloadLength)
    {
        var document = ProcessDefinitionDocuments.Create(new("large"), new("1"),
            new(Contract, Contract, new("return"),
                [new ReturnProcessNode(new("return"), Expr.Const(new string('x', payloadLength)))],
                ProcessRecoveryPolicy.ContinueAttempt), Provenance);
        ExecutionDefinitionDocumentCatalog.TryCreate([document], out var catalog);
        ProcessDefinitionValidationContext context = new();
        ExecutionDefinitionReference[] roots = [Reference(document)];
        Assert.True(ProcessStaticCompiler.Compile(document, context).IsSuccessful);
        Assert.True(ProcessStaticCompiler.CompileClosure(roots, catalog!, context).IsSuccessful);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var direct = ProcessStaticCompiler.Compile(document, context);
        var directBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var closure = ProcessStaticCompiler.CompileClosure(roots, catalog!, context);
        var closureBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(direct.IsSuccessful && closure.IsSuccessful);
        Assert.Equal(direct.Plan!.Definition, Assert.Single(closure.Plans).Definition);
        Assert.True(closureBytes < directBytes,
            $"Closure {closureBytes} bytes should avoid the repeated projection in direct compilation {directBytes} bytes.");
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
