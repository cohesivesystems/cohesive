# Tender acceptance extraction dossier

Status: research proposal, 2026-11-07. This is a bounded extraction from an application archive,
not a production migration, a complete tender model, or a proof of behavioral equivalence.

## Artifacts and authority

- [Evidence manifest](evidence.json): stable claim IDs, source-relative locations, full-file and excerpt SHA-256 hashes.
- [Proposed fluent source](../../../eng/examples/tender-acceptance/TenderAcceptanceMachines.cs): four small machines using existing Cohesive authoring and lowering.
- [Coordination extraction and trace host](../../../eng/examples/tender-acceptance/AcceptanceCoordination.cs): executable, explicitly bounded observations of effect ordering.
- [Coordination and host design](coordination.md): participant relationships, effect boundaries, failure and recovery obligations.
- [Characterization tests](../../../src/Cohesive.Tests/Machines/Distillation/TenderAcceptanceCharacterizationTests.cs): source-derived trace expectations, proposed admission, partial commits, and coherence counterexample.

Source root inspected: `/Users/eulerfx/code/Archive/backend/AlvysApi`. No Git revision was available at the supplied Archive root; the manifest pins the inspected bytes instead. Application source and infrastructure dependencies are not copied into Cohesive. Evidence IDs refer to observations, P IDs below to proposals, and Q IDs to unresolved questions. The authored C# is authoritative for the proposal; canonical Machine IR and fingerprints are derived. The evidence manifest records why the source was proposed; it is not an executable rule catalog.

No intent document was supplied. Consequently this dossier recovers implementation behavior and comments;
it does not certify business intent. Comments and code disagree in one important place: the acceptance
comment says the load is created after acknowledgement, but E02 creates and persists a linked load before
sending acceptance. The executable ordering wins as evidence of current behavior, while desired ordering
remains a product decision.

## Scope

Trace fixtures cover an ordinary initially unaccepted tender, a newly created valid load and original trip,
clean acknowledgement fields, no changes/cancellations/bids, and visibility publications disabled. They cover
legacy and event-sourced acceptance with automatic or deferred acknowledgement, plus the later receipt path
with `SendAckFor=Accept`. This is an acceptance episode, not the whole tender lifetime. Final states in the
proposed machines mean exit from this episode, not that accepted tenders can never change or cancel.

Existing-load reuse/linking, orphan cleanup and receipt correlation are inspected and documented but are
not executable fixtures yet. Changes, bids, rejection workflows, cancellation, expiration execution,
permissions, load dispatch and trip lifecycle transitions are outside the executable slice.

## What the code actually says

| Claim | Observation | Consequence for extraction |
|---|---|---|
| E01 | Orphan cleanup precedes the Cancelled check. Already-linked rejection depends on status, source and load number. Other statuses are not universally rejected here. | Do not infer a universal `New -> Accepted` admission rule from the enum. Admission facts must be resolved by a host; cleanup itself is an effect. |
| E02 | New load creation, tender linkage and tender persistence precede outbound send. | Send failure can leave a linked load. `Unlinked` is not equivalent to `Unaccepted`. |
| E03 | ES persists accepted/pending tender before marking the load; legacy marks the load first, then persists tender. Publications can fail after these writes. | Same successful final observations do not imply the same partial-failure behavior or atomicity. |
| E04 | Receipt result and errors are logged; the received result type does not choose the pending-action handler. Load acknowledgement occurs before trip notification and tender confirmation. | Negative-result semantics and replay recovery require explicit decisions. |
| E05/E09/E10 | Pending and confirmed both use `Status=Accepted`. Pending uses `Unacknowledged` and `SendAckFor`; deferred acceptance also assigns the load. | Formal acceptance state must include protocol facts, not only the status enum. |
| E06 | `LinkToLoad` also changes tender `id` to the load ID. | Linkage is currently an identity migration as well as a relationship update. Preserve provenance and aliases in any migration. |
| E07 | Reusing an existing load takes a different path: reset/link, send, soft-delete old tender, persist/create linked tender, update load. | Do not run that path through the new-load fixture and claim equivalence. |
| E08/E11 | `MarkAccepted` changes `IsAcknowledged`, `MatchingTenderId`, and optionally source; it does not advance `Load.Status` or trip status. | This slice needs a load acknowledgement model, not an invented load/trip dispatch transition. |
| E12 | Repository receipt matching requires sender/receiver and control number; it can match historical control numbers. | Correlation already exists. Current-attempt and duplicate semantics still need investigation. |
| E13 | Public acceptance takes a tenant/source/shipment lock. | This does not establish that all entry points or receipt handling share that lock or a transaction. |

## Candidate state models

**Tender acceptance**: `unaccepted -> accepted-awaiting-ack -> accepted-confirmed`, with an automatic-ack
shortcut. The proposed send-result action requires linked-load evidence and excludes cancellation and a
known already-accepted link. These facts summarize admission checks; they do not reimplement repository
lookup or assert that the checks can safely be deferred until after a send.

**Linkage**: `unlinked -> linked`. Identity alias updates and stop matching are accompanying domain changes,
not additional statuses. Unlink/relink is deliberately excluded from this episode.

**Load acknowledgement**: `not-acknowledged -> acknowledged`, with a pending-accept self-transition.
This is independent of load/trip lifecycle status. It represents a projection of the load, not a new stored
status field requirement.

**Coordination progress**: `preparing -> linked -> awaiting-receipt/confirming -> complete`;
uncertain results lead to `reconciliation-required`. These are proposed durable progress states, distinct
from business state. A request being dispatched is not evidence that its effect committed.

## Intent decisions that must remain visible

- **P01 — positive receipts:** the proposal requires positive, correlated receipt evidence. This strengthens
  the observed handler, which logs result type without branching on it. It must not be presented as an
  extracted existing guarantee. Automatic acknowledgement remains an explicit adapter result.
- **P02 — recovery:** the proposed host records partial progress and reconciles uncertain effects instead
  of treating a failed request as proof that nothing happened. No automatic resend is modeled.
- **P03 — identity:** prefer stable tender identity with an explicit load relation and legacy aliases.
  The archive rewrites identity; applying this proposal requires a migration, not merely a new binding.
- **Q01:** Which non-New statuses may legitimately begin acceptance? Which callers add checks?
- **Q02:** Does a negative 997 reject transport syntax, business acceptance, or both for each provider?
  Should it retry, require intervention, or leave business acceptance pending?
- **Q03:** How should a stale/historical control-number receipt affect a newer pending attempt?
- **Q04:** If load acknowledgement commits and notification/tender persistence fails, how should replay
  finish tender confirmation? With no updates/cancellation, the inspected already-acknowledged branch only
  persists the acknowledgement log; it does not call `SetAccepted`.
- **Q05:** Are `Accept`/`ApplyAccepted` intentionally allowed to retain older `SendAckFor` or acknowledgement
  deadline values, whereas `SetAccepted` explicitly clears them? Clean-field fixtures do not decide this.
- **Q06:** Existing-load linking passes `isAutoAck` to `LoadService.LinkToLoad`, but that method does not
  call `MarkAccepted`. Determine the intended acknowledgement state for that branch before formalizing it.

## Verification and limits

Run from the repository root:

```sh
dotnet test src/Cohesive.Tests --filter FullyQualifiedName~TenderAcceptanceCharacterizationTests
python3 docs/research/tender-acceptance/verify_evidence.py /Users/eulerfx/code/Archive/backend/AlvysApi
```

The .NET suite compiles the exact example files via a linked Compile item. It needs neither the archive nor
its infrastructure. Golden trace expectations were manually derived from E02–E04. The in-memory trace host
models independently completed effects and injected failures **before** an effect; it does not execute the
original C# or represent a timeout after an unknown remote commit. Tests are source-derived characterizations
and proposal conformance checks, not differential tests against the application. Evidence verification is
optional and checks drift; CI does not require a developer's private archive.

The machines test deterministic lowering, round-trip fingerprints, acceptance branches, proposed receipt
guards, reconciliation routing and a counterexample to independent-participant coherence. A live adapter,
actual Process IR lowering, automatic extractor, durable receipts, retry policies, and application migration
are intentionally not implemented. The next validation step is to instrument the archived application in
its own test environment and compare actual boundary traces with these fixtures before replacing behavior.
