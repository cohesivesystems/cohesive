using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Model;
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
        Realization = Realize(plan);
        var validation = Diagnostics(Realization);
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
    public static DocumentValidationResult Validate(CompiledProcessPlan plan) => Diagnostics(Realize(plan));

    /// <summary>Compiler-owned capability evidence for this exact plan and its ephemeral operating boundaries.</summary>
    public ProcessInterpreterRealizationReport Realization { get; }

    static readonly Lazy<ProcessInterpreterCapabilityProfile> Profile = new(() =>
    {
        var evidence = new List<ProcessInterpreterCapabilityEvidence>();
        foreach (var wireName in new[] { ProcessWireNames.InvokeTransitionNode, ProcessWireNames.EvaluateRelationNode,
            ProcessWireNames.ChoiceNode, ProcessWireNames.MatchNode, ProcessWireNames.ReturnNode, ProcessWireNames.FailNode })
            evidence.Add(new(new($"ephemeral/construct/{wireName}"), ProcessInterpreterRequirementKey.ForConstruct(wireName), CapabilityRealizationKind.Native));
        foreach (var key in new[] { ProcessInterpreterGuarantees.ExactDefinitionPinning,
            ProcessInterpreterGuarantees.StableExecutionIdentity, ProcessInterpreterGuarantees.StatusTraceAndExplain })
            evidence.Add(new(new($"ephemeral/{key.Name}"), key, CapabilityRealizationKind.Native));
        evidence.Add(new(new("ephemeral/replay"), ProcessInterpreterGuarantees.DeterministicReplay,
            CapabilityRealizationKind.Constrained, operatingBoundaries: [new("activation-local-materialization/no-restart")]));
        evidence.Add(new(new("ephemeral/effects"), ProcessInterpreterGuarantees.ExternalEffectDelivery,
            CapabilityRealizationKind.Constrained, operatingBoundaries: [new("host/no-interaction-emissions"), new("invocation/no-automatic-retry")]));
        evidence.Add(new(new("ephemeral/payloads"), ProcessInterpreterGuarantees.SensitiveAndOversizedPayloads,
            CapabilityRealizationKind.Constrained, operatingBoundaries: [new("invocation/protected-in-memory-evidence")]));
        return new(new("cohesive.processes/ephemeral/v1"), new("cohesive.processes/ephemeral"), [.. evidence]);
    });

    static ProcessInterpreterRealizationReport Realize(CompiledProcessPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ProcessInterpreterRealizationCompiler.Compile(plan, Profile.Value, ProcessExecutionLifetime.Ephemeral);
    }

    static DocumentValidationResult Diagnostics(ProcessInterpreterRealizationReport report) => new(
        [.. report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d =>
            new DocumentValidationDiagnostic(
                d.Requirement == ProcessInterpreterGuarantees.WholeDefinitionAtomicity ? "processes.ephemeral.atomicScopeUnsupported"
                    : d.Requirement?.Category == ProcessInterpreterRequirementCategory.Construct ? "processes.ephemeral.constructUnsupported"
                    : "processes.ephemeral.capabilityUnsupported",
                d.Severity, d.Message, "/realization"))]);

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
    /// <exception cref="EphemeralProcessExecutionException">Host execution or canonical evidence validation fails; earlier outcomes are retained.</exception>
    public async ValueTask<EphemeralProcessResult> ExecuteAsync(OperationContext context,
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
        try
        {
            var decision = await ProcessReferenceInterpreter.ActivateAsync(scoped, Plan, state, activation, observedHost).ConfigureAwait(false);
            if (!decision.Emissions.IsEmpty)
                throw new InvalidOperationException("The ephemeral host produced interactions without a qualified delivery boundary; writes may already have committed.");
            if (decision.Disposition is not (ProcessActivationDisposition.Completed or ProcessActivationDisposition.Failed
                or ProcessActivationDisposition.Cancelled or ProcessActivationDisposition.Rejected))
                throw new InvalidOperationException("Ephemeral execution did not terminate; no background continuation was admitted.");
            return new(decision, Snapshot());
        }
        catch (OperationCanceledException exception)
        {
            throw new EphemeralProcessInterruptedException(Snapshot(), exception);
        }
        catch (Exception exception)
        {
            throw new EphemeralProcessExecutionException(Snapshot(), exception);
        }

        EphemeralProcessEvidence Snapshot() => new(Plan.DefinitionReference, continuation,
            observedHost.Results.ToImmutableDictionary(), observedHost.InFlight);
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


/// <summary>Invocation-local host evidence shared by terminal results and physical interruptions.</summary>
/// <param name="Definition">Exact Process authority.</param>
/// <param name="Continuation">Invocation identity; not a durable checkpoint.</param>
/// <param name="CompletedOperations">Returned canonical outcomes, including authoritative receipt locators.</param>
/// <param name="InterruptedOperation">Host call entered without returned evidence; effects may be uncertain.</param>
/// <remarks>This evidence may contain private data. It requires resource authorization and must not be exported
/// as telemetry labels or a public exception payload. Missing evidence never proves absence of a commit.</remarks>
public sealed record EphemeralProcessEvidence(ExecutionDefinitionReference Definition,
    ProcessContinuationIdentity Continuation, ImmutableDictionary<ExecutionNodeId, ProcessOperationResult> CompletedOperations,
    ExecutionNodeId? InterruptedOperation);

/// <summary>A canonical terminal decision with the host evidence needed to reconcile earlier effects.</summary>
/// <param name="Decision">Native Process decision, including its public output and trace.</param>
/// <param name="Evidence">Returned host outcomes, including receipts even when a later step failed.</param>
public sealed record EphemeralProcessResult(ProcessActivationDecision Decision, EphemeralProcessEvidence Evidence);

/// <summary>Cancellation of an ephemeral attempt; does not imply rollback or safe replay.</summary>
public sealed class EphemeralProcessInterruptedException : OperationCanceledException
{
    internal EphemeralProcessInterruptedException(EphemeralProcessEvidence evidence, OperationCanceledException cause)
        : base("Ephemeral execution was interrupted; inspect operation evidence before deciding whether any mutation can be retried.", cause, cause.CancellationToken) => Evidence = evidence;

    /// <summary>Protected invocation evidence; missing outcomes may represent uncertain effects.</summary>
    public EphemeralProcessEvidence Evidence { get; }
}

/// <summary>A physical execution failure retaining earlier host outcomes without claiming rollback.</summary>
public sealed class EphemeralProcessExecutionException : Exception
{
    internal EphemeralProcessExecutionException(EphemeralProcessEvidence evidence, Exception cause)
        : base("Ephemeral execution failed; inspect operation evidence before deciding whether any mutation can be retried.", cause) => Evidence = evidence;

    /// <summary>Protected invocation evidence, including any returned authoritative commit locators.</summary>
    public EphemeralProcessEvidence Evidence { get; }
}
