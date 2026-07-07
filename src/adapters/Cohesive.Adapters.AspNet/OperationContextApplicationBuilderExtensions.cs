using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cohesive.Adapters.AspNet;

/// <summary>
/// Registers and initializes <see cref="OperationContext"/> instances for ASP.NET Core requests.
/// </summary>
/// <remarks>
/// Call <see cref="AddRequestOperationContext(IServiceCollection)"/> during service registration and
/// <see cref="UseRequestOperationContext(IApplicationBuilder)"/> before endpoints or middleware that
/// resolve <see cref="OperationContext"/> from dependency injection.
/// </remarks>
public static class OperationContextApplicationBuilderExtensions
{
    static readonly object RequestOperationContextKey = new();

    /// <summary>
    /// Registers request-scoped <see cref="OperationContext"/> access for ASP.NET Core.
    /// </summary>
    /// <param name="services">Service collection to update.</param>
    /// <returns>The same service collection for fluent registration.</returns>
    /// <remarks>
    /// The scoped <see cref="OperationContext"/> is read from the current <see cref="HttpContext"/>.
    /// Requests must pass through <see cref="UseRequestOperationContext(IApplicationBuilder)"/> before
    /// components attempt to resolve the context.
    /// </remarks>
    public static IServiceCollection AddRequestOperationContext(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddCohesiveOperationContext();
        services.AddHttpContextAccessor();
        services.TryAddScoped(static sp =>
        {
            var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext ?? throw new InvalidOperationException("OperationContext is only available during an active HTTP request.");
            if (httpContext.Items.TryGetValue(RequestOperationContextKey, out var existing) && existing is OperationContext operationContext)
                return operationContext;

            throw new InvalidOperationException($"{nameof(OperationContext)} has not been initialized for this request. Ensure {nameof(UseRequestOperationContext)} is registered before endpoints that resolve {nameof(OperationContext)}.");
        });
        return services;
    }

    /// <summary>
    /// Adds middleware that creates, enriches, stores, and pushes the request <see cref="OperationContext"/>.
    /// </summary>
    /// <param name="app">Application builder to update.</param>
    /// <returns>The same application builder for fluent middleware registration.</returns>
    /// <remarks>
    /// The middleware seeds the context from the current ASP.NET principal, activity trace context, and
    /// request-aborted cancellation token, then applies registered <see cref="IHttpOperationContextEnricher"/>
    /// services before invoking the next middleware.
    /// </remarks>
    public static IApplicationBuilder UseRequestOperationContext(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<RequestOperationContextMiddleware>();
    }

    static async ValueTask<OperationContext> CreateOperationContextAsync(IServiceProvider services, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(httpContext);
        var context = services.GetRequiredService<IOperationContextFactory>().Create(
            principal: httpContext.User,
            traceContext: Activity.Current?.Context,
            cancellationToken: httpContext.RequestAborted
            );

        foreach (var enricher in services.GetServices<IHttpOperationContextEnricher>())
            context = await enricher.EnrichAsync(httpContext, context).ConfigureAwait(false);

        return context;
    }

    sealed class RequestOperationContextMiddleware(RequestDelegate next)
    {
        public async Task InvokeAsync(HttpContext httpContext, IOperationContextScopeFactory scopeFactory)
        {
            var operationContext = httpContext.Items.TryGetValue(RequestOperationContextKey, out var existing)
                                   && existing is OperationContext existingOperationContext
                ? existingOperationContext
                : await CreateOperationContextAsync(httpContext.RequestServices, httpContext).ConfigureAwait(false);
            httpContext.Items[RequestOperationContextKey] = operationContext;
            using var scope = scopeFactory.Push(operationContext);
            await next(httpContext).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Enriches an ASP.NET request operation context before it is exposed through dependency injection.
/// </summary>
public interface IHttpOperationContextEnricher
{
    /// <summary>
    /// Returns an enriched operation context for the current HTTP request.
    /// </summary>
    /// <param name="httpContext">Current ASP.NET HTTP context.</param>
    /// <param name="context">Operation context built so far.</param>
    /// <returns>The operation context to expose for the request.</returns>
    ValueTask<OperationContext> EnrichAsync(
        HttpContext httpContext,
        OperationContext context
        );
}
