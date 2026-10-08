# Write exact canonical Int64 tokens without text materialization

## Evidence and semantic boundary

A fresh-process allocation profile after `c389287` attributed approximately 11 MB during catalog
authoring and 4 MB during first admission to exact decimal-rational text normalization. Enum catalog
discovery accounted for approximately 13 MB across those separate stages and remains another target.
These sampled allocation stacks are estimates; nested frames must not be added together.

The shared canonical JSON writer owns exact number spelling. For parsed JSON integers that fit Int64,
`TryGetInt64` followed by `Utf8JsonWriter.WriteNumberValue` produces the same canonical bytes without
`GetRawText`, coefficient arrays, digit strings or string builders. All Int64 values fit the profile's
fixed-notation range (adjusted exponents -6 through 20); signed zero becomes `0`. No floating-point
coercion, format-profile change or cache is introduced.

Both immutable-element and parsed-node canonicalization use the same fast path. Fractional/exponential
tokens and integers outside Int64 still use the existing arbitrary-exponent, exact decimal-rational
normalizer. Portable-observation numeric semantics remain unchanged. For example, document-local
reference index `123` no longer materializes its text; `1.0000000000000001e18` retains its exact
`1000000000000000100` spelling, and `1e999` retains its exponent without machine-number rounding.

## Qualification mechanisms

Differential tests compare signed boundaries, negative zero, notation boundaries, out-of-range
integers and 256 generated signed integers with the unchanged text normalizer. A warmed sequence
writer covers 4,096 integer tokens with input and working storage outside measurement, retaining no
output. Its 16,384 B allocation limit fails against the preceding core at 859,600 B. This protects
removal of per-number temporary representations without imposing a timing threshold.

## Representative measurements

`CanonicalIntegerNormalizationBenchmarks` compares the core at `c389287` with this change. Inputs are
prepared outside measurement. Every invocation exports newly owned canonical UTF-8 bytes, so its
allocation includes output and public export-buffer growth. The fallback fixture contains decimal,
negative zero, exponential, UInt64-sized and extreme-exponent tokens.

| Fixture | Previous | Revised |
| --- | ---: | ---: |
| Single Int64 | 941 B | 573 B |
| Integer behind 24 objects | 10,548 B | 10,191 B |
| 128 integers | 54,556 B | 7,484 B |
| 4,096 integers | 1,865,800 B | 359,009 B |
| Fractional/exponential fallback | 1,786 B | 1,781 B |

[Baseline](canonical-integer-normalization/baseline.md) and
[revised](canonical-integer-normalization/revised.md) record environment and dispersion. Recorded
settings were `--inProcess --job Short --warmupCount 3 --iterationCount 8 --invocationCount 64
--unrollFactor 1`, on Arm64 macOS/.NET 10.0.5. Integer-heavy arrays allocate 81–86% less. Timing
varied: integer arrays improved, while nested/fallback fixtures were slower in these short samples.
This does not establish a general application latency improvement. Fallback allocation is unchanged
within sample noise, and its extra attempted integer parse is part of the implementation's tradeoff.

```bash
dotnet run --project src/Cohesive.Relations.Benchmarks -c Release -- --filter '*CanonicalIntegerNormalizationBenchmarks*'
dotnet test src/Cohesive.Tests/Cohesive.Tests.csproj -c Release --filter 'FullyQualifiedName~CanonicalIntegerNormalizationTests'
```

A paired fresh-process Ari source harness measured independent allocation boundaries:

| Boundary | Previous | Revised |
| --- | ---: | ---: |
| Catalog authoring | 258,704,952 B | 245,767,592 B |
| First admission | 98,231,176 B | 93,696,216 B |
| Warm admission | 862,464 B | 814,696 B |

These are cumulative local allocated bytes, not retained heap or published-package measurements.
All 164 canonical catalog bodies remained byte-identical. No Ari package pin or permanent source
bridge changed. Temporary test-output dependency overlays are restored after qualification.
Enum discovery is addressed in [shared enum catalog preparation](shared-enum-catalog.md).
Other property/attribute discovery remains a subsequent target.

Qualification passed 4,291 Core tests (33 existing skips), 1,082 Relations tests and 855 Ari engine
tests (18 existing skips), including 35 focused canonical-writer cases. The allocation regression
fails against the preceding core. Ari dependency outputs were restored byte-for-byte. No tests
were excluded, timeouts increased or packages published.
