import { useMemo, type ReactNode } from 'react'

import type {
  ViewChromeSlotDefinition,
  ViewChromeSlotPlacement,
  ViewDefinition,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  isViewChromeSlotKind,
  isViewChromeSlotPlacementValue,
  resolveViewChromeIconSubjects,
} from '@cohesivesystems/presentation-core'
import {
  projectPresentationIconDiagnostics,
} from './presentation-icon-diagnostics'
import {
  usePresentationModule,
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesivesystems/presentation-react'
import {
  viewChromeSlotKinds,
  viewChromeSlotPlacements,
} from '@cohesivesystems/presentation-contracts'

export interface ProjectedViewChromeProps {
  readonly className?: string
  readonly componentSystem: PresentationComponentSystem
  readonly placement: ViewChromeSlotPlacement | string | number
  readonly renderContainer?: (children: ReactNode) => ReactNode
  readonly renderSlot: (slot: ViewChromeSlotDefinition) => ReactNode
  readonly view: ViewDefinition | null
}

export function ProjectedViewChrome({
  className,
  componentSystem,
  placement,
  renderContainer,
  renderSlot,
  view,
}: ProjectedViewChromeProps) {
  const module = usePresentationModule()
  const slots = useMemo(
    () => resolveViewChromeSlots(view, placement),
    [placement, view],
  )
  const iconDiagnostics = useMemo(
    () => projectPresentationIconDiagnostics({
      icons: resolveViewChromeIconSubjects(slots),
      module,
      source: `projected-view-chrome-icons:${view?.Id ?? 'unknown'}:${String(placement)}`,
      surfaceId: view?.Id,
      surfaceName: view?.Name,
    }),
    [
      module,
      placement,
      slots,
      view?.Id,
      view?.Name,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    `projected-view-chrome-icons:${view?.Id ?? 'unknown'}:${String(placement)}`,
    iconDiagnostics,
  )

  const ViewChromeSlot = componentSystem.viewChrome.ViewChromeSlot
  const renderedSlots = slots
    .map((slot) => {
      const rendered = renderSlot(slot)
      return isRenderableNode(rendered) ? (
        <ViewChromeSlot
          key={slot.Id}
          placement={placement}
          slot={slot}
          viewId={view?.Id ?? null}
        >
          {rendered}
        </ViewChromeSlot>
      ) : null
    })
    .filter(isRenderableNode)

  if (renderedSlots.length === 0) {
    return null
  }

  if (renderContainer) {
    return renderContainer(renderedSlots)
  }

  return className ? (
    <div className={cn(className)}>{renderedSlots}</div>
  ) : (
    <>{renderedSlots}</>
  )
}

export function resolveViewChromeSlots(
  view: ViewDefinition | null,
  placement: ViewChromeSlotPlacement | string | number,
) {
  return (view?.Chrome?.Slots ?? []).filter((slot) =>
    isViewChromeSlotPlacementValue(
      resolveViewChromeSlotPlacement(slot),
      placement,
    ))
}

export function resolveViewChromeSlotPlacement(
  slot: ViewChromeSlotDefinition,
): ViewChromeSlotPlacement {
  if (slot.Placement !== undefined && slot.Placement !== null) {
    return slot.Placement
  }

  if (
    isViewChromeSlotKind(slot, viewChromeSlotKinds.actions) ||
    isViewChromeSlotKind(slot, viewChromeSlotKinds.viewSwitch) ||
    isViewChromeSlotKind(slot, viewChromeSlotKinds.layoutSwitch) ||
    isViewChromeSlotKind(slot, viewChromeSlotKinds.headingTrailing) ||
    isViewChromeSlotKind(slot, viewChromeSlotKinds.status)
  ) {
    return viewChromeSlotPlacements.header
  }

  if (isViewChromeSlotKind(slot, viewChromeSlotKinds.badgeStrip)) {
    return viewChromeSlotPlacements.beforeContent
  }

  if (isViewChromeSlotKind(slot, viewChromeSlotKinds.metricStrip)) {
    return viewChromeSlotPlacements.afterContent
  }

  return viewChromeSlotPlacements.none
}

function isRenderableNode(node: ReactNode): node is Exclude<ReactNode, null | undefined | false> {
  return node !== null && node !== undefined && node !== false
}

function cn(...values: readonly (false | null | string | undefined)[]) {
  return values.filter(Boolean).join(' ')
}
