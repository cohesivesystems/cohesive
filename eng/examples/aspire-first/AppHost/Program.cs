using Microsoft.Extensions.DependencyInjection;

using Cohesive.Host.Composition;
using AspireFirst;

var explain = args.Contains("--explain", StringComparer.Ordinal);
var builder = DistributedApplication.CreateBuilder(args.Where(arg => arg != "--explain").ToArray());
var policy = AspireFirst.Orders.FulfillmentHistoryPolicy.FromConfiguration(builder.Configuration);
var model = ModelActivator.Create<FulfillmentInfrastructure>(services => services.AddSingleton(policy));
var association = model.Configure(builder);
if (!association.Deployment.IsComplete)
    throw new InvalidOperationException(string.Join(Environment.NewLine,
        association.Deployment.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
if (explain)
{
    Console.WriteLine(model.Explain(association).ToJsonString(new() { WriteIndented = true }));
    return;
}
builder.Build().Run();
