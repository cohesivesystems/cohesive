import {
  presentationBindingKinds,
  type NavigationShellRegionDefinition,
  type PresentationBindingDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  createPresentationEnumDiscriminator,
  resolvePresentationComponentBinding,
  type PresentationEnumDiscriminator,
} from './target-bindings'

/**
 * Registry of renderers for navigation shell regions.
 *
 * Renderers can be registered by concrete component key or by semantic
 * component-system role. Role lookup is preferred when a target binding
 * provides a component role.
 *
 * @typeParam TRenderer - Renderer value stored by the concrete frontend target.
 */
export interface NavigationShellRegionComponentRegistry<TRenderer = unknown> {
  /** Renderers keyed by concrete component key. */
  readonly byComponentKey?: Readonly<Record<string, TRenderer>>

  /** Renderers keyed by semantic component-system role. */
  readonly byComponentRole?: Readonly<Record<string, TRenderer>>
}

/**
 * Minimal presentation module shape required to resolve target bindings for
 * navigation shell regions.
 */
export interface NavigationShellRegionComponentModuleProjection {
  /** Target binding projections declared by the presentation module. */
  readonly Targets?: readonly {
    /** Component bindings declared for this target interpretation. */
    readonly Bindings: readonly PresentationBindingDefinition[]

    /** Optional component set discriminator for target-specific bindings. */
    readonly ComponentSet?: string | null

    /** Target discriminator value such as React, Angular, or another frontend target. */
    readonly Target: string | number
  }[]
}

/**
 * Result of resolving a navigation shell region to a renderer.
 *
 * @typeParam TRenderer - Renderer value stored by the concrete frontend target.
 */
export interface NavigationShellRegionComponentResolution<TRenderer = unknown> {
  /** Concrete component key read from the region or target binding. */
  readonly componentKey: string | null

  /** Semantic component-system role read from the target binding. */
  readonly componentRole: string | null

  /** Resolved renderer, or `null` when the registry has no matching entry. */
  readonly renderer: TRenderer | null

  /** Registry namespace that produced the renderer. */
  readonly resolutionSource: 'component-key' | 'component-role' | null

  /** Source of the target binding used during resolution. */
  readonly targetBindingSource: 'target-region-binding' | null
}

/**
 * Creates a typed navigation shell region component registry.
 *
 * This is an identity helper that preserves renderer type inference at call
 * sites.
 */
export function createNavigationShellRegionComponentRegistry<TRenderer>(
  registry: NavigationShellRegionComponentRegistry<TRenderer>,
) {
  return registry
}

/**
 * Returns every component key and component role present in a registry.
 */
export function getNavigationShellRegionComponentRegistryKeys(
  registry: NavigationShellRegionComponentRegistry | null | undefined,
) {
  return [
    ...Object.keys(registry?.byComponentKey ?? {}),
    ...Object.keys(registry?.byComponentRole ?? {}),
  ]
}

/**
 * Tests whether a concrete component key is registered.
 */
export function hasNavigationShellRegionComponentBinding(
  registry: NavigationShellRegionComponentRegistry | null | undefined,
  componentKey: string | null | undefined,
) {
  return Boolean(componentKey && registry?.byComponentKey?.[componentKey])
}

/**
 * Tests whether a shell region resolves to a renderer through target bindings
 * and the supplied registry.
 */
export function hasNavigationShellRegionComponentTargetBinding<TRenderer>({
  componentSet,
  module,
  region,
  registry,
  targetKind,
}: ResolveNavigationShellRegionComponentOptions<TRenderer>) {
  return Boolean(resolveNavigationShellRegionComponent({
    componentSet,
    module,
    region,
    registry,
    targetKind,
  }).renderer)
}

/**
 * Inputs used to resolve a navigation shell region renderer.
 *
 * @typeParam TRenderer - Renderer value stored by the concrete frontend target.
 */
export interface ResolveNavigationShellRegionComponentOptions<TRenderer> {
  /** Optional component set discriminator for target-specific bindings. */
  readonly componentSet?: string | null

  /** Presentation module projection containing target bindings. */
  readonly module?: NavigationShellRegionComponentModuleProjection | null

  /** Navigation shell region being rendered. */
  readonly region: NavigationShellRegionDefinition

  /** Registry used to resolve component keys or roles to renderers. */
  readonly registry: NavigationShellRegionComponentRegistry<TRenderer> | null | undefined

  /** Optional frontend target discriminator used to filter target bindings. */
  readonly targetKind?: PresentationEnumDiscriminator | null
}

/**
 * Resolves the renderer for a navigation shell region.
 *
 * Resolution uses target bindings first to discover semantic component roles
 * and target-specific component keys. Component roles are preferred over
 * component keys so semantic design-system bindings can override concrete
 * region component keys.
 */
export function resolveNavigationShellRegionComponent<TRenderer>({
  componentSet,
  module,
  region,
  registry,
  targetKind,
}: ResolveNavigationShellRegionComponentOptions<TRenderer>): NavigationShellRegionComponentResolution<TRenderer> {
  const targetBinding = resolveNavigationShellRegionTargetBinding({
    componentSet,
    module,
    region,
    targetKind,
  })
  const componentRole = targetBinding?.componentRole ?? null
  const componentKey = region.ComponentKey ?? targetBinding?.componentKey ?? null

  if (componentRole) {
    const renderer = registry?.byComponentRole?.[componentRole]
    if (renderer !== undefined && renderer !== null) {
      return {
        componentKey,
        componentRole,
        renderer,
        resolutionSource: 'component-role',
        targetBindingSource: targetBinding?.source ?? null,
      }
    }
  }

  if (componentKey) {
    const renderer = registry?.byComponentKey?.[componentKey]
    if (renderer !== undefined && renderer !== null) {
      return {
        componentKey,
        componentRole,
        renderer,
        resolutionSource: 'component-key',
        targetBindingSource: targetBinding?.source ?? null,
      }
    }
  }

  return {
    componentKey,
    componentRole,
    renderer: null,
    resolutionSource: null,
    targetBindingSource: targetBinding?.source ?? null,
  }
}

function resolveNavigationShellRegionTargetBinding({
  componentSet,
  module,
  region,
  targetKind,
}: {
  readonly componentSet?: string | null
  readonly module?: NavigationShellRegionComponentModuleProjection | null
  readonly region: NavigationShellRegionDefinition
  readonly targetKind?: PresentationEnumDiscriminator | null
}) {
  if (!module?.Targets) {
    return null
  }

  const resolvedBinding = resolvePresentationComponentBinding(
    { Targets: module.Targets },
    {
      bindingKind: createPresentationEnumDiscriminator(
        presentationBindingKinds,
        'navigationShellRegionComponent',
        'NavigationShellRegionComponent',
      ),
      componentSet,
      id: region.Id,
      targetKind,
    },
  )

  if (!resolvedBinding.binding) {
    return null
  }

  return {
    componentKey: resolvedBinding.componentKey,
    componentRole: resolvedBinding.componentRole,
    source: 'target-region-binding' as const,
  }
}
