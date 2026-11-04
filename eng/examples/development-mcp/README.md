# Development MCP authoring fixture

`OrderModel.cs` declares an order cancellation transition through the typed authoring API. `Program.cs` runs
lowering in a separate export process, records declared inputs and publishes a derived inspection bundle atomically.
The same model source is compiled in the integration tests. Input discovery and atomic publication use
`SemanticWorkspaceExport`; the server's independent `learn` guide explains the workflow.

See the [server quickstart](../../../src/adapters/Cohesive.Adapters.Mcp.Development/README.md) for launch commands
and the source-edit, refresh, check and compare loop. No storage, API, generated client or deployed runtime is
included in this fixture; those remain explicit follow-on integration work.
