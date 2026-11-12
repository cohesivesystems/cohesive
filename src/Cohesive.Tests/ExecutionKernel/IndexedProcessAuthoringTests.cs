using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class IndexedProcessAuthoringTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task Indexed_projection_preserves_serialized_names_and_rejects_invalid_indices(int index, bool valid)
    {
        var process = IndexedProjectionProcess.Define(new(new("test/indexed"), new("1"),
            ProcessRecoveryPolicy.ContinueAttempt, new(new("test/indexed", "1"), new("test/indexed"), DocumentOrigin.Generated)));
        var compiled = process.Compile(new ProcessDefinitionValidationContext([]));
        Assert.True(compiled.IsSuccessful, string.Join("; ", compiled.Validation.Diagnostics));
        var result = await new EphemeralProcessExecutor(compiled.Plan!).ExecuteAsync(OperationContext.Create(),
            new(new("indexed"), new("1")),
            PortableValue.Concrete(process.Definition.Input, ObservationValue.FromObject(new IndexedProjectionInput(
                [new("first"), new("second")], index))),
            new(new("test", "tenant"), new("indexed"),
                new(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal), process.Document.Metadata.Provenance),
            new NoOperations(), TimeSpan.FromSeconds(5));
        Assert.Equal(valid ? ProcessActivationDisposition.Completed : ProcessActivationDisposition.Failed, result.Decision.Disposition);
        if (valid) Assert.Equal(ObservationValue.FromString(index == 0 ? "first" : "second"), result.Decision.State.Terminal.Detail?.Value?.Value);
    }

    sealed class NoOperations : IAsyncProcessReferenceHost
    {
        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation) => throw new NotSupportedException();
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation) => throw new NotSupportedException();
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) => throw new NotSupportedException();
    }
}

public sealed record IndexedProjectionItem([property: JsonPropertyName("item.name")] string Name);
public sealed record IndexedProjectionInput(IndexedProjectionItem[] Items, int Index);

[GenerateProcessDefinition(nameof(Run))]
public static partial class IndexedProjectionProcess
{
    static async ProcessTask<string> Run(ProcessContext process, IndexedProjectionInput input)
    {
        var selected = input.Items[input.Index];
        return selected.Name;
    }
}
