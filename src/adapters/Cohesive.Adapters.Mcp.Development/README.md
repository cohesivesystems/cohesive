# Cohesive development MCP

A local stdio MCP server for inspecting and checking Cohesive execution semantics while agents edit ordinary
authoring source. It uses the official `ModelContextProtocol.Core` SDK (pinned to 1.3.0) and the shared
[`Cohesive.Semantics`](../../Cohesive.Semantics/README.md) workspace. It adds no runtime application access or source mutation tools.

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
2. Call `learn(expectedSnapshot)` for the compiled cancellation example and its framework build identity.
3. Call `resolve(query: "cancel", expectedSnapshot)` to find the owning definition.
4. Call `inspect(id: "orders/cancel", expectedSnapshot, pointer: "/definition")` to read its contract and body.
   Inspect `/metadata` for producer attribution; source-map references accompany inspection results.
5. Edit `eng/examples/development-mcp/OrderModel.cs`, for example changing the cancellation outcome text.
   A `workspace` call before re-export reports changed source inputs.
6. Rerun the exporter command. Call `workspace` for the new snapshot, then `check(expectedSnapshot)`.
7. Call `compare(baselineSnapshot: oldSnapshot, expectedSnapshot: newSnapshot)`. Semantic changes appear;
   comment-only changes produce a new input revision with no changed semantic fingerprint.

All tools are read-only and return structured results with output schemas. The adapter registers existing
transition, process and hosted-query document validators. It does not run builds, deploy, authorize operations,
or accept application changes. Normal source review remains the acceptance boundary.

`learn` embeds the exact `OrderModel.cs` file compiled by the example and tests. It covers typed transition
authoring only. Package-aware retrieval of concepts and examples is future work.

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
calling all six tools over a real stdio subprocess. The sample transition is authored through the fluent API,
not hand-constructed IR. This slice does not yet validate the Orders application's full API/client change scenario.
