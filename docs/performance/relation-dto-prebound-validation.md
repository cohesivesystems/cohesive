# Prepared validation for complete CLR root projections

The fused mapper binds a complete root layout when it prepares the evaluator. Binding checks
that the slots cover the exact field definitions of the layout's owning shape graph. The
compiled CLR projection writes one concrete value, including explicit null, to each slot.
Every projected root checks those values against the bound definitions through the existing
field validator. The complete path uses the layout's field order and needs no presence bitmap.

On failure, the existing canonical observation admission supplies the diagnostic and preserves
declaration-order failure precedence. The general ordinal validator continues to serve
partially populated layouts and other input boundaries. The new differential test covers a
reordered 65-field layout, explicit null, multiple invalid fields, and rejection of partial
or synthetic nested layouts.

## Measurements

The baseline is the typed-lookup source tree from [PR #499](https://github.com/cohesivesystems/cohesive/pull/499).
The experiment uses the same [load-summary benchmark](relation-dto-root-admission.md#reproduction)
on Apple M5 Max, .NET 10.0.13 Arm64, and BenchmarkDotNet 0.15.8. The measured operation starts with
CLR roots and includes fresh field validation, canonical ordering, prepared joins, and DTO
construction. First map includes evaluator preparation and excludes evaluator creation.

| Loads | Repeated layout validation | Bound complete layout | Warm allocation, bound layout | Prepared AutoMapper |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 151 ns | 99 ns | 304 B | 37 ns |
| 128 | 16.50 µs | 10.04 µs | 7,329 B | 5.47 µs |
| 1,024 | 164.36 µs | 101.55 µs | 57,512 B | 47.93 µs |
| 8,192 | 1.571 ms | 1.005 ms | 458,988 B | 0.453 ms |

The reverse-order repeat measured 34–39% lower warm latency with essentially the same
allocation. At 8,192 loads, the initial full run measured 1.464 ms before and 1.021 ms after.
The final implementation keeps the projector's cached path free of the closure allocation
observed in the initial candidate.

First-map results varied between runs. At 8,192 loads the initial pair measured 262.45 ms
before and 268.62 ms after, while the repeat measured 235.11 ms before and 207.01 ms after.
At one load, the first map took 0.18 ms longer in the full run and 1.16 ms longer in the
repeat. Warm timing is the repeatable improvement in this experiment.

The warmed 8,192-load CPU trace sampled 419 mapping stacks. Sorting appeared in 40% and
typed dictionary lookups in about 22%. The shared field-validation routine appeared in 6%
of stacks.

## Verification

Release runs with warnings as errors passed all 1,233 Relations tests and 5,381 core tests,
with 68 environment-dependent core skips. The tested Core and Relations assemblies match the
ones used by the final benchmark.
