import { useMemo } from 'react'

import {
  createPresentationSurfaceFromRootView,
  findPresentationView,
  getPresentationSurfaceDataSourceIds,
  projectPresentationDataSourceBindings,
  projectPresentationDataSourceCoverageDiagnostics,
  type PresentationDataSourceBinding,
  type PresentationDataSourceBindingProjectionRegistry,
  type PresentationDataSourceProjectionContext,
  type PresentationDataSourceTargetInterpretation,
  type ViewDefinition,
} from '@cohesive/presentation-core'
import {
  PresentationDataSourceBinder,
  usePresentationModule,
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesive/presentation-react'
import type {
  PresentationRendererRegistry,
} from '@cohesive/presentation-react'
import { PresentationSurfaceRenderer } from './presentation-surface-renderer'
import { ProjectedStatusBlock } from './projected-activity-state'
import type {
  NavigationShellRegionDefinition,
} from '@cohesive/presentation-contracts'

export interface ProjectedNavigationShellViewHostProps<TContext> {
  readonly bindings: readonly PresentationDataSourceBinding[]
  readonly componentSet?: string
  readonly context: TContext
  readonly region: NavigationShellRegionDefinition
  readonly rendererRegistry: PresentationRendererRegistry<TContext>
}

export interface ProjectedNavigationShellRegionViewProps<TContext> {
  readonly context: TContext
  readonly dataSourceBindingRegistry: PresentationDataSourceBindingProjectionRegistry
  readonly componentSet?: string
  readonly diagnosticsSourceId?: string
  readonly projectionContext?: PresentationDataSourceProjectionContext
  readonly region: NavigationShellRegionDefinition
  readonly rendererRegistry: PresentationRendererRegistry<TContext>
  readonly targetInterpretation?: PresentationDataSourceTargetInterpretation
}

/**
 * Projects a presentation view referenced by a navigation shell region.
 *
 * Shell regions are persistent app chrome, but their bodies can still be
 * ordinary presentation views with the same data-source and renderer
 * interpretation model used by routed surfaces.
 */
export function ProjectedNavigationShellViewHost<TContext>({
  bindings,
  componentSet,
  context,
  region,
  rendererRegistry,
}: ProjectedNavigationShellViewHostProps<TContext>) {
  const module = usePresentationModule()
  const viewId = region.ViewId ?? null
  const rootView = viewId ? findPresentationView<ViewDefinition>(module, viewId) : null
  const surface = useMemo(
    () =>
      rootView
        ? createPresentationSurfaceFromRootView(rootView, {
            id: region.Id,
          })
        : null,
    [region.Id, rootView],
  )

  if (!viewId) {
    return <ProjectedStatusBlock label={`Shell region '${region.Id}' has no presentation view.`} />
  }

  return (
    <PresentationDataSourceBinder bindings={bindings}>
      {(dataSources) => (
        <PresentationSurfaceRenderer
          componentSet={componentSet}
          context={context}
          dataSources={dataSources}
          rendererRegistry={rendererRegistry}
          surface={surface}
        />
      )}
    </PresentationDataSourceBinder>
  )
}

/**
 * Projects a shell region ViewId with the same data-source binding and
 * diagnostics pipeline used by routed presentation surfaces.
 */
export function ProjectedNavigationShellRegionView<TContext>({
  componentSet,
  context,
  dataSourceBindingRegistry,
  diagnosticsSourceId,
  projectionContext = defaultNavigationShellRegionProjectionContext,
  region,
  rendererRegistry,
  targetInterpretation,
}: ProjectedNavigationShellRegionViewProps<TContext>) {
  const module = usePresentationModule()
  const rootView = region.ViewId
    ? findPresentationView<ViewDefinition>(module, region.ViewId)
    : null
  const surface = useMemo(
    () =>
      rootView
        ? createPresentationSurfaceFromRootView(rootView, { id: region.Id })
        : null,
    [region.Id, rootView],
  )
  const dataSourceIds = useMemo(
    () => getPresentationSurfaceDataSourceIds(module, surface),
    [module, surface],
  )
  const bindings = useMemo(
    () =>
      projectPresentationDataSourceBindings({
        context: projectionContext,
        dataSourceIds,
        module,
        registry: dataSourceBindingRegistry,
      }),
    [
      dataSourceBindingRegistry,
      dataSourceIds,
      module,
      projectionContext,
    ],
  )
  const sourceId = diagnosticsSourceId ?? `navigation-shell-data-sources:${region.Id}`
  const dataSourceCoverageDiagnostics = useMemo(
    () =>
      projectPresentationDataSourceCoverageDiagnostics({
        bindings,
        dataSourceIds,
        module,
        routeParameters: projectionContext.routeParameters,
        sourceId,
        targetInterpretation,
      }),
    [
      bindings,
      dataSourceIds,
      module,
      projectionContext.routeParameters,
      sourceId,
      targetInterpretation,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    sourceId,
    dataSourceCoverageDiagnostics,
  )

  return (
    <ProjectedNavigationShellViewHost
      bindings={bindings}
      componentSet={componentSet}
      context={context}
      region={region}
      rendererRegistry={rendererRegistry}
    />
  )
}

const defaultNavigationShellRegionProjectionContext = {
  routeParameters: {},
  workspaceId: null,
} satisfies PresentationDataSourceProjectionContext
