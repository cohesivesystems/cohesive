using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Processes.Execution;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    /// <summary>One partition pause stops both flows while their exact admitted publications reconcile and drain.</summary>
    /// <returns>Completes with independent ledger receipts, no repeat effects and the group still paused.</returns>
    [Fact]
    public async Task PartitionPausePreservesBothFlowsPublicationReconciliation()
    {
        var destination = new HistoricalDestination();
        var forward = new ProcessFixture(new FixtureSetup(
            QualifyLocalHistorical(SingleSession(new(2026, 9, 2)), ForwardConsumer).Compilation!, 1, destination));
        var backfill = new ProcessFixture(new FixtureSetup(
            QualifyLocalHistorical(SingleSession(new(2026, 9, 1)), BackfillConsumer).Compilation!, 1, destination));
        var ownership = new InMemoryIntegrationLifecycleScope();
        var group = ownership.CreateGroup("research/tiingo/SPY",
            (_, actor) => actor == Operator, (_, _, actor) => actor == Operator);
        var forwardControl = group.Register(new(ForwardConsumer, 0, Operator, At)).Receipt.Controller;
        var backfillControl = group.Register(new(BackfillConsumer, 1, Operator, At)).Receipt.Controller;
        forwardControl.Change(new("active", 0, IntegrationDesiredState.Active, Operator, At));
        backfillControl.Change(new("active", 0, IntegrationDesiredState.Active, Operator, At));
        group.Change(new("active", 2, IntegrationDesiredState.Active, Operator, At));
        var forwardRequest = new IntegrationAdmissionRequest(forwardControl.Bind(forward.Compilation),
            new(new("group/forward"), new("attempt/1")), 1, Operator, At);
        var backfillRequest = new IntegrationAdmissionRequest(backfillControl.Bind(backfill.Compilation),
            new(new("group/backfill"), new("attempt/1")), 1, Operator, At);
        var forwardAdmission = forwardControl.Admit(forwardRequest).Receipt;
        var backfillAdmission = backfillControl.Admit(backfillRequest).Receipt;
        var forwardWaiting = forward.Start(forwardRequest.Continuation);
        forwardWaiting = forward.Reply(forwardWaiting, await forward.DispatchOnce(Request(forwardWaiting)));
        var backfillWaiting = backfill.Start(backfillRequest.Continuation);
        backfillWaiting = backfill.Reply(backfillWaiting, await backfill.DispatchOnce(Request(backfillWaiting)));
        await Assert.ThrowsAsync<IOException>(async () => await forward.DispatchOnce(Request(forwardWaiting), DispatchLoss.AfterEffect));
        await Assert.ThrowsAsync<IOException>(async () => await backfill.DispatchOnce(Request(backfillWaiting), DispatchLoss.AfterEffect));
        Assert.Equal(IntegrationChangeOutcome.Applied,
            group.Change(new("pause-partition", 3, IntegrationDesiredState.Paused, Operator, At)).Outcome);
        Assert.True(forwardControl.Inspect().Draining);
        Assert.True(backfillControl.Inspect().Draining);
        Assert.Same(forwardAdmission, forwardControl.Admit(forwardRequest).Receipt);
        Assert.Same(backfillAdmission, backfillControl.Admit(backfillRequest).Receipt);
        Assert.Equal(IntegrationAdmissionOutcome.GroupAdmissionStopped, forwardControl.Admit(forwardRequest with
            { Continuation = new(new("new-forward"), new("attempt/1"))}).Outcome);
        Assert.Equal(IntegrationAdmissionOutcome.GroupAdmissionStopped, backfillControl.Admit(backfillRequest with
            { Continuation = new(new("new-backfill"), new("attempt/1"))}).Outcome);

        // These are retained-effect reconciliations, not new admission or replacement provider calls.
        foreach (var (original, waiting, control, admission) in new[]
        {
            (forward, forwardWaiting, forwardControl, forwardAdmission),
            (backfill, backfillWaiting, backfillControl, backfillAdmission)
        })
        {
            var worker = new ProcessFixture(original, DispatchChange.PolicyDenied);
            var recovered = worker.ReconcilePublication(Request(waiting), Operator);
            Assert.NotNull(recovered);
            var decision = worker.Reply(waiting, recovered);
            while (decision.Disposition == ProcessActivationDisposition.DurableCut)
                decision = worker.Reply(decision, await worker.DispatchOnce(Request(decision)));
            Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
            Assert.True(control.Complete(new(admission, decision, Operator)).IsAccepted);
            Assert.False(control.Inspect().Draining);
            Assert.False(control.Inspect().Accepting);
            Assert.Equal(IntegrationDesiredState.Active, control.Inspect().Desired);
            Assert.Equal(IntegrationDesiredState.Paused, control.Inspect().Group!.Desired);
            Assert.Equal(1, original.ProviderCalls);
            Assert.Equal(1, original.Publications);
            Assert.Equal(1, original.Settlements);
            Assert.Equal(1, (await destination.Ledger.ReadAsync(original.Address, default))!.Revision);
        }
        Assert.Equal(2, destination.AcceptedDates.Length);
        Assert.Equal(4, group.Inspect().Revision);
        Assert.Equal(IntegrationDesiredState.Paused, group.Inspect().Desired);
    }
}
