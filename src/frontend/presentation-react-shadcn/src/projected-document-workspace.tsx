import { useMemo, type ReactNode } from 'react'

import {
  type DocumentWorkspaceProjectionRendererRegistry,
  type DocumentWorkspaceRuntimeSnapshot,
  renderDocumentWorkspaceProjection,
  usePresentationModule,
} from '@cohesive/presentation-react'
import {
  findDocumentProfileDataSource,
  documentSummaryStructuralMessageFieldPath,
  type PresentationDataSourceResolver,
  type DocumentMetricSourceProjection,
} from '@cohesive/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type { PresentationDesignSystem } from '@cohesive/presentation-tailwind'
import {
  ProjectedDocumentEditorSurface,
  type ProjectedDocumentEditorSurfaceProps,
} from './projected-document-editor-surface'
import type {
  PresentationBadgeTargetInterpreterRegistry,
} from './presentation-badge-target-interpreter'
import { ProjectedStatusBlock } from './projected-activity-state'
import type { PresentationActionGroupOptions } from './presentation-action-group'
import { type ProjectedMetricValue } from './projected-metric-strip'
import { documentDataSourceRoles } from '@cohesive/presentation-contracts'

const emptyMetricValues = {} as const

export interface ProjectedDocumentWorkspaceProps<TProjectionContext> {
  readonly actionGroupOptions?: PresentationActionGroupOptions<TProjectionContext>
  readonly children?: ReactNode
  readonly className?: string
  readonly componentSystem: PresentationComponentSystem
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly fallbackDescription: string
  readonly fallbackTitle: string
  readonly headerContent?: ReactNode
  readonly metadataBadgeInterpreters?: PresentationBadgeTargetInterpreterRegistry
  readonly metadataEntityReferenceRole?: string | null
  readonly metricMessage?: ReactNode
  readonly metricValues?: Readonly<Record<string, ProjectedMetricValue>>
  readonly projectionRenderContext: TProjectionContext
  readonly projectionRenderers: DocumentWorkspaceProjectionRendererRegistry<
    TProjectionContext,
    PresentationComponentSystem,
    PresentationDesignSystem
  >
  readonly renderDocumentViewState?: (content: ReactNode) => ReactNode
  readonly renderUnavailableProjection?: (viewId: string) => ReactNode
  readonly readMetadataBadgeRole?:
    ProjectedDocumentEditorSurfaceProps<TProjectionContext>['readMetadataBadgeRole']
  readonly readMetadataFieldRole?:
    ProjectedDocumentEditorSurfaceProps<TProjectionContext>['readMetadataFieldRole']
  readonly resource: unknown
  readonly runtime: DocumentWorkspaceRuntimeSnapshot
}

/**
 * Projects a semantic document workspace runtime into the standard document
 * editor surface. Route components provide data/actions; this renderer owns
 * view resolution, layout state binding, and projection renderer lookup.
 */
export function ProjectedDocumentWorkspace<TProjectionContext>({
  actionGroupOptions,
  children,
  className,
  componentSystem,
  dataSourceResolver,
  designSystem,
  fallbackDescription,
  fallbackTitle,
  headerContent,
  metadataBadgeInterpreters,
  metadataEntityReferenceRole,
  metricMessage: metricMessageOverride,
  metricValues = emptyMetricValues,
  projectionRenderContext,
  projectionRenderers,
  renderDocumentViewState,
  renderUnavailableProjection = renderDefaultUnavailableProjection,
  readMetadataBadgeRole,
  readMetadataFieldRole,
  resource,
  runtime,
}: ProjectedDocumentWorkspaceProps<TProjectionContext>) {
  const module = usePresentationModule()
  const activeViewId =
    runtime.activeViewId && runtime.projectionViewIds.includes(runtime.activeViewId)
      ? runtime.activeViewId
      : (runtime.projectionViewIds[0] ?? '')
  const metricMessage =
    metricMessageOverride ??
    renderDocumentSummaryMetricMessage(runtime, dataSourceResolver)
  const projectedMetricValues = useMemo(
    () => ({
      ...createDocumentProfileMetricValues(
        runtime.documentProfile.MetricSources ?? [],
        dataSourceResolver,
      ),
      ...metricValues,
    }),
    [dataSourceResolver, metricValues, runtime.documentProfile.MetricSources],
  )

  function renderDocumentView(viewId: string) {
    return (
      renderDocumentWorkspaceProjection(
        runtime,
        projectionRenderers,
        projectionRenderContext,
        viewId,
        { componentSystem, designSystem, module },
      ) ?? renderUnavailableProjection(viewId)
    )
  }

  return (
    <ProjectedDocumentEditorSurface
      actionContext={projectionRenderContext}
      actionGroupOptions={actionGroupOptions}
      activeViewId={activeViewId}
      className={className}
      componentSystem={componentSystem}
      dataSourceResolver={dataSourceResolver}
      designSystem={designSystem}
      documentViewIds={runtime.projectionViewIds}
      fallbackDescription={fallbackDescription}
      fallbackTitle={fallbackTitle}
      fallbackViewIds={runtime.projectionViewIds}
      headerContent={headerContent}
      activeLayoutModeId={runtime.activeLayoutModeId}
      layout={runtime.layout}
      metadataBadgeInterpreters={metadataBadgeInterpreters}
      metadataEntityReferenceRole={metadataEntityReferenceRole}
      metricMessage={metricMessage}
      metricValues={projectedMetricValues}
      onActiveViewIdChange={runtime.setActiveViewId}
      onLayoutChange={runtime.setLayout}
      pageViewId={runtime.pageView.Id}
      projections={runtime.projections}
      renderDocumentView={renderDocumentView}
      renderDocumentViewState={renderDocumentViewState}
      readMetadataBadgeRole={readMetadataBadgeRole}
      readMetadataFieldRole={readMetadataFieldRole}
      resource={resource}
      workspace={runtime.workspace}
      workspaceLayout={runtime.documentProfile.Layout}
    >
      {children}
    </ProjectedDocumentEditorSurface>
  )
}

function renderDefaultUnavailableProjection() {
  return <ProjectedStatusBlock label="This document view is not available." />
}

function createDocumentProfileMetricValues(
  metricSources: readonly DocumentMetricSourceProjection[],
  dataSourceResolver: PresentationDataSourceResolver,
): Readonly<Record<string, ProjectedMetricValue>> {
  return Object.fromEntries(
    metricSources.flatMap((metricSource) => {
      const value = dataSourceResolver.readPath(
        metricSource.Source.DataSourceId,
        metricSource.Source.FieldPath,
      )

      if (value === undefined) {
        return []
      }

      return [
        [
          metricSource.FieldId,
          {
            value: formatDocumentProfileMetricValue(value),
          } satisfies ProjectedMetricValue,
        ],
      ] as const
    }),
  )
}

function formatDocumentProfileMetricValue(value: unknown) {
  if (value === null || value === '') {
    return 'none'
  }

  return String(value)
}

function renderDocumentSummaryMetricMessage(
  runtime: DocumentWorkspaceRuntimeSnapshot,
  dataSourceResolver: PresentationDataSourceResolver,
) {
  const summaryDataSource = findDocumentProfileDataSource(
    runtime.documentProfile,
    documentDataSourceRoles.summary,
  )
  const message = summaryDataSource
    ? dataSourceResolver.readPath(
        summaryDataSource.DataSource.DataSourceId,
        documentSummaryStructuralMessageFieldPath,
      )
    : undefined

  return typeof message === 'string' && message.length > 0 ? (
    <p className="text-xs text-slate-500">{message}</p>
  ) : null
}
