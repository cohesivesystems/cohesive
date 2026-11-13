# Packed identity keys for pooled DTO batches

Canonical ordering compares escaped root identities ordinally. The first experiment packed
four UTF-16 code units after the batch's common prefix and sorted ties by the full escaped
strings. The selected implementation sorts the entire differing suffix numerically when it
fits in eight single-byte code units. It packs the suffix most significant byte first, so
numeric order follows ordinal order.

The eligibility scan stops as soon as the longest key and remaining common prefix prove a
suffix too wide. Other inputs use the existing ordinal sort. Equal packed keys are checked
against the original identities. A collision between distinct identities restarts the ordinary
sort in admission order. Roots remain validated before sorting and duplicate rejection still
precedes DTO construction.

The packed path applies to pooled batches of at least 128 roots and rents one unsigned integer
per row. Zero- and one-root batches skip sorting. The differential tests compare output and
duplicate diagnostics for both short and long escaped keys, including prefix relationships,
percent escapes, and a Unicode identity.

## Measurements

The baseline is the [prepared validation checkpoint](relation-dto-prebound-validation.md)
(`cb653721`). Both variants use the [load-summary benchmark](relation-dto-root-admission.md#reproduction)
on Apple M5 Max with .NET 10.0.13 Arm64 and BenchmarkDotNet 0.15.8. The warm benchmark includes
root validation, ordering, both joins, and DTO construction. Only the Relations assembly
changes between variants.

The longer-key control replaces the load identity and matching line-item references with
`load-{i % 4}-common-long-segment-{i}`. These batches exercise the ordinary string-sort fallback.

| Loads | Ordinary string sort | Packed sort | Warm allocation, packed sort | Prepared AutoMapper |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 102 ns | 104 ns | 304 B | 42 ns |
| 128 | 10.57 µs | 8.54 µs | 7,329 B | 5.77 µs |
| 1,024 | 103.30 µs | 72.22 µs | 57,509 B | 52.12 µs |
| 8,192 | 1.055 ms | 0.630 ms | 458,957 B | 0.463 ms |

At 8,192 loads the prefix-and-tie candidate measured 0.812 ms against its 1.010 ms baseline.
The full-suffix candidate was faster in the repeat and in the final run, where the reduction was
40% with essentially the same allocation. The longer-key control measured 1.456 ms before and
1.375 ms after at 8,192 loads. Its allocation remained near 459 KB, and the 128- and 1,024-load
controls stayed within 2% of baseline. These results establish the gain for the short-key fixture.

The 8,192-load trace sampled 300 mapping stacks. The numeric sort appeared in about 22%, compared
with 40% for the string sort in the prepared-validation profile. The two prepared dictionary
lookups appeared in about 29% of the final capture.

## Verification

All 1,234 Relations tests passed in Release with warnings as errors. The benchmarked Relations
assembly matches the tested assembly.
