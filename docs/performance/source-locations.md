# Structured source-location costs

Measured 2026-11-04 with BenchmarkDotNet 0.15.8 on Apple M5 Max, macOS 27.0.1 Arm64, .NET 10.0.13.
The [generated report](source-location-benchmarkdotnet.md) records all six cases, uncertainty and allocations.
Reproduce with:

```sh
dotnet run -c Release --project eng/benchmarks/development-mcp -- --filter '*SourceMapBenchmarks*' --warmupCount 3 --iterationCount 8 --launchCount 1
```

Each operation constructs 1, 16 or 256 source-provenance entries, normalizes an execution source map, and produces
its canonical JSON bytes through the existing strict serializer. One branch adds a mapped declaration-line
location to every entry; the other leaves locations absent. Both use the current implementation: this measures
optional metadata cost, not performance versus an older commit. No concurrent builds or tests ran during the
recorded measurements. One launch and a local machine do not establish production tail latency.

| Entries | Reference-only mean | Located mean | Reference-only allocation | Located allocation |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 0.794 us | 1.187 us | 1.75 KiB | 1.88 KiB |
| 16 | 11.100 us | 17.442 us | 26.70 KiB | 28.92 KiB |
| 256 | 207.074 us | 304.494 us | 650.46 KiB | 686.50 KiB |

Structured metadata costs 47–57% more construction/serialization time and 6–8% more allocation in these fixtures,
including larger output. This is an explicit authoring/export cost accepted for reliable source navigation.
The canonical document serializer still owns encoding; no parallel canonical writer was introduced. Each source
provenance record now has an optional location reference, and mapped entries retain their path/range value.

Portable-path validation scans the existing string without per-segment allocations. Unmapped compiler locations
return null; a regression verifies zero per-construct allocation across 1,000 calls with an unmapped path.
This is not an allocation guarantee for full exports, mapped locations, hashing or tool calls.

Locations remain non-semantic metadata. Tests cover deterministic ordering, round-trip preservation, invalid
paths/ranges and unchanged semantic fingerprints. Current producers record one inclusive declaration line; they
do not fabricate full expression spans. Reference lookup retains the existing full-content freshness checks and
runs domain-owned projections/validation over eligible exported documents; this benchmark does not measure that
separate operation or imply a bound on its latency.
