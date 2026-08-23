using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

internal sealed record CancellationFinalizerDurabilityTestFixture(
    CompiledProcessPlan Plan,
    CompiledProcessPlan FinalizerPlan,
    ProcessStartReceipt Start,
    ProcessActivationContext ActivationContext,
    ProcessInvocationProtocol<
        ProcessCancellationFinalizationInput<string>,
        ProcessCancellationAcknowledgement> Protocol,
    DurableRequestBinding Binding)
{
    internal static CancellationFinalizerDurabilityTestFixture Create()
    {
        var stringContract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        var finalizerInput = ProcessCancellationFinalizationContracts.Input(stringContract);
        var finalizer = ProcessAuthoring.Create<
            ProcessCancellationFinalizationInput<string>,
            ProcessCancellationAcknowledgement>(
            new(
                new("process/tests/durable-cancellation-finalizer"),
                new("revision/1"),
                new("return"),
                ProcessRecoveryPolicy.ContinueAttempt,
                ProcessControlTestFixture.Provenance()),
            finalizerInput,
            ProcessCancellationFinalizationContracts.Acknowledgement,
            process => process.Return(
                new("return"),
                process.CanonicalValue<ProcessCancellationAcknowledgement>(
                    Expr.Const(ObservationValue.FromObject(
                        new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                        {
                            ["attemptId"] = ObservationValue.FromString("process-attempt/1")
                        })),
                    ProcessCancellationFinalizationContracts.Acknowledgement)));
        var protocol = finalizer.InvocationProtocol(
            new("request/tests/durable-cancellation-finalizer"),
            new("revision/1"),
            ProcessInvocationResponsePolicy.ReconciledJoin(TimeSpan.FromDays(30)),
            ProcessControlTestFixture.Provenance());
        var parent = ProcessAuthoring.Create<string, string>(
            new(
                new("process/tests/durable-cancellable-parent"),
                new("revision/1"),
                new("wait"),
                ProcessRecoveryPolicy.ContinueAttempt,
                ProcessControlTestFixture.Provenance()),
            process =>
            {
                process.OnCancellation(new("cancel/finalize"), protocol);
                var dueAt = process.CanonicalValue<DateTimeOffset>(
                    Expr.Const(ObservationValue.FromDateTimeOffset(
                        new(2126, 1, 1, 0, 0, 0, TimeSpan.Zero))),
                    new(new ScalarTypeRef(ScalarTypeKind.Instant)));
                process.Timer(
                    new("wait"),
                    dueAt,
                    process.Edge(new("wait"), "elapsed", new("return")));
                process.Return(new("return"), process.Input.Value);
            });
        var linkValidation = ProcessDefinitionLink.TryCreateProcess(finalizer.Document, out var link);
        if (!linkValidation.IsValid || link is null)
        {
            throw new InvalidOperationException(Format(linkValidation));
        }
        var compilation = parent.Compile(new ProcessDefinitionValidationContext(
            definitions: [link],
            interactionContracts: protocol.Catalog));
        if (!compilation.IsSuccessful || compilation.Plan is null)
        {
            throw new InvalidOperationException(Format(compilation.Validation));
        }
        var finalizerCompilation = finalizer.Compile(new ProcessDefinitionValidationContext(
            interactionContracts: protocol.Catalog));
        if (!finalizerCompilation.IsSuccessful || finalizerCompilation.Plan is null)
        {
            throw new InvalidOperationException(Format(finalizerCompilation.Validation));
        }

        ProcessContinuationIdentity continuation = new(
            new("process-instance/durable-cancellable-parent"),
            new("process-attempt/1"));
        var issuedAtUtc = ProcessDurabilityTestFixture.IssuedAtUtc;
        var start = new ProcessStartReceipt(
            new(
                ProcessStartRequest.CurrentSchemaVersion,
                compilation.Plan.DefinitionReference,
                new(
                    new("start-command/durable-cancellable-parent"),
                    new("start-idempotency/durable-cancellable-parent"),
                    continuation.ProcessInstanceId,
                    new(
                        "operator/tests",
                        ProcessDurabilityTestFixture.Authority,
                        "policy/tests/allow"),
                    issuedAtUtc,
                    ProcessControlTestFixture.Provenance()),
                continuation,
                ProcessDurabilityTestFixture.StringValue("input/durable-cancellable-parent")),
            issuedAtUtc.AddSeconds(1));
        var activationContext = new ProcessActivationContext(
            ProcessDurabilityTestFixture.Authority,
            new("correlation/durable-cancellable-parent"),
            new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            ProcessControlTestFixture.Provenance());
        return new(
            compilation.Plan,
            finalizerCompilation.Plan,
            start,
            activationContext,
            protocol,
            protocol.BindDurably(
                maxAttempts: 3,
                claimLease: TimeSpan.FromMinutes(1),
                DurableOperationIdempotencyEvidence.TargetDeduplication,
                reconciliationTarget: new(finalizer.Reference, new("return"))));
    }

    internal PortableValue Acknowledgement(ProcessAttemptId attemptId) =>
        PortableValue.Concrete(
            ProcessCancellationFinalizationContracts.Acknowledgement,
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["attemptId"] = ObservationValue.FromString(attemptId.Value)
            }));

    static string Format(DocumentValidationResult validation) =>
        string.Join("; ", validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
}
