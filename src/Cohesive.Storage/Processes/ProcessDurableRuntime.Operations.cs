using System.Collections.Concurrent;
using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

public sealed partial class ProcessDurableRuntime
{
    readonly ConcurrentDictionary<(ProcessInstanceId InstanceId, EmissionId OperationId), SemaphoreSlim>
        operationGates = [];

    /// <summary>Advances one retained durable Request through all currently safe physical stages.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="plan">Exact compiled Process definition selected for restore validation.</param>
    /// <param name="instanceId">Logical Process instance that owns the Request.</param>
    /// <param name="operationId">Canonical Request emission and logical operation identity.</param>
    /// <returns>
    /// The latest durable operation state, aggregate snapshot, exact unresolved commit, and authored recovery intent.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="plan"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="instanceId"/> or <paramref name="operationId"/> is default.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The operation is cancelled before a physical boundary or while delegated adapter I/O is in flight.
    /// </exception>
    public async Task<ProcessDurableOperationResult> AdvanceOperationAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessInstanceId instanceId,
        EmissionId operationId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        RequireInstance(instanceId);
        if (string.IsNullOrWhiteSpace(operationId.Value))
        {
            throw new ArgumentException(
                "A durable Process operation requires its Request emission identity.",
                nameof(operationId));
        }
        context.ThrowIfCancellationRequested();

        var capabilityDiagnostics = ValidateCapabilities();
        if (!capabilityDiagnostics.IsEmpty)
        {
            return new(ProcessDurableRuntimeDisposition.Incompatible, diagnostics: capabilityDiagnostics);
        }

        var operationGate = operationGates.GetOrAdd((instanceId, operationId), static _ => new(1, 1));
        await operationGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        var gate = instanceGates.GetOrAdd(instanceId, static _ => new(1, 1));
        var gateHeld = false;
        try
        {
            await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            gateHeld = true;
            var loaded = await LoadOperationSnapshotAsync(context, plan, instanceId, operationId)
                .ConfigureAwait(false);
            if (loaded.Failure is not null)
            {
                return loaded.Failure;
            }

            var snapshot = loaded.Snapshot
                ?? throw new InvalidOperationException("A successful durable operation load returned no snapshot.");
            var operation = FindOperation(snapshot.Checkpoint, operationId)
                ?? throw new InvalidOperationException("A successful operation lookup returned no durable state.");
            var contracts = plan.ValidationContext.InteractionContracts
                ?? throw new InvalidOperationException("A durable Request requires the compiled interaction catalog.");
            var executor = new DurableOperationReferenceExecutor(contracts);
            ProcessDurableCommit? lastCommit = null;
            var disposition = ProcessDurableRuntimeDisposition.Replayed;

            async Task<(
                DurableOperationReconciliationObservation? Observation,
                OperationAttemptId AttemptId,
                OperationFence Fence,
                ProcessDurableOperationResult? Failure)> ReconcileOutsideInstanceGateAsync(
                    IDurableOperationAdapter selectedAdapter)
            {
                var acquiredForReconciliation = await AcquireOperationWorkerAsync(
                        context,
                        plan,
                        instanceId,
                        operationId)
                    .ConfigureAwait(false);
                if (acquiredForReconciliation.Failure is not null)
                {
                    return (null, default, default, acquiredForReconciliation.Failure);
                }
                var ownedSnapshot = acquiredForReconciliation.Snapshot
                    ?? throw new InvalidOperationException(
                        "A successful reconciliation acquisition returned no snapshot.");
                var ownedOperation = FindOperation(ownedSnapshot.Checkpoint, operationId)
                    ?? throw new InvalidOperationException(
                        "A successful reconciliation acquisition returned no durable operation.");
                if (ownedOperation.Status != DurableOperationStatus.ReconciliationRequired)
                {
                    return (
                        null,
                        default,
                        default,
                        CurrentOperationResult(
                            disposition,
                            ownedSnapshot,
                            ownedOperation,
                            lastCommit));
                }
                var sourceAttempt = ownedOperation.CurrentAttempt
                    ?? throw new InvalidOperationException(
                        "A reconciled durable operation has no source attempt.");
                var workerFence = ownedSnapshot.WorkerLease?.Fence
                    ?? throw new InvalidOperationException(
                        "External reconciliation requires its owning Process worker fence.");

                using var reconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    context.CancellationToken);
                var reconciliationContext = context.WithCancellationToken(
                    reconciliationCancellation.Token);
                gate.Release();
                gateHeld = false;
                var maintenanceTask = MaintainProcessWorkerOwnershipAsync(
                    reconciliationContext,
                    plan,
                    instanceId,
                    operationId,
                    sourceAttempt.Claim.AttemptId,
                    sourceAttempt.Claim.Fence,
                    workerFence,
                    gate);
                var reconciliationTask = DurableOperationReferenceExecutor.ReconcileAsync(
                        reconciliationContext,
                        ownedOperation,
                        selectedAdapter)
                    .AsTask();

                var firstCompleted = await Task.WhenAny(reconciliationTask, maintenanceTask)
                    .ConfigureAwait(false);
                if (firstCompleted == maintenanceTask)
                {
                    ProcessDurableOperationResult? earlyMaintenanceFailure;
                    try
                    {
                        earlyMaintenanceFailure = await maintenanceTask.ConfigureAwait(false);
                    }
                    catch
                    {
                        await reconciliationCancellation.CancelAsync().ConfigureAwait(false);
                        ObserveAbandonedAdapterTask(reconciliationTask);
                        throw;
                    }
                    if (earlyMaintenanceFailure is not null)
                    {
                        await reconciliationCancellation.CancelAsync().ConfigureAwait(false);
                        ObserveAbandonedAdapterTask(reconciliationTask);
                        return (null, default, default, earlyMaintenanceFailure);
                    }
                    await reconciliationCancellation.CancelAsync().ConfigureAwait(false);
                    ObserveAbandonedAdapterTask(reconciliationTask);
                    context.ThrowIfCancellationRequested();
                    throw new InvalidOperationException(
                        "Process worker ownership maintenance stopped before reconciliation completed.");
                }

                DurableOperationReconciliationObservation? reconciliationObservation = null;
                Exception? reconciliationException = null;
                try
                {
                    reconciliationObservation = await reconciliationTask.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    reconciliationException = exception;
                }

                if (reconciliationException is OperationCanceledException
                    && context.CancellationToken.IsCancellationRequested)
                {
                    await reconciliationCancellation.CancelAsync().ConfigureAwait(false);
                    _ = await maintenanceTask.ConfigureAwait(false);
                    throw reconciliationException;
                }
                if (reconciliationException is not null
                    && reconciliationException is not DurableOperationDeadlineElapsedException)
                {
                    // A thrown provider exception supplies no safe target-side evidence. Retain an authored
                    // unresolved observation rather than allowing an uncorrelated provider failure (including a
                    // provider-local cancellation) to erase the post-call recovery evidence.
                    reconciliationObservation = new DurableOperationUnresolved();
                    reconciliationException = null;
                }

                await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                gateHeld = true;
                await reconciliationCancellation.CancelAsync().ConfigureAwait(false);
                var maintenanceFailure = await maintenanceTask.ConfigureAwait(false);
                if (maintenanceFailure is not null)
                {
                    return (null, default, default, maintenanceFailure);
                }
                if (reconciliationException is DurableOperationDeadlineElapsedException)
                {
                    var deadlineLoad = await LoadOperationSnapshotAsync(
                            context,
                            plan,
                            instanceId,
                            operationId)
                        .ConfigureAwait(false);
                    if (deadlineLoad.Failure is not null)
                    {
                        return (null, default, default, deadlineLoad.Failure);
                    }
                    var deadlineSnapshot = deadlineLoad.Snapshot
                        ?? throw new InvalidOperationException(
                            "A successful post-deadline load returned no Process snapshot.");
                    var deadlineOperation = FindOperation(deadlineSnapshot.Checkpoint, operationId)
                        ?? throw new InvalidOperationException(
                            "A successful post-deadline load returned no durable operation.");
                    return (
                        null,
                        default,
                        default,
                        DeadlineBlockedResult(deadlineSnapshot, deadlineOperation, lastCommit));
                }
                if (reconciliationException is not null)
                {
                    throw reconciliationException;
                }
                if (reconciliationObservation is null)
                {
                    throw new InvalidOperationException(
                        "A durable operation adapter returned a null reconciliation observation.");
                }

                return (
                    reconciliationObservation,
                    sourceAttempt.Claim.AttemptId,
                    sourceAttempt.Claim.Fence,
                    null);
            }

            if (operation.Status == DurableOperationStatus.Dispositioned)
            {
                return CurrentOperationResult(disposition, snapshot, operation, lastCommit);
            }
            if (operation.Status == DurableOperationStatus.Acknowledged)
            {
                return await AdmitAcknowledgedOperationAsync(
                        context,
                        plan,
                        snapshot,
                        operation,
                        executor,
                        disposition,
                        lastCommit)
                    .ConfigureAwait(false);
            }
            if (RequiresExternalOperationWork(operation.Status)
                && snapshot.Checkpoint.Control.Mode == ProcessControlMode.Paused)
            {
                return new(
                    ProcessDurableRuntimeDisposition.Paused,
                    snapshot,
                    operation,
                    lastCommit,
                    DurableOperationReferenceExecutor.GetRecoveryIntent(operation),
                    [OperationBlocked(
                        "The Process is paused; no new durable Request dispatch, redispatch, or reconciliation was started.")]);
            }
            if (RequiresNewOperationAttempt(operation.Status)
                && !HasOpenOriginAttempt(snapshot.Checkpoint, operation))
            {
                return new(
                    ProcessDurableRuntimeDisposition.Terminal,
                    snapshot,
                    operation,
                    lastCommit,
                    diagnostics: [OperationBlocked(
                        "The Request's originating Process attempt is closed or no longer current; new physical work is forbidden.")]);
            }
            if (operation.Status == DurableOperationStatus.ReconciliationRequired)
            {
                if (HasElapsedDeadline(operation, context.UtcNow))
                {
                    return DeadlineBlockedResult(snapshot, operation, lastCommit);
                }
                if (!TryResolveAdapter(operation, out var reconciliationAdapter, out var adapterFailure, snapshot))
                {
                    return adapterFailure!;
                }
                try
                {
                    DurableOperationReferenceExecutor.ValidateReconciliationAdapterCapabilities(
                        operation.Binding,
                        reconciliationAdapter!.Capabilities);
                }
                catch (InvalidOperationException exception)
                {
                    return AdapterIncompatible(snapshot, operation, exception.Message);
                }

                var reconciliation = await ReconcileOutsideInstanceGateAsync(reconciliationAdapter!)
                    .ConfigureAwait(false);
                if (reconciliation.Failure is not null)
                {
                    return reconciliation.Failure;
                }
                return await RecordReconciliationAsync(
                        context,
                        plan,
                        instanceId,
                        operationId,
                        reconciliation.AttemptId,
                        reconciliation.Fence,
                        reconciliation.Observation
                            ?? throw new InvalidOperationException(
                                "Successful reconciliation execution returned no observation."),
                        executor,
                        disposition,
                        lastCommit)
                    .ConfigureAwait(false);
            }
            if (operation.Status is DurableOperationStatus.EscalationRequired
                or DurableOperationStatus.TerminalOutcomeRequired)
            {
                return CurrentOperationResult(disposition, snapshot, operation, lastCommit);
            }

            if (HasElapsedDeadline(operation, context.UtcNow))
            {
                return DeadlineBlockedResult(snapshot, operation, lastCommit);
            }

            if (!TryResolveAdapter(operation, out var adapter, out var resolutionFailure, snapshot))
            {
                return resolutionFailure!;
            }
            try
            {
                DurableOperationReferenceExecutor.ValidateAdapterCapabilities(
                    operation.Binding,
                    adapter!.Capabilities);
            }
            catch (InvalidOperationException exception)
            {
                return AdapterIncompatible(snapshot, operation, exception.Message);
            }

            if (operation.Status is (DurableOperationStatus.Claimed or DurableOperationStatus.Dispatched)
                && operation.CurrentAttempt is { } retainedAttempt
                && retainedAttempt.Claim.IsLiveAt(context.UtcNow)
                && !string.Equals(retainedAttempt.Claim.Claimant, options.WorkerId, StringComparison.Ordinal))
            {
                return new(
                    ProcessDurableRuntimeDisposition.LeaseHeld,
                    snapshot,
                    operation,
                    lastCommit);
            }

            var acquired = await AcquireOperationWorkerAsync(context, plan, instanceId, operationId)
                .ConfigureAwait(false);
            if (acquired.Failure is not null)
            {
                return acquired.Failure;
            }
            snapshot = acquired.Snapshot
                ?? throw new InvalidOperationException("A successful Process worker acquisition returned no snapshot.");
            operation = FindOperation(snapshot.Checkpoint, operationId)
                ?? throw new InvalidOperationException("A successful post-acquisition operation lookup returned no durable state.");

            if (HasElapsedDeadline(operation, context.UtcNow))
            {
                return DeadlineBlockedResult(snapshot, operation, lastCommit);
            }

            var currentAttempt = operation.CurrentAttempt;
            var requiresClaim = operation.Status is DurableOperationStatus.Pending or DurableOperationStatus.RetryEligible;
            if (operation.Status is DurableOperationStatus.Claimed or DurableOperationStatus.Dispatched)
            {
                currentAttempt = operation.CurrentAttempt
                    ?? throw new InvalidOperationException(
                        "A claimed or dispatched durable operation has no current attempt.");
                if (currentAttempt.Claim.IsLiveAt(context.UtcNow))
                {
                    if (!string.Equals(currentAttempt.Claim.Claimant, options.WorkerId, StringComparison.Ordinal))
                    {
                        return new(
                            ProcessDurableRuntimeDisposition.LeaseHeld,
                            snapshot,
                            operation,
                            lastCommit);
                    }
                }
                else
                {
                    // Claim evaluates the authored pre-/in-call expiry policy, closes the expired attempt, and
                    // allocates a strictly newer fence only when retry is safe. Never dispatch under an expired fence.
                    requiresClaim = true;
                }
            }

            if (requiresClaim)
            {
                var claim = executor.Claim(
                    operation,
                    ProcessDurableRuntimeIdentities.OperationAttempt(operationId, operation.Attempts.Length + 1),
                    options.WorkerId,
                    context.UtcNow);
                if (!ReferenceEquals(claim.State, operation))
                {
                    var persisted = await CommitOperationCutAsync(
                            context,
                            plan,
                            snapshot,
                            claim.State)
                        .ConfigureAwait(false);
                    if (!IsSuccessful(persisted.Disposition))
                    {
                        return persisted;
                    }
                    disposition = Merge(disposition, persisted.Disposition);
                    snapshot = RequireSnapshot(persisted);
                    operation = RequireOperation(persisted);
                    lastCommit = persisted.Commit;
                }

                switch (claim.Disposition)
                {
                    case DurableOperationClaimDisposition.Claimed:
                    case DurableOperationClaimDisposition.Replayed:
                        break;
                    case DurableOperationClaimDisposition.Completed:
                        return operation.Status == DurableOperationStatus.Acknowledged
                            ? await AdmitAcknowledgedOperationAsync(
                                    context,
                                    plan,
                                    snapshot,
                                    operation,
                                    executor,
                                    disposition,
                                    lastCommit)
                                .ConfigureAwait(false)
                            : CurrentOperationResult(disposition, snapshot, operation, lastCommit);
                    case DurableOperationClaimDisposition.RecoveryRequired:
                        return CurrentOperationResult(disposition, snapshot, operation, lastCommit);
                    case DurableOperationClaimDisposition.DeadlineElapsed:
                        return DeadlineBlockedResult(snapshot, operation, lastCommit);
                    case DurableOperationClaimDisposition.Busy:
                        return new(
                            ProcessDurableRuntimeDisposition.LeaseHeld,
                            snapshot,
                            operation,
                            lastCommit);
                    case DurableOperationClaimDisposition.IdentityConflict:
                        return new(
                            ProcessDurableRuntimeDisposition.IdentityConflict,
                            snapshot,
                            operation,
                            lastCommit);
                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(claim.Disposition),
                            claim.Disposition,
                            "Unsupported durable operation claim disposition.");
                }
            }

            var attempt = operation.CurrentAttempt
                ?? throw new InvalidOperationException("A claimable durable operation has no current attempt.");
            var renewal = executor.RenewClaim(
                operation,
                attempt.Claim.AttemptId,
                attempt.Claim.Fence,
                options.WorkerId,
                context.UtcNow);
            if (!ReferenceEquals(renewal.State, operation))
            {
                var persisted = await CommitOperationCutAsync(
                        context,
                        plan,
                        snapshot,
                        renewal.State)
                    .ConfigureAwait(false);
                if (!IsSuccessful(persisted.Disposition))
                {
                    return persisted;
                }
                disposition = Merge(disposition, persisted.Disposition);
                snapshot = RequireSnapshot(persisted);
                operation = RequireOperation(persisted);
                lastCommit = persisted.Commit;
                attempt = operation.CurrentAttempt
                    ?? throw new InvalidOperationException(
                        "A renewed durable operation lost its current attempt.");
            }
            switch (renewal.Disposition)
            {
                case DurableOperationRenewalDisposition.Renewed:
                case DurableOperationRenewalDisposition.Replayed:
                    break;
                case DurableOperationRenewalDisposition.Completed:
                    return operation.Status == DurableOperationStatus.Acknowledged
                        ? await AdmitAcknowledgedOperationAsync(
                                context,
                                plan,
                                snapshot,
                                operation,
                                executor,
                                disposition,
                                lastCommit)
                            .ConfigureAwait(false)
                        : CurrentOperationResult(disposition, snapshot, operation, lastCommit);
                case DurableOperationRenewalDisposition.StaleFence:
                    return new(
                        ProcessDurableRuntimeDisposition.StaleFence,
                        snapshot,
                        operation,
                        lastCommit);
                case DurableOperationRenewalDisposition.LeaseExpired:
                    return new(
                        ProcessDurableRuntimeDisposition.LeaseExpired,
                        snapshot,
                        operation,
                        lastCommit,
                        DurableOperationReferenceExecutor.GetRecoveryIntent(operation),
                        [OperationBlocked(
                            "The durable Request claim expired before dispatch and authored recovery now applies.")]);
                case DurableOperationRenewalDisposition.DeadlineElapsed:
                    return DeadlineBlockedResult(snapshot, operation, lastCommit);
                case DurableOperationRenewalDisposition.InvalidState:
                    return new(
                        ProcessDurableRuntimeDisposition.Rejected,
                        snapshot,
                        operation,
                        lastCommit,
                        diagnostics: [OperationBlocked(
                            "The durable Request attempt cannot be renewed before dispatch.")]);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(renewal.Disposition),
                        renewal.Disposition,
                        "Unsupported pre-dispatch operation renewal disposition.");
            }

            var dispatch = executor.BeginDispatch(
                operation,
                attempt.Claim.AttemptId,
                attempt.Claim.Fence,
                context.UtcNow);
            if (!ReferenceEquals(dispatch.State, operation))
            {
                var persisted = await CommitOperationCutAsync(
                        context,
                        plan,
                        snapshot,
                        dispatch.State)
                    .ConfigureAwait(false);
                if (!IsSuccessful(persisted.Disposition))
                {
                    return persisted;
                }
                disposition = Merge(disposition, persisted.Disposition);
                snapshot = RequireSnapshot(persisted);
                operation = RequireOperation(persisted);
                lastCommit = persisted.Commit;
            }

            switch (dispatch.Disposition)
            {
                case DurableOperationDispatchDisposition.Dispatched:
                case DurableOperationDispatchDisposition.Replayed:
                    break;
                case DurableOperationDispatchDisposition.Completed:
                    return operation.Status == DurableOperationStatus.Acknowledged
                        ? await AdmitAcknowledgedOperationAsync(
                                context,
                                plan,
                                snapshot,
                                operation,
                                executor,
                                disposition,
                                lastCommit)
                            .ConfigureAwait(false)
                        : CurrentOperationResult(disposition, snapshot, operation, lastCommit);
                case DurableOperationDispatchDisposition.RecoveryRequired:
                case DurableOperationDispatchDisposition.LeaseExpired:
                    return CurrentOperationResult(disposition, snapshot, operation, lastCommit);
                case DurableOperationDispatchDisposition.DeadlineElapsed:
                    return DeadlineBlockedResult(snapshot, operation, lastCommit);
                case DurableOperationDispatchDisposition.StaleFence:
                    return new(
                        ProcessDurableRuntimeDisposition.StaleFence,
                        snapshot,
                        operation,
                        lastCommit);
                case DurableOperationDispatchDisposition.InvalidState:
                    return new(
                        ProcessDurableRuntimeDisposition.Rejected,
                        snapshot,
                        operation,
                        lastCommit,
                        diagnostics: [OperationBlocked(
                            "The retained durable Request attempt cannot cross the dispatch boundary.")]);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(dispatch.Disposition),
                        dispatch.Disposition,
                        "Unsupported durable operation dispatch disposition.");
            }

            var invocation = dispatch.Invocation
                ?? throw new InvalidOperationException("A dispatchable operation returned no adapter invocation.");
            if (invocation.DeadlineUtc is { } deadlineUtc && context.UtcNow >= deadlineUtc)
            {
                return DeadlineBlockedResult(snapshot, operation, lastCommit);
            }
            try
            {
                // Perform runtime-owned pre-call validation outside the adapter exception classifier. A capability
                // mismatch is not evidence that an external effect may have happened.
                DurableOperationReferenceExecutor.ValidateAdapterCapabilities(
                    invocation.Binding,
                    adapter!.Capabilities);
            }
            catch (InvalidOperationException exception)
            {
                return AdapterIncompatible(snapshot, operation, exception.Message, lastCommit);
            }
            context.ThrowIfCancellationRequested();

            using var inFlightCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                context.CancellationToken);
            var inFlightContext = context.WithCancellationToken(inFlightCancellation.Token);
            var workerFence = snapshot.WorkerLease?.Fence
                ?? throw new InvalidOperationException(
                    "A dispatched durable operation requires its owning Process worker fence.");

            // Physical adapter I/O is not a local aggregate critical section. Lease maintenance briefly enters the
            // gate for each durable renewal, allowing Pause/Cancel/control work to interleave between heartbeats.
            gate.Release();
            gateHeld = false;
            var maintenanceTask = MaintainOperationOwnershipAsync(
                inFlightContext,
                plan,
                instanceId,
                operationId,
                invocation.AttemptId,
                invocation.Fence,
                workerFence,
                operation.Binding.ClaimLease,
                executor,
                gate);
            var adapterTask = InvokeAdapterAsync(inFlightContext, invocation, adapter!);

            var firstCompleted = await Task.WhenAny(adapterTask, maintenanceTask).ConfigureAwait(false);
            if (firstCompleted == maintenanceTask)
            {
                ProcessDurableOperationResult? earlyMaintenanceFailure;
                try
                {
                    earlyMaintenanceFailure = await maintenanceTask.ConfigureAwait(false);
                }
                catch
                {
                    await inFlightCancellation.CancelAsync().ConfigureAwait(false);
                    ObserveAbandonedAdapterTask(adapterTask);
                    throw;
                }
                if (earlyMaintenanceFailure is not null)
                {
                    await inFlightCancellation.CancelAsync().ConfigureAwait(false);
                    ObserveAbandonedAdapterTask(adapterTask);
                    return earlyMaintenanceFailure;
                }
                await inFlightCancellation.CancelAsync().ConfigureAwait(false);
                ObserveAbandonedAdapterTask(adapterTask);
                context.ThrowIfCancellationRequested();
                throw new InvalidOperationException(
                    "Durable operation ownership maintenance stopped before adapter execution completed.");
            }

            DurableOperationAttemptObservation? observation = null;
            Exception? adapterException = null;
            try
            {
                observation = await adapterTask.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                adapterException = exception;
            }

            if (adapterException is OperationCanceledException && context.CancellationToken.IsCancellationRequested)
            {
                await inFlightCancellation.CancelAsync().ConfigureAwait(false);
                _ = await maintenanceTask.ConfigureAwait(false);
                throw adapterException;
            }

            await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            gateHeld = true;
            await inFlightCancellation.CancelAsync().ConfigureAwait(false);
            var maintenanceFailure = await maintenanceTask.ConfigureAwait(false);
            if (maintenanceFailure is not null)
            {
                return maintenanceFailure;
            }
            if (adapterException is not null)
            {
                observation = new DurableOperationFailureObservation(
                    operationExceptionClassifier.Classify(adapterException));
            }
            if (observation is null)
            {
                throw new InvalidOperationException(
                    "A durable operation adapter returned a null attempt observation.");
            }

            var refreshed = await AcquireOperationWorkerAsync(context, plan, instanceId, operationId)
                .ConfigureAwait(false);
            if (refreshed.Failure is not null)
            {
                return refreshed.Failure;
            }
            snapshot = refreshed.Snapshot
                ?? throw new InvalidOperationException("A successful post-dispatch acquisition returned no snapshot.");
            operation = FindOperation(snapshot.Checkpoint, operationId)
                ?? throw new InvalidOperationException("A post-dispatch operation lookup returned no durable state.");

            var recorded = executor.RecordObservation(
                operation,
                invocation.AttemptId,
                invocation.Fence,
                observation,
                context.UtcNow);
            if (!ReferenceEquals(recorded.State, operation))
            {
                var persisted = await CommitOperationCutAsync(
                        context,
                        plan,
                        snapshot,
                        recorded.State)
                    .ConfigureAwait(false);
                if (!IsSuccessful(persisted.Disposition))
                {
                    return persisted;
                }
                disposition = Merge(disposition, persisted.Disposition);
                snapshot = RequireSnapshot(persisted);
                operation = RequireOperation(persisted);
                lastCommit = persisted.Commit;
            }

            var observationFailure = ObservationFailure(
                recorded.Disposition,
                snapshot,
                operation,
                lastCommit);
            if (observationFailure is not null)
            {
                return observationFailure;
            }

            if (operation.Status == DurableOperationStatus.Acknowledged)
            {
                return await AdmitAcknowledgedOperationAsync(
                        context,
                        plan,
                        snapshot,
                        operation,
                        executor,
                        disposition,
                        lastCommit)
                    .ConfigureAwait(false);
            }
            if (operation.Status == DurableOperationStatus.ReconciliationRequired)
            {
                if (HasElapsedDeadline(operation, context.UtcNow))
                {
                    return DeadlineBlockedResult(snapshot, operation, lastCommit);
                }
                try
                {
                    DurableOperationReferenceExecutor.ValidateReconciliationAdapterCapabilities(
                        operation.Binding,
                        adapter!.Capabilities);
                }
                catch (InvalidOperationException exception)
                {
                    return AdapterIncompatible(snapshot, operation, exception.Message, lastCommit);
                }

                var reconciliation = await ReconcileOutsideInstanceGateAsync(adapter!)
                    .ConfigureAwait(false);
                if (reconciliation.Failure is not null)
                {
                    return reconciliation.Failure;
                }
                return await RecordReconciliationAsync(
                        context,
                        plan,
                        instanceId,
                        operationId,
                        reconciliation.AttemptId,
                        reconciliation.Fence,
                        reconciliation.Observation
                            ?? throw new InvalidOperationException(
                                "Successful reconciliation execution returned no observation."),
                        executor,
                        disposition,
                        lastCommit)
                    .ConfigureAwait(false);
            }

            return CurrentOperationResult(disposition, snapshot, operation, lastCommit);
        }
        finally
        {
            if (gateHeld)
            {
                gate.Release();
            }
            operationGate.Release();
        }
    }

    async Task<ProcessDurableOperationResult> RecordReconciliationAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessInstanceId instanceId,
        EmissionId operationId,
        OperationAttemptId sourceAttemptId,
        OperationFence sourceFence,
        DurableOperationReconciliationObservation observation,
        DurableOperationReferenceExecutor executor,
        ProcessDurableRuntimeDisposition disposition,
        ProcessDurableCommit? lastCommit)
    {
        var refreshed = await AcquireOperationWorkerAsync(context, plan, instanceId, operationId)
            .ConfigureAwait(false);
        if (refreshed.Failure is not null)
        {
            return refreshed.Failure;
        }
        var snapshot = refreshed.Snapshot
            ?? throw new InvalidOperationException("A successful post-reconciliation acquisition returned no snapshot.");
        var operation = FindOperation(snapshot.Checkpoint, operationId)
            ?? throw new InvalidOperationException("A post-reconciliation operation lookup returned no durable state.");

        var recorded = executor.RecordReconciliation(
            operation,
            sourceAttemptId,
            sourceFence,
            observation,
            context.UtcNow);
        if (!ReferenceEquals(recorded.State, operation))
        {
            var persisted = await CommitOperationCutAsync(
                    context,
                    plan,
                    snapshot,
                    recorded.State)
                .ConfigureAwait(false);
            if (!IsSuccessful(persisted.Disposition))
            {
                return persisted;
            }
            disposition = Merge(disposition, persisted.Disposition);
            snapshot = RequireSnapshot(persisted);
            operation = RequireOperation(persisted);
            lastCommit = persisted.Commit;
        }

        var observationFailure = ObservationFailure(
            recorded.Disposition,
            snapshot,
            operation,
            lastCommit);
        if (observationFailure is not null)
        {
            return observationFailure;
        }

        return operation.Status == DurableOperationStatus.Acknowledged
            ? await AdmitAcknowledgedOperationAsync(
                    context,
                    plan,
                    snapshot,
                    operation,
                    executor,
                    disposition,
                    lastCommit)
                .ConfigureAwait(false)
            : CurrentOperationResult(disposition, snapshot, operation, lastCommit);
    }

    async Task<ProcessDurableOperationResult?> MaintainProcessWorkerOwnershipAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessInstanceId instanceId,
        EmissionId operationId,
        OperationAttemptId sourceAttemptId,
        OperationFence sourceFence,
        ProcessWorkerFence workerFence,
        SemaphoreSlim gate)
    {
        var interval = OperationRenewalInterval(options.WorkerLease, options.WorkerLease);
        try
        {
            while (true)
            {
                await Task.Delay(interval, context.TimeProvider, context.CancellationToken)
                    .ConfigureAwait(false);
                await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                try
                {
                    var loadedResult = await LoadOperationSnapshotAsync(context, plan, instanceId, operationId)
                        .ConfigureAwait(false);
                    if (loadedResult.Failure is not null)
                    {
                        return loadedResult.Failure;
                    }
                    var snapshot = loadedResult.Snapshot
                        ?? throw new InvalidOperationException(
                            "A successful reconciliation-maintenance load returned no snapshot.");
                    var operation = FindOperation(snapshot.Checkpoint, operationId)
                        ?? throw new InvalidOperationException(
                            "A successful reconciliation-maintenance lookup returned no operation.");
                    var sourceAttempt = operation.CurrentAttempt;
                    if (sourceAttempt is null
                        || sourceAttempt.Claim.AttemptId != sourceAttemptId
                        || sourceAttempt.Claim.Fence != sourceFence)
                    {
                        return new(
                            ProcessDurableRuntimeDisposition.StaleFence,
                            snapshot,
                            operation);
                    }
                    if (operation.Status != DurableOperationStatus.ReconciliationRequired)
                    {
                        return CurrentOperationResult(
                            ProcessDurableRuntimeDisposition.Replayed,
                            snapshot,
                            operation,
                            commit: null);
                    }

                    var observedAtUtc = context.UtcNow;
                    if (HasElapsedDeadline(operation, observedAtUtc))
                    {
                        return DeadlineBlockedResult(snapshot, operation, commit: null);
                    }
                    var workerLease = snapshot.WorkerLease;
                    if (workerLease is null
                        || workerLease.Fence != workerFence
                        || !string.Equals(workerLease.Owner, options.WorkerId, StringComparison.Ordinal))
                    {
                        return new(
                            ProcessDurableRuntimeDisposition.StaleFence,
                            snapshot,
                            operation);
                    }
                    if (!workerLease.IsLive(observedAtUtc))
                    {
                        return new(
                            ProcessDurableRuntimeDisposition.LeaseExpired,
                            snapshot,
                            operation,
                            diagnostics: [OperationBlocked(
                                "The Process worker lease expired while external reconciliation was in flight.")]);
                    }

                    var workerRenewal = await RenewWorkerExactAsync(
                            context,
                            instanceId,
                            options.WorkerId,
                            workerFence,
                            options.WorkerLease,
                            observedAtUtc)
                        .ConfigureAwait(false);
                    if (workerRenewal is null)
                    {
                        return new(
                            ProcessDurableRuntimeDisposition.CommitOutcomeUnknown,
                            snapshot,
                            operation);
                    }
                    if (workerRenewal.Disposition is not (ProcessStoreMutationDisposition.Applied
                        or ProcessStoreMutationDisposition.Replayed))
                    {
                        var rejected = workerRenewal.Snapshot;
                        return new(
                            MapStoreDisposition(workerRenewal.Disposition),
                            rejected,
                            rejected is null ? operation : FindOperation(rejected.Checkpoint, operationId));
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    async Task<ProcessDurableOperationResult?> MaintainOperationOwnershipAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessInstanceId instanceId,
        EmissionId operationId,
        OperationAttemptId attemptId,
        OperationFence operationFence,
        ProcessWorkerFence workerFence,
        TimeSpan operationLease,
        DurableOperationReferenceExecutor executor,
        SemaphoreSlim gate)
    {
        var interval = OperationRenewalInterval(options.WorkerLease, operationLease);
        try
        {
            while (true)
            {
                await Task.Delay(interval, context.TimeProvider, context.CancellationToken)
                    .ConfigureAwait(false);
                await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                try
                {
                    var loadedResult = await LoadOperationSnapshotAsync(context, plan, instanceId, operationId)
                        .ConfigureAwait(false);
                    if (loadedResult.Failure is not null)
                    {
                        return loadedResult.Failure;
                    }
                    var snapshot = loadedResult.Snapshot
                        ?? throw new InvalidOperationException(
                            "A successful ownership-maintenance load returned no snapshot.");
                    var operation = FindOperation(snapshot.Checkpoint, operationId)
                        ?? throw new InvalidOperationException(
                            "A successful ownership-maintenance lookup returned no operation.");
                    var attempt = operation.CurrentAttempt;
                    if (attempt is null
                        || attempt.Claim.AttemptId != attemptId
                        || attempt.Claim.Fence != operationFence
                        || !string.Equals(attempt.Claim.Claimant, options.WorkerId, StringComparison.Ordinal))
                    {
                        return new(
                            ProcessDurableRuntimeDisposition.StaleFence,
                            snapshot,
                            operation);
                    }

                    var observedAtUtc = context.UtcNow;
                    if (HasElapsedDeadline(operation, observedAtUtc))
                    {
                        return DeadlineBlockedResult(snapshot, operation, commit: null);
                    }
                    var workerLease = snapshot.WorkerLease;
                    if (workerLease is null
                        || workerLease.Fence != workerFence
                        || !string.Equals(workerLease.Owner, options.WorkerId, StringComparison.Ordinal))
                    {
                        return new(
                            ProcessDurableRuntimeDisposition.StaleFence,
                            snapshot,
                            operation);
                    }
                    if (!workerLease.IsLive(observedAtUtc))
                    {
                        return new(
                            ProcessDurableRuntimeDisposition.LeaseExpired,
                            snapshot,
                            operation,
                            diagnostics: [OperationBlocked(
                                "The Process worker lease expired while external Request I/O was in flight.")]);
                    }

                    var workerRenewal = await RenewWorkerExactAsync(
                            context,
                            instanceId,
                            options.WorkerId,
                            workerFence,
                            options.WorkerLease,
                            observedAtUtc)
                        .ConfigureAwait(false);
                    if (workerRenewal is null)
                    {
                        return new(
                            ProcessDurableRuntimeDisposition.CommitOutcomeUnknown,
                            snapshot,
                            operation);
                    }
                    if (workerRenewal.Disposition is not (ProcessStoreMutationDisposition.Applied
                        or ProcessStoreMutationDisposition.Replayed))
                    {
                        var rejected = workerRenewal.Snapshot;
                        return new(
                            MapStoreDisposition(workerRenewal.Disposition),
                            rejected,
                            rejected is null ? operation : FindOperation(rejected.Checkpoint, operationId));
                    }

                    snapshot = workerRenewal.Snapshot
                        ?? throw new InvalidOperationException(
                            "A successful Process worker renewal returned no snapshot.");
                    operation = FindOperation(snapshot.Checkpoint, operationId)
                        ?? throw new InvalidOperationException(
                            "A successful Process worker renewal lost the durable operation.");
                    var renewed = executor.RenewClaim(
                        operation,
                        attemptId,
                        operationFence,
                        options.WorkerId,
                        observedAtUtc);
                    ProcessDurableCommit? renewalCommit = null;
                    if (!ReferenceEquals(renewed.State, operation))
                    {
                        var persisted = await CommitOperationCutAsync(
                                context,
                                plan,
                                snapshot,
                                renewed.State)
                            .ConfigureAwait(false);
                        if (!IsSuccessful(persisted.Disposition))
                        {
                            return persisted;
                        }
                        snapshot = RequireSnapshot(persisted);
                        operation = RequireOperation(persisted);
                        renewalCommit = persisted.Commit;
                    }

                    switch (renewed.Disposition)
                    {
                        case DurableOperationRenewalDisposition.Renewed:
                        case DurableOperationRenewalDisposition.Replayed:
                            break;
                        case DurableOperationRenewalDisposition.Completed:
                            return CurrentOperationResult(
                                ProcessDurableRuntimeDisposition.Replayed,
                                snapshot,
                                operation,
                                renewalCommit);
                        case DurableOperationRenewalDisposition.StaleFence:
                            return new(
                                ProcessDurableRuntimeDisposition.StaleFence,
                                snapshot,
                                operation,
                                renewalCommit);
                        case DurableOperationRenewalDisposition.LeaseExpired:
                            return new(
                                ProcessDurableRuntimeDisposition.LeaseExpired,
                                snapshot,
                                operation,
                                renewalCommit,
                                DurableOperationReferenceExecutor.GetRecoveryIntent(operation),
                                [OperationBlocked(
                                    "The durable Request claim expired while external I/O was in flight; authored recovery now applies.")]);
                        case DurableOperationRenewalDisposition.DeadlineElapsed:
                            return DeadlineBlockedResult(snapshot, operation, renewalCommit);
                        case DurableOperationRenewalDisposition.InvalidState:
                            return new(
                                ProcessDurableRuntimeDisposition.Rejected,
                                snapshot,
                                operation,
                                renewalCommit,
                                diagnostics: [OperationBlocked(
                                    "The durable Request attempt is no longer renewable while adapter I/O is in flight.")]);
                        default:
                            throw new ArgumentOutOfRangeException(
                                nameof(renewed.Disposition),
                                renewed.Disposition,
                                "Unsupported durable operation renewal disposition.");
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    async Task<ProcessStoreMutationResult?> RenewWorkerExactAsync(
        OperationContext context,
        ProcessInstanceId instanceId,
        string owner,
        ProcessWorkerFence fence,
        TimeSpan leaseDuration,
        DateTimeOffset observedAtUtc) =>
        await RetryAmbiguousStoreMutationAsync(
                context,
                () => store.RenewWorkerAsync(
                        context,
                        instanceId,
                        owner,
                        fence,
                        leaseDuration,
                        observedAtUtc))
            .ConfigureAwait(false);

    static async Task<DurableOperationAttemptObservation> InvokeAdapterAsync(
        OperationContext context,
        DurableOperationInvocation invocation,
        IDurableOperationAdapter adapter) =>
        await adapter.ExecuteAsync(context, invocation).ConfigureAwait(false)
        ?? throw new InvalidOperationException("A durable operation adapter returned a null attempt observation.");

    static void ObserveAbandonedAdapterTask(Task adapterTask) =>
        _ = adapterTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    static TimeSpan OperationRenewalInterval(TimeSpan workerLease, TimeSpan operationLease)
    {
        const long renewalsPerLease = 3;
        var shortestLeaseTicks = Math.Min(workerLease.Ticks, operationLease.Ticks);
        return TimeSpan.FromTicks(Math.Max(1, shortestLeaseTicks / renewalsPerLease));
    }

    async Task<ProcessDurableOperationResult> AdmitAcknowledgedOperationAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessDurableStoreSnapshot snapshot,
        DurableOperationState operation,
        DurableOperationReferenceExecutor executor,
        ProcessDurableRuntimeDisposition disposition,
        ProcessDurableCommit? lastCommit)
    {
        if (operation.Request.ResponseTarget is not ProcessTokenInteractionTarget target)
        {
            return new(
                ProcessDurableRuntimeDisposition.Unsupported,
                snapshot,
                operation,
                lastCommit,
                DurableOperationReferenceExecutor.GetRecoveryIntent(operation),
                [Error(
                    ProcessDurableRuntimeDiagnosticCodes.OperationTargetUnsupported,
                    "The Storage Process runtime currently admits durable results only to Process-token targets.",
                    "/operation/request/responseTarget")]);
        }

        var targetObservation = ObserveTarget(snapshot.Checkpoint, operation, target);
        var admission = executor.AdmitResult(operation, targetObservation.Observation);
        if (admission.Kind == DurableOperationAdmissionResultKind.Duplicate)
        {
            return CurrentOperationResult(disposition, snapshot, admission.State, lastCommit);
        }
        if (admission.Kind != DurableOperationAdmissionResultKind.Dispositioned
            || admission.Admission is null)
        {
            return new(
                ProcessDurableRuntimeDisposition.Rejected,
                snapshot,
                operation,
                lastCommit,
                diagnostics: [OperationBlocked(
                    $"The acknowledged durable Request result could not be dispositioned: {admission.Kind}.")]);
        }

        var acquired = await AcquireOperationWorkerAsync(
                context,
                plan,
                snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId,
                operation.OperationId)
            .ConfigureAwait(false);
        if (acquired.Failure is not null)
        {
            return acquired.Failure;
        }
        snapshot = acquired.Snapshot
            ?? throw new InvalidOperationException("A successful admission acquisition returned no snapshot.");
        operation = FindOperation(snapshot.Checkpoint, operation.OperationId)
            ?? throw new InvalidOperationException("A post-acquisition admission lookup returned no durable state.");
        if (operation.Status == DurableOperationStatus.Dispositioned)
        {
            return CurrentOperationResult(disposition, snapshot, operation, lastCommit);
        }
        if (operation.Status != DurableOperationStatus.Acknowledged)
        {
            return new(
                ProcessDurableRuntimeDisposition.RevisionConflict,
                snapshot,
                operation,
                lastCommit,
                diagnostics: [OperationBlocked(
                    "The durable Request changed before its acknowledged result could be admitted.")]);
        }

        targetObservation = ObserveTarget(snapshot.Checkpoint, operation, target);
        admission = executor.AdmitResult(operation, targetObservation.Observation);
        if (admission.Kind == DurableOperationAdmissionResultKind.Duplicate)
        {
            return CurrentOperationResult(disposition, snapshot, admission.State, lastCommit);
        }
        if (admission.Kind != DurableOperationAdmissionResultKind.Dispositioned
            || admission.Admission is null)
        {
            return new(
                ProcessDurableRuntimeDisposition.Rejected,
                snapshot,
                operation,
                lastCommit,
                diagnostics: [OperationBlocked(
                    $"The acknowledged durable Request result could not be dispositioned after acquisition: {admission.Kind}.")]);
        }

        ProcessActivationInput? input = null;
        if (admission.Admission.AdvancesTarget)
        {
            var reply = admission.State.CreateReply(
                ProcessDurableRuntimeIdentities.OperationReply(operation.OperationId),
                ProcessDurableRuntimeIdentities.OperationReplyIdempotency(operation.OperationId),
                operation.Request.Context.Ordering,
                operation.Request.Context.Provenance);
            input = new(target, reply);
        }

        var persisted = await CommitOperationCutAsync(
                context,
                plan,
                snapshot,
                admission.State,
                input)
            .ConfigureAwait(false);
        if (!IsSuccessful(persisted.Disposition))
        {
            return persisted;
        }
        disposition = Merge(disposition, persisted.Disposition);
        return new(
            disposition,
            persisted.Snapshot,
            persisted.Operation,
            persisted.Commit,
            diagnostics: targetObservation.UsedMissingPriorFallback
                ? [OperationBlocked(
                    "The closed Request wait had no winning input disposition to reuse; the canonical Process fallback rejected the result.")]
                : []);
    }

    async Task<(ProcessDurableStoreSnapshot? Snapshot, ProcessDurableOperationResult? Failure)>
        LoadOperationSnapshotAsync(
            OperationContext context,
            CompiledProcessPlan plan,
            ProcessInstanceId instanceId,
            EmissionId operationId)
    {
        var loaded = await store.LoadAsync(context, instanceId).ConfigureAwait(false);
        if (loaded is null)
        {
            return (null, new(ProcessDurableRuntimeDisposition.NotFound));
        }

        var compatibility = Validate(plan, loaded);
        if (!compatibility.IsValid)
        {
            return (null, new(
                ProcessDurableRuntimeDisposition.Incompatible,
                loaded,
                FindOperation(loaded.Checkpoint, operationId),
                diagnostics: compatibility.Diagnostics));
        }

        if (FindOperation(loaded.Checkpoint, operationId) is null)
        {
            return (null, new(
                ProcessDurableRuntimeDisposition.Rejected,
                loaded,
                diagnostics: [Error(
                    ProcessDurableRuntimeDiagnosticCodes.OperationNotFound,
                    $"Durable Request operation '{operationId.Value}' is not retained by this Process instance.",
                    "/operationId")]));
        }

        return (loaded, null);
    }

    async Task<(ProcessDurableStoreSnapshot? Snapshot, ProcessDurableOperationResult? Failure)>
        AcquireOperationWorkerAsync(
            OperationContext context,
            CompiledProcessPlan plan,
            ProcessInstanceId instanceId,
            EmissionId operationId)
    {
        var loadedResult = await LoadOperationSnapshotAsync(context, plan, instanceId, operationId)
            .ConfigureAwait(false);
        if (loadedResult.Failure is not null)
        {
            return loadedResult;
        }
        var loaded = loadedResult.Snapshot
            ?? throw new InvalidOperationException("A successful durable operation load returned no snapshot.");

        var acquired = await AcquireOrRenewExactAsync(
                context,
                plan,
                instanceId,
                loaded.Revision,
                options.WorkerId,
                options.WorkerLease,
                context.UtcNow)
            .ConfigureAwait(false);
        if (acquired is null)
        {
            return (null, new(
                ProcessDurableRuntimeDisposition.CommitOutcomeUnknown,
                loaded,
                FindOperation(loaded.Checkpoint, operationId)));
        }
        if (acquired.Disposition is not (ProcessStoreMutationDisposition.Applied
            or ProcessStoreMutationDisposition.Replayed))
        {
            var rejectedSnapshot = acquired.Snapshot;
            return (null, new(
                MapStoreDisposition(acquired.Disposition),
                rejectedSnapshot,
                rejectedSnapshot is null
                    ? null
                    : FindOperation(rejectedSnapshot.Checkpoint, operationId)));
        }

        var snapshot = acquired.Snapshot
            ?? throw new InvalidOperationException("A successful Process worker acquisition returned no snapshot.");
        var compatibility = Validate(plan, snapshot);
        if (!compatibility.IsValid)
        {
            return (null, new(
                ProcessDurableRuntimeDisposition.Incompatible,
                snapshot,
                FindOperation(snapshot.Checkpoint, operationId),
                diagnostics: compatibility.Diagnostics));
        }
        return (snapshot, null);
    }

    async Task<ProcessDurableOperationResult> CommitOperationCutAsync(
        OperationContext context,
        CompiledProcessPlan plan,
        ProcessDurableStoreSnapshot snapshot,
        DurableOperationState operation,
        ProcessActivationInput? admittedReply = null)
    {
        var committedAtUtc = context.UtcNow;
        if (!ProcessDurableCheckpointReducer.TryApplyDurableOperation(
                snapshot.Checkpoint,
                operation,
                committedAtUtc,
                admittedReply,
                out var replacement,
                out var reductionDiagnostics))
        {
            var durableOperation = FindOperation(snapshot.Checkpoint, operation.OperationId);
            var identityConflict = reductionDiagnostics.Any(static diagnostic =>
                diagnostic.Code == ProcessDurableRuntimeDiagnosticCodes.OperationReplyIdentityConflict);
            return new(
                identityConflict
                    ? ProcessDurableRuntimeDisposition.IdentityConflict
                    : ProcessDurableRuntimeDisposition.Rejected,
                snapshot,
                durableOperation,
                diagnostics: reductionDiagnostics);
        }
        var candidate = replacement
            ?? throw new InvalidOperationException(
                "A successful durable operation reduction returned no replacement checkpoint.");
        var compatibility = ProcessCheckpointCompatibilityValidator.Validate(plan, candidate);
        if (!compatibility.IsValid)
        {
            return new(
                ProcessDurableRuntimeDisposition.Incompatible,
                snapshot,
                operation,
                diagnostics: compatibility.Diagnostics);
        }

        var lease = snapshot.WorkerLease
            ?? throw new InvalidOperationException("A durable operation commit requires the current Process worker lease.");
        var commit = new ProcessDurableCommit(
            ProcessDurableRuntimeIdentities.OperationLedgerCommit(operation),
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
                operation,
                commit);
        }

        var resultSnapshot = committed.Snapshot;
        if (committed.Disposition is ProcessStoreMutationDisposition.Applied
            or ProcessStoreMutationDisposition.Replayed)
        {
            resultSnapshot = await store.LoadAsync(
                    context,
                    snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId)
                .ConfigureAwait(false);
        }
        var retainedOperation = resultSnapshot is null
            ? operation
            : FindOperation(resultSnapshot.Checkpoint, operation.OperationId) ?? operation;
        return new(
            MapSuccessfulStoreDisposition(committed.Disposition),
            resultSnapshot,
            retainedOperation,
            commit);
    }

    bool TryResolveAdapter(
        DurableOperationState operation,
        out IDurableOperationAdapter? adapter,
        out ProcessDurableOperationResult? failure,
        ProcessDurableStoreSnapshot snapshot)
    {
        if (operationAdapterResolver.TryResolve(operation.Request, out adapter) && adapter is not null)
        {
            failure = null;
            return true;
        }

        adapter = null;
        failure = new(
            ProcessDurableRuntimeDisposition.Incompatible,
            snapshot,
            operation,
            recoveryIntent: DurableOperationReferenceExecutor.GetRecoveryIntent(operation),
            diagnostics: [Error(
                ProcessDurableRuntimeDiagnosticCodes.OperationAdapterUnavailable,
                "No durable operation adapter is registered for the exact Request contract.",
                "/operation/request/contract")]);
        return false;
    }

    static ProcessDurableOperationResult AdapterIncompatible(
        ProcessDurableStoreSnapshot snapshot,
        DurableOperationState operation,
        string message,
        ProcessDurableCommit? commit = null) =>
        new(
            ProcessDurableRuntimeDisposition.Incompatible,
            snapshot,
            operation,
            commit,
            DurableOperationReferenceExecutor.GetRecoveryIntent(operation),
            [Error(
                ProcessDurableRuntimeDiagnosticCodes.OperationAdapterIncompatible,
                message,
                "/operation/binding")]);

    static (DurableOperationTargetObservation Observation, bool UsedMissingPriorFallback) ObserveTarget(
        ProcessDurableCheckpoint checkpoint,
        DurableOperationState operation,
        ProcessTokenInteractionTarget target)
    {
        if (target.Continuation != checkpoint.ContinuationIdentity)
        {
            return (
                new(
                    target,
                    DurableOperationResultArrival.Stale,
                    DurableOperationAdmissionDisposition.Rejected),
                true);
        }

        var token = checkpoint.Continuation.Tokens.FirstOrDefault(candidate => candidate.Id == target.Token);
        if (token is null)
        {
            return (
                new(
                    target,
                    DurableOperationResultArrival.Stale,
                    DurableOperationAdmissionDisposition.Rejected),
                true);
        }

        var wait = target.WaitRegistrationId is { } registration
            ? checkpoint.Continuation.Waits.FirstOrDefault(candidate => candidate.RegistrationId == registration)
            : checkpoint.Continuation.Waits.FirstOrDefault(candidate =>
                candidate.Token == target.Token
                && candidate.Kind == ProcessWaitKind.Request
                && candidate.ObligationEmission == operation.OperationId);
        if (wait is null
            || wait.Token != target.Token
            || wait.Kind != ProcessWaitKind.Request
            || wait.ObligationEmission != operation.OperationId)
        {
            return (
                new(
                    target,
                    IsTerminal(token.Disposition)
                        ? DurableOperationResultArrival.Late
                        : DurableOperationResultArrival.Stale,
                    DurableOperationAdmissionDisposition.Rejected),
                true);
        }

        if (!wait.Active || IsTerminal(token.Disposition))
        {
            var prior = FindPriorDisposition(checkpoint, wait);
            return (
                new(
                    target,
                    DurableOperationResultArrival.Late,
                    prior ?? DurableOperationAdmissionDisposition.Rejected),
                prior is null);
        }

        var outstanding = checkpoint.Continuation.OutstandingRequests.Any(candidate =>
            candidate.Token == target.Token
            && candidate.Emission == operation.OperationId);
        return outstanding && token.Disposition == ExecutionTokenDisposition.Waiting
            ? (new(target, DurableOperationResultArrival.Eligible), false)
            : (
                new(
                    target,
                    DurableOperationResultArrival.Stale,
                    DurableOperationAdmissionDisposition.Rejected),
                true);
    }

    static DurableOperationAdmissionDisposition? FindPriorDisposition(
        ProcessDurableCheckpoint checkpoint,
        ProcessWaitState wait)
    {
        if (wait.WinnerInput is not { } winner)
        {
            return null;
        }
        var receipt = checkpoint.Inbox.FirstOrDefault(candidate => candidate.EmissionId == winner)?.Receipt;
        return receipt?.Disposition switch
        {
            ProcessInputAdmissionDisposition.Consumed => DurableOperationAdmissionDisposition.Accepted,
            ProcessInputAdmissionDisposition.Observed => DurableOperationAdmissionDisposition.Observed,
            ProcessInputAdmissionDisposition.Rejected => DurableOperationAdmissionDisposition.Rejected,
            _ => null
        };
    }

    static ProcessDurableOperationResult CurrentOperationResult(
        ProcessDurableRuntimeDisposition disposition,
        ProcessDurableStoreSnapshot snapshot,
        DurableOperationState operation,
        ProcessDurableCommit? commit)
    {
        var diagnostics = operation.Status == DurableOperationStatus.TerminalOutcomeRequired
            ? (ImmutableArray<DocumentValidationDiagnostic>)[OperationBlocked(
                "The durable Request requires its declared typed terminal outcome before it can advance.")]
            : [];
        return new(
            disposition,
            snapshot,
            operation,
            commit,
            DurableOperationReferenceExecutor.GetRecoveryIntent(operation),
            diagnostics);
    }

    static ProcessDurableOperationResult? ObservationFailure(
        DurableOperationObservationDisposition disposition,
        ProcessDurableStoreSnapshot snapshot,
        DurableOperationState operation,
        ProcessDurableCommit? commit) => disposition switch
        {
            DurableOperationObservationDisposition.Acknowledged => null,
            DurableOperationObservationDisposition.Replayed => null,
            DurableOperationObservationDisposition.RetryEligible => null,
            DurableOperationObservationDisposition.ReconciliationRequired => null,
            DurableOperationObservationDisposition.TerminalOutcomeRequired => null,
            DurableOperationObservationDisposition.EscalationRequired => null,
            DurableOperationObservationDisposition.StaleFence => new(
                ProcessDurableRuntimeDisposition.StaleFence,
                snapshot,
                operation,
                commit),
            DurableOperationObservationDisposition.ConflictingOutcome => new(
                ProcessDurableRuntimeDisposition.IdentityConflict,
                snapshot,
                operation,
                commit,
                diagnostics: [OperationBlocked(
                    "Adapter evidence conflicts with the durable Request acknowledgement.")]),
            DurableOperationObservationDisposition.InvalidEvidence => new(
                ProcessDurableRuntimeDisposition.Rejected,
                snapshot,
                operation,
                commit,
                diagnostics: [OperationBlocked(
                    "Adapter evidence is invalid for the retained durable Request attempt.")]),
            DurableOperationObservationDisposition.DeadlineElapsed => DeadlineBlockedResult(
                snapshot,
                operation,
                commit),
            DurableOperationObservationDisposition.LateResult => new(
                ProcessDurableRuntimeDisposition.Rejected,
                snapshot,
                operation,
                commit,
                diagnostics: [OperationBlocked(
                    "Adapter evidence arrived after another terminal outcome had already won and was not admitted as new evidence.")]),
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unsupported durable operation observation disposition.")
        };

    static bool HasElapsedDeadline(DurableOperationState operation, DateTimeOffset observedAtUtc) =>
        operation.Binding.TimeoutAfter is { } timeout
        && observedAtUtc >= operation.CreatedAtUtc.Add(timeout);

    static bool RequiresExternalOperationWork(DurableOperationStatus status) =>
        status is DurableOperationStatus.Pending
            or DurableOperationStatus.RetryEligible
            or DurableOperationStatus.Claimed
            or DurableOperationStatus.Dispatched
            or DurableOperationStatus.ReconciliationRequired;

    static bool RequiresNewOperationAttempt(DurableOperationStatus status) =>
        status is DurableOperationStatus.Pending
            or DurableOperationStatus.RetryEligible;

    static bool HasOpenOriginAttempt(
        ProcessDurableCheckpoint checkpoint,
        DurableOperationState operation) =>
        operation.Request.Context.Origin is ProcessInteractionOrigin origin
        && origin.Continuation == checkpoint.ContinuationIdentity
        && origin.Continuation.ProcessAttemptId == checkpoint.Control.CurrentAttempt.AttemptId
        && checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.None
        && checkpoint.Control.Mode == ProcessControlMode.Running
        && checkpoint.Control.CurrentAttempt.Disposition == ProcessControlAttemptDisposition.Current;

    static ProcessDurableOperationResult DeadlineBlockedResult(
        ProcessDurableStoreSnapshot snapshot,
        DurableOperationState operation,
        ProcessDurableCommit? commit) =>
        new(
            ProcessDurableRuntimeDisposition.Rejected,
            snapshot,
            operation,
            commit,
            diagnostics: [OperationBlocked(
                "The semantic Request deadline elapsed and requires its declared typed timeout outcome.")]);

    static DocumentValidationDiagnostic OperationBlocked(string message) =>
        Error(
            ProcessDurableRuntimeDiagnosticCodes.OperationRecoveryRequired,
            message,
            "/operation/recoveryRequirement");

    static DurableOperationState? FindOperation(ProcessDurableCheckpoint checkpoint, EmissionId operationId) =>
        checkpoint.DurableOperations.FirstOrDefault(candidate => candidate.OperationId == operationId);

    static ProcessDurableStoreSnapshot RequireSnapshot(ProcessDurableOperationResult result) =>
        result.Snapshot
        ?? throw new InvalidOperationException("A successful durable operation commit returned no aggregate snapshot.");

    static DurableOperationState RequireOperation(ProcessDurableOperationResult result) =>
        result.Operation
        ?? throw new InvalidOperationException("A successful durable operation commit returned no operation state.");

    static bool IsSuccessful(ProcessDurableRuntimeDisposition disposition) =>
        disposition is ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed;

    static ProcessDurableRuntimeDisposition Merge(
        ProcessDurableRuntimeDisposition current,
        ProcessDurableRuntimeDisposition next) =>
        current == ProcessDurableRuntimeDisposition.Applied || next == ProcessDurableRuntimeDisposition.Applied
            ? ProcessDurableRuntimeDisposition.Applied
            : ProcessDurableRuntimeDisposition.Replayed;

    static bool IsTerminal(ExecutionTokenDisposition disposition) =>
        disposition is ExecutionTokenDisposition.Completed
            or ExecutionTokenDisposition.Failed
            or ExecutionTokenDisposition.Cancelled;
}
