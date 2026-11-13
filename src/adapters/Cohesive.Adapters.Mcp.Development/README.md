# Cohesive development MCP

A local stdio MCP server for inspecting and checking Cohesive domain semantics while agents edit ordinary
authoring source. It uses the official `ModelContextProtocol.Core` SDK (pinned to 1.3.0) and the shared
[`Cohesive.Semantics`](../../Cohesive.Semantics/README.md) workspace. It adds no runtime application access or source mutation tools.

For a real application workflow with client configuration and an isolated source-change exercise, see
[Orders development onboarding](../../../eng/examples/aspire-first/Development/README.md).

## Quickstart: connect a client

Prerequisites: Git, the .NET 10 SDK and package-feed access for the first restore. No database, Docker,
Aspire host or application credentials are needed for this quickstart. Commands below use a POSIX shell.
Use a fixed checkout for reproducibility; keep server, exporter and domain packages on compatible pinned versions.

From a fresh checkout:

```sh
git clone https://github.com/cohesivesystems/cohesive.git
cd cohesive
```

From its repository root:

```sh
dotnet run --project eng/examples/development-mcp -- "$PWD" "$PWD/tmp/development-workspace.json"
dotnet build src/adapters/Cohesive.Adapters.Mcp.Development
```

Configure an MCP client to launch the built server (replace `/absolute/cohesive` with your checkout):

```json
{
  "mcpServers": {
    "cohesive": {
      "command": "dotnet",
      "args": [
        "/absolute/cohesive/src/adapters/Cohesive.Adapters.Mcp.Development/bin/Debug/net10.0/Cohesive.Adapters.Mcp.Development.dll",
        "/absolute/cohesive",
        "tmp/development-workspace.json"
      ]
    }
  }
}
```

Launch the compiled DLL directly so build output cannot contaminate stdio. The server writes only MCP traffic
to stdout. Usage errors go to stderr. The project is configured to pack as a .NET tool named `cohesive-mcp`; this quickstart uses a source build and does not assume a published tool version.
The repository root and export location are selected by the launching host, not by tool calls.

After connecting, confirm that `tools/list` exposes the nine tools below. In a client, ask:

> Use the Cohesive tools to inspect `orders/cancel`. Explain its rule and owning source, then describe how
> you would validate a change. Do not edit the exported JSON.

Call `learn` with `{}`, then `workspace` with `{}`. The fixture exports `orders/cancel`; a successful
workspace response has a nonempty `snapshotId` and empty `changedInputs`. Tool results below are their
MCP structured content, not text extracted from a client display. Exact hashes vary by checkout.

For a Codex configuration example and a real application, use the
[Orders host setup](../../../eng/examples/aspire-first/Development/README.md#build-export-and-connect).
Client configuration syntax varies; the command and arguments above are the portable stdio launch contract.

## Agent loop

1. Call `learn` with `{}` for versioned host guidance. It also works before an export exists.
2. Call `workspace` and retain `snapshotId`. Check `changedInputs` and `limitations`.
3. Call `resolve(query: "cancel", expectedSnapshot)` to find the owning definition.
4. Call `inspect(id: "orders/cancel", expectedSnapshot, pointer: "/definition")` to read its contract and body.
   Inspect `/metadata` for producer attribution; source-map references accompany inspection results.
5. Edit `eng/examples/development-mcp/OrderModel.cs`, for example changing the cancellation outcome text.
   A `workspace` call before re-export reports changed source inputs.
6. Rerun the exporter command. Call `workspace` for the new snapshot, then `check(expectedSnapshot)`.
7. Call `compare(baselineSnapshot: oldSnapshot, expectedSnapshot: newSnapshot)`. Semantic changes appear;
   comment-only changes produce a new input revision with no changed semantic fingerprint.

All tools are read-only and return structured results with output schemas. The adapter registers existing
transition, process, service, hosted-query and native relation document validators. Endpoint inspection artifacts have integrity validation only. It does not run builds, deploy, authorize operations,
or accept application changes. Normal source review remains the acceptance boundary.

`references(id, expectedSnapshot)` projects Process, Service and hosted-query dependencies through their owning domains. Native query and endpoint readers are explicitly partial. It reports
exact, content-only, identity-only, missing and stale evidence and explicit incomplete coverage. Transition documents have no built-in
execution-definition calls; unknown kinds and extensions are not assumed reference-free. `inspect.sourceMappings`
exposes optional compiler-owned portable line spans. See the Orders walkthrough for the combined navigation loop.

`learn` returns an adapter-owned guide or the host's supplied `DevelopmentGuide`, without reading the workspace.
The example remains an executable fixture, rather than a dependency of the server. Package-aware retrieval is future work.

## Tool reference

All nine tools are read-only. Editing, building, exporting and running application tests happen through the
agent's ordinary development tools. `expectedSnapshot` must be the exact ID returned by `workspace`.

| Tool | Required arguments | Result / use |
| --- | --- | --- |
| `learn` | none | Versioned host guidance; no bundle required. |
| `workspace` | none | Scope, current snapshot, declared-input freshness and subjects. |
| `resolve` | `query`, `expectedSnapshot` | Matching identities; resolve ambiguity before inspecting. |
| `inspect` | `id`, `expectedSnapshot` | Selected JSON, child pointers and source attribution; `pointer` defaults to the root. |
| `references` | `id`, `expectedSnapshot` | Direct consumers with reference evidence and incomplete coverage. |
| `impact` | `id`, `expectedSnapshot` | Bounded transitive consumers; optional `maxDepth` and `limit`. |
| `check` | `expectedSnapshot` | Integrity and available domain validation; inspect `domainValidationAvailable` and diagnostics. |
| `compare` | `baselineSnapshot`, `expectedSnapshot` | Added, removed and changed document fingerprints. |
| `compatibility` | `id`, `baselineSnapshot`, `expectedSnapshot` | Directional API wire findings; optional `direction`. |

For example, after substituting the returned snapshot:

```json
{"query":"orders/cancel","expectedSnapshot":"<snapshotId>"}
```

Pass that to `resolve`, then use its returned identity with `inspect`:

```json
{"id":"orders/cancel","expectedSnapshot":"<snapshotId>","pointer":"/definition"}
```

Workspace operations return an envelope with `snapshotId`, `inputRevision`, `semanticFingerprint`,
`changedInputs`, `limitations` and `result`. `learn` instead returns `version` and `content`.
The same snapshot can become stale relative to edited source: an unchanged `snapshotId` is not a freshness check.

`workspace`, `resolve`, `references`, `check` and `compare` accept `offset`/`limit`; follow the returned
`nextOffset` until null (for `compare`, it is under `result.changes`). `check` also accepts `diagnosticOffset`
for each definition's diagnostics. Inspection children use `offset`/`limit`, and source attribution uses
`sourceOffset`/`sourceLimit`. `impact` is bounded rather than paginated: inspect its truncation flags.
Keep the server session alive across before/after comparisons; it retains only four snapshots in memory.

## Bring your own application

Start with the [minimal exporter](../../../eng/examples/development-mcp/Program.cs) and
[typed source fixture](../../../eng/examples/development-mcp/OrderModel.cs). For a mixed application,
use [OrdersWorkspace.Export](../../../eng/examples/aspire-first/Development/Program.cs) as the composition example.
The minimal fixture demonstrates a transition, not the full API/recovery tool surface.

1. Create a .NET 10 development console project with source `ProjectReference` entries to your application's
   authoring project and `src/adapters/Cohesive.Adapters.Mcp.Development/Cohesive.Adapters.Mcp.Development.csproj`
   in a pinned Cohesive checkout. This is the composition used by the Orders host. The adapter is packaged as a
   .NET tool; do not assume that tool package is an embeddable library PackageReference. Align all domain/exporter
   dependencies with that checkout. Keep runtime startup separate from model construction.
2. In an explicit `export` command, invoke existing typed builders to lower the model. Use
   `SemanticWorkspaceExport.DiscoverInputs` for selected source directories, plus explicit build, configuration,
   lockfile and convention inputs. Use `Write` for execution documents or `WriteDocuments` for mixed domain JSON.
   The library hashes declared inputs and atomically publishes the bundle. Do not hand-author canonical IR.
3. In a separate `serve` command, use the default composition and supply project guidance:

```csharp
using Cohesive.Adapters.Mcp.Development;

var workspace = DevelopmentMcpServer.CreateDefaultWorkspace(
    root: repositoryRoot, bundlePath: "tmp/workspace.json");
await DevelopmentMcpServer.RunAsync(workspace,
    guide: new DevelopmentGuide("my-application-guide/v1", authoringGuidance),
    cancellationToken: cancellationToken);
```

Here `repositoryRoot`, `authoringGuidance` and `cancellationToken` are supplied by your console host.
The guide should name the source entry points, exact rebuild/export command, model scope and unsupported behavior.
The [Orders guide](../../../eng/examples/aspire-first/Development/Guide.md) is a concrete example. Avoid embedding
answers to benchmark tasks or private scoring material in guidance or exports.

4. Build and export before connecting. Configure your client to launch the compiled host's `serve` command.
   Verify discovery, `learn`, a fresh `workspace`, one `inspect` and `check`. Edit source, verify `changedInputs`,
   rebuild/export, then verify a new snapshot and comparison in the same session.

The default composition registers domain-owned validators, reference readers and schema readers for the supported
execution, relation, entity, shape, API and recovery documents. If you introduce another schema, construct
`SemanticWorkspace` explicitly with `documentReaders` keyed by its original schema version and the appropriate
`validators`, `referenceReaders` and `additionalReferences`. See the
[default composition](DevelopmentMcpServer.cs) and [reader contracts](../../Cohesive.Semantics/README.md#direct-consumers-and-portable-source-locations).
Replacing the workspace configuration does not implicitly inherit default readers. Preserve the existing entries
when extending them; a validators-only constructor is insufficient for mixed-domain exports. No server fork or
runtime plug-in loader is required. Custom readers and validators are trusted host code.

For portable API compatibility, export `ApiContractDocument` using the effective serializer configuration.
ASP.NET hosts can use `AspNetApiContract.Lower` for the qualified ProblemDetails interpretation. Version the
serializer profile when its behavior or interpretation changes; old coverage gaps are not retroactively cleared.

## Troubleshooting and interpretation

| Symptom | Action |
| --- | --- |
| Server exits or tools do not appear | Check .NET 10, absolute DLL/root paths and stderr. Build first; do not put build output on the protocol's stdout. Restart the client connection after configuration changes. |
| Missing export | Run the host's export command. `learn` can work before a bundle exists; semantic tools cannot. |
| Nonempty `changedInputs` | Rebuild and re-export. `--no-build` after source edits can export stale compiled source. |
| Expected snapshot rejected | Call `workspace` again and inspect what changed before retrying. Do not silently substitute a new snapshot for an already reviewed one. |
| Baseline unavailable | The server restarted or the four-snapshot cache evicted it. Re-export the intended baseline from its source revision and retain it in the same session before exporting the proposed revision. |
| Null or missing inspection value | Read `valueKind` and `isTruncated`; follow child pages for truncated containers. Oversized scalars need local artifact inspection. See the complete-value reader below. |
| `domainValidationAvailable=false` | Integrity checking is not domain validation. Recovery assessments are integrity-checked reports; run target conformance separately. Register a validator for custom executable kinds. |
| `Unknown` API verdict | Read diagnostic paths and coverage gaps. Align supported exporter/serializer profiles or add qualification; do not count Unknown as a pass. |
| Incomplete references or truncated impact | Inspect `incompleteDefinitions` and every edge's evidence; increase bounds only where allowed. Missing evidence never proves absence. |
| Unsupported schema or malformed replacement | Upgrade matching readers/exporters or repair and regenerate the artifact. The server does not silently serve an older export as current. |

The selected root constrains bundle/input paths; it is not an OS security sandbox for the host or the agent.
For evals, launch with only participant-visible files and keep the oracle outside the process's accessible environment.
Source maps are attribution, not authorization. Builds and custom host code run with the launcher's permissions.

## Diagnostic transport caveat

The current .NET 10 JSON Schema exporter used by SDK 1.3.0 fails on the default `ImmutableArray` constructor
parameters in `DocumentDiagnosticEvidence`. The `check` adapter therefore serializes each existing check result
to `JsonElement` using the shared strict serializer. Its outer revision/page schema remains typed; inner diagnostic
item schemas are open JSON. This local projection preserves the diagnostic wire data and avoids a duplicate
diagnostic model. Remove it when SDK/runtime schema inference supports those defaults.

## Validation

```sh
dotnet test src/Cohesive.Semantics.Tests
```

Tests exercise deterministic export, input freshness, failed refreshes, stale request rejection, ambiguity,
fingerprint integrity, comparison, bounded retention, path containment, JSON pointers, and an official SDK client
calling the workspace tools over a real stdio subprocess. The sample transition is authored through the fluent API,
not hand-constructed IR. The Orders exercise additionally verifies native query-to-endpoint changes and checks the ninth tool, `compatibility`, against real generated JSON payloads.

## Bounded impact

`impact(id, expectedSnapshot, maxDepth: 4, limit: 20)` returns potential transitive consumers with shortest evidence
paths. Inspect each edge and `incompleteDefinitions`; `depthLimitReached` and `resultLimitReached` report omitted
scope. Cycles are suppressed. Bounds are depth 1–16, 1–100 results, 256 exported documents and 4096 indexed edges.
Shape graphs expand into qualified, independently inspectable subjects; each workspace admits at most 4096 distinct subjects. Distinct graph slices retain their own fingerprints. Conflicting qualified members are rejected.

## Directional API compatibility

The portable API document retains HTTP bindings, result variants, serializer profile, qualified core types and execution/service references. Use `compatibility` with an endpoint ID, retained baseline snapshot and fresh expected snapshot. The default direction is existing client to proposed server; the reverse direction tests the proposed client against the existing server. Under `http-json-tolerant-readers/v2`, requests must remain acceptable to the server and responses readable by the client. Findings identify wire paths and return Compatible, Breaking or Unknown. Custom serializer behavior, authorization changes and unsupported type semantics can prevent a proof. This policy does not qualify arbitrary clients, deployed routes, or runtime behavior.

For Orders, resolve `AvailableStock`, call `impact` on the returned field identity, and retain the snapshot before editing. After rebuilding and exporting:

```json
{"id":"api:Order.Details","baselineSnapshot":"<before>","expectedSnapshot":"<after>","direction":"ExistingClientToProposedServer"}
```

Policy v2 retains reader-required evidence independently of writer presence, including optional constructor defaults.
The [API wire policy documentation](../../Cohesive.Api/README.md) describes qualified wrappers, dictionaries and
ASP.NET ProblemDetails. Re-export both source revisions with the qualified producer when historical evidence is
insufficient; a current contract self-comparison is not proof of cross-revision compatibility.

Legacy application endpoint documents remain readable as route/binding projections. They do not carry portable wire types.

Inspection responses include `valueKind` and `isTruncated`, so clients can distinguish selected JSON null from omitted
oversized content and reconstruct objects with numeric property names without mistaking them for arrays.
`Cohesive.Semantics.WorkspaceInspectionReader.ReadAsync` provides bounded complete-value retrieval over an inspection
callback. Pin the same document and `expectedSnapshot` in that callback; the reader follows all child pages and
preserves JSON shape. It requires the updated inspection contract rather than guessing the shape of older responses.
