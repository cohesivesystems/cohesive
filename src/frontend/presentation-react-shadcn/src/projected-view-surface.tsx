import type { ReactNode } from 'react'

import {
  findPresentationView,
  type ViewChromeSlotDefinition,
  type ViewChromeSlotPlacement,
  type ViewDefinition,
} from '@cohesive/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type {
  PresentationViewSurfaceContentTopInset,
  PresentationViewSurfaceVerticalResizeOptions,
} from './presentation-component-groups'
import { usePresentationModule } from '@cohesive/presentation-react'
import {
  ProjectedViewChrome,
  resolveViewChromeSlots,
} from './projected-view-chrome'
import { viewChromeSlotPlacements } from '@cohesive/presentation-contracts'

interface ProjectedViewSurfaceProps {
  readonly action?: ReactNode
  readonly children?: ReactNode
  readonly className?: string
  readonly chromeAfterContentClassName?: string
  readonly chromeBeforeContentClassName?: string
  readonly chromeFooterClassName?: string
  readonly chromeHeaderClassName?: string
  readonly collapsible?: boolean
  readonly collapsed?: boolean
  readonly collapseLabel?: string
  readonly componentSystem: PresentationComponentSystem
  readonly contentClassName?: string
  readonly contentTopInset?: PresentationViewSurfaceContentTopInset | null
  readonly defaultCollapsed?: boolean
  readonly description?: string
  readonly eyebrow?: string
  readonly onCollapsedChange?: (collapsed: boolean) => void
  readonly renderChromeSlot?: ProjectedViewSurfaceChromeSlotRenderer
  readonly title?: string
  readonly verticalResize?: boolean | PresentationViewSurfaceVerticalResizeOptions | null
  readonly view?: ViewDefinition | null
  readonly viewId?: string
}

export type ProjectedViewSurfaceChromeSlotRenderer = (
  slot: ViewChromeSlotDefinition,
  view: ViewDefinition | null,
) => ReactNode

export type ProjectedViewSurfaceContentTopInset =
  PresentationViewSurfaceContentTopInset

export type ProjectedViewSurfaceVerticalResizeOptions =
  PresentationViewSurfaceVerticalResizeOptions

export function ProjectedViewSurface({
  action,
  children,
  chromeAfterContentClassName,
  chromeBeforeContentClassName,
  chromeFooterClassName,
  chromeHeaderClassName,
  collapsible,
  componentSystem,
  renderChromeSlot,
  title,
  view: viewOverride,
  viewId,
  ...props
}: ProjectedViewSurfaceProps) {
  const module = usePresentationModule()
  const view = viewOverride ?? (viewId
    ? findPresentationView<ViewDefinition>(module, viewId)
    : null)
  const renderChromePlacement = (
    placement: ViewChromeSlotPlacement,
    className?: string,
  ) => renderChromeSlot && resolveViewChromeSlots(view, placement).length > 0 ? (
    <ProjectedViewChrome
      componentSystem={componentSystem}
      placement={placement}
      renderContainer={(children) =>
        componentSystem.surfaces.ViewSurfaceChromePlacement({
          children,
          className,
          placement,
          viewId: view?.Id ?? null,
        })}
      renderSlot={(slot) => renderChromeSlot(slot, view)}
      view={view}
    />
  ) : null
  const headerChrome = renderChromePlacement(
    viewChromeSlotPlacements.header,
    chromeHeaderClassName,
  )
  const beforeContentChrome = renderChromePlacement(
    viewChromeSlotPlacements.beforeContent,
    chromeBeforeContentClassName,
  )
  const afterContentChrome = renderChromePlacement(
    viewChromeSlotPlacements.afterContent,
    chromeAfterContentClassName,
  )
  const footerChrome = renderChromePlacement(
    viewChromeSlotPlacements.footer,
    chromeFooterClassName,
  )

  return componentSystem.surfaces.ViewSurface({
    ...props,
    action: componentSystem.surfaces.ViewSurfaceHeaderActions({
      action,
      chrome: headerChrome,
      viewId: view?.Id ?? null,
    }),
    children: componentSystem.surfaces.ViewSurfaceContent({
      afterContentChrome,
      beforeContentChrome,
      children,
      footerChrome,
      viewId: view?.Id ?? null,
    }),
    collapsible: collapsible ?? view?.Chrome?.Collapsible,
    title: title ?? view?.Name,
    viewId: view?.Id ?? null,
  })
}
