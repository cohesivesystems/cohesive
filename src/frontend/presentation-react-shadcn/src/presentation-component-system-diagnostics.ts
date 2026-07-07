import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
  PresentationShadcnComponentSystemRoleGroups as PresentationComponentSystemRoleGroups,
} from './presentation-shadcn-component-system'
import {
  componentSystemComponentRoles,
  presentationViewComponentRoles,
  promptChildViewComponentRoles,
} from '@cohesive/presentation-contracts'
import {
  createPresentationProjectionDiagnostic,
  type PresentationProjectionDiagnostic,
} from '@cohesive/presentation-core'
import type {
  DesignSystemBindingDefinition,
  PresentationBindingDefinition,
  PresentationModuleDefinition,
} from '@cohesive/presentation-contracts'

export interface ProjectPresentationComponentSystemDiagnosticsOptions {
  readonly componentSystem: PresentationComponentSystem
  readonly defaultComponentSystem: PresentationComponentSystem
  readonly sourceId: string
}

export interface ProjectPresentationDesignSystemBindingDiagnosticsOptions {
  readonly componentSystem: PresentationComponentSystem
  readonly module: Pick<PresentationModuleDefinition, 'DesignSystems'> | null
  readonly sourceId?: string
}

interface PresentationComponentRoleDescriptor {
  readonly group: keyof PresentationComponentSystemRoleGroups
  readonly name: string
  readonly read: (componentSystem: PresentationComponentSystem) => unknown
}

interface PresentationDesignSystemComponentRoleDescriptor
  extends PresentationComponentRoleDescriptor {
  readonly bindingIds: readonly string[]
  readonly componentRole: string
}

const presentationComponentRoleDescriptors = [
  createRoleDescriptor('actions', 'ActionButton'),
  createRoleDescriptor('badges', 'Badge'),
  createRoleDescriptor('collectionChrome', 'CollectionBodySlot'),
  createRoleDescriptor('collectionChrome', 'CollectionDetailSlot'),
  createRoleDescriptor('collectionChrome', 'CollectionPaginationBar'),
  createRoleDescriptor('collectionChrome', 'CollectionQueryFormSlot'),
  createRoleDescriptor('collectionChrome', 'CollectionRowActions'),
  createRoleDescriptor('collectionChrome', 'CollectionSelectionActionToolbar'),
  createRoleDescriptor('collectionChrome', 'CollectionSummarySlot'),
  createRoleDescriptor('collections', 'CollectionDetailLayout'),
  createRoleDescriptor('collections', 'DataTable'),
  createRoleDescriptor('collections', 'RowActionMenu'),
  createRoleDescriptor('collections', 'RowActionMenuItem'),
  createRoleDescriptor('collections', 'RowActionMenuTrigger'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceDetailPanel'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceLayoutGroup'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceLayoutPane'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceNodeLabel'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceShell'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceSurfaceSlot'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceStatus'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceTable'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceTreeControls'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceTreeItem'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceTreeLayout'),
  createRoleDescriptor('documentWorkspaces', 'DocumentWorkspaceTreeView'),
  createRoleDescriptor('documentWorkspaces', 'JsonDocumentDiff'),
  createRoleDescriptor('documentWorkspaces', 'JsonDocumentEditor'),
  createRoleDescriptor('fieldValues', 'FieldValueCode'),
  createRoleDescriptor('fieldValues', 'FieldValueComposite'),
  createRoleDescriptor('fieldValues', 'FieldValueEmpty'),
  createRoleDescriptor('fieldValues', 'FieldValueJson'),
  createRoleDescriptor('fieldValues', 'FieldValueScalar'),
  createRoleDescriptor('fieldValues', 'FieldValueSupportingValue'),
  createRoleDescriptor('feedback', 'StatusBlock'),
  createRoleDescriptor('forms', 'CheckboxControl'),
  createRoleDescriptor('forms', 'ChoiceToggleGroup'),
  createRoleDescriptor('forms', 'ChoiceToggleItem'),
  createRoleDescriptor('forms', 'DateTimeFilterControl'),
  createRoleDescriptor('forms', 'FormActionButton'),
  createRoleDescriptor('forms', 'FormFieldLabel'),
  createRoleDescriptor('forms', 'InputForm'),
  createRoleDescriptor('forms', 'InputFormActionRow'),
  createRoleDescriptor('forms', 'InputFormControlGroup'),
  createRoleDescriptor('forms', 'InputFormControlSlot'),
  createRoleDescriptor('forms', 'InputFormField'),
  createRoleDescriptor('forms', 'InputFormFieldMessage'),
  createRoleDescriptor('forms', 'InputFormGroup'),
  createRoleDescriptor('forms', 'InputFormGroups'),
  createRoleDescriptor('forms', 'SelectControl'),
  createRoleDescriptor('forms', 'TextInputControl'),
  createRoleDescriptor('metrics', 'MetricItem'),
  createRoleDescriptor('metrics', 'MetricStrip'),
  createRoleDescriptor('navigation', 'NavigationLink'),
  createRoleDescriptor('processes', 'ProcessTaskNotice'),
  createRoleDescriptor('prompts', 'PromptContent'),
  createRoleDescriptor('prompts', 'PromptFooter'),
  createRoleDescriptor('prompts', 'PromptHeaderActions'),
  createRoleDescriptor('prompts', 'PromptModal'),
  createRoleDescriptor('prompts', 'PromptRegion'),
  createRoleDescriptor('records', 'RecordDetailEmptyState'),
  createRoleDescriptor('records', 'RecordDetailField'),
  createRoleDescriptor('records', 'RecordDetails'),
  createRoleDescriptor('surfaces', 'ViewSurface'),
  createRoleDescriptor('surfaces', 'ViewSurfaceChromePlacement'),
  createRoleDescriptor('surfaces', 'ViewSurfaceContent'),
  createRoleDescriptor('surfaces', 'ViewSurfaceHeaderActions'),
  createRoleDescriptor('tabs', 'TabsLayout'),
  createRoleDescriptor('tabs', 'TabsList'),
  createRoleDescriptor('tabs', 'TabsPanel'),
  createRoleDescriptor('tabs', 'TabsTrigger'),
  createRoleDescriptor('viewChrome', 'ActionSlot'),
  createRoleDescriptor('viewChrome', 'BadgeStrip'),
  createRoleDescriptor('viewChrome', 'LayoutSwitch'),
  createRoleDescriptor('viewChrome', 'MetricStripSlot'),
  createRoleDescriptor('viewChrome', 'ViewChromeSlot'),
  createRoleDescriptor('viewChrome', 'ViewSwitch'),
] as const

const presentationDesignSystemComponentRoleDescriptors = [
  createDesignSystemComponentRoleDescriptor(
    'actions',
    'ActionButton',
    componentSystemComponentRoles.actionButton,
    ['button', 'action-button'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'badges',
    'Badge',
    componentSystemComponentRoles.badge,
    ['badge'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'collectionChrome',
    'CollectionBodySlot',
    componentSystemComponentRoles.collectionBodySlot,
    ['collection-body-slot'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'collectionChrome',
    'CollectionDetailSlot',
    componentSystemComponentRoles.collectionDetailSlot,
    ['collection-detail-slot'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'collectionChrome',
    'CollectionPaginationBar',
    componentSystemComponentRoles.collectionPaginationBar,
    ['collection-pagination-bar'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'collectionChrome',
    'CollectionQueryFormSlot',
    componentSystemComponentRoles.collectionQueryFormSlot,
    ['collection-query-form-slot'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'collectionChrome',
    'CollectionRowActions',
    componentSystemComponentRoles.collectionRowActions,
    ['collection-row-actions'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'collectionChrome',
    'CollectionSelectionActionToolbar',
    componentSystemComponentRoles.collectionSelectionActionToolbar,
    ['collection-selection-action-toolbar'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'collectionChrome',
    'CollectionSummarySlot',
    componentSystemComponentRoles.collectionSummarySlot,
    ['collection-summary-slot'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'collections',
    'DataTable',
    componentSystemComponentRoles.dataTable,
    ['table', 'data-table'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'fieldValues',
    'FieldValueCode',
    componentSystemComponentRoles.fieldValueCode,
    ['field-value-code'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'fieldValues',
    'FieldValueComposite',
    componentSystemComponentRoles.fieldValueComposite,
    ['field-value-composite'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'fieldValues',
    'FieldValueEmpty',
    componentSystemComponentRoles.fieldValueEmpty,
    ['field-value-empty'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'fieldValues',
    'FieldValueJson',
    componentSystemComponentRoles.fieldValueJson,
    ['field-value-json'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'fieldValues',
    'FieldValueScalar',
    componentSystemComponentRoles.fieldValueScalar,
    ['field-value-scalar'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'fieldValues',
    'FieldValueSupportingValue',
    componentSystemComponentRoles.fieldValueSupportingValue,
    ['field-value-supporting-value'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'forms',
    'InputForm',
    componentSystemComponentRoles.inputForm,
    ['input-form'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'forms',
    'InputFormActionRow',
    componentSystemComponentRoles.inputFormActionRow,
    ['input-form-action-row'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'forms',
    'InputFormControlGroup',
    componentSystemComponentRoles.inputFormControlGroup,
    ['input-form-control-group'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'forms',
    'InputFormControlSlot',
    componentSystemComponentRoles.inputFormControlSlot,
    ['input-form-control-slot'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'forms',
    'InputFormField',
    componentSystemComponentRoles.inputFormField,
    ['input-form-field'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'forms',
    'InputFormFieldMessage',
    componentSystemComponentRoles.inputFormFieldMessage,
    ['input-form-field-message'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'forms',
    'InputFormGroup',
    componentSystemComponentRoles.inputFormGroup,
    ['input-form-group'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'forms',
    'InputFormGroups',
    componentSystemComponentRoles.inputFormGroups,
    ['input-form-groups'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'documentWorkspaces',
    'DocumentWorkspaceShell',
    componentSystemComponentRoles.documentWorkspaceShell,
    ['document-workspace-shell'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'documentWorkspaces',
    'DocumentWorkspaceLayoutGroup',
    componentSystemComponentRoles.documentWorkspaceLayoutGroup,
    ['document-workspace-layout-group'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'documentWorkspaces',
    'DocumentWorkspaceLayoutPane',
    componentSystemComponentRoles.documentWorkspaceLayoutPane,
    ['document-workspace-layout-pane'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'documentWorkspaces',
    'DocumentWorkspaceSurfaceSlot',
    componentSystemComponentRoles.documentWorkspaceSurfaceSlot,
    ['document-workspace-surface-slot'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'documentWorkspaces',
    'DocumentWorkspaceTreeView',
    componentSystemComponentRoles.documentWorkspaceTreeView,
    ['tree', 'tree-view', 'document-workspace-tree-view'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'documentWorkspaces',
    'JsonDocumentDiff',
    componentSystemComponentRoles.jsonDocumentDiff,
    ['diff', 'json-diff', 'json-document-diff'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'documentWorkspaces',
    'JsonDocumentEditor',
    componentSystemComponentRoles.jsonDocumentEditor,
    ['json-editor', 'json-document-editor'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'documentWorkspaces',
    'JsonDocumentDiff',
    promptChildViewComponentRoles.jsonDocumentDiff,
    ['projected-json-document-diff'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'documentWorkspaces',
    'DocumentWorkspaceShell',
    promptChildViewComponentRoles.promptDocumentPreview,
    ['projected-prompt-document-preview'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'feedback',
    'StatusBlock',
    componentSystemComponentRoles.statusBlock,
    ['status-block'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'metrics',
    'MetricItem',
    componentSystemComponentRoles.metricItem,
    ['metric-item'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'metrics',
    'MetricStrip',
    componentSystemComponentRoles.metricStrip,
    ['metric-strip'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'metrics',
    'MetricStrip',
    presentationViewComponentRoles.metricDashboard,
    ['projected-metric-dashboard'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'processes',
    'ProcessTaskNotice',
    componentSystemComponentRoles.processTaskNotice,
    ['process-task-notice'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'prompts',
    'PromptModal',
    componentSystemComponentRoles.promptModal,
    ['prompt-modal'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'prompts',
    'PromptHeaderActions',
    componentSystemComponentRoles.promptHeaderActions,
    ['prompt-header-actions'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'prompts',
    'PromptContent',
    componentSystemComponentRoles.promptContent,
    ['prompt-content'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'prompts',
    'PromptFooter',
    componentSystemComponentRoles.promptFooter,
    ['prompt-footer'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'prompts',
    'PromptRegion',
    componentSystemComponentRoles.promptRegion,
    ['prompt-region'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'records',
    'RecordDetailEmptyState',
    componentSystemComponentRoles.recordDetailEmptyState,
    ['record-detail-empty-state'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'records',
    'RecordDetailField',
    componentSystemComponentRoles.recordDetailField,
    ['record-detail-field'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'records',
    'RecordDetails',
    componentSystemComponentRoles.recordDetails,
    ['record-details'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'tabs',
    'TabsLayout',
    componentSystemComponentRoles.tabsLayout,
    ['tabs-layout'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'tabs',
    'TabsLayout',
    presentationViewComponentRoles.tabsView,
    ['projected-tabs-view'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'tabs',
    'TabsList',
    componentSystemComponentRoles.tabsList,
    ['tabs-list'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'tabs',
    'TabsPanel',
    componentSystemComponentRoles.tabsPanel,
    ['tabs-panel'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'tabs',
    'TabsTrigger',
    componentSystemComponentRoles.tabsTrigger,
    ['tabs-trigger'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'surfaces',
    'ViewSurface',
    componentSystemComponentRoles.viewSurface,
    ['surface', 'view-surface'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'surfaces',
    'ViewSurface',
    presentationViewComponentRoles.viewSurface,
    ['projected-view-surface'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'surfaces',
    'ViewSurfaceChromePlacement',
    componentSystemComponentRoles.viewSurfaceChromePlacement,
    ['view-surface-chrome-placement'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'surfaces',
    'ViewSurfaceContent',
    componentSystemComponentRoles.viewSurfaceContent,
    ['view-surface-content'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'surfaces',
    'ViewSurfaceHeaderActions',
    componentSystemComponentRoles.viewSurfaceHeaderActions,
    ['view-surface-header-actions'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'viewChrome',
    'ActionSlot',
    componentSystemComponentRoles.viewChromeActionSlot,
    ['action-slot', 'view-chrome-action-slot'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'viewChrome',
    'BadgeStrip',
    componentSystemComponentRoles.viewChromeBadgeStrip,
    ['badge-strip', 'view-chrome-badge-strip'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'viewChrome',
    'LayoutSwitch',
    componentSystemComponentRoles.viewChromeLayoutSwitch,
    ['layout-switch', 'view-chrome-layout-switch'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'viewChrome',
    'MetricStripSlot',
    componentSystemComponentRoles.viewChromeMetricStrip,
    ['view-chrome-metric-strip'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'viewChrome',
    'ViewChromeSlot',
    componentSystemComponentRoles.viewChromeSlot,
    ['view-chrome-slot'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'viewChrome',
    'ViewSwitch',
    componentSystemComponentRoles.viewChromeViewSwitch,
    ['view-switch', 'view-chrome-view-switch'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'collections',
    'DataTable',
    presentationViewComponentRoles.collectionView,
    ['projected-collection-view'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'forms',
    'InputForm',
    presentationViewComponentRoles.inputForm,
    ['projected-input-form'],
  ),
  createDesignSystemComponentRoleDescriptor(
    'forms',
    'InputForm',
    presentationViewComponentRoles.queryForm,
    ['projected-query-form'],
  ),
] as const

/**
 * Reports which projected component roles are still interpreted by the default
 * target component system. This gives the developer toolbar a compact TODO list
 * when an app starts replacing shadcn/app defaults with its own component set.
 */
export function projectPresentationComponentSystemDiagnostics({
  componentSystem,
  defaultComponentSystem,
  sourceId,
}: ProjectPresentationComponentSystemDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  const roleStatuses = presentationComponentRoleDescriptors.map((descriptor) => {
    const roleId = `${descriptor.group}.${descriptor.name}`
    const isDefault = descriptor.read(componentSystem) === descriptor.read(defaultComponentSystem)
    return {
      roleId,
      status: isDefault ? 'default' : 'overridden',
    }
  })
  const defaultedRoles = roleStatuses
    .filter((role) => role.status === 'default')
    .map((role) => role.roleId)
  const overriddenRoles = roleStatuses
    .filter((role) => role.status === 'overridden')
    .map((role) => role.roleId)

  if (defaultedRoles.length === 0) {
    return []
  }

  return [
    createPresentationProjectionDiagnostic({
      category: 'local-interpretation',
      details: {
        defaultComponentSystemId: defaultComponentSystem.id,
        defaultedRoles,
        overriddenRoles,
        roleStatuses: Object.fromEntries(
          roleStatuses.map((role) => [role.roleId, role.status]),
        ),
      },
      id: `component-system.${componentSystem.id}.defaulted-roles`,
      interpretation: {
        status: 'locally-interpreted',
        target: componentSystem.target,
      },
      message:
        `Component system '${componentSystem.id}' uses default implementations ` +
        `for ${defaultedRoles.length} projected component role(s).`,
      severity: 'info',
      source: sourceId,
      subject: {
        id: componentSystem.id,
        kind: 'PresentationComponentSystem',
        name: componentSystem.target,
      },
      suggestedNextStep:
        'Override these component-system roles when this app needs a target-specific component interpretation.',
    }),
  ]
}

/**
 * Reports how backend-declared design-system component bindings map onto the
 * local frontend component-system interpreter. Design-system bindings describe
 * low-level target widgets, so concrete component keys are allowed as explicit
 * escape hatches, but they should be visible in the projection diagnostics.
 */
export function projectPresentationDesignSystemBindingDiagnostics({
  componentSystem,
  module,
  sourceId = 'presentation-design-system-bindings',
}: ProjectPresentationDesignSystemBindingDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  if (!module) {
    return []
  }

  return module.DesignSystems.flatMap((designSystem) =>
    designSystem.ComponentBindings.flatMap((binding) =>
      projectDesignSystemComponentBindingDiagnostics({
        binding,
        componentSystem,
        designSystem,
        sourceId,
      }),
    ),
  )
}

function projectDesignSystemComponentBindingDiagnostics({
  binding,
  componentSystem,
  designSystem,
  sourceId,
}: {
  readonly binding: PresentationBindingDefinition
  readonly componentSystem: PresentationComponentSystem
  readonly designSystem: DesignSystemBindingDefinition
  readonly sourceId: string
}): readonly PresentationProjectionDiagnostic[] {
  const bindingId = binding.Id ?? 'unknown'
  const componentKey = binding.ComponentKey ?? null
  const componentRole = binding.ComponentRole ?? null
  const expectedRoleDescriptor = resolveExpectedDesignSystemComponentRole(binding)
  const declaredRoleDescriptor = componentRole
    ? findDesignSystemComponentRoleDescriptorByRole(componentRole)
    : null
  const diagnostics: PresentationProjectionDiagnostic[] = []

  if (componentRole) {
    const roleBound = declaredRoleDescriptor
      ? Boolean(declaredRoleDescriptor.read(componentSystem))
      : false
    diagnostics.push(
      createPresentationProjectionDiagnostic({
        category: roleBound ? 'local-interpretation' : 'missing-binding',
        details: createDesignSystemComponentBindingDiagnosticDetails({
          binding,
          componentSystem,
          declaredRoleDescriptor,
          designSystem,
          expectedRoleDescriptor,
          roleBound,
        }),
        id: `design-system.${designSystem.Id}.${bindingId}.component-role-coverage`,
        interpretation: {
          status: roleBound ? 'locally-interpreted' : 'unbound',
          target: 'design-system-component-role',
        },
        message: roleBound
          ? `Design-system component '${bindingId}' role '${componentRole}' is interpreted by local component-system role '${declaredRoleDescriptor?.group}.${declaredRoleDescriptor?.name}'.`
          : `Design-system component '${bindingId}' role '${componentRole}' is not mapped to a local component-system role.`,
        severity: roleBound ? 'info' : 'warning',
        source: sourceId,
        subject: {
          id: `${designSystem.Id}:${bindingId}`,
          kind: 'design-system-component-binding',
          name: designSystem.Name,
        },
        suggestedNextStep: roleBound
          ? undefined
          : 'Add a frontend component-system role descriptor for this design-system component role or replace it with a modeled standard role.',
      }),
    )
  } else if (expectedRoleDescriptor) {
    diagnostics.push(
      createPresentationProjectionDiagnostic({
        category: 'missing-binding',
        details: createDesignSystemComponentBindingDiagnosticDetails({
          binding,
          componentSystem,
          declaredRoleDescriptor,
          designSystem,
          expectedRoleDescriptor,
          roleBound: false,
        }),
        id: `design-system.${designSystem.Id}.${bindingId}.missing-component-role`,
        interpretation: {
          status: 'unbound',
          target: 'design-system-component-role',
        },
        message:
          `Design-system component '${bindingId}' has a standard component-system ` +
          `role '${expectedRoleDescriptor.componentRole}' but does not declare it.`,
        severity: 'warning',
        source: sourceId,
        subject: {
          id: `${designSystem.Id}:${bindingId}`,
          kind: 'design-system-component-binding',
          name: designSystem.Name,
        },
        suggestedNextStep:
          `Set ComponentRole to '${expectedRoleDescriptor.componentRole}' for this design-system binding.`,
      }),
    )
  } else if (!componentKey) {
    diagnostics.push(
      createPresentationProjectionDiagnostic({
        category: 'missing-binding',
        details: createDesignSystemComponentBindingDiagnosticDetails({
          binding,
          componentSystem,
          declaredRoleDescriptor,
          designSystem,
          expectedRoleDescriptor,
          roleBound: false,
        }),
        id: `design-system.${designSystem.Id}.${bindingId}.missing-component-binding`,
        interpretation: {
          status: 'unbound',
          target: 'design-system-component-role',
        },
        message:
          `Design-system component '${bindingId}' declares neither ComponentRole nor ComponentKey.`,
        severity: 'warning',
        source: sourceId,
        subject: {
          id: `${designSystem.Id}:${bindingId}`,
          kind: 'design-system-component-binding',
          name: designSystem.Name,
        },
        suggestedNextStep:
          'Declare a standard component-system role, or use ComponentKey for a deliberate target-specific primitive.',
      }),
    )
  }

  if (componentKey) {
    diagnostics.push(
      createPresentationProjectionDiagnostic({
        category: 'escape-hatch',
        details: createDesignSystemComponentBindingDiagnosticDetails({
          binding,
          componentSystem,
          declaredRoleDescriptor,
          designSystem,
          expectedRoleDescriptor,
          roleBound: Boolean(
            componentRole &&
              declaredRoleDescriptor &&
              declaredRoleDescriptor.read(componentSystem),
          ),
        }),
        id: `design-system.${designSystem.Id}.${bindingId}.component-key-escape-hatch`,
        interpretation: {
          status: 'escape-hatch',
          target: 'design-system-component-key',
        },
        message:
          `Design-system component '${bindingId}' uses concrete component key ` +
          `'${componentKey}'.`,
        severity: 'warning',
        source: sourceId,
        subject: {
          id: `${designSystem.Id}:${bindingId}`,
          kind: 'design-system-component-binding',
          name: designSystem.Name,
        },
        suggestedNextStep: expectedRoleDescriptor
          ? `Prefer ComponentRole '${expectedRoleDescriptor.componentRole}' and keep ComponentKey only for adapter-specific overrides.`
          : 'Model a standard component-system role when this widget becomes reusable across targets; otherwise keep this as an explicit primitive escape hatch.',
      }),
    )
  }

  return diagnostics
}

function createDesignSystemComponentBindingDiagnosticDetails({
  binding,
  componentSystem,
  declaredRoleDescriptor,
  designSystem,
  expectedRoleDescriptor,
  roleBound,
}: {
  readonly binding: PresentationBindingDefinition
  readonly componentSystem: PresentationComponentSystem
  readonly declaredRoleDescriptor: PresentationDesignSystemComponentRoleDescriptor | null
  readonly designSystem: DesignSystemBindingDefinition
  readonly expectedRoleDescriptor: PresentationDesignSystemComponentRoleDescriptor | null
  readonly roleBound: boolean
}) {
  return {
    bindingId: binding.Id ?? null,
    componentKey: binding.ComponentKey ?? null,
    componentRole: binding.ComponentRole ?? null,
    componentSystemId: componentSystem.id,
    declaredComponentSystemRole: declaredRoleDescriptor
      ? `${declaredRoleDescriptor.group}.${declaredRoleDescriptor.name}`
      : null,
    designSystemId: designSystem.Id,
    designSystemKind: designSystem.Kind,
    expectedComponentRole: expectedRoleDescriptor?.componentRole ?? null,
    expectedComponentSystemRole: expectedRoleDescriptor
      ? `${expectedRoleDescriptor.group}.${expectedRoleDescriptor.name}`
      : null,
    roleBound,
  }
}

function resolveExpectedDesignSystemComponentRole(
  binding: PresentationBindingDefinition,
) {
  const componentRole = binding.ComponentRole ?? null
  if (componentRole) {
    return findDesignSystemComponentRoleDescriptorByRole(componentRole)
  }

  const bindingId = binding.Id?.toLocaleLowerCase()
  if (!bindingId) {
    return null
  }

  return presentationDesignSystemComponentRoleDescriptors.find((descriptor) =>
    descriptor.bindingIds.some((candidate) => candidate.toLocaleLowerCase() === bindingId),
  ) ?? null
}

function findDesignSystemComponentRoleDescriptorByRole(componentRole: string) {
  return presentationDesignSystemComponentRoleDescriptors.find(
    (descriptor) => descriptor.componentRole === componentRole,
  ) ?? null
}

function createRoleDescriptor(
  group: keyof PresentationComponentSystemRoleGroups,
  name: string,
): PresentationComponentRoleDescriptor {
  return {
    group,
    name,
    read: (componentSystem) => {
      const roleGroup = componentSystem[group]
      return roleGroup && typeof roleGroup === 'object'
        ? (roleGroup as unknown as Record<string, unknown>)[name]
        : undefined
    },
  }
}

function createDesignSystemComponentRoleDescriptor(
  group: keyof PresentationComponentSystemRoleGroups,
  name: string,
  componentRole: string,
  bindingIds: readonly string[],
): PresentationDesignSystemComponentRoleDescriptor {
  return {
    ...createRoleDescriptor(group, name),
    bindingIds,
    componentRole,
  }
}
