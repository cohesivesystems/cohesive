# Cohesive.Simulation.Cli

`Cohesive.Simulation.Cli` packages the provider-neutral core and relationship-world provisioners as the `cohesive-sim`
.NET tool. It
lets shell scripts, CI jobs, demo-environment setup, and Playwright global setup consume the same portable world and
retained artifact manifest used by .NET tests.

Create the immutable, content-addressed manifest first, then provision only from that retained authority:

```bash
dotnet tool install Cohesive.Simulation.Cli --global

cohesive-sim manifest \
  --world demo.world.json \
  --seed 42 \
  --out test-results/demo-world.manifest.json

cohesive-sim provision \
  --manifest test-results/demo-world.manifest.json \
  --target playwright/global-setup \
  --out test-results/demo-world.jsonl
```

`manifest` strictly deserializes and fingerprint-verifies the portable world, compiles it, and writes a canonical
`WorldArtifactManifest`. `provision` has no world-and-seed fallback: it strictly deserializes and verifies that
manifest before provisioning begins. Unsupported interpreter or entropy identities fail before a sink receives a
batch.

For a portable relationship world, use `--relationship-world` in place of `--world`. The manifest embeds the exact
fingerprint-verified relationship-world document, and `provision` selects the relationship interpreter from the
manifest rather than from another command-line option.

Both commands use `-` for standard input or standard output. File output is written to a same-directory temporary
file and moved over the requested path only after the complete command succeeds. In the recommended file workflow,
the immutable manifest therefore already exists before JSON Lines generation starts, and a failed or cancelled
provision preserves the prior complete JSON Lines file. Standard output is inherently streaming and can contain a
partial batch if its consumer fails.

The manifest and JSON Lines stream remain two independently framed artifacts rather than an invented aggregate file
format. Every v4 item cites the exact manifest schema, artifact ID, and manifest fingerprint, as well as world, run,
batch, population, exemplar, and replay provenance. The artifact ID and manifest fingerprint are independent of
`--target` and `--batch-size`; run and batch IDs bind that artifact to those execution choices. `--target` is a
required stable logical destination identity, not a machine-specific filesystem path.

Consumers can verify the entire stream against a separately loaded retained manifest before exposing any item:

```csharp
WorldArtifactManifest manifest = WorldArtifactManifestJsonSerializer.Deserialize(manifestJson);
await using var input = File.OpenRead("test-results/demo-world.jsonl");
WorldJsonLinesVerificationResult verified = await WorldJsonLinesVerifier.VerifyAsync(manifest, input);
```

The verifier uses the same internal v4 codec as the sink and rejects noncanonical or reordered records, unknown or
duplicated properties, mismatched artifact/world/run/batch identity, missing or extra items, incorrect exemplars or
replay evidence, and observations that do not exactly replay from the manifest. Tooling can call `ValidateAsync` to
receive stable `DocumentValidationResult` codes and JSON Pointer locations without treating invalid content as an
operational exception.

In Playwright, create and retain the manifest in `globalSetup`, then provision it before creating application
fixtures:

```typescript
import { execFileSync } from "node:child_process";

export default function globalSetup() {
  execFileSync("cohesive-sim", [
    "manifest",
    "--world", "test/worlds/demo.world.json",
    "--seed", process.env.TEST_SEED ?? "42",
    "--out", "test-results/demo-world.manifest.json",
  ], { stdio: "inherit" });

  execFileSync("cohesive-sim", [
    "provision",
    "--manifest", "test-results/demo-world.manifest.json",
    "--target", "playwright/global-setup",
    "--out", "test-results/demo-world.jsonl",
  ], { stdio: "inherit" });
}
```

The CLI does not invent a second fixture model or storage policy: it dispatches the retained manifest to its pinned
core or relationship-world interpreter, while repository provisioning remains in the optional
`Cohesive.Simulation.Storage` package.
Typed options, validation, generated help, output routing, and invocation behavior come from `Cohesive.Cli`; the tool
does not maintain a parallel command parser.
