using AspireFirst.PostgresCosmos;
using Cohesive.Host.Composition;
var builder = DistributedApplication.CreateBuilder(args);
ModelActivator.Create<PostgresCosmosApp>().Configure(builder);
builder.Build().Run();
