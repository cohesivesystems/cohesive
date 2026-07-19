using System.Text.Json;
using Cohesive.Api;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Processes;

static class ProcessApiRequestSupport
{
    static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask<TRequest> ReadRequestAsync<TRequest>(HttpContext httpContext, ApiOperation operation, CancellationToken ct)
    {
        var request = await HttpRequestBindingSupport
            .ReadOperationRequestAsync(httpContext, operation, ct)
            .ConfigureAwait(false);

        if (request is null && operation.Http.Query is not null)
            return JsonSerializer.Deserialize<TRequest>("{}", DefaultJsonOptions) ?? default!;

        if (request is null)
            return default!;

        if (request is TRequest typed)
            return typed;

        throw new BadHttpRequestException(
            $"Request for operation '{operation.Name}' could not be bound as '{typeof(TRequest).Name}'.");
    }
}
