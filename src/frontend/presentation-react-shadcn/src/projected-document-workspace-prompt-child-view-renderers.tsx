import type {
  DocumentWorkspaceProjectionRendererRegistry,
} from '@cohesive/presentation-react'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type {
  PresentationDesignSystem,
} from '@cohesive/presentation-tailwind'
import {
  findPromptDocumentPreviewRegionId,
  findPromptDocumentPreviewStatusRegionId,
  getErrorMessage,
  readObjectProperty,
  resolvePromptDocumentPreviewPath,
  resolvePromptDocumentPreviewText,
  resolvePromptStatusMessages,
  resolvePromptStatusMessageText,
  type PresentationDataSourceResolver,
  type ProjectedDocumentActionStatusMap,
  type ProjectedPromptDocumentPreviewData,
  type ViewDefinition,
} from '@cohesive/presentation-core'
import {
  ProjectedStatusBlock,
} from './projected-activity-state'
import {
  ProjectedPromptDocumentPreview,
} from './projected-prompt-document-preview'

export interface ProjectedDocumentWorkspacePromptPreviewRenderContextOptions {
  readonly editorPath: string
  readonly editorText: string
  readonly isReadOnly: boolean
  readonly previewData: ProjectedPromptDocumentPreviewData
  readonly view: ViewDefinition
}

interface ProjectedJsonDocumentDiffProjectionProps {
  readonly componentSystem: PresentationComponentSystem
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly view: ViewDefinition
}

interface JsonDocumentDiffReviewData {
  readonly error?: string | null
  readonly modified?: string | null
  readonly original?: string | null
  readonly path?: string | null
  readonly title?: string | null
}

interface ProjectedDocumentWorkspacePromptPreviewProjectionProps<TContext> {
  readonly actionStatuses: ProjectedDocumentActionStatusMap
  readonly componentSystem: PresentationComponentSystem
  readonly createProjectionRenderContext: (
    context: ProjectedDocumentWorkspacePromptPreviewRenderContextOptions,
  ) => TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly projectionRenderers: DocumentWorkspaceProjectionRendererRegistry<
    TContext,
    PresentationComponentSystem,
    PresentationDesignSystem
  >
  readonly promptView: ViewDefinition
  readonly statusRegionId?: string | null
  readonly view: ViewDefinition
  readonly viewId: string
}

export function ProjectedJsonDocumentDiffProjection({
  componentSystem,
  dataSourceResolver,
  view,
}: ProjectedJsonDocumentDiffProjectionProps) {
  const state = dataSourceResolver.resolveViewPrimary<JsonDocumentDiffReviewData>(view)
  const data = state?.data
  if (!data || typeof data !== 'object') {
    return <ProjectedStatusBlock label="JSON diff data is not available." />
  }

  return componentSystem.documentWorkspaces.JsonDocumentDiff({
    error: readString(data, 'error') ?? (state.error ? getErrorMessage(state.error) : null),
    modified: readString(data, 'modified') ?? '',
    original: readString(data, 'original') ?? '',
    path: readString(data, 'path') ?? 'document.json',
    title: readString(data, 'title') ?? view.Name,
    viewId: view.Id,
  })
}

export function ProjectedDocumentWorkspacePromptPreviewProjection<TContext>({
  actionStatuses,
  createProjectionRenderContext,
  componentSystem,
  dataSourceResolver,
  designSystem,
  projectionRenderers,
  promptView,
  statusRegionId,
  view,
  viewId,
}: ProjectedDocumentWorkspacePromptPreviewProjectionProps<TContext>) {
  const previewDefinition = view.PromptDocumentPreview
  const previewRegionId = findPromptDocumentPreviewRegionId(promptView, view.Id) ?? view.Id
  const previewMessages = resolvePromptStatusMessages({
    actionStatuses,
    dataSourceResolver,
    region: previewRegionId,
    view: promptView,
  })
  const previewState = resolvePromptDocumentPreviewState({
    dataSourceResolver,
    view,
  })
  const preview = readPromptDocumentPreviewData({
    state: previewState,
  })

  if (!preview) {
    if (previewState?.error) {
      return (
        <ProjectedStatusBlock
          label={getErrorMessage(previewState.error)}
          tone="error"
        />
      )
    }

    const pendingMessage = previewMessages[0]
    return pendingMessage ? (
      <ProjectedStatusBlock
        label={pendingMessage.label}
        tone={pendingMessage.tone}
      />
    ) : null
  }

  const previewError = previewMessages.find((message) => message.tone === 'error')?.label ?? null
  const resolvedStatusRegionId =
    statusRegionId ?? findPromptDocumentPreviewStatusRegionId(promptView, view.Id)
  const statusLabel = resolvedStatusRegionId
    ? resolvePromptStatusMessageText({
        actionStatuses,
        dataSourceResolver,
        region: resolvedStatusRegionId,
        view: promptView,
      })
    : null
  const previewText = resolvePromptDocumentPreviewText(previewDefinition, preview)
  const previewRenderContext = createProjectionRenderContext({
    editorPath: resolvePromptDocumentPreviewPath(previewDefinition, preview),
    editorText: previewText,
    isReadOnly: previewDefinition?.IsReadOnly ?? true,
    previewData: preview,
    view,
  })

  return (
    <ProjectedPromptDocumentPreview
      componentSystem={componentSystem}
      designSystem={designSystem}
      definition={previewDefinition}
      error={previewError}
      fallback={<ProjectedStatusBlock label="Document preview workspace is not available." />}
      previewData={preview}
      projectionRenderContext={previewRenderContext}
      projectionRenderers={projectionRenderers}
      renderUnavailableProjection={() => (
        <ProjectedStatusBlock label="This preview view is not available." />
      )}
      statusLabel={statusLabel ?? undefined}
      viewId={viewId}
    />
  )
}

function readPromptDocumentPreviewData({
  state,
}: {
  readonly state: ReturnType<typeof resolvePromptDocumentPreviewState>
}) {
  return state?.data ?? null
}

function resolvePromptDocumentPreviewState({
  dataSourceResolver,
  view,
}: {
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly view: ViewDefinition
}) {
  const dataSourceId = view.PromptDocumentPreview?.DataSourceId
  if (dataSourceId) {
    return dataSourceResolver.resolve<ProjectedPromptDocumentPreviewData>(dataSourceId)
  }

  return dataSourceResolver.resolveViewPrimary<ProjectedPromptDocumentPreviewData>(view)
}

function readString(source: object, key: string) {
  const value = readObjectProperty(source, key)
  return typeof value === 'string' ? value : null
}
