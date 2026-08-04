using System.Text;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessComputationAuthoringTests
{
    [Fact]
    public void GeneratedComputation_IsByteEquivalentToCanonicalBuilderAuthoring()
    {
        var generated = GeneratedCustomerQueryProcess.Define(Metadata());
        var query = ProcessAuthoringIdentities.NodeFor(new(["body", "query-row"]));
        var returned = ProcessAuthoringIdentities.NodeFor(new(["body", "return-1"]));
        var lowLevel = ProcessAuthoring.Create<string, string>(
            Metadata().WithEntry(query),
            process =>
            {
                var output = process.Output<string>(query, "result");

                process.EvaluateRelation(
                    query,
                    GeneratedCustomerQueryProcess.Relation,
                    process.Input.Value,
                    process.Continuation(
                        process.Edge(query, "next", returned),
                        output));
                process.Return(returned, output.Value);
            });

        Assert.True(generated.IsValid, Format(generated.Validation));
        Assert.True(lowLevel.IsValid, Format(lowLevel.Validation));
        Assert.Equal(lowLevel.Definition, generated.Definition);
        Assert.Equal(lowLevel.Document.Metadata.Fingerprint, generated.Document.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(lowLevel.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));
    }

    [Fact]
    public void PureLocalInsertion_DoesNotRenumberSemanticNodes()
    {
        var original = GeneratedCustomerQueryProcess.Define(Metadata());
        var withPureLocal = GeneratedCustomerQueryProcessWithPureLocal.Define(Metadata());

        Assert.Equal(original.Definition, withPureLocal.Definition);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(original.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(withPureLocal.Document));
    }

    [Fact]
    public void GeneratedComputation_HonorsMatchingExplicitEntryAndRejectsConflict()
    {
        var entry = ProcessAuthoringIdentities.NodeFor(new(["body", "query-row"]));

        var generated = GeneratedCustomerQueryProcess.Define(Metadata().WithEntry(entry));

        Assert.Equal(entry, generated.Definition.Entry);
        Assert.Throws<ArgumentException>(() =>
            GeneratedCustomerQueryProcess.Define(Metadata().WithEntry(new("conflicting-entry"))));
    }

    [Fact]
    public void GeneratedDocument_StrictlyRestoresWithoutHostLanguageState()
    {
        var generated = GeneratedCustomerQueryProcess.Define(Metadata());
        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(generated.Document);

        var validation = ProcessDefinitionDocuments.TryDeserialize(
            Encoding.UTF8.GetString(canonical),
            out var restoredDocument,
            out var restoredDefinition);

        Assert.True(validation.IsValid, Format(validation));
        Assert.Equal(generated.Document, restoredDocument);
        Assert.Equal(generated.Definition, restoredDefinition);
        Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restoredDocument!));
        Assert.DoesNotContain(
            restoredDocument!.Definition.EnumerateObject(),
            static property => property.Name.Contains("delegate", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("expressionTree", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("stateMachine", StringComparison.OrdinalIgnoreCase));
    }

    static ProcessAuthoringMetadata Metadata() => new(
        new("process/generated-query"),
        new("1"),
        ProcessRecoveryPolicy.ContinueAttempt,
        new(
            new("tests.process-computation", "1"),
            new("tests/ari-66/process-computation"),
            DocumentOrigin.User));

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
}

/// <summary>Representative generated Process used by canonical-equivalence tests.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedCustomerQueryProcess
{
    /// <summary>Exact Relation reference used by the generated Process.</summary>
    public static ExecutionDefinitionReference Relation { get; } = new(
        new("relation/customer-query"),
        new("1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('1', 64)));

    static async ProcessTask<string> Run(
        ProcessContext process,
        string input)
    {
        var queryInput = input;
        var row = await process.Query<string>(Relation, queryInput);
        return row;
    }
}

/// <summary>Semantically identical generated Process containing a non-effectful local.</summary>
[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedCustomerQueryProcessWithPureLocal
{
    static async ProcessTask<string> Run(
        ProcessContext process,
        string input)
    {
        var ignored = input + string.Empty;
        var queryInput = input;
        var row = await process.Query<string>(GeneratedCustomerQueryProcess.Relation, queryInput);
        return row;
    }
}
