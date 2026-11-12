# From committed movements to reactions

## Implemented: committed occurrence observation

`EntityMachineOccurrence.FromReceipt` projects the existing atomic entity receipt. It does not
introduce an event declaration, a write, or another journal. The receipt's ordered execution trace
is the semantic authority; the occurrence retains references to that receipt and its movement entry.

```csharp
// Obtain this through the trusted entity repository, including its authorization boundary.
EntityTransitionOperationReceipt receipt = committed.Receipt!;
var tender = new TenderEntityModel();
foreach (var occurrence in EntityMachineOccurrence.FromReceipt(receipt))
{
    if (occurrence.Matches(tender.Machine.SendPending))
    {
        // Inspect the exact committed snapshot, not the entity's later current state.
        var loadId = occurrence.Receipt.Entity.Entity.Observation
            .GetField(nameof(TenderState.LoadId)).GetRequiredString();
        // The next policy layer will project a request addressed to this load.
    }
}
```

This is observation code, not the proposed policy-authoring DSL. `Matches` uses the typed handle's
exact machine reference and edge identity, so a different revision with the same edge name does not
match. The full source identity is `(occurrence.Source, occurrence.Movement.Node)`. `Source` includes
the original process operation, authority/tenant, entity subject, and request fingerprint. Duplicate
receipt delivery retains that identity and the original commit time. Neither time nor current entity
state determines identity.

Only successful Applied/NoChange receipts produce occurrences. Identity-preserving operations have
no movement entry. Named self-loops do have an occurrence, including when no fields change. A trace
may contain a tentative movement followed by domain rejection: the final decision suppresses all
such occurrences. A lost concurrency fence has no committed receipt. Source admission failure is
not a committed movement.

The API accepts a trusted receipt, not arbitrary decision evidence. Receipt objects are not
cryptographic attestations: transport consumers still need authorized resolution and validation by
the owning repository. The receipt locator is not an authorization grant. Projection does not grant
publication authority, durably enqueue a reaction, guarantee delivery, or bypass target authorization.
This interpretation currently applies to the existing atomic entity operation receipt protocol;
other commit protocols need their own conformance evidence before providing equivalent occurrences.

Projection scans the retained trace once and allocates one wrapper per movement plus collection
storage. It performs no serialization, storage reads, or writes. The wrappers retain the receipt;
persist the existing receipt locator and node identity for long-lived references rather than retaining
whole receipts in an unbounded in-memory subscription.

## Implemented: declarative policy and Process interpretation

`TenderReactionModel` now declares pending acceptance and receipt confirmation policies:

```csharp
PendingAcceptance = MachineReaction.On<TenderState>(Tender.Entity, Tender.Machine.SendPending,
        id: "tender-to-load.pending", revision: "1")
    .Request(MarkPending,
        subject: tender => tender.LoadId,
        input: tender => new AcknowledgeLoad(false));
```

The expressions lower to portable Process IR. Target guards still decide admission. The existing
atomic entity adapter provides exact receipt replay, and the existing Process runtime owns execution
and checkpointing. Canonical start causation retains the exact policy and source occurrence through
recovery. Inspection distinguishes Pending, Completed, Rejected, Failed and Indeterminate from
retained receipts or explicitly scoped host failure evidence.

See [the policy contract and runtime boundary](../../../src/Cohesive.Processes/Machines/README.md)
and [the executable consumer](../../../eng/examples/tender-acceptance/TenderReactionModel.cs).
Tests cover deterministic lowering, persistence, source/target affinity, duplicate delivery,
guard rejection, explicit failure inspection, and recovery after a lost target-commit acknowledgement.

## Remaining realization work

The current profile supports one target invocation, one initial Process attempt and one authority
scope. Hosts own feed observation, start admission and scheduling. Failure inspection accepts the
existing portable failure evidence, but the native Transition-operation path does not automatically
persist transport exceptions as a durable reaction attempt history. That integration remains explicit
follow-up work; no separate reaction journal or retry scheduler is introduced.

Cosmos change-feed observation, HTTP dispatch and actor runtimes remain later adapters. Each must
declare observation, deduplication, ordering, durability and reconciliation guarantees. Delivery
acceptance is Pending work and must never substitute for a target commit receipt. Cross-authority
reactions, policy evolution over historical sources, and Process restart-as-new-attempt require
explicit semantics before support is claimed.
