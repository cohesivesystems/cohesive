# Entity transitions, processes, and state authority

## Intent

A repository is a persistence primitive. An entity API operation adds admission, a decision,
and a conditional commit; CRUD through that boundary is an implied transition even when
it has no explicitly named event. An explicit Transition is the single-subject decision
unit. Composing a query, transitions, or effects belongs to a Process. A Process can run
in memory; durability is a separate execution guarantee.

This model does not imply that every CRUD call needs a Process document or a scheduler.
Atomicity comes from the selected commit capability, not from naming an operation a transition.

## Implemented boundary

Canonical Transition IR and `TransitionDecision` remain authoritative.
`TransitionStateProjector.ApplyToEntity` now owns candidate-state preparation for the
ASP.NET transition binding, `ServiceRuntime`, and `EntityTransitionProcessOperationAdapter`.
It verifies patch before-values, validates entity shape, and applies one version policy:
creation starts at zero, `Applied` advances the version, and no-change/rejection preserves it.
Creation requires retained initial-observation evidence and cannot be applied to an existing
subject. The existing process path also validates creation entity rules.

For example, approving an eligible entity at version 7 produces version 8 through the
shared preparation path. An already-approved entity with a no-change decision stays at 7.
Previously each consumer assembled its own candidate, and the API advanced the version
for every commit-required decision, including a no-change emission-only commit.

This method does not authorize or commit. API/service boundaries retain authorization and
optimistic concurrency. Process execution retains conditional subject creation, operation
receipts, retry identity, and process-owned emissions. Those are different ownership contracts
and must not be erased by a universal execution wrapper. The retired declarative entity
runtime is not reinstated.

## State authority composition

The existing candidate projector, conditional creation, process operation receipt protocol and
interaction-envelope outbox remain the owning seams. PostgreSQL can now attach explicit state
history and independent direct-transition outbox storage to those same transactions.

`EntityReconstitution` applies retained canonical replacement actions. This is deliberately a
small first state-action contract, rather than inventing domain events for every existing CRUD
or transition operation. Complete replacement payloads cost more storage than domain-specific
or delta events, but need no parallel reducer catalog and never reevaluate decisions or effects.
Custom event reducers and stream evolution are future extensions, not claimed capabilities.

Current-state authority reads mapped rows and treats history as audit. Event authority reads
ordered actions and treats mapped rows as synchronous query projections. The same domain and
API use either policy. Full replay and exact checkpoint-plus-tail acquisition share the pure
reconstitution contract. A checkpoint is an indexed retained replacement action; it is not an
unverified copy of the current row. Append fences use stream position independently of semantic
version and native row `xmin`. An event-owned update without its expected stream fence fails.

The separate `Cohesive.Adapters.Marten` package owns the event realization. Marten provides
stream storage and version checks; the existing PostgreSQL repository owns the transaction,
relational mapping, receipts, outbox and explicit projection rebuilding. Marten sessions borrow
that transaction without owning commit. Custom history tables from the initial proposal were
removed after evaluating Marten and Eventuous; Marten supplies the PostgreSQL event infrastructure
and a future native projection path without introducing a parallel domain aggregate model.
The shared `EntityTransitionCommitProtocol` still owns receipt retry/identity semantics. Direct
transition envelopes use the existing `EntityOutboxCommit` contract and publication authority;
process receipt emissions do not enter that outbox. No generic transaction wrapper or example-local
stream coordinator was added. Native direct SQL is an explicit bypass and cannot inherit these
guarantees. Existing state-only rows and histories require explicit migration before changing authority.

Tests prove the same order HTTP transition under both policies, reconstruction after removing a
derived row, stale/concurrent writers, bounded acquisition, exact retry and atomic rollback of
state/history when receipt or outbox persistence fails. They do not establish event-driven read
models, external delivery, distributed atomicity or crash-durable example process execution.
See the [state authority walkthrough](../../eng/examples/aspire-first/STATE-AUTHORITY.md).

## Remaining composition work

1. **Event contracts and evolution.** Add domain-specific state actions/reducers, tombstones,
   explicit schema evolution and retention where needed. Preserve canonical contract provenance.
2. **Read models.** Add asynchronous projection checkpoints, idempotent acquisition, catch-up and
   rebuild independently of authoritative entity reconstitution.
3. **Durable processes.** Preserve the existing business graph while attaching durable execution,
   timers and recovery. Request/ack/poll response behavior remains independent of durability.
4. **Infrastructure requirements.** Expose the newly supported history/rebuild guarantees through
   capability evidence; do not infer cross-store atomicity or an actor/lock guarantee from a label.

## Preparation diagnostics and work lifetime

Known preparation failures use `TransitionStatePreparationException` (a `PreparationException`)
with a stable `Code` and `Location`. The process adapter retains these verbatim, including
`/decision/evidence/initialObservation` for missing or invalid creation evidence. It does not catch
arbitrary `InvalidOperationException` failures. Entity-validation causes are retained in `InnerException`.
Null/empty programmer arguments retain standard argument exceptions.

Preparation reads immutable snapshot fields directly, checks shape identity, and constructs and
validates only the resulting candidate. It does not rebuild the current `EntityState` already
constructed by API/service callers or introduce that construction into the process path. Patch
before-value checks remain mandatory. Diagnostic path strings are allocated only on failure.
This removes the redundant construction by inspection; no throughput benchmark claim is made.

Unreleased diagnostic change: process candidate-validation failures now expose the shared
`transition.state.*` codes instead of adapter-specific initialization/not-committable codes.
The lower-level `Apply` also uses typed patch failures; callers should catch the typed exception
or `PreparationException`, rather than rely on exact built-in exception types.

API preparation failures return sanitized 500 Problem Details with the shared code, location,
and trace ID. Service invocation returns `InfrastructureError` with the same code and location.
These are failed preparation guarantees, not automatically caller validation errors or retryable
conflicts. Neither boundary commits or leaks validation messages. The process path retains its
structured internal diagnostic. Unrelated exceptions continue to propagate.

`TransitionStatePreparationException.SafeMessage` owns the sanitized description used by service
results and HTTP projections. `AddCohesiveExceptionHandling` also projects the same failure through
the native ASP.NET pipeline, using the same Problem Details factory as direct endpoint bindings.
Direct bindings remain self-contained when middleware is not registered. The two boundary catches
remain intentional because they produce different result contracts, not separate error policies.
