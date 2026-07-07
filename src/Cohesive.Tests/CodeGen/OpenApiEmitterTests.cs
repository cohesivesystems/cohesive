using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Adapters.OpenApi;
using Cohesive.Api;
using Cohesive.Api.CodeGen;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Tests.CodeGen;

public sealed class OpenApiEmitterTests
{
    [Fact]
    public void Emit_Definition_GeneratesOpenApiDocument()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Entity<Shipment>()
            .Query("Get")
                .Route("GET", "/api/shipments/{id}")
                .RouteParameter<Guid>("id")
                .Returns<ShipmentDto>()
                .Summary("Get a shipment.")
                .Tag("Shipments")
                .Done()
            .Command("Dispatch")
                .Route("POST", "/api/shipments/{id}/dispatch")
                .RouteParameter<Guid>("id")
                .Body<DispatchShipmentRequest>()
                .Returns<ShipmentDto>()
                .Tag("Shipments")
                .Done()
            .Action("Search")
                .Route("GET", "/api/search")
                .Query<SearchShipmentsRequest>()
                .Returns<ShipmentDto[]>()
                .Tag("Search")
                .Done()
            .Build();

        var emission = new OpenApiEmitter(new OpenApiEmitterOptions
        {
            FileName = "shipping.openapi.generated.json",
            Title = "Shipping",
            Version = "2026.1",
            WriteIndented = false
        }).Emit(new ApiCodeGenerationRequest(definition));

        var document = Assert.Single(emission.Documents);
        Assert.Equal("shipping.openapi.generated.json", document.FileName);

        using var json = JsonDocument.Parse(document.Text);
        var root = json.RootElement;
        Assert.Equal("3.1.0", root.GetProperty("openapi").GetString());
        Assert.Equal("Shipping", root.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("2026.1", root.GetProperty("info").GetProperty("version").GetString());

        var get = root.GetProperty("paths")
            .GetProperty("/api/shipments/{id}")
            .GetProperty("get");
        Assert.Equal("Shipping.Shipment.Get", get.GetProperty("operationId").GetString());
        Assert.Equal("Get a shipment.", get.GetProperty("summary").GetString());
        Assert.Equal("Shipments", get.GetProperty("tags")[0].GetString());
        Assert.Equal("id", get.GetProperty("parameters")[0].GetProperty("name").GetString());
        Assert.Equal("path", get.GetProperty("parameters")[0].GetProperty("in").GetString());
        Assert.True(get.GetProperty("parameters")[0].GetProperty("required").GetBoolean());
        Assert.Equal("uuid", get.GetProperty("parameters")[0].GetProperty("schema").GetProperty("format").GetString());
        Assert.Equal("#/components/schemas/ShipmentDto", get.GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());

        var dispatch = root.GetProperty("paths")
            .GetProperty("/api/shipments/{id}/dispatch")
            .GetProperty("post");
        Assert.Equal("Shipping.Shipment.Dispatch", dispatch.GetProperty("operationId").GetString());
        Assert.Equal("#/components/schemas/DispatchShipmentRequest", dispatch.GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());

        var search = root.GetProperty("paths")
            .GetProperty("/api/search")
            .GetProperty("get");
        var searchParameters = search.GetProperty("parameters");
        Assert.Equal("term", searchParameters[0].GetProperty("name").GetString());
        Assert.False(searchParameters[0].GetProperty("required").GetBoolean());
        Assert.Equal("include_archived", searchParameters[1].GetProperty("name").GetString());
        Assert.Equal("array", searchParameters[2].GetProperty("schema").GetProperty("type").GetString());

        var shipmentSchema = root.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ShipmentDto");
        Assert.Equal("object", shipmentSchema.GetProperty("type").GetString());
        Assert.Equal("Id", shipmentSchema.GetProperty("required")[0].GetString());
        Assert.Equal("Status", shipmentSchema.GetProperty("required")[1].GetString());
    }

    [Fact]
    public void Emit_ScopePolicies_GeneratesOpenApiExtensionsAndScopeParameters()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Action("ScopedSearch")
                .Route("GET", "/api/search")
                .Query<ScopedSearchRequest>()
                .Returns<ShipmentDto[]>()
                .Scope(new ApiScopePolicy(
                    scopeKind: "shipping.tenant",
                    cardinality: ApiScopeCardinality.Multiple,
                    binding: ApiScopeBinding.Query,
                    access: ApiScopeAccess.FilterToAccessible,
                    multipleScopesParameterName: "tenant_ids"))
                .Scope(new ApiScopePolicy(
                    scopeKind: "shipping.account",
                    cardinality: ApiScopeCardinality.Single,
                    binding: ApiScopeBinding.Header,
                    singleScopeParameterName: "X-Account-Id",
                    allowDefaultScope: false))
                .Done()
            .Build();

        var emission = new OpenApiEmitter(new OpenApiEmitterOptions
        {
            WriteIndented = false
        }).Emit(new ApiCodeGenerationRequest(definition));

        using var json = JsonDocument.Parse(Assert.Single(emission.Documents).Text);
        var operation = json.RootElement
            .GetProperty("paths")
            .GetProperty("/api/search")
            .GetProperty("get");

        var policies = operation.GetProperty("x-cohesive-scope-policies");
        Assert.Equal("shipping.tenant", policies[0].GetProperty("kind").GetString());
        Assert.Equal("multiple", policies[0].GetProperty("cardinality").GetString());
        Assert.Equal("query", policies[0].GetProperty("binding").GetString());
        Assert.Equal("filterToAccessible", policies[0].GetProperty("access").GetString());
        Assert.Equal("tenant_ids", policies[0].GetProperty("multipleScopesParameterName").GetString());
        Assert.True(policies[0].GetProperty("allowDefaultScope").GetBoolean());
        Assert.Equal("shipping.account", policies[1].GetProperty("kind").GetString());
        Assert.Equal("X-Account-Id", policies[1].GetProperty("singleScopeParameterName").GetString());
        Assert.False(policies[1].GetProperty("allowDefaultScope").GetBoolean());

        var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Single(parameters, static parameter =>
            parameter.GetProperty("name").GetString() == "tenant_ids"
            && parameter.GetProperty("in").GetString() == "query");

        var accountHeader = Assert.Single(parameters, static parameter =>
            parameter.GetProperty("name").GetString() == "X-Account-Id"
            && parameter.GetProperty("in").GetString() == "header");
        Assert.True(accountHeader.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void Emit_ResourceScopePolicies_GeneratesResourceDerivationExtension()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Action("GetProcess")
                .Route("GET", "/api/processes/{processId}")
                .RouteParameter<string>("processId")
                .Returns<ShipmentDto>()
                .Scope(new ApiScopePolicy(
                    scopeKind: "shipping.tenant",
                    cardinality: ApiScopeCardinality.Single,
                    binding: ApiScopeBinding.Resource,
                    access: ApiScopeAccess.ValidateAccessible,
                    resourceParameterName: "processId",
                    resourceDerivation: new(
                        strategy: ApiResourceScopeDerivationStrategies.StructuredResourceId,
                        format: ApiResourceIdFormats.ScopedProcessInstanceId,
                        scopeField: ApiResourceScopeFields.ScopeId),
                    allowDefaultScope: false))
                .Done()
            .Build();

        var emission = new OpenApiEmitter(new OpenApiEmitterOptions
        {
            WriteIndented = false
        }).Emit(new ApiCodeGenerationRequest(definition));

        using var json = JsonDocument.Parse(Assert.Single(emission.Documents).Text);
        var operation = json.RootElement
            .GetProperty("paths")
            .GetProperty("/api/processes/{processId}")
            .GetProperty("get");

        var policy = operation
            .GetProperty("x-cohesive-scope-policies")[0];

        Assert.Equal("resource", policy.GetProperty("binding").GetString());
        Assert.Equal("processId", policy.GetProperty("resourceParameterName").GetString());
        var derivation = policy.GetProperty("resourceDerivation");
        Assert.Equal("structuredResourceId", derivation.GetProperty("strategy").GetString());
        Assert.Equal("scopedProcessInstanceId", derivation.GetProperty("format").GetString());
        Assert.Equal("scopeId", derivation.GetProperty("scopeField").GetString());
    }

    [Fact]
    public void Emit_MultipleResultVariants_GeneratesMultipleResponses()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Entity<Shipment>()
            .Query("Get")
                .Route("GET", "/api/shipments/{id}")
                .RouteParameter<Guid>("id")
                .Returns<ShipmentDto>()
                .Result<ApiProblem>(ApiResultKind.NotFound, description: "Shipment was not found.")
                .Result<ApiProblem>(ApiResultKind.Conflict, id: "concurrencyConflict")
                .Done()
            .Build();

        var emission = new OpenApiEmitter(new OpenApiEmitterOptions
        {
            WriteIndented = false
        }).Emit(new ApiCodeGenerationRequest(definition));

        using var json = JsonDocument.Parse(Assert.Single(emission.Documents).Text);
        var responses = json.RootElement
            .GetProperty("paths")
            .GetProperty("/api/shipments/{id}")
            .GetProperty("get")
            .GetProperty("responses");

        Assert.Equal("#/components/schemas/ShipmentDto", responses.GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString());
        Assert.Equal("Shipment was not found.", responses.GetProperty("404").GetProperty("description").GetString());
        Assert.Equal("notFound", responses.GetProperty("404").GetProperty("x-cohesive-result-id").GetString());
        Assert.Equal("NotFound", responses.GetProperty("404").GetProperty("x-cohesive-result-kind").GetString());
        Assert.Equal("notFound", responses.GetProperty("404")
            .GetProperty("x-cohesive-results")[0]
            .GetProperty("id")
            .GetString());
        Assert.Equal("#/components/schemas/ApiProblem", responses.GetProperty("404")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString());
        Assert.Equal("#/components/schemas/ApiProblem", responses.GetProperty("409")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString());
    }

    [Fact]
    public async Task MapCohesiveOpenApi_ServesGeneratedDocumentForConfiguredDocumentName()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Entity<Shipment>()
            .Query("Get")
                .Route("GET", "/api/shipments/{id}")
                .RouteParameter<Guid>("id")
                .Returns<ShipmentDto>()
                .Done()
            .Build();

        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();

        app.MapCohesiveOpenApi(
            definition,
            new OpenApiEmitterOptions
            {
                Title = "Shipping",
                Version = "v1",
                WriteIndented = false
            });

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>());

        Assert.Equal("/openapi/cohesive/{documentName}.json", endpoint.RoutePattern.RawText);

        var context = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        context.Request.RouteValues["documentName"] = "v1";
        await using var response = new MemoryStream();
        context.Response.Body = response;

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);

        response.Position = 0;
        using var json = await JsonDocument.ParseAsync(response);
        Assert.Equal("3.1.0", json.RootElement.GetProperty("openapi").GetString());
        Assert.Equal("Shipping", json.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("Shipping.Shipment.Get", json.RootElement.GetProperty("paths")
            .GetProperty("/api/shipments/{id}")
            .GetProperty("get")
            .GetProperty("operationId")
            .GetString());
    }

    sealed record Shipment(string Id);

    sealed record ShipmentDto(string Id, string Status);

    sealed record ApiProblem(string Code, string Message);

    sealed record DispatchShipmentRequest(string Reason);

    sealed record SearchShipmentsRequest(
        string? Term,
        [property: JsonPropertyName("include_archived")] bool? IncludeArchived,
        string[]? Tags);

    sealed record ScopedSearchRequest(
        [property: JsonPropertyName("tenant_ids")] string[]? TenantIds,
        string? Term);
}
