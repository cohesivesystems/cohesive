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
        var forwardWorker = new ProcessFixture(QualifyLocalHistorical(forward, ForwardConsumer).Compilation!, 1, destination);
        var forwardControl = new InMemoryIntegrationLifecycle(ForwardConsumer, (_, actor) => actor == Operator);
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
        var worker = new ProcessFixture(qualified.Compilation!, 1, destination);
        var control = new InMemoryIntegrationLifecycle(BackfillConsumer, (_, actor) => actor == Operator);
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
        var restarted = new ProcessFixture(worker, "policy");
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

    /// <summary>Backfill cannot bypass provider bounds, current dispatch policy or source coverage obligations.</summary>
    /// <param name="restriction">Constraint preventing successful backfill.</param>
    /// <returns>Completes with no accepted backfill progress or destination publication.</returns>
    [Theory]
    [InlineData("range")]
    [InlineData("policy")]
    [InlineData("missing-session")]
    public async Task BackfillKeepsNormalQualificationAndCoverageGates(string restriction)
    {
        var destination = new HistoricalDestination();
        var selection = restriction == "range" ? CreateSelection() : SingleSession(new(2026, 9, 1));
        var qualified = QualifyLocalHistorical(selection, BackfillConsumer,
            Capabilities(selection) with { MaximumRangeDays = 1 });
        if (restriction == "range")
        {
            Assert.False(qualified.IsAdmissible);
            Assert.Contains(qualified.Validation.Diagnostics, d => d.Code == "integrations.qualification.range");
        }
        else
        {
            var original = new ProcessFixture(qualified.Compilation!, restriction == "missing-session" ? 0 : 1, destination);
            var worker = restriction == "policy" ? new ProcessFixture(original, "policy") : original;
            var control = new InMemoryIntegrationLifecycle(BackfillConsumer, (_, actor) => actor == Operator);
            control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At));
            var admitted = control.Admit(new(control.Bind(worker.Compilation), Session, 1, Operator, At)).Receipt;
            var decision = await RunHistorical(worker, Session);
            Assert.Equal(ProcessActivationDisposition.Failed, decision.Disposition);
            Assert.Equal(IntegrationCompletionOutcome.Unresolved, control.Complete(new(admitted, decision, Operator)).Outcome);
            Assert.Single(control.Inspect().Pending);
            Assert.Equal(restriction == "policy" ? 0 : 1, original.ProviderCalls);
            Assert.Equal(0, original.Publications);
        }
        Assert.Empty(destination.AcceptedDates);
        Assert.Null(await destination.Ledger.ReadAsync(BackfillConsumer, default));
        Assert.Null(await destination.Ledger.ReadAsync(ForwardConsumer, default));
    }

    /// <summary>Even a fresh ledger revision cannot authorize rewinding an existing forward flow.</summary>
    /// <returns>Completes with a failed Process and an unchanged forward entry after the conflicting publication.</returns>
    [Fact]
    public async Task ReusingForwardFlowForOlderWindowCannotRewindProgress()
    {
        var destination = new HistoricalDestination();
        var forward = new ProcessFixture(QualifyLocalHistorical(SingleSession(new(2026, 9, 2)), ForwardConsumer).Compilation!, 1, destination);
        Assert.Equal(ProcessActivationDisposition.Completed,
            (await RunHistorical(forward, new(new("forward"), new("attempt/1")))).Disposition);
        var original = await destination.Ledger.ReadAsync(ForwardConsumer, default);
        var mistaken = new ProcessFixture(QualifyLocalHistorical(SingleSession(new(2026, 9, 1)), ForwardConsumer).Compilation!,
            1, destination, expectedLedgerRevision: original!.Revision);
        var failed = await RunHistorical(mistaken, new(new("mistaken-backfill"), new("attempt/1")));
        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        Assert.Equal("integrations.ledger.position-transition", mistaken.LastAdvance!.DiagnosticCode);
        Assert.Equal(original, await destination.Ledger.ReadAsync(ForwardConsumer, default));
        // Publication preceded the rejected ledger advance. It is retained, never rolled back or hidden.
        Assert.Equal(1, mistaken.Publications);
        Assert.Equal(0, mistaken.Settlements);
        Assert.Equal(2, destination.AcceptedDates.Length);
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
