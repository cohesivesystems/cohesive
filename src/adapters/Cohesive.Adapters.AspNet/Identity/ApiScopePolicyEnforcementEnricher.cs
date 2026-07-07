using Cohesive.Api;
using Cohesive.Identity;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Identity;

sealed class ApiScopePolicyEnforcementEnricher : IHttpOperationContextEnricher
{
    public ValueTask<OperationContext> EnrichAsync(
        HttpContext httpContext,
        OperationContext context
        )
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(context);
        var policies = httpContext.GetEndpoint()?.Metadata.GetOrderedMetadata<ApiScopePolicy>();
        if (policies is null || policies.Count == 0)
            return ValueTask.FromResult(context);

        var identity = context.GetIdentityContextOrDefault()
            ?? throw new BadHttpRequestException("The operation requires a resolved identity context.");
        for (var i = 0; i < policies.Count; i++)
            EnforcePolicy(identity, policies[i]);

        return ValueTask.FromResult(context);
    }

    static void EnforcePolicy(IdentityContext identity, ApiScopePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.Binding == ApiScopeBinding.Resource)
        {
            if (policy.ResourceDerivation is not null)
                RequireSingleEffectiveScope(identity, policy, $"The operation requires a resource-derived '{policy.ScopeKind}' scope.");

            return;
        }

        if (policy.Cardinality == ApiScopeCardinality.Single)
        {
            RequireSingleEffectiveScope(identity, policy, $"The operation requires exactly one effective '{policy.ScopeKind}' scope.");
            return;
        }

        if (policy.Access == ApiScopeAccess.FilterToAccessible)
            return;

        if (identity.GetEffectiveScopes(policy.ScopeKind).Length == 0)
            throw new BadHttpRequestException($"The operation requires an effective '{policy.ScopeKind}' scope.");
    }

    static void RequireSingleEffectiveScope(
        IdentityContext identity,
        ApiScopePolicy policy,
        string message
        )
    {
        var effectiveScope = identity.EffectiveScope;
        if (effectiveScope is null
            || effectiveScope.Mode != ScopeSelectionMode.Single
            || identity.GetEffectiveScopes(policy.ScopeKind).Length != 1)
        {
            throw new BadHttpRequestException(message);
        }
    }
}
