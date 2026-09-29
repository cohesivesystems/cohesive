using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;

namespace Cohesive.Processes.Execution;

/// <summary>Prepared, invocation-local execution of a finite Process through the canonical interpreter.</summary>
/// <remarks>
/// The executor retains only immutable preparation. Each invocation owns its continuation and host evidence;
/// there is no scheduler, checkpoint store, retry, or recovery after interruption. Entity writes performed by
/// the host are real writes and are not rolled back by cancellation or a later failure. Hosts must enforce
/// authorization and conditional commits and must not produce interaction emissions. Whole-definition atomicity
/// requires a different, qualified realization and is rejected here before invoking a host.
/// </remarks>
public sealed class EphemeralProcessExecutor
{
    /// <summary>Admits a plan once, without resolving or invoking any physical operation.</summary>
    /// <exception cref="ArgumentNullException">The plan is null.</exception>
    /// <exception cref="ArgumentException">The plan needs an unsupported construct or atomic scope.</exception>
    public EphemeralProcessExecutor(CompiledProcessPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var validation = Validate(plan);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join("; ", validation.Diagnostics.Select(d => $"{d.Code}: {d.Message}")), nameof(plan));
        Plan = plan;
    }

    /// <summary>Exact immutable Process authority, shared across invocations.</summary>
    public CompiledProcessPlan Plan { get; }

    /// <summary>Explains unsupported demands before any input, repository, or host is evaluated.</summary>
    /// <remarks>This target supports sequential host operations and finite branch selection. Durable boundaries,
    /// interactions, child work and parallel admission require another realization. Per-operation persistence
    /// guarantees remain the host's responsibility; successful validation does not establish ACID semantics.</remarks>
    /// <exception cref="ArgumentNullException">The plan is null.</exception>
    public static DocumentValidationResult Validate(CompiledProcessPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var diagnostics = new List<DocumentValidationDiagnostic>();
        if (plan.Options.AtomicScope != ProcessAtomicScopeDemand.None)
            diagnostics.Add(new("processes.ephemeral.atomicScopeUnsupported", DiagnosticSeverity.Error,
                "This executor cannot guarantee whole-definition atomicity.", "/options/atomicScope"));
        for (var index = 0; index < plan.Definition.Nodes.Length; index++)
        {
            var node = plan.Definition.Nodes[index];
            if (node is not (InvokeTransitionProcessNode or EvaluateRelationProcessNode
                or ChoiceProcessNode or MatchProcessNode or ReturnProcessNode or FailProcessNode))
                diagnostics.Add(new("processes.ephemeral.constructUnsupported", DiagnosticSeverity.Error,
                    $"Node '{node.Id.Value}' requires a realization beyond sequential ephemeral execution.",
                    $"/definition/nodes/{index}"));
        }
        return new([.. diagnostics]);
    }

    /// <summary>Executes one fresh attempt with a cooperative deadline and no automatic continuation or retry.</summary>
    /// <param name="context">Invocation identity, time provider, and caller cancellation.</param>
    /// <param name="continuation">Fresh invocation identity; reusing it does not provide deduplication.</param>
    /// <param name="input">Input satisfying the exact Process contract.</param>
    /// <param name="activationContext">Explicit authority, lineage and activation-local delivery policy.</param>
    /// <param name="host">Invocation-scoped physical operations; must honor cancellation and emit no interactions.</param>
    /// <param name="timeout">Positive finite cancellation budget, measured using the context's time provider.</param>
    /// <returns>Canonical terminal decision, including output and ordered execution evidence.</returns>
    /// <remarks>The deadline requests cancellation; it cannot forcibly interrupt an uncooperative host. This method
    /// waits for host quiescence rather than abandon a potentially writing task. Cancellation or an exception can
    /// follow committed writes and must never be interpreted as rollback or permission to retry the mutation.</remarks>
    /// <exception cref="ArgumentException">Input, invocation identity, or delivery policy is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Timeout is not positive and representable by a timer.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation or deadline is observed; writes may have committed.</exception>
    /// <exception cref="InvalidOperationException">A host emits interactions or execution does not terminate.</exception>
    public async ValueTask<ProcessActivationDecision> ExecuteAsync(OperationContext context,
        ProcessContinuationIdentity continuation, PortableValue input, ProcessActivationContext activationContext,
        IAsyncProcessReferenceHost host, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(activationContext);
        ArgumentNullException.ThrowIfNull(host);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (activationContext.Delivery.Durability != InteractionDurabilityDemand.ActivationLocal)
            throw new ArgumentException("Ephemeral execution requires activation-local delivery.", nameof(activationContext));
        context.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(timeout, context.TimeProvider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, deadline.Token);
        var scoped = context.WithCancellationToken(cancellation.Token);
        var state = ProcessReferenceInterpreter.Create(Plan, continuation, input);
        var activation = new ProcessActivation(new("ephemeral/0"), ProcessActivationCause.Start,
            context.UtcNow, activationContext);
        var observedHost = new ObservedHost(host);
        ProcessActivationDecision decision;
        try
        {
            decision = await ProcessReferenceInterpreter.ActivateAsync(scoped, Plan, state, activation, observedHost).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new EphemeralProcessInterruptedException(Plan.DefinitionReference, continuation,
                observedHost.Results.ToImmutableDictionary(), observedHost.InFlight, exception);
        }
        if (!decision.Emissions.IsEmpty)
            throw new InvalidOperationException("The ephemeral host produced interactions without a qualified delivery boundary; writes may already have committed.");
        if (decision.Disposition is not (ProcessActivationDisposition.Completed or ProcessActivationDisposition.Failed
            or ProcessActivationDisposition.Cancelled or ProcessActivationDisposition.Rejected))
            throw new InvalidOperationException("Ephemeral execution did not terminate; no background continuation was admitted.");
        return decision;
    }

    sealed class ObservedHost(IAsyncProcessReferenceHost inner) : IAsyncProcessReferenceHost
    {
        internal Dictionary<ExecutionNodeId, ProcessOperationResult> Results { get; } = [];
        internal ExecutionNodeId? InFlight { get; private set; }

        public async ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation)
        {
            InFlight = invocation.Node;
            return Observe(invocation.Node, await inner.InvokeTransitionAsync(context, invocation).ConfigureAwait(false));
        }

        public async ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation)
        {
            InFlight = evaluation.Node;
            return Observe(evaluation.Node, await inner.EvaluateRelationAsync(context, evaluation).ConfigureAwait(false));
        }

        ProcessOperationResult Observe(ExecutionNodeId node, ProcessOperationResult result)
        {
            if (result is null || !result.IsValidOutcome())
                throw new InvalidOperationException("The ephemeral host returned invalid operation evidence.");
            Results.Add(node, result);
            InFlight = null;
            if (!result.Emissions.IsEmpty)
                throw new InvalidOperationException("The ephemeral host produced interactions without a qualified delivery boundary; writes may already have committed.");
            return result;
        }

        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context,
            ProcessSignalTargetResolution resolution) => throw new InvalidOperationException("Signals are not admitted by this realization.");
    }

}


/// <summary>Cancellation evidence for an interrupted ephemeral attempt, without a rollback or replay claim.</summary>
/// <remarks>Returned operation values and receipt references can contain private data. They are invocation-scoped
/// evidence for authorized reconciliation, not safe telemetry labels or public exception payloads. An operation
/// without returned evidence may have committed. This exception cannot establish a negative commit claim.</remarks>
public sealed class EphemeralProcessInterruptedException : OperationCanceledException
{
    internal EphemeralProcessInterruptedException(ExecutionDefinitionReference definition,
        ProcessContinuationIdentity continuation, ImmutableDictionary<ExecutionNodeId, ProcessOperationResult> completedOperations,
        ExecutionNodeId? interruptedOperation, OperationCanceledException cause)
        : base("Ephemeral execution was interrupted; inspect operation evidence before deciding whether any mutation can be retried.", cause, cause.CancellationToken)
    {
        Definition = definition;
        Continuation = continuation;
        CompletedOperations = completedOperations;
        InterruptedOperation = interruptedOperation;
    }

    /// <summary>Exact Process authority for the observed attempt.</summary>
    public ExecutionDefinitionReference Definition { get; }
    /// <summary>Invocation identity; does not imply a durable checkpoint exists.</summary>
    public ProcessContinuationIdentity Continuation { get; }
    /// <summary>Canonical host outcomes returned before interruption, including any authoritative receipt locators.</summary>
    public ImmutableDictionary<ExecutionNodeId, ProcessOperationResult> CompletedOperations { get; }
    /// <summary>Host operation entered without returning evidence; its effects may be uncertain.</summary>
    public ExecutionNodeId? InterruptedOperation { get; }
}
