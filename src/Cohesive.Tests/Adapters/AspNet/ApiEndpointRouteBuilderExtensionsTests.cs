using Cohesive.Adapters.AspNet;
using Cohesive.Api;
using Cohesive.Execution;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Tests.Adapters.AspNet;

public sealed class ApiEndpointRouteBuilderExtensionsTests
{
    const string InspectPolicy = "cohesive.execution.inspect";

    [Fact]
    public void MapApiDefinition_ProjectsRoutesMethodsMetadataAndCustomConfigurationInDefinitionOrder()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Entity<Shipment>()
            .Query("GetById")
                .Route("GET", "/api/shipments/{id}")
                .Returns<ShipmentDto>()
                .Result<ApiProblem>(ApiResultKind.NotFound)
                .Summary("Get a shipment by id.")
                .Description("Loads the shipment read model from the API surface.")
                .Done()
            .Command("Dispatch")
                .Route("POST", "/api/shipments/{id}/dispatch")
                .Accepts<DispatchShipmentRequest>()
                .Returns<ShipmentDto>()
                .Done()
            .Build();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddEndpointsApiExplorer();
        var app = builder.Build();
        var configuredOperations = new List<ApiOperation>();

        var builders = app.MapApiDefinition(
            definition,
            static operation => operation.Name switch
            {
                "GetById" => (Func<string, ShipmentDto>)(id => new ShipmentDto(id, "Ready")),
                "Dispatch" => (Func<string, DispatchShipmentRequest, ShipmentDto>)((id, _) =>
                    new ShipmentDto(id, "Dispatched")),
                _ => throw new InvalidOperationException($"No handler configured for '{operation.Name}'.")
            },
            (routeBuilder, operation) =>
            {
                configuredOperations.Add(operation);
                routeBuilder.WithMetadata(new ProjectionMarker(operation.Id));
            });

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Equal(2, builders.Count);
        Assert.Equal(definition.Operations, configuredOperations);
        Assert.Equal(["/api/shipments/{id}", "/api/shipments/{id}/dispatch"],
            endpoints.Select(static endpoint => endpoint.RoutePattern.RawText));
        Assert.Equal(["GET"], endpoints[0].Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        Assert.Equal(["POST"], endpoints[1].Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

        var get = endpoints[0];
        Assert.Equal("GetById", get.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName);
        Assert.Equal("Get a shipment by id.", get.Metadata.GetMetadata<IEndpointSummaryMetadata>()!.Summary);
        Assert.Equal(
            "Loads the shipment read model from the API surface.",
            get.Metadata.GetMetadata<IEndpointDescriptionMetadata>()!.Description);
        Assert.Equal(["Shipment"], get.Metadata.GetMetadata<ITagsMetadata>()!.Tags);
        Assert.Contains(
            get.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
            static metadata => metadata.StatusCode == StatusCodes.Status200OK && metadata.Type == typeof(ShipmentDto));
        Assert.Contains(
            get.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
            static metadata => metadata.StatusCode == StatusCodes.Status404NotFound && metadata.Type == typeof(ApiProblem));

        for (var i = 0; i < endpoints.Length; i++)
        {
            Assert.Same(definition.Operations[i], endpoints[i].Metadata.GetMetadata<ApiOperation>());
            Assert.Same(definition.Operations[i].Http, endpoints[i].Metadata.GetMetadata<HttpBinding>());
            Assert.Equal(definition.Operations[i].Id, endpoints[i].Metadata.GetMetadata<ProjectionMarker>()!.OperationId);
        }

        var accepts = endpoints[1].Metadata.GetOrderedMetadata<IAcceptsMetadata>();
        Assert.NotEmpty(accepts);
        Assert.All(accepts, static metadata =>
        {
            Assert.Equal(typeof(DispatchShipmentRequest), metadata.RequestType);
            Assert.Equal(["application/json"], metadata.ContentTypes);
        });
    }

    [Fact]
    public void CanonicalApiDefinition_DuplicateEndpointIdsFailBeforeAspNetProjection()
    {
        var endpoint = Cohesive.Api.Api.Define("Shipping")
            .Query("GetById")
                .Route("GET", "/api/shipments/{id}")
                .Returns<ShipmentDto>()
                .Build();

        var error = Assert.Throws<InvalidOperationException>(() => ApiDefinition.From(endpoint, endpoint));

        Assert.Contains("duplicate endpoint id 'Shipping.GetById'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapApiEndpoint_RouteLessOperation_FailsWithProjectionDiagnostic()
    {
        var endpoint = Cohesive.Api.Api.Define("Execution")
            .Command("Pause")
                .Accepts<string>()
                .Build();
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            app.MapApiEndpoint(endpoint, (string _) => Results.NoContent()));

        Assert.Contains("Execution.Pause", error.Message, StringComparison.Ordinal);
        Assert.Contains("does not declare an HTTP projection", error.Message, StringComparison.Ordinal);
    }

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
        Assert.Empty(routeEndpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    [Fact]
    public void MapApiEndpoint_AuthorizationRequirementWithoutPolicyResolver_FailsClosedBeforeMapping()
    {
        var endpoint = SecuredInspectEndpoint();
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            app.MapApiEndpoint(endpoint, (string instance) => Results.Ok(instance)));

        Assert.Contains("Execution.Inspect", error.Message, StringComparison.Ordinal);
        Assert.Contains("no ASP.NET authorization policy resolver", error.Message, StringComparison.Ordinal);
        Assert.Empty(((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints));
    }

    [Fact]
    public void MapApiDefinition_MissingPolicyResolver_FailsBeforeCreatingAnyHandlersOrRoutes()
    {
        var definition = Cohesive.Api.Api.Define("Execution")
            .Query("Health")
                .Route("GET", "/health")
                .Returns<string>()
                .Done()
            .Query("Inspect")
                .Route("GET", "/executions/{instance}")
                .Returns<string>()
                .Requirement(new("execution.inspect"))
                .Done()
            .Build();
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();
        var handlerFactoryCalls = 0;

        Assert.Throws<InvalidOperationException>(() => app.MapApiDefinition(
            definition,
            _ =>
            {
                handlerFactoryCalls++;
                return () => Results.Ok();
            }));

        Assert.Equal(0, handlerFactoryCalls);
        Assert.Empty(((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints));
    }

    [Fact]
    public void MapApiDefinition_RouteLessOperation_FailsBeforeCreatingAnyHandlersOrRoutes()
    {
        var definition = Cohesive.Api.Api.Define("Execution")
            .Query("Health")
                .Route("GET", "/health")
                .Returns<string>()
                .Done()
            .Query("Inspect")
                .Returns<string>()
                .Done()
            .Build();
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();
        var handlerFactoryCalls = 0;

        var error = Assert.Throws<InvalidOperationException>(() => app.MapApiDefinition(
            definition,
            _ =>
            {
                handlerFactoryCalls++;
                return () => Results.Ok();
            }));

        Assert.Contains("Execution.Inspect", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handlerFactoryCalls);
        Assert.Empty(((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints));
    }

    [Fact]
    public void MapApiEndpoint_AttachesAuthorizationAndSemanticReferenceMetadata()
    {
        var authorization = new ApiAuthorizationRequirement("execution.inspect");
        var reference = new ApiSemanticReference(
            authority: "cohesive.execution.process-control",
            schemaVersion: new("cohesive-process-control/v1"),
            path: ExecutionSemanticPath.From("commands").Append("inspect"));
        var endpoint = Cohesive.Api.Api.Define("Execution")
            .Query("Inspect")
            .Route("GET", "/executions/{instance}")
            .Returns<string>()
            .Requirement(authorization)
            .SemanticReference(reference)
            .Build();
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();

        app.MapApiEndpoint(
            endpoint,
            (string instance) => Results.Ok(instance),
            authorizationPolicyResolver: ResolvePolicy);

        var routeEndpoint = Assert.Single(((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>());
        Assert.Same(
            authorization,
            Assert.Single(routeEndpoint.Metadata.GetOrderedMetadata<ApiAuthorizationRequirement>()));
        Assert.Same(
            reference,
            Assert.Single(routeEndpoint.Metadata.GetOrderedMetadata<ApiSemanticReference>()));
        Assert.Equal(
            InspectPolicy,
            Assert.Single(routeEndpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
    }

    [Fact]
    public async Task MapApiEndpoint_UnauthenticatedInvocationNeverReachesSecuredHandler()
    {
        var endpoint = SecuredInspectEndpoint();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(InspectPolicy, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IPolicyEvaluator, UnauthenticatedPolicyEvaluator>();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, StatusCodeAuthorizationResultHandler>();
        var app = builder.Build();
        var handlerCalls = 0;
        app.MapApiEndpoint(
            endpoint,
            (string instance) =>
            {
                handlerCalls++;
                return Results.Ok(instance);
            },
            authorizationPolicyResolver: ResolvePolicy);
        var routeEndpoint = Assert.Single(((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>());
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        context.Request.RouteValues["instance"] = "process/secured";
        context.SetEndpoint(routeEndpoint);
        var middleware = new AuthorizationMiddleware(
            routeEndpoint.RequestDelegate!,
            app.Services.GetRequiredService<IAuthorizationPolicyProvider>());

        await middleware.Invoke(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(0, handlerCalls);
    }

    static ApiEndpoint SecuredInspectEndpoint() =>
        Cohesive.Api.Api.Define("Execution")
            .Query("Inspect")
                .Route("GET", "/executions/{instance}")
                .Returns<string>()
                .Requirement(new("execution.inspect"))
            .Build();

    sealed record Shipment(string Id);

    sealed record ShipmentDto(string Id, string Status);

    sealed record ApiProblem(string Code, string Message);

    sealed record DispatchShipmentRequest(string Reason);

    sealed record ProjectionMarker(ApiEndpointId OperationId);

    static string ResolvePolicy(ApiOperation operation, ApiAuthorizationRequirement requirement)
    {
        Assert.Equal("Execution.Inspect", operation.Id.Value);
        Assert.Equal("execution.inspect", requirement.Id);
        return InspectPolicy;
    }

    sealed class UnauthenticatedPolicyEvaluator : IPolicyEvaluator
    {
        public Task<AuthenticateResult> AuthenticateAsync(
            AuthorizationPolicy policy,
            HttpContext context) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task<PolicyAuthorizationResult> AuthorizeAsync(
            AuthorizationPolicy policy,
            AuthenticateResult authenticationResult,
            HttpContext context,
            object? resource) =>
            Task.FromResult(PolicyAuthorizationResult.Challenge());
    }

    sealed class StatusCodeAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(
            RequestDelegate next,
            HttpContext context,
            AuthorizationPolicy policy,
            PolicyAuthorizationResult authorizeResult)
        {
            context.Response.StatusCode = authorizeResult.Challenged
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
    }
}
