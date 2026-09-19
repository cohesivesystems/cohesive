# Durable acquisition extraction

Status: first contract slice; physical realization and application adoption remain pending.

## Current boundary

Bounded ingestion already lowers to canonical Processes. Source progress already has date-range and opaque
cursor positions, immutable advancement documents, receipt-before-revision CAS, and unknown outcomes.
The remaining extraction is retained acquisition/preparation evidence and the durable execution binding.
A typed Process graph alone does not replace a local runner's journal or qualify external-effect recovery.

ITO-27's original all-atomic protocol must be read as two explicit supported profiles:

- Atomic: acquire/prepare → commit sink effects and domain coverage → settle.
- Separate ledger: acquire/prepare → confirm sink commit → commit source ledger and its receipt → settle.

A ledger conflict in the second profile preserves committed sink effects. It never implies rollback,
automatic rebase or a new publication identity. A ledger is not a Process checkpoint; a Process checkpoint
tracks pending execution, while a ledger tracks independently advancing source-to-destination progress.
Neither replaces destination coverage and immutable publication receipts.

## Delivery and acceptance

1. **Canonical evidence contracts (this slice).** Freeze scope, identities, exact definitions, selection,
   original ledger expectation, acquired content, and prepared publication. Verify bytes and exact lineage;
   construct existing ledger advancements from confirmed original sink receipts. Reuse existing execution
   documents and ledger position validation. Reference tests reopen every boundary, reject changed metadata,
   preserve both profiles, and replay an old acknowledgment after later progress.
2. **Physical retention and shared crash fixtures.** Bind immutable documents and content to native storage.
   Admission is write-once under operation/attempt/boundary; exact retries return original evidence and
   differing candidates conflict. Bind completeness, limits, retention horizons, concurrency and receipt
   visibility. Interrupt before/after every retained boundary, after acquisition before retention, and around
   commit. Absent evidence under ambiguous execution or weak visibility stays unknown. Prove exact existing
   bytes and metadata, including every manifest child. Do not silently replace a partially written journal.
3. **Ito EOD adoption.** Keep provider, source completeness, calendar, currency, normalization and sink guards
   in Ito. Keep an explicit legacy recovery path for existing journal versions; never infer a new expectation
   or migrate an in-flight operation silently. Run original and new paths against the same synthetic provider,
   normalizer and SQLite fixtures. Compare committed bytes, original receipts, conflict attribution and progress.
4. **Durable Process binding.** Use existing Processes continuation/Request semantics and Storage durable
   checkpoints/inbox/outbox facilities. Avoid introducing an Integrations-specific dispatcher or workflow
   state machine. Pin the full definition closure. Retain effects before dispatch and reconcile replies before
   authorizing dependent steps. Prove both acquisition and publication ambiguity, cancellation, concurrent
   dispatch and lost acknowledgments. Retire the local replay bridge only after parity is established.
5. **Additional flows.** Adopt the qualified mechanism in other acquisition flows; scheduling and provider
   selection remain application policy. Do not multiply application-local durable coordinators first.

## Explicit authority and cost

Integrations owns acquisition/preparation evidence and composition requirements. Processes owns sequencing,
continuations and request/reply correlation. Storage and adapters own physical retention and commit evidence.
Domain publishers own sink invariants, completeness and original receipts. The existing ledger reducer owns
CAS and replay. This slice adds no competing publisher outcome enum, serializer or mutable checkpoint model.

The content digest is over raw bytes, deliberately distinct from the canonical document fingerprint.
Digest verification is linear in payload length with constant working memory; document size is independent
of payload size. This path runs once per bounded acquisition boundary, not per market tick. Payload limits,
streaming support and retention cleanup belong to a physical realization; no performance claim is made for
an unimplemented adapter. Expensive domain transforms must not rerun once a prepared input is retained.

The first tests are portable contract tests using the reference ledger. They do not claim native crash
recovery, source deduplication, durable retention, or that the trusted publisher actually committed.
