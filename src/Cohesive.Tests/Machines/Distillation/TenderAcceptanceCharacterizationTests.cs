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

    [Fact]
    public void D02_linkage_and_acceptance_are_movements_of_one_tender_lifecycle()
    {
        var tender = TenderAcceptanceMachines.Tender();
        var facts = ObservationValue.FromObject(new TenderState("load-1", false, false, false, ""));
        var input = ObservationValue.FromObject(new SendResult(false));
        Assert.Equal(tender.Unlinked.Id, tender.Definition.Initial);
        Assert.Equal(MachineDecisionKind.Illegal, tender.Definition.Evaluate(
            tender.Unlinked.Id, tender.SendPending.Resolve().Edge.Action, facts, input).Kind);

        var attached = tender.Definition.Decide(tender.Unlinked.Id, tender.AttachLoad.Resolve().Edge.Action)!;
        Assert.Equal(tender.Offered.Id, attached.To);
        Assert.Null(tender.Definition.Decide(attached.To, tender.AttachLoad.Resolve().Edge.Action));
        var accepted = tender.Definition.Evaluate(attached.To, tender.SendPending.Resolve().Edge.Action, facts, input);
        Assert.Equal(MachineDecisionKind.Accepted, accepted.Kind);
        Assert.Equal(tender.Pending.Id, accepted.Edge!.To);
        Assert.Same(tender.Definition, tender.AttachLoad.Resolve().Machine);
        Assert.Same(tender.Definition, tender.SendPending.Resolve().Machine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Proposal_matches_successful_acceptance_outcomes(bool autoAck)
    {
        var machine = TenderAcceptanceMachines.Tender();
        var decision = machine.Definition.Evaluate(machine.Offered.Id, machine.SendPending.Resolve().Edge.Action,
            ObservationValue.FromObject(new TenderState("load-1", false, false, false, "")),
            ObservationValue.FromObject(new SendResult(autoAck)));
        Assert.Equal(MachineDecisionKind.Accepted, decision.Kind);
        Assert.Equal((autoAck ? machine.Confirmed : machine.Pending).Id, decision.Edge!.To);
        Assert.Equal((autoAck ? machine.SendConfirmed : machine.SendPending).Id, decision.Edge.Id);
        Assert.Equal(machine.SendPending.Resolve().Edge.Action, machine.SendConfirmed.Resolve().Edge.Action);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Proposal_rejects_missing_link_cancellation_and_duplicate_admission(bool linked, bool cancelled, bool duplicate)
    {
        var machine = TenderAcceptanceMachines.Tender();
        var decision = machine.Definition.Evaluate(machine.Offered.Id, machine.SendPending.Resolve().Edge.Action,
            ObservationValue.FromObject(new TenderState("load-1", false, false, cancelled, "")),
            ObservationValue.FromObject(new SendResult(false, LinkedLoadExists: linked, AlreadyAcceptedLink: duplicate)));
        Assert.Equal(MachineDecisionKind.Disabled, decision.Kind);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void P01_proposed_receipt_requires_positive_correlated_evidence(bool correlated, bool positive, bool accepted)
    {
        var machine = TenderAcceptanceMachines.Tender();
        var result = machine.Definition.Evaluate(machine.Pending.Id, machine.ReceiptConfirmed.Resolve().Edge.Action,
            ObservationValue.FromObject(new TenderState("load-1", false, false, false, "")),
            ObservationValue.FromObject(new Receipt(correlated, positive)));
        Assert.Equal(accepted ? MachineDecisionKind.Accepted : MachineDecisionKind.Disabled, result.Kind);
    }

    [Fact]
    public void Proposal_requires_host_coordination_instead_of_claiming_atomic_coherence()
    {
        var process = TenderAcceptanceMachines.Coordination();
        var load = TenderAcceptanceMachines.LoadAcknowledgement();
        var relation = new MachineCoherenceBuilder(process.Definition, load.Definition);
        foreach (var state in process.Definition.States)
        {
            relation.Allow(state, load.Confirmed.Id);
            if (state != process.Complete.Id) relation.Allow(state, load.Pending.Id);
        }
        // Unchecked process completion could occur before the related load acknowledges.
        // The host must substantiate ParticipantsConfirmed before advancing the process.
        var claim = relation.Analyze();
        Assert.Equal(MachineProofState.Disproven, claim.State);
        Assert.NotEmpty(claim.Trace);
    }

    [Fact]
    public void Proposal_routes_uncertain_completion_to_reconciliation_without_blind_retry()
    {
        var machine = TenderAcceptanceMachines.Coordination();
        var state = machine.Definition.Decide(machine.Preparing.Id, machine.LinkPersisted.Resolve().Edge.Action)!.To;
        state = machine.Definition.Decide(state, machine.SendPending.Resolve().Edge.Action)!.To;
        Assert.Equal(machine.AwaitingReceipt.Id, state);
        var failed = machine.Definition.Decide(state, machine.Uncertain[machine.AwaitingReceipt].Resolve().Edge.Action)!.To;
        Assert.Equal(machine.ReconciliationRequired.Id, failed);
        Assert.Empty(machine.Definition.Enabled(failed));
        var confirming = machine.Definition.Decide(state, machine.ReceiptConfirmed.Resolve().Edge.Action)!.To;
        Assert.Equal(machine.Complete.Id,
            machine.Definition.Decide(confirming, machine.ParticipantsConfirmed.Resolve().Edge.Action)!.To);
    }

    [Fact]
    public void Declarations_expose_all_handles_from_their_frozen_definition()
    {
        var tender = TenderAcceptanceMachines.Tender();
        Check(tender.Definition, [tender.Unlinked, tender.Offered, tender.Pending, tender.Confirmed],
            [tender.AttachLoad, tender.SendPending, tender.SendConfirmed, tender.ReceiptConfirmed]);
        var load = TenderAcceptanceMachines.LoadAcknowledgement();
        Check(load.Definition, [load.Pending, load.Confirmed], [load.AcceptPending, load.Confirm]);
        var coordination = TenderAcceptanceMachines.Coordination();
        Check(coordination.Definition,
            [coordination.Preparing, coordination.Linked, coordination.AwaitingReceipt, coordination.Confirming,
                coordination.Complete, coordination.ReconciliationRequired],
            [coordination.LinkPersisted, coordination.SendPending, coordination.SendConfirmed,
                coordination.ReceiptConfirmed, coordination.ParticipantsConfirmed, ..coordination.Uncertain.Values]);
        foreach (var (state, movement) in coordination.Uncertain)
        {
            Assert.Equal(state.Id, movement.Resolve().Edge.From);
            Assert.Equal(coordination.ReconciliationRequired.Id, movement.Resolve().Edge.To);
        }

        static void Check(MachineDefinition definition, MachineState[] states, MachineMovement[] movements)
        {
            Assert.Equal(definition.States.Order(), states.Select(state => state.Id).Order());
            Assert.Equal(definition.Edges.Select(edge => edge.Id).Order(), movements.Select(movement => movement.Id).Order());
            foreach (var state in states)
            {
                Assert.Same(definition, state.Resolve().Machine);
                Assert.Throws<InvalidOperationException>(() => state.Initial());
            }
            foreach (var movement in movements)
            {
                Assert.Same(definition, movement.Resolve().Machine);
                Assert.Contains(movement.Resolve().Edge, definition.Edges);
                Assert.Throws<InvalidOperationException>(() => movement.To(states[0]));
            }
        }
    }

    [Fact]
    public void Fluent_source_lowers_deterministically_and_round_trips()
    {
        Func<MachineDefinition>[] sources = [() => TenderAcceptanceMachines.Tender().Definition,
            () => TenderAcceptanceMachines.LoadAcknowledgement().Definition, () => TenderAcceptanceMachines.Coordination().Definition];
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
