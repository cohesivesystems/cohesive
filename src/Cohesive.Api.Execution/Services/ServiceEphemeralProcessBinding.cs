using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Relations.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>Associates an exact service Process with finite invocation-local execution.</summary>
/// <remarks>The factory is called only after admission and input validation. Its host must enforce resource
/// authorization and qualified per-operation persistence; service admission is not a resource grant.</remarks>
public sealed class ServiceEphemeralProcessBinding : ServiceBinding
{
    /// <summary>Prepares the canonical interpreter and validates supported constructs once.</summary>
    /// <param name="operationId">Declared operation identity.</param>
    /// <param name="plan">Exact compiled Process with its input and public result contracts.</param>
    /// <param name="authority">Execution authority combined with the admitted tenant scope.</param>
    /// <param name="host">Invocation-scoped factory called after authorization and input validation.</param>
    /// <param name="resultClassifier">Optional deterministic binding for the exact declared public-output classifier.</param>
    /// <exception cref="ArgumentException">Identity, authority or Process requirements are unsupported.</exception>
    /// <exception cref="ArgumentNullException">Plan or host factory is null.</exception>
    public ServiceEphemeralProcessBinding(string operationId, CompiledProcessPlan plan, string authority,
        Func<OperationContext, ExecutionApiInvocationContext, IAsyncProcessReferenceHost> host, DeterministicHostedQueryBinding? resultClassifier = null) : base(operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        Executor = new(plan);
        Authority = authority;
        ResultClassifier = resultClassifier;
        Host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Prepared canonical execution; contains no invocation state.</summary>
    public EphemeralProcessExecutor Executor { get; }
    /// <summary>Host-associated authority; logical tenant is supplied by invocation admission.</summary>
    public string Authority { get; }
    /// <summary>Optional exact deterministic classifier of the successful public output.</summary>
    public DeterministicHostedQueryBinding? ResultClassifier { get; }
    internal Func<OperationContext, ExecutionApiInvocationContext, IAsyncProcessReferenceHost> Host { get; }

    internal override void Validate(ServiceOperation operation)
    {
        if (operation is not ServiceProcessOperation process || process.Process != Executor.Plan.DefinitionReference)
            throw ServiceBindingValidationException.Error("services.binding.inexact",
                "The binding must realize the exact declared Process.", "/bindings/process");
        if (process.Execution is not { Lifetime: ProcessExecutionLifetime.Ephemeral, Completion: ServiceProcessCompletion.Terminal })
            throw ServiceBindingValidationException.Error("services.binding.executionUnsupported",
                "This binding requires explicit ephemeral terminal completion.", "/bindings/process/execution");
        ServiceProcessResultBinding.ValidateClassifier(process.ResultClassifier, ResultClassifier, Executor.Plan.Definition.Result);
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
            var attribution = new ProcessControlCommandContext(new($"ephemeral/{invocationIdentity.ProcessInstanceId.Value}"),
                new($"ephemeral/{invocationIdentity.ProcessInstanceId.Value}"), invocationIdentity.ProcessInstanceId,
                invocation.Authorization, invocation.IssuedAtUtc, invocation.Provenance);
            var execution = await binding.Executor.ExecuteAsync(context, invocationIdentity, materialized,
                new(invocation.Authorization.AuthorityScope, new(invocationIdentity.ProcessInstanceId.Value),
                    new(InteractionDurabilityDemand.ActivationLocal, InteractionVisibilityDemand.ActivationLocal),
                    binding.Executor.Plan.Document.Metadata.Provenance),
                new StartAttributedHost(host, attribution), operation.Execution!.Timeout!.Value).ConfigureAwait(false);
            var decision = execution.Decision;
            evidence.Record("processExecutionCompleted");
            var kind = decision.Disposition switch
            {
                ProcessActivationDisposition.Completed => ApiResultKind.Success,
                ProcessActivationDisposition.Rejected => ApiResultKind.ValidationFailed,
                _ => ApiResultKind.DomainError
            };
            if (kind == ApiResultKind.Success && binding.ResultClassifier is not null)
            {
                var terminal = decision.State.Terminal.Detail?.Value
                    ?? throw new InvalidOperationException("Completed Process returned no public value.");
                var rejection = ClassifyProcessResult<EphemeralProcessResult>(binding.ResultClassifier, terminal, context, evidence);
                if (rejection is not null) return rejection;
            }
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

    sealed class StartAttributedHost(IAsyncProcessReferenceHost host, ProcessControlCommandContext attribution) : IAsyncProcessReferenceHost
    {
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation) =>
            host.EvaluateRelationAsync(context, evaluation.WithInvocationStartContext(attribution));
        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation) =>
            host.InvokeTransitionAsync(context, invocation);
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) =>
            host.ResolveSignalTargetAsync(context, resolution);
    }

}
