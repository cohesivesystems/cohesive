import type { ReactNode } from 'react'

import {
  createPresentationEnumDiscriminator,
  mergePresentationRendererRegistries as mergeCorePresentationRendererRegistries,
  resolvePresentationViewRenderer as resolveCorePresentationViewRenderer,
  type FieldPresentationDefinition,
  type PresentationDataSourceResolver,
  type PresentationModuleDefinition,
  type PresentationSurface,
  type PresentationCompositeRendererRegistry as CorePresentationCompositeRendererRegistry,
  type PresentationRendererBindingModuleProjection,
  type PresentationRendererRegistry as CorePresentationRendererRegistry,
  type PresentationViewRendererResolution as CorePresentationViewRendererResolution,
  type PresentationViewRendererResolutionSource,
  type ViewDefinition,
} from '@cohesivesystems/presentation-core'
import { presentationTargetKinds } from '@cohesivesystems/presentation-contracts'

/**
 * Options that let a view renderer project only selected regions of a view.
 */
export interface PresentationViewRegionRenderOptions {
  /** Region ids to render. Omit this to render every region declared by the view. */
  readonly includeRegionIds?: readonly string[]
}

/**
 * Runtime context supplied to a concrete renderer for a semantic presentation view.
 *
 * @typeParam TContext - App-specific projection context carried through rendering.
 */
export interface PresentationViewRenderContext<TContext> {
  /** Component key resolved from presentation target bindings, when matched. */
  readonly componentKey: string | null

  /** Component role resolved from presentation target bindings, when matched. */
  readonly componentRole: string | null

  /** App-specific runtime context supplied by the projection host. */
  readonly context: TContext

  /** Resolver for semantic data sources referenced by the view or nested renderers. */
  readonly dataSourceResolver: PresentationDataSourceResolver

  /** Presentation module that owns the projected view graph and bindings. */
  readonly module: PresentationModuleDefinition

  /** Renders this view's regions, optionally restricted to specific region ids. */
  readonly renderRegions: (
    view: ViewDefinition,
    options?: PresentationViewRegionRenderOptions,
  ) => ReactNode

  /** Renders another view from the same presentation module by semantic view id. */
  readonly renderView: (viewId: string) => ReactNode

  /** Surface on which the current view is being projected. */
  readonly surface: PresentationSurface

  /** Semantic view definition being adapted to React. */
  readonly view: ViewDefinition
}

/**
 * React renderer for a semantic presentation view.
 */
export type PresentationViewRenderer<TContext> = (
  context: PresentationViewRenderContext<TContext>,
) => ReactNode

/**
 * Runtime context supplied when rendering a scalar or structured presentation value.
 *
 * @typeParam TContext - App-specific projection context carried through rendering.
 */
export interface PresentationValueRenderContext<TContext> {
  /** App-specific runtime context supplied by the projection host. */
  readonly context: TContext

  /** Resolver for data sources available to value renderers. */
  readonly dataSourceResolver: PresentationDataSourceResolver

  /** Field metadata that describes the value, when the value came from a field. */
  readonly field?: FieldPresentationDefinition | null

  /** Presentation module that owns the value's field and binding definitions. */
  readonly module: PresentationModuleDefinition

  /** Raw value selected from the semantic data source. */
  readonly value: unknown
}

/**
 * React renderer for an individual presentation value.
 */
export type PresentationValueRenderer<TContext> = (
  context: PresentationValueRenderContext<TContext>,
) => ReactNode

/**
 * Runtime context supplied to presentation controls that are not tied to a single view.
 *
 * @typeParam TContext - App-specific projection context carried through rendering.
 */
export interface PresentationControlRenderContext<TContext> {
  /** App-specific runtime context supplied by the projection host. */
  readonly context: TContext

  /** Resolver for data sources available to control renderers. */
  readonly dataSourceResolver: PresentationDataSourceResolver

  /** Presentation module that owns the control binding definitions. */
  readonly module: PresentationModuleDefinition
}

/**
 * React renderer for an auxiliary presentation control.
 */
export type PresentationControlRenderer<TContext> = (
  context: PresentationControlRenderContext<TContext>,
) => ReactNode

export type PresentationCompositeRendererRegistry<TContext> =
  CorePresentationCompositeRendererRegistry<PresentationViewRenderer<TContext>>

/**
 * Complete renderer registry used by the React presentation projection runtime.
 */
export interface PresentationRendererRegistry<TContext>
  extends CorePresentationRendererRegistry<
    PresentationViewRenderer<TContext>,
    PresentationControlRenderer<TContext>,
    PresentationValueRenderer<TContext>
  > {
  /** Composite renderers that adapt whole semantic views. */
  readonly composites?: PresentationCompositeRendererRegistry<TContext>

  /** Control renderers keyed by presentation control ids or component keys. */
  readonly controls?: Readonly<Record<string, PresentationControlRenderer<TContext>>>

  /** Value renderers keyed by semantic value or field renderer ids. */
  readonly values?: Readonly<Record<string, PresentationValueRenderer<TContext>>>
}

export type PresentationViewRendererResolution<TContext> =
  CorePresentationViewRendererResolution<PresentationViewRenderer<TContext>>

export type { PresentationRendererBindingModuleProjection }

export type { PresentationViewRendererResolutionSource }

export interface ResolvePresentationViewRendererOptions<
  TContext,
  TModule extends PresentationRendererBindingModuleProjection =
    PresentationRendererBindingModuleProjection,
> {
  readonly componentSet?: string | null
  readonly module: TModule
  readonly registry: PresentationRendererRegistry<TContext>
  readonly routeId?: string | null
  readonly view: ViewDefinition
}

/**
 * Combines renderer registries in declaration order, with later registries
 * overriding earlier renderer keys and fallback renderers.
 */
export function mergePresentationRendererRegistries<TContext>(
  ...registries: readonly PresentationRendererRegistry<TContext>[]
): PresentationRendererRegistry<TContext> {
  return mergeCorePresentationRendererRegistries<
    PresentationViewRenderer<TContext>,
    PresentationControlRenderer<TContext>,
    PresentationValueRenderer<TContext>
  >(...registries) as PresentationRendererRegistry<TContext>
}

/**
 * Resolves the React renderer for a semantic view while keeping binding
 * resolution in the framework-neutral core package.
 */
export function resolvePresentationViewRenderer<
  TContext,
  TModule extends PresentationRendererBindingModuleProjection =
    PresentationRendererBindingModuleProjection,
>({
  componentSet,
  module,
  registry,
  routeId,
  view,
}: ResolvePresentationViewRendererOptions<
  TContext,
  TModule
>): PresentationViewRendererResolution<TContext> {
  return resolveCorePresentationViewRenderer<
    PresentationViewRenderer<TContext>,
    TModule
  >({
    componentSet,
    module,
    registry,
    routeId,
    targetKind: reactPresentationTargetKind,
    view,
  })
}

const reactPresentationTargetKind = createPresentationEnumDiscriminator(
  presentationTargetKinds,
  'react',
  'React',
)
