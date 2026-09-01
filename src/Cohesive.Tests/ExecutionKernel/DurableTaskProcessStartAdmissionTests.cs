using Cohesive.Adapters.DurableTask;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DurableTaskProcessStartAdmissionTests
{
    [Fact]
    public void Admission_RequiresCanonicalStartGrant()
    {
        var admitted = Admission();
        var invocation = new ExecutionApiInvocationContext(
            admitted.Invocation.Authorization,
            admitted.Invocation.Provenance,
            admitted.Invocation.IssuedAtUtc,
            admitted.Invocation.ObservedAtUtc,
            []);

        var exception = Assert.Throws<UnauthorizedAccessException>(() =>
            new DurableTaskProcessStartAdmission(
                admitted.Request,
                admitted.ActivationContext,
                invocation));

        Assert.Contains(
            ExecutionControlApiWireNames.AuthorizationRequirement(ProcessStartWireNames.Start),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptedStart_SeparatesTrustedCommandEvidenceFromCanonicalActivationProvenance()
    {
        var admission = Admission();
        var activationProvenance = ActivationProvenance();

        var evaluated = DurableTaskProcessStartAdmissionEvaluator.Evaluate(
            admission,
            activationProvenance,
            sameCommand: null,
            sameIdempotency: null,
            existingInstance: null);

        Assert.Equal(ProcessStartDisposition.Accepted, evaluated.Decision.Result.Disposition);
        Assert.True(evaluated.Decision.RequiresPersistence);
        var start = Assert.IsType<DurableTaskSequentialProcessStart>(evaluated.AcceptedStart);
        var context = start.Receipt.Request.Context;
        Assert.Equal(admission.Invocation.Authorization, context.Authorization);
        Assert.Equal(admission.Invocation.IssuedAtUtc, context.IssuedAtUtc);
        Assert.Equal(admission.Invocation.Provenance, context.Provenance);
        Assert.Equal(admission.Invocation.ObservedAtUtc, start.Receipt.AcceptedAtUtc);
        Assert.Equal(admission.Invocation.Authorization.AuthorityScope, start.ActivationContext.AuthorityScope);
        Assert.Equal(activationProvenance, start.ActivationContext.Provenance);
        Assert.NotEqual(context.Provenance, start.ActivationContext.Provenance);
        Assert.Equal(admission.ActivationContext.CorrelationId, start.ActivationContext.CorrelationId);
        Assert.Equal(admission.ActivationContext.Delivery, start.ActivationContext.Delivery);
    }

    [Fact]
    public void ExactCommandAndEquivalentIdempotentRetry_ReplayWinningAdmission()
    {
        var admission = Admission();
        var accepted = DurableTaskProcessStartAdmissionEvaluator.Evaluate(
            admission,
            ActivationProvenance(),
            sameCommand: null,
            sameIdempotency: null,
            existingInstance: null);
        var start = Assert.IsType<DurableTaskSequentialProcessStart>(accepted.AcceptedStart);

        var exact = DurableTaskProcessStartAdmissionEvaluator.Evaluate(
            Admission(observedAtUtc: admission.Invocation.ObservedAtUtc.AddMinutes(2)),
            ActivationProvenance(),
            start,
            start,
            start);
        var byIdempotency = DurableTaskProcessStartAdmissionEvaluator.Evaluate(
            Admission(
                commandId: "start-command/retry",
                issuedAtUtc: admission.Invocation.IssuedAtUtc.AddMinutes(2),
                observedAtUtc: admission.Invocation.ObservedAtUtc.AddMinutes(2)),
            ActivationProvenance(),
            sameCommand: null,
            sameIdempotency: start,
            existingInstance: start);

        Assert.Equal(ProcessStartDisposition.Replayed, exact.Decision.Result.Disposition);
        Assert.Equal(ProcessStartDisposition.Replayed, byIdempotency.Decision.Result.Disposition);
        Assert.Equal(accepted.Decision.Result.Admission, exact.Decision.Result.Admission);
        Assert.Equal(accepted.Decision.Result.Admission, byIdempotency.Decision.Result.Admission);
        Assert.Null(exact.AcceptedStart);
        Assert.Null(byIdempotency.AcceptedStart);
    }

    [Fact]
    public void CommandIdempotencyAndInstanceReuse_ReturnPreciseConflicts()
    {
        var accepted = DurableTaskProcessStartAdmissionEvaluator.Evaluate(
            Admission(),
            ActivationProvenance(),
            sameCommand: null,
            sameIdempotency: null,
            existingInstance: null);
        var start = Assert.IsType<DurableTaskSequentialProcessStart>(accepted.AcceptedStart);

        var command = DurableTaskProcessStartAdmissionEvaluator.Evaluate(
            Admission(input: "different"),
            ActivationProvenance(),
            sameCommand: start,
            sameIdempotency: null,
            existingInstance: null);
        var idempotency = DurableTaskProcessStartAdmissionEvaluator.Evaluate(
            Admission(commandId: "start-command/other", input: "different"),
            ActivationProvenance(),
            sameCommand: null,
            sameIdempotency: start,
            existingInstance: null);
        var instance = DurableTaskProcessStartAdmissionEvaluator.Evaluate(
            Admission(commandId: "start-command/new", idempotencyKey: "start-idempotency/new"),
            ActivationProvenance(),
            sameCommand: null,
            sameIdempotency: null,
            existingInstance: start);

        Assert.Equal(ProcessStartDisposition.CommandIdentityConflict, command.Decision.Result.Disposition);
        Assert.Equal(ProcessStartDiagnosticCodes.CommandIdentityConflict, command.Decision.Result.DiagnosticCode);
        Assert.Equal(ProcessStartDisposition.IdempotencyConflict, idempotency.Decision.Result.Disposition);
        Assert.Equal(ProcessStartDiagnosticCodes.CommandIdempotencyConflict, idempotency.Decision.Result.DiagnosticCode);
        Assert.Equal(ProcessStartDisposition.InstanceConflict, instance.Decision.Result.Disposition);
        Assert.Equal(ProcessStartDiagnosticCodes.InstanceConflict, instance.Decision.Result.DiagnosticCode);
        Assert.All([command, idempotency, instance], static result =>
        {
            Assert.False(result.Decision.RequiresPersistence);
            Assert.Null(result.AcceptedStart);
        });
    }

    [Fact]
    public void IndexIdentity_IsOpaqueStableAndAuthorityScoped()
    {
        var first = DurableTaskSequentialProcessIdentities.StartAdmissionIndex(
            ProcessControlTestFixture.Authority,
            "command",
            "sensitive-command/1");
        var replay = DurableTaskSequentialProcessIdentities.StartAdmissionIndex(
            ProcessControlTestFixture.Authority,
            "command",
            "sensitive-command/1");
        var otherTenant = DurableTaskSequentialProcessIdentities.StartAdmissionIndex(
            new("authority/motion", "tenant/other"),
            "command",
            "sensitive-command/1");

        Assert.Equal(first, replay);
        Assert.NotEqual(first, otherTenant);
        Assert.DoesNotContain("sensitive-command", first, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant/acme", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Admission_RoundTripsThroughDurableTaskDataConverter()
    {
        var admission = Admission();
        var converter = DurableTaskProcessDataConverter.Create();

        var roundTrip = converter.Deserialize<DurableTaskProcessStartAdmission>(
            converter.Serialize(admission));

        Assert.Equivalent(admission, roundTrip, strict: true);
    }

    static DurableTaskProcessStartAdmission Admission(
        string commandId = "start-command/1",
        string idempotencyKey = "start-idempotency/1",
        string instanceId = "process/start-1",
        string input = "start-value",
        DateTimeOffset? issuedAtUtc = null,
        DateTimeOffset? observedAtUtc = null)
    {
        var issued = issuedAtUtc ?? ProcessControlTestFixture.CreatedAtUtc;
        var observed = observedAtUtc ?? issued.AddMinutes(1);
        ProcessInstanceId instance = new(instanceId);
        var untrustedScope = new InteractionAuthorityScope("authority/untrusted", "tenant/untrusted");
        var request = new ProcessStartRequest(
            ProcessStartRequest.CurrentSchemaVersion,
            ProcessControlTestFixture.DefinitionReference("process/start-test", 'b'),
            new(
                new(commandId),
                new(idempotencyKey),
                instance,
                new("caller/untrusted", untrustedScope, "policy/untrusted"),
                issued,
                new(
                    new("caller-untrusted", "1"),
                    new("caller/untrusted"),
                    DocumentOrigin.Imported)),
            new(instance, new("process-attempt/initial")),
            ProcessControlTestFixture.StringValue(input));
        var activation = new ProcessActivationContext(
            untrustedScope,
            new("correlation/start-1"),
            new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            request.Context.Provenance);
        var invocation = new ExecutionApiInvocationContext(
            new(
                "operator/alice",
                ProcessControlTestFixture.Authority,
                "policy/start-tests/allow"),
            ProcessControlTestFixture.Provenance(),
            issued,
            observed,
            [ExecutionControlApiWireNames.AuthorizationRequirement(ProcessStartWireNames.Start)]);
        return new(request, activation, invocation);
    }

    static ExecutionProvenance ActivationProvenance() => new(
        new("cohesive-tests.process", "1"),
        new("tests/process/start-definition"),
        DocumentOrigin.User);
}
