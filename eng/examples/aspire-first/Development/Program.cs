using System.Reflection;
using AspireFirst.Orders;
using Cohesive.Adapters.Mcp.Development;
using Cohesive.Semantics;

if (args.Length != 2 || args[0] is not ("export" or "serve" or "exercise"))
{
    Console.Error.WriteLine("Usage: Orders.Development <export|serve|exercise> <repository-root>");
    return 2;
}
var root = Path.GetFullPath(args[1]);
switch (args[0])
{
    case "export":
        Console.WriteLine(OrdersWorkspace.Export(root));
        break;
    case "serve":
        using (var reader = new StreamReader(typeof(OrdersWorkspace).Assembly.GetManifestResourceStream("Orders.Development.Guide.md")!))
            await DevelopmentMcpServer.RunAsync(DevelopmentMcpServer.CreateDefaultWorkspace(root, OrdersWorkspace.Bundle),
                new DevelopmentGuide(OrdersWorkspace.ProducerVersion, reader.ReadToEnd()));
        break;
    case "exercise":
        await OrdersWorkflow.RunAsync(root);
        break;
}
return 0;

internal static class OrdersWorkspace
{
    internal const string Project = "eng/examples/aspire-first/Development";
    internal const string DomainSource = "eng/examples/aspire-first/Orders.Model/FulfillmentDomain.cs";
    internal const string Bundle = "tmp/orders-development.json";
    internal static readonly string ProducerVersion = typeof(OrdersWorkspace).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    internal static string Export(string root)
    {
        // Conservative C# toolchain scope, explicitly declared rather than inferred dependency completeness.
        var sourceDirectories = Directory.EnumerateDirectories(Path.Combine(root, "src"))
            .Where(path => Path.GetFileName(path).StartsWith("Cohesive", StringComparison.Ordinal) || Path.GetFileName(path) == "adapters")
            .Select(path => Path.GetRelativePath(root, path));
        var inputs = SemanticWorkspaceExport.DiscoverInputs(root,
            [.. sourceDirectories, "eng/examples/aspire-first/Orders.Model", Project],
            ["Directory.Build.props", "nuget.config", Project + "/Guide.md"]);
        return SemanticWorkspaceExport.Write(root, Bundle, inputs, ProducerVersion, _ =>
        {
            var domain = new FulfillmentDomain();
            // Preserve the domain's identities, authored revisions and compiler-produced source maps.
            return [domain.OrderTransitions.Submit.Document, domain.InventoryTransitions.Reserve.Document,
                domain.InventoryTransitions.Release.Document, domain.ReservationTransitions.Create.Document,
                new FulfillmentProcess(domain).Definition.Document];
        });
    }
}
