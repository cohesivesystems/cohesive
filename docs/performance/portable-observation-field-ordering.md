# Portable observation field ordering

## Responsibility and invariants

Cohesive owns portable-value validation, tagged serialization and canonical observation encoding.
ObservationValue remains the authority for owned immutable fields. This change extends the existing
canonical writer's pooled ordering mechanism rather than adding an Ari-specific codec or cache.

Successful portable validation visits fields in their stored order. Only failing fields create
ordinally sorted diagnostic groups, retaining depth-first order and escaped locations without
revalidating subtrees. Canonical and tagged writers share OrderedObservationFields: ordinal immutable
sorted dictionaries pass through directly; other field collections use a scoped pooled copy and
sort only if its keys are not already ordered. Both writers retain identical ordinal wire ordering.
Buffers are cleared and returned on normal completion and exceptions. The iterative streaming writer
uses the same rental/return functions. No validation evidence is cached, and no fingerprint format changes.

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
| Cold X12 004010 normal | 1,151,182,176 | 903,437,200 | 21.5% |
| X12 005030 normal | 950,920,856 | 662,465,360 | 30.3% |
| FIX normal | 174,823,240 | 156,180,248 | 10.7% |
| All nine cases | 6,557,908,808 | 4,648,632,768 | 29.1% |

The cold case's preview allocation falls from 313.5 to 244.3 MB and execution from 522.6 to
377.2 MB. Definition catalog construction remains about 110.2 MB and seed catalog about 95.1 MB:
this optimization removes repeated runtime work, not catalog authoring. The whole nine-case run
was 10.07 versus 9.85 seconds, and the cold stage total 3.60 versus 3.43 seconds. Single timing
samples do not establish a stable latency improvement.

The checked-in 128-field allocation regression uses booleans to isolate field ordering from scalar
formatting, warms 1,000 times, and reuses Utf8JsonWriter plus its output buffer. Immutable values and
serializer metadata are created before the counter. The validator counter includes its complete
public entry point, context, location scratch storage and result.

| 128-field input | Tagged writer before/after | Validator before/after |
|---|---:|---:|
| Owned unsorted fields | 6,968 / 0 B | 7,240 / 352 B |
| Ordinal immutable sorted fields | 6,968 / 0 B | 7,240 / 328 B |

The baseline intentionally fails the new allocation budgets (writer <=128 B, validator <=1,024 B).
The revised implementation passes. The difference between sorted and owned validation is constant
enumerator/context overhead; no per-field sorted array is allocated. Tests also cover nested escaped
diagnostic ordering and reuse after a writer exception. Existing lossless round trips and canonical
JSON checks remain in force.

[Raw per-stage measurements](portable-observation-field-ordering.csv) cover both variants and all nine cases.

```bash
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~PortableValueTests|FullyQualifiedName~CanonicalJson'
ARI_COMPILATION_PROFILE_DIRECTORY=/tmp/portable-field-profile dotnet test src/Ari.Engine.Tests/Ari.Engine.Tests.csproj -c Release --filter FullyQualifiedName~CanonicalProcessCommitsDataDefinedSpecThroughRepositoryTransitions --logger 'console;verbosity=detailed'
```

Run the second command from Ari after adopting or explicitly overlaying the intended Cohesive assembly.
No narrow-fixture catalog, spec-hash reuse, EDI provenance, durable commit fingerprint or receipt-admission
change is included. Those remain separate candidates requiring their own before/after qualification.

## Qualification

The full Core suite passed 4,400 tests (33 existing optional skips), Relations passed 1,106,
and Ari Engine passed 855 (18 existing scheduler skips). The final explicit sorted-enumerator
cleanup additionally passed all 63 portable-value/canonical JSON focused cases. Package publication
and Ari dependency upgrades are not part of this change.
