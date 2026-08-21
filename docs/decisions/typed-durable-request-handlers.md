---
kind: decision
status: implemented
authority: cohesive.execution.typed-durable-request-handlers
owners: [cohesive-core]
applies_to: [cohesive, cohesive-storage, cohesive-processes]
last_verified: 2026-08-20
supersedes: []
---

# Project typed Request handlers onto durable operation adapters

## Context

`IDurableOperationAdapter` is the portable impure boundary for canonical Requests. Its raw contract is intentionally
complete: an adapter receives the exact `RequestEnvelope`, binding, logical and physical identities, fence,
deduplication key, and deadline; returns canonical terminal-outcome or physical-failure evidence; and may reconcile
an ambiguous failed attempt. That surface is appropriate for multi-protocol adapters, target-native envelopes,
physical batching, streaming, and performance-sensitive integrations.

Ordinary single-protocol implementations should not repeat that machinery. Manually comparing the Request
reference, decoding `ObservationValue`, looking up Reply schemas, constructing a terminal outcome, and maintaining
`SupportedRequests` beside the protocol creates drift and makes the infrastructure boundary dominate domain code.
The typed protocol projection already owns the request type and complete outcome inventory.

## Decision

Keep the exact canonical Request contract as routing and schema authority, and project typed handlers onto the raw
adapter boundary:

- `IDurableRequestHandler<TRequest, TOutcome>` receives a materialized domain request and a typed attempt context.
  The context retains logical `EmissionId`, correlation and authority, physical `OperationAttemptId`, ordinal,
  fence, deduplication key, deadline, operation context, and cancellation.
- A handler selects an outcome with `context.Outcome(protocol.Outcomes.Accepted, payload)`. The protocol-owned case
  descriptor supplies exact outcome identity, kind, and payload contract. The selection wrapper is noncanonical,
  contains no independent discriminator, and is discarded after adapter projection.
- `IDurableRequestReconciliationHandler<TRequest, TOutcome>` receives the original domain request plus the exact
  failed attempt, recovery identity, target, and failure evidence. Its context constructs only the existing three
  reconciliation observations: confirmed protocol outcome, confirmed not executed, or unresolved.
- The generic adapter validates the exact protocol, derives `Capabilities.SupportedRequests` from its
  `RequestContractReference`, materializes the request, projects the selected outcome, and emits the existing raw
  durable observations. It does not change operation state, Reply mappings, fingerprints, or reconciliation state.
- Target idempotency and reconciliation remain explicit deployment evidence. The registration chain requires
  `WithIdempotency(...)` followed by either `WithReconciliation(...)` or `WithoutReconciliation()`; CLR types do not
  infer either guarantee.
- `DurableOperationAdapterCatalog` remains exact-reference routing authority. Dependency-injection registration
  contributes the projected adapter to that catalog and rejects an unrelated custom resolver rather than relying
  on service-registration order.
- Malformed request or outcome projections become structured portable failure or unresolved evidence with stable
  codes. Exceptions thrown by the domain handler itself remain physical attempt exceptions and are classified by
  the owning runtime; the projection does not reinterpret them as semantic Request outcomes.
- Raw `IDurableOperationAdapter` remains the explicit escape hatch for multi-protocol, batch, streaming,
  target-native, or specialized materialization behavior.

The default projection uses the same CLR-to-`ObservationValue` conventions as typed Request authoring and validates
the resulting `PortableValue` against the protocol contract. Optional `JsonSerializerOptions` affect CLR
materialization. A target that needs a materially different wire or allocation contract should retain the raw
adapter rather than silently changing canonical Request meaning.

## C# union readiness

The handler result is bound through `RequestProtocolCase` to the representation-neutral
`RequestProtocolOutcome`. No case runtime type, record constructor, discriminator enum, or inheritance layout is
persisted. A future native C# union projection may change the source syntax used to select a case while producing
the same canonical terminal outcome and durable evidence.

## Alternatives considered

### Route handlers by CLR request type

Rejected because two protocol revisions or unrelated protocols may share a CLR payload type. Exact definition,
revision, and fingerprint remain the only routing authority.

### Make the typed handler implement `IDurableOperationAdapter`

Rejected because it would retain the envelope decoding, capability ledger, and outcome encoding ceremony in every
ordinary handler instead of projecting those mechanics once.

### Infer reconciliation from an implemented interface

Rejected because the presence of a CLR method is not deployment evidence that the target can reconcile by stable
logical identity. Registration must opt in explicitly.

### Introduce a second typed reconciliation enum or union

Rejected because the canonical durable model already owns the complete reconciliation evidence family. The typed
wrapper constructs those existing observations without duplicating their case inventory.

## Consequences

- Ordinary handlers contain domain request and result types rather than portable envelope mechanics.
- Same-payload outcomes remain distinct because selection uses the exact protocol case descriptor.
- Adding or changing a protocol outcome cannot silently drift a handler capability list.
- Typed and handwritten adapters can be differentially tested for identical attempt and reconciliation evidence.
- Registration is deliberately incomplete until idempotency and reconciliation choices are explicit.
- Handler implementations registered as singletons must be safe for concurrent invocation.
- Specialized adapters retain the complete raw boundary instead of forcing the typed projection beyond its stated
  materialization and execution guarantees.
