using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Machines;

namespace Cohesive.Storage.Processes;

/// <summary>Registers one reaction against existing durable Process and target infrastructure.</summary>
/// <remarks>Sources checkpoint after AdmitAsync confirms a discoverable Process start. Recovery workers
/// execute the retained activation independently; uncertain calls replay the same target operation identity.</remarks>
public sealed partial class MachineReactionHost
{
    readonly IProcessDurableStore store;
    readonly MachineReactionTarget target;
    readonly ProcessDurableRuntime runtime;
    readonly ProcessDurableRuntime recoveryRuntime;
    readonly MachineReactionRecoveryPolicy? recovery;
    readonly MachineReactionHost? prerequisite;
    readonly CompiledProcessPlan plan;
    readonly Func<OperationContext, EntityTransitionOperationReceipt, ValueTask<ProcessControlAuthorizationContext?>> authorize;

    /// <summary>Checks and binds one policy before observing source receipts.</summary>
    /// <param name="policy">Exact declarative policy.</param>
    /// <param name="store">Durable Process storage authority; its guarantees are checked by the existing runtime.</param>
    /// <param name="target">Exact, authorized target realization with replayable receipt resolution.</param>
    /// <param name="authorizeSource">Fresh source-resource and reaction-start authorization; null denies delivery.</param>
    /// <param name="options">Existing runtime worker, lease and bounded commit-retry policy.</param>
    /// <param name="recovery">Validated background recovery policy, or null for synchronous delivery only.</param>
    /// <param name="prerequisite">Exact prerequisite host over the same store and authority; required for coordinated policies.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Target revision does not match the policy, the store lacks discovery, or the prerequisite host has incompatible policy, store, authority or recovery authorization.</exception>
    /// <exception cref="InvalidOperationException">The policy cannot compile.</exception>
    public MachineReactionHost(MachineReaction policy, IProcessDurableStore store, MachineReactionTarget target,
        Func<OperationContext, EntityTransitionOperationReceipt, ValueTask<ProcessControlAuthorizationContext?>> authorizeSource,
        ProcessDurableRuntimeOptions options,
        MachineReactionRecoveryPolicy? recovery = null, MachineReactionHost? prerequisite = null)
    {
        this.recovery = recovery;
        this.prerequisite = prerequisite;
        ArgumentNullException.ThrowIfNull(store);
        if (store is not IProcessInstanceSource) throw new ArgumentException("Reaction admission requires discovery on its Process store.", nameof(store));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.store = store;
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        authorize = authorizeSource ?? throw new ArgumentNullException(nameof(authorizeSource));
        if (target.Transition != policy.Payload.Target.Reference) throw new ArgumentException("Target revision differs from the policy.", nameof(target));
        if (policy.Payload.Prerequisite?.Reference != prerequisite?.Policy.Document.Reference
            || prerequisite is not null && (!ReferenceEquals(prerequisite.Store, store) || prerequisite.Authority != target.Authority || !prerequisite.SupportsRecovery))
            throw new ArgumentException("A coordinated reaction requires its exact authorized prerequisite host on the same store and authority.", nameof(prerequisite));
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
    internal bool SupportsRecovery => recovery is not null;

    /// <summary>Authorizes and atomically retains starts without contacting the target.</summary>
    /// <param name="context">Trusted source-delivery context.</param>
    /// <param name="receipt">Validated immutable source receipt.</param>
    /// <returns>Durably discoverable reaction identities, or empty for unrelated scope or movement.</returns>
    /// <exception cref="UnauthorizedAccessException">Source/start permission is denied.</exception>
    /// <exception cref="InvalidOperationException">Admission is not confirmed.</exception>
    /// <exception cref="ArgumentNullException">The context or receipt is null.</exception>
    public async Task<ImmutableArray<ProcessInstanceId>> AdmitAsync(OperationContext context, EntityTransitionOperationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Request.AuthorityScope != Authority) return [];
        var results = ImmutableArray.CreateBuilder<ProcessInstanceId>();
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
            // Fresh permission was checked above; replay preserves the original start attribution.
            var replayIntent = new ProcessStartRequest(prepared.Start.SchemaVersion, prepared.Start.Definition,
                retained.Start.Context, prepared.Start.InitialContinuation, prepared.Start.Input, prepared.Start.Causation);
            if (!retained.Start.HasSameIdempotentIntent(replayIntent))
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
        var snapshot = await LoadOwnedAsync(context, instance).ConfigureAwait(false);
        if (snapshot is null) return null;
        return await RecoverSnapshotAsync(context, snapshot).ConfigureAwait(false);
    }

    internal async Task<MachineReactionProgress> RecoverSnapshotAsync(OperationContext context, ProcessDurableStoreSnapshot snapshot)
    {
        var instance = snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId;
        await AuthorizeRecoveryAsync(context, snapshot).ConfigureAwait(false);
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
            if (current.IsEmpty || (current.Length == attempts.Length && current[^1].Attempt == attempts[^1].Attempt)) throw;
            return await InspectSnapshot(context, retained).ConfigureAwait(false);
        }
    }

    bool CanDispatch(OperationContext context, ProcessDurableStoreSnapshot snapshot)
    {
        var attempts = ProcessTransitionAttempt.Read(snapshot);
        var last = attempts.LastOrDefault();
        return last is null || last.Result is not null || (last.Attempt.Failure?.Disposition != DurableOperationFailureDisposition.Terminal
            && attempts.Length < recovery!.MaximumAttempts
            && context.UtcNow - (last.Attempt.CompletedAtUtc ?? last.Attempt.Claim.ExpiresAtUtc) >= recovery!.RetryDelay);
    }

    async Task<MachineReactionProgress> Execute(OperationContext context, ProcessDurableStoreSnapshot snapshot,
        ProcessDurableRuntime? executionRuntime = null)
    {
        var retained = MachineReactionInvocation.Restore(Policy, snapshot.Checkpoint.Start.Request);
        if (snapshot.Checkpoint.Continuation.Terminal.Kind != ExecutionTerminalOutcomeKind.None)
            return await InspectSnapshot(context, snapshot).ConfigureAwait(false);
        if (prerequisite is not null && snapshot.Checkpoint.Continuation.Waits.Any(wait => wait.Active && wait.Node == MachineReaction.PrerequisiteNode))
        {
            var coordinated = await AdmitPrerequisiteAsync(context, snapshot, retained).ConfigureAwait(false);
            if (coordinated.Blocked is { } blocked) return blocked;
            snapshot = coordinated.Snapshot;
        }
        if (prerequisite is not null) _ = HasPrerequisiteProof(snapshot, retained);
        var inputs = snapshot.Checkpoint.Inbox.Where(entry => entry.Receipt is null).ToArray();
        var sequence = snapshot.Checkpoint.Continuation.CompletedActivationCount + 1;
        // Registration and signal consumption have separate stable activations; physical retries reuse each one.
        var activation = new ProcessActivation(new(retained.Start.InitialContinuation.ProcessInstanceId.Value + "/activation/" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            inputs.Length == 0 ? ProcessActivationCause.Start : ProcessActivationCause.Interaction,
            inputs.Length == 0 ? snapshot.Checkpoint.Start.AcceptedAtUtc : inputs.Max(entry => entry.AdmittedAtUtc),
            new(retained.Source.AuthorityScope, new(retained.Start.InitialContinuation.ProcessInstanceId.Value),
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), retained.Start.Context.Provenance),
            [.. inputs.Select(entry => entry.Input)]);
        var activated = await (executionRuntime ?? runtime).ActivateAsync(context, plan, retained.Start.InitialContinuation, activation).ConfigureAwait(false);
        if (activated.Snapshot is null && activated.Disposition == ProcessDurableRuntimeDisposition.Terminal)
            return await InspectSnapshot(context, await store.LoadAsync(context, retained.Start.InitialContinuation.ProcessInstanceId).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Admitted reaction disappeared.")).ConfigureAwait(false);
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
        var progress = await InspectOwnSnapshot(context, snapshot).ConfigureAwait(false);
        if (RequiresPrerequisiteEvidence(snapshot, progress))
        {
            var dependency = await InspectPrerequisiteAsync(context, MachineReactionInvocation.Restore(Policy, snapshot.Checkpoint.Start.Request)).ConfigureAwait(false);
            if (dependency.Status != MachineReactionStatus.Completed)
                progress = dependency with { Attempts = progress.Attempts, Prerequisite = progress.Prerequisite };
        }
        return progress;
    }

    bool RequiresPrerequisiteEvidence(ProcessDurableStoreSnapshot snapshot, MachineReactionProgress progress) =>
        progress.Status == MachineReactionStatus.Pending && prerequisite is not null && progress.Attempts.IsEmpty
        && !HasPrerequisiteProof(snapshot, MachineReactionInvocation.Restore(Policy, snapshot.Checkpoint.Start.Request));

    async Task<MachineReactionProgress> InspectOwnSnapshot(OperationContext context, ProcessDurableStoreSnapshot snapshot)
    {
        var invocation = MachineReactionInvocation.Restore(Policy, snapshot.Checkpoint.Start.Request);
        var attempts = ProcessTransitionAttempt.Read(snapshot);
        var progress = await InspectCheckpoint(context, invocation, snapshot.Checkpoint).ConfigureAwait(false);
        if (progress.Status == MachineReactionStatus.Pending && attempts.LastOrDefault() is { } last)
        {
            var key = ProcessTransitionAttempt.Operation(last.Invocation);
            progress = last.Attempt.Failure is { } failure ? invocation.InspectFailure(key, failure)
                : new(MachineReactionStatus.Indeterminate, key);
            if (attempts.Length >= (recovery?.MaximumAttempts ?? int.MaxValue) || last.Attempt.Failure?.Disposition == DurableOperationFailureDisposition.Terminal)
                progress = progress with { Status = progress.Status == MachineReactionStatus.Pending ? MachineReactionStatus.Failed : progress.Status,
                    Diagnostic = new("machine.reaction.recovery.stopped", DiagnosticSeverity.Warning,
                    "Automatic recovery stopped; retain this identity and investigate the target receipt before operator recovery.") };
        }
        if (snapshot.Checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.Cancelled
            && attempts.IsEmpty && snapshot.Checkpoint.Operations.IsEmpty)
            progress = new(MachineReactionStatus.Abandoned, Diagnostic: new("machine.reaction.abandoned", DiagnosticSeverity.Info,
                "Canonical Process cancellation stopped this reaction before target dispatch."));
        else if (snapshot.Checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.Cancelled
            && progress.Status is MachineReactionStatus.Pending or MachineReactionStatus.Indeterminate)
            progress = progress with
            {
                Status = progress.Status == MachineReactionStatus.Pending ? MachineReactionStatus.Failed : progress.Status,
                Diagnostic = new("machine.reaction.abandoned.targetEvidence", DiagnosticSeverity.Warning,
                    "Process execution was cancelled. Retained target evidence still governs commitment; cancellation does not undo effects or authorize a new request.")
            };
        return progress with { Attempts = attempts, Prerequisite = prerequisite is null ? null
            : MachineReactionInvocation.InstanceFor(prerequisite.Policy, invocation.Source, invocation.MovementNode) };
    }

    async Task<MachineReactionProgress> InspectCheckpoint(OperationContext context, MachineReactionInvocation invocation, ProcessDurableCheckpoint checkpoint)
    {
        var operation = checkpoint.Operations.SingleOrDefault(item => item.Key.Node == MachineReaction.InvocationNode
            && item.Key.Continuation == checkpoint.ContinuationIdentity);
        EntityTransitionOperationReceipt? receipt = null;
        if (operation?.Result.ReceiptReference is { } reference)
        {
            try { receipt = await target.ResolveAsync(context, EntityTransitionReceiptReferences.Read(reference)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException or OperationCanceledException))
            {
                if (target.DescribeFailure(exception).Disposition != DurableOperationFailureDisposition.Retryable) throw;
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
