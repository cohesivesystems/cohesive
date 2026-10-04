using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessReceiptAuthoringTests
{
    [Theory]
    [InlineData("present")]
    [InlineData(null)]
    public async Task GeneratedRequireValueEnforcesRuntimePayloadPresence(string? value)
    {
        var process = GeneratedRequiredValueProcess.Define(new(new("test/required-value"), new("1"),
            ProcessRecoveryPolicy.ContinueAttempt, new(new("test/receipt", "1"), new("test/receipt"), DocumentOrigin.Generated)));
        var compiled = process.Compile(new ProcessDefinitionValidationContext([]));
        Assert.True(compiled.IsSuccessful, string.Join("; ", compiled.Validation.Diagnostics));
        var host = new ReceiptHost(true, true, new ValueContract(new ScalarTypeRef(ScalarTypeKind.String)));
        var result = await new EphemeralProcessExecutor(compiled.Plan!).ExecuteAsync(OperationContext.Create(),
            new(new("instance/required"), new("attempt/required")),
            PortableValue.Concrete(process.Definition.Input, ObservationValue.FromObject(new RequiredValueInput(value))),
            new(new("test", "tenant"), new("correlation/required"),
                new(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal), process.Document.Metadata.Provenance),
            host, TimeSpan.FromSeconds(5));
        Assert.Equal(value is null ? ProcessActivationDisposition.Failed : ProcessActivationDisposition.Completed,
            result.Decision.Disposition);
        Assert.Equal(0, host.Calls);
        if (value is not null)
            Assert.Equal(ObservationValue.FromString(value), result.Decision.State.Terminal.Detail?.Value?.Value);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GeneratedReceiptFlowRetainsDomainOutcomeAndRequiresHostReceipt(bool accepted, bool supplyReceipt)
    {
        var process = GeneratedReceiptProcess.Define(new(new("test/receipt-execution"), new("1"),
            ProcessRecoveryPolicy.ContinueAttempt, new(new("test/receipt", "1"), new("test/receipt"), DocumentOrigin.Generated)));
        var text = process.Definition.Input;
        var compiled = process.Compile(new ProcessDefinitionValidationContext([
            new(GeneratedReceiptProcess.Transition, ProcessDefinitionLinkKind.Transition,
                text, new(new ScalarTypeRef(ScalarTypeKind.Bool)), receiptContract: text)]));
        Assert.True(compiled.IsSuccessful, string.Join("; ", compiled.Validation.Diagnostics));
        var host = new ReceiptHost(accepted, supplyReceipt, text);
        var result = await new EphemeralProcessExecutor(compiled.Plan!).ExecuteAsync(OperationContext.Create(),
            new(new("instance/receipt"), new("attempt/receipt")), PortableValue.Concrete(text, ObservationValue.FromString("input")),
            new(new("test", "tenant"), new("correlation/receipt"),
                new(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal), process.Document.Metadata.Provenance),
            host, TimeSpan.FromSeconds(5));
        Assert.Equal(1, host.Calls);
        if (!supplyReceipt)
        {
            Assert.Equal(ProcessActivationDisposition.Failed, result.Decision.Disposition);
            Assert.Contains(result.Decision.Diagnostics, diagnostic => diagnostic.Code == ProcessExecutionDiagnosticCodes.ResultContractViolated);
            return;
        }
        Assert.Equal(ProcessActivationDisposition.Completed, result.Decision.Disposition);
        Assert.Equal(ObservationValue.FromString(accepted ? "receipt/1" : "rejected"), result.Decision.State.Terminal.Detail?.Value?.Value);
        Assert.NotNull(Assert.Single(result.Evidence.CompletedOperations).Value.ReceiptReference);
    }

    sealed class ReceiptHost(bool accepted, bool supplyReceipt, ValueContract text) : IAsyncProcessReferenceHost
    {
        public int Calls { get; private set; }
        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation)
        {
            Calls++;
            var result = ProcessOperationResult.Completed(PortableValue.Concrete(
                new(new ScalarTypeRef(ScalarTypeKind.Bool)), ObservationValue.FromBool(accepted)));
            if (supplyReceipt)
                result = result.WithReceiptReference(PortableValue.Concrete(text, ObservationValue.FromString("receipt/1")));
            return ValueTask.FromResult(result);
        }
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation) => throw new InvalidOperationException();
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) => throw new InvalidOperationException();
    }

    [Fact]
    public void ReceiptAlongsideAcquisitionAndEnrichmentGeneratesAllOutputs()
    {
        var process = GeneratedReceiptWorkflow.Define(new(new("test/receipt-workflow"), new("1"),
            ProcessRecoveryPolicy.ContinueAttempt, new(new("test/receipt", "1"), new("test/receipt"), DocumentOrigin.Generated)));
        Assert.True(process.IsValid, string.Join("; ", process.Validation.Diagnostics));
        Assert.Equal(2, process.Definition.Nodes.OfType<EvaluateRelationProcessNode>().Count());
        Assert.NotNull(Assert.Single(process.Definition.Nodes.OfType<InvokeTransitionProcessNode>()).Receipt);
    }

    [Fact]
    public void GeneratedReceiptProjectionMatchesCanonicalSeparateBindings()
    {
        var metadata = new ProcessAuthoringMetadata(new("test/receipt-authoring"), new("1"),
            ProcessRecoveryPolicy.ContinueAttempt, new(new("test/receipt", "1"), new("test/receipt"), DocumentOrigin.Generated));
        var generated = GeneratedReceiptProcess.Define(metadata);
        Assert.True(generated.IsValid, string.Join("; ", generated.Validation.Diagnostics));
        var returned = ProcessAuthoringIdentities.NodeFor(new(["body", "return-0"]));
        var native = ProcessAuthoring.Create<string, string>(metadata.WithEntry(new("commit")), builder =>
        {
            var outcome = builder.Output<bool>(owner: new("commit"), role: "result");
            var receipt = builder.Output<string>(owner: new("commit"), role: "receipt");
            builder.InvokeTransitionWithReceipt(new("commit"), GeneratedReceiptProcess.Transition,
                builder.Input.Value, builder.Input.Value,
                builder.Continuation(builder.Edge(new("commit"), "next", returned), outcome), receipt);
            builder.Return(returned, builder.CanonicalValue<string>(new ConditionalExpr(outcome.Expression, receipt.Expression,
                Expr.Const("rejected"), receipt.Contract.Type), receipt.Contract));
        });
        Assert.Equal(native.Document.Metadata.Fingerprint, generated.Document.Metadata.Fingerprint);
        Assert.Equal(ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(native.Document),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(generated.Document));
        var node = Assert.Single(generated.Definition.Nodes.OfType<InvokeTransitionProcessNode>());
        Assert.NotNull(node.Receipt);
        var unattested = generated.Compile(new ProcessDefinitionValidationContext([
            new(GeneratedReceiptProcess.Transition, ProcessDefinitionLinkKind.Transition,
                generated.Definition.Input, new(new ScalarTypeRef(ScalarTypeKind.Bool)))]));
        Assert.False(unattested.IsSuccessful);
        Assert.Contains(unattested.Validation.Diagnostics, d => d.Code == ProcessDefinitionDiagnosticCodes.OutputContractMismatch);
    }
}

[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedReceiptProcess
{
    public static ExecutionDefinitionReference Transition { get; } = new(new("test/transition"), new("1"),
        new(ExecutionDefinitionFingerprinter.Algorithm, ExecutionDefinitionFingerprinter.Canonicalization, new string('1', 64)));

    static async ProcessTask<string> Run(ProcessContext process, string input)
    {
        var committed = await process.TransitionWithReceipt<bool, string>(Transition, input, input, id: new("commit"));
        return committed.Outcome ? committed.Receipt : "rejected";
    }
}

[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedReceiptWorkflow
{
    static async ProcessTask<string> Run(ProcessContext process, string input)
    {
        var acquired = await process.Query<string>(GeneratedReceiptProcess.Transition, input);
        var committed = await process.TransitionWithReceipt<bool, string>(GeneratedReceiptProcess.Transition, input, acquired);
        if (!committed.Outcome)
            return process.Constant("rejected");
        var enriched = await process.Query<string>(GeneratedReceiptProcess.Transition, committed.Receipt);
        return enriched;
    }
}

public sealed record RequiredValueInput(string? Value);

[GenerateProcessDefinition(nameof(Run))]
public static partial class GeneratedRequiredValueProcess
{
    static async ProcessTask<string> Run(ProcessContext process, RequiredValueInput input)
    {
        return process.RequireValue(input.Value);
    }
}
