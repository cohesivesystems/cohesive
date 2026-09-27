using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;

namespace Cohesive.Api.Execution.Services;

/// <summary>Associates a declared Process entry with its exact plan and existing authoritative start dispatcher.</summary>
/// <remarks>
/// The dispatcher owns durable admission, retained issuance evidence, deduplication and activation scheduling.
/// This binding does not replace those guarantees with an in-memory receipt or a service-owned workflow loop.
/// </remarks>
public sealed class ServiceProcessBinding : ServiceBinding
{
    /// <summary>Retains a compiled Process and its native admission boundary without invoking infrastructure.</summary>
    /// <param name="operationId">Declared service operation.</param>
    /// <param name="plan">Exact compiled Process authority, including input and recovery semantics.</param>
    /// <param name="authority">Native execution authority; the admitted logical scope supplies its tenant component.</param>
    /// <param name="start">Existing authoritative dispatcher qualified for the Process's required guarantees.</param>
    /// <exception cref="ArgumentException">Operation identity or authority is empty.</exception>
    /// <exception cref="ArgumentNullException">Plan or dispatcher is null.</exception>
    public ServiceProcessBinding(string operationId, CompiledProcessPlan plan, string authority,
        ExecutionProcessStartDispatcher start) : base(operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Authority = authority;
        Start = start ?? throw new ArgumentNullException(nameof(start));
    }

    /// <summary>Exact Process whose native graph owns all orchestration.</summary>
    public CompiledProcessPlan Plan { get; }
    /// <summary>Native execution authority associated by the host.</summary>
    public string Authority { get; }
    internal ExecutionProcessStartDispatcher Start { get; }

    internal override void Validate(ServiceOperation operation)
    {
        if (operation is not ServiceProcessOperation process || process.Process != Plan.DefinitionReference)
            throw ServiceBindingValidationException.Error("services.binding.inexact",
                "The binding must realize the exact declared Process.", "/bindings/process");
    }
}

public sealed partial class ServiceRuntime
{
    /// <summary>Starts the exact declared Process through its existing admission and recovery authority.</summary>
    /// <remarks>
    /// Command, idempotency and continuation identities remain caller intent. Authorization, issuance time and
    /// provenance are reconstructed from the admitted identity and service declaration. The dispatcher must retain
    /// original trusted evidence for replay, as required by the native start contract. No result means admission may
    /// be ambiguous; callers retry with the same native command/idempotency identities rather than inventing new ones.
    /// </remarks>
    /// <exception cref="ArgumentException">The operation, exact definition or Process input is invalid.</exception>
    /// <exception cref="InvalidOperationException">The native dispatcher returns incoherent admission evidence.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed, including ambiguous in-flight admission.</exception>
    public async ValueTask<ServiceOperationResult<ProcessStartResult>> StartAsync(OperationContext context, string operationId,
        ProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (!operations.TryGetValue(operationId, out var linked)
            || linked.Operation is not ServiceProcessOperation operation || linked.Binding is not ServiceProcessBinding binding)
            throw new ArgumentException("The operation is not a declared Process entry.", nameof(operationId));
        using var evidence = new ServiceInvocationEvidence(definitionReference, operationId, operation.Process,
            new(request.Context.CommandId.Value));
        try
        {
            var invocation = await AdmitProcessAsync(context, operation, binding.Authority, ProcessStartWireNames.Start,
                operation.Process).ConfigureAwait(false);
            if (invocation is null)
            {
                evidence.Record("invocationRejected", "services.authorization.denied");
                return new(ApiResultKind.Forbidden, null,
                    [new("services.authorization.denied", DiagnosticSeverity.Error, "The caller is not authorized to invoke this operation.", "/authorization")],
                    evidence.Complete(ApiResultKind.Forbidden));
            }
            evidence.Record("authorityAdmitted");
            if (request.Definition != operation.Process)
                return RejectInput("services.process.definitionMismatch", "A service entry only admits its exact declared Process.");
            if (request.Input is not { State: PortableValueState.Concrete } input
                || input.Contract != binding.Plan.Definition.Input
                || !PortableExecutionValidator.Validate(input, binding.Plan.ValidationContext.ShapeGraph).IsValid)
                return RejectInput("services.process.inputInvalid", "The input must satisfy the exact Process contract.");
            var canonical = new ProcessStartRequest(request.SchemaVersion, operation.Process,
                new(request.Context.CommandId, request.Context.IdempotencyKey, request.Context.ProcessInstanceId,
                    invocation.Authorization, invocation.IssuedAtUtc, invocation.Provenance), request.InitialContinuation, input);
            context.ThrowIfCancellationRequested();
            var result = await binding.Start(context, canonical, invocation).ConfigureAwait(false);
            if (result is null || (result.Admission is { } admission
                && (admission.Definition != operation.Process || admission.Continuation != request.InitialContinuation)))
                throw new InvalidOperationException("The Process dispatcher returned admission for a different definition or continuation.");
            evidence.Record("processStartDispatched");
            var kind = result.IsConflict ? ApiResultKind.Conflict : ApiResultKind.Success;
            return new(kind, result, [], evidence.Complete(kind));
        }
        catch (Exception exception)
        {
            evidence.Fail(exception);
            throw;
        }

        ServiceOperationResult<ProcessStartResult> RejectInput(string code, string message)
        {
            evidence.Record("invocationRejected", code);
            return new(ApiResultKind.ValidationFailed, null, [new(code, DiagnosticSeverity.Error, message, "/input")],
                evidence.Complete(ApiResultKind.ValidationFailed));
        }
    }
}
