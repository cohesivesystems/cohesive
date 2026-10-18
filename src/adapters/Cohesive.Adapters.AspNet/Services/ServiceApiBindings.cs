using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Api.Execution.Services;
using Cohesive.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Services;

/// <summary>Prepared host bindings for one complete API surface; native resources remain caller-owned.</summary>
public sealed class ServiceApiBindings
{
    readonly PreparedApiContribution[] contributions;
    int mappingStarted;
    internal ServiceApiBindings(ApiDefinition definition, PreparedApiContribution[] contributions)
    { Definition = definition; this.contributions = contributions; }
    /// <summary>The combined original API contracts.</summary>
    public ApiDefinition Definition { get; }

    internal void Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (Interlocked.Exchange(ref mappingStarted, 1) != 0)
            throw new ServiceMappingException("services.api.alreadyMapped", "A prepared service model can be mapped once. Build a separate model for another host.", "/api/mapping/state");
        var staging = new StagedRoutes(routes);
        foreach (var contribution in contributions) contribution.Map(staging);
        var endpoints = staging.DataSources.SelectMany(source => source.Endpoints).ToArray();
        var counts = new Dictionary<ApiEndpointId, int>();
        var errors = new List<DocumentValidationDiagnostic>();
        for (var index = 0; index < endpoints.Length; index++)
        {
            if (endpoints[index].Metadata.GetMetadata<ApiOperation>() is not { } operation)
            {
                errors.Add(new("services.api.incompleteMapping", DiagnosticSeverity.Error,
                    $"Mapped endpoint '{endpoints[index].DisplayName}' has no API operation metadata.", $"/api/mapping/endpoints/{index}"));
                continue;
            }
            counts[operation.Id] = counts.GetValueOrDefault(operation.Id) + 1;
            if (!Definition.Endpoints.Any(endpoint => endpoint.Id == operation.Id))
                errors.Add(new("services.api.incompleteMapping", DiagnosticSeverity.Error,
                    $"Endpoint '{operation.Id}' is not declared by this model.", EndpointLocation(operation.Id)));
        }
        foreach (var endpoint in Definition.Endpoints)
        {
            var count = counts.GetValueOrDefault(endpoint.Id);
            if (count != 1) errors.Add(new("services.api.incompleteMapping", DiagnosticSeverity.Error,
                $"Endpoint '{endpoint.Id}' mapped {count} times; expected exactly once.", EndpointLocation(endpoint.Id)));
        }
        if (errors.Count != 0) throw new ServiceMappingException(new([.. errors]));
        var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in routes.DataSources.SelectMany(source => source.Endpoints).Concat(endpoints).OfType<RouteEndpoint>())
        foreach (var method in endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
            if (!signatures.Add($"{method} {endpoint.RoutePattern.RawText}"))
                throw new ServiceMappingException("services.api.duplicateRoute", $"Duplicate HTTP route: {method} {endpoint.RoutePattern.RawText} (endpoint {endpoint.Metadata.GetMetadata<ApiOperation>()?.Id}).",
                    $"/api/routes/{Escape(method)}/{Escape(endpoint.RoutePattern.RawText ?? string.Empty)}");
        foreach (var source in staging.DataSources) routes.DataSources.Add(source);
    }

    static string Escape(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    static string EndpointLocation(ApiEndpointId id) => $"/api/endpoints/{Escape(id.Value)}";

    sealed class StagedRoutes(IEndpointRouteBuilder parent) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => parent.ServiceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => parent.CreateApplicationBuilder();
    }
}

/// <summary>Publishes a prepared API implementation through native ASP.NET routing.</summary>
public static class ServiceApiBindingsEndpointExtensions
{
    /// <summary>Validates all staged routes before publishing any of them to this host.</summary>
    /// <param name="routes">Native host or route group.</param><param name="model">Prepared service model.</param>
    /// <returns>The native route builder.</returns>
    /// <exception cref="ServiceMappingException">Mapping is repeated, incomplete or duplicates a route.</exception>
    public static IEndpointRouteBuilder MapServices(this IEndpointRouteBuilder routes, ServiceApiBindings model)
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
    /// <param name="location">JSON-pointer-style location of the failed endpoint, route or mapping state.</param>
    public ServiceMappingException(string code, string message, string location)
        : this(new DocumentValidationResult([new(code, DiagnosticSeverity.Error, message, location)])) { }
    /// <summary>Creates a mapping failure containing every identified endpoint defect.</summary>
    /// <param name="validation">Nonempty error diagnostics from mapping validation.</param>
    public ServiceMappingException(DocumentValidationResult validation) : base(Describe(validation)) => Validation = validation;

    static string Describe(DocumentValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        if (validation.IsValid) throw new ArgumentException("Mapping failure requires error diagnostics.", nameof(validation));
        return string.Join("; ", validation.Diagnostics.Select(item => $"{item.Code} at {item.Location}: {item.Message}"));
    }
    /// <summary>Structured diagnostics, matching the admission failure payload.</summary>
    public DocumentValidationResult Validation { get; }
}
