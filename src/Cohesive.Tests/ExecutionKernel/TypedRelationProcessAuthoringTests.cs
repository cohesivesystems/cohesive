using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class TypedRelationProcessAuthoringTests
{
    [Fact]
    public void TypedRelationEvaluation_IsByteEquivalentToRawExactReferenceAuthoring()
    {
        var metadata = Metadata();
        var typed = GeneratedTypedRelationProcess.Define(metadata);
        var raw = GeneratedRawRelationProcess.Define(metadata);

        Assert.True(typed.IsValid, Format(typed.Validation));
        Assert.True(raw.IsValid, Format(raw.Validation));
        Assert.Equal(raw.Definition, typed.Definition);
        Assert.Equal(raw.Document.Metadata.Fingerprint, typed.Document.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(raw.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(typed.Document));

        var evaluations = typed.Definition.Nodes.OfType<EvaluateRelationProcessNode>().ToArray();
        Assert.Equal(2, evaluations.Length);
        Assert.All(
            evaluations,
            evaluation => Assert.Equal(GeneratedTypedRelationCatalog.Normalize.Reference, evaluation.Relation));
        var link = GeneratedTypedRelationCatalog.Normalize.CreateProcessDefinitionLink();
        Assert.Equal(GeneratedTypedRelationCatalog.Normalize.Reference, link.Definition);
        Assert.Equal(GeneratedTypedRelationCatalog.Normalize.InputContract, link.Input);
        Assert.Equal(GeneratedTypedRelationCatalog.Normalize.ResultContract, link.Result);
    }

    static ProcessAuthoringMetadata Metadata() => new(
        new("process/tests/typed-relation"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-369/typed-relation"),
            DocumentOrigin.Generated));

    static string Format(DocumentValidationResult validation) =>
        string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}:{diagnostic.Location}:{diagnostic.Message}"));
}

public sealed class TypedRelationInput
{
    public required string Id { get; init; }

    public required string Value { get; init; }
}

public sealed class TypedRelationResult
{
    public required string Id { get; init; }

    public required string Normalized { get; init; }
}

public static class GeneratedTypedRelationCatalog
{
    public static Relation<TypedRelationInput, TypedRelationResult> Normalize { get; } = Create();

    static Relation<TypedRelationInput, TypedRelationResult> Create()
    {
        var author = RelationQuery.Expression();
        var input = author.Source<TypedRelationInput>();
        var output = author.Project(input, value => new TypedRelationResult
        {
            Id = value.Id,
            Normalized = value.Value
        });
        var authored = output.BuildRelation(
            value => value.Id,
            id: new("relation/tests/typed-process"),
            name: new("Typed Process relation"));
        return author.CreateRelation(
            authored,
            input,
            output,
            new("1"),
            new(origin: DocumentOrigin.Generated, producer: "tests", producerVersion: "1"));
    }
}

[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedTypedRelationProcess
{
    static async ProcessTask<TypedRelationResult> Run(ProcessContext process, TypedRelationInput input)
    {
        var normalized = await process.Query(
            GeneratedTypedRelationCatalog.Normalize,
            input,
            id: new("normalize/query"));
        var read = await process.Read(
            GeneratedTypedRelationCatalog.Normalize,
            input,
            id: new("normalize/read"));
        return read;
    }
}

[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedRawRelationProcess
{
    static async ProcessTask<TypedRelationResult> Run(ProcessContext process, TypedRelationInput input)
    {
        var normalized = await process.Query<TypedRelationResult>(
            GeneratedTypedRelationCatalog.Normalize.Reference,
            input,
            id: new("normalize/query"));
        var read = await process.Read<TypedRelationResult>(
            GeneratedTypedRelationCatalog.Normalize.Reference,
            input,
            id: new("normalize/read"));
        return read;
    }
}
