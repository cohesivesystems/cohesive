using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessOperationReplayHostTests
{
    [Fact]
    public void CommittedReceipt_ReplaysWithoutInvokingTheInnerHost()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/operation-replay/committed",
            semanticVariant: "committed");
        var inner = new RecordingHost(fixture.OperationResult);
        var host = new ProcessOperationReplayHost(inner, fixture.Checkpoint.Operations);

        var result = host.EvaluateRelation(fixture.Operation);

        Assert.Equal(fixture.OperationResult, result);
        Assert.Equal(0, inner.RelationCalls);
        Assert.Empty(host.Observations);
    }

    [Fact]
    public void FirstTransitionObservation_IsCapturedOnceAndReplayedWithinTheActivation()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/operation-replay/capture",
            semanticVariant: "capture");
        var operation = fixture.Operation;
        var invocation = new ProcessTransitionInvocation(
            operation.Definition,
            ProcessDurabilityTestFixture.StringValue("subject/capture"),
            operation.Input,
            operation.Continuation,
            operation.Activation,
            operation.Token,
            operation.Node,
            operation.Occurrence,
            operation.ObservedAtUtc,
            operation.Context);
        var inner = new RecordingHost(fixture.OperationResult);
        var host = new ProcessOperationReplayHost(inner);

        var first = host.InvokeTransition(invocation);
        var replay = host.InvokeTransition(invocation);

        Assert.Equal(fixture.OperationResult, first);
        Assert.Same(first, replay);
        Assert.Equal(1, inner.TransitionCalls);
        var observation = Assert.Single(host.Observations);
        Assert.Equal(operation.Definition, observation.OperationDefinition);
        Assert.Equal(operation.Continuation, observation.Key.Continuation);
        Assert.Equal(operation.Activation, observation.Key.Activation);
        Assert.Equal(operation.Token, observation.Key.Token);
        Assert.Equal(operation.Node, observation.Key.Node);
        Assert.Equal(operation.Occurrence, observation.Key.Occurrence);
        Assert.Same(first, observation.Result);
    }

    [Fact]
    public void ReusedOccurrenceWithAnotherDefinition_IsRejectedBeforeHostExecution()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/operation-replay/conflict",
            semanticVariant: "conflict");
        var operation = fixture.Operation;
        var retained = Assert.Single(fixture.Checkpoint.Operations);
        var conflicting = new ProcessOperationReceipt(
            retained.Key,
            ProcessDurabilityTestFixture.DefinitionReference("relation/conflicting", '9'),
            retained.Result,
            retained.RecordedAtUtc);
        var inner = new RecordingHost(fixture.OperationResult);
        var host = new ProcessOperationReplayHost(inner, [conflicting]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            host.EvaluateRelation(operation));

        Assert.Contains("another definition", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.RelationCalls);
        Assert.Empty(host.Observations);
    }

    [Fact]
    public void StructuredHostFailure_ProducesReciprocalTraceAndCapturableReceiptEvidence()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/operation-replay/failure",
            semanticVariant: "failure");
        var initial = ProcessReferenceInterpreter.Create(fixture.Plan, fixture.Start);
        var failure = new DocumentValidationDiagnostic(
            ProcessExecutionDiagnosticCodes.OperationFailed,
            DiagnosticSeverity.Error,
            "Synthetic host failure.",
            "/operation");
        var failed = ProcessOperationResult.Failed(failure);
        var host = new ProcessOperationReplayHost(new RecordingHost(failed));

        var decision = ProcessReferenceInterpreter.Activate(
            fixture.Plan,
            initial,
            fixture.Activation,
            host);

        Assert.Equal(ProcessActivationDisposition.Failed, decision.Disposition);
        var observation = Assert.Single(host.Observations);
        Assert.Same(failed, observation.Result);
        var trace = Assert.Single(
            decision.Evidence.Trace,
            static candidate => candidate.Kind == ProcessTraceEventKind.OperationCompleted);
        Assert.Equal("failed", trace.Detail);
        Assert.Equal(observation.Key.Continuation, trace.Continuation);
        Assert.Equal(observation.Key.Activation, trace.Activation);
        Assert.Equal(observation.Key.Token, trace.Token);
        Assert.Equal(observation.Key.Node, trace.Node);
        Assert.Equal(observation.Key.Occurrence, trace.OperationOccurrence);
        Assert.Contains(
            decision.Evidence.Trace,
            candidate => candidate.Kind == ProcessTraceEventKind.TerminalReached
                         && candidate.Sequence > trace.Sequence);

        var materialized = new ProcessOperationReceipt(
            observation.Key,
            observation.OperationDefinition,
            observation.Result,
            ProcessDurabilityTestFixture.CheckpointedAtUtc);
        Assert.Equal(failed, materialized.Result);
    }

    sealed class RecordingHost(ProcessOperationResult result) : IProcessReferenceHost
    {
        internal int TransitionCalls { get; private set; }

        internal int RelationCalls { get; private set; }

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation)
        {
            TransitionCalls++;
            return result;
        }

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation)
        {
            RelationCalls++;
            return result;
        }

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }
}
