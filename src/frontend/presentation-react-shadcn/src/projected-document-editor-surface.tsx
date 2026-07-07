import { Fragment, useMemo, type CSSProperties, type ReactNode } from 'react'

import {
  findPresentationView,
  formatPresentationValue,
  isViewChromeSlotKind,
  documentWorkspaceSurfaceSlotRoleKeys,
  projectDocumentWorkspaceSurfaceSlotDiagnostics,
  resolveDocumentWorkspaceSurfaceSlotRoleKey,
  resolveDocumentWorkspaceSurfaceSlots,
  resolvePresentationContent,
  resolvePresentationValue,
  type DocumentEditorLayout,
  type LayoutNodeDefinition,
  type PresentationDataSourceResolver,
  type ProjectionDefinition,
  type ResolvedDocumentWorkspaceSurfaceSlot,
  type ViewChromeSlotDefinition,
  type ViewChromeSlotKind,
  type ViewDefinition,
  type WorkspaceDefinition,
  type WorkspaceLayoutDefinition,
} from '@cohesivesystems/presentation-core'
import {
  type PresentationActionGroupOptions,
} from './presentation-action-group'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type {
  PresentationDesignSystem,
} from '@cohesivesystems/presentation-tailwind'
import type { ProjectedMetricValue } from './projected-metric-strip'
import {
  resolvePresentationRoutedSurfaceClassName,
  resolvePresentationRoutedSurfaceContentClassName,
} from '@cohesivesystems/presentation-tailwind'
import {
  layoutNodeKindLabels,
  layoutNodeKinds,
  layoutOrientationLabels,
  layoutOrientations,
  viewChromeSlotKinds,
  viewRegionKindLabels,
  viewRegionKinds,
} from '@cohesivesystems/presentation-contracts'
import type {
  PresentationBadgeTargetInterpreterRegistry,
} from './presentation-badge-target-interpreter'
import {
  renderStandardViewChromeSlot,
  type ProjectedViewChromeSlotRenderContext,
} from './standard-view-chrome-slot-renderers'
import {
  mergeDocumentWorkspaceSurfaceSlotRendererRegistries,
  projectDocumentWorkspaceSurfaceSlotRendererDiagnostics,
  resolveDocumentWorkspaceSurfaceSlotRenderer,
  standardDocumentWorkspaceSurfaceSlotRenderers,
  type DocumentWorkspaceSurfaceSlotRendererRegistry,
  type DocumentWorkspaceSurfaceSlotRenderContext,
} from './document-workspace-surface-slot-renderers'
import {
  usePresentationModule,
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesivesystems/presentation-react'

export type {
  DocumentEditorLayout,
} from '@cohesivesystems/presentation-core'

type ProjectedDocumentModuleLike = ReturnType<typeof usePresentationModule>

export interface ProjectedDocumentEditorSurfaceProps<TActionContext> {
  readonly actionContext: TActionContext
  readonly actionGroupOptions?: PresentationActionGroupOptions<TActionContext>
  readonly activeLayoutModeId?: string | null
  readonly activeViewId: string
  readonly children?: ReactNode
  readonly className?: string
  readonly componentSystem: PresentationComponentSystem
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly documentViewIds?: readonly string[]
  readonly fallbackDescription: string
  readonly fallbackTitle: string
  readonly fallbackViewIds: readonly string[]
  readonly headerContent?: ReactNode
  readonly layout: DocumentEditorLayout
  readonly metadataBadgeInterpreters?: PresentationBadgeTargetInterpreterRegistry
  readonly metadataEntityReferenceRole?: string | null
  readonly metricMessage?: ReactNode
  readonly metricValues?: Readonly<Record<string, ProjectedMetricValue>>
  readonly onActiveViewIdChange: (viewId: string) => void
  readonly onLayoutChange: (layout: DocumentEditorLayout) => void
  readonly pageViewId: string
  readonly projections?: readonly ProjectionDefinition[]
  readonly renderDocumentView: (viewId: string) => ReactNode
  readonly renderDocumentViewState?: (content: ReactNode) => ReactNode
  readonly renderViewChromeSlot?: (
    context: ProjectedViewChromeSlotRenderContext<TActionContext>,
  ) => ReactNode
  readonly readMetadataBadgeRole?:
    ProjectedViewChromeSlotRenderContext<TActionContext>['readMetadataBadgeRole']
  readonly readMetadataFieldRole?:
    ProjectedViewChromeSlotRenderContext<TActionContext>['readMetadataFieldRole']
  readonly resource: unknown
  readonly surfaceSlotRenderers?: DocumentWorkspaceSurfaceSlotRendererRegistry<TActionContext>
  readonly workspace?: WorkspaceDefinition | null
  readonly workspaceLayout?: WorkspaceLayoutDefinition | null
}

const emptyMetricValues: Readonly<Record<string, ProjectedMetricValue>> = {}

export function ProjectedDocumentEditorSurface<TActionContext>({
  actionContext,
  actionGroupOptions,
  activeLayoutModeId,
  activeViewId,
  children,
  className,
  componentSystem,
  dataSourceResolver,
  designSystem,
  documentViewIds: documentViewIdsOverride,
  fallbackDescription,
  fallbackTitle,
  fallbackViewIds,
  headerContent,
  layout,
  metadataBadgeInterpreters,
  metadataEntityReferenceRole,
  metricMessage,
  metricValues = emptyMetricValues,
  onActiveViewIdChange,
  onLayoutChange,
  pageViewId,
  projections = [],
  renderDocumentView,
  renderDocumentViewState,
  renderViewChromeSlot = renderStandardViewChromeSlot,
  readMetadataBadgeRole,
  readMetadataFieldRole,
  resource,
  surfaceSlotRenderers: surfaceSlotRendererOverrides,
  workspace,
  workspaceLayout,
}: ProjectedDocumentEditorSurfaceProps<TActionContext>) {
  const module = usePresentationModule()
  const pageView = findPresentationView(module, pageViewId)
  const surfaceSlots = useMemo(
    () => resolveDocumentWorkspaceSurfaceSlots(module, pageView, workspace),
    [module, pageView, workspace],
  )
  const headerSlot = surfaceSlots.header
  const primarySurfaceSlot = surfaceSlots.primarySurface
  const headerView = headerSlot.view
  const workspaceView = primarySurfaceSlot.view
  const surfaceSlotRenderers = useMemo(
    () => mergeDocumentWorkspaceSurfaceSlotRendererRegistries(
      standardDocumentWorkspaceSurfaceSlotRenderers,
      surfaceSlotRendererOverrides,
    ),
    [surfaceSlotRendererOverrides],
  )
  const renderedSurfaceSlots = useMemo(
    () => surfaceSlots.all.filter((slot) =>
      Boolean(resolveDocumentWorkspaceSurfaceSlotRenderer(slot, surfaceSlotRenderers))),
    [surfaceSlotRenderers, surfaceSlots],
  )
  const surfaceSlotDiagnostics = useMemo(
    () => {
      const sourceId = `document-workspace-surface-slots:${pageView?.Id ?? pageViewId}`
      return [
        ...projectDocumentWorkspaceSurfaceSlotDiagnostics({
          declaredSlots: surfaceSlots.all,
          pageView,
          renderedSlots: renderedSurfaceSlots,
          sourceId,
        }),
        ...projectDocumentWorkspaceSurfaceSlotRendererDiagnostics({
          renderers: surfaceSlotRenderers,
          slots: surfaceSlots.all,
          sourceId,
        }),
      ]
    },
    [
      pageView,
      pageViewId,
      renderedSurfaceSlots,
      surfaceSlotRenderers,
      surfaceSlots,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    `document-workspace-surface-slots:${pageView?.Id ?? pageViewId}`,
    surfaceSlotDiagnostics,
  )
  const headerChrome = headerView?.Chrome ?? null
  const workspaceChrome = workspaceView?.Chrome ?? null
  const headerChromeContent = resolvePresentationContent(headerChrome?.Content, resource)
  const workspaceChromeContent = resolvePresentationContent(workspaceChrome?.Content, resource)
  const viewSwitchSlot = findChromeSlot(workspaceView, viewChromeSlotKinds.viewSwitch)
  const documentViewIds =
    documentViewIdsOverride && documentViewIdsOverride.length > 0
      ? documentViewIdsOverride
      : resolveDocumentViewIds(viewSwitchSlot, fallbackViewIds)
  const resolvedActiveViewId = documentViewIds.includes(activeViewId)
    ? activeViewId
    : documentViewIds[0]
  const headerTitle =
    headerChromeContent.title ??
    formatResolvedPresentationValue(resolvePresentationValue(headerChrome?.Title, resource)) ??
    fallbackTitle
  const headerDescription =
    headerChromeContent.description ??
    headerChromeContent.subtitle ??
    formatResolvedPresentationValue(resolvePresentationValue(headerChrome?.Subtitle, resource)) ??
    fallbackDescription
  const workspaceTitle =
    workspaceChromeContent.title ??
    formatResolvedPresentationValue(resolvePresentationValue(workspaceChrome?.Title, resource)) ??
    workspaceView?.Name ??
    'Document'
  const pageClassName = resolvePresentationRoutedSurfaceClassName(
    pageView?.Design,
    designSystem,
  )
  const contentClassName = resolveDocumentWorkspaceContentClassName(
    pageView?.Design,
    designSystem,
  )
  const documentContent = renderDocumentWorkspaceContent({
    activeLayoutModeId,
    activeViewId: resolvedActiveViewId,
    componentSystem,
    documentViewIds,
    layout,
    module,
    projections,
    renderDocumentView,
    workspaceLayout,
    workspaceView,
  })
  const renderChromeSlot = (
    slot: ViewChromeSlotDefinition,
    view: ViewDefinition | null,
  ) =>
    renderViewChromeSlot({
      actionContext,
      actionGroupOptions,
      activeViewId: resolvedActiveViewId,
      componentSystem,
      dataSourceResolver,
      designSystem,
      documentViewIds,
      badgeInterpreters: resolveDocumentChromeSlotBadgeInterpreters(slot, {
        metadataBadgeInterpreters,
      }),
      layout,
      metadataEntityReferenceRole,
      metricMessage: isViewChromeSlotKind(slot, viewChromeSlotKinds.metricStrip)
        ? metricMessage
        : undefined,
      metricValues,
      module,
      onActiveViewIdChange,
      onLayoutChange,
      readMetadataBadgeRole,
      readMetadataFieldRole,
      resource,
      slot,
      view,
      workspaceView,
    })

  const surfaceSlotRenderContext = {
    actionContext,
    auxiliaryContent: children,
    componentSystem,
    documentContent,
    headerContent,
    headerDescription,
    headerTitle,
    pageView,
    pageViewId,
    renderChromeSlot,
    renderDocumentViewState,
    workspaceTitle,
    workspaceView,
  } satisfies Omit<DocumentWorkspaceSurfaceSlotRenderContext<TActionContext>, 'slot'>
  const renderedSlotNodes = surfaceSlots.all.flatMap((slot) => {
    const renderer = resolveDocumentWorkspaceSurfaceSlotRenderer(
      slot,
      surfaceSlotRenderers,
    )
    if (!renderer?.renderer) {
      return []
    }

    return [{
      id: slot.id,
      node: (
        <Fragment key={slot.id}>
          {renderer.renderer({
            ...surfaceSlotRenderContext,
            slot,
          })}
        </Fragment>
      ),
      roleKey: resolveDocumentWorkspaceSurfaceSlotRoleKey(slot.role, slot.roleLabel),
      slot,
    }]
  })
  const headerSlotNode = renderedSlotNodes.find((slot) =>
    slot.roleKey === documentWorkspaceSurfaceSlotRoleKeys.header)
  const primarySurfaceSlotNode = renderedSlotNodes.find((slot) =>
    slot.roleKey === documentWorkspaceSurfaceSlotRoleKeys.primarySurface)
  const auxiliarySlotNode = hasRenderableNode(children)
    ? renderedSlotNodes.find((slot) =>
        slot.roleKey === documentWorkspaceSurfaceSlotRoleKeys.auxiliary)
    : undefined
  const shouldRenderAuxiliarySidecar =
    primarySurfaceSlotNode &&
    auxiliarySlotNode &&
    isDocumentWorkspaceAuxiliarySidecarSlot(auxiliarySlotNode.slot)
  const groupedSlotIds = new Set(
    [headerSlotNode, primarySurfaceSlotNode, auxiliarySlotNode]
      .flatMap((slot) => (slot ? [slot.id] : [])),
  )
  const remainingSlotNodes = renderedSlotNodes.filter((slot) => !groupedSlotIds.has(slot.id))

  return componentSystem.documentWorkspaces.DocumentWorkspaceShell({
    className: cn(pageClassName, className),
    contentClassName,
    viewId: pageView?.Id ?? pageViewId,
    children: (
      <div className="grid min-h-0 gap-5">
        {headerSlotNode?.node}
        {shouldRenderAuxiliarySidecar ? (
          <div className="grid min-h-0 gap-y-5 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-start lg:gap-x-0 lg:has-[>aside:not(:empty)]:gap-x-5">
            <div className="min-w-0">{primarySurfaceSlotNode.node}</div>
            <aside className="min-w-0 empty:hidden lg:sticky lg:top-20 lg:w-[clamp(18rem,25vw,24rem)]">
              {auxiliarySlotNode.node}
            </aside>
          </div>
        ) : (
          <>
            {primarySurfaceSlotNode?.node}
            {auxiliarySlotNode?.node}
          </>
        )}
        {remainingSlotNodes.map((slot) => slot.node)}
      </div>
    ),
  })
}

function findChromeSlot(
  view: ViewDefinition | null,
  slotKind: ViewChromeSlotKind | string | number,
) {
  return view?.Chrome?.Slots?.find((slot) => isViewChromeSlotKind(slot, slotKind)) ?? null
}

function resolveDocumentViewIds(
  slot: ViewChromeSlotDefinition | null,
  fallbackViewIds: readonly string[],
) {
  return slot?.ViewIds && slot.ViewIds.length > 0 ? slot.ViewIds : fallbackViewIds
}

function resolveDocumentChromeSlotBadgeInterpreters(
  slot: ViewChromeSlotDefinition,
  {
    metadataBadgeInterpreters,
  }: {
    readonly metadataBadgeInterpreters?: PresentationBadgeTargetInterpreterRegistry
  },
) {
  if (isViewChromeSlotKind(slot, viewChromeSlotKinds.badgeStrip)) {
    return metadataBadgeInterpreters
  }

  return undefined
}

function renderDocumentWorkspaceContent({
  activeLayoutModeId,
  activeViewId,
  componentSystem,
  documentViewIds,
  layout,
  module,
  projections,
  renderDocumentView,
  workspaceLayout,
  workspaceView,
}: {
  readonly activeLayoutModeId?: string | null
  readonly activeViewId: string
  readonly componentSystem: PresentationComponentSystem
  readonly documentViewIds: readonly string[]
  readonly layout: DocumentEditorLayout
  readonly module: ProjectedDocumentModuleLike | null
  readonly projections: readonly ProjectionDefinition[]
  readonly renderDocumentView: (viewId: string) => ReactNode
  readonly workspaceLayout?: WorkspaceLayoutDefinition | null
  readonly workspaceView: ViewDefinition | null
}) {
  const layoutMode = resolveDocumentWorkspaceLayoutMode({
    activeLayoutModeId,
    layout,
    workspaceLayout,
  })
  if (layoutMode?.Root) {
    return renderDocumentWorkspaceLayoutNode(layoutMode.Root, {
      activeViewId,
      componentSystem,
      documentViewIds,
      module,
      projections,
      renderDocumentView,
      workspaceView,
    })
  }

  if (layout !== 'split') {
    return renderDocumentView(activeViewId)
  }

  const jsonViewId = findJsonViewId(module, documentViewIds) ?? documentViewIds[0]
  const secondaryViewId =
    activeViewId !== jsonViewId
      ? activeViewId
      : documentViewIds.find((viewId) => viewId !== jsonViewId)

  if (!jsonViewId || !secondaryViewId) {
    return renderDocumentView(activeViewId)
  }

  return componentSystem.documentWorkspaces.DocumentWorkspaceLayoutGroup({
    className: 'grid min-h-0 items-stretch gap-4 lg:grid-cols-2',
    orientation: layoutOrientations.horizontal,
    workspaceViewId: workspaceView?.Id ?? null,
    children: (
      <>
        {renderDocumentWorkspacePane({
          children: renderDocumentView(jsonViewId),
          componentSystem,
          title: getDocumentViewLabel(module, workspaceView, jsonViewId),
          viewId: jsonViewId,
          workspaceView,
        })}
        {renderDocumentWorkspacePane({
          children: renderDocumentView(secondaryViewId),
          componentSystem,
          title: getDocumentViewLabel(module, workspaceView, secondaryViewId),
          viewId: secondaryViewId,
          workspaceView,
        })}
      </>
    ),
  })
}

interface DocumentWorkspaceLayoutRenderContext {
  readonly activeViewId: string
  readonly componentSystem: PresentationComponentSystem
  readonly documentViewIds: readonly string[]
  readonly module: ProjectedDocumentModuleLike | null
  readonly projections: readonly ProjectionDefinition[]
  readonly renderDocumentView: (viewId: string) => ReactNode
  readonly workspaceView: ViewDefinition | null
}

function renderDocumentWorkspaceLayoutNode(
  node: LayoutNodeDefinition,
  context: DocumentWorkspaceLayoutRenderContext,
): ReactNode {
  if (isLayoutNodeKind(node.Kind, layoutNodeKinds.splitGroup)) {
    return renderDocumentWorkspaceSplitGroup(node, context)
  }

  if (isLayoutNodeKind(node.Kind, layoutNodeKinds.tabGroup)) {
    const selectedChild = selectLayoutTabChild(node, context)
    return selectedChild ? renderDocumentWorkspaceLayoutNode(selectedChild, context) : null
  }

  const viewId = resolveLayoutNodeViewId(node, context)
  if (viewId) {
    return context.renderDocumentView(viewId)
  }

  const firstChild = node.Children?.[0]
  return firstChild ? renderDocumentWorkspaceLayoutNode(firstChild, context) : null
}

function renderDocumentWorkspaceSplitGroup(
  node: LayoutNodeDefinition,
  context: DocumentWorkspaceLayoutRenderContext,
) {
  const children = (node.Children ?? []).filter((child) =>
    Boolean(resolveLayoutNodeViewId(child, context) ?? child.Children?.length),
  )
  if (children.length === 0) {
    return null
  }

  const isHorizontal = isLayoutOrientation(node.Orientation, layoutOrientations.horizontal)
  const gridStyle = createSplitGroupGridStyle(children, isHorizontal)

  return (
    context.componentSystem.documentWorkspaces.DocumentWorkspaceLayoutGroup({
      className: cn(
        'grid min-h-0 items-stretch gap-4',
        isHorizontal
          ? 'lg:grid-cols-[var(--document-workspace-layout-columns)]'
          : 'grid-rows-[var(--document-workspace-layout-rows)]',
      ),
      layoutNodeId: node.Id,
      orientation: node.Orientation,
      style: gridStyle,
      workspaceViewId: context.workspaceView?.Id ?? null,
      children: children.map((child) => (
        <Fragment key={child.Id}>
          {renderDocumentWorkspacePane({
            children: renderDocumentWorkspaceLayoutNode(child, context),
            componentSystem: context.componentSystem,
            layoutNodeId: child.Id,
            title: resolveLayoutNodeTitle(child, context),
            viewId: resolveLayoutNodeViewId(child, context),
            workspaceView: context.workspaceView,
          })}
        </Fragment>
      )),
    })
  )
}

function resolveDocumentWorkspaceLayoutMode({
  activeLayoutModeId,
  layout,
  workspaceLayout,
}: {
  readonly activeLayoutModeId?: string | null
  readonly layout: DocumentEditorLayout
  readonly workspaceLayout?: WorkspaceLayoutDefinition | null
}) {
  const modes = workspaceLayout?.Modes ?? []
  const requestedModeId =
    activeLayoutModeId ??
    (layout === 'split' ? 'split' : workspaceLayout?.DefaultModeId ?? 'tabs')

  return (
    modes.find((mode) => mode.Id === requestedModeId) ??
    modes.find((mode) => mode.Id === workspaceLayout?.DefaultModeId) ??
    modes[0] ??
    null
  )
}

function selectLayoutTabChild(
  node: LayoutNodeDefinition,
  context: DocumentWorkspaceLayoutRenderContext,
) {
  const children = node.Children ?? []
  return (
    children.find((child) => {
      const viewIds = resolveLayoutNodeViewIds(child, context)
      return viewIds.includes(context.activeViewId)
    }) ??
    children.find((child) => {
      const viewId = resolveLayoutNodeViewId(child, context)
      return viewId && context.documentViewIds.includes(viewId)
    }) ??
    children[0] ??
    null
  )
}

function resolveLayoutNodeTitle(
  node: LayoutNodeDefinition,
  context: DocumentWorkspaceLayoutRenderContext,
) {
  const selectedNode = isLayoutNodeKind(node.Kind, layoutNodeKinds.tabGroup)
    ? selectLayoutTabChild(node, context) ?? node
    : node
  const viewId = resolveLayoutNodeViewId(selectedNode, context)
  return viewId
    ? getDocumentViewLabel(context.module, context.workspaceView, viewId)
    : selectedNode.Id
}

function resolveLayoutNodeViewId(
  node: LayoutNodeDefinition,
  context: DocumentWorkspaceLayoutRenderContext,
) {
  return resolveLayoutNodeViewIds(node, context)[0] ?? null
}

function resolveLayoutNodeViewIds(
  node: LayoutNodeDefinition,
  context: DocumentWorkspaceLayoutRenderContext,
) {
  const projectionViewIds = (node.ProjectionIds ?? [])
    .map((projectionId) => {
      const projection = context.projections.find(
        (candidate) => candidate.Id === projectionId,
      )
      return projection?.ViewId ?? projection?.Id ?? null
    })
    .filter((viewId): viewId is string => Boolean(viewId))

  return [...projectionViewIds, ...(node.ViewIds ?? [])]
}

function createSplitGroupGridStyle(
  children: readonly LayoutNodeDefinition[],
  isHorizontal: boolean,
) {
  if (children.length <= 1) {
    return undefined
  }

  const trackSizes = children.map((child) => {
    const fraction = typeof child.Size === 'number' && child.Size > 0
      ? child.Size
      : 1
    return `minmax(0, ${fraction}fr)`
  })

  return isHorizontal
    ? ({
        '--document-workspace-layout-columns': trackSizes.join(' '),
      } as CSSProperties)
    : ({
        '--document-workspace-layout-rows': trackSizes.join(' '),
      } as CSSProperties)
}

function renderDocumentWorkspacePane({
  children,
  componentSystem,
  layoutNodeId,
  title,
  viewId,
  workspaceView,
}: {
  readonly children: ReactNode
  readonly componentSystem: PresentationComponentSystem
  readonly layoutNodeId?: string | null
  readonly title: string
  readonly viewId?: string | null
  readonly workspaceView: ViewDefinition | null
}) {
  return componentSystem.documentWorkspaces.DocumentWorkspaceLayoutPane({
    children,
    layoutNodeId,
    title,
    viewId,
    workspaceViewId: workspaceView?.Id ?? null,
  })
}

function findJsonViewId(
  module: ProjectedDocumentModuleLike | null,
  viewIds: readonly string[],
) {
  return (
    viewIds.find((viewId) => {
      const view = findPresentationView(module, viewId)
      const label = `${view?.Name ?? ''} ${viewId}`.toLowerCase()
      return label.includes('json')
    }) ?? null
  )
}

function getDocumentViewLabel(
  module: ProjectedDocumentModuleLike | null,
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

function formatResolvedPresentationValue(value: unknown) {
  return value === null || value === undefined ? null : formatPresentationValue(value)
}

function isLayoutNodeKind(
  value: string | number,
  expected: keyof typeof layoutNodeKindLabels,
) {
  return isEnumDiscriminator(value, expected, layoutNodeKindLabels[expected])
}

function isLayoutOrientation(
  value: string | number,
  expected: keyof typeof layoutOrientationLabels,
) {
  return isEnumDiscriminator(value, expected, layoutOrientationLabels[expected])
}

function isEnumDiscriminator(
  value: string | number,
  expected: string | number,
  expectedLabel: string,
) {
  return (
    value === expected ||
    normalizeEnumDiscriminator(value) === normalizeEnumDiscriminator(expectedLabel)
  )
}

function normalizeEnumDiscriminator(value: string | number) {
  return String(value).replace(/[-_\s]/g, '').toLocaleLowerCase()
}

function resolveDocumentWorkspaceContentClassName(
  design: ViewDefinition['Design'] | null | undefined,
  designSystem: PresentationDesignSystem,
) {
  return cn(
    withoutTailwindMaxWidthClassNames(
      resolvePresentationRoutedSurfaceContentClassName(design, designSystem),
    ),
    'w-full max-w-none',
  )
}

function withoutTailwindMaxWidthClassNames(className: string) {
  return className
    .split(/\s+/)
    .filter((token) => token.length > 0 && !isTailwindMaxWidthClassName(token))
    .join(' ')
}

function isTailwindMaxWidthClassName(token: string) {
  const utilityName = token.slice(token.lastIndexOf(':') + 1)
  return utilityName.startsWith('max-w-')
}

function isDocumentWorkspaceAuxiliarySidecarSlot(
  slot: ResolvedDocumentWorkspaceSurfaceSlot,
) {
  return slot.descriptor.regionKind !== null &&
    slot.descriptor.regionKind !== undefined &&
    (isEnumDiscriminator(
      slot.descriptor.regionKind,
      viewRegionKinds.sidebar,
      viewRegionKindLabels[viewRegionKinds.sidebar],
    ) ||
      isEnumDiscriminator(
        slot.descriptor.regionKind,
        viewRegionKinds.panel,
        viewRegionKindLabels[viewRegionKinds.panel],
      ) ||
      isEnumDiscriminator(
        slot.descriptor.regionKind,
        viewRegionKinds.inspector,
        viewRegionKindLabels[viewRegionKinds.inspector],
      ))
}

function hasRenderableNode(node: ReactNode): boolean {
  if (
    node === null ||
    node === undefined ||
    typeof node === 'boolean'
  ) {
    return false
  }

  if (Array.isArray(node)) {
    return node.some(hasRenderableNode)
  }

  return true
}

function cn(...values: readonly (false | null | string | undefined)[]) {
  return values.filter(Boolean).join(' ')
}
