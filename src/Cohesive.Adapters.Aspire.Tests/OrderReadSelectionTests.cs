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
    public async Task Shared_preparation_rejects_invalid_comparison_before_native_binding()
    {
        var model = TestModels.Fulfillment();
        var definition = new AspireFirst.Materialization.FulfillmentMaterializations(model.Queries).OrderDetails;
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "null");
            var calls = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Cohesive.Adapters.Elastic.ElasticMaterializationReadSelection.PrepareAsync(
                model.Queries.OrderDetails, model.Queries.OrderDetailsRelation, definition, path, _ =>
                {
                    calls++;
                    throw new InvalidOperationException("Native preparation must not run.");
                }));
            Assert.Equal(0, calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Read_selection_changes_binding_consistency_without_changing_OrderApi()
    {
        var live = TestModels.Fulfillment();
        var selected = ModelActivator.Create<FulfillmentApplication>(services => services
            .AddSingleton(new FulfillmentHistoryPolicy())
            .AddSingleton(new RelationQueryReadPolicy { Source = RelationQueryReadSource.Materialized, AcceptEventualConsistency = true }));
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
