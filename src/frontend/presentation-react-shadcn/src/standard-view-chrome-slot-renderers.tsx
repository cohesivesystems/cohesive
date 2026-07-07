import { useMemo, type ReactNode } from 'react'

import {
  createPresentationProjectionDiagnostic,
  createViewChromeSlotRendererRegistry,
  findDocumentFieldNavigationRouteBinding,
  findPresentationField,
  findPresentationView,
  formatPresentationValue,
  getViewChromeSlotRendererRegistryKeys,
  isProjectedDocumentEntityReferenceField,
  type NavigationRouteParameters,
  projectDocumentFieldNavigation,
  readPresentationFieldValue,
  resolvePresentationBadges,
  resolvePresentationValue,
  resolvePromptStatusMessages,
  resolveViewChromeSlotRenderer,
  viewChromeIconIds,
  type DocumentEditorLayout,
  type PresentationBadgeDefinition,
  type PresentationDataSourceResolver,
  type PresentationModuleDefinition,
  type PresentationProjectionDiagnostic,
  type ProjectedDocumentActionStatusMap,
  type ProjectedDocumentFieldDefinitionLike,
  type ViewChromeSlotDefinition,
  type ViewDefinition,
} from '@cohesive/presentation-core'
import {
  PresentationActionGroup,
  type PresentationActionGroupOptions,
} from './presentation-action-group'
import {
  renderPresentationIcon,
} from './presentation-icon-registry'
import {
  usePresentationNavigationRuntime,
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesive/presentation-react'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type { PresentationDesignSystem } from '@cohesive/presentation-tailwind'
import {
  interpretPresentationBadgeTarget,
  type PresentationBadgeTargetInterpreterRegistry,
} from './presentation-badge-target-interpreter'
import {
  ProjectedPresentationBadge,
} from './projected-presentation-badges'
import {
  renderProjectedEntityReferenceValue,
} from './projected-field-value-rendering'
import { ProjectedStatusBlock } from './projected-activity-state'
import {
  ProjectedMetricStrip,
  type ProjectedMetricValue,
} from './projected-metric-strip'
import {
  viewChromeSlotKinds,
} from '@cohesive/presentation-contracts'

export type ProjectedViewChromeMetadataRole = string

export type ProjectedViewChromeMetadataBadgeRoleReader = (
  badge: Pick<PresentationBadgeDefinition, 'Annotations'>,
  field: Pick<ProjectedDocumentFieldDefinitionLike, 'Annotations'> | null | undefined,
) => ProjectedViewChromeMetadataRole | null

export type ProjectedViewChromeMetadataFieldRoleReader = (
  field: Pick<ProjectedDocumentFieldDefinitionLike, 'Annotations'> | null | undefined,
) => ProjectedViewChromeMetadataRole | null

export interface ProjectedViewChromeSlotRenderContext<TActionContext> {
  readonly actionContext: TActionContext
  readonly actionGroupOptions?: PresentationActionGroupOptions<TActionContext>
  readonly actionStatuses?: ProjectedDocumentActionStatusMap
  readonly activeViewId?: string | null
  readonly componentSystem: PresentationComponentSystem
  readonly createHref?: (
    routeId: string,
    parameters?: NavigationRouteParameters,
  ) => string | null
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly documentViewIds?: readonly string[]
  readonly badgeInterpreters?: PresentationBadgeTargetInterpreterRegistry
  readonly layout?: DocumentEditorLayout | null
  readonly metadataEntityReferenceRole?: ProjectedViewChromeMetadataRole | null
  readonly metricMessage?: ReactNode
  readonly metricValues?: Readonly<Record<string, ProjectedMetricValue>>
  readonly module: PresentationModuleDefinition | null
  readonly navigateHref?: (href: string) => void
  readonly onActiveViewIdChange?: (viewId: string) => void
  readonly onLayoutChange?: (layout: DocumentEditorLayout) => void
  readonly readMetadataBadgeRole?: ProjectedViewChromeMetadataBadgeRoleReader
  readonly readMetadataFieldRole?: ProjectedViewChromeMetadataFieldRoleReader
  readonly resource?: unknown
  readonly slot: ViewChromeSlotDefinition
  readonly view: ViewDefinition | null
  readonly workspaceView?: ViewDefinition | null
}

export const standardViewChromeSlotRenderers =
  createViewChromeSlotRendererRegistry<
    ProjectedViewChromeSlotRenderContext<object>,
    ReactNode
  >([
    {
      kind: viewChromeSlotKinds.actions,
      render: renderStandardViewActions,
    },
    {
      kind: viewChromeSlotKinds.badgeStrip,
      render: renderStandardViewBadgeStrip,
    },
    {
      kind: viewChromeSlotKinds.metricStrip,
      render: renderStandardViewMetricStrip,
    },
    {
      kind: viewChromeSlotKinds.viewSwitch,
      render: renderStandardViewSwitch,
    },
    {
      kind: viewChromeSlotKinds.layoutSwitch,
      render: renderStandardLayoutSwitch,
    },
    {
      kind: viewChromeSlotKinds.headingTrailing,
      render: renderStandardViewBadgeStrip,
    },
    {
      kind: viewChromeSlotKinds.status,
      render: renderStandardViewStatus,
    },
  ])

export const standardViewChromeSlotRendererKeys =
  getViewChromeSlotRendererRegistryKeys(standardViewChromeSlotRenderers)

const emptyMetricValues: Readonly<Record<string, ProjectedMetricValue>> = {}

type ProjectedViewBadgeRenderingMode =
  | 'role-interpreter'
  | 'standard-badge'
  | 'unrendered'

interface ProjectedViewBadgeRenderingEntry {
  readonly badgeFieldId: string | null
  readonly badgeId: string
  readonly badgeName: string
  readonly field: ProjectedDocumentFieldDefinitionLike | null
  readonly fieldId: string
  readonly mode: ProjectedViewBadgeRenderingMode
  readonly node: ReactNode
  readonly rendered: boolean
  readonly role: ProjectedViewChromeMetadataRole | null
  readonly value: unknown
}

export function renderStandardViewChromeSlot<TActionContext>(
  context: ProjectedViewChromeSlotRenderContext<TActionContext>,
) {
  const renderer = resolveViewChromeSlotRenderer(
    standardViewChromeSlotRenderers,
    context.slot,
  )
  return renderer?.(
    context as unknown as ProjectedViewChromeSlotRenderContext<object>,
  ) ?? null
}

function renderStandardViewActions<TActionContext>({
  actionContext,
  actionGroupOptions,
  componentSystem,
  dataSourceResolver,
  designSystem,
  module,
  slot,
  view,
}: ProjectedViewChromeSlotRenderContext<TActionContext>) {
  if (!actionGroupOptions || !module || !view) {
    return null
  }

  return componentSystem.viewChrome.ActionSlot({
    children: (
      <PresentationActionGroup
        context={actionContext}
        dataSourceResolver={dataSourceResolver}
        module={module}
        options={{
          ...actionGroupOptions,
          componentSystem,
          designSystem,
        }}
        view={view}
      />
    ),
    slotId: slot.Id,
    viewId: view.Id,
  })
}

function renderStandardViewBadgeStrip({
  componentSystem,
  createHref,
  designSystem,
  badgeInterpreters,
  metadataEntityReferenceRole,
  module,
  navigateHref,
  readMetadataBadgeRole,
  readMetadataFieldRole,
  resource,
  slot,
  view,
}: ProjectedViewChromeSlotRenderContext<object>) {
  return (
    <ProjectedViewBadgeStrip
      badges={slot.Badges ?? []}
      componentSystem={componentSystem}
      designSystem={designSystem}
      badgeInterpreters={badgeInterpreters}
      createHref={createHref}
      fieldIds={slot.FieldIds ?? []}
      metadataEntityReferenceRole={metadataEntityReferenceRole}
      module={module}
      navigateHref={navigateHref}
      readMetadataBadgeRole={readMetadataBadgeRole}
      readMetadataFieldRole={readMetadataFieldRole}
      resource={resource}
      slotId={slot.Id}
      viewId={view?.Id}
      viewName={view?.Name}
    />
  )
}

function renderStandardViewMetricStrip({
  componentSystem,
  dataSourceResolver,
  metricMessage,
  metricValues = emptyMetricValues,
  slot,
  view,
}: ProjectedViewChromeSlotRenderContext<object>) {
  if (!slot.FieldIds || slot.FieldIds.length === 0) {
    return null
  }

  return componentSystem.viewChrome.MetricStripSlot({
    children: (
      <ProjectedMetricStrip
        className="sm:grid-cols-2 lg:grid-cols-4"
        componentSystem={componentSystem}
        dataSourceResolver={dataSourceResolver}
        fieldIds={slot.FieldIds}
        values={metricValues}
        viewId={view?.Id}
      />
    ),
    message: metricMessage,
    slotId: slot.Id,
    viewId: view?.Id,
  })
}

function renderStandardViewStatus({
  actionStatuses = {},
  componentSystem,
  dataSourceResolver,
  resource,
  slot,
  view,
}: ProjectedViewChromeSlotRenderContext<object>) {
  const promptMessages = view?.PromptStatusMessages
    ? resolvePromptStatusMessages({
        actionStatuses,
        dataSourceResolver,
        region: slot.StateId ?? slot.Id,
        view,
      })
    : []
  const literalValue = formatPresentationValue(resolvePresentationValue(slot.Value, resource))
  const messages = promptMessages.length > 0
    ? promptMessages
    : literalValue
      ? [{ label: literalValue, tone: 'default' as const }]
      : []

  if (messages.length === 0) {
    return null
  }

  return (
    <div className="grid gap-2">
      {messages.map((message, index) => (
        <ProjectedStatusBlock
          componentSystem={componentSystem}
          key={promptMessages[index]?.definition.Id ?? `${slot.Id}:${index}`}
          label={message.label}
          tone={message.tone}
        />
      ))}
    </div>
  )
}

function renderStandardViewSwitch({
  activeViewId,
  componentSystem,
  documentViewIds,
  module,
  onActiveViewIdChange,
  slot,
  workspaceView,
}: ProjectedViewChromeSlotRenderContext<object>) {
  const viewIds = documentViewIds && documentViewIds.length > 0
    ? documentViewIds
    : slot.ViewIds
  if (viewIds.length <= 1 || !activeViewId || !onActiveViewIdChange) {
    return null
  }

  return componentSystem.viewChrome.ViewSwitch({
    ariaLabel: 'Document view',
    items: viewIds.map((viewId) => {
      const label = getDocumentViewLabel(module, workspaceView ?? null, viewId)
      const icon = resolveDocumentViewIcon(label, viewId)

      return {
        icon: renderViewChromeIcon({
          fallbackIcon: icon.fallbackIcon,
          icon: icon.id,
          module,
        }),
        id: viewId,
        isActive: activeViewId === viewId,
        label,
        onSelect: () => onActiveViewIdChange(viewId),
      }
    }),
    slotId: slot.Id,
    viewId: workspaceView?.Id,
  })
}

function renderStandardLayoutSwitch({
  componentSystem,
  layout,
  module,
  onLayoutChange,
  slot,
  view,
}: ProjectedViewChromeSlotRenderContext<object>) {
  if (!layout || !onLayoutChange) {
    return null
  }

  return componentSystem.viewChrome.LayoutSwitch({
    ariaLabel: 'Document layout',
    items: [
      {
        icon: renderViewChromeIcon({
          fallbackIcon: 'square',
          icon: viewChromeIconIds.layoutSingle,
          module,
        }),
        id: 'single',
        isActive: layout === 'single',
        label: 'Single',
        onSelect: () => onLayoutChange('single'),
      },
      {
        icon: renderViewChromeIcon({
          fallbackIcon: 'columns-2',
          icon: viewChromeIconIds.layoutSplit,
          module,
        }),
        id: 'split',
        isActive: layout === 'split',
        label: 'Split',
        onSelect: () => onLayoutChange('split'),
      },
    ],
    slotId: slot.Id,
    viewId: view?.Id,
  })
}

export function ProjectedViewBadgeStrip({
  badges,
  componentSystem,
  createHref,
  designSystem,
  badgeInterpreters,
  fieldIds,
  metadataEntityReferenceRole,
  module,
  navigateHref,
  readMetadataBadgeRole,
  readMetadataFieldRole,
  resource,
  slotId = 'adhoc',
  viewId,
  viewName,
}: {
  readonly badges?: readonly PresentationBadgeDefinition[]
  readonly componentSystem: PresentationComponentSystem
  readonly createHref?: (
    routeId: string,
    parameters?: NavigationRouteParameters,
  ) => string | null
  readonly designSystem: PresentationDesignSystem
  readonly badgeInterpreters?: PresentationBadgeTargetInterpreterRegistry
  readonly fieldIds: readonly string[]
  readonly metadataEntityReferenceRole?: ProjectedViewChromeMetadataRole | null
  readonly module: PresentationModuleDefinition | null
  readonly navigateHref?: (href: string) => void
  readonly readMetadataBadgeRole?: ProjectedViewChromeMetadataBadgeRoleReader
  readonly readMetadataFieldRole?: ProjectedViewChromeMetadataFieldRoleReader
  readonly resource: unknown
  readonly slotId?: string
  readonly viewId?: string | null
  readonly viewName?: string | null
}) {
  const navigation = usePresentationNavigationRuntime()
  const resolvedCreateHref = createHref ?? navigation.createHref
  const resolvedNavigateHref = navigateHref ?? navigation.navigateHref
  const badgeDefinitions = useMemo(
    () => badges && badges.length > 0
      ? badges
      : fieldIds.map(createFieldBackedBadgeDefinition),
    [badges, fieldIds],
  )
  const renderedBadgeEntries = useMemo(
    (): readonly ProjectedViewBadgeRenderingEntry[] => badgeDefinitions
      .map((badge) => {
        const fieldId = badge.FieldId ?? badge.Id
        const field = findPresentationField<ProjectedDocumentFieldDefinitionLike>(module, fieldId)
        const value = readPresentationFieldValue(resource, field?.Field)
        const resolvedBadge = resolvePresentationBadges([badge], resource, module)[0] ?? null
        const role = readMetadataBadgeRole?.(badge, field) ?? null
        const interpretedBadge = interpretPresentationBadgeTarget(badgeInterpreters, {
          badge,
          componentSystem,
          designSystem,
          field,
          fieldId,
          module,
          resolvedBadge,
          resource,
          value,
        })

        if (interpretedBadge !== undefined) {
          const node = interpretedBadge === null ? null : (
            <span className="contents" key={fieldId}>
              {interpretedBadge}
            </span>
          )
          return {
            badgeFieldId: badge.FieldId ?? null,
            badgeId: badge.Id,
            badgeName: badge.Name,
            field,
            fieldId,
            mode: 'role-interpreter',
            node,
            rendered: Boolean(node),
            role,
            value,
          }
        }

        const node = renderStandardBadgeNode({
          badge,
          componentSystem,
          createHref: resolvedCreateHref,
          designSystem,
          field,
          module,
          navigateHref: resolvedNavigateHref,
          resolvedBadge,
          resource,
          value,
        })
        return {
          badgeFieldId: badge.FieldId ?? null,
          badgeId: badge.Id,
          badgeName: badge.Name,
          field,
          fieldId,
          mode: node ? 'standard-badge' : 'unrendered',
          node,
          rendered: Boolean(node),
          role,
          value,
        }
      }),
    [
      badgeDefinitions,
      componentSystem,
      createHref,
      designSystem,
      badgeInterpreters,
      module,
      navigateHref,
      resolvedCreateHref,
      resolvedNavigateHref,
      readMetadataBadgeRole,
      resource,
    ],
  )
  const renderedBadges = renderedBadgeEntries.flatMap((entry) =>
    entry.node ? [entry.node] : [])
  const roleDiagnosticSource =
    `projected-view-badge-strip:${viewId ?? 'unknown'}:${slotId}:badge-roles`
  const roleDiagnostics = useMemo(
    () => projectViewBadgeStripRoleDiagnostics({
      entries: renderedBadgeEntries,
      fieldIds,
      metadataEntityReferenceRole,
      module,
      readMetadataFieldRole,
      resource,
      sourceId: roleDiagnosticSource,
      slotId,
      viewId,
      viewName,
    }),
    [
      fieldIds,
      metadataEntityReferenceRole,
      module,
      readMetadataFieldRole,
      renderedBadgeEntries,
      resource,
      roleDiagnosticSource,
      slotId,
      viewId,
      viewName,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    roleDiagnosticSource,
    roleDiagnostics,
  )

  if (renderedBadges.length === 0) {
    return null
  }

  return componentSystem.viewChrome.BadgeStrip({
    badges: renderedBadges,
    slotId,
    viewId,
  })
}

function renderStandardBadgeNode({
  badge,
  componentSystem,
  createHref,
  designSystem,
  field,
  module,
  navigateHref,
  resolvedBadge,
  resource,
  value,
}: {
  readonly badge: PresentationBadgeDefinition
  readonly componentSystem: PresentationComponentSystem
  readonly createHref?: (
    routeId: string,
    parameters?: NavigationRouteParameters,
  ) => string | null
  readonly designSystem: PresentationDesignSystem
  readonly field: ProjectedDocumentFieldDefinitionLike | null
  readonly module: PresentationModuleDefinition | null
  readonly navigateHref?: (href: string) => void
  readonly resolvedBadge: ReturnType<typeof resolvePresentationBadges>[number] | null
  readonly resource: unknown
  readonly value: unknown
}) {
  if (
    field &&
    navigateHref &&
    isProjectedDocumentEntityReferenceField(field) &&
    findDocumentFieldNavigationRouteBinding(module, field)
  ) {
    const entityReferenceBadge = renderProjectedEntityReferenceValue({
      componentSystem,
      createHref,
      field,
      module,
      navigateHref,
      resource,
      unboundEntityReferenceStyle: 'badge',
      value,
    })

    return entityReferenceBadge ? (
      <span className="contents" key={badge.Id}>
        {entityReferenceBadge}
      </span>
    ) : null
  }

  return resolvedBadge ? (
    <ProjectedPresentationBadge
      badge={resolvedBadge}
      componentSystem={componentSystem}
      designSystem={designSystem}
      key={badge.Id}
      variant="outline"
    />
  ) : null
}

function projectViewBadgeStripRoleDiagnostics({
  entries,
  fieldIds,
  metadataEntityReferenceRole,
  module,
  readMetadataFieldRole,
  resource,
  sourceId,
  slotId,
  viewId,
  viewName,
}: {
  readonly entries: readonly ProjectedViewBadgeRenderingEntry[]
  readonly fieldIds: readonly string[]
  readonly metadataEntityReferenceRole?: ProjectedViewChromeMetadataRole | null
  readonly module: PresentationModuleDefinition | null
  readonly readMetadataFieldRole?: ProjectedViewChromeMetadataFieldRoleReader
  readonly resource: unknown
  readonly sourceId: string
  readonly slotId: string
  readonly viewId?: string | null
  readonly viewName?: string | null
}): readonly PresentationProjectionDiagnostic[] {
  const diagnostics: PresentationProjectionDiagnostic[] = []
  const roleEntries = entries.filter((entry) => entry.role)
  const unhandledRoleEntries = roleEntries.filter((entry) =>
    entry.mode !== 'role-interpreter')
  const referencedFieldIds = new Set(entries
    .map((entry) => entry.badgeFieldId)
    .filter((fieldId): fieldId is string => Boolean(fieldId)))
  const unreferencedRoleFields = fieldIds
    .map((fieldId) => findPresentationField<ProjectedDocumentFieldDefinitionLike>(module, fieldId))
    .filter((field): field is ProjectedDocumentFieldDefinitionLike => Boolean(field))
    .map((field) => ({
      field,
      role: readMetadataFieldRole?.(field) ?? null,
    }))
    .filter((entry) => entry.role && !referencedFieldIds.has(entry.field.Id))

  if (roleEntries.length > 0 || unreferencedRoleFields.length > 0) {
    const counts = countBadgeRenderingModes(roleEntries)
    diagnostics.push(
      createPresentationProjectionDiagnostic({
        category: 'local-interpretation',
        details: {
          badges: roleEntries.map((entry) => ({
            badgeId: entry.badgeId,
            fieldId: entry.badgeFieldId,
            mode: entry.mode,
            rendered: entry.rendered,
            role: entry.role,
          })),
          counts,
          slotId,
          viewId: viewId ?? null,
        },
        id: `view.${viewId ?? 'unknown'}.badge-strip.${slotId}.metadata-badge-coverage`,
        interpretation: {
          status: unhandledRoleEntries.length === 0 ? 'locally-interpreted' : 'unbound',
          target: 'presentation-badge-target-interpreter',
        },
        message:
          `Badge strip slot '${slotId}'${viewName ? ` on '${viewName}'` : ''} ` +
          `renders ${roleEntries.length} metadata badge role(s): ` +
          `${counts['role-interpreter']} via role interpreters, ` +
          `${counts['standard-badge']} via standard badge semantics, ` +
          `${counts.unrendered} unrendered.`,
        severity: 'info',
        source: sourceId,
        subject: {
          id: slotId,
          kind: 'view-chrome-slot',
          name: viewName,
        },
      }),
    )
  }

  diagnostics.push(
    ...projectViewBadgeStripEntityReferenceDiagnostics({
      entries: roleEntries,
      metadataEntityReferenceRole,
      module,
      resource,
      sourceId,
      slotId,
      viewId,
      viewName,
    }),
  )

  for (const entry of unhandledRoleEntries) {
    diagnostics.push(
      createPresentationProjectionDiagnostic({
        category: 'missing-binding',
        details: {
          badgeId: entry.badgeId,
          fieldId: entry.badgeFieldId,
          mode: entry.mode,
          role: entry.role,
          slotId,
          viewId: viewId ?? null,
        },
        id: `view.${viewId ?? 'unknown'}.badge-strip.${slotId}.badge.${entry.badgeId}.role-unbound`,
        interpretation: {
          status: 'unbound',
          target: 'presentation-badge-target-interpreter',
        },
        message:
          `Metadata badge '${entry.badgeName}' declares role '${entry.role}', ` +
          `but no badge target interpreter handled it.`,
        severity: 'warning',
        source: sourceId,
        subject: {
          id: entry.badgeId,
          kind: 'presentation-badge',
          name: entry.badgeName,
        },
        suggestedNextStep:
          'Bind this metadata badge role in the frontend badge target interpreter registry.',
      }),
    )
  }

  for (const { field, role } of unreferencedRoleFields) {
    diagnostics.push(
      createPresentationProjectionDiagnostic({
        category: 'incomplete-projection',
        details: {
          fieldId: field.Id,
          fieldPath: field.Field,
          role,
          slotId,
          viewId: viewId ?? null,
        },
        id: `view.${viewId ?? 'unknown'}.badge-strip.${slotId}.field.${field.Id}.role-unreferenced`,
        interpretation: {
          status: 'unbound',
          target: 'presentation-badge-definition',
        },
        message:
          `Metadata field '${field.Label}' declares role '${role}', but no ` +
          `badge in slot '${slotId}' references it.`,
        severity: 'warning',
        source: sourceId,
        subject: {
          id: field.Id,
          kind: 'field',
          name: field.Label,
        },
        suggestedNextStep:
          'Add a role-annotated PresentationBadgeDefinition for this metadata field.',
      }),
    )
  }

  return diagnostics
}

function projectViewBadgeStripEntityReferenceDiagnostics({
  entries,
  metadataEntityReferenceRole,
  module,
  resource,
  sourceId,
  slotId,
  viewId,
  viewName,
}: {
  readonly entries: readonly ProjectedViewBadgeRenderingEntry[]
  readonly metadataEntityReferenceRole?: ProjectedViewChromeMetadataRole | null
  readonly module: PresentationModuleDefinition | null
  readonly resource: unknown
  readonly sourceId: string
  readonly slotId: string
  readonly viewId?: string | null
  readonly viewName?: string | null
}): readonly PresentationProjectionDiagnostic[] {
  if (!metadataEntityReferenceRole) {
    return []
  }

  const entityReferenceEntries = entries.filter((entry) =>
    entry.role === metadataEntityReferenceRole)
  if (entityReferenceEntries.length === 0) {
    return []
  }

  const routeStates = entityReferenceEntries.map((entry) => {
    const binding = entry.field
      ? findDocumentFieldNavigationRouteBinding(module, entry.field)
      : null
    const projection =
      entry.field && binding?.RouteId
        ? projectDocumentFieldNavigation({
          field: entry.field,
          module,
          resource,
          value: entry.value,
        })
        : null

    return {
      binding,
      entry,
      projection,
    }
  })
  const routeMissing = routeStates.filter((state) => !state.binding?.RouteId)
  const unresolvedParameters = routeStates.filter((state) =>
    (state.projection?.missingParameterNames.length ?? 0) > 0)
  const routeReady = routeStates.filter((state) =>
    state.binding?.RouteId &&
    state.projection &&
    state.projection.missingParameterNames.length === 0)
  const fallback = routeStates.filter((state) =>
    !state.binding?.RouteId || !state.projection)
  const diagnostics: PresentationProjectionDiagnostic[] = [
    createPresentationProjectionDiagnostic({
      category: 'local-interpretation',
      details: {
        fallback: fallback.map(({ entry }) => entry.badgeId),
        routeMissing: routeMissing.map(({ entry }) => entry.badgeId),
        routeReady: routeReady.map(({ entry, projection }) => ({
          badgeId: entry.badgeId,
          routeId: projection?.routeId,
        })),
        slotId,
        unresolvedParameters: unresolvedParameters.map(({ entry, projection }) => ({
          badgeId: entry.badgeId,
          missingParameterNames: projection?.missingParameterNames ?? [],
          routeId: projection?.routeId,
        })),
        viewId: viewId ?? null,
      },
      id: `view.${viewId ?? 'unknown'}.badge-strip.${slotId}.entity-reference-badge-coverage`,
      interpretation: {
        status: routeMissing.length === 0 && unresolvedParameters.length === 0
          ? 'locally-interpreted'
          : 'unbound',
        target: 'entity-reference-badge-route-binding',
      },
      message:
        `Badge strip slot '${slotId}'${viewName ? ` on '${viewName}'` : ''} ` +
        `renders ${entityReferenceEntries.length} entity-reference badge(s): ` +
        `${routeReady.length} route-bound link badge(s), ` +
        `${fallback.length} non-link fallback badge(s), ` +
        `${unresolvedParameters.length} with unresolved route parameter(s).`,
      severity: 'info',
      source: sourceId,
      subject: {
        id: slotId,
        kind: 'view-chrome-slot',
        name: viewName,
      },
    }),
  ]

  for (const { entry } of routeMissing) {
    diagnostics.push(
      createPresentationProjectionDiagnostic({
        category: 'missing-binding',
        details: {
          badgeId: entry.badgeId,
          fieldId: entry.badgeFieldId,
          role: entry.role,
          slotId,
          viewId: viewId ?? null,
        },
        id: `view.${viewId ?? 'unknown'}.badge-strip.${slotId}.badge.${entry.badgeId}.entity-reference-route-missing`,
        interpretation: {
          status: 'unbound',
          target: 'entity-reference-badge-route-binding',
        },
        message:
          `Entity-reference badge '${entry.badgeName}' has no route target binding; ` +
          'it renders as a non-link fallback badge.',
        severity: 'warning',
        source: sourceId,
        subject: {
          id: entry.badgeId,
          kind: 'presentation-badge',
          name: entry.badgeName,
        },
        suggestedNextStep:
          'Add a navigation route target binding for this entity-reference field.',
      }),
    )
  }

  for (const { entry, projection } of unresolvedParameters) {
    diagnostics.push(
      createPresentationProjectionDiagnostic({
        category: 'unbound',
        details: {
          badgeId: entry.badgeId,
          fieldId: entry.badgeFieldId,
          missingParameterNames: projection?.missingParameterNames ?? [],
          role: entry.role,
          routeId: projection?.routeId,
          slotId,
          viewId: viewId ?? null,
        },
        id: `view.${viewId ?? 'unknown'}.badge-strip.${slotId}.badge.${entry.badgeId}.entity-reference-route-parameters-unresolved`,
        interpretation: {
          status: 'unbound',
          target: 'entity-reference-badge-route-parameters',
        },
        message:
          `Entity-reference badge '${entry.badgeName}' has unresolved route ` +
          `parameter(s): ${(projection?.missingParameterNames ?? []).join(', ')}.`,
        severity: 'warning',
        source: sourceId,
        subject: {
          id: entry.badgeId,
          kind: 'presentation-badge',
          name: entry.badgeName,
        },
        suggestedNextStep:
          'Update the route parameter binding for this entity-reference field or expose the required source value.',
      }),
    )
  }

  return diagnostics
}

function countBadgeRenderingModes(
  entries: readonly ProjectedViewBadgeRenderingEntry[],
): Record<ProjectedViewBadgeRenderingMode, number> {
  return entries.reduce<Record<ProjectedViewBadgeRenderingMode, number>>(
    (counts, entry) => ({
      ...counts,
      [entry.mode]: counts[entry.mode] + 1,
    }),
    {
      'role-interpreter': 0,
      'standard-badge': 0,
      unrendered: 0,
    },
  )
}

function getDocumentViewLabel(
  module: PresentationModuleDefinition | null,
  workspaceView: ViewDefinition | null,
  viewId: string,
) {
  const region = workspaceView?.Regions?.find((candidate) =>
    candidate.ViewIds?.includes(viewId),
  )
  return (
    region?.Name ??
    findPresentationView(module, viewId)?.Name ??
    getDocumentViewFallbackLabel(viewId)
  )
}

function getDocumentViewFallbackLabel(viewId: string) {
  const normalized = viewId.toLocaleLowerCase()
  if (normalized.includes('json')) {
    return 'JSON'
  }

  if (normalized.includes('structure')) {
    return 'Structure'
  }

  if (normalized.includes('types')) {
    return 'Types'
  }

  return viewId
}

function createFieldBackedBadgeDefinition(fieldId: string): PresentationBadgeDefinition {
  return {
    Annotations: [],
    Content: null,
    FieldId: fieldId,
    Id: fieldId,
    Name: fieldId,
    OmitWhenEmpty: true,
    OmitWhenZero: false,
    Tone: null,
    Value: null,
    ValueTemplate: null,
  }
}

function resolveDocumentViewIcon(label: string, viewId: string) {
  const normalized = `${label} ${viewId}`.toLocaleLowerCase()
  if (normalized.includes('json')) {
    return {
      fallbackIcon: 'braces',
      id: viewChromeIconIds.viewJson,
    }
  }

  if (normalized.includes('type')) {
    return {
      fallbackIcon: 'box',
      id: viewChromeIconIds.viewTypes,
    }
  }

  if (normalized.includes('structure')) {
    return {
      fallbackIcon: 'list-tree',
      id: viewChromeIconIds.viewStructure,
    }
  }

  return {
    fallbackIcon: 'braces',
    id: viewChromeIconIds.viewDefault,
  }
}

function renderViewChromeIcon({
  className = 'size-3.5',
  fallbackIcon,
  icon,
  module,
}: {
  readonly className?: string
  readonly fallbackIcon: string
  readonly icon: string
  readonly module?: PresentationModuleDefinition | null
}) {
  return renderPresentationIcon({
    className,
    icon,
    module,
  }) ?? renderPresentationIcon({
    className,
    icon: fallbackIcon,
  })
}
