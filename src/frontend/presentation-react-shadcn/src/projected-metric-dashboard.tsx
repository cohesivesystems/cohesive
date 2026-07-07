import type { ReactNode } from 'react'

import {
  findPresentationView,
  getPresentationViewProjectedFieldIds,
  isViewChromeSlotKind,
  type PresentationDataSourceResolver,
  type ViewDefinition,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import { usePresentationModule } from '@cohesivesystems/presentation-react'
import { ProjectedMetricStrip, type ProjectedMetricValue } from './projected-metric-strip'
import {
  ProjectedViewSurface,
  type ProjectedViewSurfaceChromeSlotRenderer,
} from './projected-view-surface'
import { viewChromeSlotKinds } from '@cohesivesystems/presentation-contracts'

export interface ProjectedMetricDashboardProps {
  readonly action?: ReactNode
  readonly children?: ReactNode
  readonly className?: string
  readonly chromeAfterContentClassName?: string
  readonly chromeBeforeContentClassName?: string
  readonly chromeFooterClassName?: string
  readonly chromeHeaderClassName?: string
  readonly componentSystem: PresentationComponentSystem
  readonly contentClassName?: string
  readonly dataSourceResolver?: PresentationDataSourceResolver
  readonly description?: string
  readonly iconByFieldId?: Readonly<Record<string, ReactNode>>
  readonly metricValues?: Readonly<Record<string, ProjectedMetricValue>>
  readonly renderChromeSlot?: ProjectedViewSurfaceChromeSlotRenderer
  readonly title?: string
  readonly view?: ViewDefinition | null
  readonly viewId: string
}

export function ProjectedMetricDashboard({
  action,
  children,
  className,
  chromeAfterContentClassName,
  chromeBeforeContentClassName,
  chromeFooterClassName,
  chromeHeaderClassName,
  componentSystem,
  contentClassName = 'grid gap-4',
  dataSourceResolver,
  description,
  iconByFieldId,
  metricValues,
  renderChromeSlot,
  title,
  view: viewOverride,
  viewId,
}: ProjectedMetricDashboardProps) {
  const module = usePresentationModule()
  const view = viewOverride ?? findPresentationView<ViewDefinition>(module, viewId)
  const renderLegacyMetricStrip = !renderChromeSlot || !hasMetricStripChromeSlot(view)

  return (
    <ProjectedViewSurface
      action={action}
      className={className}
      chromeAfterContentClassName={chromeAfterContentClassName}
      chromeBeforeContentClassName={chromeBeforeContentClassName}
      chromeFooterClassName={chromeFooterClassName}
      chromeHeaderClassName={chromeHeaderClassName}
      collapsible={view?.Chrome?.Collapsible ?? true}
      collapseLabel={view?.Name ?? 'summary'}
      componentSystem={componentSystem}
      contentClassName={contentClassName}
      description={description}
      renderChromeSlot={renderChromeSlot}
      title={title}
      view={view}
    >
      {renderLegacyMetricStrip ? (
        <ProjectedMetricStrip
          componentSystem={componentSystem}
          dataSourceResolver={dataSourceResolver}
          fieldIds={readMetricFieldIds(view)}
          iconByFieldId={iconByFieldId}
          values={metricValues}
          viewId={viewId}
        />
      ) : null}
      {children}
    </ProjectedViewSurface>
  )
}

function readMetricFieldIds(view: ViewDefinition | null) {
  const metricSlot = view?.Chrome?.Slots?.find(
    (slot) => isViewChromeSlotKind(slot, viewChromeSlotKinds.metricStrip),
  )
  return metricSlot?.FieldIds ?? (view ? getPresentationViewProjectedFieldIds(view) : undefined)
}

function hasMetricStripChromeSlot(view: ViewDefinition | null) {
  return view?.Chrome?.Slots.some((slot) =>
    isViewChromeSlotKind(slot, viewChromeSlotKinds.metricStrip)) ?? false
}
