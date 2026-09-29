using Cohesive.Api.Services;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Relations.Execution;
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
        IProcessExecutionValueRepository values, DeterministicHostedQueryBinding? resultClassifier = null) : base(operationId)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        Authority = authority;
        Values = values ?? throw new ArgumentNullException(nameof(values));
        ResultClassifier = resultClassifier;
    }
    /// <summary>Exact Process authority; its result contract is not copied into a service-owned schema.</summary>
    public CompiledProcessPlan Process { get; }
    /// <summary>Execution authority combined with the invocation's admitted logical scope.</summary>
    public string Authority { get; }
    internal IProcessExecutionValueRepository Values { get; }

    /// <summary>Exact deterministic classifier evaluated only after result-read admission.</summary>
    public DeterministicHostedQueryBinding? ResultClassifier { get; }
    internal static readonly Lazy<ValueContract> ClassificationContract = new(() =>
        new(new DefaultClrTypeRefMapper().Map(typeof(ServiceResultClassification), nullability: null)));

    internal static void ValidateClassifier(ExecutionDefinitionReference? declared, DeterministicHostedQueryBinding? binding,
        ValueContract output)
    {
        if (declared != binding?.Reference || (binding is not null
            && (binding.InputContract != output || binding.ResultContract != ClassificationContract.Value)))
            throw ServiceBindingValidationException.Error("services.binding.resultClassifierMismatch",
                "The classifier must match the exact declared Query, Process output and standard classification contract.", "/bindings/resultClassifier");
    }

    internal override void Validate(ServiceOperation operation)
    {
        if (operation is not ServiceProcessResultOperation result || result.Process != Process.DefinitionReference)
            throw ServiceBindingValidationException.Error("services.binding.resultSourceMismatch",
                "The binding must realize the exact declared Process result.", "/bindings/processResult");
        ValidateClassifier(result.ResultClassifier, ResultClassifier, Process.Definition.Result);
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
                var rejection = ClassifyProcessResult<PortableValue>(binding.ResultClassifier, result, admitted.Context, evidence);
                if (rejection is not null) return ValueTask.FromResult(rejection);
                return ValueTask.FromResult(new ServiceOperationResult<PortableValue>(ApiResultKind.Success, result, [],
                    evidence.Complete(ApiResultKind.Success)));
            });
    }
    static ServiceOperationResult<T>? ClassifyProcessResult<T>(DeterministicHostedQueryBinding? classifier,
        PortableValue terminal, OperationContext context, ServiceInvocationEvidence evidence) where T : class
    {
        if (classifier is null) return null;
        var classified = classifier.Evaluate(terminal, context.CancellationToken);
        if (classified.Type == ResultType.Failure)
            return RejectProcessResult<T>(evidence, ApiResultKind.InfrastructureError,
                "services.process.classificationFailed", "The terminal result could not be classified against its declared contract.");
        var decoded = HostedQueryValueAdapter.Decode<ServiceResultClassification>(classified.Success!,
            ServiceProcessResultBinding.ClassificationContract.Value);
        if (decoded.Type == ResultType.Failure)
            return RejectProcessResult<T>(evidence, ApiResultKind.InfrastructureError,
                "services.process.classificationFailed", "The terminal classification is invalid.");
        var classification = decoded.Success!;
        evidence.Record("terminalResultClassified", classification.Kind.ToString());
        return classification.Kind == ApiResultKind.Success ? null
            : new(classification.Kind, null, classification.Diagnostics, evidence.Complete(classification.Kind));
    }

}
