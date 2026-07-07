using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Identity;
using Cohesive.Api;
using Cohesive.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Identity;

public sealed class RequestIdentityContextTests
{
    [Fact]
    public async Task EnrichAsync_QueryScopePolicyWithoutExplicitIds_SelectsAllAccessibleScopes()
    {
        var resolver = new CapturingIdentityContextResolver();
        await EnrichAsync(
            resolver,
            new ApiScopePolicy(
                scopeKind: "sample.tenant",
                cardinality: ApiScopeCardinality.Multiple,
                binding: ApiScopeBinding.Query,
                access: ApiScopeAccess.FilterToAccessible,
                multipleScopesParameterName: "tenant_ids"
                ),
            configureRequest: static _ => { }
            );

        var requestedScope = Assert.IsType<RequestedScopeSelection>(resolver.LastRequest?.RequestedScope);
        Assert.Equal("sample.tenant", requestedScope.ScopeKind);
        Assert.Equal(ScopeSelectionMode.AllAccessible, requestedScope.Mode);
        Assert.Empty(requestedScope.ScopeIds);
    }

    [Fact]
    public async Task EnrichAsync_QueryScopePolicy_ReadsExplicitScopeIdsFromQuery()
    {
        var resolver = new CapturingIdentityContextResolver();
        await EnrichAsync(
            resolver,
            new ApiScopePolicy(
                scopeKind: "sample.tenant",
                cardinality: ApiScopeCardinality.Multiple,
                binding: ApiScopeBinding.Query,
                access: ApiScopeAccess.FilterToAccessible,
                multipleScopesParameterName: "tenant_ids"
                ),
            configureRequest: static httpContext =>
            {
                httpContext.Request.QueryString = new QueryString("?tenant_ids=ui-test&tenant_ids=sample-internal");
            }
            );

        var requestedScope = Assert.IsType<RequestedScopeSelection>(resolver.LastRequest?.RequestedScope);
        Assert.Equal(ScopeSelectionMode.Multiple, requestedScope.Mode);
        Assert.Equal(ScopeSelectionSource.RequestQuery, requestedScope.Source);
        Assert.Equal(["ui-test", "sample-internal"], requestedScope.ScopeIds.ToArray());
    }

    [Fact]
    public async Task EnrichAsync_QueryFilterScopePolicy_AllowsNoAccessibleScopes()
    {
        var resolver = new StaticIdentityContextResolver(new IdentityContext(
            Actor: new PrincipalRef("test-user", PrincipalKind.User),
            Grants: ImmutableArray<IdentityScopeGrant>.Empty
            ));

        var context = await EnrichAsync(
            resolver,
            new ApiScopePolicy(
                scopeKind: "sample.tenant",
                cardinality: ApiScopeCardinality.Multiple,
                binding: ApiScopeBinding.Query,
                access: ApiScopeAccess.FilterToAccessible,
                multipleScopesParameterName: "tenant_ids"
                ),
            configureRequest: static _ => { }
            );

        Assert.Empty(context.GetEffectiveScopes("sample.tenant"));
    }

    [Fact]
    public async Task EnrichAsync_RouteScopePolicy_ReadsExplicitScopeIdFromRoute()
    {
        var resolver = new CapturingIdentityContextResolver();
        await EnrichAsync(
            resolver,
            new ApiScopePolicy(
                scopeKind: "sample.tenant",
                cardinality: ApiScopeCardinality.Single,
                binding: ApiScopeBinding.Route,
                singleScopeParameterName: "tenantId",
                allowDefaultScope: false
                ),
            configureRequest: static httpContext =>
            {
                httpContext.Request.RouteValues["tenantId"] = "sample-internal";
            }
            );

        var requestedScope = Assert.IsType<RequestedScopeSelection>(resolver.LastRequest?.RequestedScope);
        Assert.Equal(ScopeSelectionMode.Single, requestedScope.Mode);
        Assert.Equal(ScopeSelectionSource.RequestRoute, requestedScope.Source);
        Assert.Equal(["sample-internal"], requestedScope.ScopeIds.ToArray());
    }

    [Fact]
    public async Task EnrichAsync_RequiredSingleHeaderScope_ThrowsWhenHeaderMissing()
    {
        var resolver = new CapturingIdentityContextResolver();

        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() => EnrichAsync(
            resolver,
            new ApiScopePolicy(
                scopeKind: "sample.tenant",
                cardinality: ApiScopeCardinality.Single,
                binding: ApiScopeBinding.Header,
                singleScopeParameterName: "X-Sample-Tenant-Id",
                allowDefaultScope: false
                ),
            configureRequest: static _ => { }
            ));

        Assert.Equal("The operation requires an explicitly selected 'sample.tenant' scope.", error.Message);
    }

    [Fact]
    public async Task EnrichAsync_ResourceScopePolicy_UsesAllAccessibleScopeMode()
    {
        var resolver = new CapturingIdentityContextResolver();
        await EnrichAsync(
            resolver,
            new ApiScopePolicy(
                scopeKind: "sample.tenant",
                cardinality: ApiScopeCardinality.Single,
                binding: ApiScopeBinding.Resource,
                access: ApiScopeAccess.ValidateAccessible,
                resourceParameterName: "processId",
                allowDefaultScope: false
                ),
            configureRequest: static httpContext =>
            {
                httpContext.Request.RouteValues["processId"] = "training-process-1";
            }
            );

        var requestedScope = Assert.IsType<RequestedScopeSelection>(resolver.LastRequest?.RequestedScope);
        Assert.Equal(ScopeSelectionMode.AllAccessible, requestedScope.Mode);
        Assert.Empty(requestedScope.ScopeIds);
    }

    [Fact]
    public async Task EnrichAsync_ResourceProcessScopePolicy_DerivesSingleScopeFromProcessIdRoute()
    {
        var resolver = new CapturingIdentityContextResolver();
        await EnrichAsync(
            resolver,
            new ApiScopePolicy(
                scopeKind: "sample.tenant",
                cardinality: ApiScopeCardinality.Single,
                binding: ApiScopeBinding.Resource,
                access: ApiScopeAccess.ValidateAccessible,
                resourceParameterName: "processId",
                resourceDerivation: ScopedProcessInstanceIdScopeDerivation(),
                allowDefaultScope: false
                ),
            configureRequest: static httpContext =>
            {
                httpContext.Request.RouteValues["processId"] = "training-job--sample-internal--001";
            }
            );

        var requestedScope = Assert.IsType<RequestedScopeSelection>(resolver.LastRequest?.RequestedScope);
        Assert.Equal(ScopeSelectionMode.Single, requestedScope.Mode);
        Assert.Equal(ScopeSelectionSource.RequestRoute, requestedScope.Source);
        Assert.Equal(["sample-internal"], requestedScope.ScopeIds.ToArray());
    }

    [Fact]
    public async Task EnrichAsync_DerivedResourceScopePolicy_ThrowsWhenResourceIdCannotDeriveScope()
    {
        var resolver = new CapturingIdentityContextResolver();

        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() => EnrichAsync(
            resolver,
            new ApiScopePolicy(
                scopeKind: "sample.tenant",
                cardinality: ApiScopeCardinality.Single,
                binding: ApiScopeBinding.Resource,
                access: ApiScopeAccess.ValidateAccessible,
                resourceParameterName: "processId",
                resourceDerivation: ScopedProcessInstanceIdScopeDerivation(),
                allowDefaultScope: false
                ),
            configureRequest: static httpContext =>
            {
                httpContext.Request.RouteValues["processId"] = "training-process-1";
            }
            ));

        Assert.Equal("The operation requires a resource-derived 'sample.tenant' scope.", error.Message);
    }

    [Fact]
    public async Task EnrichAsync_SingleScopePolicy_ThrowsWhenEffectiveScopeIsNotSingle()
    {
        var resolver = new StaticIdentityContextResolver(new IdentityContext(
            Actor: new PrincipalRef("test-user", PrincipalKind.User),
            EffectiveScope: new(
                Scopes: [new("sample-internal", "sample.tenant")],
                Mode: ScopeSelectionMode.AllAccessible,
                Source: ScopeSelectionSource.RequestHeader
                ),
            Grants: ImmutableArray<IdentityScopeGrant>.Empty
            ));

        var error = await Assert.ThrowsAsync<BadHttpRequestException>(() => EnrichAsync(
            resolver,
            new ApiScopePolicy(
                scopeKind: "sample.tenant",
                cardinality: ApiScopeCardinality.Single,
                binding: ApiScopeBinding.Header,
                singleScopeParameterName: "X-Sample-Tenant-Id",
                allowDefaultScope: false
                ),
            configureRequest: static httpContext =>
            {
                httpContext.Request.Headers["X-Sample-Tenant-Id"] = "sample-internal";
            }
            ));

        Assert.Equal("The operation requires exactly one effective 'sample.tenant' scope.", error.Message);
    }

    [Fact]
    public async Task EnrichAsync_ResourceScopePolicy_UsesRegisteredStructuredResourceIdParser()
    {
        var resolver = new CapturingIdentityContextResolver();
        await EnrichAsync(
            resolver,
            new ApiScopePolicy(
                scopeKind: "sample.tenant",
                cardinality: ApiScopeCardinality.Single,
                binding: ApiScopeBinding.Resource,
                access: ApiScopeAccess.ValidateAccessible,
                resourceParameterName: "workspaceId",
                resourceDerivation: WorkspaceResourceIdParser.Derivation,
                allowDefaultScope: false
                ),
            configureRequest: static httpContext =>
            {
                httpContext.Request.RouteValues["workspaceId"] = "workspace/sample-internal";
            },
            configureServices: static services =>
            {
                services.AddSingleton<IApiStructuredResourceIdParser>(new WorkspaceResourceIdParser());
            }
            );

        var requestedScope = Assert.IsType<RequestedScopeSelection>(resolver.LastRequest?.RequestedScope);
        Assert.Equal(ScopeSelectionMode.Single, requestedScope.Mode);
        Assert.Equal(ScopeSelectionSource.RequestRoute, requestedScope.Source);
        Assert.Equal(["sample-internal"], requestedScope.ScopeIds.ToArray());
    }

    static async Task<OperationContext> EnrichAsync(
        IIdentityContextResolver resolver,
        ApiScopePolicy policy,
        Action<DefaultHttpContext> configureRequest,
        Action<ServiceCollection>? configureServices = null
        )
    {
        var services = new ServiceCollection();
        services.AddRequestIdentityContext(options =>
        {
            options.ScopeKind = "fallback.scope";
            options.SingleScopeHeaderName = "X-Fallback-Scope-Id";
            options.MultipleScopesHeaderName = "X-Fallback-Scope-Ids";
            options.ScopeModeHeaderName = "X-Fallback-Scope-Mode";
        });
        services.AddSingleton<IIdentityContextResolver>(resolver);
        configureServices?.Invoke(services);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "test-user")],
                authenticationType: "Bearer"))
        };
        configureRequest(httpContext);
        httpContext.SetEndpoint(new Endpoint(
            requestDelegate: static _ => Task.CompletedTask,
            metadata: new EndpointMetadataCollection(policy),
            displayName: "test"
            ));

        var context = OperationContext.Create(principal: httpContext.User);
        foreach (var enricher in scope.ServiceProvider.GetServices<IHttpOperationContextEnricher>())
            context = await enricher.EnrichAsync(httpContext, context);

        return context;
    }

    sealed class CapturingIdentityContextResolver : IIdentityContextResolver
    {
        public IdentityContextResolutionRequest? LastRequest { get; private set; }

        public ValueTask<IdentityContext> ResolveAsync(IdentityContextResolutionRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            var effectiveScope = request.RequestedScope is null
                ? null
                : new EffectiveScope(
                    Scopes: [.. request.RequestedScope.ScopeIds.Select(scopeId => new ScopeRef(scopeId, request.RequestedScope.ScopeKind))],
                    Mode: request.RequestedScope.Mode,
                    Source: request.RequestedScope.Source
                    );

            return ValueTask.FromResult(new IdentityContext(
                Actor: new PrincipalRef("test-user", PrincipalKind.User),
                EffectiveScope: effectiveScope,
                Grants: ImmutableArray<IdentityScopeGrant>.Empty
                ));
        }
    }

    sealed class StaticIdentityContextResolver(IdentityContext identity) : IIdentityContextResolver
    {
        public ValueTask<IdentityContext> ResolveAsync(IdentityContextResolutionRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(identity);
    }

    sealed class WorkspaceResourceIdParser : IApiStructuredResourceIdParser
    {
        public static readonly ApiResourceScopeDerivation Derivation = new(
            strategy: ApiResourceScopeDerivationStrategies.StructuredResourceId,
            format: "workspaceResourceId",
            scopeField: "tenantId");

        public string Format => Derivation.Format!;

        public bool TryParse(
            string resourceId,
            [NotNullWhen(returnValue: true)] out ApiStructuredResourceId? parsed
            )
        {
            parsed = null;
            if (string.IsNullOrWhiteSpace(resourceId)
                || !resourceId.StartsWith("workspace/", StringComparison.Ordinal))
            {
                return false;
            }

            parsed = new(
                Format,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["tenantId"] = resourceId["workspace/".Length..]
                });
            return true;
        }
    }

    static ApiResourceScopeDerivation ScopedProcessInstanceIdScopeDerivation() => new(
        strategy: ApiResourceScopeDerivationStrategies.StructuredResourceId,
        format: ApiResourceIdFormats.ScopedProcessInstanceId,
        scopeField: ApiResourceScopeFields.ScopeId);
}
