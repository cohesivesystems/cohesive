using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Processes.Execution;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    /// <summary>A verified failed attempt can be followed by explicitly new work without skipping its window.</summary>
    /// <param name="retrySessions">Rows returned by the newly authorized attempt for the same one-session window.</param>
    /// <returns>Completes with original failure evidence retained and progress only if the new attempt meets coverage.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ExplicitCatchUpAttemptAfterResolutionRechecksTheUnconsumedWindow(int retrySessions)
    {
        var destination = new HistoricalDestination();
        CatchUpTick? failed = null;
        var verificationCalls = 0;
        var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(ForwardConsumer,
            (_, actor) => actor == Operator, request =>
            {
                verificationCalls++;
                // Trusted local accounting: this exact acquisition returned an incomplete response,
                // and the actual Process failed before any publication. This is not remote proof.
                return request.EvidenceReference == AccountedFailure
                    && request.Admission == failed?.Admission?.Receipt
                    && failed?.Worker is { ProviderCalls: 1, Publications: 0, Coverage.IsComplete: false }
                    ? failed.Decision : null;
            }).Receipt;
        control.Change(new("active", 0, IntegrationDesiredState.Active, Operator, At));
        failed = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 0, InitialCatchUpAttempt);
        Assert.Equal(CatchUpDisposition.Unresolved, failed.Disposition);
        Assert.Equal(ProcessActivationDisposition.Failed, failed.Decision!.Disposition);
        var original = failed.Admission!.Receipt;
        var retryAttempt = new ProcessAttemptId("attempt/2");
        var blocked = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 1, retryAttempt);
        Assert.Equal(CatchUpDisposition.Unresolved, blocked.Disposition);
        Assert.Null(blocked.Worker);
        control.Change(new("pause", 1, IntegrationDesiredState.Paused, Operator, At));
        var resolution = new IntegrationFailureResolutionRequest("resolve-catch-up/1", original,
            ExpectedRevision: 2, AccountedFailure, Operator, At);
        Assert.Equal(IntegrationFailureResolutionOutcome.EvidenceRejected,
            control.ResolveFailure(resolution with { EvidenceReference = "unavailable" }).Outcome);
        Assert.Single(control.Inspect().Pending);
        var resolved = control.ResolveFailure(resolution);
        Assert.Equal(IntegrationFailureResolutionOutcome.Applied, resolved.Outcome);
        Assert.Empty(control.Inspect().Pending);
        Assert.Null(await destination.Ledger.ReadAsync(ForwardConsumer, default));
        Assert.Empty(destination.AcceptedDates);
        Assert.Null(control.InspectCompletion(original.Continuation));
        var paused = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 1, retryAttempt);
        Assert.Equal(CatchUpDisposition.AdmissionStopped, paused.Disposition);
        Assert.Null(paused.Worker);
        control.Change(new("resume", 3, IntegrationDesiredState.Active, Operator, At));

        // A fresh control revision cannot turn the original attempt into new work.
        var reused = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 1, InitialCatchUpAttempt);
        Assert.Equal(CatchUpDisposition.AdmissionRejected, reused.Disposition);
        Assert.Equal(IntegrationAdmissionOutcome.IdentityConflict, reused.Admission!.Outcome);
        Assert.Equal(0, reused.Worker!.ProviderCalls);
        Assert.Equal(0, reused.Worker.Publications);
        var replay = control.Admit(new(control.Bind(failed.Worker!.Compilation), original.Continuation,
            original.LifecycleRevision, original.Authorization, original.AtUtc));
        Assert.Equal(IntegrationAdmissionOutcome.Replayed, replay.Outcome);
        Assert.Same(original, replay.Receipt);

        var retry = await CatchUpOneHistoricalWindow(destination, control, retrySessions, retryAttempt);
        Assert.Equal(original.Continuation.ProcessInstanceId, retry.Admission!.Receipt.Continuation.ProcessInstanceId);
        Assert.NotEqual(original.Continuation.ProcessAttemptId, retry.Admission.Receipt.Continuation.ProcessAttemptId);
        // The canonical binding pins the complete scope, calendar and normalizer revisions.
        Assert.Equal(original.Binding, retry.Admission.Receipt.Binding);
        Assert.Equal(failed.Worker.RequestedScope.StartDate, retry.Worker!.RequestedScope.StartDate);
        Assert.Equal(1, retry.Worker.ProviderCalls);
        Assert.Equal(retrySessions, retry.Worker.Publications);
        Assert.Same(resolved.Receipt, control.InspectFailureResolution(original.Continuation));
        Assert.Same(failed.Decision, resolved.Receipt.Decision);
        Assert.Same(resolved.Receipt, control.ResolveFailure(resolution).Receipt);
        Assert.Equal(2, verificationCalls); // Missing evidence, then accepted evidence; replay does not verify again.
        Assert.Equal(1, failed.Worker.ProviderCalls);
        Assert.Equal(0, failed.Worker.Publications);
        if (retrySessions == 0)
        {
            Assert.Equal(CatchUpDisposition.Unresolved, retry.Disposition);
            Assert.Equal(retry.Admission.Receipt, Assert.Single(control.Inspect().Pending));
            Assert.Null(await destination.Ledger.ReadAsync(ForwardConsumer, default));
            var next = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 1, new("attempt/3"));
            Assert.Equal(CatchUpDisposition.Unresolved, next.Disposition);
            Assert.Null(next.Worker);
        }
        else
        {
            Assert.Equal(CatchUpDisposition.CompletedWindow, retry.Disposition);
            Assert.NotNull(control.InspectCompletion(retry.Admission.Receipt.Continuation));
            var accepted = await destination.Ledger.ReadAsync(ForwardConsumer, default);
            Assert.Equal(1, accepted!.Revision);
            Assert.Equal(new IngestionDateRangePosition(new(2026, 9, 1), new(2026, 9, 2)), accepted.Position);
            var following = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 1, InitialCatchUpAttempt);
            Assert.Equal(CatchUpDisposition.CompletedWindow, following.Disposition);
            Assert.Equal(new DateOnly(2026, 9, 2), following.Worker!.RequestedScope.StartDate);
            Assert.Equal(2, (await destination.Ledger.ReadAsync(ForwardConsumer, default))!.Revision);
            Assert.Equal(2, destination.AcceptedDates.Length);
        }
        Assert.Same(resolved.Receipt, control.InspectFailureResolution(original.Continuation));
        Assert.Null(control.InspectCompletion(original.Continuation));
    }
}
