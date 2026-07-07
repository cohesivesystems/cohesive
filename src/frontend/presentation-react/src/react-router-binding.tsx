import type { ReactNode } from 'react'
import { Route, Routes, useLocation, useParams } from 'react-router'

import {
  createNavigationRouteInstanceKey,
  resolveNavigationTarget,
  type NavigationDefinitionProjection,
  type NavigationTarget,
} from '@cohesivesystems/presentation-core'
import type {
  NavigationRouteDefinition,
  PageHostDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  renderPageHost,
  type PageHostComponentRegistry,
  type RouteParameterValues,
  type UnknownPageHostRenderer,
} from './page-host-projection'
import { toReactRouterPath } from './react-router-path'

/**
 * Props for rendering one semantic navigation route through React Router.
 *
 * The route definition remains presentation IR; this component only adapts it
 * to React Router matching, resolves the target page host, and then delegates
 * rendering to either an app-specific navigation target renderer or the
 * page-host projection runtime.
 */
export interface ProjectedNavigationRouteProps<
  TComponentKey extends string,
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> {
  /** Optional concrete component registry used when rendering a bound page host directly. */
  readonly componentRegistry?: PageHostComponentRegistry<
    TComponentKey,
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >
  /** App-defined runtime context carried through route and page-host renderers. */
  readonly context: TProjectionContext
  /** Presentation module or module projection that owns the route target bindings. */
  readonly module: TModule
  /** Navigation graph containing the route and its page-host definitions. */
  readonly navigation: TNavigation
  /** Fallback renderer for unmatched routes, missing page hosts, or unknown bindings. */
  readonly renderUnknownPageHost: UnknownPageHostRenderer<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >
  /**
   * Optional higher-level renderer for the resolved navigation target.
   *
   * Use this when an app needs to attach route-level services, auth, data-source
   * bindings, or layout before rendering the page host.
   */
  readonly renderNavigationTarget?: ProjectedNavigationTargetRenderer<
    TComponentKey,
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >
  /**
   * Optional component-key resolver used by direct page-host rendering.
   *
   * Most projected apps prefer target bindings or a `renderNavigationTarget`
   * implementation; this hook remains useful for simple router integrations.
   */
  readonly resolveComponentKey?: (
    context: ProjectedNavigationRouteResolutionContext<
      TModule,
      TNavigation,
      TRoute,
      TPageHost,
      TProjectionContext
    >,
  ) => string | null
  /** The semantic route definition being adapted to a React Router route. */
  readonly route: TRoute
}

/**
 * Context available while resolving a route-level component key.
 *
 * This is deliberately pre-render context: route parameters are not included
 * here because component-key resolution should depend on the semantic route,
 * page host, and module bindings rather than current URL values.
 */
export interface ProjectedNavigationRouteResolutionContext<
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> {
  /** App-defined runtime context passed into the navigation projection. */
  readonly context: TProjectionContext
  /** Presentation module or module projection used by the resolver. */
  readonly module: TModule
  /** Navigation graph that contains the current route and page host. */
  readonly navigation: TNavigation
  /** Fully resolved semantic navigation target for the matched route. */
  readonly navigationTarget: NavigationTarget<TRoute, TPageHost>
  /** Page host addressed by the route, or `null` when the graph is incomplete. */
  readonly pageHost: TPageHost | null
  /** Matched semantic route definition. */
  readonly route: TRoute
}

/**
 * Convenience route-resolution context for integrations that use the generated
 * default navigation and page-host shapes.
 */
export type SimpleProjectedNavigationRouteResolutionContext<
  TModule,
  TProjectionContext,
> = ProjectedNavigationRouteResolutionContext<
  TModule,
  NavigationDefinitionProjection,
  NavigationRouteDefinition,
  PageHostDefinition,
  TProjectionContext
>

/**
 * Render context for apps that take over the route target after React Router
 * has matched a URL.
 *
 * This is the main extension point for product shells: callers receive route
 * params, semantic route/page-host data, and a helper for falling back to the
 * standard page-host renderer.
 */
export interface ProjectedNavigationTargetRenderContext<
  TComponentKey extends string,
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> extends ProjectedNavigationRouteResolutionContext<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  > {
  /** Route parameter values returned by React Router for the matched URL. */
  readonly parameters: RouteParameterValues
  /**
   * Renders the resolved page host through the standard page-host projection.
   *
   * Pass a component key to force a binding for simple adapters; omit it to let
   * the page-host projection resolve from the registry and target metadata.
   */
  readonly renderBoundPageHost: (
    componentKey?: TComponentKey | string | null,
  ) => ReactNode
}

/**
 * Convenience target-render context for integrations that use the generated
 * default navigation and page-host shapes.
 */
export type SimpleProjectedNavigationTargetRenderContext<
  TModule,
  TProjectionContext,
> = ProjectedNavigationTargetRenderContext<
  string,
  TModule,
  NavigationDefinitionProjection,
  NavigationRouteDefinition,
  PageHostDefinition,
  TProjectionContext
>

/**
 * App-level route target renderer.
 *
 * Implement this when route dispatch needs to be explicit and inspectable while
 * the actual page body remains projected from presentation IR.
 */
export type ProjectedNavigationTargetRenderer<
  TComponentKey extends string,
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> = (
  context: ProjectedNavigationTargetRenderContext<
    TComponentKey,
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >,
) => ReactNode

/**
 * React Router element for one generated presentation route.
 *
 * It resolves the semantic navigation target, captures URL parameters, and then
 * either invokes `renderNavigationTarget` or renders the target page host
 * directly through `renderPageHost`.
 */
export function ProjectedNavigationRoute<
  TComponentKey extends string,
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
>({
  componentRegistry,
  context,
  module,
  navigation,
  renderUnknownPageHost,
  renderNavigationTarget,
  resolveComponentKey,
  route,
}: ProjectedNavigationRouteProps<
  TComponentKey,
  TModule,
  TNavigation,
  TRoute,
  TPageHost,
  TProjectionContext
>) {
  const parameters = useParams()
  const navigationTarget = resolveNavigationTarget<TNavigation, TRoute, TPageHost>(navigation, route)
  const pageHost = navigationTarget.pageHost
  const resolvedComponentRegistry = (componentRegistry ?? {}) as PageHostComponentRegistry<
    TComponentKey,
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >
  const renderBoundPageHost = (componentKey?: TComponentKey | string | null) =>
    renderPageHost({
      componentKey,
      componentRegistry: resolvedComponentRegistry,
      context,
      module,
      navigation,
      navigationTarget,
      pageHost,
      parameters,
      renderUnknownPageHost,
      route,
    })

  if (renderNavigationTarget) {
    return renderNavigationTarget({
      context,
      module,
      navigation,
      navigationTarget,
      pageHost,
      parameters,
      renderBoundPageHost,
      route,
    })
  }

  const componentKey = resolveComponentKey?.({
    context,
    module,
    navigation,
    navigationTarget,
    pageHost,
    route,
  }) ?? null

  return renderBoundPageHost(componentKey)
}

/**
 * Props for projecting an entire navigation graph into React Router routes.
 */
export interface ProjectedNavigationRoutesProps<
  TComponentKey extends string,
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> {
  /** Optional registry used when route elements render page hosts directly. */
  readonly componentRegistry?: PageHostComponentRegistry<
    TComponentKey,
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >
  /** App-defined runtime context passed to every generated route element. */
  readonly context: TProjectionContext
  /** Presentation module or module projection used by generated routes. */
  readonly module: TModule
  /** Semantic navigation graph whose routes are projected into React Router. */
  readonly navigation: TNavigation
  /** Fallback renderer used for missing bindings and the catch-all route. */
  readonly renderUnknownPageHost: UnknownPageHostRenderer<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >
  /** Optional route-level renderer invoked after a URL has matched. */
  readonly renderNavigationTarget?: ProjectedNavigationTargetRenderer<
    TComponentKey,
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >
  /** Optional direct page-host component-key resolver. */
  readonly resolveComponentKey?: ProjectedNavigationRouteProps<
    TComponentKey,
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >['resolveComponentKey']
}

/**
 * Projects a semantic navigation graph into a React Router `<Routes>` tree.
 *
 * Each `NavigationRouteDefinition.PathTemplate` is translated to a React Router
 * path, while unmatched URLs are routed to the supplied unknown page-host
 * renderer.
 */
export function ProjectedNavigationRoutes<
  TComponentKey extends string,
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
>({
  componentRegistry,
  context,
  module,
  navigation,
  renderUnknownPageHost,
  renderNavigationTarget,
  resolveComponentKey,
}: ProjectedNavigationRoutesProps<
  TComponentKey,
  TModule,
  TNavigation,
  TRoute,
  TPageHost,
  TProjectionContext
>) {
  const location = useLocation()
  const routeInstanceKey = createNavigationRouteInstanceKey(navigation, location)

  return (
    // A semantic route instance owns route-scoped bindings and workspace state.
    // The navigation history policy declares which URL parts identify that instance.
    <Routes key={routeInstanceKey}>
      {navigation.Routes.map((route) => (
        <Route
          element={
            <ProjectedNavigationRoute
              componentRegistry={componentRegistry}
              context={context}
              module={module}
              navigation={navigation}
              renderUnknownPageHost={renderUnknownPageHost}
              renderNavigationTarget={renderNavigationTarget}
              resolveComponentKey={resolveComponentKey}
              route={route}
            />
          }
          key={route.Id}
          path={toReactRouterPath(route.PathTemplate)}
        />
      ))}
      <Route
        element={renderUnknownPageHost({
          context,
          module,
          navigation,
          navigationTarget: null,
          pageHost: null,
          parameters: {},
          reason: 'unmatched-route',
          route: null,
        })}
        path="*"
      />
    </Routes>
  )
}
