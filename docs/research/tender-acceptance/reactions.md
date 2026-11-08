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

## Next: one declarative policy, existing execution machinery

The next implementation should lower typed policy source into a portable declaration containing:

- Exact source machine and movement reference.
- Typed projection from the committed source snapshot into target identity and action input.
- Exact target action/transition contract. The target decides which movement, if any, is enabled.
- Stable policy identity, revision, fingerprint and source attribution.
- Explicit completion evidence requirement: target commit, not delivery acceptance.

Do not retain executable callbacks or closures in that declaration. Lower expression source through
the existing Relations expression language, using the same contract context as the participant
entities and transitions. Validate projections and references at registration. Policy revision and
source occurrence together identify a reaction instance; changed policy revisions require explicit
replay/evolution policy rather than silently repeating past reactions.

For the tender episode, pending acceptance requests the related load's pending-acknowledgement
action. Confirmation requests its confirm-acknowledgement action. Correlation comes from the exact
committed tender's LoadId. Neither request declares that the load already changed. The complete load
lifecycle is still unextracted, so this must remain an explicitly bounded consumer until that model
exists. Customer-ordered stops belong to the load; no original-trip proxy is introduced.

Use a Process invocation and its existing durable operation/receipt facilities for the first
interpretation. A second scheduler, journal, retry loop or independently mutable reaction status
would duplicate authority. A reaction-specific inspection view should derive the following meanings:

| View | Required underlying evidence |
|---|---|
| Pending | Durable reaction intent exists; target commitment/rejection has not been established. Dispatch acceptance alone stays pending. |
| Completed | Exact target receipt proves successful completion under the policy's requirement. |
| Rejected | Exact target admission/domain result rejects the action; source commit remains valid. |
| Failed | Terminal failure with explicit non-commit evidence and diagnostics. |
| Indeterminate | Failure evidence says target effects may have committed; reconciliation is required. |

The existing `DurableOperationStatus`, `DurableOperationFailurePhase`, and
`DurableOperationEffectEvidence` already distinguish dispatch, acknowledgement and ambiguous
effects. Preserve their attempt history and evidence instead of maintaining a parallel enum-driven
execution machine. Acknowledgement by itself is not enough: inspect the policy-required target
receipt/result before presenting Completed. Reconciliation must preserve exact operation identity;
never turn receipt absence into proof of non-commit or blindly execute a new request.

## Acceptance tests for that next layer

1. Lower equivalent typed policies deterministically and round-trip their canonical declaration.
2. Reject wrong source revision, incompatible projection contracts and unresolved target contracts.
3. Duplicate source delivery produces one reaction identity and one durable start intent.
4. Delayed or merely accepted dispatch remains Pending.
5. Target guard rejection reports Rejected without undoing the source movement.
6. Target commit produces Completed only with exact matching receipt evidence.
7. Timeout after possible target commit produces Indeterminate; reconciliation does not resend blindly.
8. Recovery retains policy revision, source causation, target identity and attempt diagnostics.
9. Compare the reference interpretation and existing Process-backed interpretation on the same traces.

Cosmos change-feed observation, HTTP dispatch and actor runtimes remain later adapters. Each must
declare its observation, deduplication, ordering, durability and reconciliation guarantees; none may
silently replace the commit-evidence requirement with transport acknowledgement.
