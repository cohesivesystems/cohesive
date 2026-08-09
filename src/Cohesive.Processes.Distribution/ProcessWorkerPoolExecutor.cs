namespace Cohesive.Processes.Distribution;

/// <summary>Execution decision returned by a local interpreter of one canonical distributed work reference.</summary>
public enum ProcessWorkExecutionDisposition
{
    /// <summary>No decision was supplied; invalid for an execution result.</summary>
    Unspecified = 0,

    /// <summary>The exact work unit completed successfully.</summary>
    Succeeded = 1,

    /// <summary>The exact work unit reached an explicit terminal failure.</summary>
    Failed = 2,

    /// <summary>The exact work unit observed accepted cancellation.</summary>
    Cancelled = 3,

    /// <summary>The work may be delivered again under a greater fence.</summary>
    Retry = 4,

    /// <summary>Effect ambiguity requires reconciliation before another delivery.</summary>
    Reconcile = 5
}

/// <summary>Result of interpreting one exact canonical Process work claim.</summary>
public sealed record ProcessWorkExecutionResult
{
    /// <summary>Creates a local work-execution result.</summary>
    /// <param name="disposition">Terminal, retry, or reconciliation decision.</param>
    /// <param name="effectEvidence">Known effect evidence at the execution boundary.</param>
    /// <param name="reasonCode">Stable reason for every non-success decision.</param>
    /// <param name="resultReference">Optional canonical result or artifact reference.</param>
    /// <param name="notBeforeUtc">Optional UTC earliest retry time.</param>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// Success carries a reason, failure lacks a reason, retry carries ambiguous effects, retry time is not UTC, or
    /// a non-retry decision carries a retry time.
    /// </exception>
    public ProcessWorkExecutionResult(
        ProcessWorkExecutionDisposition disposition,
        ProcessWorkEffectEvidence effectEvidence,
        string? reasonCode = null,
        string? resultReference = null,
        DateTimeOffset? notBeforeUtc = null)
    {
        if (!Enum.IsDefined(disposition) || disposition == ProcessWorkExecutionDisposition.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "A work-execution disposition is required.");
        if (!Enum.IsDefined(effectEvidence))
            throw new ArgumentOutOfRangeException(nameof(effectEvidence), effectEvidence, "Unsupported effect evidence.");
        if (disposition == ProcessWorkExecutionDisposition.Succeeded && !string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("Successful work cannot carry a failure reason.", nameof(reasonCode));
        if (disposition != ProcessWorkExecutionDisposition.Succeeded && string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("Non-success work requires a stable reason code.", nameof(reasonCode));
        if (disposition == ProcessWorkExecutionDisposition.Retry && effectEvidence == ProcessWorkEffectEvidence.Ambiguous)
            throw new ArgumentException("Ambiguous effects require reconciliation rather than retry.", nameof(disposition));
        if (disposition is ProcessWorkExecutionDisposition.Failed or ProcessWorkExecutionDisposition.Cancelled
            && effectEvidence == ProcessWorkEffectEvidence.Ambiguous)
        {
            throw new ArgumentException(
                "Ambiguous effects require reconciliation rather than terminal settlement.",
                nameof(disposition));
        }
        if (notBeforeUtc is { } notBefore)
        {
            ProcessDistributionRequirements.RequireUtc(notBefore, nameof(notBeforeUtc));
            if (disposition != ProcessWorkExecutionDisposition.Retry)
                throw new ArgumentException("Only retry may declare delayed eligibility.", nameof(notBeforeUtc));
        }

        Disposition = disposition;
        EffectEvidence = effectEvidence;
        ReasonCode = reasonCode.TrimmedEmptyOrWhiteSpaceAs();
        ResultReference = resultReference.TrimmedEmptyOrWhiteSpaceAs();
        NotBeforeUtc = notBeforeUtc;
    }

    /// <summary>Terminal, retry, or reconciliation decision.</summary>
    public ProcessWorkExecutionDisposition Disposition { get; }

    /// <summary>Known effect evidence at the execution boundary.</summary>
    public ProcessWorkEffectEvidence EffectEvidence { get; }

    /// <summary>Stable reason for every non-success decision.</summary>
    public string? ReasonCode { get; }

    /// <summary>Optional canonical result or artifact reference.</summary>
    public string? ResultReference { get; }

    /// <summary>Optional UTC earliest retry time.</summary>
    public DateTimeOffset? NotBeforeUtc { get; }

    /// <summary>Creates successful execution evidence.</summary>
    /// <param name="resultReference">Optional canonical result or artifact reference.</param>
    /// <returns>A successful result with applied-effect evidence.</returns>
    public static ProcessWorkExecutionResult Success(string? resultReference = null) =>
        new(ProcessWorkExecutionDisposition.Succeeded, ProcessWorkEffectEvidence.Applied, resultReference: resultReference);
}

/// <summary>Local interpretation port for exact canonical Process work references.</summary>
/// <remarks>
/// Implementations resolve <see cref="ProcessWorkClaim.Submission"/> against canonical definitions and runtime
/// services. The interface instance is never serialized into the job record.
/// </remarks>
public interface IProcessDistributedWorkExecutor
{
    /// <summary>Executes one exact live claim.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="claim">Exact canonical work, physical attempt, worker, lease, and fence.</param>
    /// <returns>Terminal, retry, or reconciliation evidence for the distribution ledger.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">Execution observes cancellation.</exception>
    ValueTask<ProcessWorkExecutionResult> ExecuteAsync(OperationContext context, ProcessWorkClaim claim);
}

/// <summary>Classifies an unhandled local executor exception without guessing about effects.</summary>
public interface IProcessWorkExceptionClassifier
{
    /// <summary>Classifies one exception thrown while a live work claim was executing.</summary>
    /// <param name="exception">Unhandled local executor exception.</param>
    /// <param name="claim">Exact claim active when the exception was observed.</param>
    /// <returns>Retry, reconciliation, or terminal evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    ProcessWorkExecutionResult Classify(Exception exception, ProcessWorkClaim claim);
}

/// <summary>Conservative exception classifier that requires reconciliation for unknown effect state.</summary>
public sealed class ConservativeProcessWorkExceptionClassifier : IProcessWorkExceptionClassifier
{
    /// <summary>Stable reason code for an unclassified executor exception.</summary>
    public const string AmbiguousExecutorException = "processes.distribution.executor.exception.ambiguous";

    /// <summary>Shared stateless conservative classifier.</summary>
    public static ConservativeProcessWorkExceptionClassifier Instance { get; } = new();

    ConservativeProcessWorkExceptionClassifier()
    {
    }

    /// <inheritdoc />
    public ProcessWorkExecutionResult Classify(Exception exception, ProcessWorkClaim claim)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(claim);
        return new(
            ProcessWorkExecutionDisposition.Reconcile,
            ProcessWorkEffectEvidence.Ambiguous,
            AmbiguousExecutorException);
    }
}

/// <summary>Decision for an exception thrown by the durable distribution-store boundary.</summary>
public enum ProcessDistributionStoreExceptionClassification
{
    /// <summary>Propagate the exception without repeating the store operation.</summary>
    Propagate = 0,

    /// <summary>Retry the exact same operation identity and canonical evidence.</summary>
    RetryExact = 1
}

/// <summary>Classifies distribution-store exceptions for bounded exact retry.</summary>
public interface IProcessDistributionStoreExceptionClassifier
{
    /// <summary>Classifies one exception without changing its store mutation intent.</summary>
    /// <param name="exception">Exception thrown by the distribution store.</param>
    /// <returns>Whether to propagate or retry the exact same request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    ProcessDistributionStoreExceptionClassification Classify(Exception exception);
}

/// <summary>Conservative classifier that exactly retries outcome-ambiguous store exceptions.</summary>
/// <remarks>
/// Causal caller cancellation is propagated before classification. Applications may replace this classifier when a
/// provider can prove that particular validation or configuration exceptions did not cross a physical boundary.
/// </remarks>
public sealed class ConservativeProcessDistributionStoreExceptionClassifier
    : IProcessDistributionStoreExceptionClassifier
{
    /// <summary>Shared stateless conservative classifier.</summary>
    public static ConservativeProcessDistributionStoreExceptionClassifier Instance { get; } = new();

    ConservativeProcessDistributionStoreExceptionClassifier()
    {
    }

    /// <inheritdoc />
    public ProcessDistributionStoreExceptionClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ProcessDistributionStoreExceptionClassification.RetryExact;
    }
}

/// <summary>Bounded polling and lease-renewal policy for one worker-pool executor.</summary>
public sealed record ProcessWorkerPoolExecutorOptions
{
    /// <summary>Creates worker-pool executor policy.</summary>
    /// <param name="idleDelay">Strictly positive delay after a pool has no eligible work.</param>
    /// <param name="renewalInterval">
    /// Preferred positive renewal interval; execution caps it at half of the provider-issued claim lease.
    /// </param>
    /// <param name="maximumStoreAttempts">Positive maximum attempts for one exact distribution-store operation.</param>
    /// <exception cref="ArgumentOutOfRangeException">A duration or <paramref name="maximumStoreAttempts"/> is not positive.</exception>
    public ProcessWorkerPoolExecutorOptions(
        TimeSpan idleDelay,
        TimeSpan renewalInterval,
        int maximumStoreAttempts = 3)
    {
        if (idleDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleDelay), idleDelay, "Idle delay must be positive.");
        if (renewalInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(renewalInterval),
                renewalInterval,
                "Renewal interval must be positive.");
        }
        if (maximumStoreAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumStoreAttempts),
                maximumStoreAttempts,
                "Maximum store attempts must be positive.");
        }

        IdleDelay = idleDelay;
        RenewalInterval = renewalInterval;
        MaximumStoreAttempts = maximumStoreAttempts;
    }

    /// <summary>Strictly positive delay after a pool has no eligible work.</summary>
    public TimeSpan IdleDelay { get; }

    /// <summary>Preferred positive interval, capped at half of the actual claim lease during execution.</summary>
    public TimeSpan RenewalInterval { get; }

    /// <summary>Positive maximum attempts for one exact distribution-store operation.</summary>
    public int MaximumStoreAttempts { get; }

    /// <summary>Conservative convention defaults for a competing consumer.</summary>
    public static ProcessWorkerPoolExecutorOptions Conventional { get; } = new(
        idleDelay: TimeSpan.FromMilliseconds(250),
        renewalInterval: TimeSpan.FromSeconds(5),
        maximumStoreAttempts: 3);
}

/// <summary>Outcome of one bounded worker-pool execution turn.</summary>
public enum ProcessWorkerExecutionDisposition
{
    /// <summary>No outcome was supplied; invalid in a worker result.</summary>
    Unspecified = 0,

    /// <summary>No eligible work was available.</summary>
    Idle = 1,

    /// <summary>One claimed work unit produced committed ledger evidence.</summary>
    Settled = 2,

    /// <summary>The worker or work ownership became stale or expired.</summary>
    Fenced = 3,

    /// <summary>The worker incarnation is unavailable and cannot continue.</summary>
    WorkerUnavailable = 4
}

/// <summary>Result of one bounded worker-pool execution turn.</summary>
/// <param name="Disposition">Idle, settled, fenced, or worker-unavailable outcome.</param>
/// <param name="Claim">Claim processed by the turn, when one was created.</param>
/// <param name="Ledger">Final ledger mutation evidence, when one was attempted.</param>
public sealed record ProcessWorkerExecutionResult(
    ProcessWorkerExecutionDisposition Disposition,
    ProcessWorkClaim? Claim = null,
    ProcessDistributionMutationResult? Ledger = null);

/// <summary>Portable competing-consumer worker-pool execution runtime.</summary>
/// <remarks>
/// The runtime registers one immutable worker incarnation, claims only eligible canonical work, renews the worker
/// and work leases while execution is in flight, cancels local execution when fenced, and submits completion or
/// recovery evidence with the exact fence. Running more runtime instances safely scales queued work without moving
/// healthy in-flight claims.
/// </remarks>
public sealed class ProcessWorkerPoolExecutor
{
    readonly IProcessDistributionStore store;
    readonly ProcessWorkerPoolId pool;
    readonly ProcessWorkerOffer offer;
    readonly IProcessDistributedWorkExecutor executor;
    readonly IProcessWorkExceptionClassifier exceptionClassifier;
    readonly IProcessDistributionStoreExceptionClassifier storeExceptionClassifier;
    readonly ProcessWorkerPoolExecutorOptions options;
    long nextClaimRequestOrdinal;

    /// <summary>Creates one worker-pool execution runtime.</summary>
    /// <param name="store">Shared distribution authority.</param>
    /// <param name="pool">Pool from which this runtime claims.</param>
    /// <param name="offer">Immutable offer for this worker incarnation.</param>
    /// <param name="executor">Local interpreter of canonical Process work references.</param>
    /// <param name="options">Optional bounded polling and renewal policy.</param>
    /// <param name="exceptionClassifier">Optional provider-aware executor exception classifier.</param>
    /// <param name="storeExceptionClassifier">Optional provider-aware distribution-store exception classifier.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="store"/>, <paramref name="offer"/>, or <paramref name="executor"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="pool"/> is default or absent from <paramref name="offer"/>.
    /// </exception>
    public ProcessWorkerPoolExecutor(
        IProcessDistributionStore store,
        ProcessWorkerPoolId pool,
        ProcessWorkerOffer offer,
        IProcessDistributedWorkExecutor executor,
        ProcessWorkerPoolExecutorOptions? options = null,
        IProcessWorkExceptionClassifier? exceptionClassifier = null,
        IProcessDistributionStoreExceptionClassifier? storeExceptionClassifier = null)
    {
        ProcessDistributionRequirements.Require(pool.Value, nameof(pool));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.offer = offer ?? throw new ArgumentNullException(nameof(offer));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        if (!offer.Pools.Contains(pool))
            throw new ArgumentException("The worker offer does not include the selected pool.", nameof(pool));

        this.pool = pool;
        this.options = options ?? ProcessWorkerPoolExecutorOptions.Conventional;
        this.exceptionClassifier = exceptionClassifier ?? ConservativeProcessWorkExceptionClassifier.Instance;
        this.storeExceptionClassifier = storeExceptionClassifier
            ?? ConservativeProcessDistributionStoreExceptionClassifier.Instance;
    }

    /// <summary>Registers the worker, claims at most one work unit, and settles its execution evidence.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <returns>One bounded idle, settled, fenced, or worker-unavailable turn.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation observes caller cancellation.</exception>
    public async Task<ProcessWorkerExecutionResult> RunOnceAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfCancellationRequested();
        var activity = ProcessDistributionTelemetry.StartWorkerTurn(context, pool, offer.Worker);
        var started = ProcessDistributionTelemetry.StartTimer();
        ProcessWorkerExecutionResult? result = null;
        Exception? failure = null;
        try
        {
            result = await RunOnceCoreAsync(context).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            ProcessDistributionTelemetry.CompleteWorkerTurn(activity, started, result, failure);
        }
    }

    async Task<ProcessWorkerExecutionResult> RunOnceCoreAsync(OperationContext context)
    {
        var ready = await EnsureWorkerAsync(context).ConfigureAwait(false);
        if (!ready)
            return new(ProcessWorkerExecutionDisposition.WorkerUnavailable);

        var requestOrdinal = Interlocked.Increment(ref nextClaimRequestOrdinal);
        var request = new ProcessWorkClaimRequestId(
            $"claim-request/{offer.Worker.Value}/{requestOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        var claimedAtUtc = context.UtcNow;
        var claimed = await ExecuteStoreAsync(
                context,
                (pool, offer.Worker, request, claimedAtUtc),
                static (distribution, operationContext, state) => distribution.ClaimAsync(
                    operationContext,
                    state.pool,
                    state.Worker,
                    state.request,
                    state.claimedAtUtc))
            .ConfigureAwait(false);
        if (claimed.Disposition == ProcessDistributionDisposition.NoEligibleWork)
            return new(ProcessWorkerExecutionDisposition.Idle);
        if (claimed.Disposition is not (ProcessDistributionDisposition.Applied
            or ProcessDistributionDisposition.Replayed) || claimed.Claim is null)
            return new(ProcessWorkerExecutionDisposition.WorkerUnavailable);

        return await ExecuteClaimAsync(context, claimed.Claim).ConfigureAwait(false);
    }

    /// <summary>Runs bounded concurrent competing-consumer lanes until cancellation.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <returns>A task that completes after cancellation or worker unavailability stops every lane.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation observes caller cancellation.</exception>
    public async Task RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfCancellationRequested();
        if (!await EnsureWorkerAsync(context).ConfigureAwait(false))
            return;

        var lanes = new Task[offer.MaximumConcurrentClaims];
        for (var index = 0; index < lanes.Length; index++)
            lanes[index] = RunLaneAsync(context);
        await Task.WhenAll(lanes).ConfigureAwait(false);
    }

    async Task RunLaneAsync(OperationContext context)
    {
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var result = await RunOnceAsync(context).ConfigureAwait(false);
            if (result.Disposition == ProcessWorkerExecutionDisposition.WorkerUnavailable)
                return;
            if (result.Disposition == ProcessWorkerExecutionDisposition.Idle)
            {
                await Task.Delay(options.IdleDelay, context.TimeProvider, context.CancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    async Task<bool> EnsureWorkerAsync(OperationContext context)
    {
        var now = context.UtcNow;
        var registration = await ExecuteStoreAsync(
                context,
                (offer, now),
                static (distribution, operationContext, state) => distribution.RegisterWorkerAsync(
                    operationContext,
                    state.offer,
                    state.now))
            .ConfigureAwait(false);
        if (registration.Disposition == ProcessDistributionDisposition.Applied)
            return true;
        if (registration.Disposition != ProcessDistributionDisposition.Replayed)
            return false;

        var renewedAtUtc = context.UtcNow;
        var renewal = await ExecuteStoreAsync(
                context,
                (offer.Worker, renewedAtUtc),
                static (distribution, operationContext, state) => distribution.RenewWorkerAsync(
                    operationContext,
                    state.Worker,
                    ProcessWorkerHealth.Healthy,
                    state.renewedAtUtc))
            .ConfigureAwait(false);
        return renewal.Disposition is ProcessDistributionDisposition.Applied
            or ProcessDistributionDisposition.Replayed;
    }

    async Task<ProcessWorkerExecutionResult> ExecuteClaimAsync(
        OperationContext context,
        ProcessWorkClaim initialClaim)
    {
        var activity = ProcessDistributionTelemetry.StartExecution(context, initialClaim);
        var started = ProcessDistributionTelemetry.StartTimer();
        ProcessWorkerExecutionResult? result = null;
        Exception? failure = null;
        try
        {
            result = await ExecuteClaimCoreAsync(context, initialClaim).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            ProcessDistributionTelemetry.CompleteExecution(activity, started, initialClaim, result, failure);
        }
    }

    async Task<ProcessWorkerExecutionResult> ExecuteClaimCoreAsync(
        OperationContext context,
        ProcessWorkClaim initialClaim)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var executionContext = context.WithCancellationToken(executionCancellation.Token);
        Task<ProcessWorkExecutionResult> execution;
        try
        {
            execution = executor.ExecuteAsync(executionContext, initialClaim).AsTask();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
        {
            execution = Task.FromResult(exceptionClassifier.Classify(exception, initialClaim));
        }

        var claim = initialClaim;
        var leaseLifetime = claim.ExpiresAtUtc - claim.RenewedAtUtc;
        var renewalInterval = TimeSpan.FromTicks(Math.Min(
            options.RenewalInterval.Ticks,
            Math.Max(1L, leaseLifetime.Ticks / 2)));
        var timeoutAtUtc = claim.Submission.Requirements.ExecutionTimeout is { } timeout
            ? Add(claim.ClaimedAtUtc, timeout)
            : (DateTimeOffset?)null;

        while (!execution.IsCompleted)
        {
            var delay = Task.Delay(renewalInterval, context.TimeProvider, context.CancellationToken);
            if (await Task.WhenAny(execution, delay).ConfigureAwait(false) == execution)
                break;

            var observedAtUtc = context.UtcNow;
            if (timeoutAtUtc <= observedAtUtc)
                executionCancellation.Cancel();
            var workerRenewal = await ExecuteStoreAsync(
                    context,
                    (offer.Worker, observedAtUtc),
                    static (distribution, operationContext, state) => distribution.RenewWorkerAsync(
                        operationContext,
                        state.Worker,
                        ProcessWorkerHealth.Healthy,
                        state.observedAtUtc))
                .ConfigureAwait(false);
            ProcessDistributionTelemetry.RecordLease("worker", workerRenewal.Disposition, pool);
            if (workerRenewal.Disposition is not (ProcessDistributionDisposition.Applied
                or ProcessDistributionDisposition.Replayed))
            {
                executionCancellation.Cancel();
                await ObserveExecutionAsync(execution).ConfigureAwait(false);
                return new(ProcessWorkerExecutionDisposition.Fenced, claim, workerRenewal);
            }

            var claimRenewal = await ExecuteStoreAsync(
                    context,
                    (claim, observedAtUtc),
                    static (distribution, operationContext, state) => distribution.RenewClaimAsync(
                        operationContext,
                        state.claim,
                        state.observedAtUtc))
                .ConfigureAwait(false);
            ProcessDistributionTelemetry.RecordLease("claim", claimRenewal.Disposition, pool);
            if (claimRenewal.Disposition is not (ProcessDistributionDisposition.Applied
                or ProcessDistributionDisposition.Replayed))
            {
                executionCancellation.Cancel();
                await ObserveExecutionAsync(execution).ConfigureAwait(false);
                return new(ProcessWorkerExecutionDisposition.Fenced, claim, claimRenewal);
            }
            if (claimRenewal.Work?.Claim is { } renewed)
                claim = renewed;
            if (claimRenewal.Work?.CancellationRequested == true)
                executionCancellation.Cancel();
        }

        ProcessWorkExecutionResult executionResult;
        try
        {
            executionResult = await execution.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!context.CancellationToken.IsCancellationRequested)
        {
            executionResult = exceptionClassifier.Classify(exception, claim);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
        {
            executionResult = exceptionClassifier.Classify(exception, claim);
        }

        var observed = context.UtcNow;
        ProcessDistributionMutationResult ledger;
        if (executionResult.Disposition is ProcessWorkExecutionDisposition.Succeeded
            or ProcessWorkExecutionDisposition.Failed
            or ProcessWorkExecutionDisposition.Cancelled)
        {
            var outcome = executionResult.Disposition switch
            {
                ProcessWorkExecutionDisposition.Succeeded => ProcessWorkCompletionOutcome.Succeeded,
                ProcessWorkExecutionDisposition.Failed => ProcessWorkCompletionOutcome.Failed,
                ProcessWorkExecutionDisposition.Cancelled => ProcessWorkCompletionOutcome.Cancelled,
                _ => throw new InvalidOperationException("Unsupported terminal execution disposition.")
            };
            var completion = new ProcessWorkCompletion(
                claim,
                outcome,
                executionResult.EffectEvidence,
                observed,
                executionResult.ResultReference,
                executionResult.ReasonCode);
            ledger = await ExecuteStoreAsync(
                    context,
                    completion,
                    static (distribution, operationContext, state) => distribution.CompleteAsync(
                        operationContext,
                        state))
                .ConfigureAwait(false);
        }
        else
        {
            var release = executionResult.Disposition == ProcessWorkExecutionDisposition.Retry
                ? ProcessWorkReleaseDisposition.Retry
                : ProcessWorkReleaseDisposition.Reconcile;
            var releaseEvidence = new ProcessWorkRelease(
                claim,
                release,
                executionResult.EffectEvidence,
                executionResult.ReasonCode!,
                observed,
                executionResult.NotBeforeUtc);
            ledger = await ExecuteStoreAsync(
                    context,
                    releaseEvidence,
                    static (distribution, operationContext, state) => distribution.ReleaseAsync(
                        operationContext,
                        state))
                .ConfigureAwait(false);
        }

        var disposition = ledger.Disposition is ProcessDistributionDisposition.Applied
            or ProcessDistributionDisposition.Replayed
            ? ProcessWorkerExecutionDisposition.Settled
            : ProcessWorkerExecutionDisposition.Fenced;
        return new(disposition, claim, ledger);
    }

    static async Task ObserveExecutionAsync(Task<ProcessWorkExecutionResult> execution)
    {
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ownership is already fenced; observing prevents an unobserved task without creating stale evidence.
        }
    }

    async Task<TResult> ExecuteStoreAsync<TState, TResult>(
        OperationContext context,
        TState state,
        Func<IProcessDistributionStore, OperationContext, TState, Task<TResult>> operation)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(store, context, state).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var classification = storeExceptionClassifier.Classify(exception);
                if (classification != ProcessDistributionStoreExceptionClassification.RetryExact
                    || attempt >= options.MaximumStoreAttempts)
                {
                    throw;
                }
                ProcessDistributionTelemetry.RecordStoreRetry(attempt);
            }
        }
    }

    static DateTimeOffset Add(DateTimeOffset value, TimeSpan duration)
    {
        try
        {
            return value.Add(duration);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "Execution timeout exceeds the supported UTC range.");
        }
    }
}
