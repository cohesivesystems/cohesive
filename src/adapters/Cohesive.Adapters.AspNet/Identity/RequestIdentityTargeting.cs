using Cohesive.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.AspNet.Identity;

/// <summary>Prepares feature targeting from the host's resolved request identity.</summary>
public static class RequestIdentityTargeting
{
    /// <summary>Validates identity registration and captures only the concurrent operation accessor.</summary>
    /// <typeparam name="TInput">Query input, ignored for identity targeting.</typeparam>
    /// <param name="services">Host services with request operation context and fixed or resolved identity registration.</param>
    /// <returns>A projection reading the current request identity on each invocation.</returns>
    /// <exception cref="InvalidOperationException">The host has no supported identity registration or operation accessor.</exception>
    /// <exception cref="BadHttpRequestException">An invocation has no resolved identity; HTTP status is 401.</exception>
    /// <remarks>Startup checks registration, not future request identities or middleware ordering.
    /// Requests must pass through UseRequestOperationContext before dispatch. Missing identity fails
    /// before flag evaluation or reader dispatch; no anonymous or cross-tenant default is invented.</remarks>
    public static Func<TInput, IdentityContext> For<TInput>(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        using (var scope = services.CreateScope())
            if (!scope.ServiceProvider.GetServices<IHttpOperationContextEnricher>()
                .Any(OperationContextApplicationBuilderExtensions.SuppliesIdentity))
                throw new InvalidOperationException("Identity targeting requires AddFixedRequestIdentity or AddRequestIdentityContext.");
        var accessor = services.GetRequiredService<IOperationContextAccessor>();
        return _ => accessor.Current?.GetIdentityContextOrDefault()
            ?? throw new BadHttpRequestException("The operation requires a resolved identity context.", StatusCodes.Status401Unauthorized);
    }
}
