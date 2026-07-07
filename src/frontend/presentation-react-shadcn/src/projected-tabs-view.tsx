import { Fragment, useEffect, useMemo, useState, type ReactNode } from 'react'

import {
  findPresentationView,
  getRegionViewIds,
  isViewChromeSlotKind,
  type ViewChromeSlotDefinition,
  type ViewDefinition,
  type ViewRegionDefinition,
} from '@cohesive/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  renderPresentationIcon,
} from './presentation-icon-registry'
import {
  projectPresentationIconDiagnostics,
} from './presentation-icon-diagnostics'
import {
  usePresentationModule,
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesive/presentation-react'
import { ProjectedStatusBlock } from './projected-activity-state'
import {
  ProjectedViewChrome,
} from './projected-view-chrome'
import type {
  ProjectedViewSurfaceChromeSlotRenderer,
} from './projected-view-surface'
import {
  viewChromeSlotKinds,
  viewChromeSlotPlacements,
  viewRegionKinds,
} from '@cohesive/presentation-contracts'

export interface ProjectedTabsViewProps {
  readonly chromeAfterContentClassName?: string
  readonly chromeBeforeContentClassName?: string
  readonly chromeFooterClassName?: string
  readonly chromeHeaderClassName?: string
  readonly className?: string
  readonly componentSystem: PresentationComponentSystem
  readonly defaultRegionId?: string
  readonly fallbackRegions?: readonly ViewRegionDefinition[]
  readonly iconByRegionId?: Readonly<Record<string, ReactNode>>
  readonly renderChromeSlot?: ProjectedViewSurfaceChromeSlotRenderer
  readonly renderView: (viewId: string) => ReactNode
  readonly view?: ViewDefinition | null
  readonly viewId: string
}

export function ProjectedTabsView({
  chromeAfterContentClassName,
  chromeBeforeContentClassName,
  chromeFooterClassName,
  chromeHeaderClassName,
  className,
  componentSystem,
  defaultRegionId,
  fallbackRegions,
  iconByRegionId,
  renderChromeSlot,
  renderView,
  view: viewOverride,
  viewId,
}: ProjectedTabsViewProps) {
  const module = usePresentationModule()
  const view = viewOverride ?? findPresentationView<ViewDefinition>(module, viewId)
  const tabRegions = useMemo(
    () => getTabRegions(view, fallbackRegions),
    [fallbackRegions, view],
  )
  const iconDiagnosticSource = `projected-tabs-view-icons:${view?.Id ?? viewId}`
  const iconDiagnostics = useMemo(
    () => projectPresentationIconDiagnostics({
      icons: resolveTabRegionIconSubjects(tabRegions),
      module,
      source: iconDiagnosticSource,
      surfaceId: view?.Id ?? viewId,
      surfaceName: view?.Name,
    }),
    [
      iconDiagnosticSource,
      module,
      tabRegions,
      view?.Id,
      view?.Name,
      viewId,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(iconDiagnosticSource, iconDiagnostics)
  const defaultValue =
    defaultRegionId ??
    view?.State?.find((state) => state.Type === 'region-id')?.DefaultValue ??
    tabRegions[0]?.Id
  const [requestedActiveRegionId, setActiveRegionId] = useState(defaultValue ?? '')
  const activeRegionId = tabRegions.some((region) => region.Id === requestedActiveRegionId)
    ? requestedActiveRegionId
    : defaultValue ?? ''
  const [visitedRegionIds, setVisitedRegionIds] = useState<ReadonlySet<string>>(
    () => new Set(activeRegionId ? [activeRegionId] : []),
  )

  useEffect(() => {
    if (!activeRegionId) {
      return
    }

    setVisitedRegionIds((current) => {
      if (current.has(activeRegionId)) {
        return current
      }

      return new Set([...current, activeRegionId])
    })
  }, [activeRegionId])

  if (!module && tabRegions.length === 0) {
    return <ProjectedStatusBlock label="Presentation module is not available." />
  }

  if (!view && tabRegions.length === 0) {
    return <ProjectedStatusBlock label={`Presentation view '${viewId}' is not available.`} />
  }

  if (!defaultValue || tabRegions.length === 0) {
    return <ProjectedStatusBlock label={`Presentation view '${view?.Name ?? view?.Id ?? viewId}' has no tabs.`} />
  }

  const renderTabsChromeSlot = (slot: ViewChromeSlotDefinition, chromeView: ViewDefinition | null) => {
    if (isViewChromeSlotKind(slot, viewChromeSlotKinds.viewSwitch)) {
      return renderTabsList({
        activeRegionId: activeRegionId || defaultValue,
        componentSystem,
        iconByRegionId,
        module,
        tabRegions,
        viewId: view?.Id ?? viewId,
      })
    }

    return renderChromeSlot?.(slot, chromeView) ?? null
  }
  const hasTabSwitchChrome = hasTabSwitchChromeSlot(view)

  return componentSystem.tabs.TabsLayout({
    className,
    onValueChange: setActiveRegionId,
    value: activeRegionId || defaultValue,
    viewId: view?.Id ?? viewId,
    children: (
      <>
      <ProjectedViewChrome
        className={chromeHeaderClassName}
        componentSystem={componentSystem}
        placement={viewChromeSlotPlacements.header}
        renderSlot={(slot) => renderTabsChromeSlot(slot, view)}
        view={view}
      />

      {hasTabSwitchChrome ? null : renderTabsList({
        activeRegionId: activeRegionId || defaultValue,
        componentSystem,
        iconByRegionId,
        module,
        tabRegions,
        viewId: view?.Id ?? viewId,
      })}

      <ProjectedViewChrome
        className={chromeBeforeContentClassName}
        componentSystem={componentSystem}
        placement={viewChromeSlotPlacements.beforeContent}
        renderSlot={(slot) => renderTabsChromeSlot(slot, view)}
        view={view}
      />

      {tabRegions.map((region) => {
        const viewIds = getRegionViewIds(region)
        const shouldRenderRegion =
          region.Id === activeRegionId || visitedRegionIds.has(region.Id)
        return (
          <Fragment key={region.Id}>
            {componentSystem.tabs.TabsPanel({
              region,
              value: region.Id,
              viewId: view?.Id ?? viewId,
              children: (
                <>
                  {shouldRenderRegion
                    ? viewIds.map((childViewId) => (
                      <div
                        className="flex min-h-0 flex-1 flex-col overflow-hidden"
                        key={childViewId}
                      >
                        {renderView(childViewId)}
                      </div>
                    ))
                    : null}
                </>
              ),
            })}
          </Fragment>
        )
      })}

      <ProjectedViewChrome
        className={chromeAfterContentClassName}
        componentSystem={componentSystem}
        placement={viewChromeSlotPlacements.afterContent}
        renderSlot={(slot) => renderTabsChromeSlot(slot, view)}
        view={view}
      />

      <ProjectedViewChrome
        className={chromeFooterClassName}
        componentSystem={componentSystem}
        placement={viewChromeSlotPlacements.footer}
        renderSlot={(slot) => renderTabsChromeSlot(slot, view)}
        view={view}
      />
      </>
    ),
  })
}

function renderTabsList({
  activeRegionId,
  componentSystem,
  iconByRegionId,
  module,
  tabRegions,
  viewId,
}: {
  readonly activeRegionId: string
  readonly componentSystem: PresentationComponentSystem
  readonly iconByRegionId: Readonly<Record<string, ReactNode>> | undefined
  readonly module: ReturnType<typeof usePresentationModule>
  readonly tabRegions: readonly ViewRegionDefinition[]
  readonly viewId: string | null
}) {
  return componentSystem.tabs.TabsList({
    viewId,
    children: tabRegions.map((region) => (
      <Fragment key={region.Id}>
        {componentSystem.tabs.TabsTrigger({
          isActive: activeRegionId === region.Id,
          region,
          value: region.Id,
          viewId,
          children: (
            <>
              {iconByRegionId?.[region.Id] ?? renderPresentationIcon({
                className: 'size-3.5',
                icon: region.Icon,
                module,
              })}
              {region.Name ?? region.Id}
            </>
          ),
        })}
      </Fragment>
    )),
  })
}

function resolveTabRegionIconSubjects(
  tabRegions: readonly ViewRegionDefinition[],
) {
  return tabRegions.flatMap((region) => {
    if (!region.Icon) {
      return []
    }

    return [{
      details: {
        regionId: region.Id,
      },
      icon: region.Icon,
      id: region.Id,
      kind: 'tab-region-icon',
      label: region.Name,
    }]
  })
}

function getTabRegions(
  view: ViewDefinition | null,
  fallbackRegions: readonly ViewRegionDefinition[] | undefined,
) {
  const regions = view?.Regions ?? fallbackRegions ?? []
  return (
    regions.filter(
      (region): region is ViewRegionDefinition =>
        hasRegionKind(region.Kind, 'Tab', 6) && getRegionViewIds(region).length > 0,
    )
  )
}

function hasRegionKind(value: string | number, name: string, numericValue: number) {
  return value === name || value === numericValue || value === viewRegionKinds.tab
}

function hasTabSwitchChromeSlot(view: ViewDefinition | null) {
  return view?.Chrome?.Slots.some((slot) =>
    isViewChromeSlotKind(slot, viewChromeSlotKinds.viewSwitch)) ?? false
}
