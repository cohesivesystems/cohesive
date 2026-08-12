# Cohesive

Core primitives, shape metadata, domain quantities, code generation abstractions, and prelude helpers shared by the Cohesive package family.

## Install

```bash
dotnet add package Cohesive
```

## Use When

- You need the common shape model used by Cohesive blocks and adapters.
- You want shared domain primitives such as typed quantities, codes, identifiers, paths, and observation values.
- You are building a new Cohesive block or adapter and need the base semantic contracts.

## Example

```csharp
using Cohesive.Model;

[ShapeDefinition("shape.shipment", ShapeRoles.Transport)]
public sealed record Shipment(string Id, IReadOnlyList<Stop> Stops);

[ShapeType("type.stop")]
public sealed record Stop(string City, string State);

var graph = new ClrShapeGraphBuilder()
    .AddShape<Shipment>()
    .Build(new("shipping"));
```

## Package Role

`Cohesive` is the foundation package. Higher-level blocks such as `Cohesive.Relations`, `Cohesive.Transitions`, `Cohesive.Processes`, `Cohesive.Presentation`, and `Cohesive.Api` depend on it.

## Canonical execution interactions

`Cohesive.Execution` separates reusable interaction contracts from their runtime emissions.
`InteractionContractDefinition` is the persisted semantic authority, carried by the shared
`ExecutionDefinitionDocument`; `InteractionEnvelope` is a versioned runtime value that references one exact,
typed contract revision and carries its stable emission identity, origin, correlation and causation, authority
scope, idempotency basis, ordering, delivery demands, and provenance. Payloads and terminal values are
materialized `PortableValue` instances rather than CLR or transport values, and their exact referenced contracts
carry the explicit schema revisions.

The contract and envelope algebras are closed over four kinds:

- A domain event records a fact and creates no emitter-side response obligation.
- A Request creates a typed terminal response obligation and names where that response is consumed.
- A Signal is an addressed one-way input with no response obligation.
- A Reply identifies the Request emission it discharges and carries one declared result, failure, timeout, or
  cancellation outcome.

Request contracts make terminal variants, timeout and cancellation support, retry and ambiguous-outcome
semantics, late/stale/duplicate-result disposition, retention, and unresolved-result handling explicit. A Request
envelope carries an exact semantic address for either a durable Process token or a Transition continuation; those
alternatives are distinct types rather than transport addresses or loosely interpreted strings. The current
interaction reader validates contract, payload, and Reply-outcome links. Definition/node resolution for Process
tokens, Transition origins, and Transition continuation addresses belongs to the Process/Transition compiler and
durable result-admission work, where the referenced definitions and live continuation state are available.

Persistence events are not interaction kinds. Change-feed records, checkpoint history, and outbox storage records
describe reconstruction, audit, and delivery mechanisms; an adapter must not treat them as domain events unless a
canonical domain-event contract and envelope explicitly provide that meaning.

`InteractionContractDocuments`, `InteractionContractCatalog`, `InteractionEnvelopeValidator`, and
`InteractionEnvelopeJsonSerializer` enforce exact schema, revision, fingerprint, discriminator, payload, and Reply
outcome links with structured diagnostics; envelope admission requires the linked catalog rather than trusting a
wire discriminator in isolation.

For C#-authored domain events, `InteractionContractAuthoring.CreateDomainEvent<TPayload>` derives the portable
payload contract from `TPayload` and returns one typed handle containing the canonical document, exact event
reference, and validation diagnostics. The durable event identity, contract revision, payload-schema revision, and
provenance remain explicit inputs: CLR names are authoring details and never become durable authority by convention.

## Canonical durable Request execution

The durable-operation reference protocol interprets an ARI-160 `RequestEnvelope` without creating a second
operation identity: the Request `EmissionId` remains the logical operation identity, while authority scope, exact
Request contract, and `InteractionIdempotencyKey` form its target-deduplication key. `DurableRequestBinding`
refines the exact Request contract with a bounded attempt count, claim-lease duration, optional concrete timeout,
explicit idempotency evidence, one exact Reply contract for every terminal outcome, and exact definition/node
targets required for reconciliation or escalation. The binding does not repeat the authored response obligation
and contains no handler, repository, transaction, clock, or provider object.

`DurableOperationState` is portable semantic state for one logical Request. It retains monotonically fenced
claims, ordered immutable attempt snapshots with append-only attempt allocation, fenced reconciliation evidence,
explicit pre-call, in-call, post-call/pre-commit, and post-commit/pre-acknowledgement failure evidence, the single
durable acknowledgement, and the later target admission as distinct facts. Acknowledgements produced by
reconciliation or escalation retain the exact recovery identity that won. Acknowledging a typed outcome
prevents another physical call; it does not itself advance a Process token or invoke a Transition continuation.
Admission instead consumes target-owner evidence and applies the Request's late, stale, or duplicate-result policy,
including replay of an already durable disposition without advancing twice.

`IDurableOperationAdapter` is the impure boundary. It receives an immutable fenced invocation carrying the same
Request, correlation, and idempotency identities across retry, exposes target idempotency and reconciliation
capabilities for exact Request contracts, and returns a typed terminal outcome or explicit failure evidence.
`IDurableOperationBatchAdapter` applies the same boundary to a physical batch while returning exactly one
emission/attempt/fence-keyed observation per item. Neither boundary receives aggregate state, an entity repository,
a Transition callback, or a Process runtime service, so adapter registration cannot become semantic authority and
a handler cannot mutate authoritative entity state through this contract.

`DurableOperationReferenceExecutor` is a deterministic state transformer, not a production dispatcher or durable
store. It defines fenced claim and renewal, dispatch, bounded retry, semantic timeout and cancellation, explicit
reconciliation and escalation intents, acknowledgement, and target admission. It makes the EK-06 crash cuts
explicit: a Request durable before dispatch remains pending; a dispatched call without acknowledgement becomes
ambiguous and may be retried only with declared idempotency evidence or after reconciliation; and an acknowledgement
replayed before target admission skips external execution and reuses the target's durable disposition. Late,
duplicate, conflicting, and stale evidence remain observable. `Cohesive.Storage.Processes.ProcessDurableRuntime`
now persists and drives those cuts over the atomic Process aggregate: it commits the Request with origin progress,
records dispatch before adapter I/O, reloads and fences returned evidence, and atomically couples final operation
disposition with Reply inbox admission. The core executor remains repository-free and reusable; production storage
and operation adapters must separately prove the capabilities they claim.

Raw Process signal payloads and legacy Process retry/dead-letter paths remain migration surfaces. They may be
adapted to canonical Requests and operation observations, but they are not a parallel semantic authority and are
not silently treated as the new durable protocol. The earlier CLR `EffectRequest` and delegate-bound continuation
surfaces have been removed rather than adapted into the canonical protocol. Entity outboxes likewise retain exact
canonical Domain Event and Request envelopes; an adapter-local message DTO is not a second semantic contract.

## Canonical Process lifecycle control

`Cohesive.Execution` defines a closed, protocol-neutral Process control family: `Inspect`, `Signal`, `Pause`,
`Continue`, `RestartAttempt`, `Cancel`, and `Terminate`. Mutating commands carry stable command and idempotency
identity, attributable authorization evidence, provenance, and an expectation for the exact Process attempt and
semantic control revision. The revision is the optimistic lifecycle fence, not an external-operation lease fence
or a physical Storage record version. Accepted mutating and Signal-admission commands retain durable replay
receipts, so exact replay returns the original result without duplicating a Signal, allocating another attempt, or
emitting another external intent; `Inspect` remains a read-only observation and creates no receipt;
conflicting identity reuse and stale attempt or revision expectations remain explicit diagnostics.

`ProcessControlState` retains lifecycle mode, ordered attempt lineage, finite activation position, explicit safe
points, and authoritative command receipts from which canonical Signal admissions are projected. State admission
replays those receipts and observations through the same pure lifecycle reducer used by
`ProcessControlReferenceExecutor`, rejecting histories that could not have been produced by the live semantics.
Pause, RestartAttempt, and cooperative Cancel defer while an activation is in flight
and take effect only at an invariant-preserving safe point. Pause and Continue preserve the logical Process
instance, current attempt, and all attempt affinities. RestartAttempt records abandonment and cleanup for the old
attempt, then starts one caller-selected stable replacement under the same Process instance without copying old
affinities. Cancel is a cooperative terminal outcome; Terminate is an immediate, irreversible forced stop with an
explicit cleanup obligation. A pending cooperative safe-point action is not silently replaced by another; only
Terminate may preempt it immediately.

A Signal command carries an already-canonical `SignalEnvelope`. The reference interpreter validates its exact
contract, authority, and current-attempt target, admits it once by emission and scoped idempotency identity, and
buffers it while the Process is paused or pausing. It returns a first-time admission intent rather than treating an
in-memory collection as a durable inbox or arbitration mechanism.

`ProcessControlJsonSerializer` provides the strict canonical command, state, and decision wire boundary. Reads
require the exact interaction catalog so Signals are contract-linked and named reason or affinity values resolve
through its retained shape graph. `ProcessControlDecision` is itself versioned and portable; first-time intents are
bound to their exact latest receipt or observation cut, while replay results never emit the intent again.

`ProcessAttemptAffinity` is a generic write-once hook for attempt-bound resources. An index-sync interpretation can
bind a candidate generation through a stable Process semantic slot: pause/continue retain that binding, while a
new attempt produced by RestartAttempt begins unbound and therefore requires a fresh generation. Cohesive.Storage,
not the control protocol, owns physical generation allocation, persistence, cleanup, exclusion, promotion, and
backend swap. The reference executor itself remains persistence-neutral; the Storage-owned durable Process runtime
now composes it with atomic checkpoint/CAS persistence, inbox commits, worker fencing, and clean attempt restart.

## Expression IR and Analysis

`Expr` is the portable, non-generic expression IR shared by Cohesive languages. An expression
describes a computation, but it does not persist the CLR object, query row, transition state, or
other runtime context in which that computation happens.

An expression site supplies that missing semantic boundary. It identifies where an expression is
used, declares the bindings, parameters, current item, and ambient capabilities available there,
and states the expected result contract. Shared expression analysis then derives the bindings,
field paths, parameters, current-item access, functions, operators, and ambient capabilities the
expression requires. The immutable result can be consumed by validators, backend compilers,
lineage tools, input-contract analysis, and diagnostics without annotating or changing the
canonical expression.

Available scope and derived requirements intentionally remain separate:

- A scope states what a particular site provides.
- Requirements state what the expression reads or otherwise needs.
- A semantics catalog describes functions and operators independently of an interpreter.
- A capability profile states which operations a language surface allows or a selected interpreter can realize.
- Runtime evaluation contexts remain private to their interpreters.

A constrained coarse category, such as Boolean or Numeric, requires a present, non-null value unless
the expectation also supplies a value contract that explicitly permits absence or null. This keeps
operator and function null semantics deterministic instead of inheriting interpreter-specific coercion.
Conversely, bare `TypeRef` metadata on a call or aggregate declares its portable type but does not
invent presence or nullability guarantees; a semantics-catalog result contract may provide stronger
guarantees when the operation defines them.

Within a function argument declared as current-item scoped, an unbound field path rooted at
`item` reads that current item: `item.Name` addresses its `Name` field, while `CurrentItemExpr`
represents the whole item. Parameter scope separately records whether an invocation may omit a
parameter and the guarantees of the value that is evaluated after defaults are applied.
Each derived field requirement identifies its root as a named binding, the scoped current item,
or unresolved, so acquisition and lineage consumers do not have to infer root semantics from a
nullable binding identifier.

The same `Expr` may therefore be analyzed under several scopes and capability profiles. This is
why the canonical IR remains non-generic: C#, TypeScript, SQL, document-query, graph-query, and
in-memory interpreters can share one persisted expression while host-language authoring helpers
lower immediately to that representation.
