using System.Text.Json;
using Cohesive.Api;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Processes;

static class ProcessApiRequestSupport
{
    static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask<HttpRequestBindingSupport.ReadResult<TRequest>> ReadRequestAsync<TRequest>(HttpRequestBindingSupport.BodyReader reader, HttpContext httpContext, CancellationToken ct)
    {
        var (request, failure) = await reader.ReadRequestAsync(httpContext, ct).ConfigureAwait(false);
        if (failure is not null) return new(default!, failure);
        if (request is null && reader.HasQuery)
            return new(JsonSerializer.Deserialize<TRequest>("{}", DefaultJsonOptions)!);

        if (request is null)
            return new(default!);

        if (request is TRequest typed)
            return new(typed);

        throw new BadHttpRequestException(
            $"Request for operation '{reader.Name}' could not be bound as '{typeof(TRequest).Name}'.");
    }
}
