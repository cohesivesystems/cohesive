using System.Text.Json.Serialization;

namespace Cohesive.Presentation;

/// <summary>
/// Defines chrome-first collection semantics for a view.
/// </summary>
/// <param name="Chrome">Collection chrome slots that bind the body, query forms, summaries, pagination, actions, selection, and detail surfaces.</param>
/// <param name="Annotations">Open annotations for collection-level extension data.</param>
public sealed record CollectionViewDefinition(
    CollectionChromeDefinition Chrome,
    PresentationAnnotationDefinition[] Annotations
);


/// <summary>
/// Defines collection-specific chrome slots around a row-oriented projection.
/// </summary>
/// <param name="Slots">Slots that target adapters may interpret as collection controls or companion surfaces.</param>
/// <param name="Annotations">Open annotations for collection-chrome-level extension data.</param>
public sealed record CollectionChromeDefinition(
    CollectionChromeSlotDefinition[] Slots,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Defines one collection chrome slot.
/// </summary>
/// <param name="Id">Stable slot identifier scoped to the collection view.</param>
/// <param name="Name">Human-readable slot name.</param>
/// <param name="Kind">Semantic chrome role supplied by the slot.</param>
/// <param name="Placement">Preferred placement for the slot relative to the collection grid.</param>
/// <param name="DataSourceIds">Data sources consumed or controlled by the slot.</param>
/// <param name="QueryFormId">Optional query form controlled or hosted by the slot.</param>
/// <param name="DetailViewId">Optional detail view hosted by the slot.</param>
/// <param name="ActionIds">Action identifiers exposed by the slot.</param>
/// <param name="RowActions">Row action bindings projected by this slot.</param>
/// <param name="SelectionActions">Selection action bindings projected by this slot.</param>
/// <param name="Columns">Collection column bindings projected by this slot.</param>
/// <param name="RowIdentityPath">Optional row value path used as a stable row identity for this slot.</param>
/// <param name="RowLabelPath">Optional row value path used for row labels and action accessibility for this slot.</param>
/// <param name="SelectionMode">Optional row selection mode owned by this slot.</param>
/// <param name="ActivatedRowActionId">Optional row action invoked when a row is activated in this slot.</param>
/// <param name="ActivateOnRowClick">Whether primary pointer activation on a row invokes <paramref name="ActivatedRowActionId"/>.</param>
/// <param name="SelectOnRowClick">Whether primary pointer activation on a row updates this slot's selection state.</param>
/// <param name="DetailActivation">Optional interaction that supplies context for a detail slot.</param>
/// <param name="Title">Optional title override for a projected companion surface.</param>
/// <param name="EmptyMessage">Optional message shown when this slot has no active context.</param>
/// <param name="ClearSelectionOnQueryChange">Whether targets should clear row selection when the collection query changes.</param>
/// <param name="FieldIds">Field identifiers displayed by the slot.</param>
/// <param name="StateId">Optional local state identifier controlled or displayed by the slot.</param>
/// <param name="Value">Optional value displayed by the slot.</param>
/// <param name="Annotations">Open annotations for slot-level extension data.</param>
public sealed record CollectionChromeSlotDefinition(
    string Id,
    string Name,
    CollectionChromeSlotKind Kind,
    CollectionChromeSlotPlacement Placement,
    string[] DataSourceIds,
    string? QueryFormId,
    string? DetailViewId,
    string[] ActionIds,
    CollectionRowActionDefinition[] RowActions,
    CollectionSelectionActionDefinition[] SelectionActions,
    CollectionColumnDefinition[] Columns,
    string? RowIdentityPath,
    string? RowLabelPath,
    CollectionSelectionMode? SelectionMode,
    string? ActivatedRowActionId,
    bool ActivateOnRowClick,
    bool SelectOnRowClick,
    CollectionDetailActivation? DetailActivation,
    string? Title,
    string? EmptyMessage,
    bool ClearSelectionOnQueryChange,
    string[] FieldIds,
    string? StateId,
    PresentationValueDefinition? Value,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies collection chrome semantics.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CollectionChromeSlotKind
{
    QueryForm = 0,
    Pagination = 1,
    SelectionActions = 2,
    RowActions = 3,
    Detail = 4,
    Summary = 5,
    Custom = 6,
    Body = 7
}

/// <summary>
/// Classifies the preferred placement of collection chrome.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CollectionChromeSlotPlacement
{
    None = 0,
    Header = 1,
    Toolbar = 2,
    Above = 3,
    Inline = 4,
    Footer = 5,
    SidePanel = 6,
    Drawer = 7
}

/// <summary>
/// Defines one action projected for a collection selection.
/// </summary>
/// <param name="Id">Stable selection action identifier scoped to the collection view.</param>
/// <param name="ActionId">Presentation action invoked by this selection action.</param>
/// <param name="Label">Optional selection-action-specific label.</param>
/// <param name="Icon">Optional selection-action-specific icon key.</param>
/// <param name="Order">Stable selection action order.</param>
/// <param name="MinimumSelectionCount">Minimum number of selected rows required to enable this action.</param>
/// <param name="MaximumSelectionCount">Optional maximum number of selected rows allowed for this action.</param>
/// <param name="Parameters">Bindings from the current selection into action parameters.</param>
/// <param name="IsEnabled">Optional expression-like value used by targets to enable or disable the selection action.</param>
/// <param name="IsVisible">Optional expression-like value used by targets to hide or show the selection action.</param>
/// <param name="Annotations">Open annotations for selection-action-level extension data.</param>
public sealed record CollectionSelectionActionDefinition(
    string Id,
    string ActionId,
    string? Label,
    string? Icon,
    int Order,
    int MinimumSelectionCount,
    int? MaximumSelectionCount,
    CollectionSelectionActionParameterBindingDefinition[] Parameters,
    PresentationValueDefinition? IsEnabled,
    PresentationValueDefinition? IsVisible,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Binds one action parameter from a collection selection.
/// </summary>
/// <param name="Name">Target action parameter name.</param>
/// <param name="Source">Selection value source used as the parameter input.</param>
/// <param name="ValuePath">Optional row value path used by selected-row value sources.</param>
/// <param name="FieldId">Optional presentation field associated with the source value.</param>
/// <param name="OmitWhenEmpty">Whether empty or missing values should omit the parameter.</param>
/// <param name="Annotations">Open annotations for parameter-binding-level extension data.</param>
public sealed record CollectionSelectionActionParameterBindingDefinition(
    string Name,
    CollectionSelectionActionParameterSource Source,
    string? ValuePath,
    string? FieldId,
    bool OmitWhenEmpty,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies selection values that can be bound into action parameters.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CollectionSelectionActionParameterSource
{
    SelectedRowIdentity = 0,
    SelectedRowIdentityList = 1,
    SelectedRowValue = 2,
    SelectedRowValueList = 3,
    SelectionCount = 4
}

/// <summary>
/// Classifies row selection behavior for a collection.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CollectionSelectionMode
{
    None = 0,
    Single = 1,
    Multiple = 2
}

/// <summary>
/// Classifies which interaction supplies context for a collection detail view.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CollectionDetailActivation
{
    None = 0,
    Selection = 1,
    RowActivation = 2,
    Hover = 3
}

/// <summary>
/// Defines one row-level action projected by a collection view.
/// </summary>
/// <param name="Id">Stable row action identifier scoped to the collection view.</param>
/// <param name="ActionId">Presentation action invoked by this row action.</param>
/// <param name="Kind">Row action placement/role within the collection.</param>
/// <param name="Label">Optional row-action-specific label.</param>
/// <param name="Icon">Optional row-action-specific icon key.</param>
/// <param name="Order">Stable row action order.</param>
/// <param name="Parameters">Bindings from row values into action parameters.</param>
/// <param name="IsEnabled">Optional expression-like value used by targets to enable or disable the row action.</param>
/// <param name="IsVisible">Optional expression-like value used by targets to hide or show the row action.</param>
/// <param name="Annotations">Open annotations for row-action-level extension data.</param>
public sealed record CollectionRowActionDefinition(
    string Id,
    string ActionId,
    CollectionRowActionKind Kind,
    string? Label,
    string? Icon,
    int Order,
    CollectionRowActionParameterBindingDefinition[] Parameters,
    PresentationValueDefinition? IsEnabled,
    PresentationValueDefinition? IsVisible,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Binds one action parameter from a row value.
/// </summary>
/// <param name="Name">Target action parameter name.</param>
/// <param name="ValuePath">Dot-separated row value path used as the parameter source.</param>
/// <param name="FieldId">Optional presentation field associated with the source value.</param>
/// <param name="OmitWhenNull">Whether null or missing values should omit the parameter.</param>
/// <param name="Annotations">Open annotations for parameter-binding-level extension data.</param>
public sealed record CollectionRowActionParameterBindingDefinition(
    string Name,
    string ValuePath,
    string? FieldId,
    bool OmitWhenNull,
    PresentationAnnotationDefinition[] Annotations
);

/// <summary>
/// Classifies row action placement within a collection.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CollectionRowActionKind
{
    Primary = 0,
    ContextMenu = 1
}

/// <summary>
/// Defines one projected collection column.
/// </summary>
/// <param name="Id">Stable column identifier scoped to the collection view.</param>
/// <param name="FieldId">Presentation field projected by this column.</param>
/// <param name="ValuePath">Optional row value path that overrides the field path.</param>
/// <param name="IsVisible">Whether the column is initially visible.</param>
/// <param name="Order">Stable column order.</param>
/// <param name="Width">Optional target-independent width hint.</param>
/// <param name="Annotations">Open annotations for column-level extension data.</param>
public sealed record CollectionColumnDefinition(
    string Id,
    string FieldId,
    string? ValuePath,
    bool IsVisible,
    int Order,
    string? Width,
    PresentationAnnotationDefinition[] Annotations
);
