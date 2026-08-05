using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Internal control-flow signal for one unmaterialized asynchronous Transition operation.</summary>
sealed class ProcessTransitionOperationPendingException(ProcessTransitionInvocation invocation) : Exception
{
    internal ProcessTransitionInvocation Invocation { get; } =
        invocation ?? throw new ArgumentNullException(nameof(invocation));
}

/// <summary>
/// Activation-local host that suspends pure interpretation until asynchronous Transition evidence is available.
/// </summary>
sealed class ProcessTransitionOperationSuspensionHost(IProcessReferenceHost inner) : IProcessReferenceHost
{
    readonly IProcessReferenceHost inner = inner ?? throw new ArgumentNullException(nameof(inner));
    readonly Dictionary<ProcessOperationOccurrence, MaterializedTransition> materialized = [];

    public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var key = Key(invocation);
        if (!materialized.TryGetValue(key, out var retained))
        {
            throw new ProcessTransitionOperationPendingException(invocation);
        }
        if (retained.Invocation != invocation)
        {
            throw new InvalidOperationException(
                "One Process Transition occurrence produced inconsistent invocation evidence during activation replay.");
        }
        return retained.Result;
    }

    public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
        inner.EvaluateRelation(evaluation);

    public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
        inner.ResolveSignalTarget(resolution);

    internal void Materialize(
        ProcessTransitionInvocation invocation,
        ProcessOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsValidOutcome())
        {
            throw new InvalidOperationException("A Process Transition adapter returned an invalid operation result.");
        }

        var key = Key(invocation);
        if (materialized.TryGetValue(key, out var retained))
        {
            if (retained.Invocation != invocation || retained.Result != result)
            {
                throw new InvalidOperationException(
                    "One Process Transition occurrence was materialized with conflicting evidence.");
            }
            return;
        }
        materialized.Add(key, new(invocation, result));
    }

    static ProcessOperationOccurrence Key(ProcessTransitionInvocation invocation) =>
        new(
            invocation.Continuation,
            invocation.Activation,
            invocation.Token,
            invocation.Node,
            invocation.Occurrence);

    sealed record MaterializedTransition(
        ProcessTransitionInvocation Invocation,
        ProcessOperationResult Result);
}
