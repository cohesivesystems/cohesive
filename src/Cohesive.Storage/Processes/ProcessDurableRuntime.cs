using System.Collections.Concurrent;
using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>
/// Storage-owned durable driver that composes canonical Process, control, and operation authorities.
/// </summary>
/// <remarks>
/// Each finite activation is interpreted synchronously in memory and committed once at an invariant-preserving
/// safe point. The runtime never persists an incomplete activation descriptor. Callers recovering uncommitted
/// work must therefore resupply the exact stable <see cref="ProcessActivation"/> request.
/// When an <see cref="IProcessTransitionOperationAdapter"/> is supplied, an unmaterialized Transition suspends the
/// finite interpretation while the adapter commits or replays the entity-side operation. Interpretation then
/// restarts against the same activation-local observation cache, and the resulting receipt, continuation, and
/// canonical emissions enter the Process aggregate in one commit.
/// A runtime may process different Process instances and operations concurrently. Supplied stores, hosts,
/// resolvers, planners, classifiers, and adapters must therefore support concurrent calls, or the runtime must be
/// scoped so that their documented concurrency boundary is not exceeded.
/// </remarks>
public sealed partial class ProcessDurableRuntime
{
    readonly IProcessDurableStore store;
    readonly IProcessReferenceHost host;
    readonly IProcessDurableRequestBindingResolver bindingResolver;
    readonly IProcessLocalMutationPlanner localMutationPlanner;
    readonly IProcessStoreMutationExceptionClassifier storeMutationExceptionClassifier;
    readonly IProcessDurableOperationAdapterResolver operationAdapterResolver;
    readonly IProcessOperationExceptionClassifier operationExceptionClassifier;
    readonly IProcessTransitionOperationAdapter? transitionOperationAdapter;
    readonly ProcessDurableRuntimeOptions options;
    readonly ConcurrentDictionary<ProcessInstanceId, SemaphoreSlim> instanceGates = [];

    /// <summary>Creates a durable Process driver over explicit physical and interpretation ports.</summary>
    /// <param name="store">Atomic Process aggregate durability provider.</param>
    /// <param name="host">Synchronous evidence host used by the pure Process reference interpreter.</param>
    /// <param name="options">Worker identity, lease, and bounded exact-retry policy.</param>
    /// <param name="bindingResolver">Optional exact durable Request binding resolver.</param>
    /// <param name="localMutationPlanner">Optional deterministic local mutation planner.</param>
    /// <param name="storeMutationExceptionClassifier">Optional provider-aware ambiguous store-mutation classifier.</param>
    /// <param name="operationAdapterResolver">Optional exact durable Request adapter resolver.</param>
    /// <param name="operationExceptionClassifier">Optional provider-aware adapter exception classifier.</param>
    /// <param name="transitionOperationAdapter">
    /// Optional asynchronous entity Transition operation adapter. When absent, Transition calls use
    /// <paramref name="host"/> directly.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="store"/>, <paramref name="host"/>, or <paramref name="options"/> is
    /// <see langword="null"/>.
    /// </exception>
    public ProcessDurableRuntime(
        IProcessDurableStore store,
        IProcessReferenceHost host,
        ProcessDurableRuntimeOptions options,
        IProcessDurableRequestBindingResolver? bindingResolver = null,
        IProcessLocalMutationPlanner? localMutationPlanner = null,
        IProcessStoreMutationExceptionClassifier? storeMutationExceptionClassifier = null,
        IProcessDurableOperationAdapterResolver? operationAdapterResolver = null,
        IProcessOperationExceptionClassifier? operationExceptionClassifier = null,
        IProcessTransitionOperationAdapter? transitionOperationAdapter = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.bindingResolver = bindingResolver ?? EmptyProcessDurableRequestBindingResolver.Instance;
        this.localMutationPlanner = localMutationPlanner ?? EmptyProcessLocalMutationPlanner.Instance;
        this.storeMutationExceptionClassifier = storeMutationExceptionClassifier
            ?? ConservativeProcessStoreMutationExceptionClassifier.Instance;
        this.operationAdapterResolver = operationAdapterResolver
            ?? EmptyProcessDurableOperationAdapterResolver.Instance;
        this.operationExceptionClassifier = operationExceptionClassifier
            ?? ConservativeProcessOperationExceptionClassifier.Instance;
        this.transitionOperationAdapter = transitionOperationAdapter;
    }

    /// <summary>Creates the initial clean continuation and control state as one durable aggregate.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="plan">Exact compiled Process definition selected for this instance.</param>
    /// <param name="start">Previously accepted canonical Process start evidence.</param>
    /// <returns>Initialization, exact replay, conflict, or compatibility evidence.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before a physical boundary.</exception>
    public async Task<ProcessDurableInitializationResult> InitializeAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessStartReceipt start)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(start);
        context.ThrowIfCancellationRequested();
        var capabilityDiagnostics = ValidateCapabilities();
        if (!capabilityDiagnostics.IsEmpty)
        {
            return new(ProcessDurableRuntimeDisposition.Incompatible, diagnostics: capabilityDiagnostics);
        }

        var continuation = ProcessReferenceInterpreter.Create(plan, start);
        var checkpoint = new ProcessDurableCheckpoint(
            ProcessDurableCheckpoint.CurrentSchemaVersion,
            start,
            continuation,
            start.CreateInitialState(),
            createdAtUtc: start.AcceptedAtUtc,
            updatedAtUtc: start.AcceptedAtUtc);
        var compatibility = ProcessCheckpointCompatibilityValidator.Validate(plan, checkpoint);
        if (!compatibility.IsValid)
        {
            return new(
                ProcessDurableRuntimeDisposition.Incompatible,
                diagnostics: compatibility.Diagnostics);
        }

        var gate = instanceGates.GetOrAdd(checkpoint.ContinuationIdentity.ProcessInstanceId, static _ => new(1, 1));
        await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            var result = await InitializeExactAsync(
                    context,
                    ProcessDurableRuntimeIdentities.Initialization(start),
                    checkpoint)
                .ConfigureAwait(false);
            if (result is null)
            {
                return new(ProcessDurableRuntimeDisposition.CommitOutcomeUnknown);
            }

            var disposition = result.Disposition switch
            {
                ProcessStoreMutationDisposition.Applied => ProcessDurableRuntimeDisposition.Applied,
                ProcessStoreMutationDisposition.Replayed => ProcessDurableRuntimeDisposition.Replayed,
                ProcessStoreMutationDisposition.AlreadyExists => ProcessDurableRuntimeDisposition.IdentityConflict,
                ProcessStoreMutationDisposition.IdentityConflict => ProcessDurableRuntimeDisposition.IdentityConflict,
                _ => MapStoreDisposition(result.Disposition)
            };
            var snapshot = result.Snapshot;
            if (disposition is ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed)
            {
                snapshot = await store.LoadAsync(
                        context,
                        checkpoint.ContinuationIdentity.ProcessInstanceId)
                    .ConfigureAwait(false);
            }
            return new(disposition, snapshot);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Restores, fences, interprets, and atomically commits one finite Process activation.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="plan">Exact compiled Process definition selected for recovery and execution.</param>
    /// <param name="expectedContinuation">
    /// Exact logical Process instance and attempt the caller intends to activate.
    /// </param>
    /// <param name="activation">Exact caller-owned activation identity and semantic observations.</param>
    /// <returns>
    /// A committed decision, exact replay, compatibility rejection, lifecycle block, fencing conflict, or exact
    /// unresolved commit intent.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/>, <paramref name="plan"/>, or <paramref name="activation"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="expectedContinuation"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before a physical boundary.</exception>
    public async Task<ProcessDurableActivationResult> ActivateAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessContinuationIdentity expectedContinuation,
        ProcessActivation activation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(expectedContinuation);
        ArgumentNullException.ThrowIfNull(activation);
        var instanceId = expectedContinuation.ProcessInstanceId;
        RequireInstance(instanceId);
        context.ThrowIfCancellationRequested();

        var capabilityDiagnostics = ValidateCapabilities();
        if (!capabilityDiagnostics.IsEmpty)
        {
            return new(ProcessDurableRuntimeDisposition.Incompatible, diagnostics: capabilityDiagnostics);
        }

        var gate = instanceGates.GetOrAdd(instanceId, static _ => new(1, 1));
        await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await store.LoadAsync(context, instanceId).ConfigureAwait(false);
            if (loaded is null)
            {
                return new(ProcessDurableRuntimeDisposition.NotFound);
            }

            var preflight = Validate(plan, loaded);
            if (!preflight.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    loaded,
                    diagnostics: preflight.Diagnostics);
            }

            var replay = FindActivation(loaded.Checkpoint, expectedContinuation, activation);
            if (replay is not null)
            {
                return new(
                    replay.Disposition,
                    loaded,
                    replay.Decision,
                    replay.Commit,
                    replay.Diagnostics);
            }
            if (loaded.Checkpoint.ContinuationIdentity != expectedContinuation)
            {
                return StaleActivationAttempt(loaded);
            }
            var lifecycle = ActivationLifecycle(loaded.Checkpoint);
            if (lifecycle is not null)
            {
                return lifecycle;
            }

            var acquiredAtUtc = context.UtcNow;
            var acquired = await AcquireOrRenewExactAsync(
                    context,
                    plan,
                    instanceId,
                    loaded.Revision,
                    options.WorkerId,
                    options.WorkerLease,
                    acquiredAtUtc)
                .ConfigureAwait(false);
            if (acquired is null)
            {
                return new(ProcessDurableRuntimeDisposition.CommitOutcomeUnknown, loaded);
            }
            if (acquired.Disposition is not (ProcessStoreMutationDisposition.Applied
                or ProcessStoreMutationDisposition.Replayed))
            {
                return new(MapStoreDisposition(acquired.Disposition), acquired.Snapshot);
            }

            var snapshot = acquired.Snapshot
                ?? throw new InvalidOperationException("A successful Process worker acquisition returned no snapshot.");
            var acquiredCompatibility = Validate(plan, snapshot);
            if (!acquiredCompatibility.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    snapshot,
                    diagnostics: acquiredCompatibility.Diagnostics);
            }

            replay = FindActivation(snapshot.Checkpoint, expectedContinuation, activation);
            if (replay is not null)
            {
                return new(
                    replay.Disposition,
                    snapshot,
                    replay.Decision,
                    replay.Commit,
                    replay.Diagnostics);
            }
            if (snapshot.Checkpoint.ContinuationIdentity != expectedContinuation)
            {
                return StaleActivationAttempt(snapshot);
            }
            lifecycle = ActivationLifecycle(snapshot.Checkpoint);
            if (lifecycle is not null)
            {
                return new(
                    lifecycle.Disposition,
                    snapshot,
                    lifecycle.Decision,
                    lifecycle.Commit,
                    lifecycle.Diagnostics);
            }

            var checkpoint = snapshot.Checkpoint;
            var controlExecutor = ControlExecutor(plan);
            var begun = controlExecutor.BeginActivation(
                checkpoint.Control,
                new(
                    Expectation(checkpoint.Control),
                    activation.Id,
                    activation.ObservedAtUtc));
            if (begun.Disposition != ProcessControlDecisionDisposition.ActivationStarted)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    snapshot,
                    diagnostics: begun.Diagnostics);
            }

            var transitionHost = transitionOperationAdapter is null
                ? null
                : new ProcessTransitionOperationSuspensionHost(host);
            var replayHost = new ProcessOperationReplayHost(
                transitionHost ?? host,
                checkpoint.Operations);
            var decision = transitionHost is null
                ? Activate(
                    plan,
                    checkpoint.Continuation,
                    activation,
                    replayHost)
                : await ActivateWithTransitionOperationsAsync(
                        context,
                        plan,
                        checkpoint.Continuation,
                        activation,
                        replayHost,
                        transitionHost)
                    .ConfigureAwait(false);
            if (decision.Disposition == ProcessActivationDisposition.Rejected)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    snapshot,
                    decision,
                    diagnostics: decision.Diagnostics);
            }

            var committedAtUtc = context.UtcNow;
            var safePointNode = ResolveSafePointNode(plan, decision);
            var before = ProcessStorageContentFingerprints.Continuation(checkpoint.Continuation);
            var safePoint = controlExecutor.ReachSafePoint(
                begun.State,
                new(
                    ProcessDurableRuntimeIdentities.SafePoint(
                        checkpoint.ContinuationIdentity,
                        activation,
                        before),
                    Expectation(begun.State),
                    activation.Id,
                    safePointNode,
                    committedAtUtc));
            if (safePoint.Disposition != ProcessControlDecisionDisposition.SafePointReached)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    snapshot,
                    decision,
                    diagnostics: safePoint.Diagnostics);
            }

            if (!ProcessDurableCheckpointReducer.TryApplyActivation(
                    plan,
                    checkpoint,
                    activation,
                    decision,
                    safePoint.State,
                    replayHost.Observations,
                    bindingResolver,
                    committedAtUtc,
                    out var replacement,
                    out var reductionDiagnostics))
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    snapshot,
                    decision,
                    diagnostics: reductionDiagnostics);
            }

            var candidate = replacement
                ?? throw new InvalidOperationException("A successful checkpoint reduction returned no replacement.");
            var candidateCompatibility = ProcessCheckpointCompatibilityValidator.Validate(plan, candidate);
            if (!candidateCompatibility.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    snapshot,
                    decision,
                    diagnostics: candidateCompatibility.Diagnostics);
            }

            var localMutations = localMutationPlanner.Plan(checkpoint, activation, decision);
            var lease = snapshot.WorkerLease
                ?? throw new InvalidOperationException("A successful Process worker acquisition retained no lease.");
            var commit = new ProcessDurableCommit(
                ProcessDurableRuntimeIdentities.ActivationCommit(
                    checkpoint.ContinuationIdentity,
                    activation,
                    before),
                snapshot.Revision,
                options.WorkerId,
                lease.Fence,
                candidate,
                localMutations,
                committedAtUtc);
            var committed = await CommitExactAsync(context, commit).ConfigureAwait(false);
            if (committed is null)
            {
                return new(
                    ProcessDurableRuntimeDisposition.CommitOutcomeUnknown,
                    snapshot,
                    decision,
                    commit);
            }

            var resultSnapshot = committed.Snapshot;
            if (committed.Disposition is ProcessStoreMutationDisposition.Applied
                or ProcessStoreMutationDisposition.Replayed)
            {
                resultSnapshot = await store.LoadAsync(context, instanceId).ConfigureAwait(false);
            }
            return new(
                MapSuccessfulStoreDisposition(committed.Disposition),
                resultSnapshot,
                decision,
                commit);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Evaluates and atomically persists one canonical Process lifecycle command.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="plan">Exact compiled Process definition selected for restore validation.</param>
    /// <param name="command">Canonical lifecycle command with exact attempt and semantic revision expectation.</param>
    /// <returns>A committed, replayed, rejected, unsupported, or fenced control result.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before a physical boundary.</exception>
    public async Task<ProcessDurableControlResult> ApplyControlAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessControlCommand command)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(command);
        context.ThrowIfCancellationRequested();
        var instanceId = command.Context.ProcessInstanceId;
        RequireInstance(instanceId);
        if (command is CancelProcessCommand or TerminateProcessCommand)
        {
            return new(
                ProcessDurableRuntimeDisposition.Unsupported,
                diagnostics: [Error(
                    ProcessDurableRuntimeDiagnosticCodes.ActivationLifecycleBlocked,
                    "Cancellation and forced termination require terminal continuation composition and are not control-only mutations.",
                    "/command")]);
        }

        var capabilityDiagnostics = ValidateCapabilities();
        if (!capabilityDiagnostics.IsEmpty)
        {
            return new(ProcessDurableRuntimeDisposition.Incompatible, diagnostics: capabilityDiagnostics);
        }

        var gate = instanceGates.GetOrAdd(instanceId, static _ => new(1, 1));
        await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await store.LoadAsync(context, instanceId).ConfigureAwait(false);
            if (loaded is null)
            {
                return new(ProcessDurableRuntimeDisposition.NotFound);
            }
            var compatibility = Validate(plan, loaded);
            if (!compatibility.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    loaded,
                    diagnostics: compatibility.Diagnostics);
            }

            var controller = ControlExecutor(plan);
            var decisionObservedAtUtc = context.UtcNow;
            var preview = controller.Apply(loaded.Checkpoint.Control, command, decisionObservedAtUtc);
            if (IsRejected(preview.Disposition))
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    loaded,
                    preview,
                    diagnostics: preview.Diagnostics);
            }
            if (IsReplay(preview.Disposition))
            {
                return new(ProcessDurableRuntimeDisposition.Replayed, loaded, preview);
            }
            if (preview.Intent is ProcessSignalAdmissionIntent
                && !ProcessDurableCheckpointReducer.TryApplyControl(
                    plan,
                    loaded.Checkpoint,
                    preview,
                    decisionObservedAtUtc,
                    out _,
                    out var previewReductionDiagnostics))
            {
                return new(
                    ProcessDurableRuntimeDisposition.IdentityConflict,
                    loaded,
                    preview,
                    diagnostics: previewReductionDiagnostics);
            }

            var acquiredAtUtc = context.UtcNow;
            var acquired = await AcquireOrRenewExactAsync(
                    context,
                    plan,
                    instanceId,
                    loaded.Revision,
                    options.WorkerId,
                    options.WorkerLease,
                    acquiredAtUtc)
                .ConfigureAwait(false);
            if (acquired is null)
            {
                return new(ProcessDurableRuntimeDisposition.CommitOutcomeUnknown, loaded);
            }
            if (acquired.Disposition is not (ProcessStoreMutationDisposition.Applied
                or ProcessStoreMutationDisposition.Replayed))
            {
                return new(MapStoreDisposition(acquired.Disposition), acquired.Snapshot);
            }
            var snapshot = acquired.Snapshot
                ?? throw new InvalidOperationException("A successful Process worker acquisition returned no snapshot.");
            compatibility = Validate(plan, snapshot);
            if (!compatibility.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    snapshot,
                    diagnostics: compatibility.Diagnostics);
            }

            var decision = controller.Apply(snapshot.Checkpoint.Control, command, decisionObservedAtUtc);
            if (IsRejected(decision.Disposition))
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    snapshot,
                    decision,
                    diagnostics: decision.Diagnostics);
            }
            if (IsReplay(decision.Disposition))
            {
                return new(ProcessDurableRuntimeDisposition.Replayed, snapshot, decision);
            }

            var committedAtUtc = context.UtcNow;
            if (!ProcessDurableCheckpointReducer.TryApplyControl(
                    plan,
                    snapshot.Checkpoint,
                    decision,
                    committedAtUtc,
                    out var replacement,
                    out var reductionDiagnostics))
            {
                return new(
                    ProcessDurableRuntimeDisposition.IdentityConflict,
                    snapshot,
                    decision,
                    diagnostics: reductionDiagnostics);
            }
            var candidate = replacement
                ?? throw new InvalidOperationException("A successful control reduction returned no checkpoint.");
            compatibility = ProcessCheckpointCompatibilityValidator.Validate(plan, candidate);
            if (!compatibility.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    snapshot,
                    decision,
                    diagnostics: compatibility.Diagnostics);
            }

            var lease = snapshot.WorkerLease
                ?? throw new InvalidOperationException("A successful Process worker acquisition retained no lease.");
            var commit = new ProcessDurableCommit(
                ProcessDurableRuntimeIdentities.ControlCommit(
                    instanceId,
                    command.Context.CommandId,
                    snapshot.Checkpoint.Control.Revision),
                snapshot.Revision,
                options.WorkerId,
                lease.Fence,
                candidate,
                [],
                committedAtUtc);
            var committed = await CommitExactAsync(context, commit).ConfigureAwait(false);
            if (committed is null)
            {
                return new(
                    ProcessDurableRuntimeDisposition.CommitOutcomeUnknown,
                    snapshot,
                    decision,
                    commit);
            }
            var resultSnapshot = committed.Snapshot;
            if (committed.Disposition is ProcessStoreMutationDisposition.Applied
                or ProcessStoreMutationDisposition.Replayed)
            {
                resultSnapshot = await store.LoadAsync(context, instanceId).ConfigureAwait(false);
            }
            return new(
                MapSuccessfulStoreDisposition(committed.Disposition),
                resultSnapshot,
                decision,
                commit);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Applies cooperative cancellation at a retained safe boundary as one command-linked terminal activation.
    /// </summary>
    /// <remarks>
    /// The applied Cancel receipt is the terminal durable cut. A cancellation requested while an activation is
    /// already in flight remains deferred to that activation's next ordinary safe point by the canonical Control
    /// protocol; this driver never persists an incomplete activation merely to manufacture that condition.
    /// </remarks>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="plan">Exact compiled Process definition selected for restore validation.</param>
    /// <param name="command">Canonical cooperative cancellation command.</param>
    /// <param name="activationContext">Explicit authority, correlation, delivery, and provenance for the terminal activation.</param>
    /// <returns>A committed, replayed, rejected, or fenced cooperative cancellation result.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The host operation is cancelled before a physical boundary.</exception>
    public async Task<ProcessDurableControlResult> CancelAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        CancelProcessCommand command,
        ProcessActivationContext activationContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(activationContext);
        context.ThrowIfCancellationRequested();
        var instanceId = command.Context.ProcessInstanceId;
        RequireInstance(instanceId);
        var capabilityDiagnostics = ValidateCapabilities();
        if (!capabilityDiagnostics.IsEmpty)
        {
            return new(ProcessDurableRuntimeDisposition.Incompatible, diagnostics: capabilityDiagnostics);
        }

        var gate = instanceGates.GetOrAdd(instanceId, static _ => new(1, 1));
        await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await store.LoadAsync(context, instanceId).ConfigureAwait(false);
            if (loaded is null)
            {
                return new(ProcessDurableRuntimeDisposition.NotFound);
            }
            var compatibility = Validate(plan, loaded);
            if (!compatibility.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    loaded,
                    diagnostics: compatibility.Diagnostics);
            }

            var controller = ControlExecutor(plan);
            var decisionObservedAtUtc = context.UtcNow;
            var preview = controller.Apply(loaded.Checkpoint.Control, command, decisionObservedAtUtc);
            if (IsRejected(preview.Disposition))
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    loaded,
                    preview,
                    diagnostics: preview.Diagnostics);
            }
            if (preview.Disposition == ProcessControlDecisionDisposition.Replayed)
            {
                return ResolveCancellationReplay(loaded, command, activationContext, preview);
            }

            var acquiredAtUtc = context.UtcNow;
            var acquired = await AcquireOrRenewExactAsync(
                    context,
                    plan,
                    instanceId,
                    loaded.Revision,
                    options.WorkerId,
                    options.WorkerLease,
                    acquiredAtUtc)
                .ConfigureAwait(false);
            if (acquired is null)
            {
                return new(ProcessDurableRuntimeDisposition.CommitOutcomeUnknown, loaded);
            }
            if (acquired.Disposition is not (ProcessStoreMutationDisposition.Applied
                or ProcessStoreMutationDisposition.Replayed))
            {
                return new(MapStoreDisposition(acquired.Disposition), acquired.Snapshot);
            }
            var snapshot = acquired.Snapshot
                ?? throw new InvalidOperationException("A successful Process worker acquisition returned no snapshot.");
            compatibility = Validate(plan, snapshot);
            if (!compatibility.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    snapshot,
                    diagnostics: compatibility.Diagnostics);
            }

            var checkpoint = snapshot.Checkpoint;
            var expectedContinuation = checkpoint.ContinuationIdentity;
            var decision = controller.Apply(checkpoint.Control, command, decisionObservedAtUtc);
            if (decision.Disposition == ProcessControlDecisionDisposition.Replayed)
            {
                return ResolveCancellationReplay(snapshot, command, activationContext, decision);
            }
            if (decision.Disposition == ProcessControlDecisionDisposition.AlreadySatisfied)
            {
                var controlCommittedAtUtc = context.UtcNow;
                var controlReplacement = ProcessDurableCheckpointReducer.ApplyControl(
                    plan,
                    checkpoint,
                    decision,
                    controlCommittedAtUtc);
                compatibility = ProcessCheckpointCompatibilityValidator.Validate(plan, controlReplacement);
                if (!compatibility.IsValid)
                {
                    return new(
                        ProcessDurableRuntimeDisposition.Incompatible,
                        snapshot,
                        decision,
                        diagnostics: compatibility.Diagnostics);
                }

                var controlLease = snapshot.WorkerLease
                    ?? throw new InvalidOperationException("A successful Process worker acquisition retained no lease.");
                var controlCommit = new ProcessDurableCommit(
                    ProcessDurableRuntimeIdentities.ControlCommit(
                        instanceId,
                        command.Context.CommandId,
                        checkpoint.Control.Revision),
                    snapshot.Revision,
                    options.WorkerId,
                    controlLease.Fence,
                    controlReplacement,
                    [],
                    controlCommittedAtUtc);
                var controlCommitted = await CommitExactAsync(context, controlCommit).ConfigureAwait(false);
                if (controlCommitted is null)
                {
                    return new(
                        ProcessDurableRuntimeDisposition.CommitOutcomeUnknown,
                        snapshot,
                        decision,
                        controlCommit);
                }

                var controlSnapshot = controlCommitted.Snapshot;
                if (controlCommitted.Disposition is ProcessStoreMutationDisposition.Applied
                    or ProcessStoreMutationDisposition.Replayed)
                {
                    controlSnapshot = await store.LoadAsync(context, instanceId).ConfigureAwait(false);
                }
                return new(
                    MapSuccessfulStoreDisposition(controlCommitted.Disposition),
                    controlSnapshot,
                    decision,
                    controlCommit);
            }
            if (decision.Disposition != ProcessControlDecisionDisposition.Applied
                || decision.Intent is not ProcessCancellationIntent cancellation)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    snapshot,
                    decision,
                    diagnostics: decision.Diagnostics);
            }

            var activationId = ProcessDurableRuntimeIdentities.CancellationActivation(
                expectedContinuation,
                command.Context.CommandId);
            var activation = new ProcessActivation(
                activationId,
                ProcessActivationCause.Control,
                decisionObservedAtUtc,
                activationContext,
                [.. checkpoint.Inbox
                    .Where(static entry => entry.Receipt is null)
                    .Select(static entry => entry.Input)],
                cancellation: cancellation);
            var activationDecision = Activate(
                plan,
                checkpoint.Continuation,
                activation,
                host);
            if (activationDecision.Disposition != ProcessActivationDisposition.Cancelled)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    snapshot,
                    decision,
                    diagnostics: activationDecision.Diagnostics);
            }

            var before = ProcessStorageContentFingerprints.Continuation(checkpoint.Continuation);
            var committedAtUtc = context.UtcNow;
            if (!ProcessDurableCheckpointReducer.TryApplyActivation(
                    plan,
                    checkpoint,
                    activation,
                    activationDecision,
                    decision.State,
                    [],
                    bindingResolver,
                    committedAtUtc,
                    out var replacement,
                    out var reductionDiagnostics))
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    snapshot,
                    decision,
                    diagnostics: reductionDiagnostics);
            }
            var candidate = replacement
                ?? throw new InvalidOperationException("A successful cancellation reduction returned no checkpoint.");
            compatibility = ProcessCheckpointCompatibilityValidator.Validate(plan, candidate);
            if (!compatibility.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    snapshot,
                    decision,
                    diagnostics: compatibility.Diagnostics);
            }

            var localMutations = localMutationPlanner.Plan(checkpoint, activation, activationDecision);
            var lease = snapshot.WorkerLease
                ?? throw new InvalidOperationException("A successful Process worker acquisition retained no lease.");
            var commit = new ProcessDurableCommit(
                ProcessDurableRuntimeIdentities.ActivationCommit(
                    expectedContinuation,
                    activation,
                    before),
                snapshot.Revision,
                options.WorkerId,
                lease.Fence,
                candidate,
                localMutations,
                committedAtUtc);
            var committed = await CommitExactAsync(context, commit).ConfigureAwait(false);
            if (committed is null)
            {
                return new(
                    ProcessDurableRuntimeDisposition.CommitOutcomeUnknown,
                    snapshot,
                    decision,
                    commit);
            }
            var resultSnapshot = committed.Snapshot;
            if (committed.Disposition is ProcessStoreMutationDisposition.Applied
                or ProcessStoreMutationDisposition.Replayed)
            {
                resultSnapshot = await store.LoadAsync(context, instanceId).ConfigureAwait(false);
            }
            return new(
                MapSuccessfulStoreDisposition(committed.Disposition),
                resultSnapshot,
                decision,
                commit);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Persists one write-once current-attempt affinity under worker and semantic fencing.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="plan">Exact compiled Process definition selected for restore validation.</param>
    /// <param name="observation">Exact current-attempt affinity observation.</param>
    /// <returns>A committed, replayed, rejected, or fenced affinity result.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before a physical boundary.</exception>
    public async Task<ProcessDurableControlResult> BindAttemptAffinityAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessAttemptAffinityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(observation);
        context.ThrowIfCancellationRequested();
        var instanceId = observation.Expectation.Continuation.ProcessInstanceId;
        RequireInstance(instanceId);
        var capabilityDiagnostics = ValidateCapabilities();
        if (!capabilityDiagnostics.IsEmpty)
        {
            return new(ProcessDurableRuntimeDisposition.Incompatible, diagnostics: capabilityDiagnostics);
        }

        var gate = instanceGates.GetOrAdd(instanceId, static _ => new(1, 1));
        await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await store.LoadAsync(context, instanceId).ConfigureAwait(false);
            if (loaded is null)
            {
                return new(ProcessDurableRuntimeDisposition.NotFound);
            }
            var compatibility = Validate(plan, loaded);
            if (!compatibility.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    loaded,
                    diagnostics: compatibility.Diagnostics);
            }
            var controller = ControlExecutor(plan);
            var preview = controller.BindAttemptAffinity(loaded.Checkpoint.Control, observation);
            if (IsRejected(preview.Disposition))
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    loaded,
                    preview,
                    diagnostics: preview.Diagnostics);
            }
            if (preview.Disposition == ProcessControlDecisionDisposition.Replayed)
            {
                return new(ProcessDurableRuntimeDisposition.Replayed, loaded, preview);
            }

            var acquiredAtUtc = context.UtcNow;
            var acquired = await AcquireOrRenewExactAsync(
                    context,
                    plan,
                    instanceId,
                    loaded.Revision,
                    options.WorkerId,
                    options.WorkerLease,
                    acquiredAtUtc)
                .ConfigureAwait(false);
            if (acquired is null)
            {
                return new(ProcessDurableRuntimeDisposition.CommitOutcomeUnknown, loaded);
            }
            if (acquired.Disposition is not (ProcessStoreMutationDisposition.Applied
                or ProcessStoreMutationDisposition.Replayed))
            {
                return new(MapStoreDisposition(acquired.Disposition), acquired.Snapshot);
            }
            var snapshot = acquired.Snapshot
                ?? throw new InvalidOperationException("A successful Process worker acquisition returned no snapshot.");
            compatibility = Validate(plan, snapshot);
            if (!compatibility.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    snapshot,
                    diagnostics: compatibility.Diagnostics);
            }

            var decision = controller.BindAttemptAffinity(snapshot.Checkpoint.Control, observation);
            if (IsRejected(decision.Disposition))
            {
                return new(
                    ProcessDurableRuntimeDisposition.Rejected,
                    snapshot,
                    decision,
                    diagnostics: decision.Diagnostics);
            }
            if (decision.Disposition == ProcessControlDecisionDisposition.Replayed)
            {
                return new(ProcessDurableRuntimeDisposition.Replayed, snapshot, decision);
            }

            var committedAtUtc = context.UtcNow;
            var replacement = ProcessDurableCheckpointReducer.ApplyAffinity(
                snapshot.Checkpoint,
                decision,
                committedAtUtc);
            compatibility = ProcessCheckpointCompatibilityValidator.Validate(plan, replacement);
            if (!compatibility.IsValid)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Incompatible,
                    snapshot,
                    decision,
                    diagnostics: compatibility.Diagnostics);
            }

            var lease = snapshot.WorkerLease
                ?? throw new InvalidOperationException("A successful Process worker acquisition retained no lease.");
            var commit = new ProcessDurableCommit(
                ProcessDurableRuntimeIdentities.AffinityCommit(
                    observation.Expectation.Continuation,
                    snapshot.Checkpoint.Control.Revision,
                    observation.Affinity),
                snapshot.Revision,
                options.WorkerId,
                lease.Fence,
                replacement,
                [],
                committedAtUtc);
            var committed = await CommitExactAsync(context, commit).ConfigureAwait(false);
            if (committed is null)
            {
                return new(
                    ProcessDurableRuntimeDisposition.CommitOutcomeUnknown,
                    snapshot,
                    decision,
                    commit);
            }
            var resultSnapshot = committed.Snapshot;
            if (committed.Disposition is ProcessStoreMutationDisposition.Applied
                or ProcessStoreMutationDisposition.Replayed)
            {
                resultSnapshot = await store.LoadAsync(context, instanceId).ConfigureAwait(false);
            }
            return new(
                MapSuccessfulStoreDisposition(committed.Disposition),
                resultSnapshot,
                decision,
                commit);
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<ProcessStoreMutationResult?> InitializeExactAsync(
        OperationContext context,
        ProcessCommitId commitId,
        ProcessDurableCheckpoint checkpoint) =>
        await RetryAmbiguousStoreMutationAsync(
                context,
                () => store.InitializeAsync(context, commitId, checkpoint))
            .ConfigureAwait(false);

    async Task<ProcessStoreMutationResult?> AcquireExactAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        ProcessStorageRevision expectedRevision,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset observedAtUtc) =>
        await RetryAmbiguousStoreMutationAsync(
                context,
                () => store.AcquireWorkerAsync(
                    context,
                    instanceId,
                    expectedRevision,
                    owner,
                    leaseDuration,
                    observedAtUtc))
            .ConfigureAwait(false);

    async Task<ProcessStoreMutationResult?> AcquireOrRenewExactAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessInstanceId instanceId,
        ProcessStorageRevision expectedRevision,
        string owner,
        TimeSpan leaseDuration,
        DateTimeOffset observedAtUtc)
    {
        var acquired = await AcquireExactAsync(
                context,
                instanceId,
                expectedRevision,
                owner,
                leaseDuration,
                observedAtUtc)
            .ConfigureAwait(false);
        if (acquired is null
            || acquired.Disposition is not (ProcessStoreMutationDisposition.Applied
                or ProcessStoreMutationDisposition.Replayed))
        {
            return acquired;
        }

        var acquiredSnapshot = acquired.Snapshot
            ?? throw new InvalidOperationException("A successful Process worker acquisition returned no snapshot.");
        if (!Validate(plan, acquiredSnapshot).IsValid)
        {
            return acquired;
        }

        var lease = acquiredSnapshot.WorkerLease
            ?? throw new InvalidOperationException("A successful Process worker acquisition retained no lease.");
        if (!string.Equals(lease.Owner, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A successful Process worker acquisition retained another owner.");
        }

        var requestedExpiry = observedAtUtc.Add(leaseDuration);
        if (lease.RenewedAtUtc == observedAtUtc && lease.ExpiresAtUtc == requestedExpiry)
        {
            return acquired;
        }

        return await RenewWorkerExactAsync(
                context,
                instanceId,
                owner,
                lease.Fence,
                leaseDuration,
                observedAtUtc)
            .ConfigureAwait(false);
    }

    async Task<ProcessStoreMutationResult?> CommitExactAsync(
        OperationContext context,
        ProcessDurableCommit commit)
    {
        var committed = await RetryAmbiguousStoreMutationAsync(
                context,
                () => store.CommitAsync(context, commit))
            .ConfigureAwait(false);
        if (committed is
            {
                Disposition: ProcessStoreMutationDisposition.Applied or ProcessStoreMutationDisposition.Replayed,
                Snapshot: { } snapshot
            })
        {
            StorageExecutionTelemetry.RecordCheckpoint(snapshot.Checkpoint);
        }
        return committed;
    }

    async Task<ProcessStoreMutationResult?> RetryAmbiguousStoreMutationAsync(
        OperationContext context,
        Func<Task<ProcessStoreMutationResult>> mutation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mutation);
        for (var attempt = 1; attempt <= options.MaxAmbiguousStoreMutationAttempts; attempt++)
        {
            try
            {
                return await mutation().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
                when (storeMutationExceptionClassifier.Classify(exception)
                      == ProcessStoreMutationExceptionClassification.Ambiguous)
            {
                if (attempt == options.MaxAmbiguousStoreMutationAttempts)
                {
                    return null;
                }
            }
        }
        throw new InvalidOperationException("The bounded ambiguous store-mutation retry loop did not return a result.");
    }

    DocumentValidationResult Validate(CompiledProcessPlan plan, ProcessDurableStoreSnapshot snapshot) =>
        ProcessCheckpointCompatibilityValidator.Validate(plan, snapshot.Checkpoint);

    ImmutableArray<DocumentValidationDiagnostic> ValidateCapabilities()
    {
        var capabilities = store.Capabilities;
        if (capabilities.SupportsAtomicAggregateCommit
            && capabilities.SupportsCompareAndSwap
            && capabilities.SupportsWorkerFencing)
        {
            return [];
        }
        return [Error(
            ProcessDurableRuntimeDiagnosticCodes.StoreCapabilityInsufficient,
            "The durable Process runtime requires atomic aggregate commit, compare-and-swap, and worker fencing.",
            "/store/capabilities")];
    }

    static ProcessControlReferenceExecutor ControlExecutor(CompiledProcessPlan plan) =>
        new(plan.ValidationContext.InteractionContracts
            ?? throw new InvalidOperationException("Durable Process control requires the compiled interaction catalog."));

    internal static ProcessActivationDecision Activate(
        CompiledProcessPlan plan,
        ProcessContinuationState state,
        ProcessActivation activation,
        IProcessReferenceHost host)
    {
        var activity = ExecutionTelemetry.StartActivity(ExecutionTelemetryActivityKind.Activation);
        try
        {
            var decision = ProcessReferenceInterpreter.Activate(plan, state, activation, host);
            var outcome = decision.Disposition switch
            {
                ProcessActivationDisposition.Quiescent or ProcessActivationDisposition.DurableCut
                    or ProcessActivationDisposition.Completed => ExecutionTelemetryOutcome.Succeeded,
                ProcessActivationDisposition.Failed => ExecutionTelemetryOutcome.Failed,
                ProcessActivationDisposition.Cancelled => ExecutionTelemetryOutcome.Cancelled,
                ProcessActivationDisposition.Rejected => ExecutionTelemetryOutcome.Rejected,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(decision),
                    decision.Disposition,
                    "Unsupported Process activation disposition.")
            };
            if (activity?.IsAllDataRequested == true)
            {
                try
                {
                    var trace = ProcessExecutionTraceProjector.Project(decision);
                    ExecutionTelemetry.CorrelateActivity(activity, trace: trace.Trace);
                }
                catch (Exception exception) when (exception is not (
                    OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    // Supplemental trace projection cannot alter the finite activation decision.
                }
            }
            ExecutionTelemetry.CompleteActivity(activity, outcome);
            return decision;
        }
        catch (ProcessTransitionOperationPendingException)
        {
            activity?.Dispose();
            throw;
        }
        catch (OperationCanceledException exception)
        {
            ExecutionTelemetry.CompleteActivity(activity, ExecutionTelemetryOutcome.Cancelled, exception);
            throw;
        }
        catch (Exception exception)
        {
            ExecutionTelemetry.CompleteActivity(activity, ExecutionTelemetryOutcome.Failed, exception);
            throw;
        }
    }

    async Task<ProcessActivationDecision> ActivateWithTransitionOperationsAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessContinuationState state,
        ProcessActivation activation,
        ProcessOperationReplayHost replayHost,
        ProcessTransitionOperationSuspensionHost transitionHost)
    {
        while (true)
        {
            context.ThrowIfCancellationRequested();
            try
            {
                return Activate(plan, state, activation, replayHost);
            }
            catch (ProcessTransitionOperationPendingException pending)
            {
                var result = await transitionOperationAdapter!.ExecuteAsync(context, pending.Invocation)
                    .ConfigureAwait(false);
                transitionHost.Materialize(pending.Invocation, result);
            }
        }
    }

    static ProcessControlExpectation Expectation(ProcessControlState state) =>
        new(
            new(state.ProcessInstanceId, state.CurrentAttempt.AttemptId),
            state.Revision);

    static ProcessDurableActivationResult? FindActivation(
        ProcessDurableCheckpoint checkpoint,
        ProcessContinuationIdentity expectedContinuation,
        ProcessActivation activation)
    {
        var receipt = checkpoint.Activations.FirstOrDefault(candidate =>
            candidate.Continuation == expectedContinuation
            && candidate.Activation.Id == activation.Id);
        if (receipt is null)
        {
            return null;
        }

        var exact = ProcessStorageContentFingerprints.Value(receipt.Activation)
            == ProcessStorageContentFingerprints.Value(activation);
        return exact
            ? new(
                ProcessDurableRuntimeDisposition.Replayed,
                decision: null,
                diagnostics: [])
            : new(
                ProcessDurableRuntimeDisposition.IdentityConflict,
                diagnostics: [Error(
                    ProcessDurableRuntimeDiagnosticCodes.ActivationIdentityConflict,
                    "The activation identity is already committed with different canonical content.",
                    "/activation/id")]);
    }

    static ProcessDurableActivationResult StaleActivationAttempt(ProcessDurableStoreSnapshot snapshot) =>
        new(
            ProcessDurableRuntimeDisposition.StaleFence,
            snapshot,
            diagnostics: [Error(
                ProcessControlDiagnosticCodes.StaleAttempt,
                "The requested Process attempt is no longer current and cannot produce new logical evidence.",
                "/expectedContinuation/processAttemptId")]);

    static ProcessDurableActivationResult? ActivationLifecycle(ProcessDurableCheckpoint checkpoint)
    {
        if (checkpoint.Control.Mode == ProcessControlMode.Paused)
        {
            return new(
                ProcessDurableRuntimeDisposition.Paused,
                diagnostics: [Error(
                    ProcessDurableRuntimeDiagnosticCodes.ActivationLifecycleBlocked,
                    "Ordinary activation is disabled while the Process is paused.",
                    "/control/mode")]);
        }
        if (checkpoint.Control.IsTerminal
            || checkpoint.Continuation.Terminal.Kind != ExecutionTerminalOutcomeKind.None)
        {
            return new(
                ProcessDurableRuntimeDisposition.Terminal,
                diagnostics: [Error(
                    ProcessDurableRuntimeDiagnosticCodes.ActivationLifecycleBlocked,
                    "A terminal Process cannot enter another finite activation.",
                    "/continuation/terminal")]);
        }
        if (checkpoint.Control.Mode != ProcessControlMode.Running
            || checkpoint.Control.CurrentAttempt.Phase is not (
                ProcessControlExecutionPhase.Ready or ProcessControlExecutionPhase.AtSafePoint))
        {
            return new(
                ProcessDurableRuntimeDisposition.Rejected,
                diagnostics: [Error(
                    ProcessDurableRuntimeDiagnosticCodes.ActivationLifecycleBlocked,
                    "The current lifecycle mode and phase do not permit ordinary activation.",
                    "/control")]);
        }
        return null;
    }

    static ExecutionNodeId ResolveSafePointNode(
        CompiledProcessPlan plan,
        ProcessActivationDecision decision) =>
        decision.Evidence.SafePointNode
        ?? (decision.Evidence.Trace.IsEmpty
            ? plan.Definition.Entry
            : decision.Evidence.Trace[^1].Node);

    static bool IsReplay(ProcessControlDecisionDisposition disposition) =>
        disposition is ProcessControlDecisionDisposition.Inspected
            or ProcessControlDecisionDisposition.Replayed;

    static ProcessDurableControlResult ResolveCancellationReplay(
        ProcessDurableStoreSnapshot snapshot,
        CancelProcessCommand command,
        ProcessActivationContext activationContext,
        ProcessControlDecision decision)
    {
        if (decision.Receipt?.Disposition == ProcessControlReceiptDisposition.AlreadySatisfied)
        {
            return new(ProcessDurableRuntimeDisposition.Replayed, snapshot, decision);
        }

        if (decision.Receipt?.Disposition != ProcessControlReceiptDisposition.Applied)
        {
            return new(
                ProcessDurableRuntimeDisposition.Incompatible,
                snapshot,
                decision,
                diagnostics: [Error(
                    ProcessCheckpointDiagnosticCodes.ActivationReceiptIncompatible,
                    "A replayed Cancel receipt has no supported terminal durable-cut disposition.",
                    "/checkpoint/control/receipts")]);
        }

        var continuation = command.Expectation?.Continuation
            ?? throw new InvalidOperationException("A canonical Cancel command requires an exact continuation expectation.");
        var activationId = ProcessDurableRuntimeIdentities.CancellationActivation(
            continuation,
            command.Context.CommandId);
        var receipt = snapshot.Checkpoint.Activations.FirstOrDefault(candidate =>
            candidate.Continuation == continuation
            && candidate.Activation.Id == activationId
            && candidate.Disposition == ProcessActivationDisposition.Cancelled);
        if (receipt is null)
        {
            return new(
                ProcessDurableRuntimeDisposition.Incompatible,
                snapshot,
                decision,
                diagnostics: [Error(
                    ProcessCheckpointDiagnosticCodes.ActivationReceiptIncompatible,
                    "The replayed Cancel receipt has no exact terminal activation evidence.",
                    "/checkpoint/activations")]);
        }

        var expected = new ProcessActivation(
            activationId,
            ProcessActivationCause.Control,
            receipt.Activation.ObservedAtUtc,
            activationContext,
            receipt.Activation.Inputs,
            cancellation: new(continuation.ProcessAttemptId, command.Reason));
        if (ProcessStorageContentFingerprints.Value(expected)
            != ProcessStorageContentFingerprints.Value(receipt.Activation))
        {
            return new(
                ProcessDurableRuntimeDisposition.IdentityConflict,
                snapshot,
                decision,
                diagnostics: [Error(
                    ProcessDurableRuntimeDiagnosticCodes.ActivationIdentityConflict,
                    "The cancellation activation identity is already committed with different canonical context.",
                    "/activationContext")]);
        }

        return new(ProcessDurableRuntimeDisposition.Replayed, snapshot, decision);
    }

    static bool IsRejected(ProcessControlDecisionDisposition disposition) =>
        disposition is ProcessControlDecisionDisposition.Unauthorized
            or ProcessControlDecisionDisposition.TargetMismatch
            or ProcessControlDecisionDisposition.StaleAttempt
            or ProcessControlDecisionDisposition.StaleRevision
            or ProcessControlDecisionDisposition.IdentityConflict
            or ProcessControlDecisionDisposition.IdempotencyConflict
            or ProcessControlDecisionDisposition.SignalConflict
            or ProcessControlDecisionDisposition.AffinityConflict
            or ProcessControlDecisionDisposition.InvalidState
            or ProcessControlDecisionDisposition.InvalidCommand;

    static ProcessDurableRuntimeDisposition MapSuccessfulStoreDisposition(
        ProcessStoreMutationDisposition disposition) =>
        disposition switch
        {
            ProcessStoreMutationDisposition.Applied => ProcessDurableRuntimeDisposition.Applied,
            ProcessStoreMutationDisposition.Replayed => ProcessDurableRuntimeDisposition.Replayed,
            _ => MapStoreDisposition(disposition)
        };

    static ProcessDurableRuntimeDisposition MapStoreDisposition(ProcessStoreMutationDisposition disposition) =>
        disposition switch
        {
            ProcessStoreMutationDisposition.Applied => ProcessDurableRuntimeDisposition.Applied,
            ProcessStoreMutationDisposition.Replayed => ProcessDurableRuntimeDisposition.Replayed,
            ProcessStoreMutationDisposition.NotFound => ProcessDurableRuntimeDisposition.NotFound,
            ProcessStoreMutationDisposition.RevisionConflict => ProcessDurableRuntimeDisposition.RevisionConflict,
            ProcessStoreMutationDisposition.LeaseHeld => ProcessDurableRuntimeDisposition.LeaseHeld,
            ProcessStoreMutationDisposition.StaleFence => ProcessDurableRuntimeDisposition.StaleFence,
            ProcessStoreMutationDisposition.LeaseExpired => ProcessDurableRuntimeDisposition.LeaseExpired,
            ProcessStoreMutationDisposition.IdentityConflict => ProcessDurableRuntimeDisposition.IdentityConflict,
            ProcessStoreMutationDisposition.LocalMutationConflict => ProcessDurableRuntimeDisposition.LocalMutationConflict,
            ProcessStoreMutationDisposition.AlreadyExists => ProcessDurableRuntimeDisposition.IdentityConflict,
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unsupported Process-store mutation disposition.")
        };

    static void RequireInstance(ProcessInstanceId instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId.Value))
        {
            throw new ArgumentException("A durable Process runtime operation requires an instance identity.", nameof(instanceId));
        }
    }

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new(stage: "processDurableRuntime"));
}
