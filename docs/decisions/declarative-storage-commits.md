# Declarative atomic storage commits

Status: experimental bounded implementation (COH-105).

## Decision

`Cohesive.Storage.Commits` owns a materialized, versioned `StorageCommitIntent` and
`IStorageCommitExecutor`. An intent declares conditional portable item writes, an operation
receipt identity, an exact retained result, and any guarded query dependencies. An executor
validates its capability profile and realizes the intent as one atomic commit. It never silently
weakens atomicity or turns an unsupported commit into several independent writes.

This is a bounded commit model, not an ambient `Begin/Commit/Rollback` lifetime. An API handler
may read, construct its decision and submit an intent. It does not keep a transaction open while
waiting for network calls, people or external effects. A durable Process may surround multiple
such commits; compensation remains authored Process behavior, not inferred storage rollback.

## Semantic authority and reuse

The immutable intent is the authority. C# constructors are currently its small authoring
projection; strict tagged JSON permits inspection, persistence, reconstruction and comparison
without a live host-language callback. No expression/closure survives into the declaration.
The wire revision, logical addresses, preconditions, values, query dependencies and retained
result are fingerprinted. Writes are sorted by ordinal target/partition/id, so write enumeration
order does not change identity. Query dependency order remains part of v1 content.

The design reuses `PortableValue`, `EntityConcurrencyToken`, the canonical JSON writer,
`EntityStorageJson`'s lossless tagged scalar codec and structured document diagnostics.
SQLite construction goes through the existing shared SQL builders and `SqliteCommandScope`;
only the fixed, module-owned DDL is literal migration SQL, as elsewhere in this adapter.

Existing `EntityTransitionOperationCommit` and `ProcessDurableCommit` remain their domain-owned
contracts. They additionally validate Transition decisions or Process checkpoint/lease/fencing
invariants. They are not aliases for generic item writes, and this slice does not migrate them.
Receipt-first reconciliation is the common protocol, not a second implementation of those
higher-level validators. Future lowering can target this commit surface after preserving every
higher-level invariant and physical schema boundary.

## Supported profile

- Nonempty conditional create/replace writes. Null expected token means **must be absent**;
  a supplied token means **must currently exist at exactly that version**. There is no
  unconditional upsert, delete, arbitrary SQL callback or universal patch language in v1.
- State writes and a receipt are atomic. A successful write's next opaque token is the intent
  fingerprint. Because the receipt prevents applying an old operation twice, reverting a value
  through a new operation cannot recreate an old token (ABA protection within this protocol).
- A receipt stores the reference and the materialized result, so reconciliation does not read
  later state or re-run decision logic. Item and receipt identities use disjoint namespaces.
- The storage authority is the configured database/schema or container plus logical target and
  partition. Operation IDs are unique within that authority, not globally. Remapping a logical
  target to a different authority requires a deliberate receipt/state migration.
- The caller owns cancellation. Transport exceptions, timeout and cancellation after submission
  can be ambiguous. Retry the **same** intent or call `ReconcileAsync` with its exact reference.
  Never recompute a changed decision under that operation identity.

`Committed` and `Replayed` carry receipts. `IdentityConflict`, `PreconditionFailed` and
`Unsupported` carry structured diagnostics. Receipt absence is `Unknown`, including on SQLite:
this uniform reconciliation contract does not establish that another attempt cannot still commit.
A precondition failure describes this attempt; it does not exclude another in-flight exact attempt.

Receipts are retained indefinitely in v1. Deleting/expiring them, writing around the executor or
changing physical ownership invalidates the protocol. Retention compaction must eventually retain
adequate identity tombstones, and is deliberately outside this proof.

## Native realizations

| Property | SQLite | Cosmos |
| --- | --- | --- |
| Owned data | Dedicated strict item/receipt table | Dedicated document namespace |
| Commit boundary | One `BEGIN IMMEDIATE` transaction | One transactional batch |
| Placement | Multiple logical targets/partitions in one database | One bound target/container and one logical partition |
| Version enforcement | Conditional update / insert-if-absent | Logical token read translated to native `IfMatchEtag` / create |
| Durable profile | WAL with FULL synchronization | Service-acknowledged batch; one observed writable region |
| Limits | Native database limits | 100 items including receipt; conservative 1 MiB serialized-document budget |
| Queries with guards | Supported under the declared reader/writer protocol | Requires observed Strong account consistency and Strong query reads |

Cosmos creation checks container partitioning (`/partitionKey`), disabled TTL, no additional unique-key constraints and a single
observed writable region. Its partition length limit follows the observed partition-key revision
(2,048 UTF-8 bytes for v2, 101 for v1), as specified in the
[service limits](https://learn.microsoft.com/en-us/azure/cosmos-db/concepts-limits). It records the account's query consistency capability. Configuration
must remain stable for the executor lifetime; recreate and requalify after topology changes.
This profile intentionally rejects other targets/partitions and does not claim to implement
other Cosmos transaction facilities. Requests exceeding its declared item or byte budget are
rejected before submission, and native batch limits remain a final atomic rejection boundary.

Both realizations own dedicated storage. **They cannot yet enlist existing entity repository
rows or arbitrary application tables/documents.** This is an executable proof of commit semantics,
not a drop-in replacement for every repository transaction. Adopting an application store will
require an explicit mapping/lowering decision, with schema and query compatibility tested.

## Query predicates and phantom protection

Checking ETags of returned items does not protect a query like “there are no active items”. A
concurrent writer can create a different matching item without changing any previously read ETag.
A supported guard protocol is:

1. Read a designated guard's token (absence may be the initial version).
2. Evaluate the query using a fresh committed read boundary that observes at least the guard read.
3. Construct the result and include a conditional **guard write**, even when its value is unchanged.
4. Declare the exact query/arguments/read-contract fingerprint and guard address as a query dependency.
5. Require **every writer that can change this predicate** to advance the same guard atomically.

If another writer changes membership during this interval, the guard precondition fails and the
entire attempted decision rolls back. The query itself need not share a long-lived transaction
with application code. On SQLite, do not query from an old read transaction. On Cosmos, this v1
qualification uses Strong guard and query reads; eventually consistent queries are not qualified.

The executor can check that a guard participates in the intent and enforce its CAS. It cannot
infer the predicate, prove its declared fingerprint, or enforce an application-wide all-writers
protocol from this declaration alone. That requirement is explicit, not evidence supplied by
an ETag. The next application adoption must centralize writer construction so bypassing guards
is prevented at its owning boundary. More selective guards, predicate locks, query read-set IR
and automatic guard inference require separate semantic work.

A query dependency with no participating guard, or without a qualified consistency profile,
is rejected before I/O. Omitting a dependency does not magically validate an application decision;
it declares only the item preconditions present in the intent.

## Recovery example

A synthetic reservation operation reads item version A, decides `reserved`, and commits the
replacement plus `{ accepted: true }` under operation R. The response is lost. Another operation
then releases the item at version C. Retrying R finds its receipt before testing A, returns the
original acceptance, and leaves C unchanged. No external action is executed by this adapter.

A Process checkpoint recording that R completed may live elsewhere. If the worker stops between
R's commit and that checkpoint, the resumed Process reconciles R and then records its progress.
The receipt is commit evidence; it is not itself an application ingestion cursor or Process checkpoint.

## Verification and deliberate limits

One conformance suite exercises both physical interpreters: conditional creation/replacement,
rollback including an earlier successful write, competing writers, disjoint receipt identity,
exact replay after later state, lost acknowledgment, target/partition limits and a query guard race.
Separate tests reopen SQLite connections and Cosmos clients, round-trip strict JSON, preserve
binary/decimal scalar tags and validate capability limits. SQLite's full adapter suite guards
against regressions in shared command/schema behavior.

The local Cosmos vNext emulator reports Eventual consistency. Native batch/CAS/replay tests run
there; query-dependent intents must be rejected under that observed profile. Its ordinary guard
CAS is still tested. A Strong production test account is required to qualify the complete Cosmos
query-read protocol; the emulator must not be relabeled Strong to make that test pass.

No new dependency, saga framework, durable runtime, general repository query API or application
code is introduced. Item acquisition helpers on the concrete adapters are a proof surface; existing
Relations evaluation remains the semantic query authority. Storage mapping and application adoption
are the next separate slice after this contract is reviewed.

## Initial cost baseline

`StorageCommitBenchmarks` measures the declaration boundary with 256-character item values,
independently of database I/O. A local September 7, 2026 run on Apple M5 Max, .NET 10.0.5,
BenchmarkDotNet 0.15.8 and `DOTNET_PROCESSOR_COUNT=2` used one launch, three warmups and five
measurement iterations:

| Writes | Construct + fingerprint | Allocated | Restore + fingerprint | Allocated |
| --- | ---: | ---: | ---: | ---: |
| 1 | 5.7 µs | 21.3 KiB | 12.6 µs | 32.4 KiB |
| 10 | 30.0 µs | 96.6 KiB | 80.0 µs | 154.1 KiB |
| 99 | 276.5 µs | 876.6 KiB | 736.3 µs | 1,392.3 KiB |

Canonical serialization alone measured 5.4 / 27.4 / 292.3 µs and 22.5 / 107.8 / 986.1 KiB
at these sizes. Already-canonical immutable write arrays retain their backing storage after
validation. Portable JSON traversal and fingerprinting still allocate substantially. These short
local measurements establish an initial baseline; restore timings were noisy and carry broad
confidence intervals. Profile realistic payloads and operation rates before changing the wire path.

Cosmos also performs one receipt lookup plus a point read for each conditional replacement before
submitting the batch. Those reads translate portable logical versions into native ETags; they do
not establish isolation. The batch's ETag conditions reject intervening writes. Native creation
writes need no item pre-read. Bounded read fan-out and more specialized storage bindings remain
future measured improvements, particularly for replacement-heavy workloads.

Reproduce with:

```sh
DOTNET_PROCESSOR_COUNT=2 dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- \
  --filter '*StorageCommitBenchmarks*' --job short --warmupCount 3 --iterationCount 5 --launchCount 1
```
