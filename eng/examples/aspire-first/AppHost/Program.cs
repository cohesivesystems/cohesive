using Microsoft.Extensions.DependencyInjection;
using Cohesive.Application;
using Cohesive.Adapters.Aspire.Application;
using AspireFirst;

return await AspireApplication.RunAsync(args, builder =>
{
    var policy = AspireFirst.Orders.FulfillmentHistoryPolicy.FromConfiguration(builder.Configuration);
    var model = ModelActivator.Create<FulfillmentInfrastructure>(services => services.AddSingleton(policy));
    return model.Explain(model.Configure(builder));
});
