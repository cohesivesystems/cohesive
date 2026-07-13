# Cohesive.Relations.Contracts

Generated or shared TypeScript/.NET contract declarations for Cohesive relations.

## Install

```bash
dotnet add package Cohesive.Relations.Contracts
```

## Use When

- You need a contract assembly that exposes canonical relationship-catalog and relation/query shapes for code generation.
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

## Related Packages

- `Cohesive.Relations` for relation authoring and execution.
- `Cohesive.CodeGen.Cli` for contract discovery and code generation.
