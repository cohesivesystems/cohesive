using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Machines;

namespace Cohesive.Storage.Processes;

/// <summary>Registers one reaction against existing durable Process and target infrastructure.</summary>
/// <remarks>Sources checkpoint after AdmitAsync confirms a discoverable Process start. Recovery workers
/// execute the retained activation independently; uncertain calls replay the same target operation identity.</remarks>
public sealed class MachineReactionHost
{
    readonly IProcessDurableStore store;
    readonly MachineReactionTarget target;
    readonly ProcessDurableRuntime runtime;
    readonly ProcessDurableRuntime recoveryRuntime;
    readonly Func<OperationContext, ProcessStartRequest, ValueTask<bool>>? authorizeRecovery;
    readonly int maximumAttempts;
    readonly TimeSpan retryDelay;
    readonly CompiledProcessPlan plan;
    readonly Func<OperationContext, EntityTransitionOperationReceipt, ValueTask<ProcessControlAuthorizationContext?>> authorize;

    /// <summary>Checks and binds one policy before observing source receipts.</summary>
    /// <param name="policy">Exact declarative policy.</param>
    /// <param name="store">Durable Process storage authority; its guarantees are checked by the existing runtime.</param>
    /// <param name="target">Exact, authorized target realization with replayable receipt resolution.</param>
    /// <param name="authorizeSource">Fresh source-resource and reaction-start authorization; null denies delivery.</param>
    /// <param name="options">Existing runtime worker, lease and bounded commit-retry policy.</param>
    /// <param name="authorizeRecovery">Fresh permission to inspect and recover a retained start; required by recovery workers.</param>
    /// <param name="maximumAttempts">Positive bounded physical dispatch budget, default five.</param>
    /// <param name="retryDelay">Delay after an unsuccessful attempt, default thirty seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException">Retry budget or delay is invalid.</exception>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Target revision does not match the policy.</exception>
    /// <exception cref="InvalidOperationException">The policy cannot compile.</exception>
    public MachineReactionHost(MachineReaction policy, IProcessDurableStore store, MachineReactionTarget target,
        Func<OperationContext, EntityTransitionOperationReceipt, ValueTask<ProcessControlAuthorizationContext?>> authorizeSource,
        ProcessDurableRuntimeOptions options,
        Func<OperationContext, ProcessStartRequest, ValueTask<bool>>? authorizeRecovery = null,
        int maximumAttempts = 5, TimeSpan? retryDelay = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        this.maximumAttempts = maximumAttempts;
        this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
        if (this.retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        this.authorizeRecovery = authorizeRecovery;
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        authorize = authorizeSource ?? throw new ArgumentNullException(nameof(authorizeSource));
        if (target.Transition != policy.Payload.Target.Reference) throw new ArgumentException("Target revision differs from the policy.", nameof(target));
        var compiled = policy.Compile();
        plan = compiled.Plan ?? throw new InvalidOperationException(string.Join("; ", compiled.Validation.Diagnostics.Select(d => d.Message)));
        runtime = new(store, new NoOtherOperations(), options, transitionOperationAdapter: target);
        recoveryRuntime = new(store, new NoOtherOperations(), options, transitionOperationAdapter: target)
        { AdmitTransitionAttempt = CanDispatch };
    }

    /// <summary>Registered semantic policy; infrastructure cannot change its projections or guards.</summary>
    public MachineReaction Policy { get; }

    /// <summary>Exact authority and tenant routed to this host; other scopes are unrelated deliveries.</summary>
    public InteractionAuthorityScope Authority => target.Authority;

    internal IProcessDurableStore Store => store;

    /// <summary>Authorizes and atomically retains starts without contacting the target.</summary>
    /// <param name="context">Trusted source-delivery context.</param>
    /// <param name="receipt">Validated immutable source receipt.</param>
    /// <returns>Durably discoverable reaction identities, or empty for unrelated scope or movement.</returns>
    /// <exception cref="UnauthorizedAccessException">Source/start permission is denied.</exception>
    /// <exception cref="InvalidOperationException">The store lacks discovery or admission is not confirmed.</exception>
    /// <exception cref="ArgumentNullException">The context or receipt is null.</exception>
    public async Task<ImmutableArray<ProcessInstanceId>> AdmitAsync(OperationContext context, EntityTransitionOperationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Request.AuthorityScope != Authority) return [];
        var results = ImmutableArray.CreateBuilder<ProcessInstanceId>();
        if (store is not IProcessInstanceSource) throw new InvalidOperationException("Durable reaction admission requires discovery on its Process store.");
        foreach (var occurrence in EntityMachineOccurrence.FromReceipt(receipt))
        {
            if (!MachineReactionInvocation.Matches(Policy, occurrence)) continue;
            var authorization = await authorize(context, receipt).ConfigureAwait(false)
                ?? throw new UnauthorizedAccessException("Reaction source/start access is denied.");
            if (authorization.AuthorityScope != target.Authority) throw new UnauthorizedAccessException("Target and source authorities differ.");
            var prepared = MachineReactionInvocation.Prepare(Policy, occurrence, authorization, context.TimeProvider.GetUtcNow());
            if (prepared is null) continue;
            var snapshot = await store.LoadAsync(context, prepared.Start.InitialContinuation.ProcessInstanceId).ConfigureAwait(false);
            if (snapshot is null)
            {
                var admission = new ProcessStartReferenceEvaluator().Evaluate(prepared.Start,
                    ProcessStartRegistryEvidence.Empty, context.TimeProvider.GetUtcNow());
                var start = admission.Receipt ?? throw new InvalidOperationException("Reaction start was not admitted.");
                var initialized = await runtime.InitializeAsync(context, plan, start).ConfigureAwait(false);
                if (initialized.Disposition is not (ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed
                    or ProcessDurableRuntimeDisposition.IdentityConflict or ProcessDurableRuntimeDisposition.CommitOutcomeUnknown))
                    throw new InvalidOperationException($"Reaction admission failed: {initialized.Disposition}.");
                snapshot = initialized.Snapshot ?? await store.LoadAsync(context, prepared.Start.InitialContinuation.ProcessInstanceId).ConfigureAwait(false);
            }
            if (snapshot is null) throw new InvalidOperationException("Reaction admission remains indeterminate; replay this delivery.");
            var retained = MachineReactionInvocation.Restore(Policy, snapshot.Checkpoint.Start.Request);
            if (retained.Source != prepared.Source || retained.MovementNode != prepared.MovementNode || retained.Start.Input != prepared.Start.Input)
                throw new InvalidOperationException("Reaction identity is already retained for different source content.");
            results.Add(retained.Start.InitialContinuation.ProcessInstanceId);
        }
        return results.ToImmutable();
    }

    /// <summary>Admits and immediately executes a delivery for synchronous callers.</summary>
    /// <param name="context">Trusted caller context.</param>
    /// <param name="receipt">Validated immutable source receipt.</param>
    /// <returns>Conclusive target results; source adapters should instead use AdmitAsync.</returns>
    /// <exception cref="InvalidOperationException">Execution or target evidence is inconclusive.</exception>
    /// <exception cref="UnauthorizedAccessException">Source or target authorization is denied.</exception>
    public async Task<ImmutableArray<MachineReactionProgress>> DeliverAsync(OperationContext context, EntityTransitionOperationReceipt receipt)
    {
        var instances = await AdmitAsync(context, receipt).ConfigureAwait(false);
        var results = ImmutableArray.CreateBuilder<MachineReactionProgress>(instances.Length);
        foreach (var instance in instances)
        {
            var snapshot = await store.LoadAsync(context, instance).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Admitted reaction disappeared.");
            var progress = await Execute(context, snapshot).ConfigureAwait(false);
            if (progress.Status is not (MachineReactionStatus.Completed or MachineReactionStatus.Rejected))
                throw new InvalidOperationException("Target commitment remains inconclusive; recover the admitted instance.");
            results.Add(progress);
        }
        return results.MoveToImmutable();
    }

    /// <summary>Recovers one admitted instance with its original activation and exact target operation identity.</summary>
    /// <param name="context">Trusted worker identity and fresh clock.</param>
    /// <param name="instance">Discovered Process instance, not a permission grant.</param>
    /// <returns>Evidence-derived progress, or null for an absent or unrelated instance. Exhausted ambiguity stays indeterminate.</returns>
    /// <exception cref="UnauthorizedAccessException">Recovery authorization is missing or denied.</exception>
    /// <exception cref="InvalidOperationException">Retained evidence is inconsistent or storage cannot confirm progress.</exception>
    public async Task<MachineReactionProgress?> RecoverAsync(OperationContext context, ProcessInstanceId instance)
    {
        var snapshot = await store.LoadAsync(context, instance).ConfigureAwait(false);
        if (snapshot is null || snapshot.Checkpoint.Start.Request.Definition != Policy.Payload.Process.Reference
            || snapshot.Checkpoint.Start.Request.Context.Authorization.AuthorityScope != Authority) return null;
        return await RecoverSnapshotAsync(context, snapshot).ConfigureAwait(false);
    }

    internal async Task<MachineReactionProgress> RecoverSnapshotAsync(OperationContext context, ProcessDurableStoreSnapshot snapshot)
    {
        var instance = snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId;
        if (authorizeRecovery is null || !await authorizeRecovery(context, snapshot.Checkpoint.Start.Request).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Reaction recovery access is denied.");
        var attempts = ProcessTransitionAttempt.Read(snapshot);
        if (!CanDispatch(context, snapshot))
            return await InspectSnapshot(context, snapshot).ConfigureAwait(false);
        try { return await Execute(context, snapshot, recoveryRuntime).ConfigureAwait(false); }
        catch (Exception exception) when (!context.CancellationToken.IsCancellationRequested
            && exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            var retained = await store.LoadAsync(context, instance).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Admitted reaction disappeared.");
            var current = ProcessTransitionAttempt.Read(retained);
            if (exception is not ProcessDurableRuntime.TransitionAttemptDeferredException
                && (current.IsEmpty || (current.Length == attempts.Length && current[^1].Attempt == attempts[^1].Attempt))) throw;
            return await InspectSnapshot(context, retained).ConfigureAwait(false);
        }
    }

    bool CanDispatch(OperationContext context, ProcessDurableStoreSnapshot snapshot)
    {
        var attempts = ProcessTransitionAttempt.Read(snapshot);
        var last = attempts.LastOrDefault();
        return last is null || last.Result is not null || (last.Attempt.Failure?.Disposition != DurableOperationFailureDisposition.Terminal
            && attempts.Length < maximumAttempts
            && context.UtcNow >= (last.Attempt.CompletedAtUtc ?? last.Attempt.Claim.ExpiresAtUtc) + retryDelay);
    }

    async Task<MachineReactionProgress> Execute(OperationContext context, ProcessDurableStoreSnapshot snapshot,
        ProcessDurableRuntime? executionRuntime = null)
    {
        var retained = MachineReactionInvocation.Restore(Policy, snapshot.Checkpoint.Start.Request);
        var activation = new ProcessActivation(new(retained.Start.InitialContinuation.ProcessInstanceId.Value + "/activation/1"),
            ProcessActivationCause.Start, snapshot.Checkpoint.Start.AcceptedAtUtc,
            new(retained.Source.AuthorityScope, new(retained.Start.InitialContinuation.ProcessInstanceId.Value),
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), retained.Start.Context.Provenance));
        var activated = await (executionRuntime ?? runtime).ActivateAsync(context, plan, retained.Start.InitialContinuation, activation).ConfigureAwait(false);
        if (activated.Snapshot is null) throw new InvalidOperationException($"Reaction recovery failed: {activated.Disposition}.");
        return await InspectSnapshot(context, activated.Snapshot).ConfigureAwait(false);
    }

    /// <summary>Inspects a retained reaction through a caller-authorized Process store and target resolver.</summary>
    /// <param name="context">Trusted authorized inspection context.</param>
    /// <param name="instance">Reaction instance locator; not an authorization grant.</param>
    /// <returns>Evidence-derived progress, or null when no retained start exists.</returns>
    /// <exception cref="InvalidOperationException">The stored start belongs to another policy.</exception>
    /// <remarks>The caller must authorize access to this Process instance before calling; target receipt access is additionally checked.</remarks>
    public async Task<MachineReactionProgress?> InspectAsync(OperationContext context, ProcessInstanceId instance)
    {
        var snapshot = await store.LoadAsync(context, instance).ConfigureAwait(false);
        if (snapshot is null) return null;
        return await InspectSnapshot(context, snapshot).ConfigureAwait(false);
    }

    async Task<MachineReactionProgress> InspectSnapshot(OperationContext context, ProcessDurableStoreSnapshot snapshot)
    {
        var invocation = MachineReactionInvocation.Restore(Policy, snapshot.Checkpoint.Start.Request);
        var attempts = ProcessTransitionAttempt.Read(snapshot);
        var progress = await InspectCheckpoint(context, invocation, snapshot.Checkpoint).ConfigureAwait(false);
        if (progress.Status == MachineReactionStatus.Pending && attempts.LastOrDefault() is { } last)
        {
            var key = new ProcessOperationOccurrence(last.Invocation.Continuation, last.Invocation.Activation,
                last.Invocation.Token, last.Invocation.Node, last.Invocation.Occurrence);
            progress = last.Attempt.Failure is { } failure ? invocation.InspectFailure(key, failure)
                : new(MachineReactionStatus.Indeterminate, key);
            if (attempts.Length >= maximumAttempts || last.Attempt.Failure?.Disposition == DurableOperationFailureDisposition.Terminal)
                progress = progress with { Status = progress.Status == MachineReactionStatus.Pending ? MachineReactionStatus.Failed : progress.Status,
                    Diagnostic = new("machine.reaction.recovery.stopped", DiagnosticSeverity.Warning,
                    "Automatic recovery stopped; retain this identity and investigate the target receipt before operator recovery.") };
        }
        return progress with { Attempts = attempts };
    }

    async Task<MachineReactionProgress> InspectCheckpoint(OperationContext context, MachineReactionInvocation invocation, ProcessDurableCheckpoint checkpoint)
    {
        var operation = checkpoint.Operations.SingleOrDefault(item => item.Key.Node == MachineReaction.InvocationNode
            && item.Key.Continuation == checkpoint.ContinuationIdentity);
        EntityTransitionOperationReceipt? receipt = null;
        if (operation?.Result.ReceiptReference is { } reference)
        {
            try { receipt = await target.ResolveAsync(context, EntityTransitionReceiptReferences.Read(reference)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or TimeoutException
                || exception is System.Net.Http.HttpRequestException http && http.StatusCode is not
                    (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Conflict))
            {
                return invocation.Inspect(checkpoint) with { Diagnostic = new("machine.reaction.receipt.unavailable", DiagnosticSeverity.Warning,
                    "The retained target operation receipt is temporarily unavailable; commitment remains unconfirmed.") };
            }
        }
        return invocation.Inspect(checkpoint, receipt);
    }

    sealed class NoOtherOperations : IProcessReferenceHost
    {
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => throw new NotSupportedException("Use the registered target adapter.");
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) => throw new NotSupportedException("Reaction profile has no relation nodes.");
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new NotSupportedException("Reaction profile has no signal nodes.");
    }
}
