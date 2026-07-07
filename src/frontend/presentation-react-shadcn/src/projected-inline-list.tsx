import { useCallback, useMemo, type ReactNode } from 'react'

import {
  defaultPresentationComponentSet,
  createProjectedCollectionRuntime,
  findPresentationView,
  isCollectionSelectionEnabled,
  projectPresentationActionRuntimeRegistry,
  projectProjectedCollectionActionRuntimeBindings,
  resolveProjectedCollectionSelectionMode,
  resolveProjectedCollectionSelectionStateId,
  type CollectionChromeSlotDefinition,
  type FieldPresentationDefinition,
  type NavigationRouteParameters,
  type PresentationActionRuntimeBinding,
  type ProjectedCollectionActionExecutionContext,
  type ProjectedCollectionActionRuntimeRegistry,
  type ProjectedCollectionRuntime,
  type ViewDefinition,
} from '@cohesive/presentation-core'
import {
  projectProjectedCollectionActionRuntimeDiagnostics,
} from './projected-collection-action-runtime-diagnostics'
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
  ProjectedCollectionRowActions,
  type ProjectedActionPlacementLike,
  type ProjectedFieldRenderContext,
} from './projected-collection-view'
import {
  renderProjectedFieldValue,
} from './projected-field-value-rendering'
import {
  collectionChromeSlotKinds,
  collectionChromeSlotPlacements,
} from '@cohesive/presentation-contracts'

type ProjectedInlineListModule = NonNullable<ReturnType<typeof usePresentationModule>>

export interface ProjectedInlineListProps<TData extends object> {
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
  readonly emptyMessage?: string | null
  readonly componentSystem: PresentationComponentSystem
  readonly fieldRenderers?: Record<
    string,
    (context: ProjectedFieldRenderContext<TData>) => ReactNode
  >
  readonly getRowLabel?: (row: TData) => string
  readonly navigateHref?: (href: string) => void
  readonly renderActionIcon?: (placement: ProjectedActionPlacementLike) => ReactNode
  readonly title?: string | null
  readonly viewId: string
}

export function ProjectedInlineList<TData extends object>({
  actionRuntimeBindings,
  componentSet = defaultPresentationComponentSet,
  componentSystem,
  createHref: createHrefOverride,
  data,
  emptyMessage,
  fieldRenderers,
  getRowLabel,
  navigateHref: navigateHrefOverride,
  renderActionIcon,
  title,
  viewId,
}: ProjectedInlineListProps<TData>) {
  const Badge = componentSystem.badges.Badge
  const module = usePresentationModule()
  const navigation = usePresentationNavigationRuntime()
  const view = findPresentationView<ViewDefinition>(module, viewId)
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
      selectionState,
      view,
    }),
    [createHref, data, module, selectionState, view],
  )
  const actionIds = useMemo(
    () => [
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
        actionIds,
        module,
        projections: [
          ...(actionRuntimeBindings ?? []),
          ...projectProjectedCollectionActionRuntimeBindings<TData, ReactNode>({
            navigateHref,
          }),
        ],
      }) as ProjectedCollectionActionRuntimeRegistry<TData>,
    [
      actionIds,
      actionRuntimeBindings,
      module,
      navigateHref,
    ],
  )
  const actionRuntimeDiagnostics = useMemo(
    () =>
      projectProjectedCollectionActionRuntimeDiagnostics({
        actionRuntimes,
        collectionRuntime,
        module,
        view,
      }),
    [
      actionRuntimes,
      collectionRuntime,
      module,
      view,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    `projected-inline-list-action-runtime.${viewId}`,
    actionRuntimeDiagnostics,
  )
  const rowActionSlot = collectionRuntime.chrome.findSlots(
    collectionChromeSlotKinds.rowActions,
    collectionChromeSlotPlacements.inline,
  )[0] ?? null
  const bodySlot = collectionRuntime.chrome.bodySlot
  const activatedRowAction = collectionRuntime.actions.activatedRowAction
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
  const shouldActivateOnRowClick = Boolean(bodySlot?.ActivateOnRowClick && activatedRowAction)
  const handleRowClick = useCallback(
    (row: TData, index: number) => {
      if (shouldSelectOnRowClick) {
        collectionRuntime.selection.activateRow(row, index)
      }

      if (!shouldActivateOnRowClick || !activatedRowAction) {
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
      canExecuteAction,
      collectionRuntime,
      executeAction,
      shouldActivateOnRowClick,
      shouldSelectOnRowClick,
    ],
  )

  if (!module) {
    return <ProjectedInlineListStatus label="Presentation module is not available." />
  }

  if (!view) {
    return <ProjectedInlineListStatus label={`Presentation view '${viewId}' is not available.`} />
  }

  if (collectionRuntime.columns.length === 0) {
    return <ProjectedInlineListStatus label={`Presentation view '${view.Name}' has no projected fields.`} />
  }

  if (data.length === 0) {
    const emptyMessageLabel = collectionRuntime.chrome.bodySlot?.EmptyMessage ?? emptyMessage
    return emptyMessageLabel ? (
      <ProjectedInlineListStatus label={emptyMessageLabel} />
    ) : null
  }

  return (
    <div className="grid gap-2 rounded-lg border border-slate-950/8 bg-white/70 p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm font-semibold text-slate-950">{title ?? view.Name}</p>
        <Badge className="border-slate-950/10 bg-white text-slate-600" variant="outline">
          {data.length.toLocaleString()}
        </Badge>
      </div>
      <div className="grid gap-2">
        {data.map((row, index) => (
          <ProjectedInlineListRow
            canExecuteAction={canExecuteAction}
            collectionRuntime={collectionRuntime}
            componentSet={componentSet}
            componentSystem={componentSystem}
            createHref={createHref}
            executeAction={executeAction}
            fieldRenderers={fieldRenderers}
            getRowLabel={getRowLabel}
            index={index}
            key={collectionRuntime.getRowId?.(row, index) ?? index}
            module={module}
            navigateHref={navigateHref}
            onRowClick={
              shouldSelectOnRowClick || shouldActivateOnRowClick
                ? handleRowClick
                : undefined
            }
            renderActionIcon={renderActionIcon}
            row={row}
            rowActionSlot={rowActionSlot}
            viewId={viewId}
          />
        ))}
      </div>
    </div>
  )
}

function ProjectedInlineListRow<TData extends object>({
  canExecuteAction,
  collectionRuntime,
  componentSet,
  componentSystem,
  createHref,
  executeAction,
  fieldRenderers,
  getRowLabel,
  index,
  module,
  navigateHref,
  onRowClick,
  renderActionIcon,
  row,
  rowActionSlot,
  viewId,
}: {
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
  readonly index: number
  readonly module: ProjectedInlineListModule
  readonly navigateHref?: (href: string) => void
  readonly onRowClick?: (row: TData, index: number) => void
  readonly renderActionIcon?: (placement: ProjectedActionPlacementLike) => ReactNode
  readonly row: TData
  readonly rowActionSlot: CollectionChromeSlotDefinition | null
  readonly viewId: string
}) {
  const [primaryColumn, ...supportingColumns] = collectionRuntime.columns
  const rowLabel = getRowLabel?.(row) ?? collectionRuntime.readRowLabel(row)

  return (
    <div
      className={cn(
        'grid gap-2 rounded-md border border-slate-950/8 bg-white/78 px-3 py-2',
        onRowClick ? 'cursor-pointer hover:border-slate-950/15 hover:bg-white' : null,
      )}
      onClick={onRowClick ? () => onRowClick(row, index) : undefined}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          {primaryColumn ? (
            <div className="text-sm font-medium text-slate-950">
              {renderInlineListField({
                column: primaryColumn,
                componentSystem,
                createHref,
                fieldRenderers,
                module,
                navigateHref,
                row,
              })}
            </div>
          ) : (
            <p className="text-sm font-medium text-slate-950">{rowLabel ?? `Item ${index + 1}`}</p>
          )}
        </div>
        {rowActionSlot ? (
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
            slot={rowActionSlot}
            viewId={viewId}
          />
        ) : null}
      </div>

      {supportingColumns.length > 0 ? (
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-slate-500">
          {supportingColumns.map((column) => (
            <span className="inline-flex min-w-0 items-center gap-1.5" key={column.id}>
              <span className="shrink-0 font-medium text-slate-400">{column.header}</span>
              <span className="min-w-0 break-all text-slate-600">
                {renderInlineListField({
                  column,
                  componentSystem,
                  createHref,
                  fieldRenderers,
                  module,
                  navigateHref,
                  row,
                })}
              </span>
            </span>
          ))}
        </div>
      ) : null}
    </div>
  )
}

function renderInlineListField<TData extends object>({
  column,
  componentSystem,
  createHref,
  fieldRenderers,
  module,
  navigateHref,
  row,
}: {
  readonly column: {
    readonly field: FieldPresentationDefinition
    readonly readValue: (row: TData) => unknown
  }
  readonly componentSystem: PresentationComponentSystem
  readonly createHref?: (
    routeId: string,
    parameters?: NavigationRouteParameters,
  ) => string | null
  readonly fieldRenderers?: Record<
    string,
    (context: ProjectedFieldRenderContext<TData>) => ReactNode
  >
  readonly module: ProjectedInlineListModule
  readonly navigateHref?: (href: string) => void
  readonly row: TData
}) {
  const field = column.field
  const value = column.readValue(row)
  const renderer = fieldRenderers?.[field.Id] ?? fieldRenderers?.[field.Field]
  return renderer
    ? renderer({ field, row, value })
    : renderProjectedFieldValue({
      componentSystem,
      createHref,
      emptyValueFallback: <span className="text-slate-400">none</span>,
      field,
      module,
      navigateHref,
      resource: row,
      value,
    })
}

function ProjectedInlineListStatus({ label }: { readonly label: ReactNode }) {
  return (
    <div className="rounded-md border border-slate-950/8 bg-white/65 px-4 py-3 text-sm text-slate-500">
      {label}
    </div>
  )
}

function cn(...values: readonly (string | null | undefined | false)[]) {
  return values.filter(Boolean).join(' ')
}
