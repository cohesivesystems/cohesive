# Cohesive.Storage

`Cohesive.Storage` defines provider-neutral durability, source acquisition, aggregate storage realization, and
relation-derived materialization contracts for Cohesive systems.

Typed entity repositories preserve the underlying repository's batch capabilities, item limits, input order, and
requested atomicity. `UpsertBatch(context, entities, atomicity)` maps ordinary records through the same selectors as
single writes, then invokes one native batch. Per-candidate concurrency tokens use the canonical
`EntityBatchWriteRequest` overload. Unsupported atomicity is rejected instead of silently using independent writes.

## Install

```bash
dotnet add package Cohesive.Storage
```

## Register a Relation source

Storage can attach an entity repository to the canonical Relations acquisition port. Shape, source identity, limits,
and version projection are derived or declared once:

<!-- docs-sync:storage-relation-source:start -->
```csharp
var source = EntityRelationQuerySourceRegistration.InMemory(
    loadShape,
    repository,
    logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
    observationVersionSemanticPath: FieldPath.FromField("SourceEntityVersion"),
    limits: new(
        maximumBatchSize: 100,
        maximumBufferedRows: 10_000,
        maximumFanOut: 100,
        maximumConcurrency: 4));

var catalog = new EntityRelationQuerySourceCatalog([source]);
IRelationQueryEvaluator evaluator = catalog.CreateEvaluator(physicalPlanningPolicy);
var outcome = await evaluator.EvaluateAsync(evaluation, cancellationToken);
```
<!-- docs-sync:storage-relation-source:end -->

Relations remains authoritative for filters, joins, projections, aggregation, and paging. Storage contributes bounded
physical acquisition and exact source evidence.

## What this package owns

- Entity repository and observation-stream ports.
- The atomic durable Process aggregate, store contract, and provider-neutral reference runtime.
- Canonical Relation source registration and evaluator composition.
- Aggregate storage structures and target realization documents.
- Relation-derived materialization definitions, impact planning, rebuilds, and incremental synchronization.
- Generation allocation, sealing, validation, promotion, cleanup, routing, and progress evidence.
- Query-authority and lifecycle-control contracts used by storage adapters.

## Important boundaries

`Cohesive.Storage` does not define another query language, Transition model, or Process model. It consumes exact
semantic documents and compiled evidence from those owning blocks. Provider SDKs and physical schemas remain in
adapter packages.

Retained entity snapshots and Transition operation commits use the explicit lossless
`EntityStorageJson.CreateOptions()` profile. It reuses the core PortableValue tagged node codec for
detached observation fields; ordinary JSON serialization is unchanged. Payload format 2 and
`sha256-entity-v2` commit fingerprints require explicit migration of older retained evidence. Request
and operation identities remain stable so old receipts cannot be mistaken for missing operations.
See the [SQLite adoption and migration notes](../adapters/Cohesive.Adapters.SQLite/OUTBOX.md).

`InMemoryProcessDurableStore` is the copy-on-write reference implementation and semantic test oracle. It is not a
production durability provider and does not claim physical exactly-once publication.

Materialization uses the Relations dependency manifest and lineage rather than copying their edges into a second
model. When an incremental route cannot be proven within configured limits, planning fails closed or requires an
explicit rebuild policy.

## Continue

- [Internals](INTERNALS.md) covers the durable Process aggregate, source contracts, storage realizations,
  materialization lifecycle, routing, and query authority in detail.
- [Index synchronization runbook](../../docs/INDEX_SYNC_RUNBOOK.md) covers the operational path.
- [`Cohesive.Relations`](../Cohesive.Relations/README.md) owns relation/query semantics and dependency evidence.
- [`Cohesive.Adapters.Postgres`](../adapters/Cohesive.Adapters.Postgres/README.md),
  [`Cohesive.Adapters.Cosmos`](../adapters/Cohesive.Adapters.Cosmos/README.md), and
  [`Cohesive.Adapters.Elastic`](../adapters/Cohesive.Adapters.Elastic/README.md) provide physical interpretations.

## Experimental atomic commit declarations

`Cohesive.Storage.Commits.IStorageCommitExecutor` accepts an immutable `StorageCommitIntent`:
conditional portable item writes, explicit query guard dependencies and a retained operation result.
SQLite and Cosmos interpreters commit the writes and receipt atomically within their advertised
placement limits. `ReconcileAsync` resolves exact receipts after uncertain acknowledgments without
reading later state. Missing evidence is `Unknown`, not proof that an operation rolled back.

This bounded profile owns dedicated item storage; it does not yet enlist arbitrary repository tables.
See the [decision and native capability boundaries](../../docs/decisions/declarative-storage-commits.md).

Third-party interpreters implement the public executor interface and advertise their capability
profile. Intent inspection, strict serialization and result factories are public; no friend-assembly
registration or access to adapter internals is required.

Commit preflight is `executor.Validate(intent)`: it includes adapter-encoded payload limits without
I/O. `Capabilities.ValidateStructure(intent)` only checks placement and dependencies; full capability
validation requires serialized byte evidence when a payload budget is declared. Query guard writes
must carry a non-null token captured before the query; initialize a missing guard separately.


### Durable Process JSON depth

Checkpoint, commit, and durable-store serialization and content fingerprints share the bounded
profile exposed by `ProcessDurableCheckpointJsonSerializer.CreateOptions` (maximum JSON depth 256).
Tagged portable values use additional containers per domain object or array; the storage budget
includes those tags and surrounding checkpoint/store envelopes. It does not relax semantic value
validation. Canonical recovery uses the same configured depth for parsing and typed projection.
Values beyond the budget fail serialization or produce a structured invalid-JSON recovery diagnostic.
Existing supported values retain their canonical bytes and fingerprints; the change expands the
accepted nesting range without changing the wire representation. `ProcessStorageDepthTests` covers
nested value round trips, fingerprint stability, shallow-byte compatibility, and bounded rejection.

## Checked immutable receipt projection

`ProcessTransitionOperationBinding.CreateProcessDefinitionLink()` supplies the native receipt-contract
attestation only when the repository declares atomic state/receipt support and its entity shape matches
the Transition observation. It performs no reads or writes; provider conformance still qualifies the claim.
`EntityTransitionReceiptReferences.ValueContract` is the single portable locator contract already used
by the native adapter, not a second receipt schema.

`EntityTransitionReceiptReferences.ResolveSnapshotAsync` resolves an immutable commit snapshot using
the expected Transition, admitted authority/continuation and trusted physical partition. It rejects
unqualified providers and mismatched locators before repository access, validates the resolved evidence,
and requires an explicit resource-authorization callback before returning the original snapshot/token.
Provider errors and cancellation propagate. Missing evidence never falls back to a current entity read.

This operation supports Process result enrichment and is also reused by service committed-entity result
reads. For example, after commit A is followed by mutation B, resolving A still returns A's fields and
concurrency token; a wrong tenant, attempt, Transition or partition returns no snapshot. In-memory tests
cover those boundaries and denied resource disclosure. Optional Cosmos integration remains a separate gate.

`ProcessEnrichmentUsesAuthorizedOriginalReceiptAfterLaterWrite` exercises this boundary with the native
Process interpreter, Transition storage adapter and a registered hosted query. A customer is committed
with status `pending`, another writer changes it to `later`, and enrichment still returns `pending`
with the original token. Denied disclosure fails the Process while preserving the committed entity and
its interaction; it does not roll back or repeat the Transition. This is in-memory integration evidence,
not Cosmos conformance or deployed recovery qualification.

### Typed identity preparation and migration (unreleased PR405)

`IEntityRepository.IdentityField` is a required member. Decorators must forward it from their underlying
repository; native implementations explicitly return their semantic mapped identity field, or null when
choosing the legacy Id/Key convention. This source-breaking requirement prevents an omitted wrapper
member from silently changing a Sku identity to an incidental Id property. Update custom repository and
wrapper implementations before compiling against this change. A deliberately chosen null remains an
explicit convention policy, not automatic metadata discovery.

`PrepareIdentitySelector<T>` retains finite metadata and lazy delegates per closed CLR type/property.
Both mapped-field and conventional selection return the same prepared delegate on warm lookup; no
expression construction, reflection, property filtering or new closure occurs in that lookup. Untyped
Upsert uses this path; typed batch mapping selects once per batch. Entity/request values are never cached.
Initialization and compilation happen on first use, not per write. Identity formatting preserves existing
EntityId/string/invariant formattable behavior; formatting non-string values may allocate their text.

`EntityIdentitySelectorTests.Warm_selector_lookup_and_string_extraction_do_not_allocate_per_call` warms
20,000 calls then measures 100,000 same-thread selector lookups plus string identity reads in Release.
It requires at most 1 KB total measured allocation for each mapped/convention case, tolerating fixed runtime
bookkeeping while rejecting even one byte per invocation. This isolates identity selection; it is not a
zero-allocation claim for complete writes, version selection, canonical state construction or provider IO.

Observation-based in-memory and Cosmos outbox repositories both explicitly use the shared typed-write
Id/Key convention (or the caller’s selector). The in-memory `idFieldName` option governs seed import
only; it does not declare a native identity mapping for later typed writes. Thus a JsonPropertyName-renamed
Id or a Key-only POCO has the same typed preparation behavior in both stores.
