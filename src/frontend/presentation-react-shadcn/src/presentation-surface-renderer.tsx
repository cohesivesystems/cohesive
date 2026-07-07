import { useCallback, useMemo, type ReactNode } from 'react'

import {
  createPresentationDataSourceResolver,
  defaultPresentationComponentSet,
  findPresentationView,
  getRegionViewIds,
  type PresentationModuleDefinition,
  type PresentationDataSourceResolver,
  type PresentationDataSourceStateMap,
  type PresentationSurface,
  type ViewDefinition,
} from '@cohesivesystems/presentation-core'
import {
  usePresentationModule,
  resolvePresentationViewRenderer,
  type PresentationRendererRegistry,
  type PresentationViewRegionRenderOptions,
  type PresentationViewRendererResolution,
} from '@cohesivesystems/presentation-react'
import { ProjectedStatusBlock } from './projected-activity-state'

export interface PresentationSurfaceRendererProps<TContext> {
  readonly componentSet?: string
  readonly context: TContext
  readonly dataSources: PresentationDataSourceStateMap
  readonly rendererRegistry: PresentationRendererRegistry<TContext>
  readonly renderUnknownView?: PresentationUnknownViewRenderer<TContext>
  readonly surface: PresentationSurface | null
}

export interface PresentationUnknownViewRenderContext<TContext> {
  readonly context: TContext
  readonly module: PresentationModuleDefinition | null
  readonly reason:
    | 'missing-module'
    | 'missing-renderer'
    | 'missing-root-view'
    | 'missing-surface'
    | 'missing-view'
  readonly resolution?: PresentationViewRendererResolution<TContext>
  readonly surface: PresentationSurface | null
  readonly view: ViewDefinition | null
  readonly viewId: string | null
}

export type PresentationUnknownViewRenderer<TContext> = (
  context: PresentationUnknownViewRenderContext<TContext>,
) => ReactNode

interface PresentationSurfaceViewRendererProps<TContext> {
  readonly componentSet: string
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly module: PresentationModuleDefinition
  readonly rendererRegistry: PresentationRendererRegistry<TContext>
  readonly renderUnknownView: PresentationUnknownViewRenderer<TContext>
  readonly surface: PresentationSurface
  readonly viewId: string
}

export function PresentationSurfaceRenderer<TContext>({
  componentSet = defaultPresentationComponentSet,
  context,
  dataSources,
  rendererRegistry,
  renderUnknownView = renderDefaultUnknownView,
  surface,
}: PresentationSurfaceRendererProps<TContext>) {
  const module = usePresentationModule()
  const dataSourceResolver = useMemo(
    () => createPresentationDataSourceResolver(dataSources),
    [dataSources],
  )

  if (!module) {
    return renderUnknownView({
      context,
      module,
      reason: 'missing-module',
      surface,
      view: null,
      viewId: surface?.rootViewId ?? null,
    })
  }

  if (!surface) {
    return renderUnknownView({
      context,
      module,
      reason: 'missing-surface',
      surface,
      view: null,
      viewId: null,
    })
  }

  const rootViewId = surface.rootView?.Id ?? surface.rootViewId
  if (!rootViewId) {
    return renderUnknownView({
      context,
      module,
      reason: 'missing-root-view',
      surface,
      view: null,
      viewId: null,
    })
  }

  return (
    <PresentationSurfaceViewRenderer
      componentSet={componentSet}
      context={context}
      dataSourceResolver={dataSourceResolver}
      module={module}
      rendererRegistry={rendererRegistry}
      renderUnknownView={renderUnknownView}
      surface={surface}
      viewId={rootViewId}
    />
  )
}

function PresentationSurfaceViewRenderer<TContext>({
  componentSet,
  context,
  dataSourceResolver,
  module,
  rendererRegistry,
  renderUnknownView,
  surface,
  viewId,
}: PresentationSurfaceViewRendererProps<TContext>) {
  const view = findPresentationView<ViewDefinition>(module, viewId)
  const renderView = useCallback(
    (childViewId: string) => (
      <PresentationSurfaceViewRenderer
        componentSet={componentSet}
        context={context}
        dataSourceResolver={dataSourceResolver}
        module={module}
        rendererRegistry={rendererRegistry}
        renderUnknownView={renderUnknownView}
        surface={surface}
        viewId={childViewId}
      />
    ),
    [
      componentSet,
      context,
      dataSourceResolver,
      module,
      rendererRegistry,
      renderUnknownView,
      surface,
    ],
  )
  const renderRegions = useCallback(
    (
      regionHostView: ViewDefinition,
      options?: PresentationViewRegionRenderOptions,
    ) =>
      renderPresentationViewRegions({
        includeRegionIds: options?.includeRegionIds,
        renderView,
        view: regionHostView,
      }),
    [renderView],
  )

  if (!view) {
    return renderUnknownView({
      context,
      module,
      reason: 'missing-view',
      surface,
      view,
      viewId,
    })
  }

  const resolution = resolvePresentationViewRenderer({
    componentSet,
    module,
    registry: rendererRegistry,
    routeId: surface.navigationTarget.route.Id,
    view,
  })
  if (!resolution.renderer) {
    return renderUnknownView({
      context,
      module,
      reason: 'missing-renderer',
      resolution,
      surface,
      view,
      viewId,
    })
  }

  return resolution.renderer({
    componentKey: resolution.componentKey,
    componentRole: resolution.componentRole,
    context,
    dataSourceResolver,
    module,
    renderRegions,
    renderView,
    surface,
    view,
  })
}

function renderPresentationViewRegions({
  includeRegionIds,
  renderView,
  view,
}: {
  readonly includeRegionIds?: readonly string[]
  readonly renderView: (viewId: string) => ReactNode
  readonly view: ViewDefinition
}) {
  const regions = includeRegionIds
    ? view.Regions.filter((region) => includeRegionIds.includes(region.Id))
    : view.Regions

  return regions.flatMap((region) =>
    getRegionViewIds(region).map((childViewId) => (
      <div className="min-h-0 w-full min-w-0" key={`${region.Id}:${childViewId}`}>
        {renderView(childViewId)}
      </div>
    )),
  )
}

function renderDefaultUnknownView<TContext>({
  reason,
  resolution,
  view,
  viewId,
}: PresentationUnknownViewRenderContext<TContext>) {
  if (reason === 'missing-module') {
    return <ProjectedStatusBlock label="Presentation module is not available." />
  }

  if (reason === 'missing-surface') {
    return <ProjectedStatusBlock label="Presentation surface is not available." />
  }

  if (reason === 'missing-root-view') {
    return <ProjectedStatusBlock label="Presentation surface has no root view." />
  }

  if (reason === 'missing-view') {
    return <ProjectedStatusBlock label={`Presentation view '${viewId ?? 'unknown'}' is not available.`} />
  }

  return (
    <ProjectedStatusBlock
      label={`Presentation view '${view?.Name ?? viewId ?? 'unknown'}' has no renderer for semantic role '${resolution?.semanticRole ?? 'unknown'}'.`}
    />
  )
}
