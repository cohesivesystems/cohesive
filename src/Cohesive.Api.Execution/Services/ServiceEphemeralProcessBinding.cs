using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>Associates an exact service Process with finite invocation-local execution.</summary>
/// <remarks>The factory is called only after admission and input validation. Its host must enforce resource
/// authorization and qualified per-operation persistence; service admission is not a resource grant.</remarks>
public sealed class ServiceEphemeralProcessBinding : ServiceBinding
{
    /// <summary>Prepares the canonical interpreter and validates supported constructs once.</summary>
    /// <exception cref="ArgumentException">Identity, authority or Process requirements are unsupported.</exception>
    /// <exception cref="ArgumentNullException">Plan or host factory is null.</exception>
    public ServiceEphemeralProcessBinding(string operationId, CompiledProcessPlan plan, string authority,
        Func<OperationContext, ExecutionApiInvocationContext, IAsyncProcessReferenceHost> host) : base(operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        Executor = new(plan);
        Authority = authority;
        Host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Prepared canonical execution; contains no invocation state.</summary>
    public EphemeralProcessExecutor Executor { get; }
    /// <summary>Host-associated authority; logical tenant is supplied by invocation admission.</summary>
    public string Authority { get; }
    internal Func<OperationContext, ExecutionApiInvocationContext, IAsyncProcessReferenceHost> Host { get; }

    internal override void Validate(ServiceOperation operation)
    {
        if (operation is not ServiceProcessOperation process || process.Process != Executor.Plan.DefinitionReference)
            throw ServiceBindingValidationException.Error("services.binding.inexact",
                "The binding must realize the exact declared Process.", "/bindings/process");
        if (process.Execution is not { Lifetime: ServiceProcessLifetime.Ephemeral, Completion: ServiceProcessCompletion.Terminal })
            throw ServiceBindingValidationException.Error("services.binding.executionUnsupported",
                "This binding requires explicit ephemeral terminal completion.", "/bindings/process/execution");
    }
}

public sealed partial class ServiceRuntime
{
    /// <summary>Authorizes and executes a declared ephemeral Process, deriving its exact input/output contracts.</summary>
    /// <remarks>There is no automatic retry or background admission. The execution budget begins after service
    /// admission and host construction. Resource-level checks belong to the exact host operations. Returned Process
    /// failure is distinct from admission failure; cancellation preserves observed host outcomes in its exception.</remarks>
    /// <exception cref="ArgumentException">The operation family, invocation identity or input is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation or deadline is observed; effects may have committed.</exception>
    /// <exception cref="InvalidOperationException">The host supplies invalid execution evidence.</exception>
    public async ValueTask<ServiceOperationResult<EphemeralProcessResult>> ExecuteProcessAsync(OperationContext context,
        string operationId, ProcessContinuationIdentity invocationIdentity, ObservationValue input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocationIdentity);
        if (!operations.TryGetValue(operationId, out var linked)
            || linked.Operation is not ServiceProcessOperation operation
            || linked.Binding is not ServiceEphemeralProcessBinding binding)
            throw new ArgumentException("The operation is not bound for ephemeral Process execution.", nameof(operationId));
        using var evidence = new ServiceInvocationEvidence(definitionReference, operationId, operation.Process,
            new(invocationIdentity.ProcessInstanceId.Value));
        try
        {
            var invocation = await AdmitProcessAsync(context, operation, binding.Authority, ProcessStartWireNames.Start,
                operation.Process).ConfigureAwait(false);
            if (invocation is null)
                return Reject(ApiResultKind.Forbidden, "services.authorization.denied", "The caller is not authorized to invoke this operation.");
            evidence.Record("authorityAdmitted");
            var materialized = PortableValue.Concrete(binding.Executor.Plan.Definition.Input, input);
            if (!PortableExecutionValidator.Validate(materialized, binding.Executor.Plan.ValidationContext.ShapeGraph).IsValid)
                return Reject(ApiResultKind.ValidationFailed, "services.process.inputInvalid", "Input must satisfy the exact Process contract.");
            context.ThrowIfCancellationRequested();
            var host = binding.Host(context, invocation)
                ?? throw new InvalidOperationException("The Process host factory returned null.");
            var execution = await binding.Executor.ExecuteAsync(context, invocationIdentity, materialized,
                new(invocation.Authorization.AuthorityScope, new(invocationIdentity.ProcessInstanceId.Value),
                    new(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal), invocation.Provenance),
                host, operation.Execution!.Timeout!.Value).ConfigureAwait(false);
            var decision = execution.Decision;
            evidence.Record("processExecutionCompleted");
            var kind = decision.Disposition switch
            {
                ProcessActivationDisposition.Completed => ApiResultKind.Success,
                ProcessActivationDisposition.Rejected => ApiResultKind.ValidationFailed,
                _ => ApiResultKind.DomainError
            };
            return new(kind, execution, decision.Diagnostics, evidence.Complete(kind));
        }
        catch (Exception exception)
        {
            evidence.Fail(exception);
            throw;
        }

        ServiceOperationResult<EphemeralProcessResult> Reject(ApiResultKind kind, string code, string message)
        {
            evidence.Record("invocationRejected", code);
            return new(kind, null, [new(code, DiagnosticSeverity.Error, message)], evidence.Complete(kind));
        }
    }
}
