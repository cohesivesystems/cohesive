import type {
  NavigationRouteDefinition,
  PageHostDefinition,
  ViewDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  findNavigationRoute,
  resolveNavigationRouteId,
  type NavigationDefinitionProjection,
} from './navigation'
import {
  getPresentationSurfaceDataSourceIds,
  getPresentationSurfaceViewTree,
  getPresentationViewProjectedActions,
  getPresentationViewProjectedDataSourceIds,
  getPresentationViewProjectedFieldIds,
  getPresentationViewSemanticRole,
  resolveNavigationTarget,
} from './presentation-semantics'
import { getRegionViewIds } from './presentation-view-tree'

export interface PresentationProjectionTraceModule {
  readonly Views: readonly ViewDefinition[]
}

export interface CreatePresentationProjectionTraceOptions<
  TModule extends PresentationProjectionTraceModule,
  TRoute extends NavigationRouteDefinition = NavigationRouteDefinition,
  TPageHost extends PageHostDefinition = PageHostDefinition,
> {
  readonly componentSet?: string
  readonly module: TModule | null
  readonly navigation: NavigationDefinitionProjection<TRoute, TPageHost> | null
  readonly pathname: string
  readonly resolvePageHostRenderer?: PresentationProjectionTracePageHostRendererResolver<
    TModule,
    TRoute,
    TPageHost
  >
  readonly resolveViewRenderer?: PresentationProjectionTraceViewRendererResolver<TModule>
}

export interface PresentationProjectionTracePageHostRendererResolutionContext<
  TModule extends PresentationProjectionTraceModule,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
> {
  readonly componentSet?: string
  readonly module: TModule
  readonly pageHost: TPageHost | null
  readonly route: TRoute | null
}

export type PresentationProjectionTracePageHostRendererResolver<
  TModule extends PresentationProjectionTraceModule,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
> = (
  context: PresentationProjectionTracePageHostRendererResolutionContext<
    TModule,
    TRoute,
    TPageHost
  >,
) => PresentationProjectionTracePageHostRenderer | null

export interface PresentationProjectionTraceViewRendererResolutionContext<
  TModule extends PresentationProjectionTraceModule,
> {
  readonly componentSet?: string
  readonly module: TModule
  readonly routeId: string | null
  readonly view: ViewDefinition
}

export interface PresentationProjectionTraceViewRendererResolution {
  readonly componentKey: string | null
  readonly componentRole: string | null
  readonly rendererResolved: boolean
  readonly resolutionSource: string | null
  readonly semanticRole?: string | null
}

export type PresentationProjectionTraceViewRendererResolver<
  TModule extends PresentationProjectionTraceModule,
> = (
  context: PresentationProjectionTraceViewRendererResolutionContext<TModule>,
) => PresentationProjectionTraceViewRendererResolution | null

export interface PresentationProjectionTrace {
  readonly dataSourceIds: readonly string[]
  readonly moduleAvailable: boolean
  readonly pageHost: PresentationProjectionTracePageHost | null
  readonly pageHostRenderer: PresentationProjectionTracePageHostRenderer | null
  readonly pathname: string
  readonly route: PresentationProjectionTraceRoute | null
  readonly surface: PresentationProjectionTraceSurface | null
  readonly views: readonly PresentationProjectionTraceView[]
}

export interface PresentationProjectionTraceRoute {
  readonly id: string
  readonly label: string
  readonly pageHostId: string
  readonly pathTemplate: string
}

export interface PresentationProjectionTracePageHost {
  readonly documentProfileId: string | null
  readonly id: string
  readonly kind: string
  readonly viewId: string | null
  readonly workspaceId: string | null
}

export interface PresentationProjectionTracePageHostRenderer {
  readonly componentKey: string | null
  readonly componentRole: string | null
  readonly rendererKey: string | null
  readonly resolutionSource: string | null
  readonly semanticRole: string | null
  readonly targetBindingSource: string | null
}

export interface PresentationProjectionTraceSurface {
  readonly id: string
  readonly rootViewId: string | null
  readonly workspaceId: string | null
}

export interface PresentationProjectionTraceView {
  readonly actionCount: number
  readonly componentKey: string | null
  readonly componentRole: string | null
  readonly dataSourceIds: readonly string[]
  readonly fieldIds: readonly string[]
  readonly id: string
  readonly kind: string
  readonly name: string
  readonly regions: readonly PresentationProjectionTraceRegion[]
  readonly rendererResolved: boolean
  readonly resolutionSource: string | null
  readonly semanticRole: string
  readonly subjectDataSourceId: string | null
}

export interface PresentationProjectionTraceRegion {
  readonly dataSourceIds: readonly string[]
  readonly id: string
  readonly viewIds: readonly string[]
}

/**
 * Builds a read-only trace of route, page-host, surface, view, and renderer
 * resolution. Framework adapters can supply renderer resolver functions; the
 * trace model itself remains pure presentation IR/runtime state.
 */
export function createPresentationProjectionTrace<
  TModule extends PresentationProjectionTraceModule,
  TRoute extends NavigationRouteDefinition = NavigationRouteDefinition,
  TPageHost extends PageHostDefinition = PageHostDefinition,
>({
  componentSet,
  module,
  navigation,
  pathname,
  resolvePageHostRenderer,
  resolveViewRenderer,
}: CreatePresentationProjectionTraceOptions<
  TModule,
  TRoute,
  TPageHost
>): PresentationProjectionTrace {
  const routeId = resolveNavigationRouteId(navigation, pathname)
  const route = navigation && routeId
    ? findNavigationRoute<TRoute>(navigation, routeId)
    : null
  const navigationTarget = navigation && route
    ? resolveNavigationTarget<
        NavigationDefinitionProjection<TRoute, TPageHost>,
        TRoute,
        TPageHost
      >(navigation, route)
    : null
  const pageHost = navigationTarget?.pageHost ?? null
  const rootViewId = pageHost?.View?.ViewId ?? null
  const rootView = module && rootViewId
    ? module.Views.find((view) => view.Id === rootViewId) ?? null
    : null
  const surface = pageHost
    ? {
        id: pageHost.Id,
        rootView,
        rootViewId,
        workspaceRef: pageHost.Workspace,
      }
    : null
  const viewTree = module
    ? getPresentationSurfaceViewTree(module, { rootView })
    : []
  const pageHostRenderer = module && resolvePageHostRenderer
    ? resolvePageHostRenderer({
        componentSet,
        module,
        pageHost,
        route,
      })
    : null

  return {
    dataSourceIds: module
      ? getPresentationSurfaceDataSourceIds(module, { rootView })
      : [],
    moduleAvailable: Boolean(module),
    pageHost: pageHost
      ? {
          documentProfileId: pageHost.Workspace?.DocumentProfileId ?? null,
          id: pageHost.Id,
          kind: String(pageHost.Kind),
          viewId: rootViewId,
          workspaceId: pageHost.Workspace?.WorkspaceId ?? null,
        }
      : null,
    pageHostRenderer,
    pathname,
    route: route
      ? {
          id: route.Id,
          label: route.Label,
          pageHostId: route.PageHostId,
          pathTemplate: route.PathTemplate,
        }
      : null,
    surface: surface
      ? {
          id: surface.id,
          rootViewId: surface.rootViewId,
          workspaceId: surface.workspaceRef?.WorkspaceId ?? null,
        }
      : null,
    views: module
      ? viewTree.map((view) =>
          createViewTrace({
            componentSet,
            module,
            resolveViewRenderer,
            routeId: route?.Id ?? null,
            view,
          }),
        )
      : [],
  }
}

function createViewTrace<TModule extends PresentationProjectionTraceModule>({
  componentSet,
  module,
  resolveViewRenderer,
  routeId,
  view,
}: {
  readonly componentSet?: string
  readonly module: TModule
  readonly resolveViewRenderer?: PresentationProjectionTraceViewRendererResolver<TModule>
  readonly routeId: string | null
  readonly view: ViewDefinition
}): PresentationProjectionTraceView {
  const resolution = resolveViewRenderer?.({
    componentSet,
    module,
    routeId,
    view,
  }) ?? null

  return {
    actionCount: getPresentationViewProjectedActions(view).length,
    componentKey: resolution?.componentKey ?? null,
    componentRole: resolution?.componentRole ?? null,
    dataSourceIds: getPresentationViewProjectedDataSourceIds(view),
    fieldIds: getPresentationViewProjectedFieldIds(view),
    id: view.Id,
    kind: String(view.Kind),
    name: view.Name,
    regions: view.Regions.map((region) => ({
      dataSourceIds: region.DataSourceIds,
      id: region.Id,
      viewIds: getRegionViewIds(region),
    })),
    rendererResolved: Boolean(resolution?.rendererResolved),
    resolutionSource: resolution?.resolutionSource ?? null,
    semanticRole: resolution?.semanticRole ?? getPresentationViewSemanticRole(view),
    subjectDataSourceId: view.Subject.DataSourceId ?? null,
  }
}
