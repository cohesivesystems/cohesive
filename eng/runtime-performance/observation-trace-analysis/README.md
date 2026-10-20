# Observation parser trace analysis

Summarizes EventPipe sample stacks containing the benchmark's non-inlined
`ObservationJsonParseProfile.Measure` marker. Setup and warmup stacks are excluded.
The JSON reports selected sample counts, self/inclusive counts, relevant JIT-inlining event
payloads, and native instruction
offsets within known tiered reader method versions. Percentages derived from these counts
are shares of managed samples, not hardware CPU counters or absolute elapsed time.

Build using the .NET 10 SDK and an **already-installed** TraceEvent directory containing
`Microsoft.Diagnostics.Tracing.TraceEvent.dll` and `Microsoft.Diagnostics.FastSerialization.dll`:

```text
dotnet build eng/runtime-performance/observation-trace-analysis -c Release -p:TraceEventDirectory=<installed-library-directory>
dotnet eng/runtime-performance/observation-trace-analysis/bin/Release/net10.0/TraceAnalysis.dll <trace.nettrace>
```

The default directory is the existing NuGet cache's TraceEvent 3.2.8 `lib/netstandard2.0` folder.
This project has no package references, downloads no diagnostics dependency, and is separate
from production libraries and the optional Ari consumer harness. The trace directory must be
writable: analysis creates a derived `.profile.etlx` file alongside the original trace.
Method-load metadata is retained in that file to resolve native instruction ranges.

Collect a trace from a frozen benchmark closure with an installed `dotnet-trace`:

```text
dotnet-trace collect --profile dotnet-sampled-thread-time --output reader.nettrace --show-child-io -- dotnet <frozen>/Cohesive.Relations.Benchmarks.dll --json-parse-profile flat 20 collection
```

The last argument selects collection warmup followed by flat measurement. Omit it for
same-workload warmup. The harness warms for approximately one second and measures for the
requested 1–60 seconds, rounded up to a batch boundary. It prints operation count, elapsed
nanoseconds, allocated bytes and a consumed result checksum. Use independent untraced
BenchmarkDotNet runs for performance claims; tracing changes scheduling and GC behavior.

Set `DOTNET_JitDisasm`, `DOTNET_JitDisasmAssemblies=Cohesive`, and `DOTNET_JitStdOutFile`
on the traced process to obtain assembly from the **same process** as sampled native offsets.
Relevant method filters are `ReadValue ReadValueCore ReadUntypedObject Set Get Dispose`.

See the [profiling report](../../../docs/performance/observation-values-cpu-jit.md).

To capture the JIT's inlining success/failure events, use
`--providers Microsoft-Windows-DotNETRuntime:0x1000:5`. JIT-only traces can have zero selected
CPU samples while still producing useful `inlining` entries.
