using Cohesive.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.GraphQL;

/// <summary>
/// ASP.NET endpoint helpers for serving GraphQL schema views emitted from Cohesive API definitions.
/// </summary>
public static class CohesiveGraphQLEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Default route pattern for GraphQL SDL.
    /// </summary>
    public const string DefaultSchemaRoutePattern = "/graphql/cohesive/{documentName}.graphql";

    /// <summary>
    /// Default route pattern for GraphQL introspection JSON.
    /// </summary>
    public const string DefaultIntrospectionRoutePattern = "/graphql/cohesive/{documentName}.json";

    /// <summary>
    /// Maps Cohesive-generated GraphQL schema view endpoints.
    /// </summary>
    public static CohesiveGraphQlEndpointBuilders MapCohesiveGraphQLSchema(
        this IEndpointRouteBuilder endpoints,
        ApiDefinition definition,
        GraphQLSchemaEmitterOptions? options = null,
        string schemaRoutePattern = DefaultSchemaRoutePattern,
        string introspectionRoutePattern = DefaultIntrospectionRoutePattern,
        string publishedDocumentName = "v1"
        )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaRoutePattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(introspectionRoutePattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedDocumentName);

        var emission = new GraphQLSchemaEmitter(options).EmitSchema(definition);

        var schema = endpoints
            .MapGet(schemaRoutePattern, ([FromRoute(Name = "documentName")] string requestedDocumentName) =>
            {
                if (!string.Equals(requestedDocumentName, publishedDocumentName, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound();

                return Results.Text(content: emission.Sdl, contentType: "application/graphql; charset=utf-8");
            })
            .WithName("CohesiveGraphQlSchema")
            .ExcludeFromDescription();

        var introspection = endpoints
            .MapGet(introspectionRoutePattern, ([FromRoute(Name = "documentName")] string requestedDocumentName) =>
            {
                if (!string.Equals(requestedDocumentName, publishedDocumentName, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound();

                return Results.Text(emission.IntrospectionJson, "application/json; charset=utf-8");
            })
            .WithName("CohesiveGraphQlIntrospection")
            .ExcludeFromDescription();

        return new(schema, introspection);
    }
}

/// <summary>
/// Endpoint builders for Cohesive-generated GraphQL schema views.
/// </summary>
public sealed record CohesiveGraphQlEndpointBuilders(RouteHandlerBuilder Schema, RouteHandlerBuilder Introspection);
