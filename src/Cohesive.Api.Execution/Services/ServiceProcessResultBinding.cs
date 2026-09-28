using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Runtime;

namespace Cohesive.Api.Execution.Services;

/// <summary>Binds an exact Process terminal-value read to the existing protected execution-value provider.</summary>
public sealed class ServiceProcessResultBinding : ServiceBinding
{
    /// <summary>Retains the Process contract and provider without reading or executing it.</summary>
    /// <exception cref="ArgumentNullException">A plan or provider is null.</exception>
    /// <exception cref="ArgumentException">The authority is empty.</exception>
    public ServiceProcessResultBinding(string operationId, CompiledProcessPlan process, string authority,
        IProcessExecutionValueRepository values) : base(operationId)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        Authority = authority;
        Values = values ?? throw new ArgumentNullException(nameof(values));
    }
    /// <summary>Exact Process authority; its result contract is not copied into a service-owned schema.</summary>
    public CompiledProcessPlan Process { get; }
    /// <summary>Execution authority combined with the invocation's admitted logical scope.</summary>
    public string Authority { get; }
    internal IProcessExecutionValueRepository Values { get; }

    internal override void Validate(ServiceOperation operation)
    {
        if (operation is not ServiceProcessResultOperation result || result.Process != Process.DefinitionReference)
            throw ServiceBindingValidationException.Error("services.binding.resultSourceMismatch",
                "The binding must realize the exact declared Process result.", "/bindings/processResult");
    }
}

public sealed partial class ServiceRuntime
{
    /// <summary>Reads the original canonical terminal value without executing the Process or consulting current entities.</summary>
    /// <remarks>Admission precedes protected reads. Optional provider-native waiting is bounded and followed by
    /// reauthorization. Requirements authorize the entire Process output in the admitted scope; use a separately
    /// restricted capability for sensitive outputs. Pending execution returns Accepted without a value.</remarks>
    /// <exception cref="ArgumentException">The operation, instance or wait duration is invalid.</exception>
    /// <exception cref="OperationCanceledException">Invocation cancellation is observed.</exception>
    public ValueTask<ServiceOperationResult<PortableValue>> ReadProcessResultAsync(OperationContext context,
        string operationId, ProcessInstanceId instance, TimeSpan? maximumWait = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.Value);
        if (maximumWait is { } duration && (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(maximumWait), "A positive finite timer duration is required.");
        if (!operations.TryGetValue(operationId, out var linked)
            || linked.Operation is not ServiceProcessResultOperation operation
            || linked.Binding is not ServiceProcessResultBinding binding)
            throw new ArgumentException("The operation is not a declared Process result read.", nameof(operationId));
        return ReadProcessResultCoreAsync<PortableValue>(context, operation, operation.Process, binding.Authority,
            binding.Values, instance, maximumWait, (admitted, evidence) =>
            {
                if (admitted.Values.TerminalOutcome!.Detail?.Value is not PortableValue result
                    || result.Contract != binding.Process.Definition.Result
                    || !PortableExecutionValidator.Validate(result, binding.Process.ValidationContext.ShapeGraph).IsValid)
                    return ValueTask.FromResult(RejectProcessResult<PortableValue>(evidence, ApiResultKind.InfrastructureError,
                        "services.process.resultUnavailable", "The retained value does not satisfy the exact Process result contract."));
                evidence.Record("terminalResultValidated");
                return ValueTask.FromResult(new ServiceOperationResult<PortableValue>(ApiResultKind.Success, result, [],
                    evidence.Complete(ApiResultKind.Success)));
            });
    }
}
