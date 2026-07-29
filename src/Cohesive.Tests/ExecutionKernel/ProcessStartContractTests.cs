using System.Text.Json;
using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessStartContractTests
{
    [Fact]
    public void Start_IsDistinctFromPostAdmissionControlAndCreatesExactInitialState()
    {
        var request = Request();
        var evaluator = new ProcessStartReferenceEvaluator();

        var decision = evaluator.Evaluate(
            request,
            ProcessStartRegistryEvidence.Empty,
            request.Context.IssuedAtUtc.AddMinutes(1));

        Assert.False(typeof(ProcessControlCommand).IsAssignableFrom(typeof(ProcessStartRequest)));
        Assert.Equal(ProcessStartDisposition.Accepted, decision.Result.Disposition);
        Assert.True(decision.RequiresPersistence);
        Assert.NotNull(decision.Receipt);
        var state = Assert.IsType<ProcessControlState>(decision.State);
        Assert.Equal(request.Definition, state.Definition);
        Assert.Equal(request.InitialContinuation.ProcessInstanceId, state.ProcessInstanceId);
        Assert.Equal(request.InitialContinuation.ProcessAttemptId, state.CurrentAttempt.AttemptId);
        Assert.Equal(ProcessControlRevision.Initial, state.Revision);
        Assert.Equal(ProcessControlMode.Running, state.Mode);
    }

    [Fact]
    public void SafeResult_DoesNotEchoTypedInputOrAuthorizationEvidence()
    {
        var request = Request(input: "highly-sensitive-start-input");
        var decision = new ProcessStartReferenceEvaluator().Evaluate(
            request,
            ProcessStartRegistryEvidence.Empty,
            request.Context.IssuedAtUtc.AddMinutes(1));
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();

        var json = JsonSerializer.Serialize(decision.Result, options);

        Assert.DoesNotContain("highly-sensitive-start-input", json, StringComparison.Ordinal);
        Assert.DoesNotContain("policy/start-tests/allow", json, StringComparison.Ordinal);
        Assert.DoesNotContain("receipt", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(request.InitialContinuation.ProcessInstanceId.Value, json, StringComparison.Ordinal);
        Assert.Equal(
            decision.Result,
            JsonSerializer.Deserialize<Cohesive.Execution.ProcessStartResult>(json, options));
    }

    [Fact]
    public void ExactCommandAndEquivalentIdempotentRetry_ReplayTheWinningAdmission()
    {
        var evaluator = new ProcessStartReferenceEvaluator();
        var request = Request();
        var accepted = evaluator.Evaluate(
            request,
            ProcessStartRegistryEvidence.Empty,
            request.Context.IssuedAtUtc.AddMinutes(1));
        var receipt = Assert.IsType<ProcessStartReceipt>(accepted.Receipt);
        var state = Assert.IsType<ProcessControlState>(accepted.State);

        var exact = evaluator.Evaluate(
            request,
            new(
                sameCommandIdentity: receipt,
                sameIdempotencyKey: receipt,
                existingInstanceReceipt: receipt,
                existingInstanceState: state),
            state.UpdatedAtUtc.AddMinutes(2));

        var retry = Request(
            commandId: "start-command/retry",
            idempotencyKey: request.Context.IdempotencyKey.Value,
            issuedAtUtc: request.Context.IssuedAtUtc.AddMinutes(2));
        var byIdempotency = evaluator.Evaluate(
            retry,
            new(
                sameIdempotencyKey: receipt,
                existingInstanceReceipt: receipt,
                existingInstanceState: state),
            retry.Context.IssuedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessStartDisposition.Replayed, exact.Result.Disposition);
        Assert.Equal(ProcessStartDisposition.Replayed, byIdempotency.Result.Disposition);
        Assert.False(exact.RequiresPersistence);
        Assert.Same(state, exact.State);
        Assert.Same(state, byIdempotency.State);
        Assert.Equal(accepted.Result.Admission, byIdempotency.Result.Admission);
    }

    [Fact]
    public void IdentityIdempotencyAndInstanceReuse_HavePreciseConflictSemantics()
    {
        var evaluator = new ProcessStartReferenceEvaluator();
        var request = Request();
        var accepted = evaluator.Evaluate(
            request,
            ProcessStartRegistryEvidence.Empty,
            request.Context.IssuedAtUtc.AddMinutes(1));
        var receipt = Assert.IsType<ProcessStartReceipt>(accepted.Receipt);
        var state = Assert.IsType<ProcessControlState>(accepted.State);

        var identity = evaluator.Evaluate(
            Request(commandId: request.Context.CommandId.Value, input: "different"),
            new(sameCommandIdentity: receipt),
            request.Context.IssuedAtUtc.AddMinutes(2));
        var idempotency = evaluator.Evaluate(
            Request(
                commandId: "start-command/other",
                idempotencyKey: request.Context.IdempotencyKey.Value,
                input: "different"),
            new(sameIdempotencyKey: receipt),
            request.Context.IssuedAtUtc.AddMinutes(2));
        var instance = evaluator.Evaluate(
            Request(commandId: "start-command/new", idempotencyKey: "start-idempotency/new"),
            new(existingInstanceReceipt: receipt, existingInstanceState: state),
            request.Context.IssuedAtUtc.AddMinutes(2));

        Assert.Equal(ProcessStartDisposition.CommandIdentityConflict, identity.Result.Disposition);
        Assert.Equal(ProcessStartDiagnosticCodes.CommandIdentityConflict, identity.Result.DiagnosticCode);
        Assert.Equal(ProcessStartDisposition.IdempotencyConflict, idempotency.Result.Disposition);
        Assert.Equal(ProcessStartDiagnosticCodes.CommandIdempotencyConflict, idempotency.Result.DiagnosticCode);
        Assert.Equal(ProcessStartDisposition.InstanceConflict, instance.Result.Disposition);
        Assert.Equal(ProcessStartDiagnosticCodes.InstanceConflict, instance.Result.DiagnosticCode);
        Assert.All([identity, idempotency, instance], static decision =>
        {
            Assert.True(decision.Result.IsConflict);
            Assert.Null(decision.Receipt);
            Assert.Null(decision.State);
        });
    }

    [Fact]
    public void StartInput_MustBeTypedAndMaterialized()
    {
        Assert.Throws<ArgumentException>(() => Request(
            portableInput: PortableValue.Unknown(ProcessControlTestFixture.StringContract)));
        Assert.Throws<ArgumentException>(() => Request(
            portableInput: PortableValue.Failed(
                ProcessControlTestFixture.StringContract,
                new(
                    "start.input.failed",
                    DiagnosticSeverity.Error,
                    "Input acquisition failed.",
                    "/input"))));
    }

    [Fact]
    public void RegistryEvidence_CannotCrossAuthorityOrTenantScope()
    {
        var evaluator = new ProcessStartReferenceEvaluator();
        var original = Request();
        var accepted = evaluator.Evaluate(
            original,
            ProcessStartRegistryEvidence.Empty,
            original.Context.IssuedAtUtc.AddMinutes(1));
        var receipt = Assert.IsType<ProcessStartReceipt>(accepted.Receipt);
        var crossScope = Request(
            authorityScope: new("authority/motion", "tenant/other"));

        Assert.Throws<ArgumentException>(() => evaluator.Evaluate(
            crossScope,
            new(sameCommandIdentity: receipt),
            crossScope.Context.IssuedAtUtc.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => evaluator.Evaluate(
            crossScope,
            new(
                existingInstanceReceipt: receipt,
                existingInstanceState: accepted.State),
            crossScope.Context.IssuedAtUtc.AddMinutes(1)));
    }

    static ProcessStartRequest Request(
        string commandId = "start-command/1",
        string idempotencyKey = "start-idempotency/1",
        string input = "start-value",
        DateTimeOffset? issuedAtUtc = null,
        PortableValue? portableInput = null,
        InteractionAuthorityScope? authorityScope = null)
    {
        ProcessInstanceId instance = new("process/start-1");
        return new(
            ProcessStartRequest.CurrentSchemaVersion,
            ProcessControlTestFixture.DefinitionReference("process/start-test", 'b'),
            new(
                new(commandId),
                new(idempotencyKey),
                instance,
                new(
                    "operator/alice",
                    authorityScope ?? ProcessControlTestFixture.Authority,
                    "policy/start-tests/allow"),
                issuedAtUtc ?? ProcessControlTestFixture.CreatedAtUtc,
                ProcessControlTestFixture.Provenance()),
            new(instance, new("process-attempt/initial")),
            portableInput ?? ProcessControlTestFixture.StringValue(input));
    }
}
