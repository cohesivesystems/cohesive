# Durable Process bookkeeping

## Contract

Cohesive.Storage owns checkpoint construction, atomic publication, revision checks, worker fencing and retry
receipts. This change extends those existing boundaries; it adds no Ari-owned durability authority or cache.

A worker defaults to `ProcessCheckpointVerification.Standard`. Activations advance their exact attempt by one
sequence; the reducer creates the receipt and continuation together. The store checks receipt chronology,
retained evidence, legal successors, expected storage revision and live worker fencing before atomic publication.
Both modes require the first activation's before-continuation fingerprint. Restart admission compares it with the
clean continuation for the pinned definition and input, including after that attempt has progressed. An absent
first receipt is allowed only when no completed activation is recorded: safe points or a progressed current
continuation require the receipt. Attempts restarted before completing any activation remain valid. Standard
omits recurring before/after content-chain checks; sequences alone do not prove arbitrary continuation content.
Persisted checkpoints still undergo schema, contract, plan, receipt and ledger admission validation.

`ProcessCheckpointVerification.Diagnostic` additionally requires before/after continuation fingerprints on every
receipt. Each receipt declares its policy independently of the hashes; missing policy or required evidence is
rejected, including deletion of both Diagnostic hashes. Admission checks the retained policy regardless of the
worker's current options. Switching policy affects future receipts; it cannot reconstruct evidence omitted by
Standard activations. Both modes use identical persistence boundaries, CAS, fences and retry-conflict semantics.
There is no checkpoint interval setting and no new exactly-once guarantee for external side effects.

```csharp
var options = new ProcessDurableRuntimeOptions(
    workerId: "worker/compile-1",
    workerLease: TimeSpan.FromMinutes(5),
    checkpointVerification: ProcessCheckpointVerification.Diagnostic);
```

## Commit identity and replay

Previously commit construction serialized and hashed the entire checkpoint, including all earlier receipts and
repeated large payloads. Every successful initialization also hashed the initial checkpoint.

Normal initialization now retains its accepted snapshot without hashing it. A normal commit retains a
`sha256-intent-v2` digest of ID, expected revision, owner, fence, local mutations and observation time, excluding
the checkpoint. Construction computes this digest once, before the provider gate or transaction; there is no Lazy
wrapper. A named record with explicit JSON names defines the persisted digest, pinned by golden canonical bytes
and a literal digest test. This metadata can still be large when local mutations contain large values. The existing replay
snapshot retains the exact accepted checkpoint; no additional copy or historical checkpoint cache is introduced.

Only an ID collision compares the incoming checkpoint against the retained accepted snapshot, plus the retained
intent digest for non-initialization commits. Exact retries return the original snapshot even after later commits;
changed content returns `IdentityConflict`. Different-ID writes must still pass current revision, fence and lease
checks. The provider-neutral receipt document preserves this evidence across restart. The in-memory store captures
immutable, append-only replay evidence under its gate and compares checkpoint content after releasing the gate.
SQLite uses a deferred WAL read transaction for initialization and commit: replay comparison does not reserve
the writer. A changed mutation upgrades to a write transaction; an invalidated snapshot rolls back and retries
against fresh state, at most three attempts. This covers primary `SQLITE_BUSY` (5) and `SQLITE_LOCKED` (6),
including their extended variants such as `BUSY_SNAPSHOT` (517). Each attempt uses the configured native busy
timeout; cancellation is checked before retry and unrelated native failures propagate immediately. The commit
rechecks the physical lease after obtaining the writer reservation and rolls back staged state and receipt if the lease expired. Other mutations retain immediate
transactions. Full commit hashing is an internal reference operation for tests and benchmarks, not a public API.
A source audit of Cohesive and the qualifying Ari checkout found no production or diagnostic consumer;
`StorageExecutionTelemetry` and the durable status projector consume checkpoint state without this digest.

Safe-point and activation commit identities use instance, attempt, activation ID and the prior activation count.
Affinity commits use their control revision and slot; operation-ledger commits use operation ID and storage
revision. Small identity derivation uses the runtime/v2 domain; it no longer traverses payload content.

This intentionally replaces checkpoint schema v6 with v7 and durable-store schema v1 with v2. Runtime-derived IDs
also change. Older checkpoint/store documents are rejected. Recompile source consumers; no wire migration,
compatibility reader or package publication is included in this change.

## Qualification

The regression suite covers both policies, retained Diagnostic tampering and deletion checks, missing policy,
clean-start compatibility after restart, later-commit collisions, altered checkpoint/metadata/local mutations,
and the named intent digest contract. Median-of-seven allocation ceilings cover construction and
initialization (3 KiB each) with 1,024 and 8,192 fields of 128 characters each. Existing crash, stale-fence, lease,
cancellation, replay and malformed-checkpoint tests remain authoritative. Materialization conformance defaults
to Standard, exercises crash/replay in both modes, and always compares final state with the reference interpreter.
SQLite tests also cover replay while another writer reservation remains held and eight concurrent identical
initializations/commits. Injected native failures at the transaction boundary cover busy/locked variants,
fresh-state retries, exhaustion, cancellation, and propagation of unrelated failures without retry.

The opt-in structured workload uses 1,024 and 8,192 four-field object rows. Setup and compilation are outside the
measurement. Full-hash preparation is an explicit same-build reference, not a claim of historical timing equivalence:

```bash
dotnet run --project eng/runtime-performance/durable-bookkeeping -c Release -- --allocations
dotnet run --project eng/runtime-performance/durable-bookkeeping -c Release -- --filter '*CommitPreparation*'
```

For cross-repository X12 qualification, build `src/Cohesive.Tests` and rebuild Ari's
`eng/runtime-performance/protocol-preparation` harness with the checked-in `UseSourceAssemblies.targets` supplied
as `CustomAfterMicrosoftCommonTargets` and the built test output directory as `CohesiveSourceAssemblyDirectory`.
Use separate output directories for baseline and candidate. This transient source substitution avoids publishing
unmerged packages; it is not a deployment mechanism. No source-substitution targets or local feed are committed to Ari.

Measured on 2026-10-15, Apple M5 Max / Arm64, macOS 27.0.1, .NET SDK 10.0.201 / runtime 10.0.5.
Control: Cohesive `7622449150b3f272463481b4fa5645b33625b4ab`. Candidate: this change on that parent.
Ari: `e1104c9013eb7b6a38f66672f2283b70b2739d2a` (the alpha.140 upgrade source; no Ari code edits).
Both harnesses are isolated copies of their respective source builds. Five alternating fresh-process pairs per
X12 case measure cumulative
process-wide allocations, including fixture/seed setup, preview, compilation, execution and assertions. This is
neither retained heap nor one isolated compiler invocation. MB below means decimal megabytes.

| X12 case | Cold control | Cold candidate | Repeated control | Repeated candidate |
|---|---:|---:|---:|---:|
| 004010 204 | 234.24 MB | 194.60 MB | 50.96 MB | 50.60 MB |
| 005030 204 | 239.95 MB | 200.43 MB | 58.01 MB | 57.75 MB |

Median cold allocation falls 39.64 / 39.53 MB (16.9% / 16.5%). Repeated allocation falls only 0.36 / 0.26 MB
(0.70% / 0.45%). Removing hashing reduces cold temporary-buffer demand, but much of its warm buffer demand was
already pooled; warm allocation is still dominated by other work. No total-case latency or CPU-share reduction
is claimed from the five process samples. Both pinned X12 graph content digests remain identical to control.

The isolated structured-row run allocates 1,568 B to construct a commit, including its small eager intent digest,
and 1,376 B to initialize a store at both sizes. Explicit full hashing allocates about 50.8 KB / 509.9 KB.
BenchmarkDotNet ShortRun measured full-hash preparation at 3.04 / 24.51 ms versus about 898 / 916 ns for commit
construction. These are different amounts of work; this demonstrates removal of the full traversal, not an equally
cheap replacement content hash. Total execution still scales with its input and explicit admission.

Evidence: [alternating X12 samples](evidence/durable-bookkeeping/x12-matched.csv),
[structured allocation samples](evidence/durable-bookkeeping/scaling.csv),
[BenchmarkDotNet report](evidence/durable-bookkeeping/commit-preparation-benchmark.md).
Use `eng/runtime-performance/durable-bookkeeping/compare-ari.py --baseline <control/ProtocolPreparation.dll>
--candidate <candidate/ProtocolPreparation.dll> --output <samples.csv>` for the same pairing procedure.

Validation: solution command 4,653 passed / 41 environment skips; native SQLite 233 passed; Ari engine source
qualification 936 passed / 18 emulator skips. Temporarily restoring eager commit hashing fails both sized
construction cases at 4,448 B against the 3,072 B ceiling; restoring initialization hashing fails both sized
initialization cases at 4,160 B against the same ceiling. Optimized source passes after restoration.
One earlier full run failed the unchanged single-sample normalization allocation ratio; its isolated recheck
and the subsequent full run passed. That test remains sensitive to allocation noise.

## Remaining work

This is the first runtime-bookkeeping slice. It does not add Merkle hashing, remove duplicated artifact payloads,
change typed conversion boundaries or share preview compilation. Local mutation digests, explicit admission and
collision comparisons, zero-activation clean-state verification and some restored successor comparisons still
hash content. Existing receipt snapshots still grow with retained history, and the bounded SQLite profile still
rewrites its complete authority document. Smaller artifact references and representation lifetime are subsequent
changes; this PR does not claim payload-independent total execution or storage cost.
