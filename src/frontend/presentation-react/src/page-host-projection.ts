import type { ReactNode } from 'react'

import type {
  NavigationDefinitionProjection,
  NavigationTarget,
} from '@cohesivesystems/presentation-core'
import type {
  NavigationRouteDefinition,
  PageHostDefinition,
} from '@cohesivesystems/presentation-contracts'

/**
 * Route parameter values resolved by the frontend router for a projected
 * navigation route. Values are strings because this sits at the router binding
 * boundary; semantic conversion belongs in the page-host or data-source layer.
 */
export type RouteParameterValues = Readonly<Record<string, string | undefined>>

/**
 * Complete rendering context for a resolved page host. Page-host renderers are
 * the adapting boundary between semantic navigation/page-host IR and concrete
 * React route components.
 *
 * @typeParam TModule - Presentation module shape available to the renderer.
 * @typeParam TNavigation - Navigation graph shape used to resolve the route.
 * @typeParam TRoute - Concrete navigation route type.
 * @typeParam TPageHost - Concrete page-host type mounted by the route.
 * @typeParam TProjectionContext - App-specific runtime context supplied by the navigation projection.
 */
export interface PageHostRenderContext<
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> {
  /** App-specific projection context carried through navigation rendering. */
  readonly context: TProjectionContext

  /** Presentation module that owns views, targets, workspaces, and bindings. */
  readonly module: TModule

  /** Navigation graph that produced the current route/page-host pairing. */
  readonly navigation: TNavigation

  /** Resolved semantic target for the active route. */
  readonly navigationTarget: NavigationTarget<TRoute, TPageHost>

  /** Page host mounted by the active route. */
  readonly pageHost: TPageHost

  /** Raw route parameters captured by the frontend router. */
  readonly parameters: RouteParameterValues

  /** Active route definition from the navigation graph. */
  readonly route: TRoute
}

/**
 * React renderer for a resolved page host.
 */
export type PageHostComponentRenderer<
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> = (
  context: PageHostRenderContext<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >,
) => ReactNode

/**
 * Registry of concrete page-host renderers keyed by adapter component key.
 * The semantic navigation runtime resolves which component key applies; this
 * registry supplies the React implementation for that key.
 */
export type PageHostComponentRegistry<
  TComponentKey extends string,
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> = Readonly<
  Partial<
    Record<
      TComponentKey,
      PageHostComponentRenderer<
        TModule,
        TNavigation,
        TRoute,
        TPageHost,
        TProjectionContext
      >
    >
  >
>

/**
 * Convenience registry type for apps that use the standard generated
 * navigation, route, and page-host shapes.
 */
export type SimplePageHostComponentRegistry<
  TComponentKey extends string,
  TModule,
  TProjectionContext,
> = PageHostComponentRegistry<
  TComponentKey,
  TModule,
  NavigationDefinitionProjection,
  NavigationRouteDefinition,
  PageHostDefinition,
  TProjectionContext
>

/**
 * Convenience render-context type for apps that use the standard generated
 * navigation, route, and page-host shapes.
 */
export type SimplePageHostRenderContext<
  TModule,
  TProjectionContext,
> = PageHostRenderContext<
  TModule,
  NavigationDefinitionProjection,
  NavigationRouteDefinition,
  PageHostDefinition,
  TProjectionContext
>

/**
 * Convenience unknown-renderer type for apps that use the standard generated
 * navigation, route, and page-host shapes.
 */
export type SimpleUnknownPageHostRenderer<
  TModule,
  TProjectionContext,
> = UnknownPageHostRenderer<
  TModule,
  NavigationDefinitionProjection,
  NavigationRouteDefinition,
  PageHostDefinition,
  TProjectionContext
>

/**
 * Context passed when a route cannot be projected to a concrete page-host
 * renderer. Unknown renderers should produce diagnostics, not throw, so route
 * and binding problems remain visible inside the app shell.
 */
export interface UnknownPageHostRenderContext<
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> {
  /** Component key that failed to resolve, when one was present. */
  readonly componentKey?: string | null

  /** App-specific projection context carried through navigation rendering. */
  readonly context: TProjectionContext

  /** Presentation module available during route projection. */
  readonly module: TModule

  /** Navigation graph being projected. */
  readonly navigation: TNavigation

  /** Resolved navigation target, or null when no route matched. */
  readonly navigationTarget: NavigationTarget<TRoute, TPageHost> | null

  /** Page host selected by the route, or null when the route references none. */
  readonly pageHost: TPageHost | null

  /** Raw route parameters captured by the frontend router. */
  readonly parameters: RouteParameterValues

  /** Failure mode that prevented a page host from rendering. */
  readonly reason:
    | 'missing-page-host'
    | 'missing-component-binding'
    | 'unknown-component-key'
    | 'unmatched-route'

  /** Active route, or null when no route matched the current URL. */
  readonly route: TRoute | null
}

/**
 * Renderer for page-host projection failures.
 */
export type UnknownPageHostRenderer<
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> = (
  context: UnknownPageHostRenderContext<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >,
) => ReactNode

/**
 * Inputs required to render a page host after router matching and semantic
 * navigation resolution have already occurred.
 */
export interface RenderPageHostOptions<
  TComponentKey extends string,
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> {
  /** Concrete component key resolved from target bindings or caller policy. */
  readonly componentKey?: string | null

  /** Registry that maps component keys to React page-host renderers. */
  readonly componentRegistry: PageHostComponentRegistry<
    TComponentKey,
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >

  /** App-specific projection context carried through navigation rendering. */
  readonly context: TProjectionContext

  /** Presentation module available during route projection. */
  readonly module: TModule

  /** Navigation graph being projected. */
  readonly navigation: TNavigation

  /** Resolved semantic target for the active route. */
  readonly navigationTarget: NavigationTarget<TRoute, TPageHost>

  /** Page host selected by the route. Null is rendered through diagnostics. */
  readonly pageHost: TPageHost | null

  /** Raw route parameters captured by the frontend router. */
  readonly parameters: RouteParameterValues

  /** Diagnostic renderer used when the page host cannot be projected. */
  readonly renderUnknownPageHost: UnknownPageHostRenderer<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >

  /** Active route definition from the navigation graph. */
  readonly route: TRoute
}

/**
 * Projects a resolved semantic page host into a concrete React renderer.
 *
 * The function does only three things: validate that a page host exists,
 * validate that a component key exists, and look up the corresponding renderer.
 * Higher-level code owns route matching and component-key resolution; unknown
 * cases are delegated to `renderUnknownPageHost` so projection failures remain
 * visible and recoverable.
 */
export function renderPageHost<
  TComponentKey extends string,
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
>({
  componentKey,
  componentRegistry,
  context,
  module,
  navigation,
  navigationTarget,
  pageHost,
  parameters,
  renderUnknownPageHost,
  route,
}: RenderPageHostOptions<
  TComponentKey,
  TModule,
  TNavigation,
  TRoute,
  TPageHost,
  TProjectionContext
>) {
  if (!pageHost) {
    return renderUnknownPageHost({
      context,
      module,
      navigation,
      navigationTarget,
      pageHost,
      parameters,
      reason: 'missing-page-host',
      route,
    })
  }

  if (!componentKey) {
    return renderUnknownPageHost({
      context,
      module,
      navigation,
      navigationTarget,
      pageHost,
      parameters,
      reason: 'missing-component-binding',
      route,
    })
  }

  const renderer = componentRegistry[componentKey as TComponentKey]
  if (!renderer) {
    return renderUnknownPageHost({
      componentKey,
      context,
      module,
      navigation,
      navigationTarget,
      pageHost,
      parameters,
      reason: 'unknown-component-key',
      route,
    })
  }

  return renderer({
    context,
    module,
    navigation,
    navigationTarget,
    pageHost,
    parameters,
    route,
  })
}
