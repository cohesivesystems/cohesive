using System.Collections.Immutable;
using Cohesive.Api;
using Cohesive.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Cohesive.Adapters.AspNet.Identity;

/// <summary>
/// Header names and scope kind used to resolve request identity context.
/// </summary>
public sealed class RequestIdentityContextOptions
{
    /// <summary>
    /// Scope kind requested by this adapter instance.
    /// </summary>
    public string ScopeKind { get; set; } = "cohesive.scope";

    /// <summary>
    /// Header carrying one selected scope id.
    /// </summary>
    public string SingleScopeHeaderName { get; set; } = "X-Cohesive-Scope-Id";

    /// <summary>
    /// Header carrying comma-separated selected scope ids.
    /// </summary>
    public string MultipleScopesHeaderName { get; set; } = "X-Cohesive-Scope-Ids";

    /// <summary>
    /// Header carrying the requested scope selection mode.
    /// </summary>
    public string ScopeModeHeaderName { get; set; } = "X-Cohesive-Scope-Mode";
}

/// <summary>
/// Registers request identity context enrichment for ASP.NET operation contexts.
/// </summary>
public static class RequestIdentityContextServiceCollectionExtensions
{
    /// <summary>
    /// Adds request identity context enrichment.
    /// </summary>
    public static IServiceCollection AddRequestIdentityContext(
        this IServiceCollection services,
        Action<RequestIdentityContextOptions>? configure = null
        )
        {
            ArgumentNullException.ThrowIfNull(services);
            if (configure is not null)
                services.Configure(configure);

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IApiStructuredResourceIdParser, ScopedProcessInstanceIdStructuredResourceIdParser>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IApiScopeResourceSelectionResolver, StructuredResourceIdApiScopeResourceSelectionResolver>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IHttpOperationContextEnricher, RequestIdentityContextEnricher>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IHttpOperationContextEnricher, ApiScopePolicyEnforcementEnricher>());
            return services;
        }
    }

sealed class RequestIdentityContextEnricher(
    IOptions<RequestIdentityContextOptions> options,
    IIdentityContextResolver resolver,
    IEnumerable<IApiScopeResourceSelectionResolver> resourceSelectionResolvers
    ) : IHttpOperationContextEnricher
{
    readonly RequestIdentityContextOptions options = options.Value;
    readonly ImmutableArray<IApiScopeResourceSelectionResolver> resourceSelectionResolverList = [..resourceSelectionResolvers];

    public async ValueTask<OperationContext> EnrichAsync(
        HttpContext httpContext,
        OperationContext context
        )
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(context);
        var identity = await resolver.ResolveAsync(
            request: new(Principal: context.Principal, RequestedScope: ReadRequestedScope(httpContext)), 
            context.CancellationToken
            ).ConfigureAwait(false);
        return context.WithIdentityContext(identity);
    }

    RequestedScopeSelection? ReadRequestedScope(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var endpointPolicies = httpContext.GetEndpoint()?.Metadata.GetOrderedMetadata<ApiScopePolicy>();
        if (endpointPolicies is { Count: > 0 })
            return ReadRequestedScope(httpContext, endpointPolicies);

        return ReadRequestedScope(httpContext.Request.Headers);
    }

    RequestedScopeSelection? ReadRequestedScope(HttpContext httpContext, IReadOnlyList<ApiScopePolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(policies);
        if (policies.Count == 0)
            return null;

        var scopeKind = ResolveScopeKind(policies);
        for (var i = 0; i < policies.Count; i++)
        {
            if (TryReadRequestedScopeSelection(httpContext, policies[i], scopeKind, out var selection))
                return selection;
        }

        for (var i = 0; i < policies.Count; i++)
        {
            var policy = policies[i];
            if (policy.Binding == ApiScopeBinding.Resource)
            {
                return new(
                    ScopeKind: scopeKind,
                    ScopeIds: [],
                    Mode: ScopeSelectionMode.AllAccessible,
                    Source: ScopeSelectionSource.Default
                    );
            }

            if (policy.Cardinality == ApiScopeCardinality.Multiple)
            {
                if (!policy.AllowDefaultScope)
                    throw CreateExplicitScopeSelectionRequiredError(scopeKind, policy);

                return new(
                    ScopeKind: scopeKind,
                    ScopeIds: [],
                    Mode: ScopeSelectionMode.AllAccessible,
                    Source: ScopeSelectionSource.Default
                    );
            }
        }

        for (var i = 0; i < policies.Count; i++)
        {
            if (!policies[i].AllowDefaultScope)
                throw CreateExplicitScopeSelectionRequiredError(scopeKind, policies[i]);
        }

        return null;
    }

    RequestedScopeSelection? ReadRequestedScope(IHeaderDictionary headers)
    {
        var mode = ReadMode(headers);
        var scopeIds = ReadScopeIds(headers);

        if (mode == ScopeSelectionMode.Default && scopeIds.Length == 0)
            return null;

        if (mode == ScopeSelectionMode.Default)
            mode = scopeIds.Length > 1 ? ScopeSelectionMode.Multiple : ScopeSelectionMode.Single;

        return new(
            ScopeKind: options.ScopeKind,
            ScopeIds: scopeIds,
            Mode: mode,
            Source: ScopeSelectionSource.RequestHeader
            );
    }

    static string ResolveScopeKind(IReadOnlyList<ApiScopePolicy> policies)
    {
        var scopeKind = policies[0].ScopeKind;
        for (var i = 1; i < policies.Count; i++)
        {
            if (!string.Equals(scopeKind, policies[i].ScopeKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Request identity context supports one declared scope kind per endpoint.");
            }
        }
        return scopeKind;
    }

    bool TryReadRequestedScopeSelection(
        HttpContext httpContext,
        ApiScopePolicy policy,
        string scopeKind,
        out RequestedScopeSelection? selection
        )
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(policy);
        var request = httpContext.Request;

        switch (policy.Binding)
        {
            case ApiScopeBinding.Header:
                return TryReadRequestedScopeSelection(
                    scopeKind,
                    policy,
                    policy.SingleScopeParameterName,
                    policy.MultipleScopesParameterName,
                    policy.ScopeModeParameterName,
                    request.Headers,
                    ScopeSelectionSource.RequestHeader,
                    out selection
                    );

            case ApiScopeBinding.Query:
                return TryReadRequestedScopeSelection(
                    scopeKind,
                    policy,
                    policy.SingleScopeParameterName,
                    policy.MultipleScopesParameterName,
                    policy.ScopeModeParameterName,
                    request.Query,
                    ScopeSelectionSource.RequestQuery,
                    out selection
                    );

            case ApiScopeBinding.Route:
                if (TryReadRouteRequestedScopeSelection(request, policy, scopeKind, out selection))
                    return true;

                selection = null;
                return false;

            case ApiScopeBinding.Resource:
                if (TryReadResourceRequestedScopeSelection(httpContext, policy, scopeKind, out selection))
                    return true;

                selection = null;
                return false;

            case ApiScopeBinding.Ambient:
            case ApiScopeBinding.Body:
            default:
                selection = null;
                return false;
        }
    }

    static bool TryReadRequestedScopeSelection(
        string scopeKind,
        ApiScopePolicy policy,
        string? singleScopeParameterName,
        string? multipleScopesParameterName,
        string? scopeModeParameterName,
        IHeaderDictionary values,
        ScopeSelectionSource source,
        out RequestedScopeSelection? selection
        )
    {
        var mode = ReadMode(values, scopeModeParameterName);
        var scopeIds = ReadScopeIds(
            singleValues: !string.IsNullOrWhiteSpace(singleScopeParameterName) && values.TryGetValue(singleScopeParameterName, out var singleHeaderValues)
                ? singleHeaderValues
                : StringValues.Empty,
            multipleValues: !string.IsNullOrWhiteSpace(multipleScopesParameterName) && values.TryGetValue(multipleScopesParameterName, out var multipleHeaderValues)
                ? multipleHeaderValues
                : StringValues.Empty
            );
        return TryCreateRequestedScopeSelection(scopeKind, policy, scopeIds, mode, source, out selection);
    }

    static bool TryReadRequestedScopeSelection(
        string scopeKind,
        ApiScopePolicy policy,
        string? singleScopeParameterName,
        string? multipleScopesParameterName,
        string? scopeModeParameterName,
        IQueryCollection values,
        ScopeSelectionSource source,
        out RequestedScopeSelection? selection
        )
    {
        var mode = ReadMode(values, scopeModeParameterName);
        var scopeIds = ReadScopeIds(
            singleValues: !string.IsNullOrWhiteSpace(singleScopeParameterName) && values.TryGetValue(singleScopeParameterName, out var singleQueryValues)
                ? singleQueryValues
                : StringValues.Empty,
            multipleValues: !string.IsNullOrWhiteSpace(multipleScopesParameterName) && values.TryGetValue(multipleScopesParameterName, out var multipleQueryValues)
                ? multipleQueryValues
                : StringValues.Empty
            );

        return TryCreateRequestedScopeSelection(scopeKind, policy, scopeIds, mode, source, out selection);
    }

    static bool TryReadRouteRequestedScopeSelection(
        HttpRequest request,
        ApiScopePolicy policy,
        string scopeKind,
        out RequestedScopeSelection? selection
        )
    {
        if (string.IsNullOrWhiteSpace(policy.SingleScopeParameterName)
            || !request.RouteValues.TryGetValue(policy.SingleScopeParameterName, out var routeValue)
            || routeValue is null)
        {
            selection = null;
            return false;
        }

        var trimmed = routeValue.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            selection = null;
            return false;
        }

        selection = new(
            ScopeKind: scopeKind,
            ScopeIds: [trimmed],
            Mode: ScopeSelectionMode.Single,
            Source: ScopeSelectionSource.RequestRoute
            );
        return true;
    }

    bool TryReadResourceRequestedScopeSelection(
        HttpContext httpContext,
        ApiScopePolicy policy,
        string scopeKind,
        out RequestedScopeSelection? selection
        )
    {
        if (policy.ResourceDerivation is null)
        {
            selection = null;
            return false;
        }

        for (var i = 0; i < resourceSelectionResolverList.Length; i++)
        {
            if (!resourceSelectionResolverList[i].CanResolve(policy.ResourceDerivation))
            {
                continue;
            }

            if (resourceSelectionResolverList[i].TryResolveRequestedScope(httpContext, policy, scopeKind, out selection))
                return true;
        }

        selection = null;
        return false;
    }

    static bool TryCreateRequestedScopeSelection(
        string scopeKind,
        ApiScopePolicy policy,
        ImmutableArray<string> scopeIds,
        ScopeSelectionMode mode,
        ScopeSelectionSource source,
        out RequestedScopeSelection? selection
        )
    {
        if (scopeIds.Length == 0)
        {
            selection = null;
            return false;
        }

        if (mode == ScopeSelectionMode.Default)
        {
            mode = scopeIds.Length > 1 || policy.Cardinality == ApiScopeCardinality.Multiple
                ? ScopeSelectionMode.Multiple
                : ScopeSelectionMode.Single;
        }

        selection = new(
            ScopeKind: scopeKind,
            ScopeIds: scopeIds,
            Mode: mode,
            Source: source
            );
        return true;
    }

    static BadHttpRequestException CreateExplicitScopeSelectionRequiredError(string scopeKind, ApiScopePolicy policy) =>
        new(policy.Cardinality == ApiScopeCardinality.Multiple
            ? $"The operation requires one or more explicitly selected '{scopeKind}' scopes."
            : $"The operation requires an explicitly selected '{scopeKind}' scope.");

    ImmutableArray<string> ReadScopeIds(IHeaderDictionary headers)
    {
        var singleValues = headers.TryGetValue(options.SingleScopeHeaderName, out var configuredSingleValues)
            ? configuredSingleValues
            : StringValues.Empty;
        var multipleValues = headers.TryGetValue(options.MultipleScopesHeaderName, out var configuredMultipleValues)
            ? configuredMultipleValues
            : StringValues.Empty;
        return ReadScopeIds(singleValues, multipleValues);
    }

    ScopeSelectionMode ReadMode(IHeaderDictionary headers) => ReadMode(headers, options.ScopeModeHeaderName);

    static ImmutableArray<string> ReadScopeIds(StringValues singleValues, StringValues multipleValues)
    {
        if (multipleValues.Count > 0)
        {
            return [
                ..multipleValues
                    .SelectMany(static value => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
            ];
        }

        if (singleValues.Count > 0)
        {
            return [
                ..singleValues
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(static value => value!.Trim())
                    .Take(1)
            ];
        }

        return [];
    }

    static ScopeSelectionMode ReadMode(IHeaderDictionary headers, string? parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName)
            || !headers.TryGetValue(parameterName, out var values))
        {
            return ScopeSelectionMode.Default;
        }

        return ParseMode(values);
    }

    static ScopeSelectionMode ReadMode(IQueryCollection query, string? parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName)
            || !query.TryGetValue(parameterName, out var values))
        {
            return ScopeSelectionMode.Default;
        }

        return ParseMode(values);
    }

    static ScopeSelectionMode ParseMode(StringValues values)
    {
        if (values.Count == 0)
            return ScopeSelectionMode.Default;

        var value = values[^1];
        if (string.IsNullOrWhiteSpace(value))
            return ScopeSelectionMode.Default;

        return value.Trim().ToLowerInvariant() switch
        {
            "single" => ScopeSelectionMode.Single,
            "multiple" => ScopeSelectionMode.Multiple,
            "all" or "all-accessible" => ScopeSelectionMode.AllAccessible,
            "system" => ScopeSelectionMode.System,
            _ => ScopeSelectionMode.Default
        };
    }
}
