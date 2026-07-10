using System.Text.Json.Serialization;

namespace Cohesive.Presentation;

/// <summary>
/// Defines a coordinated workspace for editing or inspecting a semantic document through multiple projections.
/// </summary>
/// <param name="Id">Stable workspace identifier.</param>
/// <param name="Name">Human-readable workspace name.</param>
/// <param name="DocumentProfiles">Document profiles supported by this workspace.</param>
/// <param name="DefaultDocumentProfileId">Optional default document profile identifier.</param>
/// <param name="SurfaceSlots">Workspace-level surface slots projected by page-host interpreters.</param>
/// <param name="Actions">Workspace-level action placements common to every document profile.</param>
/// <param name="Annotations">Open annotations for workspace-level extension data.</param>
public sealed record WorkspaceDefinition(
    string Id,
    string Name,
    DocumentProfileDefinition[] DocumentProfiles,
    string? DefaultDocumentProfileId,
    DocumentWorkspaceSurfaceSlotDefinition[] SurfaceSlots,
    ActionPlacementDefinition[] Actions,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares a workspace-level surface slot and, when applicable, the route page region that supplies its hosted view.
/// </summary>
/// <param name="Id">Stable slot identifier consumed by page-host and component-system interpreters.</param>
/// <param name="Role">Semantic role that selects the default slot renderer interpretation.</param>
/// <param name="RegionKind">Optional page region kind used to locate the slot's hosted view.</param>
/// <param name="RequiresHostedView">Whether the slot must resolve to a hosted view on the active route page.</param>
/// <param name="Renderer">Optional target binding that selects the frontend renderer for this slot.</param>
/// <param name="Annotations">Open annotations for slot-level extension data.</param>
public sealed record DocumentWorkspaceSurfaceSlotDefinition(
    string Id,
    DocumentWorkspaceSurfaceSlotRole Role,
    ViewRegionKind? RegionKind,
    bool RequiresHostedView,
    PresentationBindingDefinition? Renderer,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies standard document workspace surface slots by semantic responsibility.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentWorkspaceSurfaceSlotRole
{
    /// <summary>Represents the header option.</summary>
    Header = 0,
    /// <summary>Represents the primary surface option.</summary>
    PrimarySurface = 1,
    /// <summary>Represents the auxiliary option.</summary>
    Auxiliary = 2,
    /// <summary>Represents the custom option.</summary>
    Custom = 3
}

/// <summary>
/// Defines one document type/profile hosted by a workspace.
/// </summary>
/// <param name="Id">Stable document profile identifier.</param>
/// <param name="Name">Human-readable profile name.</param>
/// <param name="Document">Root semantic document edited by the profile.</param>
/// <param name="DataSources">Role-based data sources required or produced by this document profile.</param>
/// <param name="Projections">Available projections over this document type.</param>
/// <param name="Layout">Docking, tab, and split-pane layout semantics for this document type.</param>
/// <param name="SharedState">Shared cross-projection interaction state for this document type.</param>
/// <param name="Coordination">Cross-projection coordination rules for this document type.</param>
/// <param name="MetricSources">Profile-local bindings from document metric fields to summary data-source values.</param>
/// <param name="ActionStatusNotices">Profile-local action status notices projected into document workspace chrome.</param>
/// <param name="ProcessTaskSelectors">Profile-local selectors for process tasks related to this document type.</param>
/// <param name="ProcessTaskNotices">Profile-local task notices projected into document workspace chrome.</param>
/// <param name="ActionRuntimeProfiles">Profile-local runtime profiles used by frontend adapters to bind action-family state.</param>
/// <param name="Actions">Profile-level action placements.</param>
/// <param name="Annotations">Open annotations for profile-level extension data.</param>
public sealed record DocumentProfileDefinition(
    string Id,
    string Name,
    DocumentSourceDefinition Document,
    DocumentDataSourceDefinition[] DataSources,
    ProjectionDefinition[] Projections,
    WorkspaceLayoutDefinition Layout,
    InteractionStateDefinition SharedState,
    CoordinationDefinition[] Coordination,
    DocumentMetricSourceDefinition[] MetricSources,
    DocumentActionStatusNoticeDefinition[] ActionStatusNotices,
    ProcessTaskSelectorDefinition[] ProcessTaskSelectors,
    DocumentProcessTaskNoticeDefinition[] ProcessTaskNotices,
    DocumentActionRuntimeProfileDefinition[] ActionRuntimeProfiles,
    ActionPlacementDefinition[] Actions,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares document-profile-local runtime binding metadata for an action family.
/// </summary>
/// <param name="Id">Stable runtime profile identifier.</param>
/// <param name="Name">Human-readable runtime profile name.</param>
/// <param name="ActionId">Primary action whose runtime this profile configures.</param>
/// <param name="FlowId">Optional flow identifier associated with the action runtime.</param>
/// <param name="DiagnosticsKeyPrefix">Diagnostics key prefix used by frontend projection diagnostics.</param>
/// <param name="ProcessInputStateKeySuffix">Local state key suffix for process/input-form state.</param>
/// <param name="Source">Runtime source label used in diagnostics and TODO reporting.</param>
/// <param name="Annotations">Open annotations for runtime-profile extension data.</param>
public sealed record DocumentActionRuntimeProfileDefinition(
    string Id,
    string Name,
    string ActionId,
    string? FlowId,
    string DiagnosticsKeyPrefix,
    string ProcessInputStateKeySuffix,
    string Source,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares how a document profile supplies a metric field from one of its data sources.
/// </summary>
/// <param name="FieldId">Presentation field rendered as a document metric.</param>
/// <param name="Source">Data source and field path that produce the metric value.</param>
public sealed record DocumentMetricSourceDefinition(
    string FieldId,
    DataSourceRefDefinition Source
);

/// <summary>
/// Declares an action-runtime status notice projected for a document profile.
/// </summary>
/// <param name="Id">Stable notice identifier.</param>
/// <param name="Name">Human-readable notice name.</param>
/// <param name="ActionId">Action whose runtime status supplies the notice.</param>
/// <param name="Kind">Action status condition that causes the notice to render.</param>
/// <param name="Region">Workspace chrome region where the notice should render.</param>
/// <param name="Content">Optional content rendered by the action status notice.</param>
/// <param name="Annotations">Open annotations for notice-level extension data.</param>
public sealed record DocumentActionStatusNoticeDefinition(
    string Id,
    string Name,
    string ActionId,
    DocumentActionStatusNoticeKind Kind,
    string Region,
    PresentationContentDefinition? Content,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies action-runtime status notices projected by a document profile.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentActionStatusNoticeKind
{
    /// <summary>Represents the error option.</summary>
    Error = 0
}

/// <summary>
/// Selects process tasks related to a document profile, action, or runtime context.
/// </summary>
/// <param name="Id">Stable selector identifier.</param>
/// <param name="Name">Human-readable selector name.</param>
/// <param name="ProcessType">Optional process type that selected tasks must match.</param>
/// <param name="ActiveOnly">Whether terminal process tasks should be excluded.</param>
/// <param name="Matches">Field/path predicates that selected tasks must satisfy.</param>
/// <param name="ActionId">Optional action identifier that this selector supports or constrains.</param>
/// <param name="Annotations">Open annotations for selector-level extension data.</param>
public sealed record ProcessTaskSelectorDefinition(
    string Id,
    string Name,
    string? ProcessType,
    bool ActiveOnly,
    ProcessTaskSelectorMatchDefinition[] Matches,
    string? ActionId,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares one predicate in a process task selector.
/// </summary>
/// <param name="TaskPath">Path on the normalized process task, such as metadata.ediSpecId.</param>
/// <param name="ValuePath">Path on the runtime document context, such as resourceId.</param>
public sealed record ProcessTaskSelectorMatchDefinition(
    string TaskPath,
    string ValuePath
);

/// <summary>
/// Declares a process-task notice projected for a document profile.
/// </summary>
/// <param name="Id">Stable notice identifier.</param>
/// <param name="Name">Human-readable notice name.</param>
/// <param name="ProcessTaskSelectorId">Profile process-task selector that supplies the notice task.</param>
/// <param name="Region">Workspace chrome region where the notice should render.</param>
/// <param name="Content">Text content rendered by the process-task notice.</param>
/// <param name="Actions">Actions rendered with the process-task notice.</param>
/// <param name="Annotations">Open annotations for notice-level extension data.</param>
/// <param name="StatusFieldId">Optional presentation field whose display semantics describe task status values.</param>
public sealed record DocumentProcessTaskNoticeDefinition(
    string Id,
    string Name,
    string ProcessTaskSelectorId,
    string Region,
    PresentationContentDefinition? Content,
    DocumentProcessTaskNoticeActionDefinition[] Actions,
    PresentationAnnotationDefinition[] Annotations,
    string? StatusFieldId = null
);

/// <summary>
/// Declares an action rendered with a process-task notice.
/// </summary>
/// <param name="Placement">Action placement metadata used to render the action control.</param>
/// <param name="TargetPreference">Ordered process-task link targets used to resolve the action href.</param>
/// <param name="Annotations">Open annotations for notice action extension data.</param>
public sealed record DocumentProcessTaskNoticeActionDefinition(
    ActionPlacementDefinition Placement,
    DocumentProcessTaskNoticeActionTargetKind[] TargetPreference,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies process-task links that can be targeted by a notice action.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentProcessTaskNoticeActionTargetKind
{
    /// <summary>Represents the details option.</summary>
    Details = 0,
    /// <summary>Represents the target option.</summary>
    Target = 1,
    /// <summary>Represents the source option.</summary>
    Source = 2
}

/// <summary>
/// Defines the root semantic document edited or inspected by a workspace.
/// </summary>
/// <param name="Id">Stable document identifier.</param>
/// <param name="Kind">Semantic document kind.</param>
/// <param name="RootShape">Root semantic shape or contract.</param>
/// <param name="DataSource">Data source that supplies the document.</param>
/// <param name="Address">Document-level resource address used by editors, diagnostics, and projection tooling.</param>
/// <param name="Identity">Semantic identity model for nodes in the document.</param>
public sealed record DocumentSourceDefinition(
    string Id,
    DocumentKind Kind,
    ShapeRefDefinition RootShape,
    DataSourceRefDefinition DataSource,
    DocumentAddressDefinition Address,
    DocumentIdentityDefinition Identity
);

/// <summary>
/// Defines a data source used by a document profile with an explicit semantic role.
/// </summary>
/// <param name="Id">Stable profile-local data-source binding identifier.</param>
/// <param name="Role">Semantic role of this data source within the document profile.</param>
/// <param name="DataSource">Referenced presentation data source and optional field path.</param>
/// <param name="IsRequired">Whether this data source is required before the document workspace can render.</param>
/// <param name="Description">Optional human-readable role description.</param>
/// <param name="Activation">Optional policy describing when this data source should be active in the running workspace.</param>
public sealed record DocumentDataSourceDefinition(
    string Id,
    DocumentDataSourceRole Role,
    DataSourceRefDefinition DataSource,
    bool IsRequired,
    string? Description,
    DocumentDataSourceActivationPolicyDefinition? Activation = null
);

/// <summary>
/// Declares when a document data source should be bound for a workspace instance.
/// </summary>
/// <param name="RequiredRouteParameterNames">Route parameters that must have values before activation.</param>
/// <param name="ProjectionIds">Projection ids that activate this data source when active.</param>
/// <param name="ViewIds">View ids that activate this data source when active.</param>
/// <param name="LayoutModeIds">Workspace layout mode ids that activate this data source when active.</param>
public sealed record DocumentDataSourceActivationPolicyDefinition(
    string[] RequiredRouteParameterNames,
    string[] ProjectionIds,
    string[] ViewIds,
    string[] LayoutModeIds
);

/// <summary>
/// Classifies how a document profile uses a data source.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentDataSourceRole
{
    /// <summary>Represents the resource option.</summary>
    Resource = 0,
    /// <summary>Represents the metadata option.</summary>
    Metadata = 1,
    /// <summary>Represents the summary option.</summary>
    Summary = 2,
    /// <summary>Represents the working document option.</summary>
    WorkingDocument = 3,
    /// <summary>Represents the validation option.</summary>
    Validation = 4,
    /// <summary>Represents the process task option.</summary>
    ProcessTask = 5
}

/// <summary>
/// Classifies semantic documents that can be presented in workspaces.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentKind
{
    /// <summary>Represents the edi spec option.</summary>
    EdiSpec = 0,
    /// <summary>Represents the shape graph option.</summary>
    ShapeGraph = 1,
    /// <summary>Represents the json document option.</summary>
    JsonDocument = 2,
    /// <summary>Represents the ontology option.</summary>
    Ontology = 3,
    /// <summary>Represents the transition graph option.</summary>
    TransitionGraph = 4,
    /// <summary>Represents the presentation ir option.</summary>
    PresentationIr = 5,
    /// <summary>Represents the training example option.</summary>
    TrainingExample = 6,
    /// <summary>Represents the fix spec option.</summary>
    FixSpec = 7
}

/// <summary>
/// References a presentation data source.
/// </summary>
/// <param name="DataSourceId">Data-source identifier.</param>
/// <param name="FieldPath">Optional path from the data-source result to the edited document.</param>
public sealed record DataSourceRefDefinition(
    string DataSourceId,
    string? FieldPath = null
);

/// <summary>
/// Defines semantic identity rules for document nodes.
/// </summary>
/// <param name="DocumentIdField">Field or path that identifies the document resource.</param>
/// <param name="VersionField">Optional field or path containing the document version.</param>
/// <param name="SemanticKeyFields">Fields or paths used to identify semantic child nodes.</param>
public sealed record DocumentIdentityDefinition(
    string DocumentIdField,
    string? VersionField,
    string[] SemanticKeyFields
);

/// <summary>
/// Defines one projection over a semantic document, such as JSON text, a tree, a type browser, or a graph.
/// </summary>
/// <param name="Id">Stable projection identifier.</param>
/// <param name="Name">Human-readable projection name.</param>
/// <param name="Kind">Projection kind.</param>
/// <param name="Subject">Semantic subject projected by this view.</param>
/// <param name="Coordinates">Projection-local coordinate system and semantic mapping contract.</param>
/// <param name="ViewId">Optional presentation view backing this projection.</param>
/// <param name="RendererKey">Optional target renderer key.</param>
/// <param name="Actions">Projection-local action placements.</param>
/// <param name="Capabilities">Projection capabilities.</param>
/// <param name="Annotations">Open annotations for projection-level extension data.</param>
public sealed record ProjectionDefinition(
    string Id,
    string Name,
    ProjectionKind Kind,
    ProjectionSubjectDefinition Subject,
    ProjectionCoordinateSystemDefinition Coordinates,
    string? ViewId,
    string? RendererKey,
    ActionPlacementDefinition[] Actions,
    ProjectionCapabilitiesDefinition Capabilities,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies document projections.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectionKind
{
    /// <summary>Represents the json text option.</summary>
    JsonText = 0,
    /// <summary>Represents the tree view option.</summary>
    TreeView = 1,
    /// <summary>Represents the type tree option.</summary>
    TypeTree = 2,
    /// <summary>Represents the graph view option.</summary>
    GraphView = 3,
    /// <summary>Represents the form view option.</summary>
    FormView = 4,
    /// <summary>Represents the segment view option.</summary>
    SegmentView = 5,
    /// <summary>Represents the validation view option.</summary>
    ValidationView = 6,
    /// <summary>Represents the table view option.</summary>
    TableView = 7
}

/// <summary>
/// Defines the semantic subject of a projection.
/// </summary>
/// <param name="Kind">Subject kind.</param>
/// <param name="Reference">Optional canonical semantic reference.</param>
/// <param name="Path">Optional canonical semantic path.</param>
public sealed record ProjectionSubjectDefinition(
    ProjectionSubjectKind Kind,
    SemanticReference? Reference,
    SemanticPath? Path
);

/// <summary>
/// Classifies projection subjects.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectionSubjectKind
{
    /// <summary>Represents the document option.</summary>
    Document = 0,
    /// <summary>Represents the structure option.</summary>
    Structure = 1,
    /// <summary>Represents the shape graph option.</summary>
    ShapeGraph = 2,
    /// <summary>Represents the shape option.</summary>
    Shape = 3,
    /// <summary>Represents the type system option.</summary>
    TypeSystem = 4,
    /// <summary>Represents the segment catalog option.</summary>
    SegmentCatalog = 5,
    /// <summary>Represents the diagnostics option.</summary>
    Diagnostics = 6
}

/// <summary>
/// Defines how a projection names local coordinates and maps them to semantic references.
/// </summary>
/// <param name="Kind">Local coordinate kind.</param>
/// <param name="SemanticReferenceKind">Canonical semantic reference kind produced by this coordinate system.</param>
/// <param name="MappingStrategy">Target-independent mapping strategy label.</param>
/// <param name="Mappings">Optional declared mapping hooks between local and semantic coordinates.</param>
public sealed record ProjectionCoordinateSystemDefinition(
    ProjectionCoordinateKind Kind,
    SemanticReferenceKind SemanticReferenceKind,
    string MappingStrategy,
    ProjectionMappingDefinition[] Mappings
);

/// <summary>
/// Classifies projection-local coordinate systems.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectionCoordinateKind
{
    /// <summary>Represents the json pointer option.</summary>
    JsonPointer = 0,
    /// <summary>Represents the text range option.</summary>
    TextRange = 1,
    /// <summary>Represents the tree node id option.</summary>
    TreeNodeId = 2,
    /// <summary>Represents the graph node id option.</summary>
    GraphNodeId = 3,
    /// <summary>Represents the type id option.</summary>
    TypeId = 4,
    /// <summary>Represents the field binding option.</summary>
    FieldBinding = 5,
    /// <summary>Represents the diagnostic id option.</summary>
    DiagnosticId = 6
}

/// <summary>
/// Defines a mapping hook between projection-local coordinates and semantic references.
/// </summary>
/// <param name="Direction">Mapping direction.</param>
/// <param name="ActionId">Optional action used to resolve the mapping.</param>
/// <param name="ExpressionId">Optional expression used to resolve the mapping.</param>
/// <param name="Strategy">Mapping strategy label.</param>
public sealed record ProjectionMappingDefinition(
    ProjectionMappingDirection Direction,
    string? ActionId,
    string? ExpressionId,
    string? Strategy
);

/// <summary>
/// Classifies coordinate mapping direction.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectionMappingDirection
{
    /// <summary>Represents the local to semantic option.</summary>
    LocalToSemantic = 0,
    /// <summary>Represents the semantic to local option.</summary>
    SemanticToLocal = 1,
    /// <summary>Represents the bidirectional option.</summary>
    Bidirectional = 2
}

/// <summary>
/// Defines interaction capabilities for a projection.
/// </summary>
public sealed record ProjectionCapabilitiesDefinition(
    bool CanRead,
    bool CanEdit,
    bool CanSelect,
    bool CanReveal,
    bool CanHighlight,
    bool CanSearch,
    bool CanValidate,
    bool CanFormat
);

/// <summary>
/// Canonical semantic reference used as the synchronization substrate across projections.
/// </summary>
/// <param name="Kind">Semantic reference kind.</param>
/// <param name="Id">Stable semantic identifier.</param>
/// <param name="Path">Canonical semantic path.</param>
public sealed record SemanticReference(
    SemanticReferenceKind Kind,
    string Id,
    SemanticPath Path
);

/// <summary>
/// Classifies canonical semantic references.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SemanticReferenceKind
{
    /// <summary>Represents the document option.</summary>
    Document = 0,
    /// <summary>Represents the json pointer option.</summary>
    JsonPointer = 1,
    /// <summary>Represents the edi segment option.</summary>
    EdiSegment = 2,
    /// <summary>Represents the edi loop option.</summary>
    EdiLoop = 3,
    /// <summary>Represents the edi element option.</summary>
    EdiElement = 4,
    /// <summary>Represents the shape graph option.</summary>
    ShapeGraph = 5,
    /// <summary>Represents the shape option.</summary>
    Shape = 6,
    /// <summary>Represents the shape field option.</summary>
    ShapeField = 7,
    /// <summary>Represents the type option.</summary>
    Type = 8,
    /// <summary>Represents the type field option.</summary>
    TypeField = 9,
    /// <summary>Represents the diagnostic option.</summary>
    Diagnostic = 10
}

/// <summary>
/// Defines a path from a semantic document root to a nested semantic target.
/// </summary>
/// <param name="Segments">Ordered semantic path segments.</param>
public sealed record SemanticPath(
    SemanticPathSegment[] Segments
);

/// <summary>
/// Defines one segment in a semantic path.
/// </summary>
/// <param name="Kind">Segment kind.</param>
/// <param name="Id">Segment identifier.</param>
/// <param name="Label">Optional display label.</param>
public sealed record SemanticPathSegment(
    SemanticReferenceKind Kind,
    string Id,
    string? Label = null
);

/// <summary>
/// Defines a semantic selection shared by coordinated projections.
/// </summary>
/// <param name="Target">Selected semantic reference.</param>
/// <param name="SourceProjectionId">Projection that originated the selection, if any.</param>
/// <param name="SemanticPath">Canonical path to the selected semantic target.</param>
public sealed record SemanticSelection(
    SemanticReference Target,
    string? SourceProjectionId,
    SemanticPath SemanticPath
);

/// <summary>
/// Defines shared interaction state for a workspace.
/// </summary>
public sealed record InteractionStateDefinition(
    SelectionStateDefinition Selection,
    CursorStateDefinition Cursor,
    ExpandedStateDefinition[] Expanded,
    ValidationMarkerDefinition[] Validation,
    SearchStateDefinition[] Search,
    FocusStateDefinition[] Focus
);

/// <summary>
/// Defines canonical semantic selection state.
/// </summary>
/// <param name="Selected">Selected semantic references.</param>
/// <param name="SourceProjectionId">Projection that last originated the selection.</param>
public sealed record SelectionStateDefinition(
    SemanticSelection[] Selected,
    string? SourceProjectionId
);

/// <summary>
/// Defines shared cursor state.
/// </summary>
/// <param name="ProjectionId">Projection that owns the cursor coordinate.</param>
/// <param name="Coordinate">Projection-local cursor coordinate.</param>
/// <param name="SemanticReference">Semantic reference under the cursor, if resolved.</param>
public sealed record CursorStateDefinition(
    string? ProjectionId,
    ProjectionCoordinateDefinition? Coordinate,
    SemanticReference? SemanticReference
);

/// <summary>
/// Defines projection-local coordinate data.
/// </summary>
/// <param name="Kind">Coordinate kind.</param>
/// <param name="Id">Optional coordinate identifier.</param>
/// <param name="Path">Optional coordinate path.</param>
/// <param name="Range">Optional text range.</param>
public sealed record ProjectionCoordinateDefinition(
    ProjectionCoordinateKind Kind,
    string? Id,
    string? Path,
    TextRangeDefinition? Range
);

/// <summary>
/// Defines a text range in a text projection.
/// </summary>
public sealed record TextRangeDefinition(
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn
);

/// <summary>
/// Defines expanded-node state for a projection.
/// </summary>
public sealed record ExpandedStateDefinition(
    string ProjectionId,
    string[] ExpandedCoordinateIds,
    bool AutoExpandRoot,
    bool AutoExpandSingleChildChains
);

/// <summary>
/// Defines a validation marker associated with semantic and projection coordinates.
/// </summary>
public sealed record ValidationMarkerDefinition(
    string Id,
    ValidationMarkerSeverity Severity,
    string Message,
    SemanticReference? SemanticReference,
    ProjectionCoordinateDefinition[] Coordinates
);

/// <summary>
/// Classifies validation marker severity.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationMarkerSeverity
{
    /// <summary>Represents the info option.</summary>
    Info = 0,
    /// <summary>Represents the warning option.</summary>
    Warning = 1,
    /// <summary>Represents the error option.</summary>
    Error = 2
}

/// <summary>
/// Defines shared search state for a projection or workspace.
/// </summary>
public sealed record SearchStateDefinition(
    string Id,
    string? ProjectionId,
    string Query,
    SemanticReference[] Results,
    int ActiveResultIndex
);

/// <summary>
/// Defines focus state for a projection.
/// </summary>
public sealed record FocusStateDefinition(
    string ProjectionId,
    ProjectionCoordinateDefinition? Coordinate,
    SemanticReference? SemanticReference
);

/// <summary>
/// Defines a cross-projection coordination rule.
/// </summary>
public sealed record CoordinationDefinition(
    string Id,
    string Name,
    CoordinationTriggerDefinition Trigger,
    CoordinationActionDefinition Action,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Defines what starts a coordination rule.
/// </summary>
public sealed record CoordinationTriggerDefinition(
    CoordinationTriggerKind Kind,
    string? SourceProjectionId,
    string? StateId
);

/// <summary>
/// Classifies coordination triggers.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoordinationTriggerKind
{
    /// <summary>Represents the selection changed option.</summary>
    SelectionChanged = 0,
    /// <summary>Represents the cursor changed option.</summary>
    CursorChanged = 1,
    /// <summary>Represents the expanded changed option.</summary>
    ExpandedChanged = 2,
    /// <summary>Represents the validation changed option.</summary>
    ValidationChanged = 3,
    /// <summary>Represents the search changed option.</summary>
    SearchChanged = 4,
    /// <summary>Represents the focus changed option.</summary>
    FocusChanged = 5
}

/// <summary>
/// Defines the action performed by a coordination rule.
/// </summary>
public sealed record CoordinationActionDefinition(
    CoordinationActionKind Kind,
    string[] TargetProjectionIds,
    string? StateId,
    string? ActionId,
    string? ExpressionId
);

/// <summary>
/// Classifies coordination actions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CoordinationActionKind
{
    /// <summary>Represents the set semantic selection option.</summary>
    SetSemanticSelection = 0,
    /// <summary>Represents the reveal semantic selection option.</summary>
    RevealSemanticSelection = 1,
    /// <summary>Represents the highlight semantic selection option.</summary>
    HighlightSemanticSelection = 2,
    /// <summary>Represents the sync cursor option.</summary>
    SyncCursor = 3,
    /// <summary>Represents the sync expanded option.</summary>
    SyncExpanded = 4,
    /// <summary>Represents the sync validation markers option.</summary>
    SyncValidationMarkers = 5,
    /// <summary>Represents the sync search results option.</summary>
    SyncSearchResults = 6,
    /// <summary>Represents the set focus option.</summary>
    SetFocus = 7
}

/// <summary>
/// Defines the layout modes available in a workspace.
/// </summary>
public sealed record WorkspaceLayoutDefinition(
    string DefaultModeId,
    WorkspaceLayoutModeDefinition[] Modes
);

/// <summary>
/// Defines one workspace layout mode.
/// </summary>
public sealed record WorkspaceLayoutModeDefinition(
    string Id,
    string Name,
    LayoutNodeDefinition Root
);

/// <summary>
/// Defines a docking, tab, split, panel, or projection layout node.
/// </summary>
public sealed record LayoutNodeDefinition(
    string Id,
    LayoutNodeKind Kind,
    LayoutOrientation Orientation,
    string[] ProjectionIds,
    string[] ViewIds,
    LayoutNodeDefinition[] Children,
    double? Size,
    string? Placement
);

/// <summary>
/// Classifies workspace layout nodes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayoutNodeKind
{
    /// <summary>Represents the projection option.</summary>
    Projection = 0,
    /// <summary>Represents the view option.</summary>
    View = 1,
    /// <summary>Represents the tab group option.</summary>
    TabGroup = 2,
    /// <summary>Represents the split group option.</summary>
    SplitGroup = 3,
    /// <summary>Represents the dock region option.</summary>
    DockRegion = 4,
    /// <summary>Represents the floating panel option.</summary>
    FloatingPanel = 5,
    /// <summary>Represents the inspector panel option.</summary>
    InspectorPanel = 6,
    /// <summary>Represents the tool window option.</summary>
    ToolWindow = 7
}

/// <summary>
/// Classifies layout orientation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayoutOrientation
{
    /// <summary>Represents the absence of a selected option.</summary>
    None = 0,
    /// <summary>Represents the horizontal option.</summary>
    Horizontal = 1,
    /// <summary>Represents the vertical option.</summary>
    Vertical = 2
}
