using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Processes.Execution;
using static Cohesive.Examples.Integrations.HistoricalMarketDataExamples;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    /// <summary>Missed triggers catch up from confirmed progress one bounded window per tick.</summary>
    /// <returns>Completes after two windows, with repeated up-to-date ticks producing no new effects.</returns>
    [Fact]
    public async Task CatchUpUsesAcceptedProgressAcrossTicksAndStopsAtTheBound()
    {
        var destination = new HistoricalDestination();
        var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(ForwardConsumer, (_, actor) => actor == Operator).Receipt;
        control.Change(new("active", 0, IntegrationDesiredState.Active, Operator, At));
        var first = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 1);
        Assert.Equal(CatchUpDisposition.CompletedWindow, first.Disposition);
        Assert.Equal(new DateOnly(2026, 9, 1), first.Worker!.RequestedScope.StartDate);
        Assert.Single(destination.AcceptedDates);
        var firstEntry = await destination.Ledger.ReadAsync(ForwardConsumer, default);
        Assert.Equal(1, firstEntry!.Revision);

        // The next invocation has no local cursor or remembered timer occurrence.
        var second = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 1);
        Assert.Equal(CatchUpDisposition.CompletedWindow, second.Disposition);
        Assert.Equal(new DateOnly(2026, 9, 2), second.Worker!.RequestedScope.StartDate);
        Assert.Equal(2, destination.AcceptedDates.Length);
        var finalEntry = await destination.Ledger.ReadAsync(ForwardConsumer, default);
        Assert.Equal(2, finalEntry!.Revision);
        Assert.Equal(new IngestionDateRangePosition(new(2026, 9, 2), new(2026, 9, 3)), finalEntry.Position);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var current = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 1);
            Assert.Equal(CatchUpDisposition.UpToDate, current.Disposition);
            Assert.Null(current.Worker);
        }
        Assert.Equal(finalEntry, await destination.Ledger.ReadAsync(ForwardConsumer, default));
        Assert.All(new[] { first.Worker, second.Worker }, worker =>
        {
            Assert.Equal(1, worker.ProviderCalls);
            Assert.Equal(1, worker.Publications);
            Assert.Equal(1, worker.Settlements);
        });
    }

    /// <summary>An incomplete first window remains pending and prevents skipping into a later window.</summary>
    /// <returns>Completes without publication, advancement or another provider call on the next tick.</returns>
    [Fact]
    public async Task CatchUpDoesNotSkipOrRefetchAnUnresolvedWindow()
    {
        var destination = new HistoricalDestination();
        var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(ForwardConsumer, (_, _) => true).Receipt;
        control.Change(new("active", 0, IntegrationDesiredState.Active, Operator, At));
        var failed = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 0);
        Assert.Equal(CatchUpDisposition.Unresolved, failed.Disposition);
        Assert.Single(control.Inspect().Pending);
        Assert.Single(failed.Worker!.Coverage!.MissingSessions);
        Assert.Null(await destination.Ledger.ReadAsync(ForwardConsumer, default));
        var next = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 1);
        Assert.Equal(CatchUpDisposition.Unresolved, next.Disposition);
        Assert.Null(next.Worker);
        Assert.Empty(destination.AcceptedDates);
        Assert.Equal(1, failed.Worker.ProviderCalls);
        Assert.Equal(0, failed.Worker.Publications);
    }

    /// <summary>Pause prevents catch-up admission; resume selects the same unconsumed first window.</summary>
    /// <returns>Completes after confirming that paused ticks perform no work or progress change.</returns>
    [Fact]
    public async Task CatchUpPauseDoesNotConsumeAWindow()
    {
        var destination = new HistoricalDestination();
        var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(ForwardConsumer, (_, _) => true).Receipt;
        var paused = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 1);
        Assert.Equal(CatchUpDisposition.AdmissionStopped, paused.Disposition);
        Assert.Null(paused.Worker);
        Assert.Null(await destination.Ledger.ReadAsync(ForwardConsumer, default));
        control.Change(new("resume", 0, IntegrationDesiredState.Active, Operator, At));
        var resumed = await CatchUpOneHistoricalWindow(destination, control, returnedSessions: 1);
        Assert.Equal(CatchUpDisposition.CompletedWindow, resumed.Disposition);
        Assert.Equal(new DateOnly(2026, 9, 1), resumed.Worker!.RequestedScope.StartDate);
    }

    // Explicitly single-dispatcher experiment over the existing synthetic September 1–2 calendar.
    // No wall clock, timer occurrence, provider response, or process-local cursor owns progress.
    // Pending inspection is not a reservation: concurrent dispatchers need durable claims/fencing.
    static async Task<CatchUpTick> CatchUpOneHistoricalWindow(HistoricalDestination destination,
        InMemoryIntegrationLifecycle control, int returnedSessions)
    {
        var lifecycle = control.Inspect();
        if (!lifecycle.Pending.IsEmpty) return new(CatchUpDisposition.Unresolved);
        if (!lifecycle.Accepting) return new(CatchUpDisposition.AdmissionStopped);
        var current = await destination.Ledger.ReadAsync(ForwardConsumer, default);
        var horizon = CreateSelection();
        var start = current is null ? horizon.StartDate
            : Assert.IsType<IngestionDateRangePosition>(current.Position).EndExclusive;
        if (start > horizon.EndDateInclusive) return new(CatchUpDisposition.UpToDate);
        var candidate = QualifyLocalHistorical(SingleSession(start), ForwardConsumer);
        var worker = new ProcessFixture(new FixtureSetup(candidate.Compilation!, returnedSessions,
            destination, ExpectedLedgerRevision: current?.Revision ?? 0));
        var session = new ProcessContinuationIdentity(new($"catch-up/{start:yyyy-MM-dd}"), new("attempt/1"));
        var admitted = control.Admit(new(control.Bind(worker.Compilation), session, lifecycle.Revision, Operator, At));
        // A lifecycle change between inspection and admission must not be silently treated as permission.
        Assert.Equal(IntegrationAdmissionOutcome.Applied, admitted.Outcome);
        var decision = await RunHistorical(worker, session);
        if (decision.Disposition != ProcessActivationDisposition.Completed)
            return new(CatchUpDisposition.Unresolved, worker);
        Assert.True(control.Complete(new(admitted.Receipt, decision, Operator)).IsAccepted);
        return new(CatchUpDisposition.CompletedWindow, worker);
    }

    enum CatchUpDisposition { CompletedWindow, UpToDate, AdmissionStopped, Unresolved }
    sealed record CatchUpTick(CatchUpDisposition Disposition, ProcessFixture? Worker = null);
}
