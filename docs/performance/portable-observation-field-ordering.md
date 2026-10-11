# Portable observation field ordering

## Responsibility and invariants

Cohesive owns portable-value validation, tagged serialization and canonical observation encoding.
ObservationValue remains the authority for owned immutable fields. This change extends the existing
canonical writer's pooled ordering mechanism rather than adding an Ari-specific codec or cache.

Portable validation walks ordinal layouts and ordinal sorted dictionaries in canonical order; other
inputs use their stored order. Only failing fields in unordered inputs create
ordinally sorted diagnostic groups, retaining depth-first order and escaped locations without
revalidating subtrees. Canonical and tagged writers share OrderedObservationFields: ordinal immutable
sorted dictionaries pass through directly; other field collections use a scoped pooled copy and
sort only if its keys are not already ordered. Shape-backed fields traverse the layout
CanonicalJsonOrdinals directly, skipping absent values, without a copy or sort. Both writers retain identical ordinal wire ordering.
Buffers are cleared and returned on normal completion and exceptions. The iterative streaming writer
uses the same owning traversal and enumerator; the rental/return functions are private.
Validation and pooled copying share one generic storage dispatch, avoiding boxed nested enumerators.
Copies share a generation-stamped lease with exactly-once return: an old copy cannot return a later
rental. At most 256 cleared lease objects are retained; warm rentals reuse them, while deeper or
concurrent traversals may allocate more lease objects. Return is synchronized; concurrent disposal
is safe, but traversal must finish before disposal. Streaming frame transfers clear their old slots. No validation evidence is cached, and no fingerprint format changes.

## Measurement

Measured locally on October 10, 2026, macOS arm64, .NET SDK 10.0.201/runtime 10.0.5. Baseline is
merged #411/#412 behavior (main 1b7130d4); Ari's fixture is d86b5c6 with published alpha.131
packages. Only Cohesive.dll was temporarily overlaid for each variant, then restored byte-for-byte.
The before DLL SHA-256 is a39cd8c6b8559715c8607246f76486df30389b92376350c1cfda09564bc14eeb.
This is a source-assembly qualification, not a published-package adoption or CI/production result.

The original CanonicalProcessCommitsDataDefinedSpecThroughRepositoryTransitions filter runs nine
cases covering X12 004010, X12 005030 and FIX, with normal execution and interruption before/after
pointer commit. Each variant ran in a fresh test process, sequentially without concurrent builds/tests.
Stage counters use process-wide GC.GetTotalAllocatedBytes(precise: true); bytes are cumulative,
not retained memory. CSV writing and counter reset are outside stage timings. Other cases share
already-warmed static state within each variant. All nine cases passed in each run.

| Workload | Before bytes | After bytes | Reduction |
|---|---:|---:|---:|
| Cold X12 004010 normal | 1,151,182,176 | 879,940,680 | 23.6% |
| X12 005030 normal | 950,920,856 | 634,473,008 | 33.3% |
| FIX normal | 174,823,240 | 154,140,584 | 11.8% |
| All nine cases | 6,557,908,808 | 4,453,822,240 | 32.1% |

The cold case's preview allocation falls from 313.5 to 236.4 MB and execution from 522.6 to
364.9 MB. Definition catalog construction remains about 110.6 MB and seed catalog about 95.2 MB:
this optimization removes repeated runtime work, not catalog authoring. The whole nine-case run
was 10.07 versus 8.92 seconds, and the cold stage total 3.60 versus 3.52 seconds. Single timing
samples do not establish a stable latency improvement.

The checked-in 128-field allocation regression uses booleans to isolate field ordering from scalar
formatting, warms 1,000 times, and reuses Utf8JsonWriter plus its output buffer. Immutable values and
serializer metadata are created before the counter. The validator counter includes its complete
public entry point, context, location scratch storage and result.

| 128-field input | Tagged writer before/after | Validator before/after |
|---|---:|---:|
| Owned unsorted fields | 6,968 / 0 B | 7,240 / 272 B |
| Ordinal immutable sorted fields | 6,968 / 0 B | 7,240 / 272 B |

The baseline intentionally fails the new allocation budgets (writer <=128 B, validator <=1,024 B).
The revised implementation passes. Validation retains constant context/result overhead; no per-object boxed enumerator or
per-field sorted array is allocated. Tests also cover nested escaped
diagnostic ordering. A counting pool checks cleared slots and exactly-once returns during nested
streaming-writer failure, concurrent disposal of copies, and disposal of a stale copy after lease reuse. Existing lossless round trips and canonical
JSON checks remain in force.

[Compact measurements](portable-observation-field-ordering.csv) retain cold-case stages and totals
for all nine cases. Detailed local captures remain outside the repository. Only allocation reduction
is established; timing values are single samples, not a demonstrated latency gain.

```bash
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~PortableValueTests|FullyQualifiedName~CanonicalJson'
ARI_COMPILATION_PROFILE_DIRECTORY=/tmp/portable-field-profile dotnet test src/Ari.Engine.Tests/Ari.Engine.Tests.csproj -c Release --filter FullyQualifiedName~CanonicalProcessCommitsDataDefinedSpecThroughRepositoryTransitions --logger 'console;verbosity=detailed'
```

Run the second command from Ari after adopting or explicitly overlaying the intended Cohesive assembly.
No narrow-fixture catalog, spec-hash reuse, EDI provenance, durable commit fingerprint or receipt-admission
change is included. Those remain separate candidates requiring their own before/after qualification.

## Nested-object regression

The review exposed boxing hidden by the initial flat fixture. The added fixture holds 128 nested
one-field objects, warms 1,000 calls, excludes construction, and measures the same complete public
validator plus tagged/streaming writes into reused output buffers. It covers owned, immutable, sorted
and layout-backed fields. Layout ordinals are reversed physically and contain an absent optional
field; both writer forms retain canonical order and omit the absent field.

| Storage | Validator before/after | Tagged writer before/after | Streaming writer before/after |
|---|---:|---:|---:|
| Owned | 10,592 / 272 B | 0 / 0 B | 0 / 0 B |
| Immutable | 22,976 / 272 B | 0 / 0 B | 0 / 0 B |
| Immutable sorted | 7,496 / 272 B | 0 / 0 B | 0 / 0 B |
| Layout-backed | 9,560 / 272 B | 9,288 / 0 B | 9,288 / 0 B |

The before assembly was built from exact pre-review head deab5257 in an isolated checkout. It
intentionally fails all four new validator allocation budgets. Sorted input also bypasses copies
in the streaming writer, even when an already-warm pooled copy would hide that work from counters.

## Qualification

The full Core suite passed 4,408 tests (33 existing optional skips), Relations passed 1,106,
and Ari Engine passed 855 (18 existing scheduler skips). All 122 portable-value, core-observation and
canonical JSON focused cases pass, including the nested allocation checks. All nine original Ari
cases also pass in the refreshed final allocation run. Package publication and Ari dependency
upgrades are not part of this change.
