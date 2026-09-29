using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Processes.Compilation;

namespace Cohesive.Api.Execution.Services;

/// <summary>Realizes a declared lifecycle operation through native durable Process-control admission.</summary>
/// <remarks>
/// The dispatcher must enforce the trusted exact-definition restriction against authoritative state before
/// mutation or replay, and retain native receipts. It owns instance lookup, fencing and recovery. This binding
/// supports Pause, Continue, RestartAttempt, Cancel and Terminate; Signal ingress and read projections have
/// different contracts and require their own qualified bindings.
/// </remarks>
public sealed class ServiceProcessControlBinding : ServiceBinding
{
    /// <summary>Associates a prepared Process and authority with a native lifecycle dispatcher.</summary>
    /// <exception cref="ArgumentNullException">Plan or dispatcher is null.</exception>
    /// <exception cref="ArgumentException">Operation identity or authority is empty.</exception>
    public ServiceProcessControlBinding(string operationId, CompiledProcessPlan plan, string authority,
        ExecutionProcessControlDispatcher control) : base(operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Authority = authority;
        Control = control ?? throw new ArgumentNullException(nameof(control));
    }

    /// <summary>Exact Process whose lifecycle authority is controlled.</summary>
    public CompiledProcessPlan Plan { get; }
    /// <summary>Native execution authority associated by deployment.</summary>
    public string Authority { get; }
    internal ExecutionProcessControlDispatcher Control { get; }

    internal override void Validate(ServiceOperation operation)
    {
        if (operation is not ServiceProcessControlOperation control || control.Process != Plan.DefinitionReference)
            throw ServiceBindingValidationException.Error("services.binding.inexact",
                "The binding must control the exact declared Process.", "/bindings/processControl");
        ServiceRuntime.NativeLifecycleOperation(control.Action);
    }
}

public sealed partial class ServiceRuntime
{
    internal static ApiOperation NativeLifecycleOperation(string action)
    {
        var native = NativeProcessOperation(action);
        if (!typeof(ProcessControlCommand).IsAssignableFrom(native.RequestType)
            || native.RequestType == typeof(InspectProcessCommand) || native.RequestType == typeof(SignalProcessCommand))
            throw ServiceBindingValidationException.Error("services.binding.controlUnsupported",
                "This binding requires a native lifecycle mutation; inspection and Signal ingress require other bindings.", "/bindings/processControl/action");
        return native;
    }

    /// <summary>Admits and dispatches the declared native control action against its exact Process definition.</summary>
    /// <remarks>
    /// The native catalog owns command types and result semantics. Trusted authority, provenance and definition
    /// are supplied separately so the dispatcher can restore original occurrence evidence on exact replay.
    /// Missing or foreign-definition instances return NotFound without exposing their state. Cancellation can
    /// interrupt waiting after durable admission and must be retried with the same command identity.
    /// </remarks>
    /// <exception cref="ArgumentException">The operation is not a declared control operation.</exception>
    /// <exception cref="InvalidOperationException">The dispatcher returns evidence for another target.</exception>
    /// <exception cref="OperationCanceledException">Invocation or waiting is cancelled.</exception>
    public async ValueTask<ServiceOperationResult<ExecutionControlResult>> ControlAsync(
        OperationContext context, string operationId, ProcessControlCommand request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (!operations.TryGetValue(operationId, out var linked)
            || linked.Operation is not ServiceProcessControlOperation operation || linked.Binding is not ServiceProcessControlBinding binding)
            throw new ArgumentException("The operation is not a declared Process control.", nameof(operationId));
        using var evidence = new ServiceInvocationEvidence(definitionReference, operationId, operation.Process, new(request.Context.CommandId.Value));
        try
        {
            var invocation = await AdmitProcessAsync(context, operation, binding.Authority, operation.Action, operation.Process).ConfigureAwait(false);
            if (invocation is null)
                return Reject(ApiResultKind.Forbidden, "services.authorization.denied", "The caller is not authorized to invoke this operation.");
            evidence.Record("authorityAdmitted");
            var native = NativeProcessOperation(operation.Action);
            if (request.GetType() != native.RequestType)
                return Reject(ApiResultKind.ValidationFailed, "services.process.commandMismatch", "The command must match the declared native lifecycle action.");
            context.ThrowIfCancellationRequested();
            ExecutionControlResult result;
            try
            {
                result = await binding.Control(context, request, invocation).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                return Reject(ApiResultKind.NotFound, "services.process.notFound", "No Process is visible at the declared target.");
            }
            catch (UnauthorizedAccessException)
            {
                return Reject(ApiResultKind.Forbidden, "services.authorization.denied", "The caller is not authorized to control this Process.");
            }
            if (result is null || result.Status.Definition != operation.Process
                || result.Status.ProcessInstanceId != request.Context.ProcessInstanceId)
                throw new InvalidOperationException("The Process dispatcher returned control evidence for another target.");
            evidence.Record("processControlDispatched");
            return new(result.ResultKind, result, [], evidence.Complete(result.ResultKind));
        }
        catch (Exception exception)
        {
            evidence.Fail(exception);
            throw;
        }

        ServiceOperationResult<ExecutionControlResult> Reject(ApiResultKind kind, string code, string message)
        {
            evidence.Record("invocationRejected", code);
            return new(kind, null, [new(code, DiagnosticSeverity.Error, message, "/processControl")], evidence.Complete(kind));
        }
    }

    async ValueTask<ExecutionApiInvocationContext?> AdmitProcessAsync(OperationContext context, ServiceOperation operation,
        string authority, string action, ExecutionDefinitionReference process)
    {
        context.ThrowIfCancellationRequested();
        var scope = await authorization.AdmitAsync(context, operation).ConfigureAwait(false);
        var identity = context.GetIdentityContextOrDefault();
        if (scope is null || identity is null || identity.Actor.Kind == PrincipalKind.Anonymous
            || string.IsNullOrWhiteSpace(identity.Actor.Id) || identity.Subject is not null)
            return null;
        var now = context.UtcNow;
        return new(new(identity.Actor.Id, new(authority, scope.Id),
                $"service/{definitionReference.Fingerprint.Value}/operation/{operation.Id}"),
            Declaration.Metadata.Provenance, now, now,
            [ExecutionControlApiWireNames.AuthorizationRequirement(action)], expectedProcessDefinition: process);
    }
}
