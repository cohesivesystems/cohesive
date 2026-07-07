using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Presentation;

/// <summary>
/// Defines target binding metadata for a concrete presentation adapter.
/// </summary>
/// <param name="Id">Stable target binding identifier.</param>
/// <param name="Name">Human-readable target binding name.</param>
/// <param name="Target">Concrete presentation target.</param>
/// <param name="ComponentSet">Target component set identifier.</param>
/// <param name="Bindings">Bindings exposed to the target adapter.</param>
/// <param name="Annotations">Open annotations for target-level extension data.</param>
public sealed record TargetBindingDefinition(
    string Id,
    string Name,
    PresentationTargetKind Target,
    string ComponentSet,
    PresentationBindingDefinition[] Bindings,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies a concrete presentation target.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PresentationTargetKind
{
    React = 0,
    Blazor = 1,
    BlazorServer = 2,
    BlazorWebAssembly = 3,
    BlazorHybrid = 4,
    Html = 5,
    Native = 6
}

/// <summary>
/// Defines an adapter binding to a concrete endpoint, component, transition, route, page host, shell region, or local capability.
/// </summary>
/// <param name="Kind">Binding kind.</param>
/// <param name="Id">Optional binding identifier or semantic target identifier.</param>
/// <param name="EndpointId">Optional API endpoint identifier.</param>
/// <param name="RouteId">Optional navigation route identifier.</param>
/// <param name="ComponentRole">Optional semantic component role interpreted by the target adapter.</param>
/// <param name="ComponentKey">Optional concrete component key interpreted by the target adapter.</param>
/// <param name="TransitionId">Optional transition identifier.</param>
/// <param name="DataSourceId">Optional data source identifier.</param>
/// <param name="Options">Optional adapter-specific binding options.</param>
public sealed record PresentationBindingDefinition(
    PresentationBindingKind Kind,
    string? Id = null,
    string? EndpointId = null,
    string? RouteId = null,
    string? ComponentRole = null,
    string? ComponentKey = null,
    string? TransitionId = null,
    string? DataSourceId = null,
    JsonElement? Options = null
);


/// <summary>
/// Classifies adapter binding semantics.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PresentationBindingKind
{
    ApiEndpoint = 0,
    Component = 1,
    ViewComponent = 2,
    ActionEndpoint = 3,
    NavigationRoute = 4,
    FlowEvent = 5,
    LocalState = 6,
    Transition = 7,
    RelationQuery = 8,
    ExternalUri = 9,
    ProjectionRenderer = 10,
    WorkspaceRuntime = 11,
    PageHostComponent = 12,
    NavigationShellRegionComponent = 13,
    Icon = 14
}
