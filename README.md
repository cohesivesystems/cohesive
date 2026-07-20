# Cohesive

Cohesive is a library suite for semantic system definition and orchestration of existing infrastructure. It favors defining the meaning of a system first, then attaching interpretations for storage, APIs, presentation, workflows, AI, training, and provider-specific infrastructure.

Detailed package docs live beside each package under `src/*/README.md` and are included in the corresponding NuGet package.

## Core Packages

| Package | Purpose |
| --- | --- |
| `Cohesive` | Core shape model, domain primitives, code generation abstractions, and prelude helpers. |
| `Cohesive.Configuration` | Configuration profiles, projection, and dependency selection. |
| `Cohesive.Relations` | Canonical relationship, relation/query authoring, realization, execution, mapping, and diagnostics. |
| `Cohesive.Relations.Contracts` | Contract assembly for relation-oriented generated surfaces. |
| `Cohesive.Transitions` | Entity fields, invariants, transitions, effects, and domain models. |
| `Cohesive.Processes` | Declarative process definitions and runtime execution contracts. |
| `Cohesive.Presentation` | Backend-declared presentation IR for navigation, views, actions, forms, and flows. |
| `Cohesive.Api` | Semantic API declarations and endpoint metadata. |
| `Cohesive.Storage` | Entity repository, observation stream, outbox, and storage adapter contracts. |
| `Cohesive.AI` | Inference, training, text, vector, ontology, and model registry contracts. |
| `Cohesive.Identity` | Identity context and scope resolution helpers. |
| `Cohesive.Host` | CLI, host, and runtime binding helpers. |
| `Cohesive.CodeGen.Cli` | Build-facing code generation for shapes, APIs, OpenAPI, GraphQL, and TypeScript artifacts. |
| `Cohesive.Analyzers` | Roslyn analyzers and source generators for Cohesive authoring patterns. |

## Adapter Packages

| Package | Purpose |
| --- | --- |
| `Cohesive.Adapters.AspNet` | ASP.NET Core endpoints and request binding for Cohesive APIs, entities, relations, processes, and identity. |
| `Cohesive.Adapters.AzureAppConfiguration` | Azure App Configuration integration for Cohesive configuration. |
| `Cohesive.Adapters.AzureML` | Azure Machine Learning model training and dataset registry integration. |
| `Cohesive.Adapters.AzureStorage` | Azure Blob Storage training artifacts, dataset streams, and target resolution. |
| `Cohesive.Adapters.Cosmos` | Cosmos DB storage, query, aggregation, outbox, and vector adapters. |
| `Cohesive.Adapters.DurableTask` | Azure Durable Task execution for Cohesive processes. |
| `Cohesive.Adapters.Elastic` | Elasticsearch query and aggregation compilers. |
| `Cohesive.Adapters.GitHub` | GitHub App authentication and repository access for code workflows. |
| `Cohesive.Adapters.GraphQL` | GraphQL schema emission from Cohesive API declarations. |
| `Cohesive.Adapters.Json` | JSON Schema helpers and validators for Cohesive shape graph documents. |
| `Cohesive.Adapters.MicrosoftML` | Microsoft ML tokenizer integration. |
| `Cohesive.Adapters.ONNX` | ONNX Runtime inference adapters. |
| `Cohesive.Adapters.OpenApi` | OpenAPI document emission from Cohesive API declarations. |
| `Cohesive.Adapters.Parquet` | Parquet row and column writing helpers. |
| `Cohesive.Adapters.Postgres` | Provider-neutral PostgreSQL relation/query compilation and SQL construction. |
| `Cohesive.Adapters.TypeScript` | TypeScript emitters for shapes, API clients, constants, and test mocks. |

## Frontend Packages

TypeScript packages live under `src/frontend/*` and publish under the `@cohesivesystems/*` npm scope. See `src/frontend/README.md` for package details.

## Prerequisites

- .NET SDK 10 (`net10.0`)
- Node.js 24
- pnpm 11 through Corepack

## Build and Test

```bash
dotnet restore Cohesive.sln
dotnet build Cohesive.sln -c Release
dotnet test Cohesive.sln

corepack enable
corepack prepare pnpm@11.1.3 --activate
corepack pnpm install --frozen-lockfile
corepack pnpm frontend:build
corepack pnpm frontend:test
```

## Local Package Iteration

Create local NuGet packages for downstream applications:

```bash
./eng/pack-local.sh
```

The script writes a uniquely versioned `0.1.0-dev.<timestamp>` package set to
the shared sibling feed at `.feeds/nuget/cohesive-local`. Pass an explicit
version as the first argument when needed. Local packages include portable PDBs
beside their assemblies, allowing IDE navigation to resolve back to the editable
files in this checkout.

Point downstream applications at the local NuGet feed:

```bash
dotnet nuget add source ../.feeds/nuget/cohesive-local --name cohesive-local
```

Reference Cohesive packages with the floating development version
`0.1.0-dev.*`. After changing Cohesive, package it again and force downstream
restore evaluation so NuGet selects the new immutable package version:

```bash
./eng/pack-local.sh
dotnet restore /path/to/Consumer.sln --force-evaluate
```

Do not overwrite an existing local package version. NuGet caches restored
packages by ID and version, so a new version is required for source and binary
changes to propagate reliably.

Start a local npm feed in one terminal:

```bash
corepack pnpm npm:feed
```

Publish local npm packages from another terminal:

```bash
corepack pnpm npm:publish-local 0.1.0-dev.local
```

Point the downstream app at the local npm feed:

```ini
@cohesivesystems:registry=http://localhost:4873/
```

The public package repository itself should keep `.npmrc` pointed at `https://registry.npmjs.org/`. Local feed routing belongs in consuming app repositories.

## Versioning

Use one repo-wide version for NuGet and npm packages until the package graph needs independent release lines.

```bash
corepack pnpm version:set 0.1.0-alpha.2
```

Version conventions:

| Stage | Example | Notes |
| --- | --- | --- |
| Local iteration | `0.1.0-dev.local.1` | Publish to local feeds only; do not commit. |
| Alpha | `0.1.0-alpha.2` | Public prerelease; breaking changes are allowed. |
| Preview | `0.1.0-preview.1` | More stable public preview. |
| RC | `0.1.0-rc.1` | Release candidate. |
| Stable | `0.1.0` | Stable package version. |
| Next breaking wave before 1.0 | `0.2.0-alpha.1` | Use minor bumps for larger breaking waves while pre-1.0. |

## Public Publishing

The `release-packages` workflow publishes packages from a tag such as `v0.1.0-alpha.2` or from a manual workflow run with the version input `0.1.0-alpha.2`.

- NuGet packages publish with NuGet trusted publishing through `NuGet/login`.
- npm packages are packed with pnpm, then published to npmjs.org with npm trusted publishing.
- npm prerelease dist-tags map as follows: `alpha.*` to `alpha`, `preview.*` to `preview`, `rc.*` to `rc`, and other prereleases to `next`.
- Stable npm versions publish with the `latest` dist-tag.

Required GitHub secret:

```text
NUGET_USER=<nuget.org trusted-publishing policy creator username>
```

The NuGet trusted publishing policy should use repository owner `cohesivesystems`, repository `cohesive`, workflow file `release-packages.yml`, and a blank environment unless the workflow later adds an explicit GitHub environment.

Before the first npm trusted-publishing release, each `@cohesivesystems/*` package must already exist on npmjs.com. Bootstrap each package once with a temporary/manual publish, then configure npm trusted publishing for the `cohesivesystems/cohesive` repository and `release-packages.yml` workflow.

## License

Apache-2.0
