# SQLite typed row reading

`SqliteTypedRowBenchmarks` compares cached provider ordinals, the typed SQLite reader, and the canonical ordinal
conversion path using one shared synthetic relation fixture. Query compilation, schema creation, execution and
positioning are outside timing. Each invocation returns the same three-field CLR record (integer, string, bytes)
and owns its byte array. Canonical conversion snapshots the provider bytes into an immutable observation and
copies them back into mutable CLR output. Typed reading transfers the provider's owned BLOB buffer directly.

Run from the repository root:

```sh
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- \
  --filter '*SqliteTypedRowBenchmarks*' --job short
```

The retained reports were collected on an Apple M5 Max, macOS 26.6.2, .NET 10.0.5 / SDK 10.0.201, Arm64, with
`DOTNET_PROCESSOR_COUNT=2` and no concurrent local builds or tests. Before/after reports isolate removing captured
callbacks from `ValueContractSemantics.Evaluate`; the fixture and owned-byte path are identical in both.
The [initial probe](results/sqlite-typed-rows-before.csv) used 1,024 invocations, five measured iterations and three
warmups; the [final report](results/sqlite-typed-rows-after.csv) uses BenchmarkDotNet's normal ShortRun calibration
with three measured iterations and three warmups. Compare allocation across those runs, not their timing.
These local samples describe current-row materialization, not database/query latency or concurrent throughput.

| Payload bytes | Direct ordinal mean / bytes allocated | Typed mean / bytes allocated | Canonical ordinal mean / bytes allocated |
| --- | ---: | ---: | ---: |
| 0 | 128 ns / 72 B | 419 ns / 192 B | 381 ns / 168 B |
| 256 | 167 ns / 352 B | 455 ns / 472 B | 430 ns / 1,008 B |
| 65,536 | 1.47 µs / 65,632 B | 1.76 µs / 65,752 B | 5.76 µs / 196,848 B |

The typed path adds about 0.29 µs and 120 B per row relative to direct getters in this fixture. Removing captured
validation callbacks cuts another 992 B per typed row: the initial implementation allocated 1,184 / 1,464 /
66,744 B at these payload sizes. Owned-byte reading avoids the two additional payload-sized arrays in canonical
conversion. The canonical comparison calls the field materializer directly, excluding the typed reader's separate
whole-binding presence check (24 B); it is not a claim of equivalent whole-row validation. Small payloads show no
latency advantage over that canonical path. Retaining one owned payload is the intended allocation guarantee.

[Portable validation results](results/value-contract-validation.csv) report no managed allocation: about 8 ns for
one scalar, 64 ns for eight nested objects, 1.18 µs for 64 two-level objects and 75.35 µs for 4,096 such objects.
ShortRun confidence intervals are broad for tiny operations; the deterministic allocation tests are the regression
gate, not these timing values.

The reader adds presence and scalar-contract validation relative to unchecked provider getters. Its mapping and
layout are shared; each database operation allocates one borrowed reader, and each returned row owns its mutable
data. The direct byte path applies only to standard scalar bytes-to-`byte[]` mappings. Custom converters and
custom serializer policy preserve existing canonical conversion behavior.

Deterministic tests enforce one payload-sized allocation plus at most 256 B fixed row overhead at 0, 256 and 65,536
bytes, buffer independence across reads/disposal, and the default core reader's ownership semantics. Separate
tests cover every portable type case and bounded temporary allocation when validating nested collections of
1, 64 and 4,096 objects. The canonical IR, SQLite encodings and compiled result metadata remain authoritative;
no alternate property/type catalog or per-row reflection is introduced.
