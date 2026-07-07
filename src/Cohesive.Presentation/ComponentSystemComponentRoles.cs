namespace Cohesive.Presentation;

/// <summary>
/// Standard semantic component roles for target component-system primitives.
/// </summary>
/// <remarks>
/// Component-system roles sit below presentation view roles. They name
/// reusable target widgets that an adapter can interpret with React, Blazor,
/// native UI, or another component stack while still preserving semantic
/// intent in the presentation IR.
/// </remarks>
public static class ComponentSystemComponentRoles
{
    /// <summary>
    /// Standard action button component used by projected action groups.
    /// </summary>
    public const string ActionButton = "cohesive.presentation.component-system.actions.action-button";

    /// <summary>
    /// Standard badge component used by projected field, status, and metric badges.
    /// </summary>
    public const string Badge = "cohesive.presentation.component-system.badges.badge";

    /// <summary>
    /// Standard data-table component used by projected collections.
    /// </summary>
    public const string DataTable = "cohesive.presentation.component-system.collections.data-table";

    /// <summary>
    /// Standard input-form body component used by projected input forms.
    /// </summary>
    public const string InputForm = "cohesive.presentation.component-system.forms.input-form";

    /// <summary>
    /// Standard input-form action row component used by projected input forms.
    /// </summary>
    public const string InputFormActionRow = "cohesive.presentation.component-system.forms.input-form-action-row";

    /// <summary>
    /// Standard input-form grouped control component used by projected range controls.
    /// </summary>
    public const string InputFormControlGroup = "cohesive.presentation.component-system.forms.input-form-control-group";

    /// <summary>
    /// Standard input-form control slot component used by projected input fields.
    /// </summary>
    public const string InputFormControlSlot = "cohesive.presentation.component-system.forms.input-form-control-slot";

    /// <summary>
    /// Standard input-form field component used by projected input forms.
    /// </summary>
    public const string InputFormField = "cohesive.presentation.component-system.forms.input-form-field";

    /// <summary>
    /// Standard input-form field message component used by projected input forms.
    /// </summary>
    public const string InputFormFieldMessage = "cohesive.presentation.component-system.forms.input-form-field-message";

    /// <summary>
    /// Standard input-form group component used by projected input forms.
    /// </summary>
    public const string InputFormGroup = "cohesive.presentation.component-system.forms.input-form-group";

    /// <summary>
    /// Standard input-form groups component used by projected input forms.
    /// </summary>
    public const string InputFormGroups = "cohesive.presentation.component-system.forms.input-form-groups";

    /// <summary>
    /// Standard collection body slot component.
    /// </summary>
    public const string CollectionBodySlot = "cohesive.presentation.component-system.collection-chrome.body-slot";

    /// <summary>
    /// Standard collection detail slot component.
    /// </summary>
    public const string CollectionDetailSlot = "cohesive.presentation.component-system.collection-chrome.detail-slot";

    /// <summary>
    /// Standard collection pagination bar component.
    /// </summary>
    public const string CollectionPaginationBar = "cohesive.presentation.component-system.collection-chrome.pagination-bar";

    /// <summary>
    /// Standard collection query-form slot component.
    /// </summary>
    public const string CollectionQueryFormSlot = "cohesive.presentation.component-system.collection-chrome.query-form-slot";

    /// <summary>
    /// Standard collection row-actions component.
    /// </summary>
    public const string CollectionRowActions = "cohesive.presentation.component-system.collection-chrome.row-actions";

    /// <summary>
    /// Standard collection selection action toolbar component.
    /// </summary>
    public const string CollectionSelectionActionToolbar = "cohesive.presentation.component-system.collection-chrome.selection-action-toolbar";

    /// <summary>
    /// Standard collection summary slot component.
    /// </summary>
    public const string CollectionSummarySlot = "cohesive.presentation.component-system.collection-chrome.summary-slot";

    /// <summary>
    /// Standard field value code component used by projected field renderers.
    /// </summary>
    public const string FieldValueCode = "cohesive.presentation.component-system.field-values.code";

    /// <summary>
    /// Standard field value composite component used by projected field renderers.
    /// </summary>
    public const string FieldValueComposite = "cohesive.presentation.component-system.field-values.composite";

    /// <summary>
    /// Standard field value empty-state component used by projected field renderers.
    /// </summary>
    public const string FieldValueEmpty = "cohesive.presentation.component-system.field-values.empty";

    /// <summary>
    /// Standard field value JSON component used by projected field renderers.
    /// </summary>
    public const string FieldValueJson = "cohesive.presentation.component-system.field-values.json";

    /// <summary>
    /// Standard field value scalar component used by projected field renderers.
    /// </summary>
    public const string FieldValueScalar = "cohesive.presentation.component-system.field-values.scalar";

    /// <summary>
    /// Standard field value supporting value component used by projected field renderers.
    /// </summary>
    public const string FieldValueSupportingValue = "cohesive.presentation.component-system.field-values.supporting-value";

    /// <summary>
    /// Standard JSON document diff component used by document review prompts.
    /// </summary>
    public const string JsonDocumentDiff = "cohesive.presentation.component-system.document-workspaces.json-document-diff";

    /// <summary>
    /// Standard JSON document editor component used by document projections.
    /// </summary>
    public const string JsonDocumentEditor = "cohesive.presentation.component-system.document-workspaces.json-document-editor";

    /// <summary>
    /// Standard document workspace shell component.
    /// </summary>
    public const string DocumentWorkspaceShell = "cohesive.presentation.component-system.document-workspaces.shell";

    /// <summary>
    /// Standard document workspace surface slot component.
    /// </summary>
    public const string DocumentWorkspaceSurfaceSlot = "cohesive.presentation.component-system.document-workspaces.surface-slot";

    /// <summary>
    /// Standard document workspace layout group component.
    /// </summary>
    public const string DocumentWorkspaceLayoutGroup = "cohesive.presentation.component-system.document-workspaces.layout-group";

    /// <summary>
    /// Standard document workspace layout pane component.
    /// </summary>
    public const string DocumentWorkspaceLayoutPane = "cohesive.presentation.component-system.document-workspaces.layout-pane";

    /// <summary>
    /// Standard metric item component used by metric strips and dashboards.
    /// </summary>
    public const string MetricItem = "cohesive.presentation.component-system.metrics.metric-item";

    /// <summary>
    /// Standard metric strip component used by metric strips and dashboards.
    /// </summary>
    public const string MetricStrip = "cohesive.presentation.component-system.metrics.metric-strip";

    /// <summary>
    /// Standard process task notice component used by projected task notices.
    /// </summary>
    public const string ProcessTaskNotice = "cohesive.presentation.component-system.processes.process-task-notice";

    /// <summary>
    /// Standard record detail empty-state component used by projected record details.
    /// </summary>
    public const string RecordDetailEmptyState = "cohesive.presentation.component-system.records.detail-empty-state";

    /// <summary>
    /// Standard record detail field row component used by projected record details.
    /// </summary>
    public const string RecordDetailField = "cohesive.presentation.component-system.records.detail-field";

    /// <summary>
    /// Standard record details component used by projected record detail views.
    /// </summary>
    public const string RecordDetails = "cohesive.presentation.component-system.records.details";

    /// <summary>
    /// Standard tabs layout component used by projected tabbed views.
    /// </summary>
    public const string TabsLayout = "cohesive.presentation.component-system.tabs.layout";

    /// <summary>
    /// Standard tabs list component used by projected tabbed views.
    /// </summary>
    public const string TabsList = "cohesive.presentation.component-system.tabs.list";

    /// <summary>
    /// Standard tabs panel component used by projected tabbed views.
    /// </summary>
    public const string TabsPanel = "cohesive.presentation.component-system.tabs.panel";

    /// <summary>
    /// Standard tabs trigger component used by projected tabbed views.
    /// </summary>
    public const string TabsTrigger = "cohesive.presentation.component-system.tabs.trigger";

    /// <summary>
    /// Standard prompt modal shell component used by projected prompt views.
    /// </summary>
    public const string PromptModal = "cohesive.presentation.component-system.prompts.prompt-modal";

    /// <summary>
    /// Standard prompt header-actions component used by projected prompt views.
    /// </summary>
    public const string PromptHeaderActions = "cohesive.presentation.component-system.prompts.header-actions";

    /// <summary>
    /// Standard prompt content component used by projected prompt views.
    /// </summary>
    public const string PromptContent = "cohesive.presentation.component-system.prompts.content";

    /// <summary>
    /// Standard prompt footer component used by projected prompt views.
    /// </summary>
    public const string PromptFooter = "cohesive.presentation.component-system.prompts.footer";

    /// <summary>
    /// Standard prompt region component used by projected prompt views.
    /// </summary>
    public const string PromptRegion = "cohesive.presentation.component-system.prompts.region";

    /// <summary>
    /// Standard status block component used by projected loading, empty, and error states.
    /// </summary>
    public const string StatusBlock = "cohesive.presentation.component-system.feedback.status-block";

    /// <summary>
    /// Standard document workspace tree view component.
    /// </summary>
    public const string DocumentWorkspaceTreeView = "cohesive.presentation.component-system.document-workspaces.tree-view";

    /// <summary>
    /// Standard view-chrome action slot component.
    /// </summary>
    public const string ViewChromeActionSlot = "cohesive.presentation.component-system.view-chrome.action-slot";

    /// <summary>
    /// Standard view-chrome badge strip component.
    /// </summary>
    public const string ViewChromeBadgeStrip = "cohesive.presentation.component-system.view-chrome.badge-strip";

    /// <summary>
    /// Standard view-chrome layout switch component.
    /// </summary>
    public const string ViewChromeLayoutSwitch = "cohesive.presentation.component-system.view-chrome.layout-switch";

    /// <summary>
    /// Standard view-chrome metric strip component.
    /// </summary>
    public const string ViewChromeMetricStrip = "cohesive.presentation.component-system.view-chrome.metric-strip";

    /// <summary>
    /// Standard view-chrome slot wrapper component.
    /// </summary>
    public const string ViewChromeSlot = "cohesive.presentation.component-system.view-chrome.slot";

    /// <summary>
    /// Standard view-chrome view switch component.
    /// </summary>
    public const string ViewChromeViewSwitch = "cohesive.presentation.component-system.view-chrome.view-switch";

    /// <summary>
    /// Standard view surface component used by projected view surfaces.
    /// </summary>
    public const string ViewSurface = "cohesive.presentation.component-system.surfaces.view-surface";

    /// <summary>
    /// Standard view surface chrome placement component.
    /// </summary>
    public const string ViewSurfaceChromePlacement = "cohesive.presentation.component-system.surfaces.view-surface-chrome-placement";

    /// <summary>
    /// Standard view surface content layout component.
    /// </summary>
    public const string ViewSurfaceContent = "cohesive.presentation.component-system.surfaces.view-surface-content";

    /// <summary>
    /// Standard view surface header-actions component.
    /// </summary>
    public const string ViewSurfaceHeaderActions = "cohesive.presentation.component-system.surfaces.view-surface-header-actions";
}
