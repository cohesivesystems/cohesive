# Native string annotations

## Boundary and semantics

An EDI compiler emits many string-valued annotations, including text containing canonical provenance JSON.
`AnnotationValue.FromString` previously serialized each string to a JSON element immediately. The graph's
writer then decoded that string again to serialize the graph. The same work recurs even when no caller
needs a JSON element. This is representation work inside Core's existing annotation value, not another
protocol model or an Ari cache.

Keep the immutable authoring string directly and write it through the owning `Utf8JsonWriter`. The public
`Value` compatibility view is lazy and owned, with atomic publication. Concurrent first view requests may
prepare redundant snapshots; all callers receive the published view. No global cache retains annotations.
JSON-backed annotations pay one additional reference field; they remain unboxed and retain their original
owned snapshots and escape spelling. Numbers, booleans, arrays and imported/custom-converted values retain
the existing JSON representation. Invalid UTF-16 authoring strings and null retain serializer normalization.

The existing converter, flattening helper and object authoring use the native string where possible.
Structural equality handles native/parsed strings symmetrically. Hashing retains the existing canonical
hash authority and can materialize the JSON view on first use; it does not introduce another digest format.
Custom serializer converters continue to control their own values. Shared-map admission and strict duplicate
checks are unchanged. Contextual validation remains independent of representation reuse.

Using `ObservationValue` as an additional backing tree would add another conversion and would change
lexical number/imported-token behavior. Extending the existing annotation value is the smaller fit. This
change deliberately leaves imported annotation-map decoding and protocol canonicalization visible in the
profile instead of weakening their ownership or converter contracts.

## Evidence

Measured October 16, 2026, Apple M5 Max arm64, macOS 27.0.1, SDK 10.0.402, runtime 10.0.13, Release.
The control uses exact alpha.142 artifacts from successful all-family publication run
[38946004526](https://github.com/cohesivesystems/cohesive/actions/runs/38946004526), merged Core
`22ac652a337566ebf4649ed2ce4be75c5ab0073d`. NuGet processing delayed public-index availability;
the artifacts were downloaded from that run, not packed locally. Normal public-feed CI is still required
for Ari's adoption merge. Candidate Core is `0992029c5e7d3dc01497a864eb41a88a60074ba8`; both sides use
Ari `07ab81b1e609d17ab0ecd5a55bfa4c0ff7f0908a`. Source runtime and matching generator substitutions are
qualification tooling only. The annotation candidate is not published.

Five alternating fresh-process pairs run each original X12 case twice, sharing process preparation/pools
but using fresh repositories and fixtures. Whole-case counters are precise cumulative managed allocations,
not retained heap. No task builds/tests overlapped collection. MB means decimal megabytes.

| Original case | Alpha.142 repeated MB | Native strings MB | Saving |
| --- | ---: | ---: | ---: |
| X12 004010 204 | 45.91 | 43.40 | 2.51 MB / 5.5% |
| X12 005030 204 | 52.28 | 49.26 | 3.02 MB / 5.8% |

Cold medians: 190.67 → 185.08 MB (5.60 MB / 2.9%), and 196.18 → 189.58 MB
(6.61 MB / 3.4%). These are new matched comparisons; the earlier prepared-projection measurements
used runtime 10.0.5 and should not be subtracted directly from these runtime 10.0.13 runs.
[Raw counters](evidence/native-string-annotations/counters.csv),
[matching graph digests](evidence/native-string-annotations/control-graph-hashes.csv), and
[module identities](evidence/native-string-annotations/assemblies.csv) are checked in.
No application or CI latency reduction is claimed.

| Authored string length | Release bytes/operation | Native bytes/operation |
| --- | ---: | ---: |
| 32 | 216 | 48 |
| 1,024 | 1,208 | 48 |

The micro controls use the same entry point with frozen release runtime DLLs; they are runtime comparisons,
not separate package project rebuilds. Output storage is reused. See the
[control](evidence/native-string-annotations/micro-control.csv) and
[candidate](evidence/native-string-annotations/micro-candidate.csv). A 96% allocation saving on the 1 KiB
micro is not a 96% X12 saving.

Qualification passed **4,745 Core tests** (41 environment skips), plus nine focused final string-annotation
cases and two BenchmarkDotNet Dry smoke cases. Both release and candidate Ari solution runs passed
**1,352 tests** each (18 emulator skips); the release npm adoption also passed lint, hosting, production
build and all **35 Playwright tests**. Generated API/TypeScript artifacts and both X12 graph digests are
unchanged. Dry timings are execution smoke checks, not performance evidence.

Reproduce the opt-in micro counter after building `eng/runtime-performance/value-conversion` in Release:

```sh
dotnet run --project eng/runtime-performance/value-conversion -c Release -- --annotation-counters
```

It measures public authoring plus serialization with reusable output storage, seven-run medians after
20 warm calls, 128 operations per sample, for 32/1,024-character strings. The regular test enforces a
96-byte ceiling per authored/written string, independent of payload size. The existing 32-entry complex
map ceiling remains unchanged to guard the added reference-field tradeoff.


## Remaining work in matched traces

The unchanged alpha.139 attribution rules and alpha.141 summarizer put the remaining candidate allocations
at approximately 11.50/13.29 MB for observation projection, 8.75/10.63 MB for protocol JSON, and
6.66/7.76 MB for hosted decode. Other EDI compilation drops from 8.86/11.40 to 7.06/7.56 MB.
Those are disjoint sampled categories, not precise per-method counters.

String-authoring frame samples fall substantially; imported `AnnotationMapJsonConverter.Read` remains
about 4.36/4.89 MB in the candidate. That is a view inside the hosted-decode category, not another budget
to add to it. The next representation target is imported annotation/graph decoding, alongside protocol
canonicalization. This change has not removed those costs or claimed that every remaining allocation is avoidable.

[Trace budgets](evidence/native-string-annotations/allocation-budget.csv) and
[quality checks](evidence/native-string-annotations/trace-quality.csv) record all four runs. Sampled totals
are within 0.22% of each trace's own precise repeated counter. One 005030 control sample (106,600 bytes,
0.20% of the repeated trace) lacks a stack and remains in Other; all other stacks resolve and no events
were lost. Treat small category differences as sampling variation. Raw EventPipe traces remain local;
the report uses the same checked-in analyzer and attribution rules as prior reports.
