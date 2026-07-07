namespace Cohesive.Presentation;

/// <summary>
/// Defines a composable contribution to a <see cref="PresentationModuleDefinition"/>.
/// </summary>
/// <remarks>
/// A contribution is not a separately deployed presentation module. It is an
/// authoring unit used to keep feature or area-owned presentation semantics near
/// the code that owns them while still producing one runtime module definition.
/// </remarks>
public sealed record PresentationModuleContribution
{
    /// <summary>Navigation graphs contributed to the composed module.</summary>
    public NavigationDefinition[] Navigation { get; init; } = [];

    /// <summary>Views contributed to the composed module.</summary>
    public ViewDefinition[] Views { get; init; } = [];

    /// <summary>Workspaces contributed to the composed module.</summary>
    public WorkspaceDefinition[] Workspaces { get; init; } = [];

    /// <summary>Data sources contributed to the composed module.</summary>
    public DataSourceDefinition[] DataSources { get; init; } = [];

    /// <summary>Generic input forms contributed to the composed module.</summary>
    public InputFormDefinition[] InputForms { get; init; } = [];

    /// <summary>Query forms contributed to the composed module.</summary>
    public QueryFormDefinition[] QueryForms { get; init; } = [];

    /// <summary>Field semantics contributed to the composed module.</summary>
    public FieldPresentationDefinition[] Fields { get; init; } = [];

    /// <summary>Actions contributed to the composed module.</summary>
    public ActionDefinition[] Actions { get; init; } = [];

    /// <summary>Flows contributed to the composed module.</summary>
    public FlowDefinition[] Flows { get; init; } = [];

    /// <summary>Named expressions contributed to the composed module.</summary>
    public PresentationExpressionDefinition[] Expressions { get; init; } = [];

    /// <summary>Design-system bindings contributed to the composed module.</summary>
    public DesignSystemBindingDefinition[] DesignSystems { get; init; } = [];

    /// <summary>Concrete target bindings contributed to the composed module.</summary>
    public TargetBindingDefinition[] Targets { get; init; } = [];

    /// <summary>Module-level annotations contributed to the composed module.</summary>
    public PresentationAnnotationDefinition[] Annotations { get; init; } = [];
}
