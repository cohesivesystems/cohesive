import type {
  ProjectedNavigationShellLayout,
} from '@cohesivesystems/presentation-tailwind'
import type {
  NavigationShellFrameRenderer as ReactNavigationShellFrameRenderer,
  NavigationShellFrameRendererBinding as ReactNavigationShellFrameRendererBinding,
  NavigationShellFrameRendererRegistry as ReactNavigationShellFrameRendererRegistry,
  ProjectedNavigationShellFrameRenderContext as ReactProjectedNavigationShellFrameRenderContext,
} from '@cohesivesystems/presentation-react'

export {
  createNavigationShellFrameRendererKey,
  createNavigationShellFrameRendererRegistry,
  getNavigationShellFrameRendererRegistryKeys,
  hasNavigationShellFrameRendererBinding,
  resolveNavigationShellFrameRenderer,
} from '@cohesivesystems/presentation-react'

export type ProjectedNavigationShellFrameRenderContext =
  ReactProjectedNavigationShellFrameRenderContext<ProjectedNavigationShellLayout>

export type NavigationShellFrameRenderer =
  ReactNavigationShellFrameRenderer<ProjectedNavigationShellLayout>

export type NavigationShellFrameRendererRegistry =
  ReactNavigationShellFrameRendererRegistry<ProjectedNavigationShellLayout>

export type NavigationShellFrameRendererBinding =
  ReactNavigationShellFrameRendererBinding<ProjectedNavigationShellLayout>
