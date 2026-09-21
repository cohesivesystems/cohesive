using System.Net;
using System.Text.Json;

namespace Cohesive.Adapters.Azure.Infra;

// Shared bounded transport. Callers supply deadlines and own admission policy/document disposal.
static class AzureInfrastructureHttpReader
{
    const int MaximumResponseBytes = 262144;

    internal static async Task<(JsonDocument? Document, string? Error)> ReadAsync(
        HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.RequestMessage?.RequestUri is { } effective && effective != uri) return (null, "unexpectedEndpoint");
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (null, "accessDenied");
        if (response.StatusCode == HttpStatusCode.NotFound) return (null, "notFound");
        if (response.StatusCode != HttpStatusCode.OK) return (null, "httpFailure");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) return (null, "responseTooLarge");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, cancellationToken);
            if (count == 0) break;
            if (buffer.Length + count > MaximumResponseBytes) return (null, "responseTooLarge");
            buffer.Write(chunk, 0, count);
        }
        return (JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length))), null);
    }
}
