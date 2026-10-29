using AspireFirst.Orders;
using Cohesive.Api.Execution.Services;
using Cohesive.Application;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class OrderReadSelectionTests
{
    [Fact]
    public void Materialized_selection_requires_explicit_eventual_consent()
    {
        new OrderReadPolicy().Validate();
        Assert.Throws<ArgumentException>(() => new OrderReadPolicy { Source = OrderDetailsReadSource.Materialized }.Validate());
        Assert.Throws<ArgumentException>(() => new OrderReadPolicy { Source = (OrderDetailsReadSource)99 }.Validate());
    }

    [Fact]
    public void Read_selection_changes_binding_consistency_without_changing_OrderApi()
    {
        var live = TestModels.Fulfillment();
        var selected = ModelActivator.Create<FulfillmentApplication>(services => services
            .AddSingleton(new FulfillmentHistoryPolicy())
            .AddSingleton(new OrderReadPolicy { Source = OrderDetailsReadSource.Materialized, AcceptEventualConsistency = true }));
        Assert.Equal(live.Api.Details.Id, selected.Api.Details.Id);
        Assert.Equal(live.Api.Details.Operation.Http!.Route, selected.Api.Details.Operation.Http!.Route);
        Assert.Equal(live.Api.Details.Operation.Http.Method, selected.Api.Details.Operation.Http.Method);
        Assert.Equal(live.Api.Details.Operation.Http.Parameters.Select(p => (p.Name, p.Source, p.Type, p.IsOptional)),
            selected.Api.Details.Operation.Http.Parameters.Select(p => (p.Name, p.Source, p.Type, p.IsOptional)));
        Assert.Equal(live.Api.Definition.Endpoints.Select(item => item.Id), selected.Api.Definition.Endpoints.Select(item => item.Id));
        var query = selected.Endpoints.Plan.QueryDependencies.Queries.Single(item =>
            item.Request.DefinitionDocument.DefinitionFingerprint == selected.Queries.OrderDetails.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        Assert.Equal(RelationQueryConsistency.Eventual, query.Consistency);
    }

    [Theory]
    [InlineData(RelationQueryConsistency.SingleSnapshot, false)]
    [InlineData(RelationQueryConsistency.IndependentSources, false)]
    [InlineData(RelationQueryConsistency.Eventual, true)]
    public void Eventual_reader_requires_an_eventual_requirement(RelationQueryConsistency required, bool accepted)
    {
        var reader = new Stub(TestModels.Fulfillment().Queries.OrderDetails);
        if (accepted) Assert.Same(reader, ServiceQueryConsistencyValidation.Require(reader, required));
        else Assert.Throws<ServiceBindingValidationException>(() => ServiceQueryConsistencyValidation.Require(reader, required));
    }

    sealed class Stub(RelationQuery<string, OrderDetails?> definition) : IRelationQueryReader<string, OrderDetails?>
    {
        public RelationQueryConsistency Consistency => RelationQueryConsistency.Eventual;
        public RelationQuery<string, OrderDetails?> Definition => definition;
        public Task<OrderDetails?> ReadAsync(string input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
