namespace Cohesive.Presentation;

/// <summary>
/// Defines an application navigation graph independently of a concrete frontend router.
/// </summary>
/// <param name="Id">Stable identifier for the navigation graph.</param>
/// <param name="Label">Human-readable label for the navigation graph.</param>
/// <param name="Nodes">User-facing navigation nodes exposed by the graph.</param>
/// <param name="Edges">Semantic relationships between navigation nodes.</param>
/// <param name="Shell">Application shell that hosts the graph.</param>
/// <param name="Routes">Structural routes addressable by routers and navigation actions.</param>
/// <param name="PageHosts">Mounted page hosts targeted by routes.</param>
/// <param name="Actions">Intentful navigation transitions exposed by the graph.</param>
/// <param name="Contexts">Runtime navigation contexts tracked by the graph.</param>
/// <remarks>
/// A <see cref="NavigationRouteDefinition"/> is a URL address.<br />
/// A <see cref="NavigationNodeDefinition"/> is a semantic place in the navigation graph.<br />
/// A <see cref="PageHostDefinition"/> is a mounted rendering host.<br />
/// An <see cref="NavigationActionDefinition"/> is an intentful transition.<br />
/// <see cref="NavigationContextDefinition"/> is runtime provenance.
/// </remarks>
public sealed record NavigationDefinition(
    string Id,
    string Label,
    NavigationNodeDefinition[] Nodes,
    NavigationEdgeDefinition[] Edges,
    NavigationShellDefinition Shell,
    NavigationRouteDefinition[] Routes,
    PageHostDefinition[] PageHosts,
    NavigationActionDefinition[] Actions,
    NavigationContextDefinition[] Contexts
    );

/// <summary>
/// Classifies a user-facing navigation node.
/// </summary>
public enum NavigationNodeKind
{
    /// <summary>Represents the page option.</summary>
    Page = 0,
    /// <summary>Represents the collection option.</summary>
    Collection = 1,
    /// <summary>Represents the entity detail option.</summary>
    EntityDetail = 2
}

/// <summary>
/// Classifies a semantic connection between navigation nodes.
/// </summary>
public enum NavigationEdgeKind
{
    /// <summary>Represents the primary navigation option.</summary>
    PrimaryNavigation = 0,
    /// <summary>Represents the related entity route option.</summary>
    RelatedEntityRoute = 1,
    /// <summary>Represents the drill down option.</summary>
    DrillDown = 2
}

/// <summary>
/// Classifies a logical route.
/// </summary>
public enum NavigationRouteKind
{
    /// <summary>Represents the page option.</summary>
    Page = 0,
    /// <summary>Represents the entity detail option.</summary>
    EntityDetail = 1
}

/// <summary>
/// Classifies a mounted page host.
/// </summary>
public enum PageHostKind
{
    /// <summary>Represents the workspace option.</summary>
    Workspace = 0,
    /// <summary>Represents the single view option.</summary>
    SingleView = 1,
    /// <summary>Represents the tabbed host option.</summary>
    TabbedHost = 2,
    /// <summary>Represents the split host option.</summary>
    SplitHost = 3,
    /// <summary>Represents the dashboard option.</summary>
    Dashboard = 4,
    /// <summary>Represents the wizard option.</summary>
    Wizard = 5,
    /// <summary>Represents the modal host option.</summary>
    ModalHost = 6,
    /// <summary>Represents the shell host option.</summary>
    ShellHost = 7
}

/// <summary>
/// Classifies a region hosted by a page host.
/// </summary>
public enum PageRegionKind
{
    /// <summary>Represents the content option.</summary>
    Content = 0,
    /// <summary>Represents the header option.</summary>
    Header = 1,
    /// <summary>Represents the toolbar option.</summary>
    Toolbar = 2,
    /// <summary>Represents the sidebar option.</summary>
    Sidebar = 3,
    /// <summary>Represents the footer option.</summary>
    Footer = 4,
    /// <summary>Represents the tab option.</summary>
    Tab = 5,
    /// <summary>Represents the split pane option.</summary>
    SplitPane = 6,
    /// <summary>Represents the modal option.</summary>
    Modal = 7,
    /// <summary>Represents the shell option.</summary>
    Shell = 8,
    /// <summary>Represents the custom option.</summary>
    Custom = 9
}

/// <summary>
/// Classifies an intentful navigation transition.
/// </summary>
public enum NavigationActionKind
{
    /// <summary>Represents the navigate to route option.</summary>
    NavigateToRoute = 0,
    /// <summary>Represents the return to previous context option.</summary>
    ReturnToPreviousContext = 1,
    /// <summary>Represents the open modal option.</summary>
    OpenModal = 2,
    /// <summary>Represents the close modal option.</summary>
    CloseModal = 3,
    /// <summary>Represents the replace route option.</summary>
    ReplaceRoute = 4,
    /// <summary>Represents the open workspace option.</summary>
    OpenWorkspace = 5,
    /// <summary>Represents the open page host option.</summary>
    OpenPageHost = 6
}

/// <summary>
/// Classifies how a navigation action affects the runtime context.
/// </summary>
public enum NavigationContextEffectKind
{
    /// <summary>Represents the push option.</summary>
    Push = 0,
    /// <summary>Represents the replace option.</summary>
    Replace = 1,
    /// <summary>Represents the preserve option.</summary>
    Preserve = 2,
    /// <summary>Represents the clear option.</summary>
    Clear = 3,
    /// <summary>Represents the return option.</summary>
    Return = 4
}

/// <summary>
/// Classifies a runtime navigation context.
/// </summary>
public enum NavigationContextKind
{
    /// <summary>Represents the browser option.</summary>
    Browser = 0,
    /// <summary>Represents the workspace option.</summary>
    Workspace = 1,
    /// <summary>Represents the modal option.</summary>
    Modal = 2,
    /// <summary>Represents the embedded option.</summary>
    Embedded = 3
}

/// <summary>
/// Classifies navigation history storage.
/// </summary>
public enum NavigationHistoryKind
{
    /// <summary>Represents the absence of a selected option.</summary>
    None = 0,
    /// <summary>Represents the browser option.</summary>
    Browser = 1,
    /// <summary>Represents the in memory option.</summary>
    InMemory = 2,
    /// <summary>Represents the persistent option.</summary>
    Persistent = 3
}

/// <summary>
/// Classifies how route changes should be scheduled by a projection target.
/// </summary>
public enum NavigationRouteUpdateMode
{
    /// <summary>
    /// Allows the projection target to schedule route rendering as a transition.
    /// </summary>
    Transition = 0,

    /// <summary>
    /// Requires route rendering to commit synchronously with the history update.
    /// </summary>
    Synchronous = 1
}

/// <summary>
/// Classifies the identity used for mounted route instances.
/// </summary>
public enum NavigationRouteInstanceIdentityKind
{
    /// <summary>
    /// The matched route definition identifies the mounted route instance.
    /// </summary>
    MatchedRoute = 0,

    /// <summary>
    /// The concrete path identifies the mounted route instance.
    /// </summary>
    Path = 1,

    /// <summary>
    /// The concrete path and query string identify the mounted route instance.
    /// </summary>
    PathAndSearch = 2,

    /// <summary>
    /// The concrete path, query string, and hash identify the mounted route instance.
    /// </summary>
    FullLocation = 3
}

/// <summary>
/// Classifies an application shell.
/// </summary>
public enum NavigationShellKind
{
    /// <summary>Represents the top navigation option.</summary>
    TopNavigation = 0,
    /// <summary>Represents the side navigation option.</summary>
    SideNavigation = 1,
    /// <summary>Represents the bottom navigation option.</summary>
    BottomNavigation = 2,
    /// <summary>Represents the command navigation option.</summary>
    CommandNavigation = 3
}

/// <summary>
/// Classifies a persistent shell region or slot.
/// </summary>
public enum NavigationShellRegionKind
{
    /// <summary>Represents the process task drawer option.</summary>
    ProcessTaskDrawer = 0,
    /// <summary>Represents the toolbar actions option.</summary>
    ToolbarActions = 1,
    /// <summary>Represents the user menu option.</summary>
    UserMenu = 2,
    /// <summary>Represents the notifications option.</summary>
    Notifications = 3,
    /// <summary>Represents the authentication prompt option.</summary>
    AuthenticationPrompt = 4
}

/// <summary>
/// Defines a user-facing navigation node, such as a top-level page, related entity route, or detail route.
/// </summary>
/// <param name="Id">Stable identifier for the navigation node.</param>
/// <param name="Label">Human-readable label displayed for the node.</param>
/// <param name="Kind">Semantic classification of the node.</param>
/// <param name="RouteId">Identifier of the structural route associated with the node.</param>
/// <param name="IsPrimary">Whether the node participates in the shell's primary navigation.</param>
/// <param name="ActiveRouteIds">Additional route identifiers that should activate this node.</param>
/// <param name="Icon">Optional icon key for frontend projection targets.</param>
/// <param name="ActionId">Optional default navigation action invoked by the node.</param>
/// <remarks>A navigation node is a semantic navigation surface. It answers "What place does this represent in the navigation graph?"</remarks>
public sealed record NavigationNodeDefinition(
    string Id,
    string Label,
    NavigationNodeKind Kind,
    string RouteId,
    bool IsPrimary,
    string[] ActiveRouteIds,
    string? Icon = null,
    string? ActionId = null
    );

/// <summary>
/// Defines a semantic connection between navigation nodes.
/// </summary>
/// <param name="Id">Stable identifier for the navigation edge.</param>
/// <param name="FromNodeId">Identifier of the source navigation node.</param>
/// <param name="ToNodeId">Identifier of the target navigation node.</param>
/// <param name="Kind">Semantic classification of the edge.</param>
/// <param name="Label">Optional human-readable label for the relationship.</param>
public sealed record NavigationEdgeDefinition(
    string Id,
    string FromNodeId,
    string ToNodeId,
    NavigationEdgeKind Kind,
    string? Label = null
    );

/// <summary>
/// Defines a structural route and its parameter contract.
/// </summary>
/// <param name="Id">Stable identifier for the route.</param>
/// <param name="Label">Human-readable label for the route.</param>
/// <param name="Kind">Semantic classification of the route.</param>
/// <param name="PathTemplate">Route path template, including any named parameters.</param>
/// <param name="PageHostId">Identifier of the mounted page host projected for this route.</param>
/// <param name="Parameters">Parameters accepted by the route template.</param>
/// <remarks>
/// A navigation route is structural addressability.
/// It answers "What URL shape can the app match?"
/// A route is not a page semantically. It is the URL grammar that points to a page host.
/// </remarks>
public sealed record NavigationRouteDefinition(
    string Id,
    string Label,
    NavigationRouteKind Kind,
    string PathTemplate,
    string PageHostId,
    NavigationRouteParameterDefinition[] Parameters
    );

/// <summary>
/// Defines a route parameter accepted by a route template.
/// </summary>
/// <param name="Name">Parameter name used in route binding.</param>
/// <param name="Type">Semantic or platform type expected for the parameter value.</param>
/// <param name="IsRequired">Whether the parameter must be provided to bind the route.</param>
public sealed record NavigationRouteParameterDefinition(
    string Name,
    string Type,
    bool IsRequired
    );

/// <summary>
/// Defines a mounted rendering host targeted by structural routes.
/// </summary>
/// <param name="Id">Stable identifier for the page host.</param>
/// <param name="Kind">Semantic classification of the page host.</param>
/// <param name="Workspace">Optional workspace instantiated by the host.</param>
/// <param name="View">Optional primary view mounted by the host.</param>
/// <param name="Regions">Named regions mounted by the host.</param>
/// <param name="Layout">Layout semantics for host regions.</param>
/// <param name="State">Optional interaction state owned by the host.</param>
/// <param name="Annotations">Open annotations for page-host-level extension data.</param>
public sealed record PageHostDefinition(
    string Id,
    PageHostKind Kind,
    WorkspaceRefDefinition? Workspace,
    ViewRefDefinition? View,
    PageRegionDefinition[] Regions,
    LayoutDefinition Layout,
    InteractionStateDefinition? State,
    PresentationAnnotationDefinition[] Annotations
    );

/// <summary>
/// References a presentation view mounted by a page host.
/// </summary>
/// <param name="ViewId">Identifier of the view to mount.</param>
/// <param name="Annotations">Open annotations for view-reference-level extension data.</param>
public sealed record ViewRefDefinition(
    string ViewId,
    PresentationAnnotationDefinition[] Annotations
    );

/// <summary>
/// Defines a named region within a page host.
/// </summary>
/// <param name="Id">Stable region identifier scoped to the containing page host.</param>
/// <param name="Name">Human-readable region name.</param>
/// <param name="Kind">Semantic classification of the page region.</param>
/// <param name="ViewIds">View identifiers mounted in the region.</param>
/// <param name="PageHostIds">Nested page host identifiers mounted in the region.</param>
/// <param name="ProjectionIds">Projection identifiers mounted in the region.</param>
/// <param name="Placement">Optional placement hint within the containing page host.</param>
/// <param name="Annotations">Open annotations for region-level extension data.</param>
public sealed record PageRegionDefinition(
    string Id,
    string Name,
    PageRegionKind Kind,
    string[] ViewIds,
    string[] PageHostIds,
    string[] ProjectionIds,
    string? Placement,
    PresentationAnnotationDefinition[] Annotations
    );

/// <summary>
/// Defines target-independent layout semantics for a page host.
/// </summary>
/// <param name="DefaultRegionId">Default region receiving primary routed content.</param>
/// <param name="Root">Root layout node for the page host.</param>
public sealed record LayoutDefinition(
    string DefaultRegionId,
    LayoutNodeDefinition Root
    );

/// <summary>
/// Defines an intentful navigation transition independently of structural routing.
/// </summary>
/// <param name="Id">Stable identifier for the navigation action.</param>
/// <param name="Name">Human-readable action name.</param>
/// <param name="Kind">Semantic classification of the navigation transition.</param>
/// <param name="RouteId">Optional structural route targeted by the action.</param>
/// <param name="PageHostId">Optional page host targeted by the action.</param>
/// <param name="SourceNodeId">Optional semantic node where the action originates.</param>
/// <param name="TargetNodeId">Optional semantic node reached by the action.</param>
/// <param name="Parameters">Parameters accepted by the action.</param>
/// <param name="Context">How the action affects runtime navigation context.</param>
/// <param name="Annotations">Open annotations for navigation-action-level extension data.</param>
public sealed record NavigationActionDefinition(
    string Id,
    string Name,
    NavigationActionKind Kind,
    string? RouteId,
    string? PageHostId,
    string? SourceNodeId,
    string? TargetNodeId,
    NavigationRouteParameterDefinition[] Parameters,
    NavigationContextEffectDefinition Context,
    PresentationAnnotationDefinition[] Annotations
    );

/// <summary>
/// Defines the runtime-context effect of a navigation action.
/// </summary>
/// <param name="Kind">Context effect kind.</param>
/// <param name="ContextId">Optional runtime context identifier affected by the action.</param>
/// <param name="CapturesProvenance">Whether the action should capture semantic provenance.</param>
/// <param name="WritesHistory">Whether the action writes a navigation history entry.</param>
public sealed record NavigationContextEffectDefinition(
    NavigationContextEffectKind Kind,
    string? ContextId,
    bool CapturesProvenance,
    bool WritesHistory
    );

/// <summary>
/// Defines a runtime navigation context that tracks provenance, state, and history.
/// </summary>
/// <param name="Id">Stable identifier for the navigation context.</param>
/// <param name="Name">Human-readable context name.</param>
/// <param name="Kind">Semantic classification of the context.</param>
/// <param name="History">History policy used by the context.</param>
/// <param name="State">State entries owned by the context.</param>
/// <param name="Annotations">Open annotations for navigation-context-level extension data.</param>
public sealed record NavigationContextDefinition(
    string Id,
    string Name,
    NavigationContextKind Kind,
    NavigationHistoryDefinition History,
    NavigationStateDefinition[] State,
    PresentationAnnotationDefinition[] Annotations
    );

/// <summary>
/// Defines history behavior for a navigation context.
/// </summary>
/// <param name="Kind">History storage kind.</param>
/// <param name="IntegratesWithBrowserHistory">Whether the context integrates with browser history.</param>
/// <param name="CapturesSemanticContext">Whether history entries capture semantic context in addition to URLs.</param>
/// <param name="MaxEntries">Optional maximum number of entries retained by the context.</param>
/// <param name="Annotations">Open annotations for navigation-history-level extension data.</param>
/// <param name="RouteUpdateMode">How projections should schedule route rendering after history changes.</param>
/// <param name="RouteInstanceIdentity">Identity used to decide when a projected route target should be remounted.</param>
public sealed record NavigationHistoryDefinition(
    NavigationHistoryKind Kind,
    bool IntegratesWithBrowserHistory,
    bool CapturesSemanticContext,
    int? MaxEntries,
    PresentationAnnotationDefinition[] Annotations,
    NavigationRouteUpdateMode RouteUpdateMode = NavigationRouteUpdateMode.Transition,
    NavigationRouteInstanceIdentityKind RouteInstanceIdentity = NavigationRouteInstanceIdentityKind.MatchedRoute
    );

/// <summary>
/// Defines a state value owned by a navigation context.
/// </summary>
/// <param name="Id">Stable state identifier scoped to the navigation context.</param>
/// <param name="Name">Human-readable state name.</param>
/// <param name="Type">Semantic type of the state value.</param>
/// <param name="Residency">Where the state should be held.</param>
/// <param name="DefaultValue">Optional default value encoded as text.</param>
/// <param name="Annotations">Open annotations for navigation-state-level extension data.</param>
public sealed record NavigationStateDefinition(
    string Id,
    string Name,
    string Type,
    ResidencyHint Residency,
    string? DefaultValue,
    PresentationAnnotationDefinition[] Annotations
    );

/// <summary>
/// Defines the application shell that hosts navigation nodes and persistent shell capabilities.
/// </summary>
/// <param name="Id">Stable identifier for the navigation shell.</param>
/// <param name="Kind">Semantic classification of the shell.</param>
/// <param name="PrimaryNodeIds">Navigation node identifiers shown in primary navigation.</param>
/// <param name="Regions">Persistent shell regions exposed by the shell.</param>
/// <param name="Chrome">Optional shell-level chrome such as brand and title.</param>
/// <param name="Slots">Optional semantic shell slots that organize nodes, regions, and routed content.</param>
/// <param name="Design">Optional design intent for the shell frame.</param>
public sealed record NavigationShellDefinition(
    string Id,
    NavigationShellKind Kind,
    string[] PrimaryNodeIds,
    NavigationShellRegionDefinition[] Regions,
    NavigationShellChromeDefinition? Chrome = null,
    NavigationShellSlotDefinition[]? Slots = null,
    DesignIntent? Design = null
    );

/// <summary>
/// Defines a semantic slot within an application shell.
/// </summary>
/// <param name="Id">Stable identifier for the shell slot.</param>
/// <param name="Kind">Semantic classification of the slot.</param>
/// <param name="Placement">Target-independent placement hint used by layout interpreters.</param>
/// <param name="NodeIds">Navigation node identifiers rendered by the slot.</param>
/// <param name="RegionIds">Shell region identifiers rendered by the slot.</param>
/// <param name="Design">Optional design intent for the slot.</param>
/// <param name="Annotations">Open annotations for slot-level extension data.</param>
public sealed record NavigationShellSlotDefinition(
    string Id,
    NavigationShellSlotKind Kind,
    string Placement,
    string[] NodeIds,
    string[] RegionIds,
    DesignIntent? Design,
    PresentationAnnotationDefinition[] Annotations
    );

/// <summary>
/// Classifies semantic slots in an application shell.
/// </summary>
public enum NavigationShellSlotKind
{
    /// <summary>Represents the brand option.</summary>
    Brand = 0,
    /// <summary>Represents the primary navigation option.</summary>
    PrimaryNavigation = 1,
    /// <summary>Represents the utility actions option.</summary>
    UtilityActions = 2,
    /// <summary>Represents the system notices option.</summary>
    SystemNotices = 3,
    /// <summary>Represents the routed content option.</summary>
    RoutedContent = 4,
    /// <summary>Represents the custom option.</summary>
    Custom = 5
}

/// <summary>
/// Declares user-facing chrome for an application shell independently of a concrete frontend layout.
/// </summary>
/// <param name="BrandLabel">Short brand label, usually rendered as a compact mark or badge.</param>
/// <param name="Title">Primary shell title.</param>
/// <param name="Subtitle">Optional secondary shell text.</param>
/// <param name="Icon">Optional semantic icon key for the shell brand.</param>
/// <param name="Annotations">Open annotations for shell-chrome-level extension data.</param>
public sealed record NavigationShellChromeDefinition(
    string? BrandLabel,
    string? Title,
    string? Subtitle,
    string? Icon,
    PresentationAnnotationDefinition[] Annotations
    );

/// <summary>
/// Defines a shell region or slot, such as a toolbar action area or process task drawer.
/// </summary>
/// <param name="Id">Stable identifier for the shell region.</param>
/// <param name="Kind">Semantic classification of the shell region.</param>
/// <param name="Placement">Placement hint within the containing shell.</param>
/// <param name="SlotId">Optional semantic shell slot that hosts this region.</param>
/// <param name="ComponentKey">Optional projection-specific component key used as an escape hatch.</param>
/// <param name="ViewId">Optional presentation view projected into the shell region.</param>
public sealed record NavigationShellRegionDefinition(
    string Id,
    NavigationShellRegionKind Kind,
    string Placement,
    string? SlotId = null,
    string? ComponentKey = null,
    string? ViewId = null
    );
