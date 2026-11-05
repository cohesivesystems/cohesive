# Cohesive development MCP

A local stdio MCP server for inspecting and checking Cohesive domain semantics while agents edit ordinary
authoring source. It uses the official `ModelContextProtocol.Core` SDK (pinned to 1.3.0) and the shared
[`Cohesive.Semantics`](../../Cohesive.Semantics/README.md) workspace. It adds no runtime application access or source mutation tools.

For a real application workflow with client configuration and an isolated source-change exercise, see
[Orders development onboarding](../../../eng/examples/aspire-first/Development/README.md).

## Run the example

From the repository root with .NET 10:

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
to stdout. Usage errors go to stderr. The executable can also be packed as a .NET tool named `cohesive-mcp`.
The repository root and export location are selected by the launching host, not by tool calls.

## Agent loop

1. Call `workspace` and retain `snapshotId`. Check `changedInputs` and `limitations`.
2. Call `learn()` for versioned authoring guidance. It also works before an export exists.
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

## Custom hosts

The standalone executable supplies conventional domain validators and schema readers. To support
another document kind, compose the same server in a host project referencing this adapter:

```csharp
var workspace = new SemanticWorkspace(root, bundlePath, domainValidators);
await DevelopmentMcpServer.RunAsync(workspace,
    guide: new DevelopmentGuide("my-guide/v1", authoringGuidance),
    cancellationToken: cancellationToken);
```

`domainValidators` uses the workspace's existing dictionary of canonical kinds and domain-owned validation
functions. Shared integrity checks always run. This composition needs no server fork or runtime plug-in loader;
the workspace and validator policy remain protocol-independent. `CreateDefaultWorkspace` supplies the standalone
configuration when no customization is needed. The host owns any extra validator dependencies.

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
calling all eight tools over a real stdio subprocess. The sample transition is authored through the fluent API,
not hand-constructed IR. The Orders exercise additionally verifies native query-to-endpoint changes; full API/client compatibility remains outside coverage.

## Bounded impact

`impact(id, expectedSnapshot, maxDepth: 4, limit: 20)` returns potential transitive consumers with shortest evidence
paths. Inspect each edge and `incompleteDefinitions`; `depthLimitReached` and `resultLimitReached` report omitted
scope. Cycles are suppressed. Bounds are depth 1–16, 1–100 results, 256 exported documents and 4096 indexed edges.
The default adapter references Cohesive.Application to reuse its endpoint projection; no API model is duplicated
inside MCP. The endpoint artifact fingerprint covers route/binding inspection, not a full API semantic contract.
