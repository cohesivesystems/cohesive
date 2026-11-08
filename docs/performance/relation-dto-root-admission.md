# Ordinal admission for fused in-memory DTO mapping

The fused mapper prepares a CLR projection on first use for each evaluator and exact root type. It
uses the same property metadata and getter order as ordinary CLR projection. A type qualifies when
its entire projected property bag matches the root layout and each property is a string or decimal.
The compiled getters write into one owned vector per root. Admission validates every slot against
the canonical shape, then identity and relation expressions read the bound ordinals. The input is
enumerated once and all roots are admitted before any DTO constructor runs. The owned observations
can also supply the canonical budget-failure path without reading caller properties again.

Derived runtime types, custom JSON conversion, nested properties, and other unsupported projections
use ordinary admission. Shape failures use canonical diagnostics. The same kernel sorts escaped
identities and enforces uniqueness, row budgets, and cancellation. The attributed evaluation API
continues through the canonical interpreter.

## Workload

Each load has one customer and two line items. The declared relation groups the items, joins both
sources, and constructs an array of load summaries. The warm Cohesive case includes CLR root
projection, shape admission, canonical ordering, lookup, and DTO construction. The LINQ control
recomputes its groups and joins on every call. The prepared AutoMapper control uses prebuilt lookups
and CLR inputs in input order. Both AutoMapper controls omit Cohesive's shape admission and identity
ordering. Benchmark setup verifies the complete DTO values for all three paths and verifies canonical
order for Cohesive against the interpreter.

The setup case times evaluator creation and source snapshots. The first-map case uses a fresh
evaluator per iteration, with its setup outside the timing. It measures first use in a warmed
process, including relation preparation and the first DTO batch.

## Results

BenchmarkDotNet 0.15.8, AutoMapper 16.2.0, Apple M5 Max, macOS 27.0.1, .NET 10.0.13 Arm64,
Release. The baseline is merged revision `8fb32626`. Both runs used the same benchmark binaries
and dependencies except for the baseline's `Cohesive` and `Cohesive.Relations` assemblies.
Each run completed all 20 cases.

Warm means and managed allocation per batch. The AutoMapper columns show the candidate run's controls.

| Loads | Cohesive before | Cohesive ordinal | Lower latency | Cohesive allocated, before → ordinal | LINQ + AutoMapper | Prepared + AutoMapper |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 299 ns | 169 ns | 43% | 992 → 560 B | 438 ns | 36 ns |
| 128 | 36.72 µs | 22.28 µs | 39% | 77,171 → 38,130 B | 48.26 µs | 5.41 µs |
| 1,024 | 326.11 µs | 203.11 µs | 38% | 614,863 → 303,431 B | 423.83 µs | 47.16 µs |
| 8,192 | 4.82 ms | 2.76 ms | 43% | 4,916,270 → 2,425,603 B | 11.05 ms | 0.438 ms |

An independent repeat with the original load-placement harness found 37–49% lower latency and
44–51% lower allocation on the same four batch sizes. In the matched repository runs, Gen0
collections per 1,000 calls at 8,192 loads fell from 583 to 266. The prepared AutoMapper
control still has the shortest warm time. At 8,192 loads it finishes in about one-sixth the time
of the Cohesive path, whose result includes fresh shape admission and canonical identity ordering.

Preparation and first-map means in milliseconds.

| Loads | Evaluator setup before | Evaluator setup ordinal | First map before | First map ordinal |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 0.059 | 0.059 | 2.87 | 3.56 |
| 128 | 0.227 | 0.223 | 5.81 | 6.34 |
| 1,024 | 1.697 | 1.673 | 27.00 | 26.94 |
| 8,192 | 25.57 | 24.33 | 208.27 | 207.62 |

First maps that include projection compilation took about 0.5–0.7 ms longer for small batches.
Large first maps remain dominated by preparing the related sources. This version of the warm
path allocated an observation and field storage per root.

The Relations suite passed all 1,231 tests, including getter order, shape and getter diagnostics,
derived-root fallback, budget fallback without re-reading getters, and a warm allocation bound.
The core suite passed 5,380 tests with 68 environment-dependent skips. Both builds treated warnings
as errors.

## Pooled batches

A follow-up profile sampled 607 warm mapping stacks at 8,192 loads. Canonical sorting appeared in
26% of stacks and prepared join dictionary lookups in 23%. Root slot arrays, sort-list arrays,
observation objects and ordinal storage wrappers made up about 80% of sampled allocation weight.

The fused mapper can now use one rented slot vector and one rented sort buffer for an array batch
that fits the plan's row budget and the 65,536-slot scratch cap. The same compiled getters write
directly to the root's offset. Validation reads those slots before any DTO construction, then
the compiled joins read them in canonical order. The mapper clears and returns borrowed storage
on success, cancellation, or failure. The sort compares the same escaped identities.

Inputs with unknown extent or above the scratch cap retain the owned observation path. A batch
that could exceed the row budget retains owned observations for canonical fallback, so that
fallback still reads each caller getter once. Mixed CLR arrays can use pooled slots for an
exact-type root and a canonical observation for its derived neighbors.

The matched repeat uses the per-root ordinal implementation above as its baseline. All 20 cases
completed on each side with the same fixture, SDK and runtime. The AutoMapper columns show the
pooled run's controls.

| Loads | Cohesive before | Cohesive pooled | Cohesive allocated, before → pooled | LINQ + AutoMapper | Prepared + AutoMapper |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 178 ns | 185 ns | 560 → 304 B | 455 ns | 38 ns |
| 128 | 23.12 µs | 20.61 µs | 38,130 → 7,330 B | 48.83 µs | 5.46 µs |
| 1,024 | 211.51 µs | 203.38 µs | 303,431 → 57,520 B | 435.77 µs | 46.99 µs |
| 8,192 | 2.84 ms | 1.86 ms | 2,425,603 → 459,048 B | 11.15 ms | 0.454 ms |

The 8,192-load batch took 34% less time and allocated 81% less. Its managed allocation was also
46% below the prepared AutoMapper control's 852,025 B. Cohesive still took 4.1 times as long as
that control. At one load the warmed operation took 7 ns longer while allocating 256 B less.
The 8,192-load first map was effectively flat at 208.66 versus 208.18 ms.

In a follow-up profile of 522 warm mapping stacks, canonical sorting accounted for 35%, the
prepared join dictionary lookups for 19%, and ordinal validation for 18%. DTO instances and
their result arrays accounted for over 99% of sampled allocation weight. These are sampling
shares within the pooled run.

## Sorting pooled root indexes

This experiment compares the pooled whole-root sort with two ways of leaving admitted roots in
place. The first sorts compact records containing an escaped identity and a row index. The second
uses the .NET paired sort over a string span and an integer span. Both use ordinal comparison of the
same escaped identities. The paired version also tests checking for duplicate identities from the
sorted keys first, reading the original identities only when the escaped keys tie.

The experiment changes the sort implementation for eligible pooled batches. Inputs whose length
is unknown and over-budget inputs still retain owned roots for canonical fallback. A differential
test compares the full ordered DTO arrays for a large batch with escaped identities and inner-join
omissions, then checks the canonical duplicate diagnostic.

The compact record was 6% slower at 8,192 loads in the first run and 5% faster in the reverse run.
The paired string and index spans were 10% faster than the matched whole-root baseline. The final
version retains the paired spans and checks adjacent escaped keys before inspecting the original
identities. Its matched repeat used the same benchmark executable, with only the Relations
assembly changed between variants.

| Loads | Pooled whole-root sort | Pooled paired sort | Cohesive allocation, before → paired | Prepared + AutoMapper |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 169 ns | 167 ns | 304 → 304 B | 38 ns |
| 128 | 20.90 µs | 19.30 µs | 7,329 → 7,329 B | 5.64 µs |
| 1,024 | 200.91 µs | 179.25 µs | 57,518 → 57,517 B | 49.30 µs |
| 8,192 | 1.89 ms | 1.64 ms | 459,041 → 459,026 B | 0.465 ms |

The 8,192-load batch took 13% less time with essentially the same managed allocation. The two
ordering arrays are invocation-local rentals bounded by the existing pooled root limit. The warm
comparison continues to include complete root admission, the two joins, and canonical output order.

In 424 warm CPU samples, sorting appeared in 24% of stacks, prepared dictionary lookups in 25%,
and ordinal validation in 23%. The earlier whole-root profile attributed 35% of samples to sorting.

## Reproduction

```sh
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- \
  --filter '*RelationDtoRootAdmissionBenchmarks*' --job Short --inProcess \
  --iterationCount 12 --warmupCount 4 --iterationTime 175 --memory
```
