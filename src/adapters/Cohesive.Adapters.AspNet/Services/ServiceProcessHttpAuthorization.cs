using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.AspNet.Services;

// Host-local policy names avoid collisions between services that use the same requirement IDs.
// The canonical operation remains authoritative; no permissions are inferred from transport input.
internal static class ServiceProcessHttpAuthorization
{
    internal static AspNetAuthorizationPolicyResolver Register<TRequest, TResult>(IServiceCollection services,
        ServiceAcceptedProcessApi<TRequest, TResult> pair, IServiceInvocationAuthorization authorization) where TRequest : class
    {
        var operations = pair.Declaration.GetDefinition<ServiceDefinition>().Operations;
        var start = operations.Single(operation => operation.Id == pair.StartOperationId);
        var result = operations.Single(operation => operation.Id == pair.ResultOperationId);
        var prefix = "cohesive.process/" + Guid.NewGuid().ToString("N");
        var startPolicy = prefix + "/start";
        var resultPolicy = prefix + "/result";
        services.AddAuthorization(options =>
        {
            options.AddPolicy(startPolicy, policy => policy.RequireAssertion(context => Admit(context.Resource, start)));
            options.AddPolicy(resultPolicy, policy => policy.RequireAssertion(context => Admit(context.Resource, result)));
        });
        return (operation, _) => operation.Id == pair.Start.Id ? startPolicy
            : operation.Id == pair.Result.Id ? resultPolicy : null;

        async Task<bool> Admit(object? resource, ServiceOperation operation) => resource is HttpContext http
            && await authorization.AdmitAsync(http.RequestServices.GetRequiredService<OperationContext>(), operation).ConfigureAwait(false) is not null;
    }
}
