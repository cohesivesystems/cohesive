import type { ReactNode } from 'react'

import {
  DocumentWorkspaceRuntime,
  renderDocumentWorkspaceProjection,
  type DocumentWorkspaceProjectionRendererRegistry,
  usePresentationModule,
} from '@cohesive/presentation-react'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type {
  PresentationDesignSystem,
} from '@cohesive/presentation-tailwind'
import {
  type PromptDocumentPreviewDefinition,
  resolvePresentationTemplate,
  resolvePresentationBadges,
  resolvePromptDocumentPreviewTitle,
} from '@cohesive/presentation-core'
import {
  ProjectedPresentationBadge,
  ProjectedPresentationBadges,
  type ProjectedPresentationBadgeItem,
} from './projected-presentation-badges'
import { ProjectedStatusBlock } from './projected-activity-state'
import { ProjectedTabsView } from './projected-tabs-view'

export interface ProjectedPromptDocumentPreviewBadge {
  readonly className?: string
  readonly id?: string
  readonly label: ReactNode
  readonly tone?: string | null
}

export interface ProjectedPromptDocumentPreviewProps<TProjectionContext> {
  readonly badges?: readonly ProjectedPromptDocumentPreviewBadge[]
  readonly className?: string
  readonly componentSystem: PresentationComponentSystem
  readonly definition?: PromptDocumentPreviewDefinition | null
  readonly designSystem: PresentationDesignSystem
  readonly documentInstanceId?: string | null
  readonly error?: string | null
  readonly fallback?: ReactNode
  readonly path?: ReactNode
  readonly previewData?: unknown
  readonly previewViewId?: string | null
  readonly projectionRenderContext: TProjectionContext
  readonly projectionRenderers: DocumentWorkspaceProjectionRendererRegistry<
    TProjectionContext,
    PresentationComponentSystem,
    PresentationDesignSystem
  >
  readonly renderUnavailableProjection?: (viewId: string) => ReactNode
  readonly statusClassName?: string
  readonly statusLabel?: ReactNode
  readonly title?: ReactNode
  readonly viewId: string
  readonly workspacePageViewId?: string | null
}

/**
 * Projects a transient document preview inside a prompt. The caller supplies
 * the document render context while this component owns workspace resolution,
 * child projection rendering, tab chrome, and unavailable-view fallback.
 */
export function ProjectedPromptDocumentPreview<TProjectionContext>({
  badges = [],
  className,
  componentSystem,
  definition,
  designSystem,
  documentInstanceId,
  error,
  fallback = <ProjectedStatusBlock label="Document preview workspace is not available." />,
  path,
  previewData,
  previewViewId,
  projectionRenderContext,
  projectionRenderers,
  renderUnavailableProjection = renderDefaultUnavailableProjection,
  statusClassName,
  statusLabel,
  title,
  viewId,
  workspacePageViewId,
}: ProjectedPromptDocumentPreviewProps<TProjectionContext>) {
  const module = usePresentationModule()
  const resolvedPreviewViewId = definition?.PreviewViewId ?? previewViewId
  const resolvedWorkspacePageViewId =
    definition?.WorkspacePageViewId ?? workspacePageViewId
  const resolvedDocumentInstanceId =
    documentInstanceId ??
    resolvePresentationTemplate(definition?.DocumentInstanceIdTemplate, previewData) ??
    definition?.DataSourceId ??
    viewId
  const resolvedTitle =
    title ??
    resolvePromptDocumentPreviewTitle(definition, previewData)
  const resolvedPath =
    path ??
    resolvePresentationTemplate(definition?.DocumentPathTemplate, previewData) ??
    null
  const resolvedBadges = [
    ...resolvePresentationBadges(definition?.Badges, previewData, module),
    ...badges,
  ] satisfies readonly ProjectedPresentationBadgeItem[]

  if (!resolvedPreviewViewId || !resolvedWorkspacePageViewId) {
    return <>{fallback}</>
  }

  return (
    <DocumentWorkspaceRuntime
      documentInstanceId={resolvedDocumentInstanceId}
      fallback={fallback}
      pageViewId={resolvedWorkspacePageViewId}
    >
      {(runtime) => {
        const renderPreviewView = (childViewId: string) =>
          renderDocumentWorkspaceProjection(
            runtime,
            projectionRenderers,
            projectionRenderContext,
            childViewId,
            { componentSystem, designSystem, module },
          ) ?? renderUnavailableProjection(childViewId)

        if (viewId !== resolvedPreviewViewId) {
          return renderPreviewView(viewId)
        }

        return (
          <section
            className={cn(
              'flex min-h-0 flex-1 flex-col gap-3 overflow-hidden rounded-lg border border-slate-950/10 bg-slate-50/80 p-3',
              className,
            )}
          >
            <header className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0">
                <div className="flex min-w-0 flex-wrap items-center gap-2">
                  <h3 className="text-sm font-semibold text-slate-950">
                    {resolvedTitle}
                  </h3>
                  <ProjectedPresentationBadges
                    badges={resolvedBadges}
                    className="contents"
                    componentSystem={componentSystem}
                    designSystem={designSystem}
                    variant="outline"
                  />
                </div>
                {resolvedPath ? (
                  <p className="mt-1 break-all text-xs text-slate-500">
                    {resolvedPath}
                  </p>
                ) : null}
              </div>
              {statusLabel ? (
                <ProjectedPresentationBadge
                  badge={{
                    className: statusClassName,
                    label: statusLabel,
                    tone: 'info',
                  }}
                  componentSystem={componentSystem}
                  designSystem={designSystem}
                  variant="outline"
                />
              ) : null}
            </header>

            {error ? (
              <ProjectedStatusBlock label={error} tone="error" />
            ) : null}

            <ProjectedTabsView
              className="min-h-0 flex-1 overflow-hidden"
              componentSystem={componentSystem}
              renderView={renderPreviewView}
              viewId={resolvedPreviewViewId}
            />
          </section>
        )
      }}
    </DocumentWorkspaceRuntime>
  )
}

function renderDefaultUnavailableProjection(viewId: string) {
  return <ProjectedStatusBlock label={`Document preview view '${viewId}' is not available.`} />
}

function cn(...values: readonly (false | null | string | undefined)[]) {
  return values.filter(Boolean).join(' ')
}
