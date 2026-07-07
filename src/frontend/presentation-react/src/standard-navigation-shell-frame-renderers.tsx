import type { ReactNode } from 'react'

import {
  navigationShellKinds,
  type NavigationShellSlotDefinition,
} from '@cohesivesystems/presentation-contracts'
import type {
  ProjectedNavigationShellLayout,
} from '@cohesivesystems/presentation-core'
import {
  createNavigationShellFrameRendererRegistry,
  getNavigationShellFrameRendererRegistryKeys,
  resolveNavigationShellFrameRenderer,
  type ProjectedNavigationShellFrameRenderContext,
} from './navigation-shell-frame-renderer-registry'

export interface StandardNavigationShellFrameLayout {
  readonly headerClassName: string
  readonly headerContentClassName: string
  readonly headerNavigationClassName: string
  readonly rootClassName: string
}

export type StandardNavigationShellLayout<TSlotLayout = unknown> =
  ProjectedNavigationShellLayout<
    StandardNavigationShellFrameLayout,
    TSlotLayout
  >

export type StandardNavigationShellFrameRenderContext<TSlotLayout = unknown> =
  ProjectedNavigationShellFrameRenderContext<
    StandardNavigationShellLayout<TSlotLayout>
  >

export const standardNavigationShellFrameRenderers =
  createNavigationShellFrameRendererRegistry([
    {
      kind: navigationShellKinds.topNavigation,
      render: renderStandardTopNavigationShellFrame,
    },
  ])

export const standardNavigationShellFrameRendererKeys =
  getNavigationShellFrameRendererRegistryKeys(standardNavigationShellFrameRenderers)

export function renderStandardNavigationShellFrame(
  context: StandardNavigationShellFrameRenderContext,
) {
  const renderer = resolveNavigationShellFrameRenderer(
    standardNavigationShellFrameRenderers,
    context.navigation.Shell.Kind,
  )

  return renderer?.(context) ?? context.children
}

function renderStandardTopNavigationShellFrame({
  children,
  layout,
  renderSlot,
}: StandardNavigationShellFrameRenderContext) {
  const frame = layout.frame

  return (
    <div className={frame.rootClassName}>
      <header className={frame.headerClassName}>
        <div className={frame.headerContentClassName}>
          {layout.brandSlot ? (
            <ShellSlot slot={layout.brandSlot}>
              {renderSlot(layout.brandSlot)}
            </ShellSlot>
          ) : null}
          <div className={frame.headerNavigationClassName}>
            {layout.primaryNavigationSlot ? (
              <ShellSlot slot={layout.primaryNavigationSlot}>
                {renderSlot(layout.primaryNavigationSlot)}
              </ShellSlot>
            ) : null}
            {layout.utilityActionSlots.map((slot) => (
              <ShellSlot key={slot.Id} slot={slot}>
                {renderSlot(slot)}
              </ShellSlot>
            ))}
          </div>
        </div>
      </header>
      {layout.systemNoticeSlots.map((slot) => (
        <ShellSlot key={slot.Id} slot={slot}>
          {renderSlot(slot)}
        </ShellSlot>
      ))}
      <ShellSlot slot={layout.routedContentSlot}>
        {layout.routedContentSlot ? renderSlot(layout.routedContentSlot) : children}
      </ShellSlot>
    </div>
  )
}

function ShellSlot({
  children,
}: {
  readonly children: ReactNode
  readonly slot: NavigationShellSlotDefinition | null
}) {
  return <>{children}</>
}
