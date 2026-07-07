import type { ComponentType } from 'react'

import {
  presentationTargetKinds,
  type NavigationRouteDefinition,
  type PageHostDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  createPresentationEnumDiscriminator,
  presentationPageHostComponentRoles,
  resolveDefaultPresentationPageHostComponentRole,
  resolvePageHostRenderer as resolveCorePageHostRenderer,
  type NavigationDefinitionProjection,
  type PageHostRendererRegistry as CorePageHostRendererRegistry,
  type PageHostRendererResolution as CorePageHostRendererResolution,
  type PageHostRendererResolutionSource,
  type PageHostTargetBindingSource,
  type PresentationPageHostRendererModuleProjection,
} from '@cohesivesystems/presentation-core'
import type {
  PageHostComponentRenderer,
  PageHostRenderContext,
} from './page-host-projection'
import type {
  ProjectedNavigationTargetRenderContext,
  ProjectedNavigationTargetRenderer,
} from './react-router-binding'

export {
  presentationPageHostComponentRoles,
  resolveDefaultPresentationPageHostComponentRole,
}

export type {
  PageHostRendererResolutionSource,
  PageHostTargetBindingSource,
  PresentationPageHostRendererModuleProjection,
}

/**
 * Renders a semantic page host after navigation has resolved the target route.
 * These renderers sit at the projection boundary: they choose React components
 * for page-host semantics without letting the router own application meaning.
 */
export type PageHostRenderer<
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> = PageHostComponentRenderer<
  TModule,
  TNavigation,
  TRoute,
  TPageHost,
  TProjectionContext
>

/**
 * React component type that consumes the fully resolved page-host context.
 * Use `createPageHostComponentRenderer` to adapt one of these into a registry
 * entry.
 */
export type PageHostRendererComponent<
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> = ComponentType<
  PageHostRenderContext<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >
>

/**
 * Adapts a React component that consumes the resolved page-host context into a
 * registry renderer. This keeps the registry declarative while allowing route
 * hosts to remain ordinary components at the projection boundary.
 */
export function createPageHostComponentRenderer<
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
>(
  Component: PageHostRendererComponent<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >,
): PageHostRenderer<
  TModule,
  TNavigation,
  TRoute,
  TPageHost,
  TProjectionContext
> {
  return function renderPageHostWithComponent(context) {
    return <Component {...context} />
  }
}

export type PageHostRendererRegistry<
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> = CorePageHostRendererRegistry<
  PageHostRenderer<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >
>

/**
 * Convenience registry type for apps that use the standard generated
 * navigation, route, and page-host shapes.
 */
export type SimplePageHostRendererRegistry<
  TModule,
  TProjectionContext,
> = PageHostRendererRegistry<
  TModule,
  NavigationDefinitionProjection,
  NavigationRouteDefinition,
  PageHostDefinition,
  TProjectionContext
>

export type PageHostRendererResolution<
  TModule,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> = CorePageHostRendererResolution<
  PageHostRenderer<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >
>

/**
 * Options for adapting a page-host registry into a navigation-target renderer
 * that can be passed to router projection.
 */
export interface CreatePageHostTargetRendererOptions<
  TComponentKey extends string,
  TModule extends PresentationPageHostRendererModuleProjection,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> {
  /** Registry used to resolve semantic page hosts to concrete renderers. */
  readonly registry: PageHostRendererRegistry<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >

  /** Adapter component set to use when reading target bindings. */
  readonly componentSet?: string | null

  /**
   * Optional caller-supplied component-key resolver. When provided, this value
   * takes precedence over PageHostComponent target bindings.
   */
  readonly resolveComponentKey?: (
    context: ProjectedNavigationTargetRenderContext<
      TComponentKey,
      TModule,
      TNavigation,
      TRoute,
      TPageHost,
      TProjectionContext
    >,
  ) => string | null

  /**
   * Optional caller-supplied component-role resolver. When provided, this value
   * takes precedence over PageHostComponent target bindings.
   */
  readonly resolveComponentRole?: (
    context: ProjectedNavigationTargetRenderContext<
      TComponentKey,
      TModule,
      TNavigation,
      TRoute,
      TPageHost,
      TProjectionContext
    >,
  ) => string | null
}

export interface ResolvePageHostRendererOptions<
  TModule extends PresentationPageHostRendererModuleProjection,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
> {
  readonly componentKey?: string | null
  readonly componentRole?: string | null
  readonly componentSet?: string | null
  readonly module: TModule
  readonly pageHost: TPageHost | null
  readonly registry: PageHostRendererRegistry<
    TModule,
    TNavigation,
    TRoute,
    TPageHost,
    TProjectionContext
  >
  readonly route?: TRoute | null
}

/**
 * Creates a navigation-target renderer from a page-host registry. Resolution
 * starts from the semantic page-host shape, resolves an adapter component role,
 * then invokes the frontend interpreter registered for that role.
 */
export function createPageHostTargetRenderer<
  TComponentKey extends string,
  TModule extends PresentationPageHostRendererModuleProjection,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
>({
  componentSet,
  registry,
  resolveComponentKey,
  resolveComponentRole,
}: CreatePageHostTargetRendererOptions<
  TComponentKey,
  TModule,
  TNavigation,
  TRoute,
  TPageHost,
  TProjectionContext
>): ProjectedNavigationTargetRenderer<
  TComponentKey,
  TModule,
  TNavigation,
  TRoute,
  TPageHost,
  TProjectionContext
> {
  return function renderPageHostTarget(context) {
    const componentKey = resolveComponentKey?.(context) ?? null
    const componentRole = resolveComponentRole?.(context) ?? null
    const resolution = resolvePageHostRenderer({
      componentKey,
      componentRole,
      componentSet,
      module: context.module,
      pageHost: context.pageHost,
      registry,
      route: context.route,
    })

    if (context.pageHost && resolution.renderer) {
      return resolution.renderer({
        context: context.context,
        module: context.module,
        navigation: context.navigation,
        navigationTarget: context.navigationTarget,
        pageHost: context.pageHost,
        parameters: context.parameters,
        route: context.route,
      })
    }

    return context.renderBoundPageHost(componentKey)
  }
}

/**
 * Resolves a page-host renderer without rendering it.
 */
export function resolvePageHostRenderer<
  TModule extends PresentationPageHostRendererModuleProjection,
  TNavigation extends NavigationDefinitionProjection<TRoute, TPageHost>,
  TRoute extends NavigationRouteDefinition,
  TPageHost extends PageHostDefinition,
  TProjectionContext,
>({
  componentKey,
  componentRole,
  componentSet,
  module,
  pageHost,
  registry,
  route,
}: ResolvePageHostRendererOptions<
  TModule,
  TNavigation,
  TRoute,
  TPageHost,
  TProjectionContext
>): PageHostRendererResolution<
  TModule,
  TNavigation,
  TRoute,
  TPageHost,
  TProjectionContext
> {
  return resolveCorePageHostRenderer<
    PageHostRenderer<
      TModule,
      TNavigation,
      TRoute,
      TPageHost,
      TProjectionContext
    >,
    TModule,
    TRoute,
    TPageHost
  >({
    componentKey,
    componentRole,
    componentSet,
    module,
    pageHost,
    registry,
    route,
    targetKind: reactPresentationTargetKind,
  })
}

const reactPresentationTargetKind = createPresentationEnumDiscriminator(
  presentationTargetKinds,
  'react',
  'React',
)
