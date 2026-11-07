using Microsoft.Extensions.DependencyInjection;
using Cohesive.Relations.Execution;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Cohesive.Adapters.Aspire;
using Cohesive.Adapters.Aspire.Postgres;
using AspireFirst;
using AspireFirst.Orders;
using AspireFirst.PostgresCosmos;
using Cohesive.Application;
using Cohesive.Storage;
using Cohesive.Execution;
using Cohesive.Api.Services;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class FulfillmentExplanationTests
{
    static ApplicationExplanationDocument Native(EntityStateAuthority authority)
    {
        var model = TestModels.Infrastructure(authority);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        return model.Explain(model.Configure(builder));
    }

    [Theory]
    [InlineData(EntityStateAuthority.State, ProcessExecutionLifetime.Host, false)]
    [InlineData(EntityStateAuthority.State, ProcessExecutionLifetime.Durable, true)]
    [InlineData(EntityStateAuthority.Events, ProcessExecutionLifetime.Durable, false)]
    [InlineData(EntityStateAuthority.Events, ProcessExecutionLifetime.Durable, true)]
    public void Original_service_policy_is_explained_independently_of_history_and_read_selection(
        EntityStateAuthority authority, ProcessExecutionLifetime lifetime, bool selected)
    {
        var model = ModelActivator.Create<FulfillmentInfrastructure>(services => services
            .AddSingleton(new FulfillmentHistoryPolicy(authority))
            .AddSingleton(new FulfillmentExecutionPolicy { Lifetime = lifetime })
            .AddSingleton(new RelationQueryReadPolicy { Source = selected ? RelationQueryReadSource.Materialized : RelationQueryReadSource.Live,
                AcceptEventualConsistency = selected }));
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var report = model.Explain(model.Configure(builder));
        var start = report.Endpoints.Single(endpoint => endpoint.Http?.Route == "/fulfillment");
        var poll = report.Endpoints.Single(endpoint => endpoint.ResultOf == start.Id);
        var service = Assert.Single(report.Services, candidate => candidate.Reference == start.Service!.Definition);
        Assert.Equal(service.Reference, start.Service!.Definition);
        Assert.Equal(service.Reference, poll.Service!.Definition);
        var operation = Assert.IsType<ServiceProcessOperation>(start.Service.Operation);
        var original = service.GetDefinition<ServiceDefinition>().Operations.Single(item => item.Id == operation.Id);
        var options = Cohesive.Model.Serialization.StrictDocumentJson.CreateOptions();
        Assert.True(JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(original, options),
            System.Text.Json.JsonSerializer.SerializeToNode<ServiceOperation>(operation, options)));
        Assert.Equal(lifetime, operation.Execution!.Lifetime);
        Assert.Equal(ServiceProcessCompletion.Admission, operation.Execution.Completion);
        Assert.Equal(operation.Process, start.Execution);
        Assert.Equal(operation.Process, Assert.IsType<ServiceProcessResultOperation>(poll.Service.Operation).Process);
        Assert.Equal(authority, Assert.Single(report.History).Authority);
        var details = report.Storage.Queries.Single(query => query.Query == report.Endpoints.Single(endpoint => endpoint.Http?.Route == "/orders/{id}/details").Execution);
        Assert.Equal(selected ? RelationQueryConsistency.Eventual : RelationQueryConsistency.SingleSnapshot, details.Read!.Consistency);
        var json = JsonNode.Parse(report.ToJson())!;
        Assert.Contains(json["services"]!.AsArray(), item => JsonNode.DeepEquals(item, JsonNode.Parse(ExecutionDefinitionJsonSerializer.Serialize(service))));
        Assert.Equal(ApplicationExplanationScope.OfflineAdmission, report.Scope);
    }

    [Fact]
    public void Failed_pair_admission_rolls_back_service_links_with_endpoint_coverage()
    {
        var pair = TestModels.Fulfillment().Api.Fulfill;
        var plan = new Cohesive.Api.Execution.Services.ServiceImplementationPlan();
        plan.Declare(pair.Definition);
        plan.Implement(pair.Definition, pair.Result, pair.Process.Definition.Reference);
        Assert.Throws<Cohesive.Api.Execution.Services.ServiceBindingValidationException>(() => plan.ImplementProcess(pair.Definition, pair));
        // Failure on the second member must not leave the first member or its service evidence behind.
        plan.Implement(pair.Definition, pair.Start, pair.Process.Definition.Reference);
        Assert.Equal(2, plan.Implementations.Length);
        Assert.All(plan.Implementations, link => Assert.Null(link.Service));
    }

    [Fact]
    public void Serving_selection_is_explained_offline_without_loading_comparison_or_native_clients()
    {
        var infrastructure = ModelActivator.Create<FulfillmentInfrastructure>(services => services
            .AddSingleton(new FulfillmentHistoryPolicy())
            .AddSingleton(new RelationQueryReadPolicy { Source = RelationQueryReadSource.Materialized, AcceptEventualConsistency = true }));
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var selected = infrastructure.Explain(infrastructure.Configure(builder));
        var live = Native(EntityStateAuthority.State);
        var detailId = selected.Endpoints.Single(endpoint => endpoint.Http?.Route == "/orders/{id}/details").Execution;
        var details = selected.Storage.Queries.Single(query => query.Query == detailId);
        Assert.Equal("Elasticsearch", details.Read!.Backend);
        Assert.Equal(AspireFirst.Materialization.ShadowElastic.Resource, details.Read.Resource);
        Assert.Equal(RelationQueryConsistency.Eventual, details.Read.Consistency);
        Assert.Equal(RelationQueryConsistency.Eventual, details.Consistency);
        Assert.All(live.Storage.Queries, query =>
        {
            Assert.Equal("PostgreSQL", query.Read!.Backend);
            Assert.Equal("orders", query.Read.Resource);
            Assert.Equal(RelationQueryConsistency.SingleSnapshot, query.Read.Consistency);
        });
        Assert.All(selected.Storage.Queries.Where(query => query.Query != detailId), query => Assert.Equal("PostgreSQL", query.Read!.Backend));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(live.ToJson())!["endpoints"], JsonNode.Parse(selected.ToJson())!["endpoints"]));
        var encoded = JsonNode.Parse(selected.ToJson())!["storage"]!["queries"]!.AsArray();
        Assert.Contains(encoded, entry => entry!["read"]!["backend"]!.GetValue<string>() == "Elasticsearch"
            && entry["read"]!["consistency"]!.GetValue<string>() == "Eventual");
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
        Assert.Equal(4, queries.Length);
        foreach (var query in queries)
        {
            if (query.Query.DefinitionId.Value == "fulfillment/reservations-by-order")
                Assert.Contains(report.Endpoints, endpoint => endpoint.Execution?.DefinitionId.Value == "fulfillment/cancel");
            else
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
        {
            Assert.Equal(RelationQueryConsistency.IndependentSources, query.Consistency);
            Assert.Equal("Composed", query.Read!.Backend);
            Assert.Equal(RelationQueryConsistency.IndependentSources, query.Read.Consistency);
        });
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
        var options = Cohesive.Model.Serialization.StrictDocumentJson.CreateOptions();
        Assert.True(JsonNode.DeepEquals(json["infrastructure"],
            System.Text.Json.JsonSerializer.SerializeToNode(report.Infrastructure, options)));
        foreach (var (entity, index) in report.Entities.Select((entity, index) => (entity, index)))
        {
            Assert.True(JsonNode.DeepEquals(json["entities"]![index],
                System.Text.Json.JsonSerializer.SerializeToNode(entity, options)));
            Assert.NotNull(json["entities"]![index]!["shape"]);
            Assert.NotNull(json["entities"]![index]!["name"]);
        }

        // Mutating a consumer's JSON cannot alter the admitted typed report.
        json["history"] = null;
        Assert.Single(report.History);
        Assert.NotNull(JsonNode.Parse(report.ToJson())!["history"]);
    }

}
