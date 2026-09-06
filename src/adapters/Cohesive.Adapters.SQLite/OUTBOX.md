# SQLite entity outbox and Transition receipts

`SqliteEntityOutboxRepository` implements `IEntityOutboxRepository` and
`IEntityTransitionOperationRepository` on the same scalar entity mapping as `SqliteEntityRepository`.
Each mutation owns one connection and one immediate transaction in one database file. Entity state,
receipt evidence, and identity indexes either all commit or all roll back. Ordinary reads, writes,
and batches retain the existing repository behavior and native capabilities.

## Initialize explicitly

```csharp
var database = new SqliteDatabase(new SqliteDatabaseOptions("/data/ito.db"));
var mapping = new SqliteEntityRepositoryMapping(
    entityDefinition, identityField: "id", partitionField: "tenant", tableName: "runs");
var repository = new SqliteEntityOutboxRepository(database, mapping);

new SqliteSchema("ito/run-state", [mapping.InitialMigration]).Apply(database);
new SqliteSchema("ito/run-outbox", repository.Migrations).Apply(database);
```

Use stable application-owned module names. Constructors perform no I/O. An existing initialized
entity table can acquire the auxiliary schema without changing its entity migration. The mapping
and auxiliary table names are inspectable; quoted naming uses the shared SQL dialect. This does
not automatically migrate changed entity shape revisions or adopt incompatible existing tables.

## Two publication authorities

| Commit | Durable contents | Publication authority |
| --- | --- | --- |
| `UpsertWithOutbox` | State, original snapshot/token, ordered direct-Transition envelopes, emission identities | Entity outbox |
| `CommitTransitionOperation` | State, exact operation/definition/input, decision/result, guarantees, execution evidence, original snapshot/token and physical commit time | Process outbox; entity receipt is handoff evidence |

`EntityOutboxCommit` remains the validation authority for direct envelopes: durable Domain Events
or Requests from one direct Transition decision for the exact candidate entity. A Process-origin
envelope cannot enter this path. Process commits use the canonical operation commit/receipt
contracts and retain all handoff evidence. Their envelopes never appear in `ReadOutbox` and are
not inserted into the entity emission index.

Both paths use the existing entity write operation inside their owned transaction. Shape checks,
scalar encoding, token generation, and CAS SQL have one implementation. A stale or missing
first-time conditional target cannot insert. A stored shape mismatch remains an explicit shape
failure, rather than being converted into a concurrency result.

## Retry and creation identity

Direct commits use their stable caller-supplied emission identities. Exact retries must identify
one retained commit with exactly the same ordered canonical envelopes and candidate state. A
changed payload, reordered/subset envelope list, mixture of old and new identities, or changed
state fails with `InvalidOperationException`. The original snapshot/token and envelopes are
returned even when ordinary writes or later transitions have advanced the entity. Retried CAS
tokens are not reevaluated after exact replay has been established.

A direct commit with no envelopes has no caller-supplied retry identity. It behaves as an ordinary
write, produces no outbox entry, and rotates its token on each successful execution.

Process operation keys hash the canonical occurrence, including Process attempt, activation,
token, node, and occurrence index. Canonical request and commit fingerprints decide exact replay;
reusing an occurrence for another input, definition, subject, result, or commit returns structured
`IdentityConflict` evidence. First-time stale CAS produces `ConcurrencyConflict` evidence.

Creation additionally reserves one subject identity across all logical partitions of this mapped
repository. Existing ordinary state cannot be adopted as a successful creation. A replacement
Process attempt can look up the retained creation by authority-scoped intent and recover the
original receipt. Like the reference in-memory adapter, a replacement creation commit must also
agree on candidate state, decision kind, and typed result. It returns the original operation's
handoff evidence rather than manufacturing new emissions. Cosmos retains its stricter full-commit
comparison when resolving a competing creation commit; exact request/commit matching and creation
intent lookup share receipt-owned matching methods across all three adapters.

## Durable representation and limits

The ordered auxiliary migrations create three STRICT tables derived from the entity table name:

- `__receipts`: monotonic sequence, unique versioned receipt ID, direct/Process discriminator,
  canonical JSON BLOB, SHA-256 integrity hash, and explicit payload `format`. A `(kind, sequence)` index serves outbox reads.
- `__emissions`: unique direct emission ID referencing its owning receipt.
- `__creations`: unique creation subject ID referencing its Process receipt.

Canonical `EntityCommitResult` or `EntityTransitionOperationReceipt` data is authoritative; identity
indexes do not duplicate envelope payloads. The `direct/v1/` and `process/v1/` key namespaces identify
the stable identity grammar, independently of payload format. Canonical serializers preserve exact
definitions, observation shape identity, values, provenance, and normalized evidence. Reads verify
the byte bound, hash, canonical round trip, mapped state, and relevant identity evidence. Invalid
or unknown representations fail explicitly; they are never reconstructed from current state.

`EntityStorageJson` format 2 uses the existing PortableValue tagged observation codec for detached
state fields. Bytes, temporal values, and numeric kinds survive without guessing from JSON tokens.
Entity operation commit fingerprints use `sha256-entity-v2`; request and operation identities stay
unchanged. Plain JSON is still the default outside this explicit storage profile.

Migration 2 adds the format discriminator and labels existing rows as format 1 without rewriting
their evidence. Original migration fingerprints and receipt IDs remain unchanged. Format 1 and
unknown formats throw `NotSupportedException`, including when found through a duplicate or creation
index. They are never treated as absent operations. Upgrade retained evidence with its original
shape and original execution references before retrying. There is no automatic converter because
plain JSON did not retain every scalar kind; conversion requires source evidence, regeneration of
canonical payload/hash and commit fingerprint, and an explicit reviewed migration. Preserve operation
and emission identities throughout. Do not delete receipts to make retries succeed.

The default maximum is 16 MiB of canonical JSON per receipt, configurable through
`maximumReceiptBytes`. Serialization can materialize a larger candidate before rejecting it, but
that failure rolls back every database write. Reads check stored length before copying the BLOB
into managed receipt storage. Reducing this limit below existing receipt sizes makes those reads
fail; it does not truncate evidence. This limit is also the aggregate canonical-byte budget per
outbox page, not a bound on total managed object overhead.

## Read and deliver

```csharp
var page = await repository.ReadOutbox(context, afterSequence: savedCursor, maximumCommits: 100);
foreach (var entry in page)
{
    foreach (var envelope in entry.Commit.Envelopes)
        await DeliverUsingCanonicalIdentity(envelope);
    // Persist the application's delivery progress only after the complete commit was handled.
    savedCursor = entry.Sequence;
}
```

The cursor is exclusive, nonnegative, and local to this repository/database history. Sequence gaps
are allowed, including those occupied by Process receipts. Pages contain whole direct commits in
sequence order, up to 1,000 commits and the configured byte budget. A short nonempty page may have
hit the byte budget; resume after its last sequence. An empty page means caught up at that read
snapshot. An oversized first receipt fails explicitly rather than appearing as an empty page.

There is no general entity-outbox reader/acknowledgment port to implement in the current storage
contracts, so this bounded reader is adapter-specific. It does not acknowledge, claim, delete,
lease, or dispatch entries. Application delivery checkpoints, retries, and consumer deduplication
must use the canonical emission/idempotency identities. A failure after external delivery but
before checkpointing can redeliver. Persistence alone does not guarantee exactly-once external
effects. Automated pruning, retention/compaction, and a background dispatcher are deferred; all
receipts and identity indexes remain retained for replay.

## Failure and recovery boundary

Cancellation is cooperative between native operations and immediately before commit. An already
successful commit remains successful if cancellation arrives afterward. Provider lock retry is
bounded by the configured timeout; the repository does not retry whole transactions automatically.
The database's FULL/NORMAL durability choice remains explicit. No cross-file or distributed commit,
Process execution engine, or distributed lease guarantee is introduced.

Real-file tests cover direct and Process duplicate races, changed identity content, stale fences,
creation uniqueness, cancellation inside the write transaction, late envelope/receipt/creation-index
failure, byte limits, corruption, cursor paging, lost acknowledgment, and replay after later writes.
A separate child-process test kills a writer with dirty state and receipt pages before commit,
then reopens the file and verifies WAL recovery and replay. That test checks abrupt native recovery;
the repository transaction paths are independently exercised by the late-failure tests.

The [Ito adoption proof](ADOPTION.md) adds shared repository conformance, persisted exact-definition
decision replay, and a specialized three-table publication transaction fixture.
