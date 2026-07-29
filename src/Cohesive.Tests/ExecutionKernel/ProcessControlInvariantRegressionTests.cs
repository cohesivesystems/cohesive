using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessControlInvariantRegressionTests
{
    [Fact]
    public void ImmediateTerminate_RetainsInterruptedActivationForExactReplayAndConflictDetection()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var activation = new ProcessActivationStartObservation(
            fixture.Expectation(initial),
            new("activation/interrupted"),
            initial.UpdatedAtUtc.AddMinutes(1));
        var active = fixture.Executor.BeginActivation(initial, activation).State;

        var terminated = fixture.Executor.Apply(
            active,
            fixture.Terminate(active),
            active.UpdatedAtUtc.AddMinutes(1));
        var exactReplay = fixture.Executor.BeginActivation(terminated.State, activation);
        var conflictingReplay = fixture.Executor.BeginActivation(
            terminated.State,
            new(
                activation.Expectation,
                activation.ActivationId,
                activation.ObservedAtUtc.AddSeconds(1)));

        Assert.Equal(ProcessControlMode.Terminated, terminated.State.Mode);
        Assert.Equal(
            activation,
            Assert.IsType<ProcessAttemptClosure>(terminated.State.CurrentAttempt.Closure).InterruptedActivation);
        Assert.Equal(ProcessControlDecisionDisposition.Replayed, exactReplay.Disposition);
        Assert.Same(terminated.State, exactReplay.State);
        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, conflictingReplay.Disposition);
        Assert.Equal(
            ProcessControlDiagnosticCodes.ActivationConflict,
            Assert.Single(conflictingReplay.Diagnostics).Code);
        Assert.Same(terminated.State, conflictingReplay.State);
    }

    [Fact]
    public void AffinityBinding_ReplaysOnlyExactEvidenceAndFencesAnotherProcessInstance()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var expectation = fixture.Expectation(initial);
        var observedAtUtc = initial.UpdatedAtUtc.AddMinutes(1);
        var affinity = ProcessControlTestFixture.Affinity();
        var observation = new ProcessAttemptAffinityObservation(
            expectation,
            affinity,
            observedAtUtc);
        var bound = fixture.Executor.BindAttemptAffinity(initial, observation);

        var exactReplay = fixture.Executor.BindAttemptAffinity(bound.State, observation);
        var sameValueDifferentEvidence = fixture.Executor.BindAttemptAffinity(
            bound.State,
            new(expectation, affinity, observedAtUtc.AddSeconds(1)));
        var anotherInstance = fixture.Executor.BindAttemptAffinity(
            bound.State,
            new(
                new(
                    new(new("process/another-instance"), initial.CurrentAttempt.AttemptId),
                    expectation.Revision),
                affinity,
                observedAtUtc));

        Assert.Equal(ProcessControlDecisionDisposition.AffinityBound, bound.Disposition);
        Assert.Equal(ProcessControlDecisionDisposition.Replayed, exactReplay.Disposition);
        Assert.Same(bound.State, exactReplay.State);
        Assert.Equal(ProcessControlDecisionDisposition.AffinityConflict, sameValueDifferentEvidence.Disposition);
        Assert.Equal(
            ProcessControlDiagnosticCodes.AffinityConflict,
            Assert.Single(sameValueDifferentEvidence.Diagnostics).Code);
        Assert.Same(bound.State, sameValueDifferentEvidence.State);
        Assert.Equal(ProcessControlDecisionDisposition.TargetMismatch, anotherInstance.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.TargetMismatch, Assert.Single(anotherInstance.Diagnostics).Code);
        Assert.Same(bound.State, anotherInstance.State);
    }

    [Fact]
    public void ActivationLocalSignal_IsRejectedWithDurabilityDiagnostic()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var template = fixture.SignalCommand(state);
        var localSignal = WithDelivery(
            template.Signal,
            new(
                InteractionDurabilityDemand.ActivationLocal,
                InteractionVisibilityDemand.ActivationLocal));
        var command = WithSignal(template, localSignal);

        var rejected = fixture.Executor.Apply(
            state,
            command,
            state.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, rejected.Disposition);
        Assert.Equal(
            ProcessControlDiagnosticCodes.SignalDurabilityMismatch,
            Assert.Single(rejected.Diagnostics).Code);
        Assert.Same(state, rejected.State);
        Assert.Empty(rejected.State.Receipts);
    }

    [Fact]
    public void PersistedAcceptedActivationLocalSignal_IsRejectedAsMalformedState()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var accepted = fixture.Executor.Apply(
            state,
            fixture.SignalCommand(state),
            state.UpdatedAtUtc.AddMinutes(1)).State;
        var durableReceipt = Assert.Single(accepted.Receipts);
        var durableCommand = Assert.IsType<SignalProcessCommand>(durableReceipt.Command);
        var localCommand = WithSignal(
            durableCommand,
            WithDelivery(
                durableCommand.Signal,
                new(
                    InteractionDurabilityDemand.ActivationLocal,
                    InteractionVisibilityDemand.ActivationLocal)));
        var localReceipt = new ProcessControlCommandReceipt(
            localCommand,
            durableReceipt.Disposition,
            durableReceipt.RecordedAtUtc);

        var exception = Assert.Throws<ArgumentException>(() => new ProcessControlState(
            accepted.SchemaVersion,
            accepted.Definition,
            accepted.AuthorityScope,
            accepted.ProcessInstanceId,
            accepted.Revision,
            accepted.Mode,
            accepted.Attempts,
            accepted.PendingCommandId,
            [localReceipt],
            accepted.CreatedAtUtc,
            accepted.UpdatedAtUtc));

        Assert.Contains("durable delivery semantics", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamedStructuralSignalPayload_IsValidatedThroughTheCatalogShapeGraph()
    {
        TypeId payloadTypeId = new("execution/process-control-rich-signal");
        var graph = new ShapeGraph(
            new("execution/process-control-rich-signal-graph"),
            [],
            [
                new TypeDefinition.Structural(
                    payloadTypeId,
                    [
                        new(
                            new("status"),
                            new ScalarTypeRef(ScalarTypeKind.String))
                    ])
            ]);
        var payloadContract = new ValueContract(new NamedTypeRef(payloadTypeId));
        var document = InteractionContractDocuments.Create(
            new("interaction/signal/process-control-rich"),
            new("revision/1"),
            new SignalContractDefinition(new(payloadContract, new("signal-payload/v1"))),
            ProcessControlTestFixture.Provenance());
        var catalogValidation = InteractionContractCatalog.TryCreate([document], graph, out var catalog);
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var template = fixture.SignalCommand(state);
        var signal = new SignalEnvelope(
            template.Signal.SchemaVersion,
            template.Signal.Context,
            new(new(
                document.Metadata.DefinitionId,
                document.Metadata.RevisionId,
                document.Metadata.Fingerprint)),
            PortableValue.Concrete(
                payloadContract,
                ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                {
                    ["status"] = ObservationValue.FromString("ready")
                })),
            template.Signal.Target);
        var command = WithSignal(template, signal);

        Assert.True(catalogValidation.IsValid, ProcessControlTestFixture.FormatDiagnostics(catalogValidation));
        var executor = new ProcessControlReferenceExecutor(
            Assert.IsType<InteractionContractCatalog>(catalog));
        var accepted = executor.Apply(
            state,
            command,
            state.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.SignalAccepted, accepted.Disposition);
        Assert.Empty(accepted.Diagnostics);
        Assert.Equal(signal, Assert.Single(accepted.State.SignalAdmissions).Signal);
    }

    [Fact]
    public void OpaqueReasonAndAffinityValues_AreRejectedAtThePortableControlBoundary()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var opaque = PortableValue.Concrete(
            new(new OpaqueRuntimeTypeRef("Example.RuntimeHandle, Example.Runtime")),
            ObservationValue.FromString("runtime-only"));
        var cancelTemplate = fixture.Cancel(state);
        var cancel = new CancelProcessCommand(
            cancelTemplate.SchemaVersion,
            cancelTemplate.Context,
            Assert.IsType<ProcessControlExpectation>(cancelTemplate.Expectation),
            new("operator.cancel", opaque));
        var affinity = new ProcessAttemptAffinityObservation(
            fixture.Expectation(state),
            new(new("node/runtime-affinity"), opaque),
            state.UpdatedAtUtc.AddMinutes(1));

        var rejectedReason = fixture.Executor.Apply(
            state,
            cancel,
            state.UpdatedAtUtc.AddMinutes(1));
        var rejectedAffinity = fixture.Executor.BindAttemptAffinity(state, affinity);

        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, rejectedReason.Disposition);
        Assert.Contains(
            rejectedReason.Diagnostics,
            static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.OpaqueRuntimeType);
        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, rejectedAffinity.Disposition);
        Assert.Contains(
            rejectedAffinity.Diagnostics,
            static diagnostic => diagnostic.Code == PortableExecutionDiagnosticCodes.OpaqueRuntimeType);
        Assert.Same(state, rejectedReason.State);
        Assert.Same(state, rejectedAffinity.State);
    }

    static SignalProcessCommand WithSignal(
        SignalProcessCommand command,
        SignalEnvelope signal) =>
        new(
            command.SchemaVersion,
            command.Context,
            Assert.IsType<ProcessControlExpectation>(command.Expectation),
            signal);

    static SignalEnvelope WithDelivery(
        SignalEnvelope signal,
        InteractionDeliveryRequirements delivery)
    {
        var context = signal.Context;
        return new(
            signal.SchemaVersion,
            new(
                context.EmissionId,
                context.Origin,
                context.CorrelationId,
                context.CausationId,
                context.AuthorityScope,
                context.IdempotencyKey,
                context.Ordering,
                delivery,
                context.Provenance),
            signal.Contract,
            signal.Payload,
            signal.Target);
    }
}
