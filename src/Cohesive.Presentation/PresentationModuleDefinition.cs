namespace Cohesive.Presentation;

/// <summary>
/// Defines a deployable unit of target-independent presentation semantics.
/// </summary>
/// <param name="Id">Stable module identifier.</param>
/// <param name="Name">Human-readable module name.</param>
/// <param name="Version">Optional module version label.</param>
/// <param name="Navigation">Navigation graphs exposed by the module.</param>
/// <param name="Views">View definitions exposed by the module.</param>
/// <param name="Workspaces">Coordinated semantic workspaces exposed by the module.</param>
/// <param name="DataSources">Data sources consumed by views, actions, and flows.</param>
/// <param name="InputForms">Generic input forms that define reusable interaction surfaces.</param>
/// <param name="QueryForms">Query forms that bind user input to result data sources.</param>
/// <param name="Fields">Presentation semantics for fields displayed or edited by the module.</param>
/// <param name="Actions">User-invocable and system-invocable semantic actions.</param>
/// <param name="Flows">Local or distributed interaction flows.</param>
/// <param name="Expressions">Named expression definitions used by presentation policies and projections.</param>
/// <param name="DesignSystems">Design-system bindings available to target adapters.</param>
/// <param name="Targets">Concrete target bindings, such as React or Blazor bindings.</param>
/// <param name="Annotations">Open annotations for module-level extension data.</param>
public sealed record PresentationModuleDefinition(
    string Id,
    string Name,
    string? Version,
    NavigationDefinition[] Navigation,
    ViewDefinition[] Views,
    WorkspaceDefinition[] Workspaces,
    DataSourceDefinition[] DataSources,
    InputFormDefinition[] InputForms,
    QueryFormDefinition[] QueryForms,
    FieldPresentationDefinition[] Fields,
    ActionDefinition[] Actions,
    FlowDefinition[] Flows,
    PresentationExpressionDefinition[] Expressions,
    DesignSystemBindingDefinition[] DesignSystems,
    TargetBindingDefinition[] Targets,
    PresentationAnnotationDefinition[] Annotations
    );
