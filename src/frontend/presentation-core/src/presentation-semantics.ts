import {
  findPresentationDataSource,
  findPresentationInputForm,
  findPresentationQueryForm,
  findPresentationView,
  findPresentationWorkspace,
  type ActionPlacementDefinition,
  type CollectionRowActionDefinition,
  type CollectionSelectionActionDefinition,
  type DataSourceDefinition,
  type FieldPresentationDefinition,
  type InputFormDefinition,
  type PresentationModuleDefinition,
  type QueryFormDefinition,
  type ViewDefinition,
  type ViewRegionDefinition,
  type WorkspaceDefinition,
} from './module'
import {
  findNavigationPageHost,
  type NavigationDefinitionProjection,
} from './navigation'
import {
  createCollectionChromeRuntime,
  resolveCollectionChromeDataSourceIds,
} from './collection-chrome-runtime'
import { getRegionViewIds } from './presentation-view-tree'
import {
  type NavigationRouteDefinition,
  navigationRouteKinds,
  type PageHostDefinition,
  collectionSelectionActionParameterSources,
  type ViewKind,
  viewChromeSlotKinds,
  viewKinds,
  type WorkspaceRefDefinition,
} from '@cohesive/presentation-contracts'

/**
 * Semantic navigation state resolved from a route. The route is addressable;
 * the surface it activates is resolved separately because navigation targets
 * and display surfaces are not always one-to-one.
 */
export interface NavigationTarget<
  TRoute extends NavigationRouteDefinition = NavigationRouteDefinition,
  TPageHost extends PageHostDefinition = PageHostDefinition,
> {
  /** Stable route identifier used as the navigation target identity. */
  readonly id: string

  /** Page host resolved from the route, or null when the module is incomplete. */
  readonly pageHost: TPageHost | null

  /** Identifier of the page host the route targets. */
  readonly pageHostId: string

  /** Concrete route definition that produced this navigation target. */
  readonly route: TRoute

  /** URL path template declared by the route. */
  readonly urlPathTemplate: string
}

/**
 * Top-level semantic scene activated by navigation/runtime state. Current
 * generated IR stores this as a page host with a root view; this type is the
 * compatibility surface over that representation.
 */
export interface PresentationSurface<
  TRoute extends NavigationRouteDefinition = NavigationRouteDefinition,
  TPageHost extends PageHostDefinition = PageHostDefinition,
  TView extends ViewDefinition = ViewDefinition,
  TWorkspace extends WorkspaceDefinition = WorkspaceDefinition,
> {
  /** Stable surface identity, currently backed by the resolved page host id. */
  readonly id: string

  /** Navigation target that activates this semantic surface. */
  readonly navigationTarget: NavigationTarget<TRoute, TPageHost>

  /** Root view rendered by the surface, when the page host references one. */
  readonly rootView: TView | null

  /** Identifier of the root view declared by the page host. */
  readonly rootViewId: string | null

  /** Workspace resolved for the page host, when declared. */
  readonly workspace: TWorkspace | null

  /** Workspace reference declared by the page host, before module resolution. */
  readonly workspaceRef: WorkspaceRefDefinition | null
}

/**
 * Union of semantic nodes projected from a presentation surface. Consumers use
 * these nodes to inspect the view tree without coupling to rendering runtime
 * structure.
 */
export type PresentationSemanticNode =
  | PresentationSurfaceSemanticNode
  | PresentationViewSemanticNode
  | PresentationRegionSemanticNode
  | PresentationFieldSemanticNode
  | PresentationActionSemanticNode
  | WorkspaceSemanticNode

/** Semantic graph node representing the activated presentation surface. */
export interface PresentationSurfaceSemanticNode {
  /** Surface definition represented by this node. */
  readonly definition: PresentationSurface

  /** Stable node identity. */
  readonly id: string

  /** Node discriminator. */
  readonly kind: 'surface'
}

/** Semantic graph node representing a view in the surface tree. */
export interface PresentationViewSemanticNode {
  /** View definition represented by this node. */
  readonly definition: ViewDefinition

  /** Stable view identifier. */
  readonly id: string

  /** Node discriminator. */
  readonly kind: 'view'
}

/** Semantic graph node representing a region owned by a view. */
export interface PresentationRegionSemanticNode {
  /** Region definition represented by this node. */
  readonly definition: ViewRegionDefinition

  /** Stable node identity scoped by owner view and region id. */
  readonly id: string

  /** Node discriminator. */
  readonly kind: 'region'

  /** Identifier of the view that owns this region. */
  readonly ownerViewId: string
}

/** Semantic graph node representing a field projected into a view. */
export interface PresentationFieldSemanticNode {
  /** Field definition represented by this node. */
  readonly definition: FieldPresentationDefinition

  /** Stable node identity including the projection source. */
  readonly id: string

  /** Node discriminator. */
  readonly kind: 'field'

  /** Identifier of the view that projected this field. */
  readonly ownerViewId: string
}

/** Semantic graph node representing an action projected into a view. */
export interface PresentationActionSemanticNode {
  /** Projected action reference represented by this node. */
  readonly definition: PresentationViewProjectedActionRef

  /** Stable node identity including the projection source. */
  readonly id: string

  /** Node discriminator. */
  readonly kind: 'action'

  /** Identifier of the view that projected this action. */
  readonly ownerViewId: string
}

/** Semantic graph node representing a workspace attached to the surface. */
export interface WorkspaceSemanticNode {
  /** Workspace definition represented by this node. */
  readonly definition: WorkspaceDefinition

  /** Stable workspace identifier. */
  readonly id: string

  /** Node discriminator. */
  readonly kind: 'workspace'
}

/**
 * Reference to a field projected into a view by direct view fields or
 * collection chrome declarations.
 */
export interface PresentationViewProjectedFieldRef {
  /** Identifier of the field resolved against the containing module. */
  readonly fieldId: string

  /** Projection source used for diagnostics and stable semantic node ids. */
  readonly source: string
}

/** Source category for an action projected into a view. */
export type PresentationViewProjectedActionKind =
  | 'view-action-placement'
  | 'view-chrome-action-placement'
  | 'collection-slot-action'
  | 'collection-row-action'
  | 'collection-selection-action'

/** Runtime context in which a projected action can be invoked. */
export type PresentationActionContextKind =
  | 'view'
  | 'collection-row'
  | 'collection-selection'

/**
 * Invocation context contributed by the view projection. This captures the
 * collection/view state required to bind an action without making the module
 * action definition collection-aware.
 */
export interface PresentationViewProjectedActionContext {
  /** Collection view that projected the action, when applicable. */
  readonly collectionViewId?: string | null

  /** Invocation context for the projected action. */
  readonly contextKind: PresentationActionContextKind

  /** Object path used to derive the active row identity. */
  readonly rowIdentityPath?: string | null

  /** Row value paths required to bind row action parameters. */
  readonly requiredRowValuePaths?: readonly string[]

  /** Selected-row value paths required to bind selection action parameters. */
  readonly requiredSelectedRowValuePaths?: readonly string[]

  /** Object path used to derive selected row identities. */
  readonly selectedRowIdentityPath?: string | null

  /** Selection state id shared by collection chrome slots. */
  readonly selectionStateId?: string | null

  /** Collection or view chrome slot that projected the action. */
  readonly slotId?: string | null
}

// TODO: Model projected action refs as placement/context enrichment over the
// resolved ActionDefinition instead of carrying duplicated action identity and
// collection action metadata through downstream runtime shapes.
/**
 * View-local projection of a module action. The ref preserves placement,
 * context, source, and collection binding metadata that are not owned by the
 * canonical ActionDefinition.
 */
export interface PresentationViewProjectedActionRef {
  /** Identifier of the module action being projected. */
  readonly actionId: string

  /** View-projected invocation context. */
  readonly context: PresentationViewProjectedActionContext

  /** Convenience discriminator mirrored from context.contextKind. */
  readonly contextKind: PresentationActionContextKind

  /** Stable projected-action identity scoped by projection kind and source. */
  readonly id: string

  /** Source category that introduced the projected action. */
  readonly kind: PresentationViewProjectedActionKind

  /** Placement metadata used by action surfaces and renderers. */
  readonly placement: ActionPlacementDefinition

  /** Collection row action binding metadata, when this is row-scoped. */
  readonly rowAction?: CollectionRowActionDefinition

  /** Collection selection action binding metadata, when this is selection-scoped. */
  readonly selectionAction?: CollectionSelectionActionDefinition

  /** Chrome slot that introduced the action, when known. */
  readonly slotId?: string

  /** Human-readable projection source for diagnostics and stable ids. */
  readonly source: string
}

type PresentationDataSourceDiscoveryModule = {
  readonly DataSources?: readonly DataSourceDefinition[]
  readonly InputForms?: readonly InputFormDefinition[]
  readonly QueryForms?: readonly QueryFormDefinition[]
  readonly Views: readonly ViewDefinition[]
}

/**
 * Resolves the semantic navigation target for a route. The returned value keeps
 * the original route and the resolved page host together so later projection
 * steps can preserve both route semantics and display host metadata.
 *
 * @param navigation Navigation projection that owns page hosts.
 * @param route Route definition to resolve.
 */
export function resolveNavigationTarget<
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
>(
  navigation: TNavigation,
  route: TRoute,
): NavigationTarget<TRoute, TPageHost> {
  const pageHost = findNavigationPageHost<TPageHost>(navigation, route.PageHostId)
  return {
    id: route.Id,
    pageHost,
    pageHostId: route.PageHostId,
    route,
    urlPathTemplate: route.PathTemplate,
  }
}

/**
 * Resolves the presentation surface activated by a navigation target. Returns
 * null when the target cannot be connected to a page host.
 *
 * @param module Presentation module fragment containing views and workspaces.
 * @param navigationTarget Route-derived target to resolve into a surface.
 */
export function resolvePresentationSurface<
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TView extends ViewDefinition = ViewDefinition,
  TWorkspace extends WorkspaceDefinition = WorkspaceDefinition,
>(
  module: Pick<PresentationModuleDefinition, 'Views' | 'Workspaces'> | null,
  navigationTarget: NavigationTarget<TRoute, TPageHost>,
): PresentationSurface<TRoute, TPageHost, TView, TWorkspace> | null {
  const pageHost = navigationTarget.pageHost
  if (!pageHost) {
    return null
  }

  const rootViewId = pageHost.View?.ViewId ?? null
  const rootView = rootViewId ? findPresentationView<TView>(module, rootViewId) : null
  const workspaceRef = pageHost.Workspace ?? null
  const workspace = workspaceRef
    ? findPresentationWorkspace<TWorkspace>(module, workspaceRef.WorkspaceId)
    : null

  return {
    id: pageHost.Id,
    navigationTarget,
    rootView,
    rootViewId,
    workspace,
    workspaceRef,
  }
}

/**
 * Creates a lightweight surface around a known root view. This is useful for
 * runtimes and tests that start from a view instead of a navigation route.
 *
 * @param rootView Root view to expose as a surface.
 * @param options Optional synthetic identity and URL path template.
 */
export function createPresentationSurfaceFromRootView<
  TView extends ViewDefinition = ViewDefinition,
>(
  rootView: TView | null,
  options?: {
    readonly id?: string
    readonly urlPathTemplate?: string
  },
): PresentationSurface<
  NavigationRouteDefinition,
  PageHostDefinition,
  TView,
  WorkspaceDefinition
> {
  const id = options?.id ?? rootView?.Id ?? 'presentation-surface'
  const urlPathTemplate = options?.urlPathTemplate ?? ''
  const route: NavigationRouteDefinition = {
    Id: id,
    Kind: navigationRouteKinds.page,
    Label: id,
    PageHostId: id,
    Parameters: [],
    PathTemplate: urlPathTemplate,
  }

  return {
    id,
    navigationTarget: {
      id,
      pageHost: null,
      pageHostId: id,
      route,
      urlPathTemplate,
    },
    rootView,
    rootViewId: rootView?.Id ?? options?.id ?? null,
    workspace: null,
    workspaceRef: null,
  }
}

/**
 * Determines whether a view should be treated as the root of a presentation
 * surface based on generated view kind or design role.
 *
 * @param view View fragment to classify.
 */
export function isPresentationSurfaceRootView(
  view: Pick<ViewDefinition, 'Design' | 'Kind'> | null,
) {
  return (
    matchesPresentationViewKind(view?.Kind, viewKinds.page, 'Page') ||
    view?.Design?.Role === 'page' ||
    view?.Design?.Role === 'surface-root'
  )
}

/**
 * Resolves the semantic role a view plays inside a surface. Design roles win
 * when they provide specific intent; generated view kinds provide fallback
 * semantics for page, workspace, and surface-section views.
 *
 * @param view View fragment to classify.
 */
export function getPresentationViewSemanticRole(
  view: Pick<ViewDefinition, 'Design' | 'Kind'> | null,
) {
  if (!view) {
    return 'unknown'
  }

  if (isPresentationSurfaceRootView(view)) {
    return 'surface-root'
  }

  if (matchesPresentationViewKind(view.Kind, viewKinds.documentWorkspace, 'DocumentWorkspace')) {
    return 'workspace-view'
  }

  const designRole = view.Design?.Role
  if (designRole && designRole !== 'surface' && designRole !== 'tabs') {
    return designRole
  }

  if (
    matchesPresentationViewKind(view.Kind, viewKinds.surface, 'Surface') ||
    matchesPresentationViewKind(view.Kind, viewKinds.tabbedSurface, 'TabbedSurface')
  ) {
    return 'surface-section'
  }

  return designRole ?? 'view'
}

/**
 * Returns every data source directly projected by a view, including subject
 * data, direct view data sources, and collection chrome data sources.
 *
 * @param view View fragment containing data-source projection declarations.
 */
export function getPresentationViewProjectedDataSourceIds(
  view: Pick<ViewDefinition, 'Collection' | 'DataSourceIds' | 'Subject'>,
) {
  return uniqueStrings([
    ...view.DataSourceIds,
    ...(view.Subject.DataSourceId ? [view.Subject.DataSourceId] : []),
    ...resolveCollectionChromeDataSourceIds(view.Collection),
  ])
}

/**
 * Returns unique field ids projected by a view through direct fields,
 * collection body columns, and collection chrome field slots.
 *
 * @param view View fragment containing field projection declarations.
 */
export function getPresentationViewProjectedFieldIds(
  view: Pick<ViewDefinition, 'Collection' | 'FieldIds'>,
) {
  return uniqueStrings(getPresentationViewProjectedFieldRefs(view).map((ref) => ref.fieldId))
}

/**
 * Returns field references projected by a view with source provenance for each
 * projection. The same field can appear through multiple sources.
 *
 * @param view View fragment containing direct and collection field projections.
 */
export function getPresentationViewProjectedFieldRefs(
  view: Pick<ViewDefinition, 'Collection' | 'FieldIds'>,
): readonly PresentationViewProjectedFieldRef[] {
  const refs: PresentationViewProjectedFieldRef[] = [
    ...view.FieldIds.map((fieldId) => ({ fieldId, source: 'view-field' })),
  ]

  const chrome = createCollectionChromeRuntime(view.Collection)
  for (const column of chrome.bodySlot?.Columns ?? []) {
    refs.push({
      fieldId: column.FieldId,
      source: `collection-body-column.${column.Id}`,
    })
  }

  for (const slot of chrome.slots) {
    for (const fieldId of slot.FieldIds) {
      refs.push({
        fieldId,
        source: `collection-chrome-slot-field.${slot.Id}`,
      })
    }
  }

  return refs
}

/**
 * Returns all actions projected into a view. The result normalizes direct view
 * placements, view chrome actions, collection slot actions, row actions, and
 * selection actions into a single view-local action reference model.
 *
 * @param view View fragment containing action and collection declarations.
 */
export function getPresentationViewProjectedActions(
  view: Pick<ViewDefinition, 'Actions' | 'Chrome' | 'Collection' | 'Id'> | null,
): readonly PresentationViewProjectedActionRef[] {
  if (!view) {
    return []
  }

  const actions: PresentationViewProjectedActionRef[] = []

  for (const placement of view.Actions) {
    actions.push(createProjectedActionRef({
      kind: 'view-action-placement',
      placement,
      context: {
        contextKind: 'view',
      },
      source: `view-action.${placement.Region}.${placement.ActionId}`,
    }))
  }

  for (const slot of view.Chrome?.Slots ?? []) {
    if (!isViewActionsChromeSlotKind(slot.Kind)) {
      continue
    }

    for (const placement of slot.Actions) {
      actions.push(createProjectedActionRef({
        kind: 'view-chrome-action-placement',
        placement,
        context: {
          contextKind: 'view',
          slotId: slot.Id,
        },
        slotId: slot.Id,
        source: `view-chrome-slot.${slot.Id}.${placement.Region}.${placement.ActionId}`,
      }))
    }
  }

  const chrome = createCollectionChromeRuntime(view.Collection)
  const collectionViewId = view.Id
  const bodySlot = chrome.bodySlot
  const selectionStateId = bodySlot?.StateId ??
    chrome.selectionActionsSlot?.StateId ??
    chrome.detailSlot?.StateId ??
    null

  for (const slot of chrome.slots) {
    const explicitlyBoundSlotActionIds = new Set([
      ...slot.RowActions.map((rowAction) => rowAction.ActionId),
      ...slot.SelectionActions.map((selectionAction) => selectionAction.ActionId),
    ])
    for (const actionId of slot.ActionIds) {
      if (explicitlyBoundSlotActionIds.has(actionId)) {
        continue
      }

      actions.push(createProjectedActionRef({
        kind: 'collection-slot-action',
        placement: {
          ActionId: actionId,
          Region: `collection:${slot.Id}`,
        },
        context: {
          collectionViewId,
          contextKind: 'view',
          slotId: slot.Id,
        },
        slotId: slot.Id,
        source: `collection-chrome-slot.${slot.Id}.action.${actionId}`,
      }))
    }

    for (const rowAction of slot.RowActions) {
      actions.push(createProjectedActionRef({
        kind: 'collection-row-action',
        placement: {
          ActionId: rowAction.ActionId,
          Icon: rowAction.Icon,
          Label: rowAction.Label,
          Region: `collection:${slot.Id}:row`,
        },
        context: {
          collectionViewId,
          contextKind: 'collection-row',
          requiredRowValuePaths: rowAction.Parameters.map((parameter) => parameter.ValuePath),
          rowIdentityPath: bodySlot?.RowIdentityPath ?? null,
          slotId: slot.Id,
        },
        rowAction,
        slotId: slot.Id,
        source: `collection-row-action.${slot.Id}.${rowAction.Id}`,
      }))
    }

    for (const selectionAction of slot.SelectionActions) {
      actions.push(createProjectedActionRef({
        kind: 'collection-selection-action',
        placement: {
          ActionId: selectionAction.ActionId,
          Icon: selectionAction.Icon,
          Label: selectionAction.Label,
          Region: `collection:${slot.Id}:selection`,
        },
        context: {
          collectionViewId,
          contextKind: 'collection-selection',
          requiredSelectedRowValuePaths:
            resolveSelectionActionRequiredSelectedRowValuePaths(selectionAction),
          selectedRowIdentityPath: bodySlot?.RowIdentityPath ?? null,
          selectionStateId,
          slotId: slot.Id,
        },
        selectionAction,
        slotId: slot.Id,
        source: `collection-selection-action.${slot.Id}.${selectionAction.Id}`,
      }))
    }
  }

  return uniqueProjectedActionRefs(actions)
}

/**
 * Returns normalized action placements projected by a view, optionally filtered
 * by invocation context. Defaults to view-scoped actions.
 *
 * @param view View fragment containing action and collection declarations.
 * @param options Projection filters.
 */
export function getPresentationViewProjectedActionPlacements(
  view: Pick<ViewDefinition, 'Actions' | 'Chrome' | 'Collection' | 'Id'> | null,
  options?: {
    readonly contextKinds?: readonly PresentationActionContextKind[]
  },
): readonly ActionPlacementDefinition[] {
  const contextKinds = new Set(options?.contextKinds ?? ['view'])
  return getPresentationViewProjectedActions(view)
    .filter((action) => contextKinds.has(action.contextKind))
    .map((action) => action.placement)
}

function matchesPresentationViewKind(
  value: ViewDefinition['Kind'] | null | undefined,
  numericValue: ViewKind,
  label: string,
) {
  return (
    value === numericValue ||
    String(value) === String(numericValue) ||
    String(value) === label ||
    String(value) === label.charAt(0).toLowerCase() + label.slice(1)
  )
}

/**
 * Returns the depth-first view tree for a presentation surface root. Region
 * child view references determine traversal order, and repeated references are
 * visited once.
 *
 * @param module Module fragment containing views available for traversal.
 * @param surface Surface whose root view anchors the tree.
 */
export function getPresentationSurfaceViewTree(
  module: { readonly Views: readonly ViewDefinition[] } | null,
  surface: Pick<PresentationSurface, 'rootView'> | null,
) {
  const rootView = surface?.rootView ?? null
  if (!module || !rootView) {
    return []
  }

  const viewsById = new Map(module.Views.map((view) => [view.Id, view] as const))
  const visited = new Set<string>()
  const orderedViews: ViewDefinition[] = []

  visit(rootView)
  return orderedViews

  function visit(view: ViewDefinition) {
    if (visited.has(view.Id)) {
      return
    }

    visited.add(view.Id)
    orderedViews.push(view)

    for (const region of view.Regions) {
      for (const childViewId of getRegionViewIds(region)) {
        const childView = viewsById.get(childViewId)
        if (childView) {
          visit(childView)
        }
      }
    }
  }
}

/**
 * Returns the transitive data-source closure needed by a presentation surface.
 * The closure includes view projections, subject forms, query forms, collection
 * slot forms, region data sources, and data-source dependencies.
 *
 * @param module Module fragment containing views, forms, and data sources.
 * @param surface Surface whose view tree determines required data sources.
 */
export function getPresentationSurfaceDataSourceIds(
  module: PresentationDataSourceDiscoveryModule | null,
  surface: Pick<PresentationSurface, 'rootView'> | null,
) {
  const dataSourceIds = new Set<string>()
  for (const view of getPresentationSurfaceViewTree(module, surface)) {
    collectPresentationViewDataSourceIds(dataSourceIds, module, view)
  }

  expandPresentationDataSourceDependencies(dataSourceIds, module)
  return Array.from(dataSourceIds)
}

function collectPresentationViewDataSourceIds(
  dataSourceIds: Set<string>,
  module: PresentationDataSourceDiscoveryModule | null,
  view: ViewDefinition,
) {
  addDataSourceIds(dataSourceIds, getPresentationViewProjectedDataSourceIds(view))
  collectInputFormDataSourceIds(dataSourceIds, module, view.Subject.InputFormId)
  collectQueryFormDataSourceIds(dataSourceIds, module, view.Subject.QueryFormId)

  const collection = view.Collection
  if (collection) {
    for (const slot of createCollectionChromeRuntime(collection).slots) {
      collectQueryFormDataSourceIds(dataSourceIds, module, slot.QueryFormId)
    }
  }

  for (const region of view.Regions) {
    addDataSourceIds(dataSourceIds, region.DataSourceIds)
  }
}

function collectQueryFormDataSourceIds(
  dataSourceIds: Set<string>,
  module: PresentationDataSourceDiscoveryModule | null,
  queryFormId: string | null | undefined,
) {
  if (!queryFormId) {
    return
  }

  const queryForm = module?.QueryForms
    ? findPresentationQueryForm({ QueryForms: module.QueryForms }, queryFormId)
    : null
  if (!queryForm) {
    return
  }

  const state = queryForm.Target.State
  addDataSourceIds(dataSourceIds, [
    state.DraftDataSourceId,
    state.AppliedDataSourceId,
    state.ResultDataSourceId,
    ...state.SynchronizedDataSourceIds,
    queryForm.Target.Result.DataSourceId,
  ])
  collectInputFormDataSourceIds(dataSourceIds, module, queryForm.FormId)
}

function collectInputFormDataSourceIds(
  dataSourceIds: Set<string>,
  module: PresentationDataSourceDiscoveryModule | null,
  inputFormId: string | null | undefined,
) {
  if (!inputFormId) {
    return
  }

  const inputForm = module?.InputForms
    ? findPresentationInputForm({ InputForms: module.InputForms }, inputFormId)
    : null
  if (!inputForm) {
    return
  }

  addDataSourceIds(dataSourceIds, [
    inputForm.StateDataSourceId,
    inputForm.Target.DataSourceId,
  ])

  for (const field of inputForm.Fields) {
    addDataSourceId(dataSourceIds, field.ChoiceSource?.DataSourceId)
  }

  for (const suggestion of inputForm.Suggestions) {
    addDataSourceId(dataSourceIds, suggestion.DataSourceId)
  }
}

function expandPresentationDataSourceDependencies(
  dataSourceIds: Set<string>,
  module: PresentationDataSourceDiscoveryModule | null,
) {
  if (!module?.DataSources) {
    return
  }

  const pending = Array.from(dataSourceIds)
  const visited = new Set<string>()
  for (let index = 0; index < pending.length; index += 1) {
    const dataSourceId = pending[index]
    if (visited.has(dataSourceId)) {
      continue
    }

    visited.add(dataSourceId)
    const dataSource = findPresentationDataSource(
      { DataSources: module.DataSources },
      dataSourceId,
    )
    if (!dataSource) {
      continue
    }

    for (const dependencyId of resolveDataSourceDependencyIds(dataSource)) {
      if (!dataSourceIds.has(dependencyId)) {
        dataSourceIds.add(dependencyId)
        pending.push(dependencyId)
      }
    }
  }
}

function resolveDataSourceDependencyIds(dataSource: DataSourceDefinition) {
  return [
    ...(dataSource.Query?.FactDataSourceIds ?? []),
    ...(dataSource.Query?.Fields.flatMap((field) =>
      field.ChoiceDataSourceId ? [field.ChoiceDataSourceId] : []) ?? []),
    dataSource.Query?.Pagination?.Response.TotalCountDataSourceId,
    dataSource.Aggregation?.SourceDataSourceId,
  ].filter((dataSourceId): dataSourceId is string => Boolean(dataSourceId))
}

function addDataSourceIds(
  dataSourceIds: Set<string>,
  ids: readonly (string | null | undefined)[],
) {
  for (const id of ids) {
    addDataSourceId(dataSourceIds, id)
  }
}

function addDataSourceId(
  dataSourceIds: Set<string>,
  id: string | null | undefined,
) {
  if (id) {
    dataSourceIds.add(id)
  }
}

function uniqueStrings(values: readonly string[]) {
  return Array.from(new Set(values))
}

function createProjectedActionRef({
  context,
  kind,
  placement,
  rowAction,
  selectionAction,
  slotId,
  source,
}: {
  readonly context: PresentationViewProjectedActionContext
  readonly kind: PresentationViewProjectedActionKind
  readonly placement: ActionPlacementDefinition
  readonly rowAction?: CollectionRowActionDefinition
  readonly selectionAction?: CollectionSelectionActionDefinition
  readonly slotId?: string
  readonly source: string
}): PresentationViewProjectedActionRef {
  return {
    actionId: placement.ActionId,
    context,
    contextKind: context.contextKind,
    id: `${kind}:${source}`,
    kind,
    placement,
    rowAction,
    selectionAction,
    slotId,
    source,
  }
}

function resolveSelectionActionRequiredSelectedRowValuePaths(
  selectionAction: CollectionSelectionActionDefinition,
) {
  return selectionAction.Parameters.flatMap((parameter) =>
    isSelectedRowValueSelectionActionParameterSource(parameter.Source) && parameter.ValuePath
      ? [parameter.ValuePath]
      : [])
}

function isSelectedRowValueSelectionActionParameterSource(source: unknown) {
  return source === collectionSelectionActionParameterSources.selectedRowValue ||
    source === collectionSelectionActionParameterSources.selectedRowValueList ||
    String(source).toLocaleLowerCase() === 'selectedrowvalue' ||
    String(source).toLocaleLowerCase() === 'selectedrowvaluelist'
}

function uniqueProjectedActionRefs(
  actions: readonly PresentationViewProjectedActionRef[],
) {
  const seen = new Set<string>()
  return actions.filter((action) => {
    const key = `${action.kind}:${action.source}:${action.actionId}`
    if (seen.has(key)) {
      return false
    }

    seen.add(key)
    return true
  })
}

function isViewActionsChromeSlotKind(value: string | number) {
  return value === viewChromeSlotKinds.actions ||
    String(value).toLocaleLowerCase() === 'actions'
}

/**
 * Projects a surface into semantic graph nodes for inspection, diagnostics, and
 * renderer-independent traversal. The graph includes the surface, workspace,
 * reachable views, regions, projected fields, and projected actions.
 *
 * @param module Module fragment containing fields, views, and workspaces.
 * @param surface Surface to project into semantic nodes.
 */
export function getPresentationSurfaceSemanticNodes(
  module: Pick<PresentationModuleDefinition, 'Fields' | 'Views' | 'Workspaces'> | null,
  surface: PresentationSurface | null,
): readonly PresentationSemanticNode[] {
  if (!module || !surface) {
    return []
  }

  const fieldsById = new Map(module.Fields.map((field) => [field.Id, field] as const))
  const nodes: PresentationSemanticNode[] = [
    {
      definition: surface,
      id: surface.id,
      kind: 'surface',
    },
  ]

  if (surface.workspace) {
    nodes.push({
      definition: surface.workspace,
      id: surface.workspace.Id,
      kind: 'workspace',
    })
  }

  for (const view of getPresentationSurfaceViewTree(module, surface)) {
    nodes.push({
      definition: view,
      id: view.Id,
      kind: 'view',
    })

    for (const region of view.Regions) {
      nodes.push({
        definition: region,
        id: `${view.Id}:${region.Id}`,
        kind: 'region',
        ownerViewId: view.Id,
      })
    }

    for (const fieldRef of getPresentationViewProjectedFieldRefs(view)) {
      const fieldId = fieldRef.fieldId
      const field = fieldsById.get(fieldId)
      if (field) {
        nodes.push({
          definition: field,
          id: `${view.Id}:${fieldRef.source}:${field.Id}`,
          kind: 'field',
          ownerViewId: view.Id,
        })
      }
    }

    for (const action of getPresentationViewProjectedActions(view)) {
      nodes.push({
        definition: action,
        id: `${view.Id}:${action.source}`,
        kind: 'action',
        ownerViewId: view.Id,
      })
    }
  }

  return nodes
}
