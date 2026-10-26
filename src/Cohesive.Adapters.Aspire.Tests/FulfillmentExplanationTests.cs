using System.Text.Json.Nodes;
using Aspire.Hosting;
using Cohesive.Adapters.Aspire;
using Cohesive.Adapters.Aspire.Postgres;
using AspireFirst;
using AspireFirst.Orders;
using AspireFirst.PostgresCosmos;
using Cohesive.Application;
using Cohesive.Storage;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class FulfillmentExplanationTests
{
    static ApplicationExplanationDocument Native(EntityStateAuthority authority)
    {
        var model = TestModels.Infrastructure(authority);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        return model.Explain(model.Configure(builder));
    }

    [Fact]
    public void Shared_report_entry_rejects_incomplete_native_association()
    {
        var model = TestModels.Infrastructure();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var database = builder.AddPostgres("postgres").AddDatabase("orders");
        var incomplete = builder.AttachCohesive(model, new("test/incomplete"), new("development"),
            associations => associations.Resource(FulfillmentInfrastructure.Store, database, authority: new("test")));
        Assert.False(incomplete.Deployment.IsComplete);
        Assert.Throws<ArgumentException>(() => model.Explain(incomplete));
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
        Assert.Equal(EntityStateAuthority.State, Assert.Single(state.History).Authority);
        Assert.Equal(EntityStateAuthority.Events, Assert.Single(events.History).Authority);
        Assert.Equal("orders_events", Assert.Single(events.History).Resource);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(state.ToJson())!["endpoints"],
            JsonNode.Parse(events.ToJson())!["endpoints"]));
        Assert.NotEqual(state.Deployment.Fingerprint, events.Deployment.Fingerprint);
        Assert.NotEmpty(state.Executions);
        Assert.Equal(3, state.Entities.Length);
        Assert.Equal(ApplicationExplanationScope.OfflineAdmission, state.Scope);
    }

    [Fact]
    public void Report_is_deterministic_and_links_query_endpoints_to_admitted_storage()
    {
        var report = Native(EntityStateAuthority.State);
        Assert.Equal(report.ToJson(), Native(EntityStateAuthority.State).ToJson());
        var queries = report.Storage.Queries;
        Assert.Equal(2, queries.Length);
        foreach (var query in queries)
        {
            Assert.Contains(report.Endpoints, endpoint => endpoint.Execution == query.Query);
            Assert.NotEmpty(query.Sources);
            Assert.NotNull(query.Plan);
        }
    }

    [Fact]
    public void Mixed_store_report_admits_independent_sources_without_claiming_a_cross_store_snapshot()
    {
        var model = ModelActivator.Create<PostgresCosmosFulfillmentInfrastructure>();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var association = model.Configure(builder);
        var report = model.Explain(association);
        Assert.All(report.Storage.Queries, query =>
            Assert.Equal(Cohesive.Relations.Execution.RelationQueryConsistency.IndependentSources, query.Consistency));
        Assert.Contains("resources/inventory", report.Deployment.ToJson());
        Assert.Empty(report.History);
    }
    [Fact]
    public void Json_boundary_preserves_embedded_canonical_documents()
    {
        var report = Native(EntityStateAuthority.Events);
        var json = JsonNode.Parse(report.ToJson())!;
        Assert.Equal(report.SchemaVersion, json["schemaVersion"]!.GetValue<string>());
        foreach (var (document, index) in report.Queries.Select((document, index) => (document, index)))
            Assert.True(JsonNode.DeepEquals(json["queries"]![index], JsonNode.Parse(
                Cohesive.Relations.Serialization.RelationQueryJsonSerializer.Serialize(document))));
        foreach (var (document, index) in report.Executions.Select((document, index) => (document, index)))
            Assert.True(JsonNode.DeepEquals(json["executions"]![index], JsonNode.Parse(
                Cohesive.Execution.ExecutionDefinitionJsonSerializer.Serialize(document))));
        Assert.True(JsonNode.DeepEquals(json["deployment"], JsonNode.Parse(report.Deployment.ToJson())));
        // Mutating a consumer's JSON cannot alter the admitted typed report.
        json["history"] = null;
        Assert.Single(report.History);
        Assert.NotNull(JsonNode.Parse(report.ToJson())!["history"]);
    }

}
