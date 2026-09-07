# Cohesive.Integrations

This first experimental slice declares one bounded ingestion flow and lowers it to canonical
Cohesive.Processes. It is a composition compiler, not an ingestion service or a new execution runtime.
It does **not yet qualify a physical realization**. Do not interpret successful lowering as evidence
that an adapter can atomically publish data and coverage or safely retry an external operation.
Successful lowering emits `integrations.ingestion.realization.unqualified` as a structured warning;
`IsValid` means the declaration can be lowered, not that it is production-ready.

## The declaration

```csharp
// Exact RequestContractReferences resolved from an InteractionContractCatalog.
var definition = new IngestionDefinition(
    Acquire: acquireRequest,
    Publish: publishRequest,
    Settle: settleRequest);
var document = IngestionDefinitionDocuments.Create(
    new("ingestion/prices-to-research-repository"),
    new("revision/1"),
    definition,
    provenance);
var validation = IngestionDefinitionDocuments.TryLower(document, contracts, out var process);
```

The document identity names a stable source-to-destination application binding. Different consumers
or independently advancing destinations need distinct identities. Its revision and fingerprint pin the
meaning of the flow; changing a definition must not silently reset or reuse an incompatible ledger.
Exact Request references pin each operation's identity, revision, and fingerprint.

| Operation | Successful result means | Responsibility of its contract and implementation |
| --- | --- | --- |
| Acquire | One bounded, complete unit of work is available for application | Carry source identity, selected range or opaque continuation, stable work identity, completeness evidence, replayable input or retained artifact references, and eventual settlement information. |
| Publish | Destination effects, application coverage, and original operation evidence have been accepted together | Validate and normalize domain input, enforce scope/order/CAS rules, and retain a receipt for exact retry reconciliation. An existing domain publication operation can implement this boundary. |
| Settle | The source obligation is settled after publication | Acknowledge the source where applicable, or return a declared successful no-op for a pull API with no acknowledgment protocol. Repeating settlement must be safe within the declared recovery horizon. |

Request schemas own these payloads. There is no second generic envelope or progress enum. The
successful result **schema and schema revision** must equal the next Request's input schema.
A shape-compatible revision is insufficient; mappings must be explicit inside an operation or its
application Process. Each operation declares exactly one successful result. All other declared terminal
outcomes stop the flow. The graph returns `true` on completion and fails with `false` on a terminal
failure; that Boolean is completion status, never rollback evidence. Detailed outcome bindings and
publication receipts remain part of Process state/evidence and the operation's receipt store.

The compiler requires stable retry identity, ambiguity reconciliation, and reuse of duplicate outcomes
from every Request. These are **requirements on implementations**. It does not invent an exactly-once
guarantee, deduplication retention, completeness evidence, or a source snapshot. In particular, absence
of a receipt under weak visibility is not evidence that publication failed. Unknown publication must
remain unresolved/reconciling; an adapter must not convert it into a terminal rejection or authorize a
fresh differently identified publication.

## Existing authorities

- Core execution documents own persistence, canonical serialization, revisions, fingerprints, and provenance.
- Core Request contracts own payload schemas, response outcomes, and recovery obligations.
- Processes owns durable boundaries, continuation, request identity, dispatch, reply correlation, and recovery.
- The domain publication operation owns destination invariants and application coverage.
- Storage and its adapters own physical commit validation, native boundaries, receipts, and reconciliation.

Lowering produces three existing Request nodes and two terminal nodes. It performs no I/O. The derived
Process retains the exact source document reference and maps each Request node to its ingestion role.
Its size is linear in the declared terminal outcomes; no new runtime worker, scheduler, serializer,
transaction manager, or ambient dependency is introduced. Unsupported semantic extensions are rejected.

Direct immutable declarations plus the shared JSON envelope are the initial authoring surface. Agents
can persist, inspect, revise, diff, lower, and validate these documents through the same APIs as humans.
An expression/fluent projection can follow concrete domain uses; it must lower to this authority and
cannot retain executable closures. There is no AI dependency at runtime.

## Deliberate limits and next proof

This profile is restricted to **atomic publication followed by settlement**. It does not model separate
sink and ledger commits, source acknowledgment before application, fan-out, compensation, long-running
transactions, or automatic loops over pages. It also does not select the next missing range: an
application/source contract selects bounded work from ledger evidence. Range and opaque cursor inputs
pass unchanged through the same composition; their distinct ordering, completeness, expiry, overlap,
and revision semantics remain source-owned. The tests' string payloads are a transport-level example,
not an implementation of those semantics.

The next COH-107 slice must bind a concrete domain publication boundary and qualify target capabilities
and recovery evidence before execution is advertised as ingestion-ready. That includes atomic placement,
scoped idempotency, retention horizons, exact-operation receipts, source completeness, safe advancement,
and unknown-outcome handling. The existing Storage executor cannot automatically enlist arbitrary
pre-existing application tables. Preserve an existing safe domain transaction until an explicit mapping
has been demonstrated. A real bounded provider command can ship independently of this compiler.

COH-108 covers the recoverable **separate-store** protocol and crash matrix. Those guarantees require
an explicit realization; they cannot be obtained by weakening this atomic profile or retrying an upsert
that might overwrite newer destination data. Broker-specific adapters and broad Integrations features
remain later work.

## Verification

`Cohesive.Tests/Integrations/IngestionDefinitionTests.cs` exercises canonical reopening and deterministic
lowering, exact schema revisions and contract fingerprints, missing contracts, recovery requirements,
and the existing Process reference interpreter. It verifies request payload propagation, stable
re-evaluation identities, post-publication settlement, and stopping on each stage's failure. It does
not claim database crash recovery or actual external-effect deduplication; those need adapter proofs.
