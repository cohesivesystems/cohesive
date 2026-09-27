using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessOperationReceiptEvidenceTests
{
    [Fact]
    public void OmittedReference_PreservesLegacyCanonicalResultEncoding()
    {
        var result = ProcessOperationResult.Completed(ProcessDurabilityTestFixture.StringValue("done"));
        var options = ProcessDurableCheckpointJsonSerializer.CreateOptions();
        var legacy = new { result.Value, result.Emissions, result.Failure, result.IsSuccessful };
        Assert.Equal(StrictDocumentJson.GetCanonicalBytes(legacy, options),
            StrictDocumentJson.GetCanonicalBytes(result, options));
        var restored = JsonSerializer.Deserialize<ProcessOperationResult>(JsonSerializer.Serialize(legacy, options), options)!;
        Assert.Null(restored.ReceiptReference);
        Assert.Equal(result.Value, restored.Value);
    }

    [Fact]
    public void RetainedOperationReceipt_TransportsReferenceAndReplaysWithoutCallingHost()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var operation = fixture.Operation;
        var key = new ProcessOperationOccurrence(operation.Continuation, operation.Activation,
            operation.Token, operation.Node, operation.Occurrence);
        var request = new EntityTransitionOperationRequest(key, operation.Context.AuthorityScope,
            operation.Definition, new(new("entity/customer"), new("customer/1")), operation.Input);
        var reference = EntityTransitionReceiptReferences.Project(request.Reference);
        var result = fixture.OperationResult.WithReceiptReference(reference);
        var converter = Cohesive.Adapters.DurableTask.DurableTaskProcessDataConverter.Create();
        var activityResult = Assert.IsType<ProcessOperationResult>(
            converter.Deserialize(converter.Serialize(result), typeof(ProcessOperationResult)));
        Assert.Equal(request.Reference, EntityTransitionReceiptReferences.Read(activityResult.ReceiptReference!));
        Assert.Throws<InvalidOperationException>(() => result.WithReceiptReference(ProcessDurabilityTestFixture.StringValue("different")));
        var receipt = new ProcessOperationReceipt(key, operation.Definition, result, operation.ObservedAtUtc);
        var options = ProcessDurableCheckpointJsonSerializer.CreateOptions();
        var retained = JsonSerializer.Deserialize<ProcessOperationReceipt>(JsonSerializer.Serialize(receipt, options), options)!;
        var host = new ProcessOperationReplayHost(new NoOperationsHost(), [retained]);
        var invocation = new ProcessTransitionInvocation(fixture.Plan.DefinitionReference, operation.Definition,
            ProcessDurabilityTestFixture.StringValue("customer/1"), operation.Input, operation.Continuation,
            operation.Activation, operation.Token, operation.Node, operation.Occurrence,
            operation.ObservedAtUtc, operation.Context);
        var replay = host.InvokeTransition(invocation);
        Assert.Equal(result.Value, replay.Value);
        Assert.Equal(request.Reference, EntityTransitionReceiptReferences.Read(replay.ReceiptReference!));
        Assert.Empty(host.Observations);
    }

    [Fact]
    public void Reference_RejectsUnresolvedValuesFailedOutcomesAndWrongLocatorContracts()
    {
        var success = ProcessOperationResult.Completed(ProcessDurabilityTestFixture.StringValue("done"));
        Assert.Throws<ArgumentException>(() => success.WithReceiptReference(
            PortableValue.Unknown(ProcessDurabilityTestFixture.StringContract)));
        var failed = ProcessOperationResult.Failed(new("test.failure", DiagnosticSeverity.Error, "failed"));
        Assert.Throws<ArgumentException>(() => failed.WithReceiptReference(ProcessDurabilityTestFixture.StringValue("receipt")));
        Assert.Throws<ArgumentException>(() => EntityTransitionReceiptReferences.Read(ProcessDurabilityTestFixture.StringValue("receipt")));
    }

    [Fact]
    public void Checkpoint_RejectsReceiptReferenceThatDoesNotMatchActivationEvidence()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var original = Assert.Single(fixture.Checkpoint.Operations);
        var altered = new ProcessOperationReceipt(original.Key, original.OperationDefinition,
            original.Result.WithReceiptReference(ProcessDurabilityTestFixture.StringValue("unmatched/receipt")),
            original.RecordedAtUtc);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(fixture.Checkpoint, operations: [altered]);
        var validation = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, checkpoint);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, item => item.Code == ProcessCheckpointDiagnosticCodes.OperationReceiptIncompatible);
    }

    [Fact]
    public void TraceProjection_RejectsReceiptReferenceOnNonOperationEvent()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var activation = Assert.Single(fixture.Checkpoint.Activations);
        var index = Array.FindIndex(activation.Evidence.Trace.ToArray(), item => item.Kind != ProcessTraceEventKind.OperationCompleted);
        var trace = activation.Evidence.Trace.SetItem(index, activation.Evidence.Trace[index] with
            { ReceiptReference = ProcessDurabilityTestFixture.StringValue("unattributed/receipt") });
        var projected = ProcessExecutionTraceProjector.ProjectCommitted(activation.Evidence with { Trace = trace },
            activation.Disposition, activation.Sequence, fixture.Checkpoint.Definition, activation.Continuation,
            fixture.Checkpoint.Emissions.Select(item => item.Envelope));
        Assert.False(projected.IsSuccessful);
        Assert.Contains(projected.Validation.Diagnostics, item => item.Code == ExecutionTraceDiagnosticCodes.EventInvalid
            && item.Location!.EndsWith("/receiptReference", StringComparison.Ordinal));
    }

    sealed class NoOperationsHost : IProcessReferenceHost
    {
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => throw new InvalidOperationException("Replay must not invoke the host.");
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) => throw new InvalidOperationException("Replay must not evaluate a relation.");
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new InvalidOperationException("Replay must not resolve a signal.");
    }
}
