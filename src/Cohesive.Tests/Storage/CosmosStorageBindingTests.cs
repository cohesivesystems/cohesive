using System.Net;
using System.Text;
using Cohesive.Adapters.Cosmos;
using Cohesive.Model;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Storage;

public sealed class CosmosStorageBindingTests
{
    public sealed record Item(string Id, string Partition);
    sealed class Domain : DomainModel
    {
        public Domain() : base("1") { Items = Entity<Item>("test/item"); }
        public DomainEntity<Item> Items { get; }
    }

    [Theory]
    [InlineData("/partitionKey", true)]
    [InlineData("/other", false)]
    public async Task Binding_uses_named_shared_client_and_validates_native_partition(string path, bool valid)
    {
        var requests = new Metadata(path);
        var allocations = 0;
        var services = new ServiceCollection();
        services.AddKeyedSingleton<CosmosClient>("existing-account", (_, _) =>
        {
            allocations++;
            return new("https://localhost:8081/", Convert.ToBase64String(new byte[64]), new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway, CustomHandlers = { requests },
                HttpClientFactory = () => new HttpClient(new AccountMetadata())
            });
        });
        await using var provider = services.BuildServiceProvider();
        var binding = new CosmosEntityStorageBinding(new Domain().Items.Definition, "Id",
            "existing-account", "inventory", "stock", new("test/local"), "local", "Partition");
        if (!valid)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => binding.BindAsync(provider).AsTask());
        }
        else
        {
            var first = await binding.BindAsync(provider);
            var second = await binding.BindAsync(provider);
            Assert.Equal(first.Source.PersistedObservationType, second.Source.PersistedObservationType);
            Assert.Equal(2, requests.Reads);
        }
        Assert.Equal(1, allocations);
    }

    sealed class Metadata(string path) : RequestHandler
    {
        internal int Reads;
        public override Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("colls/stock", request.RequestUri.ToString());
            Reads++;
            return Task.FromResult(new ResponseMessage(HttpStatusCode.OK)
            {
                Content = new MemoryStream(Encoding.UTF8.GetBytes("{\"id\":\"stock\",\"partitionKey\":{\"paths\":[\"" + path + "\"],\"kind\":\"Hash\",\"version\":2}}"))
            });
        }
    }
    sealed class AccountMetadata : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"test","writableLocations":[{"name":"local","databaseAccountEndpoint":"https://localhost:8081/"}],
                    "readableLocations":[{"name":"local","databaseAccountEndpoint":"https://localhost:8081/"}],
                    "userConsistencyPolicy":{"defaultConsistencyLevel":"Session"},"enableMultipleWriteLocations":false}
                    """, Encoding.UTF8, "application/json")
            });
        }
    }
}
