# Durable Process bookkeeping

## Contract

Cohesive.Storage owns checkpoint construction, atomic publication, revision checks, worker fencing and retry
receipts. This change extends those existing boundaries; it adds no Ari-owned durability authority or cache.

A worker defaults to `ProcessCheckpointVerification.Standard`. Activations advance their exact attempt by one
sequence; the reducer creates the receipt and continuation together. The store checks receipt chronology,
retained evidence, legal successors, expected storage revision and live worker fencing before atomic publication.
It does not fingerprint the before/after continuation for each activation. This deliberately removes the extra
content-chain check on that path; sequences alone do not prove that an arbitrary continuation has the expected
content. Persisted checkpoints still undergo schema, contract, plan, receipt and ledger admission validation.

`ProcessCheckpointVerification.Diagnostic` additionally records and checks paired before/after continuation
fingerprints. Declared diagnostic evidence is checked when loading a checkpoint regardless of the worker's
current policy. Switching policy affects future receipts; it cannot reconstruct evidence omitted by earlier
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
the checkpoint. This metadata can still be large when local mutations contain large values. The existing replay
snapshot retains the exact accepted checkpoint; no additional copy or historical checkpoint cache is introduced.

Only an ID collision compares the incoming checkpoint against the retained accepted snapshot, plus the retained
intent digest for non-initialization commits. Exact retries return the original snapshot even after later commits;
changed content returns `IdentityConflict`. Different-ID writes must still pass current revision, fence and lease
checks. The provider-neutral receipt document preserves this evidence across restart. The SQLite adapter delegates
to the same semantics inside its transaction. Full `ProcessDurableCommit.Fingerprint` evidence remains available
lazily for explicit diagnostics; reading that property intentionally incurs full hashing.

Safe-point and activation commit identities use instance, attempt, activation ID and the prior activation count.
Affinity commits use their control revision and slot; operation-ledger commits use operation ID and storage
revision. Small identity derivation uses the runtime/v2 domain; it no longer traverses payload content.

This intentionally replaces checkpoint schema v6 with v7 and durable-store schema v1 with v2. Runtime-derived IDs
also change. Older checkpoint/store documents are rejected. Recompile source consumers; no wire migration,
compatibility reader or package publication is included in this change.

## Qualification

The regression suite covers both policies, retained Diagnostic tampering checks, paired evidence, restart and
later-commit collisions, altered checkpoint/metadata/local mutations, plus fixed median-of-seven allocation
ceilings for fresh commit construction and initialization with 128 KiB and 1 MiB scalar inputs. Existing crash,
stale-fence, lease, cancellation, replay and malformed-checkpoint tests remain authoritative. Materialization
conformance tests explicitly select Diagnostic mode when comparing retained continuation hash chains.

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
Both harnesses use matched source assemblies; candidate Core, Processes, Relations and Storage DLL hashes were
checked against the qualified build outputs. Five alternating fresh-process pairs per X12 case measure cumulative
process-wide allocations, including fixture/seed setup, preview, compilation, execution and assertions. This is
neither retained heap nor one isolated compiler invocation. MB below means decimal megabytes.

| X12 case | Cold control | Cold candidate | Repeated control | Repeated candidate |
|---|---:|---:|---:|---:|
| 004010 204 | 233.96 MB | 194.42 MB | 50.95 MB | 50.59 MB |
| 005030 204 | 240.02 MB | 200.43 MB | 58.07 MB | 57.71 MB |

Median cold allocation falls 39.54 / 39.59 MB (16.9% / 16.5%). Repeated allocation falls only 0.37 / 0.36 MB
(0.72% / 0.62%). Removing hashing reduces cold temporary-buffer demand, but much of its warm buffer demand was
already pooled; warm allocation is still dominated by other work. No total-case latency or CPU-share reduction
is claimed from the five process samples. Both pinned X12 graph content digests remain identical to control.

The isolated structured-row run allocates 624 B to construct a commit and 1,376 B to initialize a store at both
sizes. Explicit full hashing allocates about 49.9 KB / 509.0 KB. BenchmarkDotNet ShortRun measured full-hash
preparation at 3.02 / 24.22 ms versus about 53 ns for commit construction. These are different amounts of work;
this demonstrates removal of the traversal, not an equally cheap replacement content hash. Total execution still
scales with its input and explicit admission.

Evidence: [alternating X12 samples](evidence/durable-bookkeeping/x12-matched.csv),
[structured allocation samples](evidence/durable-bookkeeping/scaling.csv),
[BenchmarkDotNet report](evidence/durable-bookkeeping/commit-preparation-benchmark.md).
Use `eng/runtime-performance/durable-bookkeeping/compare-ari.py --baseline <control/ProtocolPreparation.dll>
--candidate <candidate/ProtocolPreparation.dll> --output <samples.csv>` for the same pairing procedure.

Validation: solution command 4,644 passed / 41 environment skips; native SQLite 220 passed; Ari engine source
qualification 936 passed / 18 emulator skips. Restoring eager hash preparation temporarily makes both allocation
regressions fail at 3,552 B against the 2,048 B construction cap; optimized code passes. The old mechanism is
restored only for that counterexample, never in the final build.

## Remaining work

This is the first runtime-bookkeeping slice. It does not add Merkle hashing, remove duplicated artifact payloads,
change typed conversion boundaries or share preview compilation. Local mutation digests, explicit admission and
collision comparisons, zero-activation clean-state verification and some restored successor comparisons still
hash content. Existing receipt snapshots still grow with retained history, and the bounded SQLite profile still
rewrites its complete authority document. Smaller artifact references and representation lifetime are subsequent
changes; this PR does not claim payload-independent total execution or storage cost.
