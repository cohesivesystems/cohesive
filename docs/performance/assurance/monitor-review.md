# Reference monitor replay and fingerprint review

Measured locally on 2026-11-15 in Release/.NET 10 using the same machine and harness,
before changes at `c799c4e2` and after the review changes. Each operation received five
warmups and 30 measured invocations. These are small diagnostic measurements, not a
statistical throughput qualification; timing depends on the local runtime and machine.

| Operation | Retained rows | Before ms/op | After ms/op | Before bytes/op | After bytes/op |
| --- | ---: | ---: | ---: | ---: | ---: |
| Replay current reference, last occurrence | 100 | 0.1635 | 0.0068 | 116,844 | 14,524 |
| Replay current reference, last occurrence | 2,000 | 3.7149 | 0.1473 | 2,295,361 | 293,762 |
| Repeated Reference() | 100 | 1.4317 | <0.0001 | 27,225 | 1 |
| Repeated Reference() | 2,000 | 21.6238 | <0.0001 | 553,572 | 1 |

The approximately one byte per reference invocation is amortized Stopwatch allocation
in this harness, not an allocation in the cached lookup. First-use fingerprint construction
is excluded by warmup and still serializes/hashes the full canonical checkpoint. A new
snapshot must compute its own fingerprint; cached state never enters the persisted artifact.

Replay validates incoming records and continuity before returning the current immutable
snapshot. It still constructs identity/frontier indexes over retained history; it is not O(1).
The improvement removes full batch interpretation and result materialization for an unchanged
current-reference replay. New or changed updates continue to use the batch interpreter.

Reproduce the harness on the selected implementation revision:

```sh
dotnet run --project eng/assurance-monitor-review-bench/bench.csproj -c Release
```

For the baseline, use the same harness with the Assurance source at `c799c4e2`.
No external benchmark dependency was added.
