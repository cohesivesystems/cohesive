using AspireFirst.PostgresCosmos;
using Cohesive.Application;
using Cohesive.Adapters.Aspire.Application;

return await AspireApplication.RunAsync(args, builder =>
{
    var model = ModelActivator.Create<PostgresCosmosFulfillmentInfrastructure>();
    return model.Explain(model.Configure(builder));
});
