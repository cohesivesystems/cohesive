using AspireFirst.Orders;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.Aspire.Tests;

// Focused entity/query HTTP fixtures contribute only that declared API subset. Process execution has
// its own full-surface fixture, so these tests can inject the stale-write repository deterministically.
internal static class OrderHttpTestBindings
{
    internal static void Map(WebApplication app, FulfillmentApplication model, IEntityRepository<Order> orders,
        IRelationQueryReader<string, OrderDetails?> details, IRelationQueryReader<string, ReservationAvailability[]> availability)
    {
        var definition = ApiDefinition.From(model.Api.Create, model.Api.Get, model.Api.Submit, model.Api.Details, model.Api.Availability);
        var prepared = new ServiceApiBindingsBuilder().Implement(model.Api, definition, bindings => OrderEndpoints.ConfigureOrders(bindings, model))
            .Build(new ServiceCollection(), new Persistence(model, orders, details, availability));
        app.MapServices(prepared);
    }
    sealed class Persistence(FulfillmentApplication model, IEntityRepository orders,
        IRelationQueryReader<string, OrderDetails?> details, IRelationQueryReader<string, ReservationAvailability[]> availability) : IServicePersistence
    {
        public IEntityRepository Repository(ValueContract observation) => orders;
        public IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query, int maximumRows, long maximumBytes)
        {
            if (ReferenceEquals(query, model.Queries.OrderDetails)) return (IRelationQueryReader<TInput, TResult>)(object)details;
            if (ReferenceEquals(query, model.Queries.ReservationAvailability)) return (IRelationQueryReader<TInput, TResult>)(object)availability;
            throw new InvalidOperationException("Unexpected query in focused HTTP fixture.");
        }
    }
}
