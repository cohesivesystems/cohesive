import type {
  ProjectedNavigationShellSlotLayout,
} from '@cohesive/presentation-tailwind'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type {
  NavigationShellSlotRenderer as ReactNavigationShellSlotRenderer,
  NavigationShellSlotRendererBinding as ReactNavigationShellSlotRendererBinding,
  NavigationShellSlotRendererRegistry as ReactNavigationShellSlotRendererRegistry,
  ProjectedNavigationShellSlotRenderContext as ReactProjectedNavigationShellSlotRenderContext,
} from '@cohesive/presentation-react'

export {
  createNavigationShellSlotRendererKey,
  createNavigationShellSlotRendererRegistry,
  getNavigationShellSlotRendererCandidateKeys,
  getNavigationShellSlotRendererRegistryKeys,
  hasNavigationShellSlotRendererBinding,
  navigationShellSlotAnyPlacement,
  resolveNavigationShellSlotRenderer,
} from '@cohesive/presentation-react'

export type ProjectedNavigationShellSlotRenderContext =
  ReactProjectedNavigationShellSlotRenderContext<
    ProjectedNavigationShellSlotLayout,
    PresentationComponentSystem
  >

export type NavigationShellSlotRenderer =
  ReactNavigationShellSlotRenderer<
    ProjectedNavigationShellSlotLayout,
    PresentationComponentSystem
  >

export type NavigationShellSlotRendererRegistry =
  ReactNavigationShellSlotRendererRegistry<
    ProjectedNavigationShellSlotLayout,
    PresentationComponentSystem
  >

export type NavigationShellSlotRendererBinding =
  ReactNavigationShellSlotRendererBinding<
    ProjectedNavigationShellSlotLayout,
    PresentationComponentSystem
  >
