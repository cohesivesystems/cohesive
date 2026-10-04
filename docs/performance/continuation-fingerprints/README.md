# Immutable continuation fingerprint reuse

Storage persists several projections of one immutable `ProcessContinuationState`:
activation evidence, checkpoint state, and commit proposals. In Ari's nine canonical
compilation cases, a temporary identity counter observed 315 fingerprint calls for
207 distinct snapshots. Previously all 315 calls serialized, canonicalized, and
hashed the complete snapshot.

`ProcessStorageContentFingerprints.Continuation` now shares successful sha256-v1
evidence by snapshot object identity. The serializer profile remains the existing
private checkpoint profile. A weak-key table retains only the digest and lazy
preparation; it releases these when the snapshot becomes unreachable. Concurrent
first callers share preparation. Failed preparation is removed and remains
retryable. Distinct snapshots, including deserialized checkpoints, independently
prepare evidence. External checkpoint integrity and compatibility validation
remain mandatory. Invocation inputs, authorization, mutable objects, and results
are not cached.

The responsible existing component is Cohesive.Storage's fingerprint helper;
this extends its preparation lifetime rather than adding an Ari cache or changing
Process semantic authority. Immutable Process continuations remain authoritative;
the digest is derived evidence. A generic fingerprint cache was rejected because
other callers do not have the same immutability and fixed-profile contract.

## Qualification

Six regression tests cover exact canonical SHA equivalence, distinct and changed
snapshots, concurrent reuse, bounded warm allocations, weak lifetime, retryable
serialization failures, and rejection of tampered persisted evidence. The full
core suite passed 4,178 tests with 33 skips before the final persisted-tampering
case was added; all six focused tests passed afterward. Ari's engine suite passed
849 tests with 18 skips in 72 seconds using local dependency-closure packages.

The accompanying BenchmarkDotNet ShortRun report includes both repeated and fresh
snapshots. Warm calls allocate zero bytes. Fresh small snapshots add about
186–192 bytes for cache preparation; timing confidence intervals are broad.
Snapshot construction and validation are included in both fresh measurements.

A single cold Ari X12 204 compilation measurement, including catalog creation,
allocated 2,118,573,448 bytes before reuse and 2,039,046,912 afterward (about 80 MB,
3.8%). Execution alone fell from 795,658,888 to 716,138,400 bytes. Wall time was
5,322 ms versus 5,155 ms; the full engine suite remained approximately 72 seconds.
These samples establish an allocation reduction, not a material end-to-end
latency improvement. Baseline counting instrumentation introduced a small
additional allocation cost and is absent from production source.

Measurements used macOS ARM64, .NET SDK 10.0.201/runtime 10.0.5, Release builds,
and local packages containing PR #392's canonicalization fix in both variants.
No network or emulator work was included in the cold compilation sample.

Run the reusable benchmark with:

```sh
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*ProcessContinuationFingerprintBenchmarks*' --job short
```

The Ari reproduction uses its opt-in `ARI_COMPILATION_PROFILE_DIRECTORY` stage
profiler and `CanonicalProcessCommitsDataDefinedSpecThroughRepositoryTransitions`
filter. Microbenchmark gains apply only to repeated identities; new snapshots
still pay canonicalization and hashing costs.
