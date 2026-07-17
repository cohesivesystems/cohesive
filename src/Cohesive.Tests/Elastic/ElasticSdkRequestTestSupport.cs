using System.Text;
using System.Text.Json;
using global::Elastic.Clients.Elasticsearch;
using global::Elastic.Transport;

namespace Cohesive.Tests.Elastic;

static class ElasticSdkRequestTestSupport
{
    static readonly ElasticsearchClient Client = new(
        new ElasticsearchClientSettings(new InMemoryRequestInvoker()));

    internal static JsonDocument Serialize(SearchRequest request) =>
        JsonDocument.Parse(SerializeToUtf8(request));

    internal static string SerializeToString(SearchRequest request) =>
        Encoding.UTF8.GetString(SerializeToUtf8(request));

    static byte[] SerializeToUtf8(SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using MemoryStream stream = new();
        Client.RequestResponseSerializer.Serialize(request, stream);
        return stream.ToArray();
    }
}
