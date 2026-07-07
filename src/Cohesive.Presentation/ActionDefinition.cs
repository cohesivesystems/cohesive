using System.Text.Json.Serialization;

namespace Cohesive.Presentation;

/// <summary>
/// Defines a semantic action, independently of the visual control that invokes it.
/// An <see cref="ActionPlacementDefinition"/> is used to place an action within a view (<see cref="ViewDefinition"/>).
/// </summary>
/// <param name="Id">Stable action identifier.</param>
/// <param name="Name">Human-readable action name.</param>
/// <param name="Kind">Semantic action kind.</param>
/// <param name="Scope">Scope where the action applies.</param>
/// <param name="Binding">Adapter binding used to realize the action.</param>
/// <param name="Parameters">Parameters accepted by the action.</param>
/// <param name="Preparation">Optional action preparation semantics.</param>
/// <param name="Enablement">Criteria that must be satisfied before the action can execute.</param>
/// <param name="Execution">Optional action execution semantics.</param>
/// <param name="EndpointRequests">Optional endpoint request projections used by endpoint-backed interpretations.</param>
/// <param name="Result">Optional action result handling semantics.</param>
/// <param name="Design">Target-independent design intent for the action.</param>
/// <param name="Accessibility">Target-independent accessibility semantics for the action.</param>
/// <param name="Annotations">Open annotations for action-level extension data.</param>
/// <param name="Semantics">Optional first-class action semantics interpreted by target adapters.</param>
/// <param name="RuntimePresentation">Optional runtime presentation labels resolved while the action is executing.</param>
public sealed record ActionDefinition(
    string Id,
    string Name,
    ActionKind Kind,
    ActionScopeKind Scope,
    PresentationBindingDefinition Binding,
    ParameterDefinition[] Parameters,
    ActionPreparation? Preparation,
    ActionEnablementCriterionDefinition[] Enablement,
    ActionExecutionPolicy? Execution,
    ActionEndpointRequestProjectionDefinition[] EndpointRequests,
    ActionResultPolicy? Result,
    DesignIntent? Design,
    AccessibilityContract? Accessibility,
    PresentationAnnotationDefinition[] Annotations,
    ActionSemanticsDefinition? Semantics = null,
    ActionRuntimePresentationDefinition? RuntimePresentation = null
);

/// <summary>
/// Declares adapter-neutral labels and other presentation details for runtime
/// action states such as pending execution.
/// </summary>
/// <param name="PendingLabel">Default label shown while the action is pending.</param>
/// <param name="PendingLabelVariants">Data-driven pending label variants evaluated before <paramref name="PendingLabel"/>.</param>
/// <param name="Annotations">Open annotations for runtime presentation extension data.</param>
public sealed record ActionRuntimePresentationDefinition(
    string? PendingLabel,
    ActionRuntimeLabelVariantDefinition[] PendingLabelVariants,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares a runtime action label selected when a condition matches the current
/// action runtime data.
/// </summary>
/// <param name="Label">Label to render when the variant matches.</param>
/// <param name="Condition">Value resolved against action runtime data.</param>
/// <param name="ExpectedValue">String value that must match the resolved condition value.</param>
/// <param name="Annotations">Open annotations for variant-level extension data.</param>
public sealed record ActionRuntimeLabelVariantDefinition(
    string Label,
    PresentationValueDefinition Condition,
    string ExpectedValue,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares action-level semantics independently of how a concrete frontend,
/// runtime, or infrastructure adapter implements them.
/// </summary>
/// <param name="Kind">Semantic action family.</param>
/// <param name="LocalDocumentEditor">Optional local document editor semantics.</param>
/// <param name="DocumentWorkspace">Optional document-workspace action semantics.</param>
/// <param name="Annotations">Open annotations for semantic action extension data.</param>
public sealed record ActionSemanticsDefinition(
    ActionSemanticsKind Kind,
    LocalDocumentEditorActionSemantics? LocalDocumentEditor,
    DocumentWorkspaceActionSemantics? DocumentWorkspace,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies semantic action families that are intentionally interpreted by adapters.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionSemanticsKind
{
    LocalDocumentEditor = 0,
    DocumentWorkspace = 1
}

/// <summary>
/// Declares an action that targets the local document editor without specifying
/// how a concrete editor component realizes that action.
/// </summary>
/// <param name="Kind">Local document editor action kind.</param>
/// <param name="Annotations">Open annotations for local editor action extension data.</param>
public sealed record LocalDocumentEditorActionSemantics(
    LocalDocumentEditorActionKind Kind,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies local document editor actions that are interpreted by frontend adapters.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocalDocumentEditorActionKind
{
    Reset = 0,
    Format = 1
}

/// <summary>
/// Declares an action that participates in a semantic document workspace flow
/// without specifying the concrete frontend component that realizes it.
/// </summary>
/// <param name="Kind">Document workspace action kind.</param>
/// <param name="Annotations">Open annotations for document-workspace action extension data.</param>
public sealed record DocumentWorkspaceActionSemantics(
    DocumentWorkspaceActionKind Kind,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies document-workspace action roles interpreted by frontend adapters.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentWorkspaceActionKind
{
    SaveReview = 0,
    SaveCommit = 1,
    SaveCancel = 2,
    SaveRevert = 3,
    ProcessPreview = 4,
    ProcessStart = 5,
    ProcessCancel = 6
}

/// <summary>
/// Declares how an endpoint-backed action should lower semantic runtime state into an endpoint request.
/// </summary>
/// <param name="DataSourceId">Optional data source identifier used to select this projection for data-source-specific endpoints.</param>
/// <param name="EndpointId">Optional endpoint identifier used to select this projection for endpoint-specific request shapes.</param>
/// <param name="RouteParameters">Route parameter bindings for the endpoint request.</param>
/// <param name="BodyFields">Body field bindings for the endpoint request.</param>
/// <param name="Annotations">Open annotations for request projection extension data.</param>
public sealed record ActionEndpointRequestProjectionDefinition(
    string? DataSourceId,
    string? EndpointId,
    ActionEndpointRequestValueBindingDefinition[] RouteParameters,
    ActionEndpointRequestValueBindingDefinition[] BodyFields,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Binds one value source into a route parameter or request body field.
/// </summary>
/// <param name="TargetPath">Route parameter name or body field path to write.</param>
/// <param name="Source">Presentation value expression resolved against the runtime action context.</param>
/// <param name="OmitWhenNull">Whether null or missing values should be omitted from the request.</param>
/// <param name="Annotations">Open annotations for binding-level extension data.</param>
public sealed record ActionEndpointRequestValueBindingDefinition(
    string TargetPath,
    PresentationValueDefinition Source,
    bool OmitWhenNull,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Declares one semantic criterion that controls whether an action can execute.
/// </summary>
/// <param name="Id">Stable criterion identifier.</param>
/// <param name="Name">Human-readable criterion name.</param>
/// <param name="Kind">Criterion kind.</param>
/// <param name="ProcessTaskSelectorId">Optional process-task selector identifier used by process-based criteria.</param>
/// <param name="Message">Optional user-facing reason shown when the criterion blocks execution.</param>
/// <param name="Annotations">Open annotations for criterion-level extension data.</param>
/// <param name="ReferencedActionId">Optional action identifier used by action-relative criteria.</param>
public sealed record ActionEnablementCriterionDefinition(
    string Id,
    string Name,
    ActionEnablementCriterionKind Kind,
    string? ProcessTaskSelectorId,
    string? Message,
    PresentationAnnotationDefinition[] Annotations,
    string? ReferencedActionId = null
);

/// <summary>
/// Classifies semantic action enablement criteria.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionEnablementCriterionKind
{
    NoActiveProcessTask = 0,
    LocalDocumentClean = 1,
    LocalDocumentValid = 2,
    NoPendingAction = 3
}

/// <summary>
/// Classifies a user-invocable or system-invocable semantic action.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionKind
{
    TransitionAction = 0,
    RelationAction = 1,
    LocalStateAction = 2,
    PromptAction = 3,
    FlowAction = 4,
    NavigationAction = 5,
    ExternalAction = 6,
    EffectAction = 7,
    CompositeAction = 8,
    NoOpAction = 9,
    ProcessStartAction = 10
}

/// <summary>
/// Classifies the scope where an action applies.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionScopeKind
{
    Global = 0,
    Page = 1,
    View = 2,
    Collection = 3,
    Row = 4,
    Entity = 5,
    Flow = 6,
    Field = 7,
    Selection = 8,
    System = 9
}

/// <summary>
/// Defines how an action is prepared before execution, such as by opening a prompt or flow.
/// </summary>
/// <param name="Kind">Preparation kind.</param>
/// <param name="FlowId">Optional flow identifier used during preparation.</param>
/// <param name="PromptViewId">Optional prompt view identifier shown during preparation.</param>
/// <param name="CanCancel">Whether the preparation step can be cancelled.</param>
/// <param name="RequiresExplicitCommit">Whether preparation requires an explicit commit before execution.</param>
public sealed record ActionPreparation(
    PreparationKind Kind,
    string? FlowId,
    string? PromptViewId,
    bool CanCancel,
    bool RequiresExplicitCommit
    );

/// <summary>
/// Classifies pre-execution action preparation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreparationKind
{
    None = 0,
    Prompt = 1,
    PreviewFlow = 2,
    Confirmation = 3
}

/// <summary>
/// Defines action execution semantics.
/// </summary>
/// <param name="Mode">Execution mode.</param>
/// <param name="IsLongRunning">Whether execution is expected to outlive the immediate UI interaction.</param>
/// <param name="RequiresConfirmation">Whether execution requires confirmation.</param>
public sealed record ActionExecutionPolicy(
    ActionExecutionMode Mode,
    bool IsLongRunning,
    bool RequiresConfirmation
    );


/// <summary>
/// Classifies how an action is executed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionExecutionMode
{
    Local = 0,
    Immediate = 1,
    RequestAcknowledgePoll = 2,
    Optimistic = 3,
    Background = 4,
    Deferred = 5
}

/// <summary>
/// Defines action result handling semantics.
/// </summary>
/// <param name="InvalidateDataSourceIds">Data source identifiers invalidated by the action result.</param>
/// <param name="NavigateToRouteId">Optional route identifier to navigate to after the action result.</param>
/// <param name="Toast">Optional toast message associated with the action result.</param>
/// <param name="StateWrites">Presentation state writes applied from the action response.</param>
public sealed record ActionResultPolicy(
    string[] InvalidateDataSourceIds,
    string? NavigateToRouteId,
    string? Toast,
    ActionResultStateWriteDefinition[]? StateWrites = null
    );

/// <summary>
/// Declares how an action response updates a presentation data source.
/// </summary>
/// <param name="TargetDataSourceId">Target presentation data-source identifier.</param>
/// <param name="SourcePath">Optional dot-separated path within the action response; null means the full response.</param>
/// <param name="Mode">Write mode used when applying the value.</param>
public sealed record ActionResultStateWriteDefinition(
    string TargetDataSourceId,
    string? SourcePath,
    ActionResultStateWriteMode Mode
    );

/// <summary>
/// Classifies how an action response value is written into presentation state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionResultStateWriteMode
{
    Replace = 0,
    Merge = 1,
    Append = 2,
    Clear = 3
}

/// <summary>
/// Defines where and how an action (<see cref="ActionDefinition"/>) is placed within a view (<see cref="ViewDefinition"/>).
/// </summary>
/// <param name="ActionId">Identifier of the action being placed.</param>
/// <param name="Region">Region key where the action is placed.</param>
/// <param name="Label">Optional placement-specific label.</param>
/// <param name="Icon">Optional placement-specific icon key.</param>
/// <param name="Intent">Optional placement-specific intent.</param>
public sealed record ActionPlacementDefinition(
    string ActionId,
    string Region,
    string? Label = null,
    string? Icon = null,
    string? Intent = null
    );
