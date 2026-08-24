# Cohesive.Relations.Contracts

Generated or shared TypeScript/.NET contract declarations for Cohesive relations.

## Install

```bash
dotnet add package Cohesive.Relations.Contracts
```

## Use When

- You need a contract assembly that exposes canonical relationship-catalog, relation-draft, and relation/query shapes for code generation.
- You need the generated portable `ValueContract` semantic type used by relation, Process, and execution API contracts.
- You need portable target-capability profiles, realization reports, source placements, physical plans, or lifecycle explain artifacts for explain tooling, deployment gates, or frontend visualization.
- You want relation contract definitions to be packaged separately from the relation runtime.
- You are building generated frontend or API artifacts from Cohesive relation metadata.

## TypeScript Wire Contracts

Canonical persisted documents should be projected with the System.Text.Json wire profile so generated
property names, scalar identifiers, enum values, dictionaries, and converter-backed values match the
JSON accepted by `Cohesive.Relations`:

```bash
dotnet run --project src/Cohesive.CodeGen.Cli -- \
  --contracts path/to/Cohesive.Relations.Contracts.dll \
  --out src/frontend/relations/src/generated \
  --emit shapes \
  --module relations \
  --shape-projection canonical-json
```

The generated TypeScript declarations describe the portable value shape. Strict duplicate-property,
unknown-property, and case-sensitive input enforcement remains the responsibility of the canonical
.NET document serializers because TypeScript uses open structural object types.

`ValueContract` is projected from `Cohesive.Model` as part of this package's shared semantic-model surface. It
retains canonical type, optional qualified shape, cardinality, presence, and nullability; the CLR declaration
remains semantic authority. Consumer generators can therefore externalize `Cohesive.Model` references to
`@cohesivesystems/relations` without recreating the contract locally.

`RelationQueryExplainArtifact` is the single code-generation root for relation/query explainability. Its
`$stage`-discriminated union exposes target-independent static compilation, profile feasibility, exact source
placement and bound realization, physical planning, target-neutral native-compilation evidence, and sanitized
runtime evaluation summaries. Deterministic lifecycle identity excludes the runtime stage; each sanitized
evaluation summary carries its own observation fingerprint. The artifact also exposes an optional compact
capability summary whose canonical
capability values and evidence IDs resolve into the retained profile and realization stages. It is an index rather
than a parallel capability model. Consumers should tolerate stages that were not attempted while preserving the
canonical order of stages that are present.

Persisted explain JSON must still be read and written through `RelationQueryExplainJsonSerializer`. Generated
TypeScript describes its portable shape but cannot enforce strict unknown-member rejection, stage affinity,
status/source consistency, canonical ordering, diagnostic projection, or fingerprint verification.

OpenTelemetry activities and metrics are intentionally not part of this contract projection. Runtime telemetry
uses stable low-cardinality names, statuses, counts, and metric dimensions; high-cardinality evaluation and artifact
fingerprints are trace-only correlation attributes. Sampled diagnostic events contain only stable code and severity.
A durable explain document retains the detailed deterministic evidence. Do not copy result values, source keys,
diagnostic prose, resolutions, or high-cardinality runtime identities into telemetry or into the capability summary.

Realization declaration enums use canonical strings for known values. A malformed declaration's retained
undefined 32-bit value is encoded as a JSON number, and only those diagnostic-preserving fields are projected
to TypeScript as the known enum union plus `number`.

Realization boundary limits, static facts, and measured validation values preserve the full non-negative
.NET `Int64` range by using canonical base-10 JSON strings. Boundary limits remain positive, while static
facts and measured values may be zero. The wire form rejects JSON numbers, leading zeroes, a leading plus,
negative zero, whitespace, and out-of-range values so fingerprint-significant integers have one exact
representation across runtimes. TypeScript consumers should retain the generated `string` value when
transporting or fingerprinting it and parse a validated value with `BigInt` when integer arithmetic is needed;
converting these values to `number` can lose precision.

Source-placement limits and physical-planning limits use the same canonical string-encoded `Int64`
wire representation. Physical plans are derived artifacts: consumers should retain their semantic-plan,
realization-report, placement, policy, stage-provenance, and fingerprint attribution together rather than
treating stage identifiers as an independent source of truth.

## Related Packages

- `Cohesive.Relations` for relation authoring and execution.
- `Cohesive.CodeGen.Cli` for contract discovery and code generation.
