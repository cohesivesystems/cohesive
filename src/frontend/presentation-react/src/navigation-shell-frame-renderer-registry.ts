import type { ReactNode } from 'react'

import type {
  NavigationDefinition,
  NavigationNodeDefinition,
  NavigationShellKind,
  NavigationShellRegionDefinition,
  NavigationShellSlotDefinition,
} from '@cohesive/presentation-contracts'
import {
  createNavigationShellFrameRendererRegistry as createCoreNavigationShellFrameRendererRegistry,
  resolveNavigationShellFrameRenderer as resolveCoreNavigationShellFrameRenderer,
  type NavigationShellFrameRendererBinding as CoreNavigationShellFrameRendererBinding,
  type NavigationShellFrameRendererRegistry as CoreNavigationShellFrameRendererRegistry,
} from '@cohesive/presentation-core'
export {
  createNavigationShellFrameRendererKey,
  getNavigationShellFrameRendererRegistryKeys,
  hasNavigationShellFrameRendererBinding,
} from '@cohesive/presentation-core'

export interface ProjectedNavigationShellFrameRenderContext<TLayout = unknown> {
  readonly activePath: string
  readonly children: ReactNode
  readonly layout: TLayout
  readonly navigation: NavigationDefinition
  readonly renderNodeIcon?: (node: NavigationNodeDefinition) => ReactNode
  readonly renderShellIcon?: (icon: string) => ReactNode
  readonly renderShellRegion?: (region: NavigationShellRegionDefinition) => ReactNode
  readonly renderSlot: (slot: NavigationShellSlotDefinition | null) => ReactNode
}

export type NavigationShellFrameRenderer<TLayout = unknown> = (
  context: ProjectedNavigationShellFrameRenderContext<TLayout>,
) => ReactNode

export type NavigationShellFrameRendererRegistry<TLayout = unknown> =
  CoreNavigationShellFrameRendererRegistry<NavigationShellFrameRenderer<TLayout>>

export type NavigationShellFrameRendererBinding<TLayout = unknown> =
  CoreNavigationShellFrameRendererBinding<NavigationShellFrameRenderer<TLayout>>

export function createNavigationShellFrameRendererRegistry<TLayout>(
  bindings: readonly NavigationShellFrameRendererBinding<TLayout>[],
): NavigationShellFrameRendererRegistry<TLayout> {
  return createCoreNavigationShellFrameRendererRegistry(bindings)
}

export function resolveNavigationShellFrameRenderer<TLayout>(
  registry: NavigationShellFrameRendererRegistry<TLayout> | null | undefined,
  kind: NavigationShellKind | string | number,
) {
  return resolveCoreNavigationShellFrameRenderer(registry, kind)
}
