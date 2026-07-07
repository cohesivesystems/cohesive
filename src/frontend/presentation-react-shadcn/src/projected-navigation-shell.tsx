import { type ReactNode, useCallback, useMemo } from 'react'

import {
  defaultNavigationShellSlotPlacements,
  findPresentationView,
  projectNavigationShellDiagnostics,
  resolveProjectedNavigationShellSlots,
} from '@cohesive/presentation-core'
import {
  getNavigationShellFrameRendererRegistryKeys,
  resolveNavigationShellFrameRenderer,
  type NavigationShellFrameRendererRegistry,
} from './navigation-shell-frame-renderer-registry'
import {
  projectNavigationShellLayoutDiagnostics,
  resolveProjectedNavigationShellLayout,
  resolveProjectedNavigationShellSlotLayout,
} from '@cohesive/presentation-tailwind'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  projectPresentationDesignSystemBindingDiagnostics,
  projectPresentationComponentSystemDiagnostics,
} from './presentation-component-system-diagnostics'
import type { PresentationDesignSystem } from '@cohesive/presentation-tailwind'
import {
  getNavigationShellSlotRendererRegistryKeys,
  resolveNavigationShellSlotRenderer,
  type NavigationShellSlotRendererRegistry,
} from './navigation-shell-slot-renderer-registry'
import {
  usePresentationModule,
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesive/presentation-react'
import {
  standardNavigationShellSlotRenderers,
} from '@cohesive/presentation-react'
import {
  standardNavigationShellFrameRenderers,
} from '@cohesive/presentation-react'
import type {
  NavigationDefinition,
  NavigationNodeDefinition,
  NavigationShellRegionDefinition,
  NavigationShellSlotDefinition,
} from '@cohesive/presentation-contracts'

export interface ProjectedNavigationShellProps {
  readonly activePath: string
  readonly children: ReactNode
  readonly componentSystem: PresentationComponentSystem
  readonly defaultComponentSystem: PresentationComponentSystem
  readonly designSystem: PresentationDesignSystem
  readonly frameRendererRegistry?: NavigationShellFrameRendererRegistry
  readonly isNodeIconSupported?: (icon: string) => boolean
  readonly isShellRegionComponentBound?: (
    componentKey: string,
    region: NavigationShellRegionDefinition,
  ) => boolean
  readonly isShellRegionComponentTargetBound?: (
    region: NavigationShellRegionDefinition,
  ) => boolean
  readonly isShellRegionSupported?: (region: NavigationShellRegionDefinition) => boolean
  readonly navigation: NavigationDefinition
  readonly renderNodeIcon?: (node: NavigationNodeDefinition) => ReactNode
  readonly renderShellIcon?: (icon: string) => ReactNode
  readonly renderShellRegion?: (region: NavigationShellRegionDefinition) => ReactNode
  readonly slotRendererRegistry?: NavigationShellSlotRendererRegistry
  readonly supportedSlotPlacements?: readonly string[]
}

/**
 * Interprets a navigation shell from the presentation/navigation IR.
 *
 * The shell frame owns generic concerns such as brand chrome, primary
 * navigation, placement slots, and the routed content slot. App-specific
 * integrations remain attached through icon and shell-region renderers.
 */
export function ProjectedNavigationShell({
  activePath,
  children,
  componentSystem,
  defaultComponentSystem,
  designSystem,
  isNodeIconSupported,
  isShellRegionComponentBound,
  isShellRegionComponentTargetBound,
  isShellRegionSupported,
  navigation,
  renderNodeIcon,
  renderShellIcon,
  renderShellRegion,
  frameRendererRegistry = standardNavigationShellFrameRenderers,
  slotRendererRegistry = standardNavigationShellSlotRenderers,
  supportedSlotPlacements = defaultNavigationShellSlotPlacements,
}: ProjectedNavigationShellProps) {
  const module = usePresentationModule()
  const shellSlots = useMemo(
    () => resolveProjectedNavigationShellSlots(navigation),
    [navigation],
  )
  const shellLayout = useMemo(
    () => resolveProjectedNavigationShellLayout(navigation, shellSlots, designSystem),
    [designSystem, navigation, shellSlots],
  )
  const shellFrameRendererKeys = useMemo(
    () => getNavigationShellFrameRendererRegistryKeys(frameRendererRegistry),
    [frameRendererRegistry],
  )
  const shellSlotRendererKeys = useMemo(
    () => getNavigationShellSlotRendererRegistryKeys(slotRendererRegistry),
    [slotRendererRegistry],
  )
  const renderSlot = useCallback(
    (slot: NavigationShellSlotDefinition | null) => {
      if (!slot) {
        return null
      }

      const renderer = resolveNavigationShellSlotRenderer(slotRendererRegistry, slot)

      return renderer?.({
        activePath,
        children,
        componentSystem,
        navigation,
        renderNodeIcon,
        renderShellIcon,
        renderShellRegion,
        slot,
        slotLayout:
          shellLayout.slotLayouts[slot.Id] ??
          resolveProjectedNavigationShellSlotLayout(slot, designSystem),
      }) ?? null
    },
    [
      activePath,
      children,
      componentSystem,
      designSystem,
      navigation,
      renderNodeIcon,
      renderShellIcon,
      renderShellRegion,
      shellLayout,
      slotRendererRegistry,
    ],
  )
  const shellFrameRenderer = useMemo(
    () => resolveNavigationShellFrameRenderer(frameRendererRegistry, navigation.Shell.Kind),
    [frameRendererRegistry, navigation.Shell.Kind],
  )
  const diagnostics = useMemo(
    () => [
      ...projectPresentationComponentSystemDiagnostics({
        componentSystem,
        defaultComponentSystem,
        sourceId: `navigation-shell:${navigation.Shell.Id}`,
      }),
      ...projectPresentationDesignSystemBindingDiagnostics({
        componentSystem,
        module,
        sourceId: `navigation-shell:${navigation.Shell.Id}`,
      }),
      ...projectNavigationShellLayoutDiagnostics({
        designSystem,
        navigation,
        shellFrameRendererKeys,
        shellSlots,
      }),
      ...projectNavigationShellDiagnostics({
        isNodeIconSupported,
        isShellRegionComponentBound,
        isShellRegionComponentTargetBound,
        isShellRegionSupported,
        isShellRegionViewBound: module
          ? (viewId) => Boolean(findPresentationView(module, viewId))
          : undefined,
        navigation,
        renderShellIconBound: Boolean(renderShellIcon),
        shellSlotRendererKeys,
        shellSlots,
        supportedSlotPlacements,
      }),
    ],
    [
      componentSystem,
      defaultComponentSystem,
      designSystem,
      isNodeIconSupported,
      isShellRegionComponentBound,
      isShellRegionComponentTargetBound,
      isShellRegionSupported,
      module,
      navigation,
      renderShellIcon,
      shellFrameRendererKeys,
      shellSlotRendererKeys,
      shellSlots,
      supportedSlotPlacements,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    `navigation-shell:${navigation.Shell.Id}`,
    diagnostics,
  )

  return shellFrameRenderer?.({
    activePath,
    children,
    layout: shellLayout,
    navigation,
    renderNodeIcon,
    renderShellIcon,
    renderShellRegion,
    renderSlot,
  }) ?? children
}
