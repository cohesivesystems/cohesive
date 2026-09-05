# Repository seeding and Playwright

Simulation supports two different boundaries that should not be conflated:

- `Cohesive.Simulation.Storage` commits generated entity snapshots to Cohesive repositories.
- `WorldJsonLinesSink` and `cohesive-sim provision` produce a portable artifact for another process to verify and
  import.

The CLI does not know an application's repositories, credentials, replacement policy, or transaction guarantees.
Writing JSONL is therefore not itself database seeding.

## Seed repositories directly from .NET

Bind every world population to the exact repository that owns its entity shape:

```csharp
using Cohesive.Simulation.Storage;

var sink = new RepositoryWorldProvisioningSink(
    destinationId: "demo/local",
    operationContext: OperationContext.Create(),
    bindings:
    [
        new RepositoryWorldPopulationBinding("customers", customerRepository),
        new RepositoryWorldPopulationBinding("orders", orderRepository)
    ]);

await WorldProvisioner.ProvisionAsync(
    artifact,
    sink,
    new(batchSize: 100));
```

The world already owns entity identity. The sink validates exact shape compatibility and entity semantics before
repository access. Its effective target identity includes destination, entity type, qualified shape, state version,
and requested batch atomicity.

The generic repository contract has no durable provisioning ledger. The reference sink performs deterministic
upserts and reports `Committed`; it does not claim exactly-once delivery. Provider adapters can add reconciliation or
create-only policies only when their target capabilities support those guarantees.

## Use a .NET seeder from Playwright

The shortest complete browser-test path is an application-owned .NET seeder that references the same world-building
code and `Cohesive.Simulation.Storage`. Invoke it from `globalSetup`, then start or reset browser fixtures only after
the command succeeds:

```typescript
import { execFileSync } from "node:child_process";

export default function globalSetup() {
  execFileSync("dotnet", [
    "run",
    "--project", "test/SeedDemo/SeedDemo.csproj",
    "--",
    "--seed", process.env.TEST_SEED ?? "42",
    "--manifest", "test-results/demo.manifest.json",
  ], { stdio: "inherit" });
}
```

The seeder should retain the exact manifest it used. Playwright can use stable exemplar names such as
`customer-for-browser` rather than relying on arbitrary search results or undocumented ordinal assumptions.

## Exchange a portable artifact with another process

When the application owns a non-.NET importer or a remote seeding endpoint, create, provision, and verify the artifact
before import:

```bash
cohesive-sim manifest \
  --world test/worlds/demo.world.json \
  --seed 42 \
  --out test-results/demo.manifest.json

cohesive-sim provision \
  --manifest test-results/demo.manifest.json \
  --target playwright/demo-import \
  --out test-results/demo.jsonl

cohesive-sim verify \
  --manifest test-results/demo.manifest.json \
  --jsonl test-results/demo.jsonl \
  > test-results/demo.verification.json

./application-owned-importer \
  test-results/demo.manifest.json \
  test-results/demo.jsonl
```

The importer remains responsible for mapping canonical observations to its target, enforcing authorization and
replacement policy, and recording the artifact/run/batch IDs needed for reconciliation. It must not reinterpret
entity identity or relationship references.

## Relationship worlds

Use `--relationship-world` instead of `--world` when the portable source is a
`RelationshipWorldDefinitionDocument`. Provisioning and verification dispatch from the retained interpreter identity;
there is no second CLI switch that can contradict the manifest.

## Agentic assurance

Retain these files as test evidence:

- the canonical world or relationship-world document;
- the exact manifest;
- the verified JSONL artifact when the destination consumes it;
- the CLI verification report;
- the seed and any failing replay token;
- the application-specific import or repository receipt.

Together they let an agent reproduce inputs, distinguish semantic changes from target failures, and cite stable
exemplars without treating generated output as a parallel source of truth.

See the [executable freight example](../../Cohesive.Examples/Simulation/SimulationAdoptionExamples.cs) for shared
typed shapes, relationship generation, repository seeding, retained artifacts, verification, and exemplar discovery.
