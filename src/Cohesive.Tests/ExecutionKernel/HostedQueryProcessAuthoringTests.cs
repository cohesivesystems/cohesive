using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class HostedQueryProcessAuthoringTests
{
    [Fact]
    public void TypedHostedQueryEvaluation_IsByteEquivalentToRawExactReferenceAuthoring()
    {
        var metadata = Metadata();
        var typed = GeneratedTypedHostedQueryProcess.Define(metadata);
        var raw = GeneratedRawHostedQueryProcess.Define(metadata);

        Assert.True(GeneratedHostedQueryCatalog.ById.IsValid, Format(GeneratedHostedQueryCatalog.ById.Validation));
        Assert.Equivalent(
            HostedQueryDefinitionDocuments.Validate(GeneratedHostedQueryCatalog.ById.Document),
            GeneratedHostedQueryCatalog.ById.Validation,
            strict: true);
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
            evaluation => Assert.Equal(GeneratedHostedQueryCatalog.ById.Reference, evaluation.Relation));
        var link = GeneratedHostedQueryCatalog.ById.CreateProcessDefinitionLink();
        Assert.Equal(GeneratedHostedQueryCatalog.ById.Reference, link.Definition);
        Assert.Equal(GeneratedHostedQueryCatalog.ById.InputContract, link.Input);
        Assert.Equal(GeneratedHostedQueryCatalog.ById.ResultContract, link.Result);
    }

    [Fact]
    public void ProcessLink_RejectsInvalidHostedQueryAuthority()
    {
        var invalid = HostedQuery<HostedQueryProcessInput, HostedQueryProcessResult>.Create(
            new("query/tests/invalid-hosted-process"),
            new("1"),
            new("tests.hosted-query", "1"),
            new Dictionary<string, string> { ["policy"] = "exact" },
            new(
                new("tests.hosted-query-authoring", "1"),
                new("tests/ari-370/invalid-hosted-query"),
                DocumentOrigin.Generated));

        Assert.False(invalid.IsValid);
        Assert.Throws<InvalidOperationException>(() => invalid.CreateProcessDefinitionLink());
    }

    static ProcessAuthoringMetadata Metadata() => new(
        new("process/tests/typed-hosted-query"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-370/typed-hosted-query"),
            DocumentOrigin.Generated));

    static string Format(DocumentValidationResult validation) =>
        string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}:{diagnostic.Location}:{diagnostic.Message}"));
}

public sealed class HostedQueryProcessInput
{
    public required string Id { get; init; }
}

public sealed class HostedQueryProcessResult
{
    public required string Id { get; init; }

    public required string Value { get; init; }
}

public sealed record HostedQueryProcessConfiguration(string SourceFamily, string ReadPolicy);

public static class GeneratedHostedQueryCatalog
{
    public static HostedQuery<HostedQueryProcessInput, HostedQueryProcessResult> ById { get; } =
        HostedQuery<HostedQueryProcessInput, HostedQueryProcessResult>.Create(
            new("query/tests/typed-hosted-process"),
            new("1"),
            new("tests.hosted-query", "1"),
            new HostedQueryProcessConfiguration("entity", "partition-exact"),
            new(
                new("tests.hosted-query-authoring", "1"),
                new("tests/ari-370/typed-hosted-query"),
                DocumentOrigin.Generated));
}

[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedTypedHostedQueryProcess
{
    static async ProcessTask<HostedQueryProcessResult> Run(
        ProcessContext process,
        HostedQueryProcessInput input)
    {
        var queried = await process.Query(
            GeneratedHostedQueryCatalog.ById,
            input,
            id: new("hosted/query"));
        var read = await process.Read(
            GeneratedHostedQueryCatalog.ById,
            input,
            id: new("hosted/read"));
        return read;
    }
}

[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedRawHostedQueryProcess
{
    static async ProcessTask<HostedQueryProcessResult> Run(
        ProcessContext process,
        HostedQueryProcessInput input)
    {
        var queried = await process.Query<HostedQueryProcessResult>(
            GeneratedHostedQueryCatalog.ById.Reference,
            input,
            id: new("hosted/query"));
        var read = await process.Read<HostedQueryProcessResult>(
            GeneratedHostedQueryCatalog.ById.Reference,
            input,
            id: new("hosted/read"));
        return read;
    }
}
