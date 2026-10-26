using Microsoft.Extensions.DependencyInjection;

using Cohesive.Host.Composition;
using AspireFirst;

var explain = new ApplicationExplainCommand(args);
var builder = DistributedApplication.CreateBuilder(explain.Arguments);
var policy = AspireFirst.Orders.FulfillmentHistoryPolicy.FromConfiguration(builder.Configuration);
var model = ModelActivator.Create<FulfillmentInfrastructure>(services => services.AddSingleton(policy));
var association = model.Configure(builder);
if (!association.Deployment.IsComplete)
    throw new InvalidOperationException(string.Join(Environment.NewLine,
        association.Deployment.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
if (explain.TryWrite(Console.Out, () => model.Explain(association))) return;
builder.Build().Run();
