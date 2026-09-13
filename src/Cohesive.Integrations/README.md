# Cohesive.Integrations

This experimental block declares bounded ingestion flows and lowers it to canonical
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

Request schemas own application payloads. The ledger contracts below own only progress and receipt metadata. The
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

Atomic-profile lowering produces three existing Request nodes and up to two terminal nodes. It performs no I/O. The derived
Process retains the exact source document reference and maps each Request node to its ingestion role.
Its size is linear in the declared terminal outcomes; no new runtime worker, scheduler, serializer,
transaction manager, or ambient dependency is introduced. Unsupported semantic extensions are rejected.

Direct immutable declarations plus the shared JSON envelope are the initial authoring surface. Agents
can persist, inspect, revise, diff, lower, and validate these documents through the same APIs as humans.
An expression/fluent projection can follow concrete domain uses; it must lower to this authority and
cannot retain executable closures. There is no AI dependency at runtime.

## Deliberate limits and next proof

The original atomic profile is restricted to **atomic publication followed by settlement**. The
separate-ledger profile below models an additional ledger commit. Neither profile models source
acknowledgment before application, fan-out, compensation, long-running
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

COH-108 tracks the recoverable **separate-store** protocol and native crash matrix. The reference contract below begins that work. Physical guarantees require
an explicit realization; they cannot be obtained by weakening this atomic profile or retrying an upsert
that might overwrite newer destination data. Broker-specific adapters and broad Integrations features
remain later work.

## Verification

`Cohesive.Tests/Integrations/IngestionDefinitionTests.cs` exercises canonical reopening and deterministic
lowering, exact schema revisions and contract fingerprints, missing contracts, recovery requirements,
and the existing Process reference interpreter. It verifies request payload propagation, stable
re-evaluation identities, post-publication settlement, and stopping on each stage's failure. It does
not claim database crash recovery or actual external-effect deduplication; those need adapter proofs.

## Separate ingestion ledger profile

A flow may now declare `AdvanceLedger` as an additional exact Request:

```csharp
var definition = new IngestionDefinition(
    Acquire: acquireRequest,
    Publish: publishRequest,
    Settle: settleRequest,
    AdvanceLedger: advanceLedgerRequest);
```

This produces `integration.ingestion.separate-ledger.v1`, lowered through the same compiler to
**acquire → publish → advanceLedger → settle**. The existing atomic kind and its three-step wire remain
unchanged when the optional field is absent. Reinterpreting an old document as a different protocol is
rejected; reauthor a separate definition revision for a deliberate migration. Exact adjacent result/input
contracts still have to match. Acquire owns retaining exact work, expected ledger revision and publication
identity before publication; Publish returns verified original sink evidence; AdvanceLedger commits
progress with its own receipt; Settle acts only after that success. Unknown effects remain unresolved
Requests, not declared terminal failures. A definite ledger conflict stops before source settlement;
it does not roll back sink data or automatically rebase the ledger.

### Ownership and persistence

`IngestionLedgerAddress` scopes progress by flow, source, destination and partition. Two consumers of
the same source and destination use distinct flow identities. Exact definition revision/fingerprint is
retained in the entry rather than made part of its address: a new semantic revision cannot silently
create an empty ledger and restart ingestion. Position and expected revision are captured during work
selection and must not be refreshed because an acknowledgment was lost.

- `IngestionCursorPosition` retains an exact opaque next token, its source format/revision and optional
  expiry. Null token explicitly means source exhaustion. No entry means no committed progress. Values
  are neither trimmed nor sorted; the source handles cycles, token validity, resumption and completeness.
- `IngestionDateRangePosition` records a half-open forward date window. Its exclusive end is the next
  forward boundary. Successive windows must abut; calendar coverage, confirmed gaps, source date-range
  conversion, overlap reads and historical backfill are not inferred. Use a separately scoped flow for
  a backfill. This first range representation uses dates, not arbitrary intraday timestamp windows.
- The sink retains acquired evidence, domain coverage and original publication receipts. The ingestion
  ledger records source progress. The Process owns outstanding work and recovery. These contracts do
  not require separate physical databases.

`IngestionLedgerAdvance` is immutable control data, not a second envelope for application payloads.
It binds the ledger address, exact flow definition, original expected revision, next position and
`IngestionPublicationReceipt`. A receipt declares the original publication identity, prepared-content
fingerprint and receipt locator. The publisher owns the fingerprint algorithm and the receipt's native
representation. Trusted adapters must verify this binding and scope before calling the ledger. Merely
constructing this record does not prove that any sink committed; unknown outcomes cannot be promoted
to this evidence.

`IngestionLedgerDocuments` uses the existing execution document envelope, canonicalization, strict
projection, fingerprints and provenance. No serializer or patch system is added. Its document identity
encodes all four scope components independently; its revision identifies the publication. Persist the
original work/expected revision before sink dispatch, and the completed advancement document before
ledger dispatch. A publication handler's retained result can carry this document to the next Request.
Direct C# records are draft producers; `Create`/`TryRead` validate the materialized document. An expression
DSL is deliberately deferred because this surface currently consists only of immutable data.

### Reference contract and crash behavior

`IIngestionLedger` provides current progress observation and exact `AdvanceAsync`. Each advance must
commit the new entry **and an independent original-result receipt atomically**. `IngestionLedgerReduction`
owns the decision rules so adapters need not independently implement another replay/CAS algorithm:

1. Check the original receipt under `(ledger address, publication ID)` **before** inspecting current
   progress. Exact input returns that receipt, even after later work; different input is identity conflict.
2. Require the originally captured revision (zero means initial absence).
3. Require the pinned definition and supported position transition; no implicit migration, skipped date
   windows, cursor-format change or reset after source exhaustion.
4. Propose the next revision and original result. A physical interpreter must atomically persist both
   before exposing `Advanced`. It must never apply that result on `Replayed` or `Conflict`.

The reducer requires a definitive, atomic view. Weak visibility with an absent receipt is inconclusive:
return `Unknown` without treating it as a precondition conflict. The storage adapter must serialize the
read/evaluate/write boundary or supply equivalent native CAS/transaction semantics. Arbitrary constructor
values, current snapshots or an in-memory lock are not evidence of physical qualification.

| Interruption | Required recovery |
| --- | --- |
| After acquisition, before publication | Resume retained exact work and identity. |
| Sink committed, acknowledgment lost | Reconcile/retry the same sink operation; advance no ledger while unknown. |
| Sink confirmed, before ledger commit | Retry the exact retained advancement; do not republish with a new identity. |
| Ledger committed, acknowledgment lost | Recover the original ledger receipt before current revision checks. |
| Later work advances the ledger before an old retry | Return the old original receipt without regressing current progress. |
| Ledger conflict after publication | Preserve the sink result and stop; require explicit recovery/replanning, not compensation by default. |
| Source settlement interrupted | Retry its stable Request only after confirmed ledger success. |

`InMemoryIngestionLedger` is a thread-safe reference interpreter retaining receipts for its lifetime.
It is **not process-durable**, has no retention eviction, and must not be used as a production ledger.
Tests model both acknowledgment-loss boundaries and reference Process sequencing; native database
crash recovery is deferred to the next SQLite adoption slice. The per-advance cost includes canonical
projection/validation; it is intended for bounded acquisition units, not individual market ticks. Identity
fields are limited to 1024 UTF-8 bytes and cursor tokens to 16384. Retained receipt growth is explicit.

`IngestionRecoveryCapabilities.Validate` emits stable diagnostics for missing prepared input, unsafe
publication retry, non-atomic ledger/receipt writes, unsafe receipt visibility, and insufficient or unknown
input/sink/ledger retention horizons. It validates declared evidence for those obligations, not the
complete physical realization. Source completeness, settlement and native adapter conformance remain
required; successful lowering still emits `integrations.ingestion.realization.unqualified`.

Storage's generic commit executor can eventually realize a ledger transaction, but does not own source
position semantics or automatically enlist pre-existing domain tables. The next adoption step is an
explicit SQLite mapping and Process binding, preserving domain publication receipts and keeping
combined atomic sink/ledger transactions as a separate optional realization. Cosmos, generic UoW,
compensation, scheduling and native broker transports are outside this PR.
