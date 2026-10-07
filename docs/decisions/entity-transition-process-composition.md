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

## Remaining composition work

1. **CRUD lowering.** The API create initializer currently commits without a canonical
   decision, and its repository upsert can overwrite when no concurrency token is supplied.
   Define create-if-absent versus replacement/upsert explicitly before lowering those actions
   into canonical transitions. Never advertise absence enforcement with an unconditional upsert.
2. **Process example.** Compose availability query, inventory reservation, and order transition
   in one canonical Process, with an in-memory interpretation first. Durable execution must
   preserve the same business graph while supplying retry/recovery guarantees. PostgreSQL's
   current entity repository does not implement the process transition receipt repository;
   that missing atomic state-plus-receipt capability needs an adapter implementation before
   claiming reliable process transition execution on that backend.
3. **History and reconstitution.** Choose event authority independently of delivery. An outbox
   supplies atomic delivery intent, not a complete authoritative history. Event-sourced state
   needs ordered, versioned state actions, conditional append, and reconstruction equivalence
   for full replay, snapshot-plus-tail, and eagerly maintained state. The current decision patch
   projector is not that stream contract. Extend the responsible storage and transition contracts
   rather than adding an example-local event store or copying persistence types.
4. **Read models and guarantees.** Attach query projections independently, then declare ordering,
   audit retention, orchestration, and recovery requirements. Actor placement or distributed
   locking must satisfy those requirements; neither name alone establishes the guarantees.

Tests exercise pure decision preparation and existing API, service, and process consumers.
They do not establish event replay, distributed atomicity, or durable PostgreSQL execution.

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
