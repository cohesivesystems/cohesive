using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.MaterializationHarness.Control;
using Cohesive.MaterializationHarness.Materialize;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationHarnessElasticFaultHandlerTests
{
    static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task RetryableBulkRejectionSplitsGzipRequestAndCorrelatesRejectedRetry()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var markerPath = Path.Combine(directory, "elastic-fault.json");
            RecordingHandler inner = new(CreateBulkResponse);
            using HttpClient client = new(new MaterializationHarnessElasticFaultHandler(
                innerHandler: inner,
                plan: Plan(
                    kind: MaterializationHarnessElasticFaultKind.RetryableBulkRejection,
                    markerPath: markerPath)));

            using var injected = BulkRequest(("freight-index", "item-1"), ("freight-index", "item-2"));
            using var injectedResponse = await client.SendAsync(injected);
            var responseBody = await injectedResponse.Content.ReadAsByteArrayAsync();
            using var response = JsonDocument.Parse(responseBody);

            Assert.True(injectedResponse.IsSuccessStatusCode);
            Assert.True(response.RootElement.GetProperty("errors").GetBoolean());
            Assert.Equal(2, response.RootElement.GetProperty("items").GetArrayLength());
            var firstAppliedRequest = Assert.Single(inner.Requests);
            Assert.Empty(firstAppliedRequest.ContentEncodings);
            Assert.Equal(["item-1"], firstAppliedRequest.ItemIds);

            using var retry = BulkRequest(("freight-index", "item-2"));
            using var retryResponse = await client.SendAsync(retry);
            Assert.True(retryResponse.IsSuccessStatusCode);

            var evidence = await ReadEvidenceAsync(markerPath);
            Assert.Equal(2, evidence.MatchingRequestCount);
            Assert.Equal(["item-1"], evidence.AppliedItems.Select(static item => item.Item));
            Assert.Equal(["item-2"], evidence.RejectedItems.Select(static item => item.Item));
            Assert.True(evidence.RejectedItems.SequenceEqual(evidence.ExactRetryItems));
            Assert.NotNull(evidence.ExactRetryRequestFingerprint);
            Assert.Null(evidence.ReconciliationRequestPath);
            Assert.Equal(["item-2"], inner.Requests[1].ItemIds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AppliedPromotionResponseLossRecordsAliasReconciliationRead()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var markerPath = Path.Combine(directory, "elastic-fault.json");
            RecordingHandler inner = new(static request => request.RequestUri!.AbsolutePath == "/_aliases"
                ? JsonResponse("{\"acknowledged\":true}")
                : JsonResponse("{}"));
            using HttpClient client = new(new MaterializationHarnessElasticFaultHandler(
                innerHandler: inner,
                plan: Plan(
                    kind: MaterializationHarnessElasticFaultKind.AppliedPromotionResponseLoss,
                    markerPath: markerPath)));

            using var promotion = JsonRequest(
                HttpMethod.Post,
                "http://localhost/_aliases",
                "{\"actions\":[{\"add\":{\"index\":\"freight-index\",\"alias\":\"freight-read\"}}]}");
            await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(promotion));

            using var reconciliation = new HttpRequestMessage(
                HttpMethod.Get,
                "http://localhost/_alias/freight-read,marker-next?ignore_unavailable=true");
            using var reconciliationResponse = await client.SendAsync(reconciliation);
            Assert.True(reconciliationResponse.IsSuccessStatusCode);

            var evidence = await ReadEvidenceAsync(markerPath);
            Assert.True(evidence.ResponseLostAfterApply);
            Assert.Equal(2, evidence.MatchingRequestCount);
            Assert.Equal("/_alias/freight-read,marker-next", evidence.ReconciliationRequestPath);
            Assert.Null(evidence.ExactRetryRequestFingerprint);
            Assert.Equal(["/_aliases", "/_alias/freight-read,marker-next"],
                inner.Requests.Select(static request => request.Path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static MaterializationHarnessElasticFaultPlan Plan(
        MaterializationHarnessElasticFaultKind kind,
        string markerPath) => new(
        RunIdentity: "run/test",
        Provider: "postgres",
        Kind: kind,
        MarkerPath: markerPath,
        ReadAlias: "freight-read");

    static HttpRequestMessage BulkRequest(params (string Index, string Item)[] items)
    {
        StringBuilder body = new();
        foreach (var item in items)
        {
            body.Append("{\"index\":{\"_index\":\"")
                .Append(item.Index)
                .Append("\",\"_id\":\"")
                .Append(item.Item)
                .Append("\",\"version\":1,\"version_type\":\"external\"}}\n")
                .Append("{\"value\":\"")
                .Append(item.Item)
                .Append("\"}\n");
        }
        return GzipRequest(HttpMethod.Post, "http://localhost/_bulk", body.ToString());
    }

    static HttpRequestMessage JsonRequest(HttpMethod method, string uri, string body) =>
        GzipRequest(method, uri, body);

    static HttpRequestMessage GzipRequest(HttpMethod method, string uri, string body)
    {
        using MemoryStream encoded = new();
        using (GZipStream gzip = new(encoded, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(body));
        HttpRequestMessage request = new(method, uri)
        {
            Content = new ByteArrayContent(encoded.ToArray())
        };
        request.Content.Headers.ContentEncoding.Add("gzip");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        return request;
    }

    static HttpResponseMessage CreateBulkResponse(HttpRequestMessage request)
    {
        var items = RecordingHandler.ReadItemIds(request);
        var responseItems = string.Join(",", items.Select(item =>
            $"{{\"index\":{{\"_index\":\"freight-index\",\"_id\":\"{item}\",\"status\":201}}}}"));
        return JsonResponse($"{{\"took\":1,\"errors\":false,\"items\":[{responseItems}]}}");
    }

    static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    static async Task<MaterializationHarnessElasticFaultObservation> ReadEvidenceAsync(string path) =>
        JsonSerializer.Deserialize<MaterializationHarnessElasticFaultObservation>(
            await File.ReadAllTextAsync(path),
            EvidenceJson) ?? throw new InvalidOperationException("The test fault marker was empty.");

    static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cohesive-elastic-fault-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(
                Path: request.RequestUri!.AbsolutePath,
                ContentEncodings: [.. request.Content?.Headers.ContentEncoding ?? []],
                ItemIds: ReadItemIds(request)));
            var result = response(request);
            result.RequestMessage = request;
            result.Headers.TryAddWithoutValidation("X-Elastic-Product", "Elasticsearch");
            return Task.FromResult(result);
        }

        internal static string[] ReadItemIds(HttpRequestMessage request)
        {
            if (request.Content is null || request.RequestUri!.AbsolutePath != "/_bulk")
                return [];
            var bytes = request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (request.Content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase))
            {
                using MemoryStream source = new(bytes, writable: false);
                using GZipStream gzip = new(source, CompressionMode.Decompress);
                using MemoryStream decoded = new();
                gzip.CopyTo(decoded);
                bytes = decoded.ToArray();
            }
            var lines = Encoding.UTF8.GetString(bytes)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines
                .Where(static (_, index) => index % 2 == 0)
                .Select(static line => JsonDocument.Parse(line).RootElement
                    .GetProperty("index")
                    .GetProperty("_id")
                    .GetString()!)
                .ToArray();
        }
    }

    sealed record RecordedRequest(
        string Path,
        string[] ContentEncodings,
        string[] ItemIds);
}
