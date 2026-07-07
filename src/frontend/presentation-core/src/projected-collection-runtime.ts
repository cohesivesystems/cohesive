import type {
  ActionDefinition,
  CollectionChromeDefinition,
  CollectionChromeSlotDefinition,
  CollectionChromeSlotPlacement,
  CollectionColumnDefinition,
  CollectionRowActionDefinition,
  CollectionRowActionParameterBindingDefinition,
  CollectionSelectionActionDefinition,
  CollectionSelectionMode,
  FieldPresentationDefinition,
  PresentationModuleDefinition,
  ViewDefinition,
} from './module'
import type {
  CollectionChromeRuntime,
} from './collection-chrome-runtime'
import {
  createCollectionChromeRuntime,
  matchesCollectionChromeSlotPlacement,
  resolveCollectionChromeSlots,
} from './collection-chrome-runtime'
import {
  findPresentationAction,
  findPresentationField,
} from './module'
import {
  getPresentationViewProjectedActions,
  type PresentationViewProjectedActionRef,
} from './presentation-semantics'
import type {
  CollectionSelectionStateEntry,
} from './collection-selection-state'
import type {
  NavigationRouteParameters,
} from './navigation'
import {
  readObjectPath,
  readObjectProperty,
} from './object-path'
import {
  resolvePresentationPageInfo,
  type PresentationPaginationBinding,
  type PresentationPaginationRuntime,
  type PresentationPaginationState,
  type ResolvedPresentationPageInfo,
} from './presentation-pagination'
import type {
  PresentationActionRuntimeRegistry,
} from './presentation-action-runtime-projection'
import {
  collectionDetailActivations,
  collectionChromeSlotPlacements,
  collectionRowActionKinds,
  collectionSelectionActionParameterSources,
  collectionSelectionModes,
  presentationValueKinds,
} from '@cohesive/presentation-contracts'

/**
 * Collection definition projected from a presentation view.
 */
export type ProjectedCollectionDefinition = NonNullable<ViewDefinition['Collection']>
type ProjectedCollectionRowActionDefinition = CollectionRowActionDefinition
type ProjectedCollectionSelectionActionDefinition = CollectionSelectionActionDefinition
type ProjectedCollectionParameterDefinition = ActionDefinition['Parameters'][number]
type ProjectedCollectionModuleDefinition =
  Pick<PresentationModuleDefinition, 'Actions' | 'Fields' | 'Targets'>

/**
 * Annotation name used on collection chrome definitions to describe how the
 * chrome was projected.
 */
export const collectionChromeProjectionAnnotationName =
  'cohesive.presentation.collection-chrome.projection'

/**
 * Annotation name used on collection chrome slot definitions to describe how a
 * specific slot was projected.
 */
export const collectionChromeSlotProjectionAnnotationName =
  'cohesive.presentation.collection-chrome.slot.projection'

/**
 * Projection-mode tokens emitted by compatibility and declared collection
 * chrome projection paths.
 */
export const collectionChromeProjectionModes = {
  compatibilitySynthesized: 'compatibility-synthesized',
  declared: 'declared',
  mixed: 'mixed',
} as const

/**
 * Supported collection chrome projection modes.
 */
export type CollectionChromeProjectionMode =
  typeof collectionChromeProjectionModes[keyof typeof collectionChromeProjectionModes]

/**
 * Minimal action placement shape shared by row and selection action projections.
 */
export interface ProjectedActionPlacementLike {
  /** Action identifier resolved against the containing presentation module. */
  readonly ActionId: string

  /** Optional icon override projected onto the placement. */
  readonly Icon?: string | null

  /** Optional label override projected onto the placement. */
  readonly Label?: string | null

  /** Optional region or surface hint for placement-specific rendering. */
  readonly Region?: string | null
}

/**
 * Row action resolved from a view action reference and module action
 * definition.
 */
export interface ResolvedProjectedCollectionRowAction {
  /** Concrete action definition invoked by the row action. */
  readonly action: ActionDefinition

  /** View-level projected action reference that introduced the action. */
  readonly actionRef: PresentationViewProjectedActionRef

  /** Collection row action placement metadata. */
  readonly rowAction: CollectionRowActionDefinition

  /** Chrome slot that owns the action, when known. */
  readonly slotId?: string | null
}

/**
 * Selection action resolved from a view action reference and module action
 * definition.
 */
export interface ResolvedProjectedCollectionSelectionAction {
  /** Concrete action definition invoked by the selection action. */
  readonly action: ActionDefinition

  /** View-level projected action reference that introduced the action. */
  readonly actionRef: PresentationViewProjectedActionRef

  /** Collection selection action placement metadata. */
  readonly selectionAction: CollectionSelectionActionDefinition

  /** Chrome slot that owns the action, when known. */
  readonly slotId?: string | null
}

/**
 * Render-ready row action item for a specific collection row.
 */
export interface ProjectedCollectionRowActionItem<TData extends object> {
  /** Concrete action definition invoked by this item. */
  readonly action: ActionDefinition

  /** Execution context bound to the row. */
  readonly actionContext: ProjectedRowActionExecutionContext<TData>

  /** View-level projected action reference. */
  readonly actionRef: PresentationViewProjectedActionRef

  /** Whether the action passes semantic predicates and host runtime checks. */
  readonly isEnabled: boolean

  /** Row action placement metadata. */
  readonly rowAction: CollectionRowActionDefinition
}

/**
 * Render-ready selection action item for the current collection selection.
 */
export interface ProjectedCollectionSelectionActionItem<TData extends object> {
  /** Concrete action definition invoked by this item. */
  readonly action: ActionDefinition

  /** Execution context bound to the current selection. */
  readonly actionContext: ProjectedSelectionActionExecutionContext<TData>

  /** View-level projected action reference. */
  readonly actionRef: PresentationViewProjectedActionRef

  /** Whether the action passes semantic predicates and host runtime checks. */
  readonly isEnabled: boolean

  /** Selection action placement metadata. */
  readonly selectionAction: CollectionSelectionActionDefinition
}

/**
 * Discriminator for action execution contexts created by the projected
 * collection runtime.
 */
export type ProjectedCollectionActionContextKind =
  | 'collection-row'
  | 'collection-selection'

/**
 * Union of row-scoped and selection-scoped collection action contexts.
 */
export type ProjectedCollectionActionExecutionContext<TData extends object> =
  | ProjectedRowActionExecutionContext<TData>
  | ProjectedSelectionActionExecutionContext<TData>

/**
 * Host action runtime registry keyed by action identifier.
 */
export type ProjectedCollectionActionRuntimeRegistry<TData extends object> =
  PresentationActionRuntimeRegistry<
    ProjectedCollectionActionExecutionContext<TData>,
    unknown
  >

/**
 * Execution context for an action bound to a single collection row.
 */
export interface ProjectedRowActionExecutionContext<TData extends object> {
  /** Concrete action definition to execute. */
  readonly action: ActionDefinition

  /** View-level projected action reference. */
  readonly actionRef: PresentationViewProjectedActionRef

  /** Context discriminator for runtime dispatch. */
  readonly contextKind: 'collection-row'

  /** Navigation href derived from the action route and bound parameters. */
  readonly href: string | null

  /** Route or action parameters derived from the row. */
  readonly parameters: NavigationRouteParameters

  /** Row that the action targets. */
  readonly row: TData

  /** Row action placement metadata. */
  readonly rowAction: CollectionRowActionDefinition
}

/**
 * Execution context for an action bound to the current collection selection.
 */
export interface ProjectedSelectionActionExecutionContext<TData extends object> {
  /** Concrete action definition to execute. */
  readonly action: ActionDefinition

  /** View-level projected action reference. */
  readonly actionRef: PresentationViewProjectedActionRef

  /** Context discriminator for runtime dispatch. */
  readonly contextKind: 'collection-selection'

  /** Navigation href derived from the action route and bound parameters. */
  readonly href: string | null

  /** Route or action parameters derived from selected rows. */
  readonly parameters: NavigationRouteParameters

  /** Stable identities for the selected rows. */
  readonly selectedRowIds: readonly string[]

  /** Data objects for selected rows currently visible to the runtime. */
  readonly selectedRows: readonly TData[]

  /** Selection action placement metadata. */
  readonly selectionAction: CollectionSelectionActionDefinition
}

/**
 * Action runtime projected from collection row and selection action semantics.
 */
export interface ProjectedCollectionActionRuntime<TData extends object> {
  /** Row action invoked by row activation, such as row click, when configured. */
  readonly activatedRowAction: ResolvedProjectedCollectionRowAction | null

  /** Checks whether a row or selection action can be invoked by host runtime. */
  readonly canInvokeAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
    actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
  ) => boolean

  /** Checks whether a row action can be invoked by host runtime. */
  readonly canInvokeRowAction: (
    context: ProjectedRowActionExecutionContext<TData>,
    actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
  ) => boolean

  /** Checks whether a selection action can be invoked by host runtime. */
  readonly canInvokeSelectionAction: (
    context: ProjectedSelectionActionExecutionContext<TData>,
    actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
  ) => boolean

  /** Row actions intended for contextual surfaces such as menus. */
  readonly contextRowActions: readonly ResolvedProjectedCollectionRowAction[]

  /** Creates an execution context for a resolved row action and row. */
  readonly createRowActionContext: (
    resolvedAction: ResolvedProjectedCollectionRowAction,
    row: TData,
  ) => ProjectedRowActionExecutionContext<TData>

  /** Invokes a row or selection action through the supplied host runtimes. */
  readonly invokeAction: (
    context: ProjectedCollectionActionExecutionContext<TData>,
    actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
  ) => Promise<void>

  /** Invokes a row action through the supplied host runtimes. */
  readonly invokeRowAction: (
    context: ProjectedRowActionExecutionContext<TData>,
    actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
  ) => Promise<void>

  /** Invokes a selection action through the supplied host runtimes. */
  readonly invokeSelectionAction: (
    context: ProjectedSelectionActionExecutionContext<TData>,
    actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
  ) => Promise<void>

  /** Row actions intended for primary inline affordances. */
  readonly primaryRowActions: readonly ResolvedProjectedCollectionRowAction[]

  /** Resolves render-ready row action items for a specific row. */
  readonly resolveRowActionItems: (
    row: TData,
    rowActions: readonly ResolvedProjectedCollectionRowAction[],
    options?: ProjectedCollectionRowActionItemOptions<TData>,
  ) => readonly ProjectedCollectionRowActionItem<TData>[]

  /** Resolves render-ready selection action items for current selection. */
  readonly resolveSelectionActionItems: (
    options?: ProjectedCollectionSelectionActionItemOptions<TData>,
  ) => readonly ProjectedCollectionSelectionActionItem<TData>[]

  /** All resolved row actions declared for the collection view. */
  readonly rowActions: readonly ResolvedProjectedCollectionRowAction[]

  /** All resolved selection actions declared for the collection view. */
  readonly selectionActions: readonly ResolvedProjectedCollectionSelectionAction[]
}

/**
 * Options for resolving row action items.
 */
export interface ProjectedCollectionRowActionItemOptions<TData extends object> {
  /** Optional host-level predicate layered after semantic visibility checks. */
  readonly canExecuteAction?: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => boolean
}

/**
 * Options for resolving selection action items.
 */
export interface ProjectedCollectionSelectionActionItemOptions<TData extends object> {
  /** Optional host-level predicate layered after semantic visibility checks. */
  readonly canExecuteAction?: (
    context: ProjectedCollectionActionExecutionContext<TData>,
  ) => boolean

  /** Optional subset of selection actions to resolve. */
  readonly selectionActions?: readonly ResolvedProjectedCollectionSelectionAction[]
}

/**
 * Host callback used to navigate to an already-created href.
 */
export type ProjectedNavigateHref = (href: string) => void

/**
 * Runtime representation of a projected collection column.
 */
export interface ProjectedCollectionColumnRuntime<TData extends object> {
  /** Column declaration from the body chrome slot. */
  readonly column: CollectionColumnDefinition

  /** Field definition resolved from the presentation module. */
  readonly field: FieldPresentationDefinition

  /** Header label to render for the column. */
  readonly header: string

  /** Stable column identifier. */
  readonly id: string

  /** Reads this column's value from a row. */
  readonly readValue: (row: TData) => unknown

  /** Object path used to read row values for the column. */
  readonly valuePath: string
}

/**
 * Runtime collection chrome shape shared with the core chrome resolver.
 */
export type ProjectedCollectionChromeRuntime = CollectionChromeRuntime

/**
 * Inputs used to bind collection pagination chrome to a concrete pagination
 * runtime and data-source response.
 */
export interface ProjectedCollectionPaginationInput {
  /** Whether the data source backing the collection is currently fetching. */
  readonly isFetching?: boolean

  /** Reads a data source result by identifier for pagination metadata. */
  readonly readDataSource?: (dataSourceId: string) => unknown

  /** Current response from the paged data source. */
  readonly response?: unknown

  /** Host pagination runtime for changing page state. */
  readonly runtime?: PresentationPaginationRuntime | null

  /** Explicit total count override when the response does not carry one. */
  readonly totalCount?: number | null
}

/**
 * Runtime pagination state projected from collection chrome and host paging
 * runtime.
 */
export interface ProjectedCollectionPaginationRuntime {
  /** Data source identifier named by the pagination slot, when present. */
  readonly dataSourceId: string | null

  /** Pagination slot definition, when pagination is declared. */
  readonly definition: CollectionChromeSlotDefinition | null

  /** Whether the collection declares a pagination slot. */
  readonly isEnabled: boolean

  /** Whether the backing data source is currently fetching. */
  readonly isFetching: boolean

  /** Whether pagination should render in footer chrome. */
  readonly isFooterEnabled: boolean

  /** Declared pagination slot placement. */
  readonly placement: CollectionChromeSlotDefinition['Placement'] | null

  /** Pagination slot definition, when pagination is declared. */
  readonly slot: CollectionChromeSlotDefinition | null

  /** Active paging window when a host runtime and page info are available. */
  readonly window: ProjectedCollectionWindowRuntime | null
}

/**
 * Page navigation window exposed to collection chrome renderers.
 */
export interface ProjectedCollectionWindowRuntime {
  /** Pagination binding that describes the paging strategy. */
  readonly binding: PresentationPaginationBinding

  /** Whether a next page can be requested. */
  readonly canGoNextPage: boolean

  /** Whether a previous page can be requested. */
  readonly canGoPreviousPage: boolean

  /** Data source controlled by this pagination window. */
  readonly dataSourceId: string

  /** Navigates to the first page. */
  readonly goToFirstPage: () => void

  /** Navigates to the next page using the current response as cursor context. */
  readonly goToNextPage: () => void

  /** Navigates to the previous page. */
  readonly goToPreviousPage: () => void

  /** Zero-based page index. */
  readonly pageIndex: number

  /** Resolved page metadata for labels and navigation state. */
  readonly pageInfo: ResolvedPresentationPageInfo

  /** Current page size. */
  readonly pageSize: number

  /** Mutable pagination state owned by the host runtime. */
  readonly state: PresentationPaginationState
}

/**
 * Inputs required to project a collection runtime from view IR and concrete
 * data.
 */
export interface ProjectedCollectionRuntimeOptions<TData extends object> {
  /** Optional href factory for navigation actions. */
  readonly createHref?: (
    routeId: string,
    parameters?: NavigationRouteParameters,
  ) => string | null

  /** Data rows currently visible in the collection. */
  readonly data: readonly TData[]

  /** Presentation module used to resolve fields and actions. */
  readonly module?: ProjectedCollectionModuleDefinition | null

  /** Optional pagination bridge for collection pagination chrome. */
  readonly pagination?: ProjectedCollectionPaginationInput | null

  /** Optional mutable selection state shared across projected runtimes. */
  readonly selectionState?: CollectionSelectionStateEntry | null

  /** View definition containing the collection IR to project. */
  readonly view?: ViewDefinition | null
}

/**
 * Runtime projection of a collection view over concrete row data.
 *
 * The runtime keeps backend-declared collection semantics as the source of
 * truth for columns, chrome, actions, selection, detail, and pagination while
 * exposing host-friendly functions for rendering and execution.
 */
export interface ProjectedCollectionRuntime<TData extends object> {
  /** Projected row and selection action runtime. */
  readonly actions: ProjectedCollectionActionRuntime<TData>

  /** Resolved collection chrome slots and slot shortcuts. */
  readonly chrome: ProjectedCollectionChromeRuntime

  /** Visible body columns resolved against module fields. */
  readonly columns: readonly ProjectedCollectionColumnRuntime<TData>[]

  /** Collection definition from the view, when present. */
  readonly collection: ProjectedCollectionDefinition | null

  /** Data rows currently visible in the collection. */
  readonly data: readonly TData[]

  /** Detail projection state derived from selection and detail chrome. */
  readonly detail: ProjectedCollectionDetailRuntime<TData>

  /** Reads a stable row identity when the body slot declares one. */
  readonly getRowId?: (row: TData, index: number) => string

  /** Pagination runtime projected from pagination chrome. */
  readonly pagination: ProjectedCollectionPaginationRuntime

  /** Reads the best available row label for display and accessibility. */
  readonly readRowLabel: (row: TData) => string | null

  /** Raw row action declarations resolved from the collection. */
  readonly rowActions: readonly ProjectedCollectionRowActionDefinition[]

  /** Selection runtime projected from collection selection semantics. */
  readonly selection: ProjectedCollectionSelectionRuntime<TData>

  /** Raw selection action declarations resolved from the collection. */
  readonly selectionActions: readonly ProjectedCollectionSelectionActionDefinition[]
}

/**
 * Runtime selection state and operations for a projected collection.
 */
export interface ProjectedCollectionSelectionRuntime<TData extends object> {
  /** Activates a row according to the collection selection mode. */
  readonly activateRow: (row: TData, index: number) => void

  /** Whether row selection is enabled. */
  readonly isEnabled: boolean

  /** Whether the selection mode supports multiple rows. */
  readonly isMultiple: boolean

  /** Tests whether a row is currently selected, when row identity is available. */
  readonly isRowSelected?: (row: TData, index: number) => boolean

  /** Declared collection selection mode. */
  readonly mode: CollectionSelectionMode

  /** Removes selected row ids that are no longer visible when configured. */
  readonly pruneToVisibleRows: () => void

  /** First selected visible row, used by single-row detail projections. */
  readonly selectedRow: TData | null

  /** Set view of selected row identities for efficient membership checks. */
  readonly selectedRowIdSet: ReadonlySet<string>

  /** Selected row identities from the active selection state. */
  readonly selectedRowIds: readonly string[]

  /** Selected rows that are currently visible in `data`. */
  readonly selectedRows: readonly TData[]

  /** Active mutable selection state, or `null` when selection is disabled. */
  readonly state: CollectionSelectionStateEntry | null
}

/**
 * Runtime state for the detail surface associated with the selected row.
 */
export interface ProjectedCollectionDetailRuntime<TData extends object> {
  /** Selected row data shown by the detail surface. */
  readonly data: TData | null

  /** Message to show when no detail row is selected. */
  readonly emptyMessage?: string | null

  /** Whether the detail slot has enough semantic information to render. */
  readonly isRenderable: boolean

  /** Declared detail slot placement. */
  readonly placement?: CollectionChromeSlotPlacement | null

  /** Detail surface title. */
  readonly title?: string | null

  /** Detail view identifier projected for the selected row. */
  readonly viewId: string | null
}

/**
 * Creates the host-facing runtime for a projected collection.
 *
 * @param options View IR, data, module metadata, selection state, pagination,
 * and navigation hooks used to bind collection semantics to runtime behavior.
 */
export function createProjectedCollectionRuntime<TData extends object>({
  createHref,
  data,
  module,
  pagination: paginationInput,
  selectionState = null,
  view,
}: ProjectedCollectionRuntimeOptions<TData>): ProjectedCollectionRuntime<TData> {
  const collection = view?.Collection ?? null
  const chrome = createProjectedCollectionChromeRuntime(collection)
  const bodySlot = chrome.bodySlot
  const selectionMode = resolveProjectedCollectionSelectionMode(collection)
  const selectionEnabled = isCollectionSelectionEnabled(selectionMode)
  const activeSelectionState = selectionEnabled ? selectionState : null
  const rowIdentityPath = bodySlot?.RowIdentityPath ?? null
  const rowLabelPath = bodySlot?.RowLabelPath ?? null
  const getRowId = rowIdentityPath
    ? (row: TData, index: number) =>
        readProjectedCollectionRowIdentity(row, rowIdentityPath, index)
    : undefined
  const selectedRowIds = activeSelectionState?.selectedRowIds ?? []
  const selectedRowIdSet = new Set(selectedRowIds)
  const selectedRows = getRowId && selectedRowIds.length > 0
    ? data.filter((row, index) => selectedRowIdSet.has(getRowId(row, index)))
    : []
  const selectedRow = selectedRows[0] ?? null
  const detailSlot = chrome.detailSlot
  const detailIsRenderable = isRenderableCollectionDetailSlot(detailSlot)
  const columns = createProjectedCollectionColumnRuntime<TData>({
    bodySlot,
    module,
  })
  const projectedActionRefs = getPresentationViewProjectedActions(view ?? null)
  const rowActions = resolveProjectedCollectionRowActions(module, projectedActionRefs)
  const selectionActions = resolveProjectedCollectionSelectionActions(module, projectedActionRefs)
  const actionRuntime = createProjectedCollectionActionRuntime({
    createHref,
    rowActions,
    selectionActions,
    bodySlot,
    selectedRowIds,
    selectedRows,
  })
  const pagination = createProjectedCollectionPaginationRuntime({
    chrome,
    input: paginationInput,
    itemCount: data.length,
  })

  return {
    actions: actionRuntime,
    chrome,
    columns,
    collection,
    data,
    detail: {
      data: selectedRow,
      emptyMessage: detailSlot?.EmptyMessage,
      isRenderable: detailIsRenderable,
      placement: detailSlot?.Placement,
      title: detailSlot?.Title,
      viewId: detailIsRenderable
        ? detailSlot?.DetailViewId ?? null
        : null,
    },
    getRowId,
    pagination,
    readRowLabel(row) {
      return readProjectedCollectionRowLabel(row, rowLabelPath)
    },
    rowActions: rowActions.map(({ rowAction }) => rowAction),
    selection: {
      activateRow(row, index) {
        const rowId = getRowId?.(row, index) ?? null
        if (!rowId || !activeSelectionState) {
          return
        }

        if (isMultipleCollectionSelection(selectionMode)) {
          activeSelectionState.toggleRowId(rowId, selectionMode)
          return
        }

        activeSelectionState.selectRowId(rowId, selectionMode)
      },
      isEnabled: selectionEnabled,
      isMultiple: isMultipleCollectionSelection(selectionMode),
      isRowSelected: activeSelectionState && getRowId
        ? (row, index) => selectedRowIdSet.has(getRowId(row, index))
        : undefined,
      mode: selectionMode,
      pruneToVisibleRows() {
        if (
          !bodySlot?.ClearSelectionOnQueryChange ||
          !activeSelectionState ||
          !getRowId ||
          selectedRowIds.length === 0
        ) {
          return
        }

        const visibleRowIds = new Set(data.map((row, index) => getRowId(row, index)))
        const visibleSelectedRowIds = selectedRowIds.filter((rowId) => visibleRowIds.has(rowId))
        if (visibleSelectedRowIds.length !== selectedRowIds.length) {
          activeSelectionState.setSelectedRowIds(visibleSelectedRowIds)
        }
      },
      selectedRow,
      selectedRowIdSet,
      selectedRowIds,
      selectedRows,
      state: activeSelectionState,
    },
    selectionActions: selectionActions.map(({ selectionAction }) => selectionAction),
  }
}

/**
 * Creates the collection chrome runtime for a projected collection definition.
 */
export function createProjectedCollectionChromeRuntime(
  collection: ProjectedCollectionDefinition | null | undefined,
): ProjectedCollectionChromeRuntime {
  return createCollectionChromeRuntime(collection)
}

/**
 * Resolves all collection chrome slots from a collection definition.
 */
export function resolveProjectedCollectionChromeSlots(
  collection: ProjectedCollectionDefinition | null | undefined,
): readonly CollectionChromeSlotDefinition[] {
  return resolveCollectionChromeSlots(collection)
}

/**
 * Resolves the selection state identifier implied by collection chrome.
 *
 * Body state is preferred, followed by selection actions and detail state.
 */
export function resolveProjectedCollectionSelectionStateId(
  collection: ProjectedCollectionDefinition | null | undefined,
) {
  const chrome = createProjectedCollectionChromeRuntime(collection)
  return chrome.bodySlot?.StateId ??
    chrome.selectionActionsSlot?.StateId ??
    chrome.detailSlot?.StateId ??
    null
}

/**
 * Resolves the collection selection mode, defaulting to `none` when no body
 * slot declares selection.
 */
export function resolveProjectedCollectionSelectionMode(
  collection: ProjectedCollectionDefinition | null | undefined,
) {
  return createProjectedCollectionChromeRuntime(collection).bodySlot?.SelectionMode ??
    collectionSelectionModes.none
}

/**
 * Reads the projection mode annotation from collection chrome.
 */
export function readCollectionChromeProjectionMode(
  chrome: Pick<CollectionChromeDefinition, 'Annotations'> | null | undefined,
): CollectionChromeProjectionMode | null {
  return readCollectionChromeProjectionModeAnnotation(
    chrome?.Annotations,
    collectionChromeProjectionAnnotationName,
  )
}

/**
 * Reads the projection mode annotation from a collection chrome slot.
 */
export function readCollectionChromeSlotProjectionMode(
  slot: Pick<CollectionChromeSlotDefinition, 'Annotations'> | null | undefined,
): CollectionChromeProjectionMode | null {
  return readCollectionChromeProjectionModeAnnotation(
    slot?.Annotations,
    collectionChromeSlotProjectionAnnotationName,
  )
}

/**
 * Checks whether a collection chrome slot carries a specific projection mode.
 */
export function isCollectionChromeSlotProjectionMode(
  slot: Pick<CollectionChromeSlotDefinition, 'Annotations'> | null | undefined,
  mode: CollectionChromeProjectionMode,
) {
  return readCollectionChromeSlotProjectionMode(slot) === mode
}

/**
 * Resolves the first data source identifier attached to the collection
 * pagination slot.
 */
export function resolveProjectedCollectionPaginationDataSourceId(
  collection: ProjectedCollectionDefinition | null | undefined,
) {
  const paginationSlot = createProjectedCollectionChromeRuntime(collection).paginationSlot
  return paginationSlot?.DataSourceIds[0] ?? null
}

/**
 * Checks whether collection pagination chrome is declared.
 */
export function isProjectedCollectionPaginationEnabled(
  collection: ProjectedCollectionDefinition | null | undefined,
) {
  return Boolean(createProjectedCollectionChromeRuntime(collection).paginationSlot)
}

/**
 * Checks whether collection pagination is declared for footer placement.
 */
export function isProjectedCollectionPaginationFooterEnabled(
  collection: ProjectedCollectionDefinition | null | undefined,
) {
  const chrome = createProjectedCollectionChromeRuntime(collection)
  return Boolean(chrome.paginationFooterSlot)
}

/**
 * Creates the pagination runtime by combining declared pagination chrome with a
 * host pagination runtime and the current data-source response.
 */
function createProjectedCollectionPaginationRuntime({
  chrome,
  input,
  itemCount,
}: {
  readonly chrome: ProjectedCollectionChromeRuntime
  readonly input?: ProjectedCollectionPaginationInput | null
  readonly itemCount: number
}): ProjectedCollectionPaginationRuntime {
  const slot = chrome.paginationSlot
  const definition = slot
  const isEnabled = Boolean(slot)
  const dataSourceId = slot?.DataSourceIds[0] ?? null
  const placement = slot?.Placement ?? null
  const runtime = input?.runtime ?? null
  const pageInfo = runtime
    ? resolvePresentationPageInfo({
        binding: runtime.binding,
        itemCount,
        readDataSource: input?.readDataSource,
        response: input?.response,
        state: runtime.state,
        totalCount: input?.totalCount,
      })
    : null

  return {
    dataSourceId,
    definition,
    isEnabled,
    isFetching: Boolean(input?.isFetching),
    isFooterEnabled: isEnabled && isCollectionChromeFooterPlacement(slot?.Placement ?? null),
    placement,
    slot,
    window: runtime && pageInfo
      ? {
          binding: runtime.binding,
          canGoNextPage: pageInfo.hasNextPage,
          canGoPreviousPage: runtime.canGoPreviousPage,
          dataSourceId: runtime.dataSourceId,
          goToFirstPage: runtime.goToFirstPage,
          goToNextPage: () => runtime.goToNextPage(input?.response),
          goToPreviousPage: runtime.goToPreviousPage,
          pageIndex: runtime.pageIndex,
          pageInfo,
          pageSize: runtime.pageSize,
          state: runtime.state,
        }
      : null,
  }
}

/**
 * Reads and validates collection chrome projection-mode annotations.
 */
function readCollectionChromeProjectionModeAnnotation(
  annotations: readonly CollectionChromeDefinition['Annotations'][number][] | null | undefined,
  annotationName: string,
): CollectionChromeProjectionMode | null {
  const annotation = annotations?.find((candidate) =>
    candidate.Name.toLocaleLowerCase() === annotationName)
  if (!annotation) {
    return null
  }

  const value = annotation.Value
  const mode = typeof value === 'string'
    ? value
    : value && typeof value === 'object'
      ? (value as Readonly<Record<string, unknown>>).mode
      : null

  return isCollectionChromeProjectionMode(mode) ? mode : null
}

/**
 * Checks whether an arbitrary annotation value is a known projection mode.
 */
function isCollectionChromeProjectionMode(
  value: unknown,
): value is CollectionChromeProjectionMode {
  return value === collectionChromeProjectionModes.declared ||
    value === collectionChromeProjectionModes.compatibilitySynthesized ||
    value === collectionChromeProjectionModes.mixed
}

/**
 * Checks whether a slot placement is semantically the footer placement.
 */
function isCollectionChromeFooterPlacement(
  placement: CollectionChromeSlotPlacement | null,
) {
  return matchesCollectionChromeSlotPlacement(
    placement,
    collectionChromeSlotPlacements.footer,
  )
}

/**
 * Builds visible column runtimes by resolving body-slot column declarations
 * against field definitions in the presentation module.
 */
function createProjectedCollectionColumnRuntime<TData extends object>({
  bodySlot,
  module,
}: {
  readonly bodySlot?: CollectionChromeSlotDefinition | null
  readonly module?: ProjectedCollectionModuleDefinition | null
}): readonly ProjectedCollectionColumnRuntime<TData>[] {
  if (!module || !bodySlot) {
    return []
  }

  return resolveCollectionBodyColumns(bodySlot).flatMap((column) => {
    const field = findPresentationField<FieldPresentationDefinition>(module, column.FieldId)
    if (!field) {
      return []
    }

    const valuePath = column.ValuePath ?? field.Field
    return [{
      column,
      field,
      header: field.Label,
      id: column.Id || field.Id,
      readValue: (row: TData) => readProjectedCollectionValue(row, valuePath),
      valuePath,
    }]
  })
}

/**
 * Returns visible body columns in declared order.
 */
function resolveCollectionBodyColumns(
  bodySlot: CollectionChromeSlotDefinition,
): readonly CollectionColumnDefinition[] {
  return bodySlot.Columns
    .filter((column) => column.IsVisible)
    .sort((left, right) => left.Order - right.Order)
}

/**
 * Creates the action runtime that resolves row and selection action items,
 * builds execution contexts, and delegates invocation to host action runtimes.
 */
function createProjectedCollectionActionRuntime<TData extends object>({
  bodySlot,
  createHref,
  rowActions,
  selectionActions,
  selectedRowIds,
  selectedRows,
}: {
  readonly bodySlot: CollectionChromeSlotDefinition | null
  readonly createHref?: (
    routeId: string,
    parameters?: NavigationRouteParameters,
  ) => string | null
  readonly rowActions: readonly ResolvedProjectedCollectionRowAction[]
  readonly selectionActions: readonly ResolvedProjectedCollectionSelectionAction[]
  readonly selectedRowIds: readonly string[]
  readonly selectedRows: readonly TData[]
}): ProjectedCollectionActionRuntime<TData> {
  const primaryRowActions = rowActions.filter(({ rowAction }) =>
    isPrimaryCollectionRowAction(rowAction))
  const contextRowActions = rowActions.filter(({ rowAction }) =>
    isContextMenuCollectionRowAction(rowAction))
  const activatedRowAction = resolveActivatedCollectionRowAction(
    bodySlot,
    rowActions,
  )

  return {
    activatedRowAction,
    canInvokeAction: canInvokeProjectedCollectionAction,
    canInvokeRowAction: canInvokeProjectedRowAction,
    canInvokeSelectionAction: canInvokeProjectedSelectionAction,
    contextRowActions,
    createRowActionContext(resolvedAction, row) {
      return createProjectedRowActionExecutionContext(resolvedAction, row, createHref)
    },
    invokeAction: invokeProjectedCollectionAction,
    invokeRowAction: invokeProjectedRowAction,
    invokeSelectionAction: invokeProjectedSelectionAction,
    primaryRowActions,
    resolveRowActionItems(row, resolvedActions, options) {
      return resolvedActions.flatMap((resolvedAction) => {
        const { action, actionRef, rowAction } = resolvedAction
        if (!evaluateProjectedRowActionPredicate(rowAction.IsVisible, row, true)) {
          return []
        }

        const actionContext = createProjectedRowActionExecutionContext(
          resolvedAction,
          row,
          createHref,
        )
        const isEnabled =
          evaluateProjectedRowActionPredicate(rowAction.IsEnabled, row, true) &&
          (options?.canExecuteAction?.(actionContext) ?? true)
        return [{
          action,
          actionContext,
          actionRef,
          isEnabled,
          rowAction,
        }]
      })
    },
    resolveSelectionActionItems(options) {
      if (selectedRowIds.length === 0) {
        return []
      }

      return (options?.selectionActions ?? selectionActions)
        .slice()
        .sort((left, right) => left.selectionAction.Order - right.selectionAction.Order)
        .flatMap((resolvedAction) => {
          const { action, actionRef, selectionAction } = resolvedAction
          if (!evaluateProjectedSelectionActionPredicate(
            selectionAction.IsVisible,
            selectedRows,
            true,
          )) {
            return []
          }

          const actionContext = createProjectedSelectionActionExecutionContext(
            resolvedAction,
            selectedRows,
            selectedRowIds,
            createHref,
          )
          const isEnabled =
            isSelectionActionSelectionCountEnabled(selectionAction, selectedRowIds.length) &&
            evaluateProjectedSelectionActionPredicate(
              selectionAction.IsEnabled,
              selectedRows,
              true,
            ) &&
            (options?.canExecuteAction?.(actionContext) ?? true)
          return [{
            action,
            actionContext,
            actionRef,
            isEnabled,
            selectionAction,
          }]
        })
    },
    rowActions,
    selectionActions,
  }
}

/**
 * Resolves row action references to concrete module actions.
 */
function resolveProjectedCollectionRowActions(
  module: ProjectedCollectionModuleDefinition | null | undefined,
  actionRefs: readonly PresentationViewProjectedActionRef[],
): readonly ResolvedProjectedCollectionRowAction[] {
  return sortResolvedCollectionRowActions(actionRefs.flatMap((actionRef) => {
    if (actionRef.contextKind !== 'collection-row' || !actionRef.rowAction) {
      return []
    }

    const action = findPresentationAction<ActionDefinition>(
      module ?? null,
      actionRef.actionId,
    )

    return action
      ? [{
          action,
          actionRef,
          rowAction: actionRef.rowAction,
          slotId: actionRef.slotId ?? null,
        }]
      : []
  }))
}

/**
 * Resolves selection action references to concrete module actions.
 */
function resolveProjectedCollectionSelectionActions(
  module: ProjectedCollectionModuleDefinition | null | undefined,
  actionRefs: readonly PresentationViewProjectedActionRef[],
): readonly ResolvedProjectedCollectionSelectionAction[] {
  return sortResolvedCollectionSelectionActions(actionRefs.flatMap((actionRef) => {
    if (actionRef.contextKind !== 'collection-selection' || !actionRef.selectionAction) {
      return []
    }

    const action = findPresentationAction<ActionDefinition>(
      module ?? null,
      actionRef.actionId,
    )

    return action
      ? [{
          action,
          actionRef,
          selectionAction: actionRef.selectionAction,
          slotId: actionRef.slotId ?? null,
        }]
      : []
  }))
}

/**
 * Sorts resolved row actions by declared action order.
 */
function sortResolvedCollectionRowActions(
  actions: readonly ResolvedProjectedCollectionRowAction[],
) {
  return actions.slice().sort((left, right) => left.rowAction.Order - right.rowAction.Order)
}

/**
 * Sorts resolved selection actions by declared action order.
 */
function sortResolvedCollectionSelectionActions(
  actions: readonly ResolvedProjectedCollectionSelectionAction[],
) {
  return actions
    .slice()
    .sort((left, right) => left.selectionAction.Order - right.selectionAction.Order)
}

/**
 * Resolves the row action invoked by row activation.
 *
 * An explicit activated-row action wins; otherwise the first primary row action
 * is used when row activation is enabled.
 */
function resolveActivatedCollectionRowAction(
  bodySlot: CollectionChromeSlotDefinition | null,
  rowActions: readonly ResolvedProjectedCollectionRowAction[],
) {
  if (!bodySlot?.ActivateOnRowClick) {
    return null
  }

  return bodySlot.ActivatedRowActionId
    ? rowActions.find(({ rowAction }) =>
        rowAction.Id === bodySlot.ActivatedRowActionId ||
        rowAction.ActionId === bodySlot.ActivatedRowActionId) ?? null
    : rowActions.find(({ rowAction }) => isPrimaryCollectionRowAction(rowAction)) ?? null
}

/**
 * Checks whether a row action is intended for the primary action surface.
 */
function isPrimaryCollectionRowAction(rowAction: CollectionRowActionDefinition) {
  return matchesProjectionEnum(rowAction.Kind, collectionRowActionKinds.primary, 'primary')
}

/**
 * Checks whether a row action is intended for contextual menu surfaces.
 */
function isContextMenuCollectionRowAction(rowAction: CollectionRowActionDefinition) {
  return matchesProjectionEnum(rowAction.Kind, collectionRowActionKinds.contextMenu, 'contextMenu')
}

/**
 * Creates a row action execution context with row-bound parameters and optional
 * navigation href.
 */
function createProjectedRowActionExecutionContext<TData extends object>(
  resolvedAction: ResolvedProjectedCollectionRowAction,
  row: TData,
  createHref:
    | ((routeId: string, parameters?: NavigationRouteParameters) => string | null)
    | undefined,
): ProjectedRowActionExecutionContext<TData> {
  const { action, actionRef, rowAction } = resolvedAction
  const parameters = createProjectedRowActionParameters(actionRef, action, row)
  return {
    action,
    actionRef,
    contextKind: 'collection-row',
    href: createProjectedActionHrefFromParameters(action, parameters, createHref),
    parameters,
    row,
    rowAction,
  }
}

/**
 * Creates a selection action execution context with selection-bound parameters
 * and optional navigation href.
 */
function createProjectedSelectionActionExecutionContext<TData extends object>(
  resolvedAction: ResolvedProjectedCollectionSelectionAction,
  selectedRows: readonly TData[],
  selectedRowIds: readonly string[],
  createHref:
    | ((routeId: string, parameters?: NavigationRouteParameters) => string | null)
    | undefined,
): ProjectedSelectionActionExecutionContext<TData> {
  const { action, actionRef, selectionAction } = resolvedAction
  const parameters = createProjectedSelectionActionParameters(
    actionRef,
    selectedRows,
    selectedRowIds,
  )
  return {
    action,
    actionRef,
    contextKind: 'collection-selection',
    href: createProjectedActionHrefFromParameters(action, parameters, createHref),
    parameters,
    selectedRowIds,
    selectedRows,
    selectionAction,
  }
}

/**
 * Builds a navigation href for actions that target a route.
 */
function createProjectedActionHrefFromParameters(
  action: ActionDefinition,
  parameters: NavigationRouteParameters,
  createHref:
    | ((routeId: string, parameters?: NavigationRouteParameters) => string | null)
    | undefined,
) {
  const routeId = action.Binding?.RouteId ?? action.Result?.NavigateToRouteId ?? null
  if (!routeId || !createHref) {
    return null
  }

  return createHref(routeId, parameters)
}

/**
 * Creates row action parameters from explicit row-action bindings when present,
 * otherwise from action parameter names read directly from the row.
 */
function createProjectedRowActionParameters<TData extends object>(
  actionRef: PresentationViewProjectedActionRef,
  action: ActionDefinition,
  row: TData,
) {
  const rowAction = actionRef.rowAction
  if (!rowAction) {
    return createProjectedActionParameters(row, action.Parameters ?? [])
  }

  return rowAction.Parameters.length > 0
    ? createProjectedBoundActionParameters(row, rowAction.Parameters)
    : createProjectedActionParameters(row, action.Parameters ?? [])
}

/**
 * Creates route parameters for a selection action from its declared selection
 * parameter bindings.
 */
function createProjectedSelectionActionParameters<TData extends object>(
  actionRef: PresentationViewProjectedActionRef,
  selectedRows: readonly TData[],
  selectedRowIds: readonly string[],
) {
  const selectionAction = actionRef.selectionAction
  if (!selectionAction) {
    return {}
  }

  const routeParameters: NavigationRouteParameters = {}
  for (const binding of selectionAction.Parameters) {
    const value = readProjectedSelectionActionParameterValue(
      binding,
      selectedRows,
      selectedRowIds,
    )
    if (value === null || value === undefined || isEmptySelectionParameterValue(value)) {
      if (!binding.OmitWhenEmpty) {
        routeParameters[binding.Name] = ''
      }
      continue
    }

    routeParameters[binding.Name] = Array.isArray(value)
      ? value.map(toRouteParameterValue).join(',')
      : toRouteParameterValue(value)
  }

  return routeParameters
}

/**
 * Reads the raw value for a selection action parameter binding.
 */
function readProjectedSelectionActionParameterValue<TData extends object>(
  binding: CollectionSelectionActionDefinition['Parameters'][number],
  selectedRows: readonly TData[],
  selectedRowIds: readonly string[],
) {
  if (matchesCollectionSelectionActionParameterSource(binding.Source, 'selectedRowIdentity')) {
    return selectedRowIds[0]
  }

  if (matchesCollectionSelectionActionParameterSource(binding.Source, 'selectedRowIdentityList')) {
    return selectedRowIds
  }

  if (matchesCollectionSelectionActionParameterSource(binding.Source, 'selectedRowValue')) {
    return binding.ValuePath && selectedRows[0]
      ? readProjectedCollectionValue(selectedRows[0], binding.ValuePath)
      : undefined
  }

  if (matchesCollectionSelectionActionParameterSource(binding.Source, 'selectedRowValueList')) {
    return binding.ValuePath
      ? selectedRows.map((row) => readProjectedCollectionValue(row, binding.ValuePath ?? ''))
      : []
  }

  if (matchesCollectionSelectionActionParameterSource(binding.Source, 'selectionCount')) {
    return selectedRowIds.length
  }

  return undefined
}

/**
 * Matches generated enum values and string fallback keys for selection action
 * parameter sources.
 */
function matchesCollectionSelectionActionParameterSource(
  source: CollectionSelectionActionDefinition['Parameters'][number]['Source'],
  key: keyof typeof collectionSelectionActionParameterSources,
) {
  return source === collectionSelectionActionParameterSources[key] ||
    String(source).toLocaleLowerCase() === key.toLocaleLowerCase()
}

/**
 * Checks whether a selection parameter should be treated as empty.
 */
function isEmptySelectionParameterValue(value: unknown) {
  return Array.isArray(value) && value.length === 0
}

/**
 * Creates route parameters from row action parameter bindings.
 */
function createProjectedBoundActionParameters<TData extends object>(
  row: TData,
  bindings: readonly CollectionRowActionParameterBindingDefinition[],
) {
  const routeParameters: NavigationRouteParameters = {}
  for (const binding of bindings) {
    const value = readProjectedCollectionValue(row, binding.ValuePath)
    if (value === null || value === undefined) {
      if (!binding.OmitWhenNull) {
        routeParameters[binding.Name] = ''
      }
      continue
    }

    routeParameters[binding.Name] = toRouteParameterValue(value)
  }

  return routeParameters
}

/**
 * Creates route parameters by reading action parameter names from a row object.
 */
function createProjectedActionParameters<TData extends object>(
  row: TData,
  parameters: readonly ProjectedCollectionParameterDefinition[],
) {
  const routeParameters: NavigationRouteParameters = {}
  for (const parameter of parameters) {
    const value = readObjectProperty(row, parameter.Name)
    if (value === null || value === undefined) {
      continue
    }

    routeParameters[parameter.Name] = toRouteParameterValue(value)
  }

  return routeParameters
}

/**
 * Checks host runtime visibility, disabled state, and optional `canExecute`
 * before allowing action invocation.
 */
function canInvokeProjectedCollectionAction<TData extends object>(
  context: ProjectedCollectionActionExecutionContext<TData>,
  actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
) {
  const runtime = resolveProjectedCollectionActionRuntime(actionRuntimes, context)
  if (runtime?.isHidden || runtime?.isDisabled) {
    return false
  }

  if (runtime?.execute) {
    return runtime.canExecute?.(context) ?? true
  }

  return false
}

/**
 * Row-action convenience wrapper around generic collection action invocation
 * checks.
 */
function canInvokeProjectedRowAction<TData extends object>(
  context: ProjectedRowActionExecutionContext<TData>,
  actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
) {
  return canInvokeProjectedCollectionAction(context, actionRuntimes)
}

/**
 * Invokes an action through the host action runtime when it is executable.
 */
async function invokeProjectedCollectionAction<TData extends object>(
  context: ProjectedCollectionActionExecutionContext<TData>,
  actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
) {
  const runtime = resolveProjectedCollectionActionRuntime(actionRuntimes, context)
  if (
    runtime?.execute &&
    !runtime.isHidden &&
    !runtime.isDisabled &&
    (runtime.canExecute?.(context) ?? true)
  ) {
    await runtime.execute(context)
    return
  }
}

/**
 * Row-action convenience wrapper around generic collection action invocation.
 */
async function invokeProjectedRowAction<TData extends object>(
  context: ProjectedRowActionExecutionContext<TData>,
  actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
) {
  await invokeProjectedCollectionAction(context, actionRuntimes)
}

/**
 * Selection-action convenience wrapper around generic collection action
 * invocation checks.
 */
function canInvokeProjectedSelectionAction<TData extends object>(
  context: ProjectedSelectionActionExecutionContext<TData>,
  actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
) {
  return canInvokeProjectedCollectionAction(context, actionRuntimes)
}

/**
 * Selection-action convenience wrapper around generic collection action
 * invocation.
 */
async function invokeProjectedSelectionAction<TData extends object>(
  context: ProjectedSelectionActionExecutionContext<TData>,
  actionRuntimes?: ProjectedCollectionActionRuntimeRegistry<TData>,
) {
  await invokeProjectedCollectionAction(context, actionRuntimes)
}

/**
 * Resolves the host action runtime by action identifier.
 */
function resolveProjectedCollectionActionRuntime<TData extends object>(
  actionRuntimes: ProjectedCollectionActionRuntimeRegistry<TData> | undefined,
  context: ProjectedCollectionActionExecutionContext<TData>,
) {
  return actionRuntimes?.[context.action.Id] ?? null
}

/**
 * Checks whether the current selection count is inside an action's declared
 * min/max bounds.
 */
function isSelectionActionSelectionCountEnabled(
  selectionAction: CollectionSelectionActionDefinition,
  selectionCount: number,
) {
  return selectionCount >= selectionAction.MinimumSelectionCount &&
    (
      selectionAction.MaximumSelectionCount === null ||
      selectionAction.MaximumSelectionCount === undefined ||
      selectionCount <= selectionAction.MaximumSelectionCount
    )
}

/**
 * Evaluates row action visibility or enablement predicates against a row.
 */
function evaluateProjectedRowActionPredicate<TData extends object>(
  predicate: CollectionRowActionDefinition['IsEnabled'],
  row: TData,
  fallback: boolean,
) {
  if (!predicate) {
    return fallback
  }

  if (
    predicate.Kind === presentationValueKinds.literal ||
    String(predicate.Kind).toLocaleLowerCase() === 'literal'
  ) {
    return coercePredicateValue(predicate.Literal, fallback)
  }

  if (
    predicate.Field &&
    (
      predicate.Kind === presentationValueKinds.field ||
      String(predicate.Kind).toLocaleLowerCase() === 'field'
    )
  ) {
    return coercePredicateValue(readProjectedCollectionValue(row, predicate.Field), fallback)
  }

  return fallback
}

/**
 * Evaluates selection action visibility or enablement predicates against the
 * first selected row.
 */
function evaluateProjectedSelectionActionPredicate<TData extends object>(
  predicate: CollectionSelectionActionDefinition['IsEnabled'],
  selectedRows: readonly TData[],
  fallback: boolean,
) {
  if (!predicate) {
    return fallback
  }

  if (
    predicate.Kind === presentationValueKinds.literal ||
    String(predicate.Kind).toLocaleLowerCase() === 'literal'
  ) {
    return coercePredicateValue(predicate.Literal, fallback)
  }

  if (
    predicate.Field &&
    selectedRows[0] &&
    (
      predicate.Kind === presentationValueKinds.field ||
      String(predicate.Kind).toLocaleLowerCase() === 'field'
    )
  ) {
    return coercePredicateValue(
      readProjectedCollectionValue(selectedRows[0], predicate.Field),
      fallback,
    )
  }

  return fallback
}

/**
 * Coerces presentation predicate values into booleans while preserving the
 * caller-provided fallback for unsupported values.
 */
function coercePredicateValue(value: unknown, fallback: boolean) {
  if (value === null || value === undefined) {
    return fallback
  }

  if (typeof value === 'boolean') {
    return value
  }

  if (typeof value === 'number') {
    return value !== 0
  }

  if (typeof value === 'string') {
    const normalized = value.trim().toLocaleLowerCase()
    if (normalized === 'true' || normalized === '1' || normalized === 'yes') {
      return true
    }

    if (normalized === 'false' || normalized === '0' || normalized === 'no' || normalized === '') {
      return false
    }
  }

  return fallback
}

/**
 * Converts arbitrary parameter values into route parameter primitives.
 */
function toRouteParameterValue(value: unknown) {
  return typeof value === 'boolean' || typeof value === 'number' || typeof value === 'string'
    ? value
    : String(value)
}

/**
 * Reads a projected collection value from a row.
 *
 * Exact object-path lookup is attempted first. If it is missing, the final path
 * segment is used as a direct property fallback for flatter data shapes.
 */
export function readProjectedCollectionValue<TData extends object>(
  row: TData,
  fieldPath: string,
) {
  const exactValue = readObjectPath(row, fieldPath)
  if (exactValue !== undefined) {
    return exactValue
  }

  const fieldName = fieldPath.split('.').at(-1) ?? fieldPath
  return readObjectProperty(row, fieldName)
}

/**
 * Reads a stable row identity from a row, falling back to the visible row index
 * when the declared identity path is missing.
 */
export function readProjectedCollectionRowIdentity<TData extends object>(
  row: TData,
  rowIdentityPath: string,
  index: number,
) {
  const value = readProjectedCollectionValue(row, rowIdentityPath)
  return value === null || value === undefined ? String(index) : String(value)
}

/**
 * Reads a display label for a row.
 *
 * The declared label path is preferred, followed by conventional `Name` and
 * `Id` properties.
 */
export function readProjectedCollectionRowLabel<TData extends object>(
  row: TData,
  rowLabelPath: string | null | undefined,
) {
  const pathValue = rowLabelPath
    ? readObjectPath(row, rowLabelPath)
    : undefined
  const value = pathValue ??
    readObjectProperty(row, 'Name') ??
    readObjectProperty(row, 'Id')
  return value === null || value === undefined ? null : String(value)
}

/**
 * Checks whether a collection selection mode enables any row selection.
 */
export function isCollectionSelectionEnabled(mode: CollectionSelectionMode) {
  return matchesProjectionEnum(mode, collectionSelectionModes.single, 'single') ||
    matchesProjectionEnum(mode, collectionSelectionModes.multiple, 'multiple')
}

/**
 * Checks whether a collection selection mode allows multiple selected rows.
 */
export function isMultipleCollectionSelection(mode: CollectionSelectionMode) {
  return matchesProjectionEnum(mode, collectionSelectionModes.multiple, 'multiple')
}

/**
 * Checks whether a detail slot can render as a persistent detail surface.
 */
function isRenderableCollectionDetailSlot(
  slot: CollectionChromeSlotDefinition | null,
) {
  return Boolean(
    slot?.DetailViewId &&
    !isCollectionDetailActivationNone(slot.DetailActivation) &&
    !isCollectionDetailActivationHover(slot.DetailActivation),
  )
}

/**
 * Checks whether detail activation is explicitly disabled.
 */
function isCollectionDetailActivationNone(activation: unknown) {
  return matchesProjectionEnum(activation, collectionDetailActivations.none, 'none')
}

/**
 * Checks whether detail activation is hover-only and therefore not renderable
 * as the selected-row detail surface.
 */
function isCollectionDetailActivationHover(activation: unknown) {
  return matchesProjectionEnum(activation, collectionDetailActivations.hover, 'hover')
}

/**
 * Matches generated enum values while tolerating string fallback keys from
 * non-generated or serialized projection inputs.
 */
function matchesProjectionEnum(
  value: unknown,
  generatedValue: unknown,
  fallbackKey: string,
) {
  return value === generatedValue ||
    String(value).toLocaleLowerCase() === fallbackKey.toLocaleLowerCase()
}
