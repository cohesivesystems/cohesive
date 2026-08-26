using System.Collections.Immutable;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Storage;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

static class EntityApiRequestSupport
{
    public static IEntityRepository ResolveRepository(HttpContext httpContext, EntityApiEndpointOptions options) =>
        options.RepositoryResolver(httpContext.RequestServices, options.Entity);

    public static string GetRequiredEntityId(HttpContext httpContext, EntityApiEndpointOptions options)
    {
        if (httpContext.Request.RouteValues.TryGetValue(options.EntityIdRouteParameter, out var value)
            && value is not null
            && !string.IsNullOrWhiteSpace(value.ToString()))
        {
            return value.ToString()!;
        }

        throw new BadHttpRequestException($"Route value '{options.EntityIdRouteParameter}' is required.");
    }

    public static ValueTask<object?> ReadBodyRequestAsync(
        HttpContext httpContext,
        ApiOperation operation,
        CancellationToken ct) =>
        HttpRequestBindingSupport.ReadOperationBodyAsync(httpContext, operation, ct);

    public static async Task<EntitySnapshot> CommitAsync(
        EntityApiCommitContext context,
        EntityApiEndpointOptions options,
        EntityConcurrencyToken? expectedConcurrencyToken,
        ImmutableArray<InteractionEnvelope> envelopes = default
        )
    {
        var write = new EntityWriteRequest(context.NewState.Snapshot, expectedConcurrencyToken);
        if (!envelopes.IsDefaultOrEmpty)
        {
            var outboxRepository = options.OutboxRepositoryResolver(context.HttpContext.RequestServices, options.Entity);
            var commit = await outboxRepository.UpsertWithOutbox(
                    context.OperationContext,
                    new EntityOutboxCommit(write, envelopes))
                .ConfigureAwait(false);
            return commit.Entity;
        }

        return await context.Repository.Upsert(context.OperationContext, write).ConfigureAwait(false);
    }

}
