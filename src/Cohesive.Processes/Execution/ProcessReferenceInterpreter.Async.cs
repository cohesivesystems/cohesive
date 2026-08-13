using Cohesive.Execution;
using Cohesive.Prelude;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Execution;

public static partial class ProcessReferenceInterpreter
{
    /// <summary>
    /// Executes one finite canonical activation while materializing naturally asynchronous host evidence.
    /// </summary>
    /// <param name="context">Physical operation context carrying cancellation and infrastructure attribution.</param>
    /// <param name="plan">Exact compiled canonical Process plan.</param>
    /// <param name="state">Immutable continuation state at activation entry.</param>
    /// <param name="activation">Finite activation request interpreted by the canonical reducer.</param>
    /// <param name="host">Asynchronous physical host for exact operations reached by the reducer.</param>
    /// <returns>The same canonical activation decision produced by synchronous reference interpretation.</returns>
    /// <remarks>
    /// An unmaterialized operation suspends only this physical driver. The method awaits the operation, retains its
    /// result by exact occurrence identity, and re-enters <see cref="Activate"/> from the unchanged activation input.
    /// Materialized occurrences are not invoked twice during re-entry. Cancellation leaves no partial semantic
    /// decision and must be retried from the caller's retained state.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="context"/> is cancelled or the asynchronous host cancels physical execution.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A host returns null or invalid evidence, or one exact occurrence is materialized with conflicting evidence.
    /// </exception>
    public static async ValueTask<ProcessActivationDecision> ActivateAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessContinuationState state,
        ProcessActivation activation,
        IAsyncProcessReferenceHost host)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(host);

        var materialized = new AsyncMaterializingHost();
        while (true)
        {
            context.ThrowIfCancellationRequested();
            try
            {
                return Activate(plan, state, activation, materialized);
            }
            catch (PendingAsyncTransitionException pending)
            {
                var result = await host.InvokeTransitionAsync(context, pending.Invocation).ConfigureAwait(false);
                materialized.Materialize(pending.Invocation, RequireOperationResult(result, "Transition"));
            }
            catch (PendingAsyncRelationException pending)
            {
                var result = await host.EvaluateRelationAsync(context, pending.Evaluation).ConfigureAwait(false);
                materialized.Materialize(pending.Evaluation, RequireOperationResult(result, "Relation/Query"));
            }
            catch (PendingAsyncSignalTargetException pending)
            {
                var result = await host.ResolveSignalTargetAsync(context, pending.Resolution).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "The asynchronous Process host returned null Signal-target evidence.");
                materialized.Materialize(pending.Resolution, result);
            }
        }
    }

    static ProcessOperationResult RequireOperationResult(ProcessOperationResult? result, string operation)
    {
        if (result is null)
            throw new InvalidOperationException($"The asynchronous Process host returned null {operation} evidence.");
        if (!result.IsValidOutcome())
            throw new InvalidOperationException($"The asynchronous Process host returned invalid {operation} evidence.");
        return result;
    }

    sealed class PendingAsyncTransitionException(ProcessTransitionInvocation invocation) : Exception
    {
        internal ProcessTransitionInvocation Invocation { get; } = invocation;
    }

    sealed class PendingAsyncRelationException(ProcessRelationEvaluation evaluation) : Exception
    {
        internal ProcessRelationEvaluation Evaluation { get; } = evaluation;
    }

    sealed class PendingAsyncSignalTargetException(ProcessSignalTargetResolution resolution) : Exception
    {
        internal ProcessSignalTargetResolution Resolution { get; } = resolution;
    }

    sealed class AsyncMaterializingHost : IProcessReferenceHost
    {
        readonly Dictionary<AsyncOperationKey, MaterializedOperation> operations = [];
        readonly Dictionary<AsyncOperationKey, MaterializedSignalTarget> signalTargets = [];

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            var key = Key(AsyncOperationKind.Transition, invocation.Continuation, invocation.Activation,
                invocation.Token, invocation.Node, invocation.Occurrence);
            if (!operations.TryGetValue(key, out var retained))
                throw new PendingAsyncTransitionException(invocation);
            RequireSame(retained.Request, invocation, key);
            return retained.Result;
        }

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation)
        {
            ArgumentNullException.ThrowIfNull(evaluation);
            var key = Key(AsyncOperationKind.RelationQuery, evaluation.Continuation, evaluation.Activation,
                evaluation.Token, evaluation.Node, evaluation.Occurrence);
            if (!operations.TryGetValue(key, out var retained))
                throw new PendingAsyncRelationException(evaluation);
            RequireSame(retained.Request, evaluation, key);
            return retained.Result;
        }

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution)
        {
            ArgumentNullException.ThrowIfNull(resolution);
            var key = Key(AsyncOperationKind.SignalTarget, resolution.Continuation, resolution.Activation,
                resolution.Token, resolution.Node, resolution.Occurrence);
            if (!signalTargets.TryGetValue(key, out var retained))
                throw new PendingAsyncSignalTargetException(resolution);
            RequireSame(retained.Resolution, resolution, key);
            return retained.Result;
        }

        internal void Materialize(ProcessTransitionInvocation invocation, ProcessOperationResult result) =>
            Materialize(
                Key(AsyncOperationKind.Transition, invocation.Continuation, invocation.Activation,
                    invocation.Token, invocation.Node, invocation.Occurrence),
                invocation,
                result);

        internal void Materialize(ProcessRelationEvaluation evaluation, ProcessOperationResult result) =>
            Materialize(
                Key(AsyncOperationKind.RelationQuery, evaluation.Continuation, evaluation.Activation,
                    evaluation.Token, evaluation.Node, evaluation.Occurrence),
                evaluation,
                result);

        internal void Materialize(ProcessSignalTargetResolution resolution, ProcessSignalTargetResult result)
        {
            ArgumentNullException.ThrowIfNull(resolution);
            ArgumentNullException.ThrowIfNull(result);
            var key = Key(AsyncOperationKind.SignalTarget, resolution.Continuation, resolution.Activation,
                resolution.Token, resolution.Node, resolution.Occurrence);
            if (signalTargets.TryGetValue(key, out var retained))
            {
                if (retained.Resolution != resolution || retained.Result != result)
                    throw Conflict(key);
                return;
            }
            signalTargets.Add(key, new(resolution, result));
        }

        void Materialize(AsyncOperationKey key, object request, ProcessOperationResult result)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(result);
            if (operations.TryGetValue(key, out var retained))
            {
                if (!Equals(retained.Request, request) || retained.Result != result)
                    throw Conflict(key);
                return;
            }
            operations.Add(key, new(request, result));
        }

        static void RequireSame(object retained, object requested, AsyncOperationKey key)
        {
            if (!Equals(retained, requested))
            {
                throw new InvalidOperationException(
                    $"Process host occurrence '{Describe(key)}' changed during activation replay.");
            }
        }

        static InvalidOperationException Conflict(AsyncOperationKey key) => new(
            $"Process host occurrence '{Describe(key)}' was materialized with conflicting evidence.");
    }

    static AsyncOperationKey Key(
        AsyncOperationKind kind,
        ProcessContinuationIdentity continuation,
        ActivationId activation,
        TokenId token,
        ExecutionNodeId node,
        long occurrence) =>
        new(kind, continuation, activation, token, node, occurrence);

    static string Describe(AsyncOperationKey key) =>
        $"{key.Continuation.ProcessInstanceId.Value}/{key.Continuation.ProcessAttemptId.Value}/"
        + $"{key.Activation.Value}/{key.Token.Value}/{key.Node.Value}/{key.Occurrence}/{key.Kind}";

    enum AsyncOperationKind
    {
        Transition,
        RelationQuery,
        SignalTarget
    }

    readonly record struct AsyncOperationKey(
        AsyncOperationKind Kind,
        ProcessContinuationIdentity Continuation,
        ActivationId Activation,
        TokenId Token,
        ExecutionNodeId Node,
        long Occurrence);

    sealed record MaterializedOperation(object Request, ProcessOperationResult Result);

    sealed record MaterializedSignalTarget(
        ProcessSignalTargetResolution Resolution,
        ProcessSignalTargetResult Result);
}
