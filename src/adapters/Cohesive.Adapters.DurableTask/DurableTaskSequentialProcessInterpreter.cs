using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Adapters.DurableTask;

static class DurableTaskSequentialProcessInterpreter
{
    internal static async Task<DurableTaskSequentialProcessResult> RunAsync(
        CompiledProcessPlan plan,
        DurableTaskSequentialProcessStart start,
        Func<DurableTaskProcessHostOperation, Task<ProcessOperationResult>> executeOperation,
        Func<Task<ProcessActivationInput>> waitForInteraction,
        Func<Task> createDurableCut,
        Func<DateTimeOffset> getCurrentUtc,
        Action<DurableTaskSequentialProcessResult>? observe = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(executeOperation);
        ArgumentNullException.ThrowIfNull(waitForInteraction);
        ArgumentNullException.ThrowIfNull(createDurableCut);
        ArgumentNullException.ThrowIfNull(getCurrentUtc);
        if (plan.DefinitionReference != start.Receipt.Request.Definition)
        {
            throw new ArgumentException("The Process start pins a different exact compiled definition.", nameof(start));
        }

        var state = ProcessReferenceInterpreter.Create(plan, start.Receipt);
        var cause = ProcessActivationCause.Start;
        var observedAtUtc = start.Receipt.AcceptedAtUtc;
        ImmutableArray<ProcessActivationInput> inputs = [];
        List<InteractionEnvelope> emissions = [];
        List<ProcessInputReceipt> inputAdmissions = [];
        List<DocumentValidationDiagnostic> diagnostics = [];
        List<ProcessExecutionEvidence> evidence = [];

        while (true)
        {
            var activation = new ProcessActivation(
                DurableTaskSequentialProcessIdentities.Activation(state),
                cause,
                observedAtUtc,
                start.ActivationContext,
                inputs);
            var decision = await ActivateAsync(plan, state, activation, executeOperation).ConfigureAwait(true);
            state = decision.State;
            emissions.AddRange(decision.Emissions);
            inputAdmissions.AddRange(decision.InputAdmissions);
            diagnostics.AddRange(decision.Diagnostics);
            evidence.Add(decision.Evidence);
            var result = new DurableTaskSequentialProcessResult(
                decision.Disposition,
                state,
                [.. emissions],
                [.. inputAdmissions],
                [.. diagnostics],
                [.. evidence]);
            observe?.Invoke(result);

            switch (decision.Disposition)
            {
                case ProcessActivationDisposition.Completed:
                case ProcessActivationDisposition.Failed:
                case ProcessActivationDisposition.Cancelled:
                case ProcessActivationDisposition.Quiescent:
                    return result;

                case ProcessActivationDisposition.Rejected:
                    if (state.OutstandingRequests.IsEmpty)
                    {
                        return result;
                    }
                    inputs = [await waitForInteraction().ConfigureAwait(true)];
                    cause = ProcessActivationCause.Interaction;
                    observedAtUtc = RequireUtc(getCurrentUtc());
                    break;

                case ProcessActivationDisposition.DurableCut:
                    var safePoint = decision.Evidence.SafePointNode
                        ?? throw new InvalidOperationException("A durable-cut decision did not identify its safe-point node.");
                    switch (plan.GetNode(safePoint))
                    {
                        case RequestProcessNode:
                            inputs = [await waitForInteraction().ConfigureAwait(true)];
                            cause = ProcessActivationCause.Interaction;
                            observedAtUtc = RequireUtc(getCurrentUtc());
                            break;
                        case DurableCutProcessNode:
                            await createDurableCut().ConfigureAwait(true);
                            inputs = [];
                            cause = ProcessActivationCause.Continue;
                            observedAtUtc = RequireUtc(getCurrentUtc());
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Sequential Durable Task execution cannot resume safe point '{safePoint.Value}'.");
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(decision.Disposition),
                        decision.Disposition,
                        "Unsupported canonical Process activation disposition.");
            }
        }
    }

    static async Task<ProcessActivationDecision> ActivateAsync(
        CompiledProcessPlan plan,
        ProcessContinuationState state,
        ProcessActivation activation,
        Func<DurableTaskProcessHostOperation, Task<ProcessOperationResult>> executeOperation)
    {
        var host = new SuspendingHost();
        while (true)
        {
            try
            {
                return ProcessReferenceInterpreter.Activate(plan, state, activation, host);
            }
            catch (PendingHostOperationException pending)
            {
                var result = await executeOperation(pending.Operation).ConfigureAwait(true)
                    ?? throw new InvalidOperationException("A Durable Task host-operation activity returned null.");
                host.Materialize(pending.Operation, result);
            }
        }
    }

    static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Durable Task orchestration time must use the UTC offset.");
        }
        return value;
    }

    sealed class PendingHostOperationException(DurableTaskProcessHostOperation operation) : Exception
    {
        internal DurableTaskProcessHostOperation Operation { get; } = operation;
    }

    sealed class SuspendingHost : IProcessReferenceHost
    {
        readonly Dictionary<OperationKey, MaterializedOperation> materialized = [];

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            Resolve(DurableTaskProcessHostOperation.For(invocation));

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            Resolve(DurableTaskProcessHostOperation.For(evaluation));

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new NotSupportedException(
                $"Signal target resolution at '{resolution.Node.Value}' is outside the sequential executable slice.");

        internal void Materialize(DurableTaskProcessHostOperation operation, ProcessOperationResult result)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(result);
            if (!result.IsValidOutcome())
            {
                throw new InvalidOperationException("A Durable Task host-operation activity returned an invalid outcome.");
            }

            var key = Key(operation);
            if (materialized.TryGetValue(key, out var retained))
            {
                if (retained.Operation != operation || retained.Result != result)
                {
                    throw new InvalidOperationException(
                        "One Process host-operation occurrence was materialized with conflicting evidence.");
                }
                return;
            }
            materialized.Add(key, new(operation, result));
        }

        ProcessOperationResult Resolve(DurableTaskProcessHostOperation operation)
        {
            var key = Key(operation);
            if (!materialized.TryGetValue(key, out var retained))
            {
                throw new PendingHostOperationException(operation);
            }
            if (retained.Operation != operation)
            {
                throw new InvalidOperationException(
                    "One Process host-operation occurrence produced inconsistent invocation evidence during replay.");
            }
            return retained.Result;
        }

        static OperationKey Key(DurableTaskProcessHostOperation operation) => operation.Kind switch
        {
            DurableTaskProcessHostOperationKind.Transition => Key(operation.Transition!),
            DurableTaskProcessHostOperationKind.RelationQuery => Key(operation.RelationQuery!),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, "Unsupported host operation.")
        };

        static OperationKey Key(ProcessTransitionInvocation invocation) => new(
            invocation.Continuation,
            invocation.Activation,
            invocation.Token,
            invocation.Node,
            invocation.Occurrence);

        static OperationKey Key(ProcessRelationEvaluation evaluation) => new(
            evaluation.Continuation,
            evaluation.Activation,
            evaluation.Token,
            evaluation.Node,
            evaluation.Occurrence);
    }

    readonly record struct OperationKey(
        ProcessContinuationIdentity Continuation,
        ActivationId Activation,
        TokenId Token,
        ExecutionNodeId Node,
        long Occurrence);

    sealed record MaterializedOperation(
        DurableTaskProcessHostOperation Operation,
        ProcessOperationResult Result);
}
