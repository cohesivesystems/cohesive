using Cohesive.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Tests.Api;

public sealed class ApiEndpointRouteBuilderExtensionsTests
{
    [Fact]
    public void MapApiEndpoint_AttachesDeclaredScopePolicyMetadata()
    {
        var scopePolicy = new ApiScopePolicy(
            scopeKind: "shipping.tenant",
            cardinality: ApiScopeCardinality.Single,
            binding: ApiScopeBinding.Header,
            singleScopeParameterName: "X-Tenant-Id",
            allowDefaultScope: false
            );
        var endpoint = Cohesive.Api.Api.Define("Shipping")
            .Action("QueryShipments")
            .Route("GET", "/shipments")
            .Returns<string[]>()
            .Scope(scopePolicy)
            .Build();
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapApiEndpoint(endpoint, () => Results.Ok(Array.Empty<string>()));

        var routeEndpoint = Assert.Single(((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>());
        var metadataPolicy = Assert.Single(routeEndpoint.Metadata.GetOrderedMetadata<ApiScopePolicy>());

        Assert.Equal(scopePolicy, metadataPolicy);
    }
}
