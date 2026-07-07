import type { ComponentType, ReactNode } from 'react'

import {
  presentationTargetKinds,
  type NavigationShellRegionDefinition,
} from '@cohesive/presentation-contracts'
import {
  createPresentationEnumDiscriminator,
  createNavigationShellRegionComponentRegistry as createCoreNavigationShellRegionComponentRegistry,
  getNavigationShellRegionComponentRegistryKeys,
  hasNavigationShellRegionComponentBinding,
  hasNavigationShellRegionComponentTargetBinding as hasCoreNavigationShellRegionComponentTargetBinding,
  resolveNavigationShellRegionComponent as resolveCoreNavigationShellRegionComponent,
  type NavigationShellRegionComponentModuleProjection,
  type NavigationShellRegionComponentRegistry as CoreNavigationShellRegionComponentRegistry,
  type NavigationShellRegionComponentResolution as CoreNavigationShellRegionComponentResolution,
  type ResolveNavigationShellRegionComponentOptions as CoreResolveNavigationShellRegionComponentOptions,
} from '@cohesive/presentation-core'

export {
  getNavigationShellRegionComponentRegistryKeys,
  hasNavigationShellRegionComponentBinding,
}

export type { NavigationShellRegionComponentModuleProjection }

/**
 * Props passed to a React component that renders a navigation shell region.
 */
export interface NavigationShellRegionComponentProps {
  /** Navigation shell region definition selected by the shell runtime. */
  readonly region: NavigationShellRegionDefinition
}

/**
 * React component type used to render one navigation shell region.
 */
export type NavigationShellRegionComponentRenderer =
  ComponentType<NavigationShellRegionComponentProps>

/**
 * React renderer registry for navigation shell regions.
 */
export type NavigationShellRegionComponentRegistry =
  CoreNavigationShellRegionComponentRegistry<NavigationShellRegionComponentRenderer>

/**
 * React-specific shell region component resolution.
 */
export type NavigationShellRegionComponentResolution =
  CoreNavigationShellRegionComponentResolution<NavigationShellRegionComponentRenderer>

/**
 * Inputs used to resolve a React navigation shell region renderer.
 *
 * The React wrapper supplies the React target discriminator internally.
 */
export type ResolveNavigationShellRegionComponentOptions =
  Omit<
    CoreResolveNavigationShellRegionComponentOptions<
      NavigationShellRegionComponentRenderer
    >,
    'targetKind'
  >

/**
 * Creates a typed React navigation shell region component registry.
 */
export function createNavigationShellRegionComponentRegistry(
  registry: NavigationShellRegionComponentRegistry,
) {
  return createCoreNavigationShellRegionComponentRegistry(registry)
}

/**
 * Tests whether a React shell region resolves to a renderer through the active
 * target bindings and registry.
 */
export function hasNavigationShellRegionComponentTargetBinding(
  options: ResolveNavigationShellRegionComponentOptions,
) {
  return hasCoreNavigationShellRegionComponentTargetBinding({
    ...options,
    targetKind: reactPresentationTargetKind,
  })
}

/**
 * Host component that resolves and renders a projected navigation shell region.
 */
export function ProjectedNavigationShellRegionComponentHost({
  componentSet,
  fallback = null,
  module,
  region,
  registry,
}: ResolveNavigationShellRegionComponentOptions & {
  readonly fallback?: ReactNode
}) {
  const resolution = resolveNavigationShellRegionComponent({
    componentSet,
    module,
    region,
    registry,
  })
  const Renderer = resolution.renderer

  return Renderer ? <Renderer region={region} /> : fallback
}

/**
 * Resolves the React renderer for a projected navigation shell region.
 */
export function resolveNavigationShellRegionComponent(
  options: ResolveNavigationShellRegionComponentOptions,
): NavigationShellRegionComponentResolution {
  return resolveCoreNavigationShellRegionComponent({
    ...options,
    targetKind: reactPresentationTargetKind,
  })
}

const reactPresentationTargetKind = createPresentationEnumDiscriminator(
  presentationTargetKinds,
  'react',
  'React',
)
