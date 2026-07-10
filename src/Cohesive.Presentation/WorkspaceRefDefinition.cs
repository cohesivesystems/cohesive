using System.Text.Json.Serialization;
using Cohesive.Model;

namespace Cohesive.Presentation;

/// <summary>
/// Defines how a view instantiates a coordinated workspace.
/// </summary>
/// <remarks>
/// A workspace reference is orchestration metadata for a view. It does not replace the view subject,
/// which remains the semantic object being presented or manipulated.
/// </remarks>
public sealed record WorkspaceRefDefinition
{
    /// <summary>
    /// Creates a workspace reference.
    /// </summary>
    /// <param name="workspaceId"><see cref="WorkspaceDefinition"/> to instantiate.</param>
    /// <param name="documentProfileId">Optional document interpretation/profile.</param>
    /// <param name="layoutProfileId">Optional layout preset/profile.</param>
    /// <param name="initialProjectionIds">Optional initial visible projections.</param>
    /// <param name="instantiation">Workspace instantiation lifetime.</param>
    /// <param name="documentBinding">Expression resolving the document source or binding.</param>
    [JsonConstructor]
    public WorkspaceRefDefinition(
        string workspaceId,
        string? documentProfileId = null,
        string? layoutProfileId = null,
        string[]? initialProjectionIds = null,
        WorkspaceInstantiationMode instantiation = WorkspaceInstantiationMode.Shared,
        Expr? documentBinding = null
        )
    {
        WorkspaceId = workspaceId;
        DocumentProfileId = documentProfileId;
        LayoutProfileId = layoutProfileId;
        InitialProjectionIds = initialProjectionIds ?? [];
        Instantiation = instantiation;
        DocumentBinding = documentBinding;
    }

    /// <summary>
    /// Workspace definition to instantiate.
    /// </summary>
    public string WorkspaceId { get; init; }

    /// <summary>
    /// Optional document interpretation/profile.
    /// </summary>
    public string? DocumentProfileId { get; init; }

    /// <summary>
    /// Optional layout preset/profile.
    /// </summary>
    public string? LayoutProfileId { get; init; }

    /// <summary>
    /// Optional initial visible projections.
    /// </summary>
    public string[] InitialProjectionIds { get; init; }

    /// <summary>
    /// Shared, transient, singleton, or route-scoped workspace instantiation mode.
    /// </summary>
    public WorkspaceInstantiationMode Instantiation { get; init; }

    /// <summary>
    /// Expression resolving the document source or binding.
    /// </summary>
    public Expr? DocumentBinding { get; init; }
}

/// <summary>
/// Classifies how a workspace instance is reused.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceInstantiationMode
{
    /// <summary>Represents the shared option.</summary>
    Shared = 0,
    /// <summary>Represents the route scoped option.</summary>
    RouteScoped = 1,
    /// <summary>Represents the transient option.</summary>
    Transient = 2,
    /// <summary>Represents the singleton option.</summary>
    Singleton = 3
}
