using Cohesive.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.OpenApi;

/// <summary>
/// ASP.NET endpoint helpers for serving OpenAPI documents emitted from Cohesive API definitions.
/// </summary>
public static class CohesiveOpenApiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Default route pattern for Cohesive-generated OpenAPI documents.
    /// </summary>
    public const string DefaultRoutePattern = "/openapi/cohesive/{documentName}.json";

    /// <summary>
    /// Maps a Cohesive-generated OpenAPI document endpoint.
    /// </summary>
    public static RouteHandlerBuilder MapCohesiveOpenApi(
        this IEndpointRouteBuilder endpoints,
        ApiDefinition definition,
        OpenApiEmitterOptions? options = null,
        string routePattern = DefaultRoutePattern,
        string publishedDocumentName = "v1"
        )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(routePattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedDocumentName);

        var emission = new OpenApiEmitter(options).Emit(definition);
        var document = emission.Documents.Single();

        return endpoints
            .MapGet(routePattern, ([FromRoute(Name = "documentName")] string requestedDocumentName) =>
            {
                if (!string.Equals(requestedDocumentName, publishedDocumentName, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound();

                return Results.Text(document.Text, "application/json; charset=utf-8");
            })
            .WithName("CohesiveOpenApi")
            .ExcludeFromDescription();
    }
}
