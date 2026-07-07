using System.Text.Json;
using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Processes;

static class ProcessApiRequestSupport
{
    static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask<TRequest> ReadRequestAsync<TRequest>(HttpContext httpContext, ApiOperation operation, CancellationToken ct)
    {
        var request = operation.Http.Body is not null
            ? await EntityApiRequestSupport
                .ReadBodyRequestAsync(httpContext, operation, ct)
                .ConfigureAwait(false)
            : EntityApiRequestSupport.ReadQueryRequest(httpContext, operation);

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
