# Cohesive.Relations.Contracts

Generated or shared TypeScript/.NET contract declarations for Cohesive relations.

## Install

```bash
dotnet add package Cohesive.Relations.Contracts
```

## Use When

- You need a contract assembly that exposes canonical relationship-catalog, relation-draft, and relation/query shapes for code generation.
- You need portable target-capability profiles or derived realization reports for explain tooling, deployment gates, or frontend visualization.
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

## Related Packages

- `Cohesive.Relations` for relation authoring and execution.
- `Cohesive.CodeGen.Cli` for contract discovery and code generation.
