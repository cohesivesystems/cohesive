# Cosmos Process Transition Receipts

## Decision

`CosmosEntityOutboxRepository` is the Cosmos realization of Cohesive's existing
`IEntityTransitionOperationRepository` contract. It atomically commits entity state and one exact Process
Transition operation receipt in a Cosmos transactional batch. Creation Transitions also commit a subject-scoped
index that points to the exact occurrence receipt.

The repository uses two concurrency values with distinct ownership:

- Cosmos `_etag` is provider-owned compare-and-swap evidence used only at the adapter boundary.
- `entityConcurrencyToken` is an opaque repository-owned token retained in the entity document and copied into the
  receipt transaction. It is the `EntitySnapshot.ConcurrencyToken` exposed to Cohesive callers.

Legacy entity documents without `entityConcurrencyToken` continue to expose `_etag`. Their next successful write
creates an application token. A compare-and-swap first resolves the authoritative document, verifies the caller's
opaque token, and then fences the transactional write with the current `_etag`.

## Invariants

- Entity state, exact occurrence receipt, and any creation index share one physical partition and one Cosmos
  transactional batch.
- A retained occurrence replays only when its canonical request or commit fingerprint matches.
- A retained creation index replays only the same authority-scoped creation intent.
- Receipt restoration recomputes and verifies request, intent, commit, subject, and entity-version evidence.
- Receipt lookup requires exact point-read partition placement. Missing placement fails closed as insufficient
  capability.
- Canonical envelopes retained in the receipt remain Process-outbox handoff evidence; the Cosmos entity outbox does
  not publish them independently.

## Alternatives rejected

Using the in-memory repository for Ari's live profile would avoid the missing capability but would not validate the
production Cosmos boundary. Committing the entity first and patching a receipt with its `_etag` afterward leaves a
crash window without exact replay evidence. Treating the receipt document's `_etag` as the entity concurrency token
would conflate two physical authorities and violate the receipt snapshot invariant.

## Validation

The emulator regression proves first commit, repository restart, exact receipt restoration, canonical outcome and
envelope recovery, entity-state equality, and equality of committed, replayed, and current entity concurrency
tokens. Existing repository contract tests cover legacy `_etag` fallback and discriminator validation.
