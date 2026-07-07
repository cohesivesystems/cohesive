import type { ColumnDef } from '@tanstack/react-table'
import type {
  PresentationCollectionDetailLayoutMode,
} from './presentation-component-groups'
import { Fragment, useCallback, useEffect, useMemo, type ReactNode } from 'react'

import {
  collectionChromeIconIds,
  createPresentationTestAttributes,
  defaultPresentationComponentSet,
  resolveCollectionChromeIconSubjects,
} from '@cohesive/presentation-core'
import {
  findPresentationView,
  isCollectionChromeSlotKind,
  isCollectionChromeSlotPlacement,
  isCollectionChromeSlotPlacementValue,
  projectPresentationActionRuntimeRegistry,
  projectProjectedCollectionActionRuntimeBindings,
  type CollectionChromeSlotDefinition,
  type CollectionChromeSlotPlacement,
  type FieldPresentationDefinition,
  type NavigationRouteParameters,
  type PresentationActionRuntimeBinding,
  type ViewDefinition,
} from '@cohesive/presentation-core'
import {
  projectProjectedCollectionActionRuntimeDiagnostics,
} from './projected-collection-action-runtime-diagnostics'
import {
  projectPresentationIconDiagnostics,
} from './presentation-icon-diagnostics'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  useCollectionSelectionState,
  usePresentationModule,
  usePresentationNavigationRuntime,
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesive/presentation-react'
import {
  renderPresentationIcon,
} from './presentation-icon-registry'
import {
  renderProjectedFieldValue,
} from './projected-field-value-rendering'
import {
  ProjectedRecordDetails,
  type ProjectedRecordFieldRenderContext,
} from './projected-record-details'
import {
  createProjectedCollectionRuntime,
  isCollectionSelectionEnabled,
  resolveProjectedCollectionSelectionMode,
  resolveProjectedCollectionSelectionStateId,
  type ProjectedCollectionActionExecutionContext,
  type ProjectedCollectionActionRuntimeRegistry,
  type ProjectedCollectionPaginationInput,
  type ProjectedActionPlacementLike,
  type ProjectedCollectionRuntime,
  type ResolvedProjectedCollectionRowAction,
  type ResolvedProjectedCollectionSelectionAction,
} from '@cohesive/presentation-core'
import {
  collectionChromeSlotKinds,
  collectionChromeSlotPlacements,
} from '@cohesive/presentation-contracts'

export type {
  ProjectedActionPlacementLike,
  ProjectedRowActionExecutionContext,
  ProjectedSelectionActionExecutionContext,
} from '@cohesive/presentation-core'

type ProjectedFieldDefinitionLike = FieldPresentationDefinition
type ProjectedViewDefinitionLike = ViewDefinition
type ProjectedModuleDefinitionLike = NonNullable<ReturnType<typeof usePresentationModule>>

export interface ProjectedFieldRenderContext<TData extends object> {
  readonly field: ProjectedFieldDefinitionLike
  readonly row: TData
  readonly value: unknown
}

export interface ProjectedCollectionViewProps<TData extends object> {
  readonly actionRuntimeBindings?: readonly PresentationActionRuntimeBinding<
    ProjectedCollectionActionExecutionContext<TData>,
    ReactNode
  >[]
  readonly createHref?: (
    routeId: string,
    parameters?: NavigationRouteParameters,
  ) => string | null
  readonly componentSet?: string
  readonly data: readonly TData[]
  readonly emptyMessage: string
  readonly componentSystem: PresentationComponentSystem
  readonly fieldRenderers?: Record<
    string,
    (context: ProjectedFieldRenderContext<TData>) => ReactNode
  >
  readonly footer?:
    | ReactNode
    | ((runtime: ProjectedCollectionRuntime<TData>) => ReactNode)
  readonly detailFieldRenderers?: Record<
    string,
    (context: ProjectedCollectionDetailFieldRenderContext<TData>) => ReactNode
  >
  readonly getRowLabel?: (row: TData) => string
  readonly navigateHref?: (href: string) => void
  readonly pagination?: ProjectedCollectionPaginationInput | null
  readonly renderActionIcon?: (placement: ProjectedActionPlacementLike) => ReactNode
  readonly renderChromeSlot?: (
    context: ProjectedCollectionChromeSlotRenderContext<TData>,
  ) => ReactNode
  readonly viewId: string
}

export interface ProjectedCollectionChromeSlotRenderContext<TData extends object> {
  readonly actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>
  readonly canExecuteAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => boolean
  readonly collectionRuntime: ProjectedCollectionRuntime<TData>
  readonly componentSet: string
  readonly componentSystem: PresentationComponentSystem
  readonly createHref?: (
    routeId: string,
    parameters?: NavigationRouteParameters,
  ) => string | null
  readonly executeAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => Promise<void> | void
  readonly module?: ProjectedModuleDefinitionLike | null
  readonly navigateHref?: (href: string) => void
  readonly renderActionIcon?: (placement: ProjectedActionPlacementLike) => ReactNode
  readonly renderBodySlot?: (slot: CollectionChromeSlotDefinition) => ReactNode
  readonly renderDetailSlot?: (slot: CollectionChromeSlotDefinition) => ReactNode
  readonly renderQueryFormSlot?: (slot: CollectionChromeSlotDefinition) => ReactNode
  readonly renderRowActionsSlot?: (
    slot: CollectionChromeSlotDefinition,
    row: TData,
    rowLabel: string | null,
  ) => ReactNode
  readonly renderSummarySlot?: (slot: CollectionChromeSlotDefinition) => ReactNode
  readonly row?: TData
  readonly rowLabel?: string | null
  readonly slot: CollectionChromeSlotDefinition
  readonly viewId: string
}

export function ProjectedCollectionView<TData extends object>({
  actionRuntimeBindings,
  componentSet = defaultPresentationComponentSet,
  componentSystem,
  createHref: createHrefOverride,
  data,
  detailFieldRenderers,
  emptyMessage,
  fieldRenderers,
  footer,
  getRowLabel,
  navigateHref: navigateHrefOverride,
  pagination,
  renderActionIcon,
  renderChromeSlot,
  viewId,
}: ProjectedCollectionViewProps<TData>) {
  const module = usePresentationModule()
  const navigation = usePresentationNavigationRuntime()
  const view = findPresentationView<ProjectedViewDefinitionLike>(module, viewId)
  const createHref = createHrefOverride ?? navigation.createHref
  const navigateHref = navigateHrefOverride ?? navigation.navigateHref
  const selectionMode = resolveProjectedCollectionSelectionMode(view?.Collection)
  const selectionStateId = resolveProjectedCollectionSelectionStateId(view?.Collection)
  const selectionState = useCollectionSelectionState(
    isCollectionSelectionEnabled(selectionMode)
      ? selectionStateId
      : null,
  )
  const collectionRuntime = useMemo(
    () => createProjectedCollectionRuntime({
      createHref,
      data,
      module,
      pagination,
      selectionState,
      view,
    }),
    [createHref, data, module, pagination, selectionState, view],
  )

  const getTableRowId = collectionRuntime.getRowId
  const isRowSelected = collectionRuntime.selection.isRowSelected
  const activatedRowAction = collectionRuntime.actions.activatedRowAction
  const collectionActionIds = useMemo(
    () =>
      [
        ...collectionRuntime.actions.rowActions.map(({ action }) => action.Id),
        ...collectionRuntime.actions.selectionActions.map(({ action }) => action.Id),
      ],
    [
      collectionRuntime.actions.rowActions,
      collectionRuntime.actions.selectionActions,
    ],
  )
  const actionRuntimes = useMemo(
    () =>
      projectPresentationActionRuntimeRegistry<
        ProjectedCollectionActionExecutionContext<TData>,
        ReactNode
      >({
        actionIds: collectionActionIds,
        module,
        projections: [
          ...(actionRuntimeBindings ?? []),
          ...projectProjectedCollectionActionRuntimeBindings<TData, ReactNode>({
            navigateHref,
          }),
        ],
      }) as ProjectedCollectionActionRuntimeRegistry<TData>,
    [
      actionRuntimeBindings,
      collectionActionIds,
      module,
      navigateHref,
    ],
  )
  const actionRuntimeDiagnostics = useMemo(
    () =>
      [
        ...projectProjectedCollectionActionRuntimeDiagnostics({
          actionRuntimes,
          collectionRuntime,
          module,
          view,
        }),
        ...projectPresentationIconDiagnostics({
          icons: resolveCollectionChromeIconSubjects(collectionRuntime.chrome),
          module,
          source: `projected-collection-chrome-icons.${viewId}`,
          surfaceId: view?.Id ?? viewId,
          surfaceName: view?.Name ?? viewId,
        }),
      ],
    [
      actionRuntimes,
      collectionRuntime,
      module,
      view,
      viewId,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    `projected-collection-action-runtime.${viewId}`,
    actionRuntimeDiagnostics,
  )
  const bodySlot = collectionRuntime.chrome.bodySlot
  const resolvedFooter =
    typeof footer === 'function' ? footer(collectionRuntime) : footer
  useEffect(() => {
    collectionRuntime.selection.pruneToVisibleRows()
  }, [collectionRuntime])
  const canExecuteAction = useCallback(
    (context: ProjectedCollectionActionExecutionContext<TData>) =>
      collectionRuntime.actions.canInvokeAction(context, actionRuntimes),
    [actionRuntimes, collectionRuntime],
  )
  const executeAction = useCallback(
    (context: ProjectedCollectionActionExecutionContext<TData>) =>
      collectionRuntime.actions.invokeAction(context, actionRuntimes),
    [actionRuntimes, collectionRuntime],
  )
  const shouldSelectOnRowClick = Boolean(
    bodySlot?.SelectOnRowClick && collectionRuntime.selection.state,
  )
  const handleRowClick = useCallback(
    (row: TData, index: number) => {
      if (shouldSelectOnRowClick) {
        collectionRuntime.selection.activateRow(row, index)
      }

      if (!bodySlot?.ActivateOnRowClick || !activatedRowAction) {
        return
      }

      const actionContext = collectionRuntime.actions.createRowActionContext(
        activatedRowAction,
        row,
      )
      if (canExecuteAction(actionContext)) {
        void executeAction(actionContext)
      }
    },
    [
      activatedRowAction,
      bodySlot?.ActivateOnRowClick,
      canExecuteAction,
      collectionRuntime,
      executeAction,
      shouldSelectOnRowClick,
    ],
  )
  const shouldHandleRowClick = Boolean(
    shouldSelectOnRowClick ||
      (bodySlot?.ActivateOnRowClick && activatedRowAction),
  )
  const renderDetailSlot = useCallback(
    (slot: CollectionChromeSlotDefinition) => createCollectionDetailProjection({
      componentSystem,
      componentSet,
      createHref,
      data: collectionRuntime.detail.data,
      emptyMessage: collectionRuntime.detail.emptyMessage,
      fieldRenderers: detailFieldRenderers,
      navigateHref,
      onClose: collectionRuntime.selection.state
        ? () => collectionRuntime.selection.state?.clearSelection()
        : undefined,
      title: collectionRuntime.detail.title,
      viewId: slot.DetailViewId ?? collectionRuntime.detail.viewId,
    }),
    [
      collectionRuntime.detail.data,
      collectionRuntime.detail.emptyMessage,
      collectionRuntime.detail.title,
      collectionRuntime.detail.viewId,
      collectionRuntime.selection.state,
      componentSet,
      componentSystem,
      createHref,
      detailFieldRenderers,
      navigateHref,
    ],
  )
  const renderRowActionsSlot = useCallback(
    (slot: CollectionChromeSlotDefinition, row: TData, rowLabel: string | null) => (
      <ProjectedCollectionRowActions
        canExecuteAction={canExecuteAction}
        collectionRuntime={collectionRuntime}
        componentSet={componentSet}
        componentSystem={componentSystem}
        executeAction={executeAction}
        module={module}
        renderActionIcon={renderActionIcon}
        row={row}
        rowLabel={rowLabel}
        slot={slot}
        viewId={viewId}
      />
    ),
    [
      canExecuteAction,
      collectionRuntime,
      componentSet,
      componentSystem,
      executeAction,
      module,
      renderActionIcon,
      viewId,
    ],
  )
  const columns = useMemo(
    () =>
      view
        ? createProjectedColumns({
            actionRuntimes,
            canExecuteAction,
            collectionRuntime,
            componentSet,
            componentSystem,
            createHref,
            executeAction,
            fieldRenderers,
            getRowLabel,
            module,
            navigateHref,
            renderChromeSlot,
            renderActionIcon,
            renderRowActionsSlot,
            viewId,
          })
        : [],
    [
      actionRuntimes,
      canExecuteAction,
      componentSystem,
      componentSet,
      createHref,
      executeAction,
      fieldRenderers,
      getRowLabel,
      collectionRuntime,
      module,
      navigateHref,
      renderChromeSlot,
      renderActionIcon,
      renderRowActionsSlot,
      view,
      viewId,
    ],
  )
  const chromeSlotContext = {
    actionRuntimes,
    canExecuteAction,
    collectionRuntime,
    componentSet,
    componentSystem,
    createHref,
    executeAction,
    module,
    navigateHref,
    renderActionIcon,
    renderDetailSlot,
    renderRowActionsSlot,
    viewId,
  } satisfies Omit<ProjectedCollectionChromeSlotRenderContext<TData>, 'slot'>
  const leadingChrome = renderCollectionChromeSlots({
    context: chromeSlotContext,
    placements: [
      collectionChromeSlotPlacements.header,
      collectionChromeSlotPlacements.toolbar,
      collectionChromeSlotPlacements.above,
    ],
    renderChromeSlot,
  })
  const footerChrome = renderCollectionChromeSlots({
    context: chromeSlotContext,
    placements: [collectionChromeSlotPlacements.footer],
    renderChromeSlot,
  })
  const renderBodySlot = () => (
    <ProjectedCollectionBody
      columns={columns}
      componentSystem={componentSystem}
      data={data}
      emptyMessage={emptyMessage}
      footer={footerChrome.length > 0 ? footerChrome : resolvedFooter}
      getRowId={getTableRowId}
      isRowSelected={isRowSelected}
      onRowClick={shouldHandleRowClick ? handleRowClick : undefined}
      viewId={viewId}
    />
  )
  const bodyChromeSlotContext = {
    ...chromeSlotContext,
    renderBodySlot,
  } satisfies Omit<ProjectedCollectionChromeSlotRenderContext<TData>, 'slot'>
  const bodyChrome = renderCollectionChromeSlots({
    context: bodyChromeSlotContext,
    includeSlot: (slot) => isCollectionChromeSlotKind(
      slot,
      collectionChromeSlotKinds.body,
    ),
    placements: [collectionChromeSlotPlacements.inline],
    renderChromeSlot,
    renderFallbackSlot: () => renderBodySlot(),
  })
  const collectionBody = bodyChrome.length > 0 ? bodyChrome : (
    <ProjectionStatusBlock
      label={
        `Presentation view '${view?.Name ?? viewId}' does not declare a collection Body chrome slot.`
      }
    />
  )
  const collectionDetailSlot = collectionRuntime.chrome.detailSlot
  const detailChrome = renderCollectionChromeSlots({
    context: chromeSlotContext,
    includeSlot: (slot) => isCollectionChromeSlotKind(
      slot,
      collectionChromeSlotKinds.detail,
    ),
    placements: [
      collectionChromeSlotPlacements.inline,
      collectionChromeSlotPlacements.sidePanel,
      collectionChromeSlotPlacements.drawer,
    ],
    renderChromeSlot,
    renderFallbackSlot: ({ slot }) => renderDetailSlot(slot),
  })
  const collectionDetail = detailChrome.length > 0 ? detailChrome : null
  const table = (
    <div className="grid w-full min-w-0 gap-4">
      {leadingChrome}
      {collectionBody}
    </div>
  )

  if (!module) {
    return <ProjectionStatusBlock label="Presentation module is not available." />
  }

  if (!view) {
    return <ProjectionStatusBlock label={`Presentation view '${viewId}' is not available.`} />
  }

  if (!collectionRuntime.chrome.bodySlot) {
    return (
      <ProjectionStatusBlock
        label={`Presentation view '${view.Name}' does not declare a collection Body chrome slot.`}
      />
    )
  }

  if (columns.length === 0) {
    return <ProjectionStatusBlock label={`Presentation view '${view.Name}' has no projected fields.`} />
  }

  return (
    <ProjectedCollectionDetailLayout
      componentSystem={componentSystem}
      detail={collectionDetail}
      placement={collectionDetailSlot?.Placement ?? collectionChromeSlotPlacements.none}
      table={table}
    />
  )
}

export type ProjectedCollectionDetailFieldRenderContext<TData extends object> =
  ProjectedRecordFieldRenderContext<TData>

export function ProjectedCollectionBody<TData extends object>({
  columns,
  componentSystem,
  data,
  emptyMessage,
  footer,
  getRowId,
  isRowSelected,
  onRowClick,
  viewId,
}: {
  readonly columns: readonly ColumnDef<TData>[]
  readonly componentSystem: PresentationComponentSystem
  readonly data: readonly TData[]
  readonly emptyMessage: string
  readonly footer?: ReactNode
  readonly getRowId?: (row: TData, index: number) => string
  readonly isRowSelected?: (row: TData, index: number) => boolean
  readonly onRowClick?: (row: TData, index: number) => void
  readonly viewId: string
}) {
  return componentSystem.collections.DataTable({
    columns,
    data,
    emptyMessage,
    footer,
    getRowId,
    isRowSelected,
    onRowClick,
    viewId,
  })
}

function ProjectedCollectionDetail<TData extends object>({
  componentSet,
  componentSystem,
  createHref,
  data,
  emptyMessage,
  fieldRenderers,
  navigateHref,
  onClose,
  title,
  viewId,
}: {
  readonly componentSet: string
  readonly componentSystem: PresentationComponentSystem
  readonly createHref?: (
    routeId: string,
    parameters?: NavigationRouteParameters,
  ) => string | null
  readonly data: TData | null
  readonly emptyMessage?: string | null
  readonly fieldRenderers?: Record<
    string,
    (context: ProjectedCollectionDetailFieldRenderContext<TData>) => ReactNode
  >
  readonly navigateHref?: (href: string) => void
  readonly onClose?: () => void
  readonly title?: string | null
  readonly viewId: string
}) {
  const module = usePresentationModule()
  const detailView = findPresentationView<ProjectedViewDefinitionLike>(module, viewId)
  const ActionButton = componentSystem.actions.ActionButton

  return (
    <section className="grid gap-2">
      <div className="flex items-center justify-between gap-2">
        {detailView || title ? (
          <h2 className="text-sm font-semibold text-slate-800">{title ?? detailView?.Name}</h2>
        ) : <span />}
        {onClose ? (
          <ActionButton
            aria-label="Close details"
            onClick={onClose}
            size="icon-sm"
            type="button"
            variant="ghost"
          >
            {renderPresentationIcon({
              className: 'size-4',
              componentSet,
              icon: collectionChromeIconIds.detailClose,
              module,
            })}
          </ActionButton>
        ) : null}
      </div>
      {data ? (
        <ProjectedRecordDetails
          componentSystem={componentSystem}
          createHref={createHref}
          data={data}
          fieldRenderers={fieldRenderers}
          navigateHref={navigateHref}
          viewId={viewId}
        />
      ) : (
        <ProjectionStatusBlock label={emptyMessage ?? 'Select a row to inspect it.'} />
      )}
    </section>
  )
}

function ProjectedCollectionDetailLayout({
  componentSystem,
  detail,
  placement,
  table,
}: {
  readonly componentSystem: PresentationComponentSystem
  readonly detail: ReactNode
  readonly placement: CollectionChromeSlotPlacement | null | undefined
  readonly table: ReactNode
}) {
  const CollectionDetailLayout = componentSystem.collections.CollectionDetailLayout
  return (
    <CollectionDetailLayout
      detail={detail}
      mode={resolveCollectionDetailLayoutMode(placement)}
      table={table}
    />
  )
}

function resolveCollectionDetailLayoutMode(
  placement: CollectionChromeSlotPlacement | null | undefined,
): PresentationCollectionDetailLayoutMode {
  if (
    isCollectionChromeSlotPlacementValue(
      placement,
      collectionChromeSlotPlacements.none,
    )
  ) {
    return 'none'
  }

  if (
    isCollectionChromeSlotPlacementValue(
      placement,
      collectionChromeSlotPlacements.sidePanel,
    )
  ) {
    return 'side-panel'
  }

  if (
    isCollectionChromeSlotPlacementValue(
      placement,
      collectionChromeSlotPlacements.drawer,
    )
  ) {
    return 'drawer'
  }

  return 'stack'
}

function createCollectionDetailProjection<TData extends object>({
  componentSet,
  componentSystem,
  createHref,
  data,
  emptyMessage,
  fieldRenderers,
  navigateHref,
  onClose,
  title,
  viewId,
}: {
  readonly componentSet: string
  readonly componentSystem: PresentationComponentSystem
  readonly createHref?: (
    routeId: string,
    parameters?: NavigationRouteParameters,
  ) => string | null
  readonly data: TData | null
  readonly emptyMessage?: string | null
  readonly fieldRenderers?: Record<
    string,
    (context: ProjectedCollectionDetailFieldRenderContext<TData>) => ReactNode
  >
  readonly navigateHref?: (href: string) => void
  readonly onClose?: () => void
  readonly title?: string | null
  readonly viewId: string | null
}) {
  if (!viewId || (!data && !emptyMessage)) {
    return null
  }

  return (
    <ProjectedCollectionDetail
      componentSet={componentSet}
      componentSystem={componentSystem}
      createHref={createHref}
      data={data}
      emptyMessage={emptyMessage}
      fieldRenderers={fieldRenderers}
      navigateHref={navigateHref}
      onClose={data ? onClose : undefined}
      title={title}
      viewId={viewId}
    />
  )
}

function renderCollectionChromeSlots<TData extends object>({
  context,
  includeSlot,
  placements,
  renderFallbackSlot,
  renderChromeSlot,
}: {
  readonly context: Omit<ProjectedCollectionChromeSlotRenderContext<TData>, 'slot'>
  readonly includeSlot?: (slot: CollectionChromeSlotDefinition) => boolean
  readonly placements: readonly number[]
  readonly renderFallbackSlot?: (
    context: ProjectedCollectionChromeSlotRenderContext<TData>,
  ) => ReactNode
  readonly renderChromeSlot?: (
    context: ProjectedCollectionChromeSlotRenderContext<TData>,
  ) => ReactNode
}) {
  if (!renderChromeSlot && !renderFallbackSlot) {
    return []
  }

  return context.collectionRuntime.chrome.slots.flatMap((slot) => {
    if (includeSlot && !includeSlot(slot)) {
      return []
    }

    if (!placements.some((placement) => isCollectionChromeSlotPlacement(slot, placement))) {
      return []
    }

    const slotContext = { ...context, slot }
    const rendered = renderChromeSlot
      ? renderChromeSlot(slotContext)
      : renderFallbackSlot?.(slotContext)
    if (rendered === null || rendered === undefined || rendered === false) {
      return []
    }

    return [
      <Fragment key={slot.Id}>{rendered}</Fragment>,
    ]
  })
}

interface CreateProjectedColumnsOptions<TData extends object> {
  readonly actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>
  readonly canExecuteAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => boolean
  readonly collectionRuntime: ProjectedCollectionRuntime<TData>
  readonly componentSet: string
  readonly componentSystem: PresentationComponentSystem
  readonly createHref?: (
    routeId: string,
    parameters?: NavigationRouteParameters,
  ) => string | null
  readonly executeAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => Promise<void> | void
  readonly fieldRenderers?: Record<
    string,
    (context: ProjectedFieldRenderContext<TData>) => ReactNode
  >
  readonly getRowLabel?: (row: TData) => string
  readonly module: ProjectedModuleDefinitionLike | null
  readonly navigateHref?: (href: string) => void
  readonly renderActionIcon?: (placement: ProjectedActionPlacementLike) => ReactNode
  readonly renderChromeSlot?: (
    context: ProjectedCollectionChromeSlotRenderContext<TData>,
  ) => ReactNode
  readonly renderRowActionsSlot?: (
    slot: CollectionChromeSlotDefinition,
    row: TData,
    rowLabel: string | null,
  ) => ReactNode
  readonly viewId: string
}

function createProjectedColumns<TData extends object>({
  actionRuntimes,
  canExecuteAction,
  collectionRuntime,
  componentSet,
  componentSystem,
  createHref,
  executeAction,
  fieldRenderers,
  getRowLabel,
  module,
  navigateHref,
  renderActionIcon,
  renderChromeSlot,
  renderRowActionsSlot,
  viewId,
}: CreateProjectedColumnsOptions<TData>): readonly ColumnDef<TData>[] {
  if (!module) {
    return []
  }

  const fieldColumns = collectionRuntime.columns.flatMap<ColumnDef<TData>>((columnRuntime) => {
    return [
      {
        cell: ({ row }) => {
          const field = columnRuntime.field
          const value = columnRuntime.readValue(row.original)
          const renderer =
            fieldRenderers?.[field.Id] ??
            fieldRenderers?.[field.Field]
          return renderer
            ? renderer({ field, row: row.original, value })
            : renderProjectedFieldValue({
              componentSystem,
              createHref,
              emptyValueFallback: <span className="text-slate-400">none</span>,
              field,
              module,
              navigateHref,
              resource: row.original,
              value,
            })
        },
        header: columnRuntime.header,
        id: columnRuntime.id,
      },
    ]
  })

  const rowActionColumns = createProjectedRowActionChromeColumns({
    actionRuntimes,
    canExecuteAction,
    collectionRuntime,
    componentSet,
    componentSystem,
    createHref,
    executeAction,
    getRowLabel,
    module,
    navigateHref,
    renderChromeSlot,
    renderActionIcon,
    renderRowActionsSlot,
    viewId,
  })

  return [...fieldColumns, ...rowActionColumns]
}

function createProjectedRowActionChromeColumns<TData extends object>({
  actionRuntimes,
  canExecuteAction,
  collectionRuntime,
  componentSet,
  componentSystem,
  executeAction,
  getRowLabel,
  module,
  navigateHref,
  renderActionIcon,
  renderChromeSlot,
  renderRowActionsSlot,
  viewId,
}: CreateProjectedColumnsOptions<TData>): readonly ColumnDef<TData>[] {
  const slots = collectionRuntime.chrome.findSlots(
    collectionChromeSlotKinds.rowActions,
    collectionChromeSlotPlacements.inline,
  )
  if (slots.length === 0) {
    return []
  }

  return slots.map((slot) => ({
    cell: ({ row }) => {
      const rowLabel = getRowLabel?.(row.original) ??
        collectionRuntime.readRowLabel(row.original)
      const slotContext = {
        actionRuntimes,
        canExecuteAction,
        collectionRuntime,
        componentSet,
        componentSystem,
        executeAction,
        module,
        navigateHref,
        renderActionIcon,
        renderRowActionsSlot,
        row: row.original,
        rowLabel,
        slot,
        viewId,
      } satisfies ProjectedCollectionChromeSlotRenderContext<TData>
      const rendered = renderChromeSlot
        ? renderChromeSlot(slotContext)
        : renderRowActionsSlot?.(slot, row.original, rowLabel)

      return rendered ?? null
    },
    header: () => <span className="sr-only">{slot.Name || 'Actions'}</span>,
    id: slot.Id || 'collection-row-actions',
  }))
}

export function ProjectedCollectionRowActions<TData extends object>({
  canExecuteAction,
  collectionRuntime,
  componentSet,
  componentSystem,
  executeAction,
  module,
  renderActionIcon,
  row,
  rowLabel,
  slot,
  viewId,
}: {
  readonly canExecuteAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => boolean
  readonly collectionRuntime: ProjectedCollectionRuntime<TData>
  readonly componentSet: string
  readonly componentSystem: PresentationComponentSystem
  readonly executeAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => Promise<void> | void
  readonly module?: ProjectedModuleDefinitionLike | null
  readonly renderActionIcon?: (placement: ProjectedActionPlacementLike) => ReactNode
  readonly row: TData
  readonly rowLabel: string | null
  readonly slot: CollectionChromeSlotDefinition
  readonly viewId: string
}) {
  const ActionButton = componentSystem.actions.ActionButton
  const primaryRowActions = resolveSlotRowActions(
    slot,
    collectionRuntime.actions.primaryRowActions,
  )
  const contextRowActions = resolveSlotRowActions(
    slot,
    collectionRuntime.actions.contextRowActions,
  )
  const primaryItems = collectionRuntime.actions.resolveRowActionItems(
    row,
    primaryRowActions,
    { canExecuteAction },
  )

  if (primaryItems.length === 0 && contextRowActions.length === 0) {
    return null
  }

  return componentSystem.collectionChrome.CollectionRowActions({
    children: (
      <>
        {primaryItems.map(({ action, actionContext, actionRef, isEnabled, rowAction }) => {
          const label = actionRef.placement.Label ?? rowAction.Label ?? action.Name
          return (
            <ActionButton
              aria-label={rowLabel ? `${label}: ${rowLabel}` : label}
              {...createPresentationTestAttributes({
                actionId: rowAction.ActionId,
                collectionSlotId: slot.Id,
                viewId,
              })}
              disabled={
                !isEnabled ||
                !canExecuteAction(actionContext)
              }
              key={actionRef.id}
              onClick={(event) => {
                event.stopPropagation()
                void executeAction(actionContext)
              }}
              size="icon-sm"
              type="button"
              variant="ghost"
            >
              {renderActionIcon?.(actionRef.placement) ??
                renderDefaultActionIcon(actionRef.placement, module, componentSet)}
            </ActionButton>
          )
        })}
        <ProjectedRowActionMenu
          canExecuteAction={canExecuteAction}
          collectionRuntime={collectionRuntime}
          componentSystem={componentSystem}
          componentSet={componentSet}
          executeAction={executeAction}
          module={module}
          renderActionIcon={renderActionIcon}
          row={row}
          rowActions={contextRowActions}
          rowLabel={rowLabel}
          slotId={slot.Id}
          viewId={viewId}
        />
      </>
    ),
    rowLabel,
    slotId: slot.Id,
  })
}

function resolveSlotRowActions(
  slot: CollectionChromeSlotDefinition,
  rowActions: readonly ResolvedProjectedCollectionRowAction[],
) {
  const scopedActions = rowActions.filter((rowAction) => rowAction.slotId === slot.Id)
  if (scopedActions.length > 0) {
    return scopedActions
  }

  const actionIds = new Set(
    slot.RowActions.length > 0
      ? slot.RowActions.flatMap((rowAction) => [rowAction.Id, rowAction.ActionId])
      : slot.ActionIds,
  )
  if (actionIds.size === 0) {
    return rowActions
  }

  return rowActions.filter(({ action, actionRef, rowAction }) =>
    actionIds.has(action.Id) ||
    actionIds.has(actionRef.actionId) ||
    actionIds.has(actionRef.id) ||
    actionIds.has(rowAction.ActionId) ||
    actionIds.has(rowAction.Id))
}

function ProjectedRowActionMenu<TData extends object>({
  canExecuteAction,
  collectionRuntime,
  componentSet,
  componentSystem,
  executeAction,
  module,
  renderActionIcon,
  row,
  rowActions,
  rowLabel,
  slotId,
  viewId,
}: {
  readonly canExecuteAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => boolean
  readonly collectionRuntime: ProjectedCollectionRuntime<TData>
  readonly componentSet: string
  readonly componentSystem: PresentationComponentSystem
  readonly executeAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => Promise<void> | void
  readonly module?: ProjectedModuleDefinitionLike | null
  readonly renderActionIcon?: (placement: ProjectedActionPlacementLike) => ReactNode
  readonly row: TData
  readonly rowActions: readonly ResolvedProjectedCollectionRowAction[]
  readonly rowLabel: string | null
  readonly slotId: string
  readonly viewId: string
}) {
  const RowActionMenu = componentSystem.collections.RowActionMenu
  const RowActionMenuItem = componentSystem.collections.RowActionMenuItem
  const RowActionMenuTrigger = componentSystem.collections.RowActionMenuTrigger
  const items = collectionRuntime.actions.resolveRowActionItems(
    row,
    rowActions,
    { canExecuteAction },
  )

  if (items.length === 0) {
    return null
  }

  return (
    <RowActionMenu
      trigger={(
        <RowActionMenuTrigger aria-label={rowLabel ? `Actions: ${rowLabel}` : 'Row actions'}>
          {renderPresentationIcon({
            className: 'size-4',
            componentSet,
            icon: collectionChromeIconIds.rowActionsMenu,
            module,
          })}
        </RowActionMenuTrigger>
      )}
    >
      {items.map(({ action, actionContext, actionRef, isEnabled, rowAction }) => {
        const label = actionRef.placement.Label ?? rowAction.Label ?? action.Name
        return (
          <RowActionMenuItem
            actionId={rowAction.ActionId}
            collectionSlotId={slotId}
            disabled={
              !isEnabled ||
              !canExecuteAction(actionContext)
            }
            key={actionRef.id}
            onClick={(event) => {
              event.stopPropagation()
              closeContainingDetails(event.currentTarget)
              void executeAction(actionContext)
            }}
            viewId={viewId}
          >
            {renderActionIcon?.(actionRef.placement) ??
              renderDefaultActionIcon(actionRef.placement, module, componentSet)}
            <span>{label}</span>
          </RowActionMenuItem>
        )
      })}
    </RowActionMenu>
  )
}

export function ProjectedCollectionSelectionActionToolbar<TData extends object>({
  canExecuteAction,
  collectionRuntime,
  componentSet,
  componentSystem,
  executeAction,
  module,
  renderActionIcon,
  slot,
}: {
  readonly canExecuteAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => boolean
  readonly collectionRuntime: ProjectedCollectionRuntime<TData>
  readonly componentSet: string
  readonly componentSystem: PresentationComponentSystem
  readonly executeAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => Promise<void> | void
  readonly module?: ProjectedModuleDefinitionLike | null
  readonly renderActionIcon?: (placement: ProjectedActionPlacementLike) => ReactNode
  readonly slot?: CollectionChromeSlotDefinition | null
}) {
  const ActionButton = componentSystem.actions.ActionButton
  const selectedRowIds = collectionRuntime.selection.selectedRowIds
  if (selectedRowIds.length === 0) {
    return null
  }

  const items = collectionRuntime.actions.resolveSelectionActionItems({
    canExecuteAction,
    selectionActions: slot
      ? resolveSlotSelectionActions(slot, collectionRuntime.actions.selectionActions)
      : undefined,
  })

  if (items.length === 0) {
    return null
  }

  const selectedLabel =
    selectedRowIds.length === 1 ? '1 row selected' : `${selectedRowIds.length} rows selected`

  return componentSystem.collectionChrome.CollectionSelectionActionToolbar({
    actions: (
      <>
        {items.map(({ action, actionContext, actionRef, isEnabled, selectionAction }) => {
          const label = actionRef.placement.Label ?? selectionAction.Label ?? action.Name
          return (
            <ActionButton
              disabled={
                !isEnabled ||
                !canExecuteAction(actionContext)
              }
              key={actionRef.id}
              onClick={() => {
                void executeAction(actionContext)
              }}
              size="sm"
              type="button"
              variant="outline"
            >
              {renderActionIcon?.(actionRef.placement) ??
                renderDefaultActionIcon(actionRef.placement, module, componentSet)}
              {label}
            </ActionButton>
          )
        })}
      </>
    ),
    selectedCount: selectedRowIds.length,
    selectedLabel,
    slotId: slot?.Id ?? null,
  })
}

function resolveSlotSelectionActions(
  slot: CollectionChromeSlotDefinition,
  selectionActions: readonly ResolvedProjectedCollectionSelectionAction[],
) {
  const scopedActions = selectionActions.filter((selectionAction) =>
    selectionAction.slotId === slot.Id)
  if (scopedActions.length > 0) {
    return scopedActions
  }

  const actionIds = new Set(
    slot.SelectionActions.length > 0
      ? slot.SelectionActions.flatMap((selectionAction) => [
          selectionAction.Id,
          selectionAction.ActionId,
        ])
      : slot.ActionIds,
  )
  if (actionIds.size === 0) {
    return selectionActions
  }

  return selectionActions.filter(({ action, actionRef, selectionAction }) =>
    actionIds.has(action.Id) ||
    actionIds.has(actionRef.actionId) ||
    actionIds.has(actionRef.id) ||
    actionIds.has(selectionAction.ActionId) ||
    actionIds.has(selectionAction.Id))
}

function renderDefaultActionIcon(
  placement: ProjectedActionPlacementLike,
  module?: ProjectedModuleDefinitionLike | null,
  componentSet = defaultPresentationComponentSet,
) {
  return renderPresentationIcon({
    className: 'size-4',
    componentSet,
    icon: placement.Icon ?? 'square-arrow-out-up-right',
    module,
    subject: placement,
  })
}

function closeContainingDetails(element: HTMLElement) {
  element.closest('details')?.removeAttribute('open')
}

function ProjectionStatusBlock({ label }: { readonly label: string }) {
  return (
    <div className="rounded-2xl border border-slate-950/8 bg-white/65 px-4 py-3 text-sm text-slate-500">
      {label}
    </div>
  )
}
