using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Adapters.DurableTask;

static class DurableTaskDurableOperationInterpreter
{
    const string Claimant = "cohesive.adapters.durable-task/request-interpreter/v1";

    internal static async Task<DurableTaskDurableOperationResult> RunAsync(
        InteractionContractCatalog contracts,
        RequestEnvelope request,
        DurableRequestBinding binding,
        Func<DurableOperationInvocation, Task<DurableTaskDurableOperationAttemptResult>> execute,
        Func<DurableOperationState, Task<DurableTaskDurableOperationReconciliationResult>> reconcile,
        Func<TimeSpan, CancellationToken, Task> createTimer,
        Func<DateTimeOffset> getCurrentUtc,
        Func<DurableTaskDurableOperationCut, DurableOperationState, Task>? createCut = null)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(reconcile);
        ArgumentNullException.ThrowIfNull(createTimer);
        ArgumentNullException.ThrowIfNull(getCurrentUtc);
        RequireActivityRedeliverySafety(binding);

        var executor = new DurableOperationReferenceExecutor(contracts);
        var validation = executor.TryCreate(request, binding, RequireUtc(getCurrentUtc()), out var created);
        if (!validation.IsValid || created is null)
        {
            throw new InvalidOperationException(
                "The exact durable Request binding is invalid: "
                + string.Join("; ", validation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        }
        var state = created;

        while (true)
        {
            if (state.Status == DurableOperationStatus.Acknowledged)
            {
                return await AdmitReplyAsync(state, executor, createCut).ConfigureAwait(true);
            }
            if (state.Status is DurableOperationStatus.TerminalOutcomeRequired
                or DurableOperationStatus.EscalationRequired
                or DurableOperationStatus.ReconciliationRequired)
            {
                return new(DurableTaskDurableOperationDisposition.RecoveryRequired, state);
            }

            var ordinal = state.Attempts.Length + 1;
            var claim = executor.Claim(
                state,
                DurableOperationIdentities.Attempt(state.OperationId, ordinal),
                Claimant,
                RequireUtc(getCurrentUtc()));
            state = claim.State;
            if (claim.Disposition == DurableOperationClaimDisposition.DeadlineElapsed)
            {
                return new(DurableTaskDurableOperationDisposition.DeadlineElapsed, state);
            }
            if (claim.Disposition == DurableOperationClaimDisposition.RecoveryRequired)
            {
                return new(DurableTaskDurableOperationDisposition.RecoveryRequired, state);
            }
            if (claim.Disposition is not (DurableOperationClaimDisposition.Claimed
                or DurableOperationClaimDisposition.Replayed))
            {
                throw new InvalidOperationException($"Durable Task could not claim the Request operation: {claim.Disposition}.");
            }

            var liveClaim = claim.Claim
                ?? throw new InvalidOperationException("A claimed durable Request returned no claim evidence.");
            await CreateCutAsync(createCut, DurableTaskDurableOperationCut.BeforeDispatch, state).ConfigureAwait(true);
            var dispatch = executor.BeginDispatch(
                state,
                liveClaim.AttemptId,
                liveClaim.Fence,
                RequireUtc(getCurrentUtc()));
            state = dispatch.State;
            if (dispatch.Disposition == DurableOperationDispatchDisposition.DeadlineElapsed)
            {
                return new(DurableTaskDurableOperationDisposition.DeadlineElapsed, state);
            }
            if (dispatch.Disposition == DurableOperationDispatchDisposition.RecoveryRequired)
            {
                return new(DurableTaskDurableOperationDisposition.RecoveryRequired, state);
            }
            if (dispatch.Disposition is not (DurableOperationDispatchDisposition.Dispatched
                or DurableOperationDispatchDisposition.Replayed))
            {
                throw new InvalidOperationException($"Durable Task could not dispatch the Request operation: {dispatch.Disposition}.");
            }

            var invocation = dispatch.Invocation
                ?? throw new InvalidOperationException("A dispatched durable Request returned no invocation.");
            await CreateCutAsync(createCut, DurableTaskDurableOperationCut.AfterDispatch, state).ConfigureAwait(true);
            var activity = execute(invocation)
                ?? throw new InvalidOperationException("The Durable Task dispatch delegate returned null.");
            var completed = await AwaitWithRenewalsAsync(
                    activity,
                    state,
                    executor,
                    createTimer,
                    getCurrentUtc)
                .ConfigureAwait(true);
            state = completed.State;
            if (completed.DeadlineElapsed)
            {
                ObserveAbandoned(activity);
                return new(DurableTaskDurableOperationDisposition.DeadlineElapsed, state);
            }
            var activityResult = completed.Result
                ?? throw new InvalidOperationException("A completed Durable Task dispatch returned no result.");
            if (activityResult.DeadlineElapsed)
            {
                return new(DurableTaskDurableOperationDisposition.DeadlineElapsed, state);
            }

            var attempt = state.CurrentAttempt
                ?? throw new InvalidOperationException("A dispatched durable Request lost its attempt evidence.");
            var recorded = executor.RecordObservation(
                state,
                attempt.Claim.AttemptId,
                attempt.Claim.Fence,
                activityResult.Observation
                    ?? throw new InvalidOperationException("A completed dispatch returned no observation."),
                RequireUtc(getCurrentUtc()));
            state = recorded.State;
            if (state.Status == DurableOperationStatus.Acknowledged)
            {
                continue;
            }
            if (recorded.Disposition == DurableOperationObservationDisposition.DeadlineElapsed)
            {
                return new(DurableTaskDurableOperationDisposition.DeadlineElapsed, state);
            }
            if (state.Status != DurableOperationStatus.ReconciliationRequired)
            {
                if (state.RecoveryRequirement == DurableOperationRecoveryRequirement.Retry)
                {
                    continue;
                }
                return new(DurableTaskDurableOperationDisposition.RecoveryRequired, state);
            }

            var sourceAttempt = state.CurrentAttempt
                ?? throw new InvalidOperationException("A reconcilable durable Request lost its source attempt.");
            var reconciliationResult = await reconcile(state).ConfigureAwait(true)
                ?? throw new InvalidOperationException("The Durable Task reconciliation delegate returned null.");
            if (reconciliationResult.DeadlineElapsed)
            {
                return new(DurableTaskDurableOperationDisposition.DeadlineElapsed, state);
            }
            var reconciled = executor.RecordReconciliation(
                state,
                sourceAttempt.Claim.AttemptId,
                sourceAttempt.Claim.Fence,
                reconciliationResult.Observation
                    ?? throw new InvalidOperationException("A completed reconciliation returned no observation."),
                RequireUtc(getCurrentUtc()));
            state = reconciled.State;
            if (reconciled.Disposition == DurableOperationObservationDisposition.DeadlineElapsed)
            {
                return new(DurableTaskDurableOperationDisposition.DeadlineElapsed, state);
            }
        }
    }

    static async Task<DurableTaskDurableOperationResult> AdmitReplyAsync(
        DurableOperationState state,
        DurableOperationReferenceExecutor executor,
        Func<DurableTaskDurableOperationCut, DurableOperationState, Task>? createCut)
    {
        await CreateCutAsync(createCut, DurableTaskDurableOperationCut.AfterAcknowledgement, state).ConfigureAwait(true);
        if (state.Request.ResponseTarget is not ProcessTokenInteractionTarget target)
        {
            throw new InvalidOperationException(
                "The sequential Durable Task interpreter admits durable Replies only to Process-token targets.");
        }
        await CreateCutAsync(createCut, DurableTaskDurableOperationCut.BeforeReplyAdmission, state).ConfigureAwait(true);
        var admission = executor.AdmitResult(
            state,
            new DurableOperationTargetObservation(target, DurableOperationResultArrival.Eligible));
        if (admission.Kind != DurableOperationAdmissionResultKind.Dispositioned
            || admission.Admission is not { AdvancesTarget: true })
        {
            throw new InvalidOperationException(
                $"The canonical durable Reply could not advance its live Process target: {admission.Kind}.");
        }
        state = admission.State;
        var reply = state.CreateReply(
            DurableOperationIdentities.Reply(state.OperationId),
            DurableOperationIdentities.ReplyIdempotency(state.OperationId),
            state.Request.Context.Ordering,
            state.Request.Context.Provenance);
        return new(
            DurableTaskDurableOperationDisposition.ReplyReady,
            state,
            new ProcessActivationInput(target, reply));
    }

    static async Task<(DurableOperationState State, T? Result, bool DeadlineElapsed)> AwaitWithRenewalsAsync<T>(
        Task<T> activity,
        DurableOperationState state,
        DurableOperationReferenceExecutor executor,
        Func<TimeSpan, CancellationToken, Task> createTimer,
        Func<DateTimeOffset> getCurrentUtc)
    {
        while (!activity.IsCompleted)
        {
            var current = state.CurrentAttempt
                ?? throw new InvalidOperationException("An in-flight durable Request lost its attempt evidence.");
            var interval = TimeSpan.FromTicks(Math.Max(1, state.Binding.ClaimLease.Ticks / 3));
            if (state.Binding.TimeoutAfter is { } timeoutAfter)
            {
                var deadline = state.CreatedAtUtc.Add(timeoutAfter);
                var remaining = deadline - RequireUtc(getCurrentUtc());
                if (remaining <= TimeSpan.Zero)
                {
                    return (state, default, DeadlineElapsed: true);
                }
                interval = interval <= remaining ? interval : remaining;
            }

            using var timerCancellation = new CancellationTokenSource();
            var timer = createTimer(interval, timerCancellation.Token)
                ?? throw new InvalidOperationException("The Durable Task timer delegate returned null.");
            if (await Task.WhenAny(activity, timer).ConfigureAwait(true) == activity)
            {
                timerCancellation.Cancel();
                break;
            }

            var renewal = executor.RenewClaim(
                state,
                current.Claim.AttemptId,
                current.Claim.Fence,
                Claimant,
                RequireUtc(getCurrentUtc()));
            state = renewal.State;
            if (renewal.Disposition == DurableOperationRenewalDisposition.DeadlineElapsed)
            {
                return (state, default, DeadlineElapsed: true);
            }
            if (renewal.Disposition is not (DurableOperationRenewalDisposition.Renewed
                or DurableOperationRenewalDisposition.Replayed))
            {
                throw new InvalidOperationException(
                    $"Durable Task could not renew an in-flight Request claim: {renewal.Disposition}.");
            }
        }
        return (state, await activity.ConfigureAwait(true), DeadlineElapsed: false);
    }

    static void RequireActivityRedeliverySafety(DurableRequestBinding binding)
    {
        if (binding.IdempotencyEvidence == DurableOperationIdempotencyEvidence.None)
        {
            throw new InvalidOperationException(
                "Durable Task activities are at-least-once and cannot dispatch a Request binding without target "
                + "deduplication or natural idempotency evidence.");
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

    static Task CreateCutAsync(
        Func<DurableTaskDurableOperationCut, DurableOperationState, Task>? createCut,
        DurableTaskDurableOperationCut cut,
        DurableOperationState state) => createCut?.Invoke(cut, state) ?? Task.CompletedTask;

    static void ObserveAbandoned(Task activity) =>
        _ = activity.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
}
