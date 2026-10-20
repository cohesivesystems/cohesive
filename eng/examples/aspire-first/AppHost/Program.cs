using Microsoft.Extensions.DependencyInjection;

using Cohesive.Host.Composition;
using AspireFirst;

var builder = DistributedApplication.CreateBuilder(args);
var policy = AspireFirst.Orders.FulfillmentHistoryPolicy.FromConfiguration(builder.Configuration);
var association = ModelActivator.Create<FulfillmentInfrastructure>(services => services.AddSingleton(policy)).Configure(builder);
if (!association.Deployment.IsComplete)
    throw new InvalidOperationException(string.Join(Environment.NewLine,
        association.Deployment.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
builder.Build().Run();
