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

One realization can contain hundreds of requirements sharing semantic guarantees. `Resolve` already
shared capability proofs, but `ValidateGuarantees` rebuilt their guarantee-evidence combinations,
normalized arrays and ordering keys for each requirement. The matcher now prepares:

- Combinations and pure missing-guarantee facts by proof identity and normalized guarantee sequence.
  Separately allocated but equal guarantee sequences share preparation.
- Native guarantee evidence by guarantee kind, including empty evidence. Different capability proofs
  reuse the same immutable native evidence without repeating its filtering, grouping and sorting.

Entries belong to one synchronous match and disappear with it; there is no global cache, weak table,
lock, new public API or additional semantic authority. The fixed target profile and compiler policy
remain authoritative. Failure *facts* are reusable; diagnostic objects and messages are freshly built
for their requirement. Preparation records the full missing set for composed proofs separately from
the first unavailable native guarantee; only preparation classifies the proof strategy. A private
constructor and an outcome-specific factory for success, composed failure and native failure prevent
callers from populating both failure fields. The consumer rejects a default struct before reading
any outcome, using the default proofs array as the uninitialized sentinel. Requirement-specific boundary facts and measurements are always validated
independently. A five-row page-size proof cannot admit a later 40-row demand against a 25-row boundary.
Overrides keep their existing validation and diagnostics. Final report validation is unchanged.

This extends the existing Cohesive realization matcher. Ari uses its existing evaluator as-is, with
no Ari-local preparation bridge. Publication and Ari's package upgrade are subsequent deliverables.

### Measured allocation and CPU

Apple arm64/macOS, .NET 10.0.13, Release. The baseline is **merged Ari #249** (`eb906fe`) with official
alpha.157 dependencies. It includes no unmerged seed-family change. The source worktree's tree hash
matches that merge. Identical frozen harnesses and dependencies differ only in `Cohesive.Relations.dll`;
the actual loaded module hashes are checked before measurement. Three alternating fresh-process pairs
per source run 64 original workflow cases per process. Cold figures are medians across three processes.
MB is decimal MB; cumulative allocation is not retained memory.

| Original X12 workflow | Cold before | Cold after | Change | CPU before/after |
|---|---:|---:|---:|---:|
| 004010 | 140.040 MB | 135.479 MB | −4.561 MB (3.3%) | 1,516 / 1,508 ms |
| 005030 | 141.411 MB | 136.269 MB | −5.142 MB (3.6%) | 1,559 / 1,537 ms |

Cold CPU differences are 0.5–1.4% in these local samples and remain inconclusive. Warm allocation is
effectively unchanged at about 3.5 MB; the workflow already prepares realization once per plan.
Warm CPU varies: 004010 is effectively unchanged, while 005030 measures 35.3 versus 38.9 ms.
This preparation runs cold; these samples do not establish a warm CPU improvement.
Separate stage runs place about 1.1–1.2 MB of saving in preview and 3.5–4.0 MB in first durable
execution. Do not interpret the entire saving as preview-only work.

Three independent traces per source and variant attribute realization preparation at approximately
18.4–18.5 MB before and 13.4–13.6 MB after. All traces report zero lost events and missing allocation or
managed CPU stacks. These are sampled estimates of the entire cold workflow, not an additional
saving to add to the untraced counters. Trace timing is not used for CPU claims.

### Mechanism-specific guards

The public `Match` fixture uses 128 requirements and three required guarantees, taking the median
of seven warmed runs. Its four report fingerprints were recorded from alpha.157 and also match the
original PR tip `c1f76081`. Cases cover both shared and distinct capability proofs, with and without
complete guarantee evidence.

| Proofs / evidence | alpha.157 bytes | Original PR bytes | Updated bytes | Ceiling |
|---|---:|---:|---:|---:|
| Shared / complete | 2,394,488 | 1,075,968 | 1,076,240 | 1,400,000 |
| Shared / missing | 1,659,712 | 1,659,800 | 753,984 | 1,000,000 |
| Distinct / complete | 2,583,040 | 2,596,040 | 2,174,128 | 2,300,000 |
| Distinct / missing | 1,945,680 | 1,945,768 | 1,621,120 | 1,850,000 |

All four allocation guards fail against alpha.157. The three new scenarios fail against the original
PR tip and pass after the review changes. Ceilings have approximately 6–33% headroom. The semantic
tests intentionally pass on the unchanged parent: they protect independent boundary facts, different
guarantee sets and requirement-specific native/composed failure messages. Allocation guards prove
that preparation reuse actually occurs. No timing threshold was added to CI.

Current revision: 1,159 Relations tests and all four pinned report fingerprints pass. The 768 normal
X12 workflow cases and 12 fresh traces above were measured at `25240c5f`, before the outcome-factory
encapsulation; the struct retains the same fields and preparation behavior. Broader qualification at preceding tip `d4920f3f` passed
5,175 Core tests with 60 existing gated skips, 1,009 merged-Ari Engine tests with 18 existing gated
skips, and native recovery across both X12 versions and FIX at three interruption points. Those
broader suites were not repeated for this internal result-field clarification. Native and Engine
checks loaded the candidate from an isolated output directory; official package caches are untouched.
This is source qualification, not a published-package or deployment-latency claim.

[Manifest and module hashes](evidence/guarantee-preparation/manifest.json),
[normal counters](evidence/guarantee-preparation/windows.csv),
[cold stages](evidence/guarantee-preparation/cold-stages.csv),
[focused allocation](evidence/guarantee-preparation/focused-allocation.csv),
[realization attribution](evidence/guarantee-preparation/realization-attribution.csv) and
[trace quality](evidence/guarantee-preparation/trace-quality.csv) retain the compact evidence.
Current raw artifacts remain under `/tmp/guarantee-result-profile`; historical broad qualification
remains under `/tmp/guarantee-review`. The earlier seed-dependent measurements are
superseded; they are not the baseline of the current claim. Reproduce the focused guards with:

```sh
dotnet test src/Cohesive.Relations.Tests/Cohesive.Relations.Tests.csproj -c Release --filter FullyQualifiedName~RepeatedGuaranteeRequirements
```

Ari's existing protocol-preparation harness supplies `--iterations 64`, independent stage counters
through `ARI_COMPILATION_PROFILE_DIRECTORY`, runtime/module identity and EventPipe attribution.
Next cold investigations should separate declaration/contract authoring and remaining provenance
preparation from invocation work, preserving fresh persisted admission and contextual validation.
