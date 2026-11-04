using Cohesive.Adapters.Mcp.Development;
using ModelContextProtocol.Server;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: cohesive-mcp <repository-root> <relative-workspace-bundle.json>");
    return 2;
}

var tools = new DevelopmentTools(args[0], args[1]);
var options = new McpServerOptions
{
    ServerInfo = new() { Name = "cohesive-development", Version = "0.1.0" },
    ServerInstructions = "Call workspace first. Use its snapshotId for subsequent calls. Edit owning source, then rerun the project's exporter. ChangedInputs means the exported model is stale. Read limitations on every result. This server never executes application code or edits IR.",
    ToolCollection =
    [
        McpServerTool.Create(tools.Workspace),
        McpServerTool.Create(tools.Resolve),
        McpServerTool.Create(tools.Inspect),
        McpServerTool.Create(tools.Check),
        McpServerTool.Create(tools.Compare),
        McpServerTool.Create(tools.Learn)
    ]
};
await using var server = McpServer.Create(new StdioServerTransport(options), options);
await server.RunAsync();
return 0;
