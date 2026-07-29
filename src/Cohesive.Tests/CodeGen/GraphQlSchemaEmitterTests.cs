using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Adapters.GraphQL;
using Cohesive.Api;
using Cohesive.Api.CodeGen;
using Cohesive.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Tests.CodeGen;

public sealed class GraphQlSchemaEmitterTests
{
    [Fact]
    public void Emit_RouteLessSemanticOperation_OmitsItFromHttpBackedSchema()
    {
        var definition = Cohesive.Api.Api.Define("Execution")
            .Query("Inspect")
                .Returns<string>()
                .Done()
            .Action("Health")
                .Route("GET", "/health")
                .Returns<string>()
                .Done()
            .Build();

        var emission = new GraphQLSchemaEmitter().Emit(new ApiCodeGenerationRequest(definition));
        var sdl = Assert.Single(
            emission.Documents,
            static document => document.FileName.EndsWith(".graphql", StringComparison.Ordinal)).Text;

        Assert.Contains("health: String!", sdl, StringComparison.Ordinal);
        Assert.DoesNotContain("inspect", sdl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Emit_HttpProjection_RetainsAuthorizationAndSemanticProvenanceDirectives()
    {
        var definition = Cohesive.Api.Api.Define("Execution")
            .Action("Health")
                .Route("GET", "/health")
                .Returns<string>()
                .Requirement(new("execution.inspect", "Inspect execution health."))
                .SemanticReference(new(
                    "cohesive.execution.process-control",
                    new("cohesive-process-control-command/v1"),
                    new(["commands", "inspect"]),
                    new("spec://execution-kernel", new(["operations", "inspect"]), "Normative source.")))
                .Done()
            .Build();

        var emission = new GraphQLSchemaEmitter(new GraphQLSchemaEmitterOptions
        {
            WriteIndented = false
        }).Emit(new ApiCodeGenerationRequest(definition));
        var sdl = Assert.Single(
            emission.Documents,
            static document => document.FileName.EndsWith(".graphql", StringComparison.Ordinal)).Text;

        Assert.Contains(
            "@cohesiveAuthorizationRequirement(id: \"execution.inspect\", description: \"Inspect execution health.\")",
            sdl,
            StringComparison.Ordinal);
        Assert.Contains(
            "@cohesiveSemanticReference(authority: \"cohesive.execution.process-control\", schemaVersion: \"cohesive-process-control-command/v1\", path: [\"commands\", \"inspect\"], sourceReference: \"spec://execution-kernel\", sourceSemanticPath: [\"operations\", \"inspect\"], sourceDescription: \"Normative source.\")",
            sdl,
            StringComparison.Ordinal);

        var introspection = Assert.Single(
            emission.Documents,
            static document => document.FileName.EndsWith(".json", StringComparison.Ordinal)).Text;
        using var json = JsonDocument.Parse(introspection);
        var directives = json.RootElement.GetProperty("data").GetProperty("__schema").GetProperty("directives");
        Assert.Contains(
            directives.EnumerateArray(),
            static directive => directive.GetProperty("name").GetString() == "cohesiveAuthorizationRequirement");
        Assert.Contains(
            directives.EnumerateArray(),
            static directive => directive.GetProperty("name").GetString() == "cohesiveSemanticReference");
    }

    [Fact]
    public void Emit_Definition_GeneratesSchemaAndIntrospectionDocuments()
    {
        var definition = CreateShippingApi();

        var emission = new GraphQLSchemaEmitter(new GraphQLSchemaEmitterOptions
        {
            SchemaFileName = "shipping.graphql",
            IntrospectionFileName = "shipping.introspection.json",
            SchemaName = "Shipping",
            WriteIndented = false
        }).Emit(new ApiCodeGenerationRequest(definition));

        Assert.Equal("graphql", emission.Language);
        Assert.Equal(2, emission.Documents.Length);

        var sdl = Assert.Single(emission.Documents, static document => document.FileName == "shipping.graphql").Text;
        Assert.Contains("directive @cohesiveOperation", sdl);
        Assert.Contains("schema {", sdl);
        Assert.Contains("type Query", sdl);
        Assert.Contains("getShipment(id: ID!): ShipmentDto!", sdl);
        Assert.Contains("search(request: SearchShipmentsRequestInput): [ShipmentDto!]!", sdl);
        Assert.Contains("type Mutation", sdl);
        Assert.Contains("dispatchShipment(id: ID!, request: DispatchShipmentRequestInput!): ShipmentDto!", sdl);
        Assert.Contains("input DispatchShipmentRequestInput", sdl);
        Assert.Contains("enum ShipmentStatus", sdl);
        Assert.Contains("@cohesiveOperation(id: \"Shipping.Shipment.Dispatch\", method: \"POST\", route: \"/api/shipments/{id}/dispatch\", kind: \"command\", entity: \"Shipment\")", sdl);

        var introspection = Assert.Single(emission.Documents, static document => document.FileName == "shipping.introspection.json").Text;
        using var json = JsonDocument.Parse(introspection);
        var schema = json.RootElement.GetProperty("data").GetProperty("__schema");
        Assert.Equal("Query", schema.GetProperty("queryType").GetProperty("name").GetString());
        Assert.Equal("Mutation", schema.GetProperty("mutationType").GetProperty("name").GetString());

        var query = FindIntrospectionType(schema, "Query");
        Assert.Contains(query.GetProperty("fields").EnumerateArray(), static field => field.GetProperty("name").GetString() == "getShipment");
        Assert.Contains(query.GetProperty("fields").EnumerateArray(), static field => field.GetProperty("name").GetString() == "search");

        var mutation = FindIntrospectionType(schema, "Mutation");
        Assert.Contains(mutation.GetProperty("fields").EnumerateArray(), static field => field.GetProperty("name").GetString() == "dispatchShipment");
    }

    [Fact]
    public void Emit_ScopePolicies_GeneratesScopeDirective()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Action("ScopedSearch")
                .Route("GET", "/api/search")
                .Returns<ShipmentDto[]>()
                .Scope(new ApiScopePolicy(
                    scopeKind: "shipping.tenant",
                    cardinality: ApiScopeCardinality.Single,
                    binding: ApiScopeBinding.Header,
                    singleScopeParameterName: "X-Tenant-Id",
                    allowDefaultScope: false))
                .Done()
            .Build();

        var emission = new GraphQLSchemaEmitter(new GraphQLSchemaEmitterOptions
        {
            SchemaFileName = "shipping.graphql",
            IntrospectionFileName = "shipping.introspection.json",
            WriteIndented = false
        }).Emit(new ApiCodeGenerationRequest(definition));

        var sdl = Assert.Single(emission.Documents, static document => document.FileName == "shipping.graphql").Text;
        Assert.Contains("directive @scope", sdl);
        Assert.Contains("@scope(kind: \"shipping.tenant\", cardinality: \"single\", binding: \"header\", access: \"requireSelected\", singleScopeParameterName: \"X-Tenant-Id\", allowDefaultScope: false)", sdl);

        var introspection = Assert.Single(emission.Documents, static document => document.FileName == "shipping.introspection.json").Text;
        using var json = JsonDocument.Parse(introspection);
        var directives = json.RootElement.GetProperty("data").GetProperty("__schema").GetProperty("directives").EnumerateArray();
        var scope = Assert.Single(directives, static directive => directive.GetProperty("name").GetString() == "scope");
        Assert.True(scope.GetProperty("isRepeatable").GetBoolean());
        Assert.Contains(scope.GetProperty("args").EnumerateArray(), static argument => argument.GetProperty("name").GetString() == "allowDefaultScope");
    }

    [Fact]
    public void Emit_ResourceScopePolicies_GeneratesResourceDerivationDirective()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Action("GetProcess")
                .Route("GET", "/api/processes/{processId}")
                .RouteParameter<string>("processId")
                .Returns<ShipmentDto[]>()
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

        var emission = new GraphQLSchemaEmitter(new GraphQLSchemaEmitterOptions
        {
            SchemaFileName = "shipping.graphql",
            IntrospectionFileName = "shipping.introspection.json",
            WriteIndented = false
        }).Emit(new ApiCodeGenerationRequest(definition));

        var sdl = Assert.Single(emission.Documents, static document => document.FileName == "shipping.graphql").Text;
        Assert.Contains("resourceParameterName: \"processId\"", sdl);
        Assert.Contains("resourceDerivationStrategy: \"structuredResourceId\"", sdl);
        Assert.Contains("resourceDerivationFormat: \"scopedProcessInstanceId\"", sdl);
        Assert.Contains("resourceDerivationScopeField: \"scopeId\"", sdl);

        var introspection = Assert.Single(emission.Documents, static document => document.FileName == "shipping.introspection.json").Text;
        using var json = JsonDocument.Parse(introspection);
        var directives = json.RootElement.GetProperty("data").GetProperty("__schema").GetProperty("directives").EnumerateArray();
        var scope = Assert.Single(directives, static directive => directive.GetProperty("name").GetString() == "scope");
        Assert.Contains(scope.GetProperty("args").EnumerateArray(), static argument => argument.GetProperty("name").GetString() == "resourceDerivationStrategy");
        Assert.Contains(scope.GetProperty("args").EnumerateArray(), static argument => argument.GetProperty("name").GetString() == "resourceDerivationFormat");
        Assert.Contains(scope.GetProperty("args").EnumerateArray(), static argument => argument.GetProperty("name").GetString() == "resourceDerivationScopeField");
    }

    [Fact]
    public void Emit_MultipleResultVariants_GeneratesWrapperTypesAndUnion()
    {
        var definition = Cohesive.Api.Api.Define("Shipping")
            .Entity<Shipment>()
            .Query("Get")
                .Route("GET", "/api/shipments/{id}")
                .RouteParameter<Guid>("id")
                .Returns<ShipmentDto>()
                .Result<ApiProblem>(ApiResultKind.NotFound)
                .Done()
            .Build();

        var emission = new GraphQLSchemaEmitter(new GraphQLSchemaEmitterOptions
        {
            SchemaFileName = "shipping.graphql",
            IntrospectionFileName = "shipping.introspection.json",
            WriteIndented = false
        }).Emit(new ApiCodeGenerationRequest(definition));

        var sdl = Assert.Single(emission.Documents, static document => document.FileName == "shipping.graphql").Text;
        Assert.Contains("getShipment(id: ID!): ShipmentGetResult!", sdl);
        Assert.Contains("type ShipmentGetSuccessResult", sdl);
        Assert.Contains("body: ShipmentDto!", sdl);
        Assert.Contains("type ShipmentGetNotFoundResult", sdl);
        Assert.Contains("body: ApiProblem!", sdl);
        Assert.Contains("union ShipmentGetResult = ShipmentGetSuccessResult | ShipmentGetNotFoundResult", sdl);

        var introspection = Assert.Single(emission.Documents, static document => document.FileName == "shipping.introspection.json").Text;
        using var json = JsonDocument.Parse(introspection);
        var schema = json.RootElement.GetProperty("data").GetProperty("__schema");
        var union = FindIntrospectionType(schema, "ShipmentGetResult");
        Assert.Equal("UNION", union.GetProperty("kind").GetString());
        Assert.Contains(union.GetProperty("possibleTypes").EnumerateArray(), static type => type.GetProperty("name").GetString() == "ShipmentGetSuccessResult");
        Assert.Contains(union.GetProperty("possibleTypes").EnumerateArray(), static type => type.GetProperty("name").GetString() == "ShipmentGetNotFoundResult");
    }

    [Fact]
    public async Task MapCohesiveGraphQlSchema_ServesSchemaAndIntrospectionForConfiguredDocumentName()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();

        app.MapCohesiveGraphQLSchema(
            CreateShippingApi(),
            new GraphQLSchemaEmitterOptions
            {
                SchemaName = "Shipping",
                WriteIndented = false
            });

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var schemaEndpoint = Assert.Single(endpoints, static endpoint =>
            endpoint.RoutePattern.RawText == CohesiveGraphQLEndpointRouteBuilderExtensions.DefaultSchemaRoutePattern);
        var introspectionEndpoint = Assert.Single(endpoints, static endpoint =>
            endpoint.RoutePattern.RawText == CohesiveGraphQLEndpointRouteBuilderExtensions.DefaultIntrospectionRoutePattern);

        var schemaResponse = await InvokeAsync(app, schemaEndpoint, "v1");
        Assert.Equal(StatusCodes.Status200OK, schemaResponse.StatusCode);
        Assert.Equal("application/graphql; charset=utf-8", schemaResponse.ContentType);
        Assert.Contains("type Query", schemaResponse.Body);

        var introspectionResponse = await InvokeAsync(app, introspectionEndpoint, "v1");
        Assert.Equal(StatusCodes.Status200OK, introspectionResponse.StatusCode);
        Assert.Equal("application/json; charset=utf-8", introspectionResponse.ContentType);
        using var json = JsonDocument.Parse(introspectionResponse.Body);
        Assert.Equal("Query", json.RootElement.GetProperty("data").GetProperty("__schema").GetProperty("queryType").GetProperty("name").GetString());
    }

    static ApiDefinition CreateShippingApi() => Cohesive.Api.Api.Define("Shipping")
        .Entity<Shipment>()
        .Query("Get")
            .Route("GET", "/api/shipments/{id}")
            .RouteParameter<Guid>("id")
            .Returns<ShipmentDto>()
            .Summary("Get a shipment.")
            .Done()
        .Command("Dispatch")
            .Route("POST", "/api/shipments/{id}/dispatch")
            .RouteParameter<Guid>("id")
            .Body<DispatchShipmentRequest>()
            .Returns<ShipmentDto>()
            .Summary("Dispatch a shipment.")
            .Done()
        .Action("Search")
            .Route("GET", "/api/search")
            .Query<SearchShipmentsRequest>()
            .Returns<ShipmentDto[]>()
            .Summary("Search shipments.")
            .Done()
        .Build();

    static JsonElement FindIntrospectionType(JsonElement schema, string name)
    {
        foreach (var type in schema.GetProperty("types").EnumerateArray())
        {
            if (type.GetProperty("name").GetString() == name)
                return type;
        }

        throw new InvalidOperationException($"Introspection type '{name}' was not emitted.");
    }

    static async Task<(int StatusCode, string? ContentType, string Body)> InvokeAsync(WebApplication app, RouteEndpoint endpoint, string documentName)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        context.Request.RouteValues["documentName"] = documentName;
        await using var response = new MemoryStream();
        context.Response.Body = response;

        await endpoint.RequestDelegate!(context);

        response.Position = 0;
        using var reader = new StreamReader(response);
        return (context.Response.StatusCode, context.Response.ContentType, await reader.ReadToEndAsync());
    }

    sealed record Shipment(string Id);

    sealed record ShipmentDto(string Id, ShipmentStatus Status, string[] Tags);

    sealed record ApiProblem(string Code, string Message);

    enum ShipmentStatus
    {
        Pending,
        Dispatched
    }

    sealed record DispatchShipmentRequest(string Reason);

    sealed record SearchShipmentsRequest(
        string? Term,
        [property: JsonPropertyName("include_archived")] bool? IncludeArchived,
        string[]? Tags);
}
