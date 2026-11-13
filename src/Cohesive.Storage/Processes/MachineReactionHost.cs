using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Machines;

namespace Cohesive.Storage.Processes;

/// <summary>Registers one reaction against existing durable Process and target infrastructure.</summary>
/// <remarks>Delivery is backpressured through conclusive target evidence. A source must checkpoint only after
/// DeliverAsync succeeds. Failed or ambiguous delivery is retried with the same receipt, never a new operation.</remarks>
public sealed class MachineReactionHost
{
    readonly IProcessDurableStore store;
    readonly MachineReactionTarget target;
    readonly ProcessDurableRuntime runtime;
    readonly CompiledProcessPlan plan;
    readonly Func<OperationContext, EntityTransitionOperationReceipt, ValueTask<ProcessControlAuthorizationContext?>> authorize;

    /// <summary>Checks and binds one policy before observing source receipts.</summary>
    /// <param name="policy">Exact declarative policy.</param>
    /// <param name="store">Durable Process storage authority; its guarantees are checked by the existing runtime.</param>
    /// <param name="target">Exact, authorized target realization with replayable receipt resolution.</param>
    /// <param name="authorizeSource">Fresh source-resource and reaction-start authorization; null denies delivery.</param>
    /// <param name="options">Existing runtime worker, lease and bounded commit-retry policy.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Target revision does not match the policy.</exception>
    /// <exception cref="InvalidOperationException">The policy cannot compile.</exception>
    public MachineReactionHost(MachineReaction policy, IProcessDurableStore store, MachineReactionTarget target,
        Func<OperationContext, EntityTransitionOperationReceipt, ValueTask<ProcessControlAuthorizationContext?>> authorizeSource,
        ProcessDurableRuntimeOptions options)
    {
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.target = target ?? throw new ArgumentNullException(nameof(target));
        authorize = authorizeSource ?? throw new ArgumentNullException(nameof(authorizeSource));
        if (target.Transition != policy.Payload.Target.Reference) throw new ArgumentException("Target revision differs from the policy.", nameof(target));
        var compiled = policy.Compile();
        plan = compiled.Plan ?? throw new InvalidOperationException(string.Join("; ", compiled.Validation.Diagnostics.Select(d => d.Message)));
        runtime = new(store, new NoOtherOperations(), options, transitionOperationAdapter: target);
    }

    /// <summary>Registered semantic policy; infrastructure cannot change its projections or guards.</summary>
    public MachineReaction Policy { get; }

    /// <summary>Exact authority and tenant routed to this host; other scopes are unrelated deliveries.</summary>
    public InteractionAuthorityScope Authority => target.Authority;

    /// <summary>Durably admits and realizes matching movements from one trusted committed source receipt.</summary>
    /// <param name="context">Trusted identity, physical clock and cancellation.</param>
    /// <param name="receipt">Validated source receipt from the declared observation authority.</param>
    /// <returns>Conclusive Completed/Rejected results, or empty for another authority, tenant or policy occurrence.</returns>
    /// <exception cref="UnauthorizedAccessException">Source, reaction start, target or receipt access is denied.</exception>
    /// <exception cref="InvalidOperationException">Admission conflicts, execution is blocked, or conclusive target evidence is unavailable.</exception>
    /// <exception cref="OperationCanceledException">Delivery is cancelled; the source must not checkpoint it.</exception>
    public async Task<ImmutableArray<MachineReactionProgress>> DeliverAsync(OperationContext context, EntityTransitionOperationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Request.AuthorityScope != Authority) return [];
        var results = ImmutableArray.CreateBuilder<MachineReactionProgress>();
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
            var activation = new ProcessActivation(new(retained.Start.InitialContinuation.ProcessInstanceId.Value + "/activation/1"),
                ProcessActivationCause.Start, snapshot.Checkpoint.Start.AcceptedAtUtc,
                new(retained.Source.AuthorityScope, new(retained.Start.InitialContinuation.ProcessInstanceId.Value),
                    new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), retained.Start.Context.Provenance));
            var activated = await runtime.ActivateAsync(context, plan, retained.Start.InitialContinuation, activation).ConfigureAwait(false);
            if (activated.Disposition is not (ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed)
                || activated.Snapshot is null)
                throw new InvalidOperationException($"Reaction execution is not conclusive: {activated.Disposition}. Replay the source receipt.");
            var progress = await InspectCheckpoint(context, retained, activated.Snapshot.Checkpoint).ConfigureAwait(false);
            if (progress.Status is not (MachineReactionStatus.Completed or MachineReactionStatus.Rejected))
                throw new InvalidOperationException("Reaction target commitment remains indeterminate. Do not checkpoint this source delivery.");
            results.Add(progress);
        }
        return results.ToImmutable();
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
        return await InspectCheckpoint(context, MachineReactionInvocation.Restore(Policy, snapshot.Checkpoint.Start.Request), snapshot.Checkpoint).ConfigureAwait(false);
    }

    async Task<MachineReactionProgress> InspectCheckpoint(OperationContext context, MachineReactionInvocation invocation, ProcessDurableCheckpoint checkpoint)
    {
        var operation = checkpoint.Operations.SingleOrDefault(item => item.Key.Node == MachineReaction.InvocationNode
            && item.Key.Continuation == checkpoint.ContinuationIdentity);
        var receipt = operation?.Result.ReceiptReference is { } reference
            ? await target.ResolveAsync(context, EntityTransitionReceiptReferences.Read(reference)).ConfigureAwait(false) : null;
        return invocation.Inspect(checkpoint, receipt);
    }

    sealed class NoOtherOperations : IProcessReferenceHost
    {
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => throw new NotSupportedException("Use the registered target adapter.");
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) => throw new NotSupportedException("Reaction profile has no relation nodes.");
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new NotSupportedException("Reaction profile has no signal nodes.");
    }
}
