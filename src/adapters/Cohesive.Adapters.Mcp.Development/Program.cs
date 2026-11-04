using Cohesive.Adapters.Mcp.Development;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: cohesive-mcp <repository-root> <relative-workspace-bundle.json>");
    return 2;
}

await DevelopmentMcpServer.RunAsync(DevelopmentMcpServer.CreateDefaultWorkspace(args[0], args[1]));
return 0;
