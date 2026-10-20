# CPU follow-up: the short repeated window was still warming up

The original default-runtime cases 9–16 showed higher CPU for #460. Those numbers are real,
but they are not steady-state measurements. Extending the same X12 test shows substantial tiered
compilation throughout hundreds of cases. A transient slowdown remains visible early in some
processes; after compilation settles, the long 004010 comparisons reverse it in both execution
orders. No runtime/library code change was justified by this investigation.

## Matched long comparison

The frozen runtime is the same verified alpha.152 baseline and #460 candidate (`1baafb3b`)
qualified in the [overview](README.md). Only Core and Relations DLLs differ. The original Ari
`CanonicalProcessCommitsDataDefinedSpecThroughRepositoryTransitions` test is unmodified.
An isolated harness retains its measurement loop and trace markers and raises the CLI iteration
cap. Both variants in each pair use identical harness bytes. No package cache is modified.

The late window is cases **1537–2048** of 004010. These are two independent process pairs, with
opposite orders, not 512 independent benchmark samples. CPU includes JIT and GC; nothing is
subtracted. Values are per-process medians, with mean CPU retained alongside them.

| Process order | CPU control → candidate | Mean CPU control → candidate | Elapsed control → candidate | Allocation control → candidate |
| --- | --- | --- | --- | --- |
| Candidate first | 17.363 → 15.912 ms | 17.516 → 16.003 ms | 17.328 → 15.889 ms | 4,941,796 → 4,378,208 B |
| Control first | 17.962 → 15.645 ms | 18.029 → 15.873 ms | 17.928 → 15.592 ms | 4,941,680 → 4,377,776 B |

Total JIT time over each 512-case late window is 0–0.35 ms. In the first pair, the earlier
897–1024 window has exactly zero JIT time in both versions and CPU is 16.833 → 15.080 ms.
The same pair **reproduces** the early slowdown at cases 9–16: 49.005 → 60.423 ms CPU.
Thus the early penalty and the later gain coexist in the same matched experiment.
This establishes no persistent CPU penalty in this qualified long workload, not a universal
8–13% product-latency improvement or a promise about other sources/hosts.

## Why the previous result was misleading

- Five new default-runtime pairs/source at 64 cases do not preserve the original ranking:
  004010 cases 9–16 become 52.724 → 51.278 ms CPU; 005030 is 60.368 → 62.841 ms.
  By cases 49–64 the corresponding values are 46.068 → 43.816 and 50.099 → 48.323 ms.
- Three 64-case pairs/source with `DOTNET_TieredPGO=0` still show an early penalty, but the last
  window is faster: 23.084 → 21.433 ms (004010) and 25.012 → 23.700 ms (005030).
  Disabling PGO alone does not disable tiered compilation. This is a diagnostic intervention,
  not a recommended deployment configuration.
- Three default-runtime 512-case pairs/source still include large compilation transitions.
  For 004010 cases 385–512, process CPU is 16.921 → 20.009 ms while elapsed is slightly lower
  at 16.427 → 15.991 ms. The candidate still has measured JIT activity in that window.
  005030 late CPU is 18.105 → 18.008 ms; its elapsed times are 17.984 → 18.007 ms.

These observations are consistent with different tier-promotion/optimization timing, rather
than an increased steady-state cost in ID normalization. The exact generated method responsible
for each transient was not isolated; compiler time alone does not explain every early slowdown.
Do not subtract JIT counters to manufacture an improvement or silently drop unfavorable runs.
Cold and early repeated-call behavior remains part of the qualification evidence.

## CPU stack evidence

Eight 64-case thread-time/JIT traces cover two alternating pairs/source. All 512 traced cases
pass; there are zero lost events and zero missing managed stacks. For cases 9–64, the changed ID
handling paths account for 193 → 65 managed samples (004010) and 203 → 79 (005030).
The larger differences sit in unchanged canonical JSON, observation serialization, protocol and
runtime paths. Samples are diagnostic counts, not measured CPU milliseconds or causal attribution;
external samples are excluded, and sampled safepoint/lock frames must not be treated as GC duration.

The 005030 source was not extended to 2048 cases; its three 512-case pairs and two trace pairs
provide narrower evidence. No production PGO setting, inlining attribute, cache or semantic
behavior was changed. The allocation guards, golden digests and previously green Core/Relations/
Ari suites remain applicable because runtime code is unchanged.

## Reproduction and evidence

[Per-process windows](cpu-process-windows.csv) retain favorable and unfavorable runs, medians,
mean CPU, allocation, GC and total JIT work. [Trace groups](cpu-trace-groups.csv) are disjoint
semantic groups, not overlapping inclusive totals. [The manifest](cpu-manifest.json) records
module/harness identities, counter hashes and all **16,384 successful untraced cases**.
Full raw counters and traces remain local; the following runner reproduces the experiment with
qualified frozen runtimes and .NET 10 SDK installed:

```sh
python3 cpu_compare.py --control /path/to/control --candidate /path/to/candidate \
  --output /tmp/query-identity-cpu-recheck --iterations 2048 --runs 1 \
  --sources x12-004010-204 --candidate-first \
  --ari-program /path/to/ari/eng/runtime-performance/protocol-preparation/Program.cs
```

Repeat without `--candidate-first` for the opposite order. Use `--iterations 64 --runs 3
--mode pgo-off` with both default sources for the diagnostic. Builds finish before measurement;
run other tests, benchmarks and tracing separately. The runner fails if DLL differences extend
beyond the two qualified modules or ambient tiering settings would contaminate the comparison.
