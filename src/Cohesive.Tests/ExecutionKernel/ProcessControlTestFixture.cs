using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

internal sealed class ProcessControlTestFixture
{
    internal static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    internal static readonly InteractionAuthorityScope Authority =
        new("authority/motion", "tenant/acme");

    internal static readonly ValueContract StringContract =
        new(new ScalarTypeRef(ScalarTypeKind.String));

    ProcessControlTestFixture(
        InteractionContractCatalog catalog,
        SignalContractReference signalContract)
    {
        Catalog = catalog;
        SignalContract = signalContract;
        Executor = new(catalog);
    }

    internal InteractionContractCatalog Catalog { get; }

    internal SignalContractReference SignalContract { get; }

    internal ProcessControlReferenceExecutor Executor { get; }

    internal static ProcessControlTestFixture Create()
    {
        var signalDocument = InteractionContractDocuments.Create(
            new("interaction/signal/control-test"),
            new("revision/1"),
            new SignalContractDefinition(new(StringContract, new("signal-payload/v1"))),
            Provenance());
        var validation = InteractionContractCatalog.TryCreate([signalDocument], out var catalog);
        Assert.True(validation.IsValid, FormatDiagnostics(validation));

        return new(
            Assert.IsType<InteractionContractCatalog>(catalog),
            new(Reference(signalDocument)));
    }

    internal ProcessControlState State(string attemptId = "process-attempt/1") =>
        ProcessControlState.Create(
            DefinitionReference("process/dq-onboarding", 'a'),
            Authority,
            new("process/onboarding-1"),
            new(attemptId),
            CreatedAtUtc);

    internal ProcessControlExpectation Expectation(
        ProcessControlState state,
        ProcessAttemptId? attemptId = null,
        ProcessControlRevision? revision = null) =>
        new(
            new(state.ProcessInstanceId, attemptId ?? state.CurrentAttempt.AttemptId),
            revision ?? state.Revision);

    internal InspectProcessCommand Inspect(
        ProcessControlState state,
        string id = "inspect/1",
        DateTimeOffset? issuedAtUtc = null,
        ProcessControlExpectation? expectation = null) =>
        new(
            ProcessControlCommand.CurrentSchemaVersion,
            Context(state, id, issuedAtUtc ?? state.UpdatedAtUtc),
            expectation);

    internal PauseProcessCommand Pause(
        ProcessControlState state,
        string id = "pause/1",
        string? idempotencyKey = null,
        DateTimeOffset? issuedAtUtc = null,
        ProcessControlExpectation? expectation = null) =>
        new(
            ProcessControlCommand.CurrentSchemaVersion,
            Context(state, id, issuedAtUtc ?? state.UpdatedAtUtc, idempotencyKey),
            expectation ?? Expectation(state));

    internal ContinueProcessCommand Continue(
        ProcessControlState state,
        string id = "continue/1",
        DateTimeOffset? issuedAtUtc = null,
        ProcessControlExpectation? expectation = null) =>
        new(
            ProcessControlCommand.CurrentSchemaVersion,
            Context(state, id, issuedAtUtc ?? state.UpdatedAtUtc),
            expectation ?? Expectation(state));

    internal RestartProcessAttemptCommand Restart(
        ProcessControlState state,
        string newAttemptId = "process-attempt/2",
        string id = "restart/1",
        DateTimeOffset? issuedAtUtc = null,
        ProcessAttemptCleanupRequirement cleanup =
            ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources,
        ProcessControlExpectation? expectation = null) =>
        new(
            ProcessControlCommand.CurrentSchemaVersion,
            Context(state, id, issuedAtUtc ?? state.UpdatedAtUtc),
            expectation ?? Expectation(state),
            new(
                new(newAttemptId),
                cleanup,
                new("operator.restart")));

    internal CancelProcessCommand Cancel(
        ProcessControlState state,
        string id = "cancel/1",
        DateTimeOffset? issuedAtUtc = null,
        ProcessControlExpectation? expectation = null) =>
        new(
            ProcessControlCommand.CurrentSchemaVersion,
            Context(state, id, issuedAtUtc ?? state.UpdatedAtUtc),
            expectation ?? Expectation(state),
            new("operator.cancel"));

    internal TerminateProcessCommand Terminate(
        ProcessControlState state,
        string id = "terminate/1",
        DateTimeOffset? issuedAtUtc = null,
        ProcessAttemptCleanupRequirement cleanup =
            ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources,
        ProcessControlExpectation? expectation = null) =>
        new(
            ProcessControlCommand.CurrentSchemaVersion,
            Context(state, id, issuedAtUtc ?? state.UpdatedAtUtc),
            expectation ?? Expectation(state),
            new("operator.terminate"),
            cleanup);

    internal SignalProcessCommand SignalCommand(
        ProcessControlState state,
        string id = "signal-command/1",
        string emissionId = "emission/signal/1",
        string? signalIdempotencyKey = null,
        string payload = "ready",
        ProcessAttemptId? targetAttemptId = null,
        DateTimeOffset? issuedAtUtc = null,
        ProcessControlExpectation? expectation = null) =>
        new(
            ProcessControlCommand.CurrentSchemaVersion,
            Context(state, id, issuedAtUtc ?? state.UpdatedAtUtc),
            expectation ?? Expectation(state),
            Signal(
                state,
                emissionId,
                signalIdempotencyKey ?? $"idempotency/{emissionId}",
                payload,
                targetAttemptId));

    internal SignalEnvelope Signal(
        ProcessControlState state,
        string emissionId,
        string idempotencyKey,
        string payload,
        ProcessAttemptId? targetAttemptId = null)
    {
        var continuation = new ProcessContinuationIdentity(
            state.ProcessInstanceId,
            targetAttemptId ?? state.CurrentAttempt.AttemptId);
        return new(
            InteractionEnvelope.CurrentSchemaVersion,
            new(
                new(emissionId),
                new ProcessInteractionOrigin(
                    state.Definition,
                    new("node/emit-signal"),
                    continuation,
                    new("activation/signal-source"),
                    new("token/signal-source")),
                new("correlation/control-test"),
                causationId: null,
                state.AuthorityScope,
                new(idempotencyKey),
                ordering: null,
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                Provenance()),
            SignalContract,
            StringValue(payload),
            new ProcessTokenInteractionTarget(continuation, new("token/control-input")));
    }

    internal ProcessControlDecision BeginActivation(
        ProcessControlState state,
        string activationId = "activation/1",
        DateTimeOffset? observedAtUtc = null) =>
        Executor.BeginActivation(
            state,
            new(
                Expectation(state),
                new(activationId),
                observedAtUtc ?? state.UpdatedAtUtc.AddMinutes(1)));

    internal ProcessControlDecision ReachSafePoint(
        ProcessControlState state,
        string safePointId = "safe-point/1",
        string node = "node/checkpoint",
        DateTimeOffset? observedAtUtc = null) =>
        Executor.ReachSafePoint(
            state,
            new(
                new(safePointId),
                Expectation(state),
                state.CurrentAttempt.ActiveActivationId
                    ?? throw new InvalidOperationException("The fixture requires an activation in flight."),
                new(node),
                observedAtUtc ?? state.UpdatedAtUtc.AddMinutes(1)));

    internal ProcessControlDecision BindAffinity(
        ProcessControlState state,
        string slot = "node/index-generation",
        string value = "generation/1",
        DateTimeOffset? observedAtUtc = null,
        ProcessControlExpectation? expectation = null) =>
        Executor.BindAttemptAffinity(
            state,
            new(
                expectation ?? Expectation(state),
                Affinity(slot, value),
                observedAtUtc ?? state.UpdatedAtUtc.AddMinutes(1)));

    internal static ProcessAttemptAffinity Affinity(
        string slot = "node/index-generation",
        string value = "generation/1") =>
        new(new(slot), StringValue(value));

    internal static PortableValue StringValue(string value) =>
        PortableValue.Concrete(StringContract, ObservationValue.FromString(value));

    internal static ExecutionDefinitionReference DefinitionReference(string id, char fingerprintDigit) =>
        new(
            new(id),
            new("revision/1"),
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                new string(fingerprintDigit, 64)));

    internal static ExecutionProvenance Provenance() =>
        new(
            new("process-control-tests", "1"),
            new("tests/execution-kernel/process-control"),
            DocumentOrigin.Generated);

    internal static string FormatDiagnostics(DocumentValidationResult validation) =>
        string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) =>
        new(
            document.Metadata.DefinitionId,
            document.Metadata.RevisionId,
            document.Metadata.Fingerprint);

    static ProcessControlCommandContext Context(
        ProcessControlState state,
        string id,
        DateTimeOffset issuedAtUtc,
        string? idempotencyKey = null) =>
        new(
            new(id),
            new(idempotencyKey ?? $"idempotency/{id}"),
            state.ProcessInstanceId,
            new("operator/alice", state.AuthorityScope, "policy/control-test/allow"),
            issuedAtUtc,
            Provenance());
}
