using Cohesive.Api;
using Cohesive.Execution;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Tests.Api;

public sealed class ApiDefinitionTests
{
    [Fact]
    public void Build_RootSemanticOperations_RetainAuthorizationAndExactProvenanceWithoutHttp()
    {
        var authorization = new ApiAuthorizationRequirement(
            id: "execution.inspect",
            description: "Inspect execution state.");
        var reference = new ApiSemanticReference(
            authority: "cohesive.execution.process-control",
            schemaVersion: new("cohesive-process-control/v1"),
            path: ExecutionSemanticPath.From("commands").Append("inspect"),
            source: new ExecutionSourceProvenance("execution-kernel-spec#control"));

        var definition = Cohesive.Api.Api.Define("Execution")
            .Query("Inspect")
                .Accepts<InspectRequest>()
                .Returns<ExecutionStatus>()
                .Requirement(authorization)
                .Requirement(authorization)
                .SemanticReference(reference)
                .SemanticReference(reference)
                .Done()
            .Command("Pause")
                .Accepts<PauseRequest>()
                .Returns<ExecutionStatus>()
                .Done()
            .Build();

        Assert.Collection(
            definition.Operations,
            inspect =>
            {
                Assert.Equal(ApiOperationKind.Query, inspect.Kind);
                Assert.Equal("Execution.Inspect", inspect.Id.Value);
                Assert.Null(inspect.Http);
                Assert.Null(inspect.PrimaryResult.Http);
                Assert.Same(authorization, Assert.Single(inspect.AuthorizationRequirements));
                Assert.Same(reference, Assert.Single(inspect.SemanticReferences));
            },
            pause =>
            {
                Assert.Equal(ApiOperationKind.Command, pause.Kind);
                Assert.Equal("Execution.Pause", pause.Id.Value);
                Assert.Null(pause.Http);
                Assert.Null(pause.PrimaryResult.Http);
            });
    }

    [Fact]
    public void WithHttp_ProjectsSemanticOperationWithoutLosingItsAuthorityMetadata()
    {
        var authorization = new ApiAuthorizationRequirement("execution.pause");
        var reference = new ApiSemanticReference(
            authority: "cohesive.execution.process-control",
            schemaVersion: new("cohesive-process-control/v1"),
            path: ExecutionSemanticPath.From("commands").Append("pause"));
        var semanticEndpoint = Cohesive.Api.Api.Define("Execution")
            .Command("Pause")
                .Accepts<PauseRequest>()
                .Returns<ExecutionStatus>()
                .Result<ApiProblem>(ApiResultKind.Conflict)
                .Requirement(authorization)
                .SemanticReference(reference)
                .Build();
        var semantic = semanticEndpoint.Operation;

        var projectedEndpoint = semanticEndpoint.WithHttp(new HttpBinding(
            method: "POST",
            route: "/api/executions/{instance}/pause",
            parameters: [new("instance", HttpParameterSource.Route, typeof(string))],
            body: new(typeof(PauseRequest))));
        var projected = projectedEndpoint.Operation;

        Assert.Null(semantic.Http);
        Assert.All(semantic.Results, static result => Assert.Null(result.Http));
        Assert.Equal(semanticEndpoint.Id, projectedEndpoint.Id);
        Assert.Equal("POST", projected.Http?.Method);
        Assert.Equal(StatusCodes.Status200OK, projected.Results[0].Http?.StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, projected.Results[1].Http?.StatusCode);
        Assert.Same(authorization, Assert.Single(projected.AuthorizationRequirements));
        Assert.Same(reference, Assert.Single(projected.SemanticReferences));
    }

    [Fact]
    public void Build_EntityDsl_InfersRouteParametersAndBodies()
    {
        var transitionReference = DefinitionReference("Dispatch");
        var definition = Cohesive.Api.Api.Define()
            .Entity<Shipment>()
            .Query("GetById")
                .Route("GET", "/api/shipments/{id}")
                .Returns<ShipmentDto>()
                .Done()
            .Command("Dispatch")
                .Route("POST", "/api/shipments/{id}/dispatch")
                .Accepts<DispatchShipmentRequest>()
                .Transition(transitionReference)
                .Done()
            .Build();

        Assert.Equal(2, definition.Operations.Count);

        var query = definition.Operations[0];
        Assert.Equal(ApiOperationKind.Query, query.Kind);
        Assert.Equal(typeof(void), query.RequestType);
        Assert.Equal(typeof(ShipmentDto), query.ResponseType);
        var queryHttp = Assert.IsType<HttpBinding>(query.Http);
        Assert.Equal("GET", queryHttp.Method);
        Assert.Equal("/api/shipments/{id}", queryHttp.Route);
        var queryRoute = Assert.Single(queryHttp.Parameters);
        Assert.Equal("id", queryRoute.Name);
        Assert.Equal(HttpParameterSource.Route, queryRoute.Source);
        Assert.Equal(typeof(string), queryRoute.Type);
        Assert.Null(queryHttp.Body);

        var command = definition.Operations[1];
        Assert.Equal(ApiOperationKind.Command, command.Kind);
        Assert.Equal(typeof(DispatchShipmentRequest), command.RequestType);
        Assert.Equal(typeof(void), command.ResponseType);
        Assert.Same(command.PrimaryResult, Assert.Single(command.Results));
        Assert.Equal(ApiResultKind.NoContent, command.PrimaryResult.Kind);
        Assert.Equal(StatusCodes.Status204NoContent, command.PrimaryResult.Http?.StatusCode);
        Assert.Same(transitionReference, command.TransitionReference);
        var commandHttp = Assert.IsType<HttpBinding>(command.Http);
        Assert.NotNull(commandHttp.Body);
        Assert.Equal(typeof(DispatchShipmentRequest), commandHttp.Body!.BodyType);
        var commandRoute = Assert.Single(commandHttp.Parameters);
        Assert.Equal("id", commandRoute.Name);
        Assert.Equal(HttpParameterSource.Route, commandRoute.Source);
        Assert.Equal(typeof(string), commandRoute.Type);
    }

    [Fact]
    public void Build_ReturnsDefinesPrimarySuccessResult()
    {
        var definition = Cohesive.Api.Api.Define()
            .Entity<Shipment>()
            .Query("Get")
                .Route("GET", "/api/shipments/{id}")
                .Returns<ShipmentDto>()
                .Done()
            .Build();

        var operation = Assert.Single(definition.Operations);
        var result = Assert.Single(operation.Results);

        Assert.Same(result, operation.PrimaryResult);
        Assert.Equal(typeof(ShipmentDto), operation.ResponseType);
        Assert.Equal(typeof(ShipmentDto), result.BodyType);
        Assert.Equal("success", result.Id);
        Assert.Equal(ApiResultKind.Success, result.Kind);
        Assert.True(result.IsPrimary);
        Assert.Equal(StatusCodes.Status200OK, result.Http?.StatusCode);
    }

    [Fact]
    public void Build_ResultAddsAdditionalVariants()
    {
        var definition = Cohesive.Api.Api.Define()
            .Entity<Shipment>()
            .Query("Get")
                .Route("GET", "/api/shipments/{id}")
                .Returns<ShipmentDto>()
                .Result<ApiProblem>(ApiResultKind.NotFound, description: "Shipment was not found.")
                .Result(ApiResultKind.NoContent, id: "cachedNoContent")
                .Done()
            .Build();

        var operation = Assert.Single(definition.Operations);

        Assert.Equal(typeof(ShipmentDto), operation.ResponseType);
        Assert.Equal(typeof(ShipmentDto), operation.PrimaryResult.BodyType);
        Assert.Collection(
            operation.Results,
            result =>
            {
                Assert.Equal("success", result.Id);
                Assert.True(result.IsPrimary);
                Assert.Equal(StatusCodes.Status200OK, result.Http?.StatusCode);
            },
            result =>
            {
                Assert.Equal("notFound", result.Id);
                Assert.False(result.IsPrimary);
                Assert.Equal(ApiResultKind.NotFound, result.Kind);
                Assert.Equal(typeof(ApiProblem), result.BodyType);
                Assert.Equal(StatusCodes.Status404NotFound, result.Http?.StatusCode);
                Assert.Equal("Shipment was not found.", result.Description);
            },
            result =>
            {
                Assert.Equal("cachedNoContent", result.Id);
                Assert.Equal(ApiResultKind.NoContent, result.Kind);
                Assert.Equal(typeof(void), result.BodyType);
                Assert.Equal(StatusCodes.Status204NoContent, result.Http?.StatusCode);
            });
    }

    [Fact]
    public void Build_ResultRejectsDuplicateIds()
    {
        var builder = Cohesive.Api.Api.Define()
            .Entity<Shipment>()
            .Query("Get")
                .Route("GET", "/api/shipments/{id}")
                .Returns<ShipmentDto>()
                .Result<ApiProblem>(ApiResultKind.NotFound, id: "problem")
                .Result<ApiProblem>(ApiResultKind.Conflict, id: "problem");

        var exception = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("duplicate result id 'problem'", exception.Message);
    }

    [Fact]
    public void Operation_PrimaryResultDefaultsToFirstResultWhenNotExplicit()
    {
        var operation = new ApiOperation(
            name: "Get",
            kind: ApiOperationKind.Query,
            requestType: typeof(void),
            responseType: typeof(ShipmentDto),
            http: new HttpBinding("GET", "/api/shipments/{id}", null, null),
            results:
            [
                new ApiResultDefinition(ApiResultKind.Success, typeof(ShipmentDto), http: new ApiHttpResultBinding(200)),
                new ApiResultDefinition(ApiResultKind.NotFound, typeof(ApiProblem), http: new ApiHttpResultBinding(404))
            ]);

        Assert.True(operation.Results[0].IsPrimary);
        Assert.Same(operation.Results[0], operation.PrimaryResult);
        Assert.Equal(typeof(ShipmentDto), operation.ResponseType);
    }

    [Fact]
    public void Operation_WithoutHttp_StripsResultTransportBindingsAtTheSemanticBoundary()
    {
        var operation = new ApiOperation(
            name: "Inspect",
            kind: ApiOperationKind.Query,
            requestType: typeof(InspectRequest),
            responseType: typeof(ExecutionStatus),
            results:
            [
                new ApiResultDefinition(
                    ApiResultKind.Success,
                    typeof(ExecutionStatus),
                    http: new ApiHttpResultBinding(StatusCodes.Status200OK))
            ]);

        Assert.Null(operation.Http);
        Assert.Null(operation.PrimaryResult.Http);
    }

    [Fact]
    public void Build_OptionalQueryParameters_RecordOptionality()
    {
        var definition = Cohesive.Api.Api.Define()
            .Action("Search")
                .Route("GET", "/api/search")
                .OptionalQueryParameter<string>("term")
                .OptionalQueryParameter<int?>("limit")
                .Returns<ShipmentDto[]>()
                .Done()
            .Build();

        var operation = Assert.Single(definition.Operations);
        var http = Assert.IsType<HttpBinding>(operation.Http);
        Assert.Collection(
            http.Parameters,
            term =>
            {
                Assert.Equal("term", term.Name);
                Assert.Equal(HttpParameterSource.Query, term.Source);
                Assert.Equal(typeof(string), term.Type);
                Assert.True(term.IsOptional);
            },
            limit =>
            {
                Assert.Equal("limit", limit.Name);
                Assert.Equal(HttpParameterSource.Query, limit.Source);
                Assert.Equal(typeof(int?), limit.Type);
                Assert.True(limit.IsOptional);
            });
    }

    [Fact]
    public void Build_QueryDto_RecordsQueryBindingAndRequestType()
    {
        var definition = Cohesive.Api.Api.Define()
            .Action("Search")
                .Route("GET", "/api/search")
                .Query<SearchShipmentsRequest>()
                .Returns<ShipmentDto[]>()
                .Done()
            .Build();

        var operation = Assert.Single(definition.Operations);
        var http = Assert.IsType<HttpBinding>(operation.Http);
        Assert.Equal(typeof(SearchShipmentsRequest), operation.RequestType);
        Assert.NotNull(http.Query);
        Assert.Equal(typeof(SearchShipmentsRequest), http.Query!.QueryType);
        Assert.Null(http.Body);
        Assert.Empty(http.Parameters);
    }

    [Fact]
    public void Build_EndpointHandles_AreCollectedByDefinitionBuilder()
    {
        var api = Cohesive.Api.Api.Define("Shipments");
        var entity = api.Entity<Shipment>();

        var query = entity
            .Query("Search")
            .Route("GET", "/api/shipments")
            .Query<SearchShipmentsRequest>()
            .Returns<ShipmentDto[]>()
            .Build();
        var get = entity
            .Query("Get")
            .Route("GET", "/api/shipments/{id}")
            .RouteParameter<string>("id")
            .Returns<ShipmentDto>()
            .Build();

        var definition = api.Build();

        Assert.Equal(2, definition.Endpoints.Count);
        Assert.Same(query.Operation, definition.GetOperation(query));
        Assert.Same(get.Operation, definition.GetOperation(get));
        Assert.Equal("Shipments.Shipment.Search", query.Id.Value);
        Assert.Equal("Shipments.Shipment.Get", get.Id.Value);
    }

    [Fact]
    public void MapApiDefinition_AppliesMinimalApiMetadata()
    {
        var definition = Cohesive.Api.Api.Define()
            .Entity<Shipment>()
            .Query("GetById")
                .Route("GET", "/api/shipments/{id}")
                .Returns<ShipmentDto>()
                .Result<ApiProblem>(ApiResultKind.NotFound)
                .Summary("Get a shipment by id.")
                .Description("Loads the shipment read model from the API surface.")
                .Done()
            .Build();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddEndpointsApiExplorer();
        var app = builder.Build();

        app.MapApiDefinition(definition, static operation => operation.Name switch
        {
            "GetById" => (Func<string, ShipmentDto>)(id => new ShipmentDto(id, "Ready")),
            _ => throw new InvalidOperationException($"No handler configured for '{operation.Name}'.")
        });

        var dataSources = ((IEndpointRouteBuilder)app).DataSources;
        var endpoint = Assert.Single(dataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>());
        Assert.Equal("/api/shipments/{id}", endpoint.RoutePattern.RawText);

        var nameMetadata = Assert.Single(endpoint.Metadata.OfType<IEndpointNameMetadata>());
        Assert.Equal("GetById", nameMetadata.EndpointName);

        var summaryMetadata = Assert.Single(endpoint.Metadata.OfType<IEndpointSummaryMetadata>());
        Assert.Equal("Get a shipment by id.", summaryMetadata.Summary);

        var descriptionMetadata = Assert.Single(endpoint.Metadata.OfType<IEndpointDescriptionMetadata>());
        Assert.Equal("Loads the shipment read model from the API surface.", descriptionMetadata.Description);

        var tagsMetadata = Assert.Single(endpoint.Metadata.OfType<ITagsMetadata>());
        Assert.Equal(["Shipment"], tagsMetadata.Tags);

        Assert.Contains(
            endpoint.Metadata.OfType<IProducesResponseTypeMetadata>(),
            static metadata => metadata.StatusCode == StatusCodes.Status200OK && metadata.Type == typeof(ShipmentDto));

        Assert.Contains(
            endpoint.Metadata.OfType<IProducesResponseTypeMetadata>(),
            static metadata => metadata.StatusCode == StatusCodes.Status404NotFound && metadata.Type == typeof(ApiProblem));

        Assert.Contains(endpoint.Metadata, static metadata => metadata is ApiOperation apiOperation && apiOperation.Name == "GetById");
    }

    static ExecutionDefinitionReference DefinitionReference(string name) => new(
        new(name),
        new("revision/1"),
        new(
            ExecutionDefinitionFingerprinter.Algorithm,
            ExecutionDefinitionFingerprinter.Canonicalization,
            new string('a', 64)));

    sealed record Shipment(string Id);

    sealed record ShipmentDto(string Id, string Status);

    sealed record ApiProblem(string Code, string Message);

    sealed record DispatchShipmentRequest(string Reason);

    sealed record SearchShipmentsRequest(
        string? Term,
        [property: JsonPropertyName("include_archived")] bool? IncludeArchived,
        int? Limit);

    sealed record InspectRequest(string InstanceId);

    sealed record PauseRequest(string InstanceId);

    sealed record ExecutionStatus(string Phase);
}
