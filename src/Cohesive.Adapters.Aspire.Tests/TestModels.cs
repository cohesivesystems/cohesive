using AspireFirst;
using AspireFirst.Orders;
using Cohesive.Application;
using Cohesive.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.Aspire.Tests;

internal static class TestModels
{
    internal static FulfillmentApplication Fulfillment(EntityStateAuthority authority = EntityStateAuthority.State) =>
        ModelActivator.Create<FulfillmentApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy(authority)));

    internal static FulfillmentInfrastructure Infrastructure(EntityStateAuthority authority = EntityStateAuthority.State) =>
        ModelActivator.Create<FulfillmentInfrastructure>(services => services.AddSingleton(new FulfillmentHistoryPolicy(authority)));
}
