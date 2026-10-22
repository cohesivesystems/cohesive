# Observation value baselines

This suite separates representation costs from consumer execution. It introduces no new NuGet
dependency and no production reference to Ari. Run builds/tests before timing; do not overlap
benchmarks, tracing or builds. Record source revisions, .NET version, OS/architecture and exact
binary hashes with each report. Short exploratory runs are not latency guarantees.

## In-repository workloads

`ObservationValuePipelineBenchmarks` covers a flat sixteen-field object, 32 nested objects,
128 repeated records and 4,096 records. Each record contains an integer, Unicode/escaped text,
a Boolean and Null. Input generation is outside measurement. Both decoders must produce equal
values; the pipeline must compute the expected total before measurements begin.

- `ParseDirect`: UTF-8 tokens to fresh owned observation storage.
- `ParseThroughDocument`: temporary JsonDocument plus existing element interpretation; a real
  alternate interpretation, not a reproduction of historical implementation details.
- `ParseMapWrite`: fresh direct parse, native relation expression, UTF-8 output. The reusable
  destination excludes output buffer growth but includes writer allocation. Only the sum (or
  flat two-field sum) is emitted. This is a representation microbenchmark, not the EDI workload.

`ObservationAggregateBenchmarks` covers sum/average/minimum/maximum, empty/128/4,096 items,
with and without a field selector. Setup owns input storage. Warm evaluation includes ordinary
capability dispatch and field traversal. Scalar results do not require retained heap storage.

```sh
dotnet build src/Cohesive.Relations.Benchmarks -c Release -m:1
dotnet run --no-build --project src/Cohesive.Relations.Benchmarks -c Release -- \
  --filter '*ObservationValuePipelineBenchmarks*' '*ObservationAggregateBenchmarks*' \
  --inProcess --warmupCount 3 --iterationCount 5 --iterationTime 250
```

For matched comparisons, freeze the entire benchmark output twice and substitute only the changed
Core/Relations DLLs. Execute the identical benchmark DLL in each directory. BenchmarkDotNet full
JSON and Markdown reports retain distributions, allocation/GC evidence and environment metadata.
Allocation totals include retained output; they are not retained heap measurements or counts of
temporary bytes alone. Compare the required retained arrays with total allocations explicitly.

## Real EDI 204 data plane (optional)

This harness consumes an already-built Ari runtime containing `Cohesive.Adapters.Edi.dll` and the
existing public X12 004010 graph. It reads Ari's authoritative accepted relation, target graph and
two-stop acceptance fixture from an explicit checkout. It does not rebuild or edit that checkout,
duplicate the EDI parser, or put Ari dependencies into production packages.

It expands the fixture to 2/16/64 stops with consecutive stop numbers and a corrected SE count.
These are reproducible scaled synthetic loads based on the acceptance fixture, not a claim about
production size distributions. Parsing is strict; target stop counts and one-row cardinality are
checked. The output is the complete canonical load's ordinary JSON projection.

```sh
dotnet build eng/runtime-performance/observation-values/EdiDataPlane.csproj -c Release \
  -p:AriRuntimeDirectory=/absolute/path/to/frozen/ari/runtime -m:1
dotnet eng/runtime-performance/observation-values/bin/Release/net10.0/EdiDataPlane.dll \
  /absolute/path/to/ari 16 8
```

Preparation reads the source/target/definition and constructs one reusable compilation request.
The first pipeline operation includes lazy parser/query compilation. Twenty executions warm caches;
seven batches then measure parse → supplied-source admission → relation evaluation → output bytes.
Reuse the exact compilation request: a new request per row would benchmark recompilation rather
than steady-state mapping. All input instances are freshly parsed; boundary admission remains fresh.
Output is an owned exact byte array, so its length is an explicit required result allocation.

CSV reports process-wide allocated bytes and wall time for each batch, input/output lengths and
SHA-256 of output. Divide batch bytes/time by iterations. The hash is computed after measurement;
every batch must match the first output. Cold preparation is separate from first execution.
Diagnostic full-GC retained-heap deltas retain 32 wire outputs or parsed observations; they are
written to stderr, include surviving runtime caches and exclude native memory. They are not an
exact heap census, and must not be subtracted from cumulative allocation to infer temporary bytes.
Input text and shared definitions are already live before each retention sample.

Freeze the resulting runtime twice, substitute candidate Core/Relations only, then run:

```sh
python3 eng/runtime-performance/observation-values/compare.py \
  --baseline CONTROL/EdiDataPlane.dll --candidate CANDIDATE/EdiDataPlane.dll \
  --ari-root /absolute/path/to/ari --output counters.csv --runs 5 --iterations 8
```

The comparator alternates process order and rejects output differences. Raw retention diagnostics
are kept alongside counters. A binary substitution is appropriate for these representation-only
changes; it is not a package upgrade or a substitute for rebuilding consumers after API changes.

## Existing control plane and durable workloads

Retain Ari's original `ProtocolPreparation` harness and
`eng/runtime-performance/durable-bookkeeping/compare-ari.py`. It runs the complete repository
transition cases for both X12 versions, with fresh-process cold and second-invocation counters.
Do not replace that workload with the new mapping microbenchmark. Its reproduction and prior
evidence remain in `docs/performance/prepared-observation-projections.md`.

Existing `ObservationProjectionBenchmarks`, `ProcessContinuationFingerprintBenchmarks` and
`ExecutionDefinitionFingerprintReuseBenchmarks` supply materialization, canonical writing and
durable fingerprint baselines. The new harness reports complete mapped wire size, not Process
checkpoint size. Full checkpoint-size instrumentation remains a separate durable-workload task.

## Initial optimization boundaries

The first implementation removes capacity pre-scans, shares the existing small-object storage,
and uses inline/pooled array scratch with one exact frozen result. Union discriminator lookahead
remains: it is required for typed case selection and is not a capacity-counting pass.

Aggregate reduction reads the existing array directly when no selector exists. With a selector,
pooled scratch preserves complete selector evaluation before reduction. This intentionally forgoes
full fusion to preserve first-failure behavior. Field reads use the core ordinal-name lookup rather
than a parallel member-enumeration algorithm. Pools are cleared on success and failure.

Recursive layouts, generated views, cross-owner indexes, binary identity formats and additional
HTTP/store codec changes are outside this first measured increment. In particular, arbitrary CLR
response projection functions cannot simply be bypassed by writing their source observation.

## Boundary follow-up

`ObservationBoundaryConversionBenchmarks` measures fresh tagged payload decoding and the original
response JSON path. `PreparedObservationResponseBenchmarks` compares that response path with one
registration-owned prepared reader, including a separate reader-compilation benchmark. That
compilation benchmark has warm serializer metadata; it is not a fresh-process cold-start measurement.
The response includes 64 KiB of immutable text and 1/128/4,096 independently materialized mutable items.

`ObservationCheckpointBenchmarks` measures full checkpoint text encoding and decoding with fresh
exact-plan recovery admission. Setup includes the start, continuation and control envelopes, checks
an exact canonical round trip and excludes definition compilation from measurement. Flat, nested,
128-item and 4,096-item payloads are synthetic bounded fixtures. Print complete wire sizes and hashes:

```sh
dotnet src/Cohesive.Relations.Benchmarks/bin/Release/net10.0/Cohesive.Relations.Benchmarks.dll \
  --checkpoint-wire-sizes
dotnet src/Cohesive.Relations.Benchmarks/bin/Release/net10.0/Cohesive.Relations.Benchmarks.dll \
  --filter '*ObservationBoundaryConversionBenchmarks*' '*ObservationCheckpointBenchmarks*' \
  --inProcess --warmupCount 3 --iterationCount 5 --iterationTime 250 --artifacts RESULTS
dotnet src/Cohesive.Relations.Benchmarks/bin/Release/net10.0/Cohesive.Relations.Benchmarks.dll \
  --filter '*PreparedObservationResponseBenchmarks*' \
  --inProcess --warmupCount 3 --iterationCount 5 --iterationTime 250 --artifacts RESPONSE_RESULTS
```

Freeze the identical benchmark binary with each production closure before a matched run. Execute
sequentially without concurrent tests, builds or tracing. Checkpoint staging still includes a temporary
serializer document; the final text owns its storage. Prepared response allocation includes the
required independent CLR graph but shares valid immutable source strings. Tagged decoding owns fresh
storage; a payload preceding its tag uses a local deferred document. Neither benchmark measures
network/store I/O or permits bypassing a user response projection callback.

## Bounded token text reuse

`ObservationJsonStringReuseBenchmarks` extends the pipeline suite with unique names/values, repeated
escaped text, tokens crossing eleven-byte segments, and repeated 8 KiB strings. Inputs and sequence
segments are created outside measurement. Every setup compares direct and element interpretations.
Use the existing flat/nested/128-record/4,096-record pipeline fixtures for normal distributions and
include tagged decoding and complete recovery so payload-only results are not mistaken for total cost:

```sh
dotnet BENCHMARK_RUNTIME/Cohesive.Relations.Benchmarks.dll \
  --filter '*ObservationValuePipelineBenchmarks*' '*ObservationJsonStringReuseBenchmarks*' \
    '*ObservationBoundaryConversionBenchmarks.DecodePortable*' \
    '*ObservationCheckpointBenchmarks.DecodeAndAdmit*' \
  --inProcess --warmupCount 3 --iterationCount 5 --iterationTime 250 --artifacts RESULTS
```

Freeze the same harness for both closures and substitute Core, keeping the other measured dependencies
identified in the manifest. Token scopes rent at most the fixed table, never input-sized text-key
storage. Activation skips the first 64 eligible tokens, oversized/segmented tokens bypass reuse,
and 32 initial misses disable an unproductive table. Warm repeated-text allocation includes the
required value containers and activation-prefix strings; cold or concurrent table-pool misses can
allocate bounded scratch. Hash collisions and process-randomized bucket placement may vary exact
hit rates without changing values or canonical output. The element reader has a different operating
policy because its source document remains live throughout traversal; it can reuse larger strings.

Fixed tagged-kind metadata is measured separately by `ObservationTaggedMetadataBenchmarks`.
The follow-up [report](../../../docs/performance/observation-values-tagged-metadata.md) records
zero-allocation singleton decoding, escaped/segmented conformance, small-envelope tradeoffs,
and matched whole-checkpoint recovery measurements.

For CPU/JIT investigation, the benchmark executable also accepts
`--json-parse-profile <workload> <seconds> [warmup-workload]`. The optional final argument
makes warmup history explicit. The [trace-analysis tool](../observation-trace-analysis/README.md)
filters samples to the measured loop and resolves reader method instruction offsets.

When using the in-process BenchmarkDotNet toolchain, launch each workload filter in a separate
process to measure its isolated behavior. A matrix invocation shares tiered JIT decisions between
workloads; retain that as a separate mixed-workload regime rather than treating its rows as isolated.
The [CPU/JIT report](../../../docs/performance/observation-values-cpu-jit.md) records the distinction.
