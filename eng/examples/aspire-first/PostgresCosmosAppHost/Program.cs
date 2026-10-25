using AspireFirst.PostgresCosmos;
using Cohesive.Host.Composition;
var explain = new ApplicationExplainCommand(args);
var builder = DistributedApplication.CreateBuilder(explain.Arguments);
var model = ModelActivator.Create<PostgresCosmosFulfillmentInfrastructure>();
var association = model.Configure(builder);
if (explain.TryWrite(Console.Out, () => model.Explain(association))) return;
builder.Build().Run();
