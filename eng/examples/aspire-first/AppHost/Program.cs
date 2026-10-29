using Microsoft.Extensions.Configuration;
using AspireFirst.Orders;
using Microsoft.Extensions.DependencyInjection;
using Cohesive.Application;
using Cohesive.Adapters.Aspire.Application;
using AspireFirst;

return await AspireApplication.RunAsync(args, builder =>
{
    var policy = AspireFirst.Orders.FulfillmentHistoryPolicy.FromConfiguration(builder.Configuration);
    var reads = builder.Configuration.GetSection("OrderReads").Get<OrderReadPolicy>() ?? new();
    reads.Validate();
    var model = ModelActivator.Create<FulfillmentInfrastructure>(services => services.AddSingleton(policy).AddSingleton(reads));
    return model.Explain(model.Configure(builder));
});
