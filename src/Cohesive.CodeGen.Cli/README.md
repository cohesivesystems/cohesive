# Cohesive.CodeGen.Cli

`Cohesive.CodeGen.Cli` is the build-facing entry point for Cohesive code generation.

## Install

```bash
dotnet add package Cohesive.CodeGen.Cli
```

Its current job is:

1. load a compiled contracts assembly
2. project exported CLR contract types into Cohesive shape IR
3. emit TypeScript, OpenAPI, and GraphQL artifacts into a target directory under a frontend app

The CLI is designed for build integration, idempotent writes, and incremental frontend workflows.

## Current Scope

Implemented today:

- `shapes` -> TypeScript declarations generated from exported CLR contracts
- `apis` -> TypeScript client functions generated from exported `ApiDefinition` members
- `openapi` -> OpenAPI 3.1 JSON generated from exported `ApiDefinition` members
- `graphql` -> GraphQL SDL and introspection JSON generated from exported `ApiDefinition` members

Reserved for follow-on work:

- `transitions`
- `processes`
- `invariants`

## Usage

```bash
dotnet exec path/to/cohesive-codegen.dll \
  --contracts path/to/MyApp.Contracts.dll \
  --out path/to/generated \
  --emit shapes,apis,openapi,graphql \
  --module myapp
```

Equivalent conceptual tool form:

```bash
cohesive-codegen \
  --contracts path/to/MyApp.Api.Contracts.dll \
  --out path/to/generated \
  --emit shapes,apis,openapi,graphql \
  --module myapp
```

## Arguments

- `--contracts`
  Path to the compiled .NET assembly that contains exported API contract types.

- `--out`
  Output directory for generated artifacts. For React + Vite this should usually be a folder under `src/generated`.

- `--emit`
  Comma-separated list of artifact kinds. Today `shapes`, `apis`, `openapi`, and `graphql` are implemented.

- `--module`
  Logical module name used in output filenames, for example `myapp.shapes.generated.ts`.

- `--help`
  Prints CLI usage.

## Output Behavior

For `--emit shapes,apis,openapi,graphql`, the CLI currently writes:

- `<module>.shapes.generated.ts`
- `<module>.api.generated.ts`
- `<module>.openapi.generated.json`
- `<module>.graphql.generated.graphql`
- `<module>.graphql.introspection.generated.json`

Example:

- `myapp.shapes.generated.ts`
- `myapp.api.generated.ts`
- `myapp.openapi.generated.json`
- `myapp.graphql.generated.graphql`
- `myapp.graphql.introspection.generated.json`

Writes are content-aware:

- if the generated content is unchanged, the file is left untouched
- if the generated content changes, the file is atomically replaced

This matters for frontend dev servers because it avoids unnecessary file watcher churn.

## React / Vite Workflow

Recommended layout:

```text
src/
  MyApp.Contracts/
  myapp-web/
    src/
      generated/
        myapp.shapes.generated.ts
```

Recommended loop:

1. `dotnet build src/MyApp.Contracts/MyApp.Contracts.csproj`
2. `npm run dev` inside the frontend app

Because the generated file lives under the Vite app's `src` tree, real contract changes trigger hot reload naturally.

## MSBuild Integration

The intended integration point is an `AfterTargets="Build"` target on the contracts project.

Typical shape:

```xml
<Target Name="GenerateTypeScriptContracts"
        AfterTargets="Build"
        Inputs="$(TargetPath);$(CohesiveCodeGenCliDll)"
        Outputs="$(CohesiveGeneratedShapesFile)"
        Condition="'$(CohesiveCodeGenEnabled)' == 'true'">
  <MakeDir Directories="$(CohesiveTypeScriptOutDir)" />
  <Exec Command="dotnet exec &quot;$(CohesiveCodeGenCliDll)&quot; --contracts &quot;$(TargetPath)&quot; --out &quot;$(CohesiveTypeScriptOutDir)&quot; --emit shapes,apis,openapi,graphql --module &quot;$(CohesiveCodeGenModule)&quot;" />
</Target>
```

Two separate mechanisms prevent rebuild churn:

1. MSBuild `Inputs` / `Outputs` can skip the target entirely when the generated file is already up to date.
2. The CLI itself skips rewriting the file when the generated text is identical.

Both are useful. The first avoids unnecessary process execution. The second protects correctness when the target does execute.

## Type Discovery

The CLI currently:

- loads the target assembly in an isolated `AssemblyLoadContext`
- inspects exported public CLR types
- filters to contract-like object types with readable public instance properties
- builds a `ShapeGraph`
- emits TypeScript from that graph
- discovers exported public static `ApiDefinition` members named `Definition`, `Api`, or `ApiDefinition`
- emits TypeScript client functions that call an injected `http` function
- emits OpenAPI and GraphQL schema projections from the same API definitions

This supports a POCO-first workflow while still targeting Cohesive IR internally.

## Direction

The longer-term model is:

- author shapes as CLR POCOs, or directly as Cohesive IR
- project those shapes into a common IR
- emit multiple target languages from the same semantic graph

TypeScript is the first target, not the terminal abstraction.
