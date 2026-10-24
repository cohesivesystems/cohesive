# Relation preparation reuse

Four changes remove repeated preparation work in `Cohesive.Relations`. Each keeps results, fingerprints and
validation identical; they only avoid recomputing values that are already fixed for an immutable input.

| Change | Repeated work removed |
|---|---|
| `HostedQueryDefinition` implements `IImmutableExecutionDefinition` | Every `HostedQuery.Definition`, `InputContract` or `ResultContract` read decoded the whole definition document again. Like Process, Transition and Relation definitions, the decoded projection is now shared per document instance. |
| `SequenceKeyBuilder` for nested ordering keys | Realization trace, proof and validated-proof keys, and static-compilation trace keys, allocated every inner key and every `ToString("D4")` ordinal as a separate string before copying it into the outer key. The builder writes the identical text directly. `NormalizeTraces` now computes each trace key once and skips work for zero or one trace. |
| Shared `RelationQueryTargetCapabilityProfileAnalysis` | Realization, placement and physical planning analyzed the same immutable target profile repeatedly. Analyses are now weakly cached per profile instance. |
| Report fingerprint computed once | The realization compiler computed a report's fingerprint and the report constructor computed it again to validate it. The compiler now lets the constructor compute it once from the normalized content, and physical planning no longer recomputes it, because the report's only public constructor already rejects a mismatched fingerprint. |

## Invariants

- Ordering keys are byte-for-byte unchanged, so realization fingerprints and decision ordering are unchanged.
  `RealizationAllocationTests` compares the builder with the original concatenation for nulls, empty
  strings, non-ASCII text, values larger than the stack buffer, negative ordinals and nesting. The existing
  golden realization fingerprint tests pass unchanged.
- Shared projections and analyses are keyed by instance identity with weak keys, so an equal but
  different instance is still decoded or analyzed independently, and retention follows the owner.
- `HostedQueryDefinition`'s reachable graph passes the existing `ExecutionImmutabilityContractTests`
  reflection audit for marked definitions.
- The public `RelationQueryRealizationReport` constructor still rejects a supplied fingerprint that differs
  from the normalized content.

## Evidence

Ari `ProtocolCompilationAllocationTests` workload (X12 004010 204 protocol-spec preview plus durable
compilation), local Linux container, Release, Cohesive `main` (`341a949`) packed locally and consumed by
Ari `main`. Cumulative allocated bytes per case; warm is the steady third/fourth case in one process.

| Configuration | First case | Warm case |
|---|---:|---:|
| Cohesive `main` | 251.9 MB | 53.5 MB |
| + shared hosted-query projection | 246.2 MB | 51.3 MB |
| + nested key builder | 238.6 MB | 51.2 MB |
| + shared profile analysis, no planner fingerprint recomputation | 234.5 MB | 50.1 MB |
| + single report fingerprint | 233.2 MB | 50.1 MB |

These are allocation measurements of one end-to-end workload, not latency claims.

## Matcher-scoped guarantee preparation (October 24, 2026)

One realization can contain hundreds of requirements with the same capability proof and required
semantic guarantees. `Resolve` already shared the capability proof, but `ValidateGuarantees` rebuilt
its guarantee-evidence combinations, normalized arrays and ordering keys for each requirement.
The matcher now retains successful combinations by proof identity and the normalized guarantee
sequence. Separately allocated but equal guarantee sequences share preparation. The entries belong
to one synchronous match and disappear with it; there is no global cache, weak table, lock, new
public API or additional semantic authority.

The fixed target profile and compiler policy remain authoritative. Requirement-specific boundary facts,
measurements and validation still run independently; failures are not cached, so their diagnostics keep
the owning requirement identity. A page-size proof admitted for five rows cannot admit a later
40-row demand against a 25-row boundary. Different guarantee sets and missing evidence remain
independent. Override admission and final report validation are unchanged.

This extends the existing Cohesive realization matcher. Ari uses its existing evaluator as-is; there
is no Ari-local preparation bridge. Publication and Ari's package upgrade are subsequent deliverables.

### Measured allocation and CPU

Apple arm64/macOS, .NET 10.0.13, Release. The Ari baseline is published alpha.157 plus the locally
qualified seed-family change (`5c8e2ba`). Identical frozen harnesses and dependencies differ only in
`Cohesive.Relations.dll`; the actual loaded module hashes are checked before measurement. Three
alternating fresh-process pairs per source run 64 original workflow cases per process. Cold figures
are medians across three processes. MB is decimal MB; cumulative allocation is not retained memory.

| Original X12 workflow | Cold before | Cold after | Change | CPU before/after |
|---|---:|---:|---:|---:|
| 004010 | 117.246 MB | 112.150 MB | −5.096 MB (4.3%) | 1,583 / 1,551 ms |
| 005030 | 118.150 MB | 113.166 MB | −4.984 MB (4.2%) | 1,589 / 1,572 ms |

Cold process CPU differs by only 1–2% in these local samples; that is inconclusive. Warm allocation
is effectively unchanged at about 3.5 MB; the workflow already prepares realization once per plan.
Warm timing varies with runtime warmup and is not attributed to this cold preparation change.
Separate stage runs place about 1.1–1.4 MB of saving in preview and 3.3–3.7 MB in first durable
execution. Do not interpret the entire saving as preview-only work.

Three independent traces per source and variant attribute realization preparation at approximately
18.4–18.5 MB before and 13.7–13.8 MB after. All traces report zero lost events and missing allocation
or managed CPU stacks. These are sampled estimates of the entire cold workflow, not an additional
saving to add to the untraced counters. Trace timing is not used for CPU claims.

The focused public `Match` fixture (128 requirements, three repeated guarantees) allocates
2,394,488 → 1,075,968 bytes per match, a 55% reduction, using the median of seven warm runs.
Its 1.4 MB regression ceiling has about 30% headroom and fails against published alpha.157.
The report's pinned fingerprint is identical on both versions. No timing threshold was added to CI.

Qualification: 1,154 Relations tests; 5,175 Core tests with 60 existing gated skips; 1,013 Ari Engine
tests with 18 existing gated skips; native recovery across both X12 versions and FIX at three
interruption points; 768 normal X12 workflow cases; unchanged pinned graph fingerprints. Native
and Engine checks load the candidate from an isolated output directory. Official package caches
are untouched. This is source qualification, not a published-package or deployment-latency claim.

[Manifest and module hashes](evidence/guarantee-preparation/manifest.json),
[normal counters](evidence/guarantee-preparation/windows.csv),
[cold stages](evidence/guarantee-preparation/cold-stages.csv),
[focused allocation](evidence/guarantee-preparation/focused-allocation.csv),
[realization attribution](evidence/guarantee-preparation/realization-attribution.csv) and
[trace quality](evidence/guarantee-preparation/trace-quality.csv) retain the compact evidence.
Large raw artifacts remain under `/tmp/guarantee-preparation-profile` and `/tmp/cold-preview-profile`.
Reproduce the focused guard with:

```sh
dotnet test src/Cohesive.Relations.Tests/Cohesive.Relations.Tests.csproj -c Release --filter FullyQualifiedName~RepeatedGuaranteeRequirements
```

Ari's existing protocol-preparation harness supplies `--iterations 64`, independent stage counters
through `ARI_COMPILATION_PROFILE_DIRECTORY`, runtime/module identity and EventPipe attribution.
Next cold investigations should separate declaration/contract authoring and remaining provenance
preparation from invocation work, preserving fresh persisted admission and contextual validation.
