---
kind: decision
status: implemented
authority: cohesive.execution.typed-request-outcome-projection
owners: [cohesive-core]
applies_to: [cohesive, cohesive-processes]
last_verified: 2026-08-20
supersedes: []
---

# Project canonical Request outcomes as a source-only closed C# family

## Context

A canonical Request owns a heterogeneous terminal-outcome set. Each outcome has a stable identity, semantic kind,
portable payload schema, response policy, and exact Reply contract. Process callers need ordinary typed C# control
flow over that set, including when two outcomes carry the same CLR payload type.

The representation-neutral `RequestProtocol<TRequest, TOutcomes>` names a request payload and a caller-owned set of
typed canonical descriptors. It deliberately does not name a CLR union root. C# therefore cannot infer the result
of `var outcome = await process.Effect(protocol, input)` from that handle alone. Repeating descriptor identities and
payload type arguments at every Effect call would recreate the parallel call-site ledger that the protocol removes.

The Process computation generator must also determine exhaustiveness without executing a protocol factory. A
protocol may come from the current compilation or a referenced assembly, so its finite case set must be visible in
ordinary CLR type metadata.

## Decision

Keep canonical Request and Reply documents as the sole durable semantic authority. Add a non-canonical typed
projection with these roles:

- `RequestProtocol<TRequest, TOutcome, TOutcomes>` names the source-only closed result-family root in addition to the
  existing descriptor-set type.
- `RequestProtocolCase<TCase, TPayload>` associates one distinct CLR case with one canonical
  `RequestProtocolOutcome<TPayload>`. The association contains no case constructor, callback, discriminator, or
  runtime matching behavior.
- Each current record case declares exactly one public payload property. `TOutcomes` publicly exposes every case
  descriptor exactly once, in protocol declaration order, as an instance property. Protocol authoring validates
  those conventions. The Process analyzer reads the same property signatures from Roslyn metadata to obtain the
  complete case and payload-type inventory without running user code.
- A typed `ProcessContext.Effect` returns `TOutcome`. Its immediately following unguarded type switch must handle
  every protocol case exactly once. A direct positional or property payload binding is projected to the canonical
  Request outcome binding; the wrapper case itself is never constructed or serialized.
- The generator fuses switch sections into the existing `RequestProcessNode` and `RequestProcessOutcome`
  continuations. It derives default branch identities from the owning Request node plus the canonical outcome id,
  not from CLR case names or declaration ordinals.
- Raw exact-reference and outcome-array authoring remains the advanced importer and compatibility escape hatch.

The current abstract-record family is a host-language projection, not a new union IR. When native C# unions are
available, the analyzer may recognize their case metadata and project the same canonical outcome associations.
Canonical Request documents, Process IR, fingerprints, adapters, and durable evidence require no migration.

## Alternatives considered

### Repeat typed outcome handlers at every Effect call

Rejected because the call site would repeat protocol identities and payload arguments, could drift from the
canonical outcome set, and would preserve the branch-function ceremony this projection is intended to remove.

### Add an independently maintained discriminator enum

Rejected because it would become a competing identity authority and would not distinguish revisions or preserve
the canonical outcome id automatically.

### Use the descriptor-set type itself as the result-family root

Rejected because one type would then represent both the immutable catalog instance and selected runtime cases.
Those values have different invariants and lifecycles, and the conflation would make native-union adoption harder.

### Generate concrete case records from protocol factories now

Deferred. A generator cannot generally execute an arbitrary protocol factory, and parsing factory syntax would not
work for protocols supplied by referenced assemblies. Explicit record cases plus metadata-visible descriptors give
the required semantics with a small replaceable projection. Native C# union support can later remove that ceremony.

## Consequences

- Request payload mistakes fail through ordinary C# generic type checking.
- Same-payload outcomes remain distinct through their case types while sharing the exact canonical payload schema.
- Adding or exposing a protocol case makes an existing typed Effect switch incomplete at the source location.
- No callbacks, wrapper objects, case discriminators, or CLR type identities enter canonical documents or replay
  state.
- Typed and raw authoring can be differentially tested for normalized bytes, fingerprints, compiled effects, and
  reference-interpreter behavior.
- Protocol authors temporarily declare the record family and typed case-descriptor properties. That ceremony is an
  explicit replacement boundary for native C# unions rather than a durable semantic contract.
