using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Elastic;
using Elastic.Clients.Elasticsearch;

namespace AspireFirst.Materialization;

internal static class ShadowElastic
{
    internal const string TemplateName = "cohesive-order-shadow-v1";
    internal const string Template = """
        {"index_patterns":["cohesive-order-shadow-generation-*",".cohesive-order-shadow-control"],"template":{"settings":{"index":{"number_of_shards":"1","number_of_replicas":"0"}},"mappings":{"dynamic":true}}}
        """;

    internal static async Task<ElasticMaterializationTarget> BindAsync(HttpClient http,
        FulfillmentShadowDefinition definition, CancellationToken cancellationToken)
    {
        using var info = JsonDocument.Parse(await http.GetStringAsync("/", cancellationToken));
        var cluster = new ElasticClusterId(info.RootElement.GetProperty("cluster_uuid").GetString()!);
        using var installed = JsonDocument.Parse(await http.GetStringAsync("/_index_template/" + TemplateName, cancellationToken));
        using var expected = JsonDocument.Parse(Template);
        var actual = installed.RootElement.GetProperty("index_templates")[0].GetProperty("index_template");
        if (actual.TryGetProperty("composed_of", out var components) && components.GetArrayLength() != 0)
            throw new InvalidOperationException("The shadow template must not inherit unqualified component templates.");
        // Compare native declaration fields. Elasticsearch may add defaults, but must retain this template's pattern and body.
        foreach (var field in expected.RootElement.EnumerateObject())
            if (!actual.TryGetProperty(field.Name, out var value) || !JsonElement.DeepEquals(value, field.Value))
                throw new InvalidOperationException("Shadow template differs; initialize or explicitly migrate the dedicated shadow template.");
        var binding = new ElasticMaterializationTargetBinding(new("fulfillment/shadow/elastic/v1"), cluster,
            new("fulfillment/order-details/shadow"), definition.Document.Definition.Id, "orders-shadow",
            "cohesive-order-shadow-generation-", ".cohesive-order-shadow-control",
            new(TemplateName, new("sha256", "aspire-first/elastic-template-json/v1",
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Template)))), FulfillmentShadowRuntime.Authority),
            new(FulfillmentShadowRuntime.Authority, "postgres-session-advisory-lock/order-shadow"),
            new(new("fulfillment/shadow/search/v1"), new("elastic/order-shadow"), new("elastic/order-shadow/placement"),
                ElasticRelationQueryTargetProfile.Target, ElasticRelationQueryTargetProfile.ProfileId, "orders-shadow", []));
        var client = new ElasticsearchClient(new ElasticsearchClientSettings(http.BaseAddress!));
        return new(binding, ElasticMaterializationTargetPolicy.Default,
            new ElasticElasticsearchRuntimeBinding(cluster, client, FulfillmentShadowRuntime.Authority));
    }
}
