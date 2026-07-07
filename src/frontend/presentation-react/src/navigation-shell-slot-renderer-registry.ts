import type { ReactNode } from 'react'

import type {
  NavigationDefinition,
  NavigationNodeDefinition,
  NavigationShellRegionDefinition,
  NavigationShellSlotDefinition,
  NavigationShellSlotKind,
} from '@cohesivesystems/presentation-contracts'
import {
  createNavigationShellSlotRendererRegistry as createCoreNavigationShellSlotRendererRegistry,
  resolveNavigationShellSlotRenderer as resolveCoreNavigationShellSlotRenderer,
  type NavigationShellSlotRendererBinding as CoreNavigationShellSlotRendererBinding,
  type NavigationShellSlotRendererRegistry as CoreNavigationShellSlotRendererRegistry,
} from '@cohesivesystems/presentation-core'
export {
  createNavigationShellSlotRendererKey,
  getNavigationShellSlotRendererCandidateKeys,
  getNavigationShellSlotRendererRegistryKeys,
  hasNavigationShellSlotRendererBinding,
  navigationShellSlotAnyPlacement,
} from '@cohesivesystems/presentation-core'

export interface ProjectedNavigationShellSlotRenderContext<
  TSlotLayout = unknown,
  TComponentSystem = unknown,
> {
  readonly activePath: string
  readonly children: ReactNode
  readonly componentSystem: TComponentSystem
  readonly navigation: NavigationDefinition
  readonly renderNodeIcon?: (node: NavigationNodeDefinition) => ReactNode
  readonly renderShellIcon?: (icon: string) => ReactNode
  readonly renderShellRegion?: (region: NavigationShellRegionDefinition) => ReactNode
  readonly slot: NavigationShellSlotDefinition
  readonly slotLayout: TSlotLayout
}

export type NavigationShellSlotRenderer<
  TSlotLayout = unknown,
  TComponentSystem = unknown,
> = (
  context: ProjectedNavigationShellSlotRenderContext<
    TSlotLayout,
    TComponentSystem
  >,
) => ReactNode

export type NavigationShellSlotRendererRegistry<
  TSlotLayout = unknown,
  TComponentSystem = unknown,
> = CoreNavigationShellSlotRendererRegistry<
  NavigationShellSlotRenderer<TSlotLayout, TComponentSystem>
>

export type NavigationShellSlotRendererBinding<
  TSlotLayout = unknown,
  TComponentSystem = unknown,
> = CoreNavigationShellSlotRendererBinding<
  NavigationShellSlotRenderer<TSlotLayout, TComponentSystem>
>

export function createNavigationShellSlotRendererRegistry<
  TSlotLayout,
  TComponentSystem,
>(
  bindings: readonly NavigationShellSlotRendererBinding<
    TSlotLayout,
    TComponentSystem
  >[],
): NavigationShellSlotRendererRegistry<TSlotLayout, TComponentSystem> {
  return createCoreNavigationShellSlotRendererRegistry(bindings)
}

export function resolveNavigationShellSlotRenderer<
  TSlotLayout,
  TComponentSystem,
>(
  registry: NavigationShellSlotRendererRegistry<
    TSlotLayout,
    TComponentSystem
  > | null | undefined,
  slot: NavigationShellSlotDefinition,
) {
  return resolveCoreNavigationShellSlotRenderer(registry, slot)
}

export type { NavigationShellSlotKind }
