# Cohesive.Simulation.Cli

`Cohesive.Simulation.Cli` packages the provider-neutral reference provisioner as the `cohesive-sim` .NET tool. It
lets shell scripts, CI jobs, demo-environment setup, and Playwright global setup consume the same canonical portable
`WorldDefinition` used by .NET tests.

```bash
dotnet tool install Cohesive.Simulation.Cli --global

cohesive-sim provision \
  --world demo.world.json \
  --seed 42 \
  --target playwright/global-setup \
  --out test-results/demo-world.jsonl
```

Use `-` for standard input or standard output. Standard output contains JSON Lines only, so it can be piped directly
into another fixture process:

```bash
cohesive-sim provision \
  --world demo.world.json \
  --seed 42 \
  --target scripts/demo \
  --out -
```

The input document is fingerprint-verified and compiled before output begins. File output is first written to a
same-directory temporary file and moved over the requested path only after every batch succeeds, so invalid input,
cancellation, or provisioning failure preserves the previous artifact. Standard output is streaming and can contain
a partial batch if its consumer fails.

The emitted records use `WorldJsonLinesSink.Format` and retain deterministic artifact-manifest, world, run, batch,
population, sequence, named-exemplar, and replay provenance. The artifact ID and manifest fingerprint are independent
of `--target` and `--batch-size`; run and batch IDs bind that artifact to those execution choices. Each record's
`exemplars` array lets a script locate purposeful instances such as `customer-for-ui` without relying on output
position or incidental generated values. `--target` is required because logical destination identity participates in
run and batch IDs; filesystem paths and machine-specific working directories are not allowed to silently change
those identities.

JSON Lines records reference the exact manifest but do not repeat the embedded world definition on every line. A
consumer that retains generated data beyond the producing process should also persist the corresponding
`WorldArtifactManifest`; concrete joint framing for a manifest and large streamed batches remains a separate format
decision.

In Playwright, run the tool from `globalSetup` before creating application fixtures, then load the resulting JSON
Lines file or pass it to an application-specific seeding endpoint:

```typescript
import { execFileSync } from "node:child_process";

export default function globalSetup() {
  execFileSync("cohesive-sim", [
    "provision",
    "--world", "test/worlds/demo.world.json",
    "--seed", process.env.TEST_SEED ?? "42",
    "--target", "playwright/global-setup",
    "--out", "test-results/demo-world.jsonl",
  ], { stdio: "inherit" });
}
```

The CLI does not invent a second fixture model or storage policy: it interprets the portable world through
`WorldProvisioner`, while repository provisioning remains in the optional `Cohesive.Simulation.Storage` package.
Its typed options, validation, generated help, output routing, and invocation behavior come from `Cohesive.Cli`; the
tool does not maintain a parallel command parser.
