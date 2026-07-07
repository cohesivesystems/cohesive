using System.Text.Json.Serialization;

namespace Cohesive.Presentation;

/// <summary>
/// Defines a logical user-facing unit such as a page, panel, collection, editor, prompt, drawer, or navigation shell.
/// </summary>
/// <param name="Id">Stable view identifier.</param>
/// <param name="Name">Human-readable view name.</param>
/// <param name="Kind">Logical view role.</param>
/// <param name="Subject">Semantic subject presented or manipulated by the view.</param>
/// <param name="DataSourceIds">Identifiers of data sources directly consumed by the view.</param>
/// <param name="Regions">Named child regions hosted by the view.</param>
/// <param name="FieldIds">Identifiers of fields projected by the view.</param>
/// <param name="Actions">Action placements exposed by the view.</param>
/// <param name="Chrome">Optional view chrome such as title, subtitle, actions, badges, switches, and trailing content.</param>
/// <param name="State">Local presentation state owned by the view.</param>
/// <param name="Synchronization">Synchronization contracts between child views.</param>
/// <param name="InteractionStateId">Optional identifier for local interaction state associated with the view.</param>
/// <param name="Accessibility">Target-independent accessibility semantics for the view.</param>
/// <param name="Design">Target-independent design intent for the view.</param>
/// <param name="Annotations">Open annotations for view-level extension data.</param>
/// <param name="Workspace">Optional workspace orchestration/container model instantiated by the view.</param>
/// <param name="Collection">Optional collection/grid semantics used when the view projects row-oriented data.</param>
/// <param name="PromptDocumentPreview">Optional document-preview contract for prompt-hosted preview surfaces.</param>
/// <param name="PromptDismiss">Optional dismiss policy for prompt or modal views.</param>
/// <param name="PromptStatusMessages">Optional prompt-local status and description messages.</param>
public sealed record ViewDefinition(
    string Id,
    string Name,
    ViewKind Kind,
    ViewSubjectDefinition Subject,
    string[] DataSourceIds,
    ViewRegionDefinition[] Regions,
    string[] FieldIds,
    ActionPlacementDefinition[] Actions,
    ViewChromeDefinition? Chrome,
    ViewStateDefinition[] State,
    SelectionSynchronizationDefinition[] Synchronization,
    string? InteractionStateId,
    AccessibilityContract? Accessibility,
    DesignIntent? Design,
    PresentationAnnotationDefinition[] Annotations,
    WorkspaceRefDefinition? Workspace = null,
    CollectionViewDefinition? Collection = null,
    PromptDocumentPreviewDefinition? PromptDocumentPreview = null,
    PromptDismissPolicyDefinition? PromptDismiss = null,
    PromptStatusMessageDefinition[]? PromptStatusMessages = null
);


/// <summary>
/// Declares one prompt-local message resolved from action status or prompt data.
/// </summary>
/// <param name="Id">Stable message identifier scoped to the prompt.</param>
/// <param name="Name">Human-readable message name.</param>
/// <param name="Kind">Condition that activates the message.</param>
/// <param name="Region">Prompt-local region where this message should be projected.</param>
/// <param name="Message">Literal fallback message.</param>
/// <param name="MessageTemplate">Optional template resolved against data-source payloads.</param>
/// <param name="ActionId">Optional action identifier used by action-status message kinds.</param>
/// <param name="DataSourceId">Optional data source used by data-driven message kinds.</param>
/// <param name="FieldPath">Optional field path used by data-driven message kinds.</param>
/// <param name="ExpectedValue">Optional expected formatted field value for equality matching.</param>
/// <param name="Tone">Optional design tone such as info, warning, or danger.</param>
/// <param name="Content">Optional common content rendered by target adapters for the status message.</param>
/// <param name="Annotations">Open annotations for message-level extension data.</param>
public sealed record PromptStatusMessageDefinition(
    string Id,
    string Name,
    PromptStatusMessageKind Kind,
    string Region,
    string Message,
    string? MessageTemplate,
    string? ActionId,
    string? DataSourceId,
    string? FieldPath,
    string? ExpectedValue,
    string? Tone,
    PresentationContentDefinition? Content,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies conditions that activate prompt-local status messages.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PromptStatusMessageKind
{
    ActionPending = 0,
    ActionError = 1,
    DataFieldEquals = 2,
    DataFieldTruthy = 3
}

/// <summary>
/// Defines how a prompt or modal view may be dismissed by target adapters.
/// </summary>
/// <param name="DismissActionId">Action dispatched when the prompt is dismissed.</param>
/// <param name="ShowCloseButton">Whether to render an explicit close affordance.</param>
/// <param name="DismissOnBackdrop">Whether backdrop clicks dismiss the prompt.</param>
/// <param name="DismissOnEscape">Whether Escape dismisses the prompt.</param>
/// <param name="DisableWhenActionPending">Whether dismiss should be blocked while the dismiss action is pending.</param>
/// <param name="Annotations">Open annotations for dismiss-policy extension data.</param>
public sealed record PromptDismissPolicyDefinition(
    string DismissActionId,
    bool ShowCloseButton,
    bool DismissOnBackdrop,
    bool DismissOnEscape,
    bool DisableWhenActionPending,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Defines a prompt-hosted document preview backed by transient presentation state.
/// </summary>
/// <param name="DataSourceId">Data source that publishes the preview state.</param>
/// <param name="PreviewViewId">Root view rendered as the preview chrome/content surface.</param>
/// <param name="WorkspacePageViewId">Page view used to resolve the document workspace runtime.</param>
/// <param name="Workspace">Workspace/profile/projection contract used by the preview.</param>
/// <param name="DocumentInstanceIdTemplate">Template that resolves a stable transient workspace instance id from preview data.</param>
/// <param name="DocumentPathTemplate">Optional display path template for the previewed document.</param>
/// <param name="Title">Title value resolved from preview data.</param>
/// <param name="DocumentText">Document text value resolved from preview data.</param>
/// <param name="IsReadOnly">Whether the projected document preview is read-only.</param>
/// <param name="Badges">Badge values resolved from preview data.</param>
/// <param name="Content">Optional common content rendered by target adapters for the preview title and document text.</param>
/// <param name="Annotations">Open annotations for preview-level extension data.</param>
public sealed record PromptDocumentPreviewDefinition(
    string DataSourceId,
    string PreviewViewId,
    string WorkspacePageViewId,
    WorkspaceRefDefinition Workspace,
    string DocumentInstanceIdTemplate,
    string? DocumentPathTemplate,
    PresentationValueDefinition Title,
    PresentationValueDefinition DocumentText,
    bool IsReadOnly,
    PresentationBadgeDefinition[] Badges,
    PresentationContentDefinition? Content,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Defines a badge or chip rendered by presentation surfaces.
/// </summary>
/// <param name="Id">Stable badge identifier scoped to the containing surface.</param>
/// <param name="Name">Human-readable badge name.</param>
/// <param name="Content">Optional text content resolved from runtime data.</param>
/// <param name="Value">Value resolved from runtime data.</param>
/// <param name="ValueTemplate">Optional template resolved from runtime data after <paramref name="Value"/> is resolved.</param>
/// <param name="FieldId">Optional presentation field used to format the badge value.</param>
/// <param name="Tone">Optional design tone.</param>
/// <param name="OmitWhenEmpty">Whether to hide the badge when the resolved value is empty.</param>
/// <param name="OmitWhenZero">Whether to hide the badge when the resolved value is numeric zero.</param>
/// <param name="Annotations">Open annotations for badge-level extension data.</param>
public sealed record PresentationBadgeDefinition(
    string Id,
    string Name,
    PresentationContentDefinition? Content,
    PresentationValueDefinition? Value,
    string? ValueTemplate,
    string? FieldId,
    string? Tone,
    bool OmitWhenEmpty,
    bool OmitWhenZero,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies the logical role of a view in the presentation graph.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewKind
{
    /// <summary>
    /// The view is a page as a visual surface onto which the rest of the components of a navigation target are rendered.
    /// </summary>
    Page = 0,
    Panel = 1,
    Collection = 2,
    RecordDetail = 3,
    Form = 4,

    /// <summary>
    /// A dashboard view includes summary metrics.
    /// </summary>
    Dashboard = 5,

    Graph = 6,
    Timeline = 7,
    Search = 8,
    Wizard = 9,
    Prompt = 10,
    Modal = 11,
    Drawer = 12,
    NavigationShell = 13,
    CommandSurface = 14,
    Surface = 15,
    DocumentWorkspace = 16,

    /// <summary>
    /// A tabbed surface groups child views into mutually exclusive tab regions.
    /// </summary>
    TabbedSurface = 17
}

/// <summary>
/// Classifies a named region (<see cref="ViewRegionDefinition"/>) within a view (<see cref="ViewDefinition"/>).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewRegionKind
{
    Content = 0,
    Primary = 1,
    Header = 2,
    Toolbar = 3,
    Footer = 4,
    Sidebar = 5,
    Tab = 6,
    List = 7,
    Collection = 8,
    RecordDetail = 9,
    Detail = 10,
    StatusList = 11,
    Form = 12,
    Modal = 13,
    Panel = 14,
    Drawer = 15,
    Inspector = 16,
    SplitPane = 17,
    Surface = 18,
    BadgeStrip = 19,
    MetricStrip = 20,
    ViewSwitch = 21,
    ComponentHost = 22
}

/// <summary>
/// Defines the semantic subject of a view (<see cref="ViewDefinition"/>).
/// </summary>
/// <param name="Kind">Subject classification.</param>
/// <param name="EntityId">Optional entity identifier associated with the subject.</param>
/// <param name="ShapeId">Optional shape identifier associated with the subject.</param>
/// <param name="RouteId">Optional route identifier associated with the subject.</param>
/// <param name="DataSourceId">Optional data source identifier associated with the subject.</param>
/// <param name="InputFormId">Optional input form identifier associated with the subject.</param>
/// <param name="QueryFormId">Optional query form identifier associated with the subject.</param>
/// <param name="FlowId">Optional flow identifier associated with the subject.</param>
/// <param name="NavigationNodeId">Optional navigation node identifier associated with the subject.</param>
public sealed record ViewSubjectDefinition(
    ViewSubjectKind Kind,
    string? EntityId = null,
    string? ShapeId = null,
    string? RouteId = null,
    string? DataSourceId = null,
    string? InputFormId = null,
    string? QueryFormId = null,
    string? FlowId = null,
    string? NavigationNodeId = null
);

/// <summary>
/// Classifies the semantic subject that a view presents or manipulates.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewSubjectKind
{
    Shape = 0,
    Entity = 1,
    RelationProjection = 2,
    TransitionInputForm = 3,
    EffectStream = 4,
    LocalFlowState = 5,
    NavigationNode = 6,
    GeneratedSearchResult = 7,
    DashboardAggregate = 8,
    Route = 9,
    DataSource = 10,
    Flow = 11,
    Processes = 12,
    PromptInput = 13,
    QueryForm = 14
}

/// <summary>
/// Defines a named region within a view (<see cref="ViewDefinition"/>).
/// </summary>
/// <param name="Id">Stable region identifier scoped to the containing view.</param>
/// <param name="Name">Human-readable region name.</param>
/// <param name="Kind">Logical region role.</param>
/// <param name="ViewIds">Child view identifiers hosted by the region.</param>
/// <param name="DataSourceIds">Data source identifiers directly consumed by the region.</param>
/// <param name="Actions">Action placements exposed by the region.</param>
/// <param name="Annotations">Open annotations for region-level extension data.</param>
/// <param name="Icon">Optional semantic icon identifier associated with the region.</param>
public sealed record ViewRegionDefinition(
    string Id,
    string Name,
    ViewRegionKind Kind,
    string[] ViewIds,
    string[] DataSourceIds,
    ActionPlacementDefinition[] Actions,
    PresentationAnnotationDefinition[] Annotations,
    string? Icon = null
);


/// <summary>
/// Classifies a chrome slot within a view.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewChromeSlotKind
{
    Actions = 0,
    BadgeStrip = 1,
    MetricStrip = 2,
    ViewSwitch = 3,
    LayoutSwitch = 4,
    HeadingTrailing = 5,
    Status = 6,
    Custom = 7
}

/// <summary>
/// Classifies the preferred placement of a chrome slot within a view surface.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewChromeSlotPlacement
{
    None = 0,
    Header = 1,
    Toolbar = 2,
    BeforeContent = 3,
    AfterContent = 4,
    Footer = 5
}

/// <summary>
/// Classifies local view state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ViewStateKind
{
    Choice = 0,
    Toggle = 1,
    Value = 2,
    DocumentAddress = 3
}


/// <summary>
/// Defines local presentation state owned by a view (<see cref="ViewDefinition"/>).
/// </summary>
/// <param name="Id">Stable state identifier scoped to the view.</param>
/// <param name="Name">Human-readable state name.</param>
/// <param name="Kind">State classification.</param>
/// <param name="Type">Optional type name for the state value.</param>
/// <param name="DefaultValue">Optional default value encoded as text.</param>
/// <param name="AllowedValues">Allowed values for choice-like state.</param>
/// <param name="Residency">Where the state should be held.</param>
/// <param name="Annotations">Open annotations for state-level extension data.</param>
public sealed record ViewStateDefinition(
    string Id,
    string Name,
    ViewStateKind Kind,
    string? Type,
    string? DefaultValue,
    string[] AllowedValues,
    ResidencyHint Residency,
    PresentationAnnotationDefinition[] Annotations
);


/// <summary>
/// Defines the chrome surrounding a view, such as title, subtitle, toolbar actions, badges, or view switches.
/// </summary>
/// <param name="Title">Optional legacy title value. Prefer <paramref name="Content"/> for new projections.</param>
/// <param name="Subtitle">Optional legacy subtitle value. Prefer <paramref name="Content"/> for new projections.</param>
/// <param name="Collapsible">Whether the view chrome/content can be collapsed by the target.</param>
/// <param name="CollapseStateId">Optional state identifier used to persist collapsed state.</param>
/// <param name="Slots">Named chrome slots projected by target adapters.</param>
/// <param name="Content">Optional common content rendered by target adapters for the view chrome.</param>
public sealed record ViewChromeDefinition(
    PresentationValueDefinition? Title,
    PresentationValueDefinition? Subtitle,
    bool Collapsible,
    string? CollapseStateId,
    ViewChromeSlotDefinition[] Slots,
    PresentationContentDefinition? Content = null
);

/// <summary>
/// Classifies how a presentation value is resolved.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PresentationValueKind
{
    Literal = 0,
    Field = 1,
    Expression = 2,
    State = 3
}


/// <summary>
/// Defines a value used by presentation chrome or policies.
/// </summary>
/// <param name="Kind">How the value is resolved.</param>
/// <param name="Literal">Literal value when <paramref name="Kind"/> is <see cref="PresentationValueKind.Literal"/>.</param>
/// <param name="Field">Field path when <paramref name="Kind"/> is <see cref="PresentationValueKind.Field"/>.</param>
/// <param name="Expression">Expression text when <paramref name="Kind"/> is <see cref="PresentationValueKind.Expression"/>.</param>
/// <param name="StateId">State identifier when <paramref name="Kind"/> is <see cref="PresentationValueKind.State"/>.</param>
public sealed record PresentationValueDefinition(
    PresentationValueKind Kind,
    string? Literal = null,
    string? Field = null,
    string? Expression = null,
    string? StateId = null
);

/// <summary>
/// Defines common text content for presentation surfaces such as notices, prompts, panels, and empty states.
/// </summary>
/// <param name="Title">Optional primary title value resolved against the surface runtime data.</param>
/// <param name="Subtitle">Optional secondary title value resolved against the surface runtime data.</param>
/// <param name="Description">Optional descriptive value resolved against the surface runtime data.</param>
/// <param name="DescriptionTemplate">Optional descriptive template resolved against the surface runtime data.</param>
/// <param name="Annotations">Open annotations for content-level extension data.</param>
public sealed record PresentationContentDefinition(
    PresentationValueDefinition? Title,
    PresentationValueDefinition? Subtitle,
    PresentationValueDefinition? Description,
    string? DescriptionTemplate,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Defines a named chrome slot within a view.
/// </summary>
/// <param name="Id">Stable slot identifier scoped to the containing view.</param>
/// <param name="Name">Human-readable slot name.</param>
/// <param name="Kind">Slot classification.</param>
/// <param name="Placement">Preferred placement for the slot in the projected view surface.</param>
/// <param name="FieldIds">Field identifiers displayed by the slot.</param>
/// <param name="Actions">Action placements displayed by the slot.</param>
/// <param name="ViewIds">Child view identifiers controlled or referenced by the slot.</param>
/// <param name="StateId">Optional state identifier controlled or displayed by the slot.</param>
/// <param name="Value">Optional value displayed by the slot.</param>
/// <param name="Badges">Badges or chips displayed by the slot.</param>
/// <param name="Annotations">Open annotations for slot-level extension data.</param>
public sealed record ViewChromeSlotDefinition(
    string Id,
    string Name,
    ViewChromeSlotKind Kind,
    ViewChromeSlotPlacement Placement,
    string[] FieldIds,
    ActionPlacementDefinition[] Actions,
    string[] ViewIds,
    string? StateId,
    PresentationValueDefinition? Value,
    PresentationBadgeDefinition[] Badges,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Defines a document address space used to correlate selections across document views.
/// </summary>
/// <param name="Kind">Address kind.</param>
/// <param name="Root">Optional root address for scoped address spaces.</param>
public sealed record DocumentAddressDefinition(
    DocumentAddressKind Kind,
    string? Root = null
);


/// <summary>
/// Classifies the address system used to identify parts of a document.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentAddressKind
{
    JsonPointer = 0,
    Uri = 1
}


/// <summary>
/// Defines selection synchronization between views that project the same document.
/// </summary>
/// <param name="Id">Stable synchronization identifier scoped to the view.</param>
/// <param name="Name">Human-readable synchronization name.</param>
/// <param name="Address">Document address space used by the synchronization contract.</param>
/// <param name="StateId">View state identifier that stores the active address.</param>
/// <param name="ParticipantViewIds">Views that consume or emit the active address.</param>
/// <param name="Annotations">Open annotations for synchronization-level extension data.</param>
public sealed record SelectionSynchronizationDefinition(
    string Id,
    string Name,
    DocumentAddressDefinition Address,
    string StateId,
    string[] ParticipantViewIds,
    PresentationAnnotationDefinition[] Annotations
);
