using AspireFirst.PostgresCosmos;
using Cohesive.Host.Composition;
var explain = args.Contains("--explain", StringComparer.Ordinal);
var builder = DistributedApplication.CreateBuilder(args.Where(arg => arg != "--explain").ToArray());
var model = ModelActivator.Create<PostgresCosmosFulfillmentInfrastructure>();
var association = model.Configure(builder);
if (explain)
{
    var report = AspireFirst.Orders.FulfillmentExplanation.Create(model.Application, model.Definition, association.Deployment);
    Console.WriteLine(report.ToJsonString(new() { WriteIndented = true }));
    return;
}
builder.Build().Run();
