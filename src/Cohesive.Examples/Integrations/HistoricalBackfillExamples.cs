using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Processes.Execution;
using static Cohesive.Examples.Integrations.HistoricalMarketDataExamples;
using static Cohesive.Examples.Integrations.TiingoCoverageEvidenceExamples;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    static readonly IngestionLedgerAddress ForwardConsumer = new("research/eod", "tiingo", "research/bars", "SPY");
    static readonly IngestionLedgerAddress BackfillConsumer = ForwardConsumer with { Flow = "research/backfill/september-1" };

    /// <summary>Publishes an older bounded window to the same local destination without resetting forward progress.</summary>
    /// <returns>Completes with independent progress receipts and both dates present in the shared destination.</returns>
    [Fact]
    public async Task BoundedHistoricalBackfillPreservesForwardProgress()
    {
        var destination = new HistoricalDestination();
        var forward = SingleSession(new(2026, 9, 2));
        var backfill = SingleSession(new(2026, 9, 1));
        var forwardWorker = new ProcessFixture(new FixtureSetup(QualifyLocalHistorical(forward, ForwardConsumer).Compilation!, 1, destination));
        var forwardControl = new InMemoryIntegrationLifecycleScope().CreateConsumer(ForwardConsumer, (_, actor) => actor == Operator);
        forwardControl.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At));
        var forwardSession = new ProcessContinuationIdentity(new("forward/september-2"), new("attempt/1"));
        var forwardAdmission = forwardControl.Admit(new(forwardControl.Bind(forwardWorker.Compilation), forwardSession, 1, Operator, At)).Receipt;
        var forwardDone = await RunHistorical(forwardWorker, forwardSession);
        Assert.True(forwardControl.Complete(new(forwardAdmission, forwardDone, Operator)).IsAccepted);
        var originalForward = await destination.Ledger.ReadAsync(ForwardConsumer, default);
        Assert.Equal(new IngestionDateRangePosition(new(2026, 9, 2), new(2026, 9, 3)), originalForward!.Position);

        // A bounded scope is ordinary authoring input. Separate flow identity owns backfill progress.
        // A one-calendar-day provider limit is enforced, not silently relaxed for backfill.
        var qualified = QualifyLocalHistorical(backfill, BackfillConsumer,
            Capabilities(backfill) with { MaximumRangeDays = 1 });
        Assert.True(qualified.IsAdmissible);
        var worker = new ProcessFixture(new FixtureSetup(qualified.Compilation!, 1, destination));
        var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(BackfillConsumer, (_, actor) => actor == Operator);
        var binding = control.Bind(worker.Compilation);
        var identity = new ProcessContinuationIdentity(new("backfill/september-1"), new("attempt/1"));
        var intent = new IntegrationAdmissionRequest(binding, identity, 0, Operator, At);
        Assert.Equal(IntegrationAdmissionOutcome.AdmissionStopped, control.Admit(intent).Outcome);
        Assert.Equal(0, worker.ProviderCalls);
        control.Change(new("activate-backfill", 0, IntegrationDesiredState.Active, Operator, At));
        intent = intent with { ExpectedRevision = 1 };
        var admission = control.Admit(intent).Receipt;
        var decision = worker.Start(identity);
        var acquire = Request(decision);
        var acquired = await worker.DispatchOnce(acquire);
        // Drain this admitted window, but do not accept another window while paused.
        control.Change(new("pause-backfill", 1, IntegrationDesiredState.Paused, Operator, At));
        Assert.Equal(IntegrationAdmissionOutcome.AdmissionStopped, control.Admit(intent with
            { Continuation = new(new("another-window"), new("attempt/1")), ExpectedRevision = 2 }).Outcome);
        var restarted = new ProcessFixture(worker, DispatchChange.PolicyDenied);
        decision = restarted.Reply(decision, acquired);
        while (decision.Disposition == ProcessActivationDisposition.DurableCut)
            decision = restarted.Reply(decision, await restarted.DispatchOnce(Request(decision)));
        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.True(control.Complete(new(admission, decision, Operator)).IsAccepted);
        Assert.False(control.Inspect().Accepting);
        Assert.False(control.Inspect().Draining);
        Assert.Same(admission, control.Admit(intent).Receipt);
        var originalBackfillReceipt = worker.LastAdvance!.Receipt;
        Assert.Equal(new IngestionDateRangePosition(new(2026, 9, 1), new(2026, 9, 2)), originalBackfillReceipt!.Entry.Position);
        Assert.Equal(1, originalBackfillReceipt.Entry.Revision);
        Assert.Equal(originalForward, await destination.Ledger.ReadAsync(ForwardConsumer, default));
        Assert.Equal(new[] { new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2) }, destination.AcceptedDates);
        Assert.Equal(1, worker.ProviderCalls);
        Assert.Equal(1, worker.Publications);
        Assert.Equal(1, worker.Settlements);
        Assert.Equal(1, forwardWorker.Publications);
    }

    /// <summary>A backfill outside the provider's bounded offer is rejected with qualification diagnostics.</summary>
    [Fact]
    public void BackfillOutsideProviderRangeIsRejectedBeforeBinding()
    {
        var selection = CreateSelection();
        var qualified = AssessLocalHistorical(selection, BackfillConsumer,
            Capabilities(selection) with { MaximumRangeDays = 1 });
        Assert.False(qualified.IsAdmissible);
        Assert.Contains(qualified.Validation.Diagnostics, d => d.Code == "integrations.qualification.range");
        var diagnostic = Assert.ThrowsAny<Exception>(() => QualifyLocalHistorical(selection, BackfillConsumer,
            Capabilities(selection) with { MaximumRangeDays = 1 }));
        Assert.Contains("integrations.qualification.range", diagnostic.Message);
    }

    /// <summary>Backfill retains current dispatch policy and source coverage obligations.</summary>
    /// <param name="change">Current dispatch condition.</param>
    /// <param name="sessions">Number of source sessions returned.</param>
    /// <returns>Completes with no accepted progress or destination publication.</returns>
    [Theory]
    [InlineData(DispatchChange.PolicyDenied, 1)]
    [InlineData(DispatchChange.Unchanged, 0)]
    public async Task BackfillKeepsNormalQualificationAndCoverageGates(DispatchChange change, int sessions)
    {
        var destination = new HistoricalDestination();
        var selection = SingleSession(new(2026, 9, 1));
        var qualified = QualifyLocalHistorical(selection, BackfillConsumer,
            Capabilities(selection) with { MaximumRangeDays = 1 });
        var original = new ProcessFixture(new FixtureSetup(qualified.Compilation!, sessions, destination));
        var worker = new ProcessFixture(original, change);
        var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(BackfillConsumer, (_, actor) => actor == Operator);
        control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At));
        var admitted = control.Admit(new(control.Bind(worker.Compilation), Session, 1, Operator, At)).Receipt;
        var decision = await RunHistorical(worker, Session);
        Assert.Equal(ProcessActivationDisposition.Failed, decision.Disposition);
        Assert.Equal(IntegrationCompletionOutcome.Unresolved, control.Complete(new(admitted, decision, Operator)).Outcome);
        Assert.Single(control.Inspect().Pending);
        Assert.Equal(change == DispatchChange.PolicyDenied ? 0 : 1, original.ProviderCalls);
        Assert.Equal(0, original.Publications);
        Assert.Empty(destination.AcceptedDates);
        Assert.Null(await destination.Ledger.ReadAsync(BackfillConsumer, default));
        Assert.Null(await destination.Ledger.ReadAsync(ForwardConsumer, default));
    }

    /// <summary>Even a fresh ledger revision cannot authorize rewinding an existing forward flow.</summary>
    /// <returns>Completes with a failed Process and an unchanged forward entry before any external effect.</returns>
    [Fact]
    public async Task ReusingForwardFlowForOlderWindowCannotRewindProgress()
    {
        var destination = new HistoricalDestination();
        var forward = new ProcessFixture(new FixtureSetup(QualifyLocalHistorical(SingleSession(new(2026, 9, 2)), ForwardConsumer).Compilation!, 1, destination));
        Assert.Equal(ProcessActivationDisposition.Completed,
            (await RunHistorical(forward, new(new("forward"), new("attempt/1")))).Disposition);
        var original = await destination.Ledger.ReadAsync(ForwardConsumer, default);
        var mistaken = new ProcessFixture(new FixtureSetup(QualifyLocalHistorical(SingleSession(new(2026, 9, 1)), ForwardConsumer).Compilation!,
            1, destination, ExpectedLedgerRevision: original!.Revision));
        var failed = await RunHistorical(mistaken, new(new("mistaken-backfill"), new("attempt/1")));
        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        Assert.Contains(mistaken.AcquisitionValidation!.Diagnostics, d => d.Code == "integrations.ledger.position-transition");
        Assert.Null(mistaken.PublicationValidation);
        Assert.Null(mistaken.LastAdvance);
        Assert.Equal(original, await destination.Ledger.ReadAsync(ForwardConsumer, default));
        Assert.Equal(0, mistaken.ProviderCalls);
        Assert.Equal(0, mistaken.Publications);
        Assert.Equal(0, mistaken.Settlements);
        Assert.Single(destination.AcceptedDates);
    }

    /// <summary>A competing advancement after acquisition blocks a now-stale publication.</summary>
    /// <returns>Completes with only the competing publication present and no silent revision refresh.</returns>
    [Fact]
    public async Task ProgressIsRecheckedBeforePublication()
    {
        var destination = new HistoricalDestination();
        var selected = QualifyLocalHistorical(SingleSession(new(2026, 9, 1)), ForwardConsumer).Compilation!;
        var waitingWorker = new ProcessFixture(new FixtureSetup(selected, 1, destination));
        var waiting = waitingWorker.Start(new(new("waiting"), new("attempt/1")));
        waiting = waitingWorker.Reply(waiting, await waitingWorker.DispatchOnce(Request(waiting)));
        Assert.Equal(1, waitingWorker.ProviderCalls);
        var winner = new ProcessFixture(new FixtureSetup(selected, 1, destination));
        Assert.Equal(ProcessActivationDisposition.Completed,
            (await RunHistorical(winner, new(new("winner"), new("attempt/1")))).Disposition);
        var entry = await destination.Ledger.ReadAsync(ForwardConsumer, default);
        var failed = waitingWorker.Reply(waiting, await waitingWorker.DispatchOnce(Request(waiting)));
        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        Assert.Contains(waitingWorker.PublicationValidation!.Diagnostics, d => d.Code == "integrations.ledger.revision-conflict");
        Assert.True(waitingWorker.AcquisitionValidation!.IsValid);
        Assert.Equal(0, waitingWorker.Publications);
        Assert.Null(waitingWorker.LastAdvance);
        Assert.Equal(entry, await destination.Ledger.ReadAsync(ForwardConsumer, default));
        Assert.Single(destination.Publications);
    }

    static HistoricalCoverageScope SingleSession(DateOnly date) => CreateSelection() with
        { StartDate = date, EndDateInclusive = date, ExpectedSessions = [date] };

    static async Task<ProcessActivationDecision> RunHistorical(ProcessFixture worker, ProcessContinuationIdentity identity)
    {
        var decision = worker.Start(identity);
        while (decision.Disposition == ProcessActivationDisposition.DurableCut)
            decision = worker.Reply(decision, await worker.DispatchOnce(Request(decision)));
        return decision;
    }

}
