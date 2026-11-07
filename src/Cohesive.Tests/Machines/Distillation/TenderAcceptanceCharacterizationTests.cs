using Cohesive.Examples.TenderAcceptance;
using Cohesive.Execution;
using Cohesive.Machines;
using Cohesive.Model;

namespace Cohesive.Tests.Machines.Distillation;

// Source-derived characterizations. These do not execute the archived application's code.
public sealed class TenderAcceptanceCharacterizationTests
{
    [Theory]
    [InlineData(false, false, "CreateLoad,PersistLink,SendAcceptance,MarkLoadPending,PersistPendingTender")]
    [InlineData(false, true, "CreateLoad,PersistLink,SendAcceptance,MarkLoadConfirmed,PersistConfirmedTender")]
    [InlineData(true, false, "CreateLoad,PersistLink,SendAcceptance,PersistPendingTender,MarkLoadPending")]
    [InlineData(true, true, "CreateLoad,PersistLink,SendAcceptance,PersistConfirmedTender,MarkLoadConfirmed")]
    public void E02_E03_acceptance_preserves_observed_effect_order(bool eventSourced, bool autoAck, string expected)
    {
        var plan = AcceptanceCoordination.ObserveAcceptance(
            eventSourced ? PersistencePath.EventSourced : PersistencePath.Legacy, autoAck);
        var run = AcceptanceTraceHost.Run(plan);
        Assert.Equal(expected, string.Join(",", run.Completed));
        Assert.Equal(new AcceptanceSnapshot(true, true, true, !autoAck, autoAck), run.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void E02_send_failure_can_leave_a_persisted_linked_load(bool eventSourced)
    {
        var run = AcceptanceTraceHost.Run(AcceptanceCoordination.ObserveAcceptance(
            eventSourced ? PersistencePath.EventSourced : PersistencePath.Legacy, autoAcknowledged: false),
            failBefore: AcceptanceEffect.SendAcceptance);
        Assert.Equal(new AcceptanceSnapshot(true, true), run.Snapshot);
        Assert.Equal(AcceptanceEffect.SendAcceptance, run.FailedBefore);
        Assert.Equal([AcceptanceEffect.CreateLoad, AcceptanceEffect.PersistLink], run.Completed);
    }

    [Fact]
    public void E03_persistence_order_produces_different_partial_commits()
    {
        var legacy = AcceptanceTraceHost.Run(AcceptanceCoordination.ObserveAcceptance(PersistencePath.Legacy, true),
            failBefore: AcceptanceEffect.PersistConfirmedTender);
        var events = AcceptanceTraceHost.Run(AcceptanceCoordination.ObserveAcceptance(PersistencePath.EventSourced, true),
            failBefore: AcceptanceEffect.MarkLoadConfirmed);
        Assert.Equal(new AcceptanceSnapshot(true, true, false, false, true), legacy.Snapshot);
        Assert.Equal(new AcceptanceSnapshot(true, true, true, false, false), events.Snapshot);
        Assert.Contains(AcceptanceEffect.SendAcceptance, legacy.Completed);
        Assert.Contains(AcceptanceEffect.SendAcceptance, events.Completed);
    }

    [Fact]
    public void E04_receipt_marks_load_before_confirming_tender()
    {
        var pending = new AcceptanceSnapshot(true, true, true, true, false);
        var plan = AcceptanceCoordination.ObserveReceipt(loadAlreadyAcknowledged: false);
        Assert.Equal("MarkLoadConfirmed,PublishTripUpdated,PersistConfirmedTender", string.Join(",", plan));
        Assert.Equal(pending with { PendingReceipt = false, LoadAcknowledged = true }, AcceptanceTraceHost.Run(plan, pending).Snapshot);
        var interrupted = AcceptanceTraceHost.Run(plan, pending, AcceptanceEffect.PublishTripUpdated);
        Assert.True(interrupted.Snapshot.PendingReceipt);
        Assert.True(interrupted.Snapshot.LoadAcknowledged);
        // Replaying the observed no-updates branch does not repair the tender pending flag.
        var retry = AcceptanceTraceHost.Run(AcceptanceCoordination.ObserveReceipt(true), interrupted.Snapshot);
        Assert.True(retry.Snapshot.PendingReceipt);
        Assert.Equal([AcceptanceEffect.PersistAcknowledgementLog], retry.Completed);
    }

    [Theory]
    [InlineData(false, "accepted-awaiting-ack")]
    [InlineData(true, "accepted-confirmed")]
    public void Proposal_matches_successful_acceptance_outcomes(bool autoAck, string target)
    {
        var machine = TenderAcceptanceMachines.Tender();
        var decision = machine.Evaluate(machine.Initial, "send-result",
            ObservationValue.FromObject(new AcceptanceFacts(true, false, false)),
            ObservationValue.FromObject(new SendResult(autoAck)));
        Assert.Equal(MachineDecisionKind.Accepted, decision.Kind);
        Assert.Equal(target, decision.Edge!.To);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Proposal_rejects_missing_link_cancellation_and_duplicate_admission(bool linked, bool cancelled, bool duplicate)
    {
        var decision = TenderAcceptanceMachines.Tender().Evaluate("unaccepted", "send-result",
            ObservationValue.FromObject(new AcceptanceFacts(linked, cancelled, duplicate)),
            ObservationValue.FromObject(new SendResult(false)));
        Assert.Equal(MachineDecisionKind.Disabled, decision.Kind);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void P01_proposed_receipt_requires_positive_correlated_evidence(bool correlated, bool positive, bool accepted)
    {
        var result = TenderAcceptanceMachines.Tender().Evaluate("accepted-awaiting-ack", "receipt-confirmed",
            ObservationValue.FromObject(new AcceptanceFacts(true, false, false)),
            ObservationValue.FromObject(new Receipt(correlated, positive)));
        Assert.Equal(accepted ? MachineDecisionKind.Accepted : MachineDecisionKind.Disabled, result.Kind);
    }

    [Fact]
    public void Proposal_requires_host_coordination_instead_of_claiming_atomic_coherence()
    {
        var linkage = TenderAcceptanceMachines.Linkage();
        var load = TenderAcceptanceMachines.LoadAcknowledgement();
        var relation = new MachineCoherenceBuilder(linkage, load)
            .Allow("unlinked", "not-acknowledged").Allow("linked", "not-acknowledged").Allow("linked", "acknowledged");
        var claim = relation.Analyze();
        Assert.Equal(MachineProofState.Disproven, claim.State);
        Assert.NotEmpty(claim.Trace);
    }

    [Fact]
    public void Proposal_routes_uncertain_completion_to_reconciliation_without_blind_retry()
    {
        var machine = TenderAcceptanceMachines.Coordination();
        var state = machine.Decide(machine.Initial, "link-persisted")!.To;
        state = machine.Decide(state, "send-pending")!.To;
        Assert.Equal("awaiting-receipt", state);
        var failed = machine.Decide(state, "uncertain")!.To;
        Assert.Equal("reconciliation-required", failed);
        Assert.Empty(machine.Enabled(failed));
        var confirming = machine.Decide(state, "receipt-confirmed")!.To;
        Assert.Equal("complete", machine.Decide(confirming, "participants-confirmed")!.To);
    }

    [Fact]
    public void Fluent_source_lowers_deterministically_and_round_trips()
    {
        Func<MachineDefinition>[] sources = [TenderAcceptanceMachines.Tender, TenderAcceptanceMachines.Linkage,
            TenderAcceptanceMachines.LoadAcknowledgement, TenderAcceptanceMachines.Coordination];
        foreach (var source in sources)
        {
            var machine = source();
            Assert.Equal(machine.Fingerprint, source().Fingerprint);
            var json = ExecutionDefinitionJsonSerializer.Serialize(machine.Document);
            Assert.Equal(machine.Fingerprint, MachineDefinition.Deserialize(json).Fingerprint);
            Assert.Contains("stateDiagram-v2", machine.ToMermaid());
        }
    }
}
