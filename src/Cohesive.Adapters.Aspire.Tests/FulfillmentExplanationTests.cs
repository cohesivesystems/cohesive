using System.Text.Json.Nodes;
using Aspire.Hosting;
using AspireFirst;
using AspireFirst.Orders;
using AspireFirst.PostgresCosmos;
using Cohesive.Host.Composition;
using Cohesive.Storage;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class FulfillmentExplanationTests
{
    static JsonObject Native(EntityStateAuthority authority)
    {
        var model = TestModels.Infrastructure(authority);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        return model.Explain(model.Configure(builder));
    }

    [Fact]
    public void Failed_admission_never_exposes_successful_storage_explanation()
    {
        var model = TestModels.Fulfillment();
        var missing = new Cohesive.Api.Execution.Services.ServiceStorageComposition(
            Array.Empty<Cohesive.Api.Execution.Services.IServiceStorageComposition>());
        Assert.Throws<InvalidOperationException>(() => missing.Explanation);
        Assert.Throws<Cohesive.Api.Execution.Services.ServiceBindingValidationException>(() => missing.Validate([model.Endpoints.Plan]));
        Assert.Throws<InvalidOperationException>(() => missing.Explanation);
    }

    [Fact]
    public void Foreign_infrastructure_association_is_rejected()
    {
        var native = TestModels.Infrastructure();
        var mixed = ModelActivator.Create<PostgresCosmosFulfillmentInfrastructure>();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        Assert.Throws<ArgumentException>(() => native.Explain(mixed.Configure(builder)));
    }

    [Fact]
    public void State_and_events_keep_endpoint_query_identity_but_explain_different_storage_authorities()
    {
        var state = Native(EntityStateAuthority.State);
        var events = Native(EntityStateAuthority.Events);
        Assert.Equal("State", state["history"]!["authority"]!.GetValue<string>());
        Assert.Equal("Events", events["history"]!["authority"]!.GetValue<string>());
        Assert.Equal("orders_events", events["history"]!["database"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(state["endpoints"], events["endpoints"]));
        Assert.False(JsonNode.DeepEquals(state["deployment"], events["deployment"]));
        Assert.NotEmpty(state["executions"]!.AsArray());
        Assert.Equal(3, state["entities"]!.AsArray().Count);
        Assert.Contains("Offline", state["scope"]!.GetValue<string>());
    }

    [Fact]
    public void Report_is_deterministic_and_links_query_endpoints_to_admitted_storage()
    {
        var report = Native(EntityStateAuthority.State);
        Assert.Equal(report.ToJsonString(), Native(EntityStateAuthority.State).ToJsonString());
        var queries = report["storage"]!["queries"]!.AsArray();
        Assert.Equal(2, queries.Count);
        foreach (var query in queries)
        {
            Assert.Contains(report["endpoints"]!.AsArray(), endpoint => JsonNode.DeepEquals(endpoint!["execution"], query!["query"]));
            Assert.NotEmpty(query!["sources"]!.AsArray());
            Assert.NotNull(query["plan"]);
        }
    }

    [Fact]
    public void Mixed_store_report_admits_independent_sources_without_claiming_a_cross_store_snapshot()
    {
        var model = ModelActivator.Create<PostgresCosmosFulfillmentInfrastructure>();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var association = model.Configure(builder);
        var report = FulfillmentExplanation.Create(model.Application, model.Definition, association.Deployment);
        Assert.All(report["storage"]!["queries"]!.AsArray(), query =>
            Assert.Equal(nameof(Cohesive.Relations.Execution.RelationQueryConsistency.IndependentSources), query!["consistency"]!.GetValue<string>()));
        Assert.Contains("resources/inventory", report["deployment"]!.ToJsonString());
    }
}
