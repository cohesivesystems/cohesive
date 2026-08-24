# Cohesive.Processes.Contracts

Code-generation roots for the canonical Process definition and its persisted execution-definition envelope.

## Use When

- You need generated frontend contracts for exact canonical Process documents.
- You need the closed `ProcessNode` and `ProcessAwaitClause` unions with runtime discriminator inventories.
- You are building Process inspection, visualization, or authoring tooling without recreating Process IR in a UI.

The CLR `ProcessDefinition`, `ProcessNode`, and `ProcessAwaitClause` declarations remain the semantic authority.
The persisted `JsonDerivedType` metadata owns each closed-union inventory. This assembly only roots those types for
generation; it defines no parallel DTOs, node kinds, or execution behavior.

## TypeScript Wire Contracts

Generate the focused frontend package from the canonical JSON wire contract:

```bash
dotnet run --project src/Cohesive.CodeGen.Cli -- \
  --contracts src/Cohesive.Processes.Contracts/bin/Debug/net10.0/Cohesive.Processes.Contracts.dll \
  --out src/frontend/processes/src/generated \
  --emit shapes \
  --module processes \
  --shape-projection canonical-json \
  --external-shapes Cohesive.Model=@cohesivesystems/relations \
  --union-catalog ProcessNode=canonicalProcessNodeKinds \
  --union-catalog ProcessAwaitClause=canonicalProcessAwaitClauseKinds
```

The generated runtime catalogs are derived from the same union cases that generate the discriminated TypeScript
unions. Adding a CLR construct without a generated disposition therefore makes the committed artifact conformance
test fail; consumers do not maintain an independent list.

`ExecutionDefinitionDocument.definition` intentionally remains `unknown`: it is the canonical normalized JSON
envelope payload. A consumer that has admitted `kind: "process"` uses the separately generated `ProcessDefinition`
contract to interpret that payload. Strict schema, fingerprint, unknown-field, and activation checks remain owned by
the .NET execution-definition serializer and validator.

Types owned by `Cohesive.Model`, including `ValueContract`, `Expr`, and `TypeRef`, are imported from
`@cohesivesystems/relations`. The Process package does not duplicate that shared portable semantic model.

## Related Packages

- `Cohesive.Processes` for Process authoring, compilation, validation, and interpretation.
- `Cohesive.Relations.Contracts` and `@cohesivesystems/relations` for shared portable semantic-model contracts.
- `Cohesive.CodeGen.Cli` for deterministic contract generation.
