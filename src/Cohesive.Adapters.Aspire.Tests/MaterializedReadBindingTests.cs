using System.Net;
using AspireFirst.Materialization;
using Cohesive.Adapters.Elastic;
using Cohesive.Adapters.Postgres;
using Cohesive.Storage.Materialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class MaterializedReadBindingTests
{
    [Fact]
    public void Source_configuration_preparation_does_not_resolve_native_clients()
    {
        var model = TestModels.Fulfillment();
        var definitions = new FulfillmentMaterializations(model.Queries);
        var source = new FulfillmentMaterializationStorage(model.Storage, definitions).OrderDetails.ForReads("Replication");
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Replication:PositionKey"] = Convert.ToBase64String(new byte[32]),
                ["Replication:SlotGeneration"] = "test/v1"
            }).Build()).BuildServiceProvider();
        Assert.Same(definitions.OrderDetails, source.Materialization);
        Assert.NotNull(source.Prepare(services)); // No native clients are registered.
    }

    [Fact]
    public void Invalid_source_configuration_fails_before_native_client_resolution()
    {
        var model = TestModels.Fulfillment();
        var source = new FulfillmentMaterializationStorage(model.Storage, new(model.Queries)).OrderDetails.ForReads("Replication");
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Replication:PositionKey"] = "invalid-base64" }).Build()).BuildServiceProvider();
        Assert.Throws<FormatException>(() => source.Prepare(services));
    }

    [Fact]
    public void Missing_slot_generation_fails_before_native_client_resolution()
    {
        var model = TestModels.Fulfillment();
        var source = new FulfillmentMaterializationStorage(model.Storage, new(model.Queries)).OrderDetails.ForReads("Replication");
        using var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Replication:{PostgresMaterializationStorage.PositionKeyConfigurationKey}"] = Convert.ToBase64String(new byte[32])
            }).Build()).BuildServiceProvider();
        var error = Assert.Throws<InvalidOperationException>(() => source.Prepare(services));
        Assert.Contains($"Replication:{PostgresMaterializationStorage.SlotGenerationConfigurationKey}", error.Message);
    }

    [Fact]
    public async Task Target_resolves_existing_client_identity_without_taking_ownership()
    {
        var materialization = new FulfillmentMaterializations(TestModels.Fulfillment().Queries).OrderDetails;
        using var http = new HttpClient(new NativeMetadata()) { BaseAddress = new("http://127.0.0.1:1") };
        using var services = new ServiceCollection().AddKeyedSingleton<HttpClient>(ShadowElastic.Resource, http).BuildServiceProvider();
        var target = await ShadowElastic.Reads.BindAsync(services, materialization);
        Assert.Equal(ShadowElastic.Reads.Id, target.Descriptor.Id);
        Assert.Equal(materialization.Document.Definition.Id, target.Descriptor.MaterializationId);
        Assert.Contains("cluster_uuid", await http.GetStringAsync("/"));
    }

    [Fact]
    public async Task Target_identity_cannot_disagree_with_its_declaration()
    {
        var materialization = new FulfillmentMaterializations(TestModels.Fulfillment().Queries).OrderDetails;
        using var http = new HttpClient(new NativeMetadata()) { BaseAddress = new("http://127.0.0.1:1") };
        using var services = new ServiceCollection().AddKeyedSingleton<HttpClient>("wrong-target", http).BuildServiceProvider();
        var target = new ElasticMaterializationReadBinding(new("wrong-target"), ShadowElastic.BindAsync);
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.BindAsync(services, materialization));
    }

    [Fact]
    public async Task Cancelled_target_admission_does_not_resolve_a_client()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var materialization = new FulfillmentMaterializations(TestModels.Fulfillment().Queries).OrderDetails;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ShadowElastic.Reads.BindAsync(services, materialization, new(true)));
    }

    sealed class NativeMetadata : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/"
                    ? "{\"cluster_uuid\":\"test-cluster\"}"
                    : "{\"index_templates\":[{\"index_template\":" + ShadowElastic.Template + "}]}")
            });
    }
}
