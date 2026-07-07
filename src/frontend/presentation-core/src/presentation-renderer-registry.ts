import {
  presentationBindingKinds,
  type PresentationBindingDefinition,
  type ViewDefinition,
} from '@cohesive/presentation-contracts'
import { getPresentationViewSemanticRole } from './presentation-semantics'
import {
  createPresentationEnumDiscriminator,
  resolvePresentationComponentBinding,
  type PresentationEnumDiscriminator,
} from './target-bindings'

/**
 * Registry for composite view renderers, keyed by progressively more semantic
 * matching strategies. Resolution prefers semantic roles and view kinds before
 * using component bindings as an explicit escape hatch.
 */
export interface PresentationCompositeRendererRegistry<TRenderer = unknown> {
  /** Renderers keyed by semantic component roles resolved from target bindings. */
  readonly byComponentRole?: Readonly<Record<string, TRenderer>>

  /** Renderers keyed by concrete component keys resolved from target bindings. */
  readonly byComponentKey?: Readonly<Record<string, TRenderer>>

  /** Renderers keyed by semantic view role. */
  readonly bySemanticRole?: Readonly<Record<string, TRenderer>>

  /** Renderers keyed by generated view kind discriminator values. */
  readonly byViewKind?: Readonly<Record<string, TRenderer>>

  /** Renderer used when no key, role, or kind-specific renderer matches. */
  readonly fallback?: TRenderer
}

/**
 * Complete renderer registry used by presentation projection runtimes. The
 * renderer values are intentionally generic so core can resolve semantic
 * bindings without depending on React, DOM, or any other adapter runtime.
 */
export interface PresentationRendererRegistry<
  TViewRenderer = unknown,
  TControlRenderer = unknown,
  TValueRenderer = unknown,
> {
  /** Composite renderers that adapt whole semantic views. */
  readonly composites?: PresentationCompositeRendererRegistry<TViewRenderer>

  /** Control renderers keyed by presentation control ids or component keys. */
  readonly controls?: Readonly<Record<string, TControlRenderer>>

  /** Value renderers keyed by semantic value or field renderer ids. */
  readonly values?: Readonly<Record<string, TValueRenderer>>
}

/**
 * Minimal module projection needed to resolve component bindings without
 * coupling renderer resolution to a full generated presentation module shape.
 */
export interface PresentationRendererBindingModuleProjection {
  /** Binding targets declared by the presentation module. */
  readonly Targets: readonly {
    /** Bindings declared for this target. */
    readonly Bindings: readonly PresentationBindingDefinition[]

    /** Optional component set that scopes concrete component keys. */
    readonly ComponentSet?: string | null

    /** Target discriminator value, such as the generated React target kind. */
    readonly Target: string | number
  }[]
}

/**
 * Combines renderer registries in declaration order, with later registries
 * overriding earlier renderer keys and fallback renderers.
 */
export function mergePresentationRendererRegistries<
  TViewRenderer = unknown,
  TControlRenderer = unknown,
  TValueRenderer = unknown,
>(
  ...registries: readonly PresentationRendererRegistry<
    TViewRenderer,
    TControlRenderer,
    TValueRenderer
  >[]
): PresentationRendererRegistry<TViewRenderer, TControlRenderer, TValueRenderer> {
  return {
    composites: {
      byComponentRole: mergeRecord(
        registries.map((registry) => registry.composites?.byComponentRole),
      ),
      byComponentKey: mergeRecord(
        registries.map((registry) => registry.composites?.byComponentKey),
      ),
      bySemanticRole: mergeRecord(
        registries.map((registry) => registry.composites?.bySemanticRole),
      ),
      byViewKind: mergeRecord(
        registries.map((registry) => registry.composites?.byViewKind),
      ),
      fallback: registries.reduce<TViewRenderer | undefined>(
        (fallback, registry) => registry.composites?.fallback ?? fallback,
        undefined,
      ),
    },
    controls: mergeRecord(registries.map((registry) => registry.controls)),
    values: mergeRecord(registries.map((registry) => registry.values)),
  }
}

/**
 * Strategy that selected a renderer for a presentation view.
 */
export type PresentationViewRendererResolutionSource =
  | 'component-key'
  | 'component-role'
  | 'fallback'
  | 'semantic-role'
  | 'view-kind'

/**
 * Result of resolving a concrete renderer for a semantic presentation view.
 */
export interface PresentationViewRendererResolution<TRenderer = unknown> {
  /** Component key resolved from target bindings, when one matched. */
  readonly componentKey: string | null

  /** Component role resolved from target bindings, when one matched. */
  readonly componentRole: string | null

  /** Renderer selected from the registry, or null when no renderer matched. */
  readonly renderer: TRenderer | null

  /** Resolution strategy that selected the renderer, or null when unresolved. */
  readonly resolutionSource: PresentationViewRendererResolutionSource | null

  /** Semantic role derived from the view definition and available to diagnostics. */
  readonly semanticRole: string
}

export interface ResolvePresentationViewRendererOptions<
  TRenderer,
  TModule extends PresentationRendererBindingModuleProjection =
    PresentationRendererBindingModuleProjection,
> {
  readonly componentSet?: string | null
  readonly module: TModule
  readonly registry: PresentationRendererRegistry<TRenderer>
  readonly routeId?: string | null
  readonly targetKind?: PresentationEnumDiscriminator | null
  readonly view: ViewDefinition
}

/**
 * Resolves the renderer for a semantic view by consulting semantic role and
 * view kind first. Component bindings remain available as an explicit escape
 * hatch for views that cannot yet be interpreted from the IR shape alone.
 */
export function resolvePresentationViewRenderer<
  TRenderer,
  TModule extends PresentationRendererBindingModuleProjection =
    PresentationRendererBindingModuleProjection,
>({
  componentSet,
  module,
  registry,
  routeId,
  targetKind,
  view,
}: ResolvePresentationViewRendererOptions<
  TRenderer,
  TModule
>): PresentationViewRendererResolution<TRenderer> {
  const componentBinding = resolvePresentationComponentBinding(module, {
    bindingKind: createPresentationEnumDiscriminator(
      presentationBindingKinds,
      'viewComponent',
      'ViewComponent',
    ),
    componentSet,
    id: view.Id,
    routeId,
    targetKind,
  })
  const componentKey = componentBinding.componentKey
  const componentRole = componentBinding.componentRole
  const composites = registry.composites
  const semanticRole = getPresentationViewSemanticRole(view)

  const semanticRoleRenderer = composites?.bySemanticRole?.[semanticRole]
  if (semanticRoleRenderer !== undefined && semanticRoleRenderer !== null) {
    return {
      componentKey,
      componentRole,
      renderer: semanticRoleRenderer,
      resolutionSource: 'semantic-role',
      semanticRole,
    }
  }

  const viewKindRenderer = composites?.byViewKind?.[String(view.Kind)]
  if (viewKindRenderer !== undefined && viewKindRenderer !== null) {
    return {
      componentKey,
      componentRole,
      renderer: viewKindRenderer,
      resolutionSource: 'view-kind',
      semanticRole,
    }
  }

  if (componentRole) {
    const renderer = composites?.byComponentRole?.[componentRole]
    if (renderer !== undefined && renderer !== null) {
      return {
        componentKey,
        componentRole,
        renderer,
        resolutionSource: 'component-role',
        semanticRole,
      }
    }
  }

  if (componentKey) {
    const renderer = composites?.byComponentKey?.[componentKey]
    if (renderer !== undefined && renderer !== null) {
      return {
        componentKey,
        componentRole,
        renderer,
        resolutionSource: 'component-key',
        semanticRole,
      }
    }
  }

  if (composites?.fallback !== undefined && composites.fallback !== null) {
    return {
      componentKey,
      componentRole,
      renderer: composites.fallback,
      resolutionSource: 'fallback',
      semanticRole,
    }
  }

  return {
    componentKey,
    componentRole,
    renderer: null,
    resolutionSource: null,
    semanticRole,
  }
}

function mergeRecord<TValue>(
  records: readonly (Readonly<Record<string, TValue>> | undefined)[],
) {
  const merged: Record<string, TValue> = {}
  for (const record of records) {
    if (record) {
      Object.assign(merged, record)
    }
  }

  return merged
}
