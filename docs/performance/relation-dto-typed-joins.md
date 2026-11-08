# Typed payloads for prepared relation joins

The canonical interpreter prepares and validates the static join inputs. The fused DTO compiler
then follows projection aliases to find the fields actually used by subsequent joins and the DTO
constructor. A static join qualifies for a typed lookup when it has one consumed scalar field and
every consumer takes an exact CLR string or decimal. Preparation uses the mapper's existing
conversions to fill the typed dictionary. A match provides the value directly, including when it
becomes the next join's key.

Joins with multiple consumed fields, a raw observation consumer, or a value that the scalar reader
cannot convert keep their general row lookup. Duplicate static keys still trigger canonical
fallback. Root admission, canonical ordering, and the published relation IR continue to govern
the result. The single-field boundary keeps the representation local to the existing kernel.

## Measurements

The baseline is checkpoint commit `5b7fa4ed` from [PR #498](https://github.com/cohesivesystems/cohesive/pull/498).
Both variants ran the same [load-summary benchmark](relation-dto-root-admission.md#reproduction)
on Apple M5 Max with .NET 10.0.13 Arm64 and BenchmarkDotNet 0.15.8. Only the Relations assembly
changed between the variants. The complete 20-case suite ran before the reverse-order repeat
focused on warm mapping, first use, and the prepared AutoMapper control.

| Loads | Whole-row lookups | Typed lookups | Warm allocation with typed lookups |
| ---: | ---: | ---: | ---: |
| 1 | 166 ns | 150 ns | 304 B |
| 128 | 19.26 µs | 16.17 µs | 7,329 B |
| 1,024 | 177.48 µs | 154.05 µs | 57,516 B |
| 8,192 | 1.678 ms | 1.465 ms | 459,017 B |

These are the reverse-order means. At 8,192 loads the first and reverse-order runs measured
17% and 13% less warm time, respectively, with essentially the same managed allocation. AutoMapper
with prepared lookups took 0.444 ms in the typed run and allocated about 852 KB.

At 8,192 loads, first-map means in the repeat were 208.20 ms before and 207.63 ms after. The typed
version allocated an additional 533 KB on that first map while building the specialized tables.
The first full run was more variable, so these data do not establish a first-use latency gain.

A warmed CPU capture confirmed both string and decimal dictionary lookups. The capture contained
418 mapping stacks, with ordinal validation in 31%, canonical sorting in 29%, and typed dictionary
lookups in about 18%.

## Verification

The Relations suite passes all 1,233 tests with warnings as errors. Differential coverage includes
typed fields passed to a subsequent join, duplicate lookup-key fallback, missing matches, and a raw
DTO that retains the Int64 kind of an integral sum.

The core suite passes 5,380 tests with warnings as errors and 68 environment-dependent skips.
