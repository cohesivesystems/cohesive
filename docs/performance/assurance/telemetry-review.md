# Telemetry contract comparison review

Run `dotnet run --project eng/assurance-telemetry-review-bench -c Release`.

This focused before/after measurement reproduces #576's original two-contract canonical JSON
comparison and compares it with `TraceObservationContract.SameContent`. Each contract has two
partitions per requirement. Expected and received contracts have separately allocated references
and arrays; setup is excluded. Each case warms 200 calls, then measures 2,000 calls with
`Stopwatch` and per-thread allocated bytes. No external benchmark package is required.

Local .NET 10 Release run, microseconds and allocated bytes per comparison:

| Requirements | JSON comparison | SameContent | JSON allocation | SameContent allocation |
| --- | ---: | ---: | ---: | ---: |
| 1 | 14.008 µs | 0.105 µs | 1,984 B | 0 B |
| 16 | 93.263 µs | 0.976 µs | 6,384 B | 0 B |
| 128 | 450.042 µs | 11.598 µs | 39,649 B | 0 B |

This isolates contract comparison, not end-to-end capture. Decoding, immutable capture receipts,
observation retention and batch re-evaluation still allocate and may dominate ingestion. Results
are a single local run, not a throughput guarantee. The semantic reason for the change is one
core-owned comparison rule; the speed/allocation improvement follows from removing serialization.
Exact sequence order is preserved, including requirement and partition order. This does not claim
semantic equivalence of reordered contracts.

After extracting the ordered array traversal into the shared internal helper, a repeat run measured
0.121 / 1.449 / 12.292 µs for 1 / 16 / 128 requirements, still 0 B per comparison. Corresponding
JSON baselines were 13.178 / 94.416 / 430.855 µs with unchanged allocation. These local runs are
not statistically controlled; the helper preserves the allocation result without claiming an additional speedup.
