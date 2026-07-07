import type {
  PresentationBindingDefinition,
  ViewDefinition,
} from '@cohesive/presentation-core'
import type {
  NavigationDefinitionProjection,
} from '@cohesive/presentation-core'
import {
  createPresentationProjectionTrace as createCorePresentationProjectionTrace,
} from '@cohesive/presentation-core'
import {
  resolvePageHostRenderer,
  type PresentationPageHostRendererModuleProjection,
  type SimplePageHostRendererRegistry,
} from './page-host-renderer-registry'
import {
  resolvePresentationViewRenderer,
  type PresentationRendererRegistry,
} from './presentation-renderer-registry'

export type {
  PresentationProjectionTrace,
  PresentationProjectionTracePageHost,
  PresentationProjectionTracePageHostRenderer,
  PresentationProjectionTraceRegion,
  PresentationProjectionTraceRoute,
  PresentationProjectionTraceSurface,
  PresentationProjectionTraceView,
} from '@cohesive/presentation-core'

export interface PresentationProjectionTraceModule
  extends PresentationPageHostRendererModuleProjection {
  readonly Targets: readonly {
    readonly Bindings: readonly PresentationBindingDefinition[]
    readonly ComponentSet?: string | null
    readonly Target: string | number
  }[]
  readonly Views: readonly ViewDefinition[]
}

export interface CreatePresentationProjectionTraceOptions<
  TModule extends PresentationProjectionTraceModule,
  TContext,
> {
  readonly componentSet?: string
  readonly module: TModule | null
  readonly navigation: NavigationDefinitionProjection | null
  readonly pageHostRendererRegistry?: SimplePageHostRendererRegistry<
    TModule,
    TContext
  >
  readonly pathname: string
  readonly rendererRegistry?: PresentationRendererRegistry<TContext>
}

/**
 * Builds a read-only projection trace while adapting React renderer
 * registries into framework-neutral trace resolver results.
 */
export function createPresentationProjectionTrace<
  TModule extends PresentationProjectionTraceModule,
  TContext = unknown,
>({
  componentSet,
  module,
  navigation,
  pageHostRendererRegistry,
  pathname,
  rendererRegistry,
}: CreatePresentationProjectionTraceOptions<TModule, TContext>) {
  return createCorePresentationProjectionTrace({
    componentSet,
    module,
    navigation,
    pathname,
    resolvePageHostRenderer: pageHostRendererRegistry
      ? ({ module, pageHost, route }) => {
          const resolution = resolvePageHostRenderer({
            componentKey: null,
            componentSet,
            module,
            pageHost,
            registry: pageHostRendererRegistry,
            route,
          })

          return {
            componentKey: resolution.componentKey,
            componentRole: resolution.componentRole,
            rendererKey: resolution.rendererKey,
            resolutionSource: resolution.resolutionSource,
            semanticRole: resolution.semanticRole,
            targetBindingSource: resolution.targetBindingSource,
          }
        }
      : undefined,
    resolveViewRenderer: rendererRegistry
      ? ({ module, routeId, view }) => {
          const resolution = resolvePresentationViewRenderer({
            componentSet,
            module,
            registry: rendererRegistry,
            routeId,
            view,
          })

          return {
            componentKey: resolution.componentKey,
            componentRole: resolution.componentRole,
            rendererResolved: Boolean(resolution.renderer),
            resolutionSource: resolution.resolutionSource,
            semanticRole: resolution.semanticRole,
          }
        }
      : undefined,
  })
}
