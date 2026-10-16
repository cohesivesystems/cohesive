using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Api.Execution.Services;
using Cohesive.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Services;

/// <summary>Prepared host bindings for one complete API surface; native resources remain caller-owned.</summary>
public sealed class ServiceModel
{
    readonly PreparedApiContribution[] contributions;
    int mappingStarted;
    internal ServiceModel(ApiDefinition definition, PreparedApiContribution[] contributions)
    { Definition = definition; this.contributions = contributions; }
    /// <summary>The combined original API contracts.</summary>
    public ApiDefinition Definition { get; }

    internal void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (Interlocked.Exchange(ref mappingStarted, 1) != 0)
            throw new ServiceMappingException("services.api.alreadyMapped", "A prepared service model can be mapped once. Build a separate model for another host.");
        var staging = new StagedRoutes(routes);
        foreach (var contribution in contributions) contribution.Map(staging);
        var endpoints = staging.DataSources.SelectMany(source => source.Endpoints).ToArray();
        var implemented = endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<ApiOperation>()?.Id).ToArray();
        if (implemented.Any(id => id is null) || implemented.Length != Definition.Endpoints.Count
            || implemented.Distinct().Count() != implemented.Length
            || Definition.Endpoints.Any(endpoint => !implemented.Contains(endpoint.Id)))
            throw new ServiceMappingException("services.api.incompleteMapping", "Every declared API endpoint must map exactly once; complete every binding.");
        var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in routes.DataSources.SelectMany(source => source.Endpoints).Concat(endpoints).OfType<RouteEndpoint>())
        foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
            if (!signatures.Add($"{method} {endpoint.RoutePattern.RawText}"))
                throw new ServiceMappingException("services.api.duplicateRoute", $"Duplicate HTTP route: {method} {endpoint.RoutePattern.RawText}.");
        foreach (var source in staging.DataSources) routes.DataSources.Add(source);
    }

    sealed class StagedRoutes(IEndpointRouteBuilder parent) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => parent.ServiceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => parent.CreateApplicationBuilder();
    }
}

/// <summary>Publishes a prepared API implementation through native ASP.NET routing.</summary>
public static class ServiceModelEndpointExtensions
{
    /// <summary>Validates all staged routes before publishing any of them to this host.</summary>
    /// <param name="routes">Native host or route group.</param><param name="model">Prepared service model.</param>
    /// <returns>The native route builder.</returns>
    /// <exception cref="ServiceMappingException">Mapping is repeated, incomplete or duplicates a route.</exception>
    public static IEndpointRouteBuilder MapServices(this IEndpointRouteBuilder routes, ServiceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Map(routes);
        return routes;
    }
}

/// <summary>A native route attachment state error, preserving InvalidOperationException catch behavior.</summary>
public sealed class ServiceMappingException : InvalidOperationException
{
    /// <summary>Creates a coded mapping failure.</summary>
    /// <param name="code">Stable diagnostic code.</param><param name="message">Actionable explanation.</param>
    public ServiceMappingException(string code, string message) : base(message) =>
        Validation = new([new(code, DiagnosticSeverity.Error, message, "/api/mapping")]);
    /// <summary>Structured diagnostics, matching the admission failure payload.</summary>
    public DocumentValidationResult Validation { get; }
}
