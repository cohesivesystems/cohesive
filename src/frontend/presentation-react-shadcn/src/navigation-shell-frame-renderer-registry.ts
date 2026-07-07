import type {
  ProjectedNavigationShellLayout,
} from '@cohesive/presentation-tailwind'
import type {
  NavigationShellFrameRenderer as ReactNavigationShellFrameRenderer,
  NavigationShellFrameRendererBinding as ReactNavigationShellFrameRendererBinding,
  NavigationShellFrameRendererRegistry as ReactNavigationShellFrameRendererRegistry,
  ProjectedNavigationShellFrameRenderContext as ReactProjectedNavigationShellFrameRenderContext,
} from '@cohesive/presentation-react'

export {
  createNavigationShellFrameRendererKey,
  createNavigationShellFrameRendererRegistry,
  getNavigationShellFrameRendererRegistryKeys,
  hasNavigationShellFrameRendererBinding,
  resolveNavigationShellFrameRenderer,
} from '@cohesive/presentation-react'

export type ProjectedNavigationShellFrameRenderContext =
  ReactProjectedNavigationShellFrameRenderContext<ProjectedNavigationShellLayout>

export type NavigationShellFrameRenderer =
  ReactNavigationShellFrameRenderer<ProjectedNavigationShellLayout>

export type NavigationShellFrameRendererRegistry =
  ReactNavigationShellFrameRendererRegistry<ProjectedNavigationShellLayout>

export type NavigationShellFrameRendererBinding =
  ReactNavigationShellFrameRendererBinding<ProjectedNavigationShellLayout>
