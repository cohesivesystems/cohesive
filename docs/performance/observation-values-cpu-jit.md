# ObservationValue small-input CPU and JIT investigation

Measured October 18, 2026 on Apple Arm64, .NET 10.0.13 / SDK 10.0.402.

## Finding: warmup history changes the cost

The flat fixture has **16 numeric fields**, beyond the object builder's **8-field inline
storage** limit. Collection records have only four fields and stay in inline storage. This
investigation concerns untyped wide-object parsing; it does not establish a regression for
all small scalars, narrow objects, or shape-bound ordinal hydration.

The previously reported flat-input regression is not a uniform per-read cache cost.
The in-process benchmark matrix runs collections before flat input and shares tiered JIT
profiles across those workloads. Fresh processes measuring only flat input show a much smaller
gap. Mixed-workload behavior is still real and relevant; it must be reported separately from
isolated workload behavior.

Three fresh-process BenchmarkDotNet runs per library, restricted to the flat case, produced
median means of **542.55 ns current** and **541.00 ns before reuse**, with identical **1,680 B**
allocation. Diagnostic-only variants bypassing cache lookup (544.65 ns) or also removing the
root cleanup wrapper (539.09 ns) did not establish a meaningful isolated-input improvement.
Those variants deliberately remove token string reuse and are discarded as production alternatives.

An independent sustained-loop harness explicitly selects a warmup workload before measuring
flat parsing. Three fresh processes per condition, five seconds of measurement after one second
of warmup, produced these median costs:

| Library | Flat warmup | Collection warmup | Effect of changing warmup |
|---|---:|---:|---:|
| Current accepted baseline | 540.38 ns | 622.47 ns | +15.2% |
| Preserved library before reuse | 529.74 ns | 583.96 ns | +10.2% |

The current/reference gap is 2.0% with flat warmup and 6.6% with collection warmup. Every loop
allocated exactly 1,680 B per operation. This corroborates warmup sensitivity; these sustained
loop measurements are not interchangeable with BenchmarkDotNet latency estimates. Individual
processes vary, and tracing itself changes scheduling/GC behavior.

## Direct JIT evidence

Matched current-library BenchmarkDotNet invocations with JIT disassembly yielded:

| Method, optimized native code | Flat-only process | Collection-first matrix |
|---|---:|---:|
| Root `ReadValue` | 176 B | 1,384 B |
| `ReadScope.ReadValueCore` | 1,532 B | 1,120 B |
| `ReadScope.ReadUntypedObject` | 5,600 B | 3,700 B |

The root frame grows from 80 to 160 bytes when pool-return logic is inlined into its cleanup
funclet. However, forcing only cache disposal out of line shrank that root method to 172 B
without improving the flat matrix timing (618 ns versus 609 ns in the diagnostic control).
That experiment is discarded: smaller generated code alone is not a performance conclusion.

CLR JIT-tracing events identify the more useful boundary in the object loop:

| Inlinee while compiling `ReadUntypedObject` | Flat warmup | Collection warmup |
|---|---|---|
| `GetPreparedToken` cache-table helper | Rejected: `unprofitable inline` | Inlined |
| `Utf8JsonReader.GetString` | At least one decoding path inlined | All four recorded attempts rejected |
| `Dictionary.TryInsert` | Three recorded sites inlined | Three recorded sites rejected: `unprofitable inline` |
| Object builder `Set` | Inlined | Inlined |

These are observed, opposing inlining choices rather than costs inferred from source line count.
They do not prove that cache code exhausted a fixed inlining budget: `unprofitable inline`
can also reflect the learned frequency of a call site. Collection training makes cache reuse
common and the wide-object dictionary path uncommon. Native code and sample attribution change
accordingly. The targeted experiment below checks whether outlining cache mechanics alone
restores performance; it does not establish that fix.

## Sampling evidence and limits

EventPipe managed-stack samples were filtered to the non-inlined `ObservationJsonParseProfile.Measure`
marker, excluding setup and warmup. The same-process disassembly and method-version metadata
allow native instruction offsets to be inspected. Current flat and both mixed captures have
matching same-process disassembly; the reference flat capture has method-version offsets only. Flat-only captures selected 13,323 current
and 13,643 reference samples; collection-trained captures selected 7,289 and 7,312.

Most samples are in the object loop, token reading, string transcoding and dictionary work.
Under flat training, dictionary insertion is mainly attributed to the inlined object loop.
After collection training, standalone `Dictionary.TryInsert` accounts for 12.4% of current
samples and 10.9% of reference samples. These percentages cannot be compared as if inlining
were unchanged: a method's disappearance from a stack does not mean its work disappeared.

The flat fixture decodes 16 property names, below the 64-token activation prefix. It never
creates a token cache owner or rents its table. The observed mixed-input cost therefore cannot
be explained by an additional cache allocation in those flat reads. Eligibility checks, mutable
prefix counting, scope propagation and cleanup still exist, but the evidence does not resolve
the small isolated residual to a single instruction.

This machine has command-line developer tools without Instruments, so these are managed
sample stacks rather than hardware-counter or kernel-only CPU profiles. GC polling/wait samples
are visible (roughly 7–8%). No absolute CPU-cycle claim is made. The installed trace tool's
Speedscope conversion lacked its matching FastSerialization dependency; the checked-in analyzer
uses the already-installed TraceEvent 3.2.8 libraries instead, without adding a production package.

## Targeted experiments: neither retained

Forcing only disposal out of line removes the root cleanup expansion, but the diagnostic
matrix result above did not improve. Forcing `GetPreparedToken` out of line also preserves all
semantic rules and thresholds. Its first run looked promising, so it received three alternating
fresh-process comparisons of the complete plain-parser matrix. Median means were:

| Workload | Control | Outlined cache helper | Candidate change |
|---|---:|---:|---:|
| Flat | 606.50 ns | 600.18 ns | −1.0% |
| Nested | 1.734 μs | 1.730 μs | −0.2% |
| 128 records | 32.453 μs | 32.760 μs | +0.9% |
| 4096 records | 1.265 ms | 1.299 ms | +2.7% |

The flat result changed direction between process pairs. This does not justify a new inlining
constraint or trading large-input performance for a marginal small-input result. Tagged,
escaped-text and isolated-flat checks are retained in the evidence as additional qualification,
not evidence that overrides the primary result. Both experimental annotations are removed. A JIT-event capture of the outlined-cache candidate
confirms that its helper no longer inlines, yet all three dictionary-insertion sites still fail
with `unprofitable inline`. This rejects the simple hypothesis that outlining that helper alone
restores dictionary inlining; the recorded failures do not establish budget exhaustion.

## Runtime-switch controls

Single-process diagnostic probes (five seconds each, not repeated latency estimates) also
show that the result cannot be reduced to one runtime switch:

| Runtime setting | Current, flat warmup | Current, collection warmup |
|---|---:|---:|
| Dynamic PGO disabled | 765.81 ns | 844.87 ns |
| Tiered compilation disabled | 1,668.32 ns | 1,596.05 ns |

Both settings were slower than normal execution. Disabling dynamic PGO alone did not remove
the warmup effect; disabling tiered compilation changed its direction and generated different
fully optimized code. These probes are deliberately limited: they support investigating the
broader tiering/inlining history, not a claim that dynamic PGO alone causes the entire gap or
a recommendation to disable runtime optimization. The reference PGO-disabled probes and native
code from the tiering-disabled runs are retained in the evidence.

## Result and next optimization boundary

Production parsing, cache thresholds, ownership, admission, duplicate policies, scalar conversion
and canonical writing remain unchanged. The deliverable is a repeatable profiling harness,
trace analyzer, and a corrected performance model: report isolated steady-state parsing and
heterogeneous warmup separately. The previously reported 7–15% is not a universal flat-input tax.
The small isolated residual is real enough to keep measuring, but these experiments do not
attribute it to one instruction or establish a worthwhile implementation change.

The next focused benchmark should vary object width around the 8-field storage boundary
(1, 4, 8, 9, 16 and 32 fields), with both narrow and wide warmup. A future mixed-input
optimization should explicitly account for shape frequency and inlining choices in the whole
object-building loop. Merely changing cache activation checks or forcing
one helper boundary does not recreate the flat-trained realization. Keep the large-input
allocation improvement and require representative before/after gains before adding reader
specializations or a new policy surface.

## Verification

All experimental production edits are restored. The final focused suite passed **58 checks**
covering ownership, bounded allocation, malformed JSON, typed parsing, tagged values and wire
round trips. The trace analyzer builds without warnings and reproduces the selected sample
counts and native-offset counts from the earlier analysis; ordering is explicitly ordinal.
The profiling CLI validates arguments and consumes its output checksum.

The restored build has a newer source-revision informational stamp (`71982c48…`) than the
preserved baseline (`a5a6d5a2…`), so its complete DLL hash differs. An independent SDK metadata
comparison verifies that **all 11,812 method signatures, bodies, implementation flags, locals
and exception regions match** the accepted baseline. The outlined-cache diagnostic differs in
exactly the intended method's implementation flag. Restoration and candidate comparisons are
included in the evidence; no whole-DLL byte-equality claim is made.

## Reproduction and artifacts

The benchmark executable accepts:

```text
--json-parse-profile <flat|nested|collection|large> <seconds:1..60> [warmup-workload]
```

Build once and freeze the benchmark/dependency closure for both libraries. Substitute only Core.
The harness checks parser equivalence through the existing benchmark setup and consumes a
result checksum in the timed loop. See the [trace-analysis guide](../../eng/runtime-performance/observation-trace-analysis/README.md)
for sample filtering, native offsets and installed-library requirements. For JIT inlining events,
collect `Microsoft-Windows-DotNETRuntime:0x1000:5`; the analyzer retains relevant event payloads.

For isolated in-process BenchmarkDotNet measurements, launch a separate process per workload:

```text
dotnet <frozen>/Cohesive.Relations.Benchmarks.dll --filter '*ObservationValuePipelineBenchmarks.ParseDirect*flat*' --inProcess --warmupCount 6 --iterationCount 8 --iterationTime 250 --artifacts <results>
```

A single invocation matching the entire workload matrix is a separate, collection-first
training regime. Preserve both regimes when evaluating compiler-sensitive changes.

Raw reports, assembly manifests, sampled stacks, JIT events, disassembly and exact orchestration
scripts are collected in [the evidence directory](evidence/observation-values-cpu-jit/).

The follow-up [width benchmark investigation](observation-values-width.md) tests narrow/wide training across the eight-field boundary and records why separating dictionary insertion was rejected.
