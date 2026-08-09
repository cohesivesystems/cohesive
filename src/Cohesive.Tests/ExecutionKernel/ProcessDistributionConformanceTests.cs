using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Distribution;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessDistributionConformanceTests
{
    static readonly DateTimeOffset InitialUtc = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    static readonly ExecutionIrSchemaVersion ProcessIrVersion = ExecutionDefinitionDocument.CurrentSchemaVersion;
    static readonly ProcessWorkerPoolId PoolId = new("pool/default");

    [Fact]
    public async Task Submit_ReplaysIdenticalIntentAndRejectsConflictingIntent()
    {
        var fixture = await Fixture.CreateAsync();
        var first = fixture.Submission("work/1", "intent/1", "tenant/a");

        var applied = await fixture.Store.SubmitAsync(fixture.Context(), first);
        var replay = await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/retry", "intent/1", "tenant/a", referenceId: "work/1"));
        var conflict = await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/conflict", "intent/1", "tenant/b"));

        Assert.Equal(ProcessDistributionDisposition.Applied, applied.Disposition);
        Assert.Equal(ProcessDistributionDisposition.Replayed, replay.Disposition);
        Assert.Equal(first.Id, replay.Work!.Submission.Id);
        Assert.Equal(ProcessDistributionDisposition.IdentityConflict, conflict.Disposition);
    }

    [Fact]
    public async Task MultipleWorkers_ClaimDifferentWorkWithoutSingletonCoordination()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 4);
        for (var index = 0; index < 4; index++)
        {
            await fixture.Store.SubmitAsync(
                fixture.Context(),
                fixture.Submission($"work/{index}", $"intent/{index}", $"tenant/{index % 2}"));
        }
        var workerA = fixture.Offer("worker/a", maximumConcurrentClaims: 2);
        var workerB = fixture.Offer("worker/b", maximumConcurrentClaims: 2);
        await fixture.RegisterAsync(workerA);
        await fixture.RegisterAsync(workerB);

        var claims = await Task.WhenAll(
            fixture.ClaimAsync(workerA),
            fixture.ClaimAsync(workerB),
            fixture.ClaimAsync(workerA),
            fixture.ClaimAsync(workerB));

        Assert.All(claims, static result => Assert.Equal(ProcessDistributionDisposition.Applied, result.Disposition));
        Assert.Equal(4, claims.Select(static result => result.Claim!.Submission.Id).Distinct().Count());
        var pool = await fixture.Store.InspectPoolAsync(fixture.Context(), PoolId, fixture.Time.GetUtcNow());
        Assert.Equal(4, pool!.Claimed);
        Assert.Equal(4, Assert.Single(pool.ReservedCapacity).Units);
    }

    [Fact]
    public async Task ClaimRequest_ExactlyReplaysTheSameLiveFencedDispatch()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 2);
        var worker = fixture.Offer("worker/a", maximumConcurrentClaims: 2);
        await fixture.RegisterAsync(worker);
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/1", "intent/1", "tenant/a"));
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/2", "intent/2", "tenant/b"));
        ProcessWorkClaimRequestId request = new("claim-request/exact-replay");

        var applied = await fixture.Store.ClaimAsync(
            fixture.Context(),
            PoolId,
            worker.Worker,
            request,
            fixture.Time.GetUtcNow());
        var replayed = await fixture.Store.ClaimAsync(
            fixture.Context(),
            PoolId,
            worker.Worker,
            request,
            fixture.Time.GetUtcNow());

        Assert.Equal(ProcessDistributionDisposition.Applied, applied.Disposition);
        Assert.Equal(ProcessDistributionDisposition.Replayed, replayed.Disposition);
        Assert.Equal(applied.Claim, replayed.Claim);
        var pool = await fixture.Store.InspectPoolAsync(fixture.Context(), PoolId, fixture.Time.GetUtcNow());
        Assert.Equal(1, pool!.Claimed);
    }

    [Fact]
    public async Task CapacityDomain_ReservationsPreventConcurrentOverAdmission()
    {
        var fixture = await Fixture.CreateAsync(
            maximumConcurrentClaims: 4,
            capacityDomains: [new("domain/a", 1)]);
        var worker = fixture.Offer("worker/a", maximumConcurrentClaims: 4);
        await fixture.RegisterAsync(worker);
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/1", "intent/1", "tenant/a", capacityDomain: "domain/a"));
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/2", "intent/2", "tenant/b", capacityDomain: "domain/a"));

        var first = await fixture.ClaimAsync(worker);
        var saturated = await fixture.ClaimAsync(worker);
        Assert.Equal(ProcessDistributionDisposition.Applied, first.Disposition);
        Assert.Equal(ProcessDistributionDisposition.NoEligibleWork, saturated.Disposition);

        await fixture.CompleteAsync(first.Claim!);
        var admitted = await fixture.ClaimAsync(worker);
        Assert.Equal(ProcessDistributionDisposition.Applied, admitted.Disposition);
        Assert.NotEqual(first.Claim!.Submission.Id, admitted.Claim!.Submission.Id);
    }

    [Fact]
    public async Task WorkerOffer_FailsClosedForUnsupportedWorkEffectGuarantee()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var submission = fixture.Submission(
            "work/reconciled",
            "intent/reconciled",
            "tenant/a",
            recoveryMode: ProcessWorkRecoveryMode.ReconcileBeforeRedispatch,
            effectGuarantee: ProcessWorkEffectGuarantee.Reconciled);
        await fixture.Store.SubmitAsync(fixture.Context(), submission);
        var worker = fixture.Offer(
            "worker/idempotent-only",
            maximumConcurrentClaims: 1,
            supportedEffectGuarantees: [ProcessWorkEffectGuarantee.Idempotent]);
        await fixture.RegisterAsync(worker);

        var result = await fixture.ClaimAsync(worker);

        Assert.Equal(ProcessDistributionDisposition.NoEligibleWork, result.Disposition);
        Assert.Equal(0, (await fixture.Store.InspectWorkAsync(fixture.Context(), submission.Id))!.AttemptCount);
    }

    [Fact]
    public async Task TenantFairness_RotatesBeforeAdmittingSameTenantAgain()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var worker = fixture.Offer("worker/a", maximumConcurrentClaims: 1);
        await fixture.RegisterAsync(worker);
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/a1", "intent/a1", "tenant/a"));
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/a2", "intent/a2", "tenant/a"));
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/b1", "intent/b1", "tenant/b"));

        var first = (await fixture.ClaimAsync(worker)).Claim!;
        await fixture.CompleteAsync(first);
        var second = (await fixture.ClaimAsync(worker)).Claim!;
        await fixture.CompleteAsync(second);
        var third = (await fixture.ClaimAsync(worker)).Claim!;

        Assert.Equal("work/a1", first.Submission.Id.Value);
        Assert.Equal("work/b1", second.Submission.Id.Value);
        Assert.Equal("work/a2", third.Submission.Id.Value);
    }

    [Fact]
    public async Task ExpiredOwnership_IsReclaimedWithGreaterFenceAndStaleCompletionIsRejected()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var workerA = fixture.Offer("worker/a", maximumConcurrentClaims: 1);
        await fixture.RegisterAsync(workerA);
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission(
                "work/1",
                "intent/1",
                "tenant/a",
                recoveryMode: ProcessWorkRecoveryMode.Redispatch));
        var first = (await fixture.ClaimAsync(workerA)).Claim!;

        fixture.Time.Advance(TimeSpan.FromSeconds(11));
        var workerB = fixture.Offer("worker/b", maximumConcurrentClaims: 1);
        await fixture.RegisterAsync(workerB);
        var replacement = (await fixture.ClaimAsync(workerB)).Claim!;

        Assert.Equal(1, first.Fence.Ordinal);
        Assert.Equal(2, replacement.Fence.Ordinal);
        var stale = await fixture.Store.CompleteAsync(
            fixture.Context(),
            new ProcessWorkCompletion(
                first,
                ProcessWorkCompletionOutcome.Succeeded,
                ProcessWorkEffectEvidence.Applied,
                fixture.Time.GetUtcNow()));
        Assert.Equal(ProcessDistributionDisposition.StaleFence, stale.Disposition);

        var completed = await fixture.CompleteAsync(replacement);
        Assert.Equal(ProcessWorkStatus.Succeeded, completed.Work!.Status);
    }

    [Fact]
    public async Task AmbiguousWorkerLoss_RequiresReconciliationBeforeRedispatch()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var workerA = fixture.Offer("worker/a", maximumConcurrentClaims: 1);
        await fixture.RegisterAsync(workerA);
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission(
                "work/1",
                "intent/1",
                "tenant/a",
                recoveryMode: ProcessWorkRecoveryMode.ReconcileBeforeRedispatch,
                effectGuarantee: ProcessWorkEffectGuarantee.Reconciled));
        var first = (await fixture.ClaimAsync(workerA)).Claim!;

        fixture.Time.Advance(TimeSpan.FromSeconds(11));
        var workerB = fixture.Offer("worker/b", maximumConcurrentClaims: 1);
        await fixture.RegisterAsync(workerB);
        var blocked = await fixture.ClaimAsync(workerB);
        var ambiguous = await fixture.Store.InspectWorkAsync(fixture.Context(), first.Submission.Id);

        Assert.Equal(ProcessDistributionDisposition.NoEligibleWork, blocked.Disposition);
        Assert.Equal(ProcessWorkStatus.ReconciliationRequired, ambiguous!.Status);
        var reconciled = await fixture.Store.ReconcileAsync(
            fixture.Context(),
            new ProcessWorkReconciliation(
                first.Submission.Id,
                first.Fence,
                ProcessWorkReconciliationOutcome.Redispatch,
                "test/no-effect-committed",
                fixture.Time.GetUtcNow()));
        Assert.Equal(ProcessWorkStatus.Queued, reconciled.Work!.Status);

        var replacement = (await fixture.ClaimAsync(workerB)).Claim!;
        Assert.Equal(2, replacement.Fence.Ordinal);
    }

    [Fact]
    public async Task DrainingWorker_FinishesExistingClaimButCannotClaimNewWork()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 2);
        var worker = fixture.Offer("worker/a", maximumConcurrentClaims: 2);
        await fixture.RegisterAsync(worker);
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/1", "intent/1", "tenant/a"));
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/2", "intent/2", "tenant/b"));
        var inFlight = (await fixture.ClaimAsync(worker)).Claim!;

        var draining = await fixture.Store.SetWorkerDrainingAsync(
            fixture.Context(),
            worker.Worker,
            draining: true,
            fixture.Time.GetUtcNow());
        var denied = await fixture.ClaimAsync(worker);
        var completion = await fixture.CompleteAsync(inFlight);

        Assert.Equal(ProcessDistributionDisposition.Applied, draining.Disposition);
        Assert.Equal(ProcessDistributionDisposition.WorkerUnavailable, denied.Disposition);
        Assert.Equal(ProcessWorkStatus.Succeeded, completion.Work!.Status);
    }

    [Fact]
    public async Task WorkerPoolExecutor_ClaimsAndSettlesCanonicalWork()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var submission = fixture.Submission("work/1", "intent/1", "tenant/a");
        await fixture.Store.SubmitAsync(fixture.Context(), submission);
        var worker = fixture.Offer("worker/a", maximumConcurrentClaims: 1);
        var executor = new CapturingExecutor(ProcessWorkExecutionResult.Success("artifact/report/1"));
        var runtime = new ProcessWorkerPoolExecutor(fixture.Store, PoolId, worker, executor);

        var result = await runtime.RunOnceAsync(fixture.Context());
        var stored = await fixture.Store.InspectWorkAsync(fixture.Context(), submission.Id);

        Assert.Equal(ProcessWorkerExecutionDisposition.Settled, result.Disposition);
        Assert.Same(submission, executor.Observed!.Submission);
        Assert.Equal(ProcessWorkStatus.Succeeded, stored!.Status);
        Assert.Equal("artifact/report/1", stored.Completion!.ResultReference);
    }

    [Fact]
    public async Task WorkerPoolExecutor_UnknownExceptionRequiresReconciliation()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var submission = fixture.Submission(
            "work/1",
            "intent/1",
            "tenant/a",
            recoveryMode: ProcessWorkRecoveryMode.ReconcileBeforeRedispatch,
            effectGuarantee: ProcessWorkEffectGuarantee.Reconciled);
        await fixture.Store.SubmitAsync(fixture.Context(), submission);
        var runtime = new ProcessWorkerPoolExecutor(
            fixture.Store,
            PoolId,
            fixture.Offer("worker/a", maximumConcurrentClaims: 1),
            new ThrowingExecutor());

        var result = await runtime.RunOnceAsync(fixture.Context());
        var stored = await fixture.Store.InspectWorkAsync(fixture.Context(), submission.Id);

        Assert.Equal(ProcessWorkerExecutionDisposition.Settled, result.Disposition);
        Assert.Equal(ProcessWorkStatus.ReconciliationRequired, stored!.Status);
        Assert.Equal(
            ConservativeProcessWorkExceptionClassifier.AmbiguousExecutorException,
            stored.ReasonCode);
    }

    [Fact]
    public async Task WorkerPoolExecutor_NonCausalCancellationRequiresReconciliation()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var submission = fixture.Submission(
            "work/cancellation",
            "intent/cancellation",
            "tenant/a",
            recoveryMode: ProcessWorkRecoveryMode.ReconcileBeforeRedispatch,
            effectGuarantee: ProcessWorkEffectGuarantee.Reconciled);
        await fixture.Store.SubmitAsync(fixture.Context(), submission);
        var runtime = new ProcessWorkerPoolExecutor(
            fixture.Store,
            PoolId,
            fixture.Offer("worker/cancellation", maximumConcurrentClaims: 1),
            new CancellingExecutor());

        await runtime.RunOnceAsync(fixture.Context());
        var stored = await fixture.Store.InspectWorkAsync(fixture.Context(), submission.Id);

        Assert.Equal(ProcessWorkStatus.ReconciliationRequired, stored!.Status);
        Assert.Equal(ProcessWorkEffectEvidence.Ambiguous, stored.LastRelease!.EffectEvidence);
    }

    [Fact]
    public async Task WorkerPoolExecutor_ExactlyRetriesOutcomeAmbiguousClaimAndCompletion()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var submission = fixture.Submission("work/ambiguous", "intent/ambiguous", "tenant/a");
        await fixture.Store.SubmitAsync(fixture.Context(), submission);
        var ambiguousStore = new OutcomeAmbiguousStore(
            fixture.Store,
            throwAfterClaim: true,
            throwAfterCompletion: true,
            throwAfterRelease: false);
        var executor = new CapturingExecutor(ProcessWorkExecutionResult.Success());
        var runtime = new ProcessWorkerPoolExecutor(
            ambiguousStore,
            PoolId,
            fixture.Offer("worker/ambiguous", maximumConcurrentClaims: 1),
            executor);

        var result = await runtime.RunOnceAsync(fixture.Context());
        var stored = await fixture.Store.InspectWorkAsync(fixture.Context(), submission.Id);

        Assert.Equal(ProcessWorkerExecutionDisposition.Settled, result.Disposition);
        Assert.Equal(ProcessWorkStatus.Succeeded, stored!.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(2, ambiguousStore.ClaimCalls);
        Assert.Equal(2, ambiguousStore.CompletionCalls);
        Assert.NotNull(executor.Observed);
    }

    [Fact]
    public async Task WorkerPoolExecutor_ExactlyRetriesOutcomeAmbiguousRelease()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var submission = fixture.Submission("work/release", "intent/release", "tenant/a");
        await fixture.Store.SubmitAsync(fixture.Context(), submission);
        var ambiguousStore = new OutcomeAmbiguousStore(
            fixture.Store,
            throwAfterClaim: false,
            throwAfterCompletion: false,
            throwAfterRelease: true);
        var runtime = new ProcessWorkerPoolExecutor(
            ambiguousStore,
            PoolId,
            fixture.Offer("worker/release", maximumConcurrentClaims: 1),
            new CapturingExecutor(new(
                ProcessWorkExecutionDisposition.Retry,
                ProcessWorkEffectEvidence.NotStarted,
                "tests/transient")));

        var result = await runtime.RunOnceAsync(fixture.Context());
        var stored = await fixture.Store.InspectWorkAsync(fixture.Context(), submission.Id);

        Assert.Equal(ProcessWorkerExecutionDisposition.Settled, result.Disposition);
        Assert.Equal(ProcessWorkStatus.Queued, stored!.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Equal(2, ambiguousStore.ReleaseCalls);
    }

    [Fact]
    public async Task OversizedWork_IsDurablyPoisonedWithoutPhysicalDispatch()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var submission = fixture.Submission(
            "work/oversized",
            "intent/oversized",
            "tenant/a",
            capacityUnits: 5);

        var admitted = await fixture.Store.SubmitAsync(fixture.Context(), submission);
        var worker = fixture.Offer("worker/a", maximumConcurrentClaims: 1);
        await fixture.RegisterAsync(worker);
        var claim = await fixture.ClaimAsync(worker);

        Assert.Equal(ProcessDistributionDisposition.Applied, admitted.Disposition);
        Assert.Equal(ProcessWorkStatus.Poisoned, admitted.Work!.Status);
        Assert.Equal(0, admitted.Work.AttemptCount);
        Assert.Equal(ProcessDistributionDisposition.NoEligibleWork, claim.Disposition);
    }

    [Fact]
    public async Task RetryExhaustion_PoisonsLogicalWorkAtConfiguredAttemptBoundary()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1, maximumAttempts: 2);
        var submission = fixture.Submission("work/retry", "intent/retry", "tenant/a");
        var worker = fixture.Offer("worker/a", maximumConcurrentClaims: 1);
        await fixture.Store.SubmitAsync(fixture.Context(), submission);
        await fixture.RegisterAsync(worker);

        var first = (await fixture.ClaimAsync(worker)).Claim!;
        var retry = await fixture.Store.ReleaseAsync(
            fixture.Context(),
            new(
                first,
                ProcessWorkReleaseDisposition.Retry,
                ProcessWorkEffectEvidence.NotStarted,
                "tests/retry",
                fixture.Time.GetUtcNow()));
        var second = (await fixture.ClaimAsync(worker)).Claim!;
        var exhausted = await fixture.Store.ReleaseAsync(
            fixture.Context(),
            new(
                second,
                ProcessWorkReleaseDisposition.Retry,
                ProcessWorkEffectEvidence.NotStarted,
                "tests/retry",
                fixture.Time.GetUtcNow()));

        Assert.Equal(ProcessWorkStatus.Queued, retry.Work!.Status);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(ProcessWorkStatus.Poisoned, exhausted.Work!.Status);
    }

    [Fact]
    public async Task ExpiredDeadline_FailsQueuedWorkBeforeClaim()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var submission = fixture.Submission(
            "work/deadline",
            "intent/deadline",
            "tenant/a",
            deadlineUtc: fixture.Time.GetUtcNow().AddSeconds(1));
        var worker = fixture.Offer("worker/a", maximumConcurrentClaims: 1);
        await fixture.Store.SubmitAsync(fixture.Context(), submission);
        await fixture.RegisterAsync(worker);

        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        var claim = await fixture.ClaimAsync(worker);
        var stored = await fixture.Store.InspectWorkAsync(fixture.Context(), submission.Id);

        Assert.Equal(ProcessDistributionDisposition.NoEligibleWork, claim.Disposition);
        Assert.Equal(ProcessWorkStatus.Failed, stored!.Status);
        Assert.Equal(0, stored.AttemptCount);
    }

    [Fact]
    public async Task QueuedCancellation_IsIdempotentAndPreventsDispatch()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var submission = fixture.Submission("work/cancel", "intent/cancel", "tenant/a");
        var worker = fixture.Offer("worker/a", maximumConcurrentClaims: 1);
        await fixture.Store.SubmitAsync(fixture.Context(), submission);
        await fixture.RegisterAsync(worker);

        var cancelled = await fixture.Store.RequestCancellationAsync(
            fixture.Context(),
            submission.Id,
            "tests/cancelled",
            fixture.Time.GetUtcNow());
        var replay = await fixture.Store.RequestCancellationAsync(
            fixture.Context(),
            submission.Id,
            "tests/cancelled",
            fixture.Time.GetUtcNow());
        var claim = await fixture.ClaimAsync(worker);

        Assert.Equal(ProcessWorkStatus.Cancelled, cancelled.Work!.Status);
        Assert.Equal(ProcessDistributionDisposition.Replayed, replay.Disposition);
        Assert.Equal(ProcessDistributionDisposition.NoEligibleWork, claim.Disposition);
    }

    [Fact]
    public async Task Telemetry_EmitsBoundedMetricsAndTraceOnlyOccurrenceIdentities()
    {
        List<Activity> stopped = [];
        List<(string Name, long Value, Dictionary<string, object?> Tags)> longMeasurements = [];
        List<(string Name, double Value, Dictionary<string, object?> Tags)> doubleMeasurements = [];
        using ActivityListener activityListener = new()
        {
            ShouldListenTo = static source => string.Equals(
                source.Name,
                ProcessDistributionTelemetry.ActivitySourceName,
                StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(activityListener);
        using MeterListener meterListener = new();
        meterListener.InstrumentPublished = static (instrument, listener) =>
        {
            if (string.Equals(instrument.Meter.Name, ProcessDistributionTelemetry.MeterName, StringComparison.Ordinal))
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            longMeasurements.Add((instrument.Name, value, Copy(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            doubleMeasurements.Add((instrument.Name, value, Copy(tags))));
        meterListener.Start();

        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 1);
        var submission = fixture.Submission("work/telemetry", "intent/telemetry", "tenant/a");
        await fixture.Store.SubmitAsync(fixture.Context(), submission);
        var runtime = new ProcessWorkerPoolExecutor(
            fixture.Store,
            PoolId,
            fixture.Offer("worker/telemetry", maximumConcurrentClaims: 1),
            new CapturingExecutor(ProcessWorkExecutionResult.Success()));

        await runtime.RunOnceAsync(fixture.Context());
        var snapshot = await fixture.Store.InspectPoolAsync(
            fixture.Context(),
            PoolId,
            fixture.Time.GetUtcNow());
        ProcessDistributionTelemetry.RecordPoolSnapshot(snapshot!);

        Assert.Contains(stopped, static activity =>
            activity.OperationName == ProcessDistributionTelemetry.WorkerTurnActivityName);
        var execution = Assert.Single(stopped, static activity =>
            activity.OperationName == ProcessDistributionTelemetry.WorkExecutionActivityName);
        Assert.Equal(submission.Id.Value, execution.GetTagItem(ProcessDistributionTelemetry.WorkIdTagName));
        Assert.Contains(longMeasurements, static measurement =>
            measurement.Name == ProcessDistributionTelemetry.OperationsInstrumentName);
        Assert.Contains(longMeasurements, static measurement =>
            measurement.Name == ProcessDistributionTelemetry.TerminalOutcomesInstrumentName);
        Assert.Contains(longMeasurements, static measurement =>
            measurement.Name == ProcessDistributionTelemetry.PoolWorkInstrumentName);
        Assert.Contains(doubleMeasurements, static measurement =>
            measurement.Name == ProcessDistributionTelemetry.QueueDurationInstrumentName);
        Assert.All(
            longMeasurements.SelectMany(static measurement => measurement.Tags.Keys)
                .Concat(doubleMeasurements.SelectMany(static measurement => measurement.Tags.Keys)),
            static key => Assert.DoesNotContain(
                key,
                new[]
                {
                    ProcessDistributionTelemetry.WorkIdTagName,
                    ProcessDistributionTelemetry.WorkerIdTagName,
                    ProcessDistributionTelemetry.DispatchIdTagName,
                    ProcessDistributionTelemetry.FenceTagName
                }));

        static Dictionary<string, object?> Copy(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Dictionary<string, object?> result = new(StringComparer.Ordinal);
            foreach (var tag in tags)
                result.Add(tag.Key, tag.Value);
            return result;
        }
    }

    [Fact]
    public void CapabilityValidation_FailsClosedForReferenceOnlyStore()
    {
        var validation = ProcessDistributionCapabilityValidator.ValidateProduction(
            new InMemoryProcessDistributionStore().Capabilities);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessDistributionDiagnosticCodes.DurabilityUnavailable);
        Assert.Contains(validation.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessDistributionDiagnosticCodes.AtomicProcessCommitUnavailable);
    }

    [Fact]
    public async Task LedgerDocument_StrictRoundTripPreservesClaimsAndFairness()
    {
        var fixture = await Fixture.CreateAsync(maximumConcurrentClaims: 2);
        var worker = fixture.Offer("worker/a", maximumConcurrentClaims: 2);
        await fixture.RegisterAsync(worker);
        await fixture.Store.SubmitAsync(
            fixture.Context(),
            fixture.Submission("work/1", "intent/1", "tenant/a"));
        var claim = (await fixture.ClaimAsync(worker)).Claim!;

        var json = ProcessDistributionJsonSerializer.SerializeLedger(fixture.Store.CaptureLedger());
        var restoredDocument = ProcessDistributionJsonSerializer.DeserializeLedger(json);
        var restored = new InMemoryProcessDistributionStore(restoredDocument);
        var work = await restored.InspectWorkAsync(fixture.Context(), claim.Submission.Id);

        Assert.Equal(ProcessDistributionWireNames.CurrentSchemaVersion, restoredDocument.SchemaVersion);
        Assert.Equal(ProcessWorkStatus.Claimed, work!.Status);
        Assert.Equal(claim.Dispatch, work.Claim!.Dispatch);
        Assert.Equal(claim.Fence, work.Claim.Fence);
    }

    [Fact]
    public async Task AzureFunctionsProfile_FailsClosedForUnboundedAffinityWork()
    {
        var fixture = await Fixture.CreateAsync();
        var evidence = new ProcessDistributionConfigurationEvidence(
            ProcessDistributionConfigurationSource.Adapter,
            "tests/azure-functions",
            "hosting-plan/test");
        var capabilities = new ProcessDistributionStoreCapabilities(
            isDurable: true,
            supportsAtomicClaim: true,
            supportsCompareAndSwap: true,
            supportsWorkerLeases: true,
            supportsClaimRenewal: true,
            supportsMonotonicFencing: true,
            supportsRunnableDiscovery: true,
            supportsCapacityReservations: true,
            supportsPoisonWork: true,
            supportsAtomicProcessCommit: true);
        var profile = ProcessDistributionTargetProfiles.AzureFunctions(
            capabilities,
            maximumExecutionDuration: TimeSpan.FromMinutes(5),
            evidence);

        var unbounded = profile.Validate(fixture.Submission("work/1", "intent/1", "tenant/a"));
        var incompatible = profile.Validate(fixture.Submission(
            "work/2",
            "intent/2",
            "tenant/a",
            affinity: "region/west",
            executionTimeout: TimeSpan.FromMinutes(10)));

        Assert.Contains(unbounded.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessDistributionTargetDiagnosticCodes.ExecutionBoundRequired);
        Assert.Contains(incompatible.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessDistributionTargetDiagnosticCodes.AffinityUnavailable);
        Assert.Contains(incompatible.Diagnostics, static diagnostic =>
            diagnostic.Code == ProcessDistributionTargetDiagnosticCodes.ExecutionDurationExceeded);
    }

    sealed class Fixture(
        InMemoryProcessDistributionStore store,
        MutableTimeProvider time,
        ProcessWorkerPoolDefinition pool)
    {
        long nextClaimRequestOrdinal;

        internal InMemoryProcessDistributionStore Store { get; } = store;

        internal MutableTimeProvider Time { get; } = time;

        internal ProcessWorkerPoolDefinition Pool { get; } = pool;

        internal static async Task<Fixture> CreateAsync(
            int maximumConcurrentClaims = 2,
            int maximumAttempts = 3,
            ImmutableArray<ProcessCapacityDomainLimit> capacityDomains = default)
        {
            var time = new MutableTimeProvider(InitialUtc);
            var store = new InMemoryProcessDistributionStore();
            var pool = new ProcessWorkerPoolDefinition(
                ProcessDistributionWireNames.CurrentSchemaVersion,
                PoolId,
                new ProcessWorkerPoolPolicy(
                    maximumConcurrentClaims,
                    maximumAttempts,
                    workerLeaseDuration: TimeSpan.FromSeconds(10),
                    claimLeaseDuration: TimeSpan.FromSeconds(10),
                    capacity: [new("cpu", 4, "slots")],
                    capacityDomains,
                    ProcessOversizedWorkBehavior.Poison,
                    new(
                        ProcessDistributionConfigurationSource.Explicit,
                        "tests/process-distribution",
                        "fixture/pool")));
            var fixture = new Fixture(store, time, pool);
            var ensured = await store.EnsurePoolAsync(fixture.Context(), pool);
            Assert.Equal(ProcessDistributionDisposition.Applied, ensured.Disposition);
            return fixture;
        }

        internal OperationContext Context() => OperationContext.Create(timeProvider: Time);

        internal ProcessWorkerOffer Offer(
            string id,
            int maximumConcurrentClaims,
            ImmutableArray<ProcessWorkEffectGuarantee> supportedEffectGuarantees = default) => new(
                ProcessDistributionWireNames.CurrentSchemaVersion,
                new(id),
                [Pool.Id],
                [ProcessIrVersion],
                [ProcessWorkKind.Activation],
                supportedEffectGuarantees.IsDefault
                    ? [ProcessWorkEffectGuarantee.Idempotent, ProcessWorkEffectGuarantee.Reconciled]
                    : supportedEffectGuarantees,
                ["capability/test"],
                [new("cpu", 2, "slots")],
                affinities: [],
                maximumConcurrentClaims);

        internal ProcessWorkSubmission Submission(
            string id,
            string idempotency,
            string fairness,
            string? capacityDomain = null,
            ProcessWorkRecoveryMode recoveryMode = ProcessWorkRecoveryMode.Redispatch,
            ProcessWorkEffectGuarantee effectGuarantee = ProcessWorkEffectGuarantee.Idempotent,
            string? referenceId = null,
            string? affinity = null,
            TimeSpan? executionTimeout = null,
            long capacityUnits = 1,
            DateTimeOffset? deadlineUtc = null) => new(
            ProcessDistributionWireNames.CurrentSchemaVersion,
            new(id),
            new(idempotency),
            new ProcessWorkReference(
                new(
                    new("process/test"),
                    new("revision/1"),
                    new("sha256", "test-canonical", "definition-fingerprint")),
                ProcessIrVersion,
                new(new($"instance/{referenceId ?? id}"), new("attempt/1")),
                ProcessWorkKind.Activation,
                new(["activations", referenceId ?? id]),
                new(
                    new("tests", "1"),
                    new($"fixture/{referenceId ?? id}"),
                    DocumentOrigin.User)),
            new ProcessWorkRequirements(
                Pool.Id,
                ["capability/test"],
                [new("cpu", capacityUnits, "slots")],
                effectGuarantee,
                recoveryMode,
                capacityDomain,
                fairness,
                affinity,
                deadlineUtc: deadlineUtc,
                executionTimeout: executionTimeout),
            Time.GetUtcNow());

        internal Task<ProcessDistributionMutationResult> RegisterAsync(ProcessWorkerOffer offer) =>
            Store.RegisterWorkerAsync(Context(), offer, Time.GetUtcNow());

        internal Task<ProcessWorkClaimResult> ClaimAsync(ProcessWorkerOffer offer) =>
            Store.ClaimAsync(
                Context(),
                Pool.Id,
                offer.Worker,
                new($"claim-request/tests/{Interlocked.Increment(ref nextClaimRequestOrdinal)}"),
                Time.GetUtcNow());

        internal Task<ProcessDistributionMutationResult> CompleteAsync(ProcessWorkClaim claim) =>
            Store.CompleteAsync(
                Context(),
                new(
                    claim,
                    ProcessWorkCompletionOutcome.Succeeded,
                    ProcessWorkEffectEvidence.Applied,
                    Time.GetUtcNow()));
    }

    sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        internal void Advance(TimeSpan duration) => utcNow = utcNow.Add(duration);
    }

    sealed class CapturingExecutor(ProcessWorkExecutionResult result) : IProcessDistributedWorkExecutor
    {
        internal ProcessWorkClaim? Observed { get; private set; }

        public ValueTask<ProcessWorkExecutionResult> ExecuteAsync(
            OperationContext context,
            ProcessWorkClaim claim)
        {
            ArgumentNullException.ThrowIfNull(context);
            Observed = claim ?? throw new ArgumentNullException(nameof(claim));
            return ValueTask.FromResult(result);
        }
    }

    sealed class ThrowingExecutor : IProcessDistributedWorkExecutor
    {
        public ValueTask<ProcessWorkExecutionResult> ExecuteAsync(
            OperationContext context,
            ProcessWorkClaim claim) => throw new InvalidOperationException("Injected executor failure.");
    }

    sealed class CancellingExecutor : IProcessDistributedWorkExecutor
    {
        public ValueTask<ProcessWorkExecutionResult> ExecuteAsync(
            OperationContext context,
            ProcessWorkClaim claim) => ValueTask.FromException<ProcessWorkExecutionResult>(
                new OperationCanceledException("Injected non-causal cancellation."));
    }

    sealed class OutcomeAmbiguousStore(
        IProcessDistributionStore inner,
        bool throwAfterClaim,
        bool throwAfterCompletion,
        bool throwAfterRelease) : IProcessDistributionStore
    {
        bool throwAfterClaim = throwAfterClaim;
        bool throwAfterCompletion = throwAfterCompletion;
        bool throwAfterRelease = throwAfterRelease;

        internal int ClaimCalls { get; private set; }

        internal int CompletionCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        public ProcessDistributionStoreCapabilities Capabilities => inner.Capabilities;

        public Task<ProcessDistributionMutationResult> EnsurePoolAsync(
            OperationContext context,
            ProcessWorkerPoolDefinition pool) => inner.EnsurePoolAsync(context, pool);

        public Task<ProcessDistributionMutationResult> SubmitAsync(
            OperationContext context,
            ProcessWorkSubmission submission) => inner.SubmitAsync(context, submission);

        public Task<ProcessDistributionMutationResult> RegisterWorkerAsync(
            OperationContext context,
            ProcessWorkerOffer offer,
            DateTimeOffset observedAtUtc) => inner.RegisterWorkerAsync(context, offer, observedAtUtc);

        public Task<ProcessDistributionMutationResult> RenewWorkerAsync(
            OperationContext context,
            ProcessWorkerIncarnationId worker,
            ProcessWorkerHealth health,
            DateTimeOffset observedAtUtc) => inner.RenewWorkerAsync(context, worker, health, observedAtUtc);

        public Task<ProcessDistributionMutationResult> SetWorkerDrainingAsync(
            OperationContext context,
            ProcessWorkerIncarnationId worker,
            bool draining,
            DateTimeOffset observedAtUtc) => inner.SetWorkerDrainingAsync(context, worker, draining, observedAtUtc);

        public async Task<ProcessWorkClaimResult> ClaimAsync(
            OperationContext context,
            ProcessWorkerPoolId pool,
            ProcessWorkerIncarnationId worker,
            ProcessWorkClaimRequestId request,
            DateTimeOffset observedAtUtc)
        {
            ClaimCalls++;
            var result = await inner.ClaimAsync(context, pool, worker, request, observedAtUtc);
            if (throwAfterClaim)
            {
                throwAfterClaim = false;
                throw new TimeoutException("Injected outcome-ambiguous claim timeout.");
            }
            return result;
        }

        public Task<ProcessDistributionMutationResult> RenewClaimAsync(
            OperationContext context,
            ProcessWorkClaim claim,
            DateTimeOffset observedAtUtc) => inner.RenewClaimAsync(context, claim, observedAtUtc);

        public async Task<ProcessDistributionMutationResult> CompleteAsync(
            OperationContext context,
            ProcessWorkCompletion completion)
        {
            CompletionCalls++;
            var result = await inner.CompleteAsync(context, completion);
            if (throwAfterCompletion)
            {
                throwAfterCompletion = false;
                throw new TimeoutException("Injected outcome-ambiguous completion timeout.");
            }
            return result;
        }

        public async Task<ProcessDistributionMutationResult> ReleaseAsync(
            OperationContext context,
            ProcessWorkRelease release)
        {
            ReleaseCalls++;
            var result = await inner.ReleaseAsync(context, release);
            if (throwAfterRelease)
            {
                throwAfterRelease = false;
                throw new TimeoutException("Injected outcome-ambiguous release timeout.");
            }
            return result;
        }

        public Task<ProcessDistributionMutationResult> ReconcileAsync(
            OperationContext context,
            ProcessWorkReconciliation reconciliation) => inner.ReconcileAsync(context, reconciliation);

        public Task<ProcessDistributionMutationResult> RequestCancellationAsync(
            OperationContext context,
            ProcessWorkId work,
            string reasonCode,
            DateTimeOffset observedAtUtc) => inner.RequestCancellationAsync(context, work, reasonCode, observedAtUtc);

        public Task<ProcessWorkRecord?> InspectWorkAsync(
            OperationContext context,
            ProcessWorkId work) => inner.InspectWorkAsync(context, work);

        public Task<ProcessWorkerPoolSnapshot?> InspectPoolAsync(
            OperationContext context,
            ProcessWorkerPoolId pool,
            DateTimeOffset observedAtUtc) => inner.InspectPoolAsync(context, pool, observedAtUtc);
    }
}
