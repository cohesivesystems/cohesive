using Cohesive.Transitions.Model;
using Cohesive.Api;
using Cohesive.Model.Serialization;
using System.Text.Json;
using Cohesive.Application;
using Cohesive.Execution;
using Cohesive.Relations.Serialization;
using System.Reflection;
using AspireFirst.Orders;
using Cohesive.Adapters.Mcp.Development;
using Cohesive.Semantics;

if (args.Length is < 2 or > 3 || args[0] is not ("export" or "serve" or "exercise" or "wire-sample" or "cancellation-exercise") || (args.Length == 3 && args[0] is not ("exercise" or "cancellation-exercise")))
{
    Console.Error.WriteLine("Usage: Orders.Development <export|serve|exercise|wire-sample|cancellation-exercise> <repository-root> [authoring-base-commit for exercise]");
    return 2;
}
var root = Path.GetFullPath(args[1]);
switch (args[0])
{
    case "wire-sample":
        Console.WriteLine(JsonSerializer.Serialize(new OrderDetails("order", OrderStatus.Submitted,
            [new ReservationSummary("reservation", "sku", 1, 42)]), OrdersWireContext.Default.OrderDetails));
        break;
    case "export":
        Console.WriteLine(OrdersWorkspace.Export(root));
        break;
    case "serve":
        using (var reader = new StreamReader(typeof(OrdersWorkspace).Assembly.GetManifestResourceStream("Orders.Development.Guide.md")!))
            await DevelopmentMcpServer.RunAsync(DevelopmentMcpServer.CreateDefaultWorkspace(root, OrdersWorkspace.Bundle),
                new DevelopmentGuide(OrdersWorkspace.ProducerVersion, reader.ReadToEnd()));
        break;
    case "cancellation-exercise":
        await CancellationWorkflow.RunAsync(root, args.Length == 3 ? args[2] : "a2461a5098bd337977af8540ff233f7753501d36");
        break;
    case "exercise":
        await OrdersWorkflow.RunAsync(root, args.Length == 3 ? args[2] : null);
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
        return SemanticWorkspaceExport.WriteDocuments(root, Bundle, inputs, ProducerVersion, _ =>
        {
            var domain = new FulfillmentDomain();
            var process = new FulfillmentProcess(domain);
            var queries = new FulfillmentQueries(domain);
            var cancellation = new CancellationProcess(domain, queries);
            var endpoints = new OrderEndpoints(new OrderApi(domain, process, cancellation: cancellation), domain, queries).Plan;
            var executions = new[] { domain.OrderTransitions.Submit.Document, domain.InventoryTransitions.Reserve.Document,
                domain.InventoryTransitions.Release.Document, domain.ReservationTransitions.Create.Document, process.Definition.Document,
                domain.OrderTransitions.BeginCancellation.Document, domain.OrderTransitions.CompleteCancellation.Document,
                domain.ReservationTransitions.MarkReleased.Document, cancellation.Definition.Document, cancellation.Reservations.Contract.Document, cancellation.ReservationEvidence.Query.Document }
                .Concat(endpoints.Implementations.Where(e => e.Service is not null).Select(e => e.Service!.Declaration))
                .DistinctBy(d => d.Reference);
            var requests = endpoints.QueryDependencies.Queries.Select(q => q.Request).ToArray();
            var entities = domain.Definition.Entities;
            var shapes = requests.SelectMany(r => r.ShapeDocuments)
                .Concat(entities.Select(e => e.ShapeGraph?.Document ?? ShapeGraphDocument.FromGraph(e.StateShape.Graph)))
                .Select(d => JsonSerializer.Serialize(d, RelationQueryJsonSerializer.CreateOptions())).Distinct()
                .Select(json => JsonSerializer.Deserialize<JsonElement>(json));
            var catalogs = requests.Select(r => r.RelationshipCatalogDocument).OfType<RelationshipCatalogDocument>()
                .DistinctBy(d => d.CatalogFingerprint).Select(d => JsonSerializer.Deserialize<JsonElement>(RelationshipCatalogJsonSerializer.Serialize(d)));
            return [.. entities.Select(EntityStateDocument.Export), .. shapes, .. catalogs, .. executions.Select(d => JsonSerializer.Deserialize<JsonElement>(ExecutionDefinitionJsonSerializer.Serialize(d))),
                .. endpoints.QueryDependencies.Queries.Select(q => q.Request.DefinitionDocument)
                    .Select(d => JsonSerializer.Deserialize<JsonElement>(RelationQueryJsonSerializer.Serialize(d))),
                .. endpoints.Implementations.Select(e => ApiContractDocument.Lower(e.Endpoint,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web), "aspnet-web-defaults/v1",
                    e.Execution, e.Service?.Declaration.Reference, e.Service?.Operation.Id).ToJson())];
        });
    }
}
