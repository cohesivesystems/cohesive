using AspireFirst.PostgresCosmos;
using Cohesive.Host.Composition;
var builder = DistributedApplication.CreateBuilder(args);
ModelActivator.Create<PostgresCosmosFulfillmentInfrastructure>().Configure(builder);
builder.Build().Run();
