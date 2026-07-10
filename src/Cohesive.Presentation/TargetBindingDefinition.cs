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
    /// <summary>Represents the react option.</summary>
    React = 0,
    /// <summary>Represents the blazor option.</summary>
    Blazor = 1,
    /// <summary>Represents the blazor server option.</summary>
    BlazorServer = 2,
    /// <summary>Represents the blazor web assembly option.</summary>
    BlazorWebAssembly = 3,
    /// <summary>Represents the blazor hybrid option.</summary>
    BlazorHybrid = 4,
    /// <summary>Represents the html option.</summary>
    Html = 5,
    /// <summary>Represents the native option.</summary>
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
    /// <summary>Represents the api endpoint option.</summary>
    ApiEndpoint = 0,
    /// <summary>Represents the component option.</summary>
    Component = 1,
    /// <summary>Represents the view component option.</summary>
    ViewComponent = 2,
    /// <summary>Represents the action endpoint option.</summary>
    ActionEndpoint = 3,
    /// <summary>Represents the navigation route option.</summary>
    NavigationRoute = 4,
    /// <summary>Represents the flow event option.</summary>
    FlowEvent = 5,
    /// <summary>Represents the local state option.</summary>
    LocalState = 6,
    /// <summary>Represents the transition option.</summary>
    Transition = 7,
    /// <summary>Represents the relation query option.</summary>
    RelationQuery = 8,
    /// <summary>Represents the external uri option.</summary>
    ExternalUri = 9,
    /// <summary>Represents the projection renderer option.</summary>
    ProjectionRenderer = 10,
    /// <summary>Represents the workspace runtime option.</summary>
    WorkspaceRuntime = 11,
    /// <summary>Represents the page host component option.</summary>
    PageHostComponent = 12,
    /// <summary>Represents the navigation shell region component option.</summary>
    NavigationShellRegionComponent = 13,
    /// <summary>Represents the icon option.</summary>
    Icon = 14
}
