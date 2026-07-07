/* eslint-disable react-refresh/only-export-components */

import {
  useCallback,
  useMemo,
  useState,
  type ReactNode,
} from 'react'

import {
  createDocumentResourceEditorKey,
  createPresentationDataSourceResolver,
  readDocumentResourceDocument,
  readDocumentResourceTitle,
  createQueryActivityState,
  presentationDataSourceAuthorization,
  presentationDataSourceBindings,
  type PresentationDataSourceResolver,
  type PresentationDataSourceState,
  type PresentationDataSourceStateMap,
  type PresentationModuleDefinition,
  type ProjectedDocumentActionStatusMap,
  type DocumentWorkspaceProfileProjection,
  type ProjectedDocumentKind,
  type ProjectedDocumentResource,
} from '@cohesive/presentation-core'
import {
  type DocumentWorkspaceProjectionRendererRegistry,
  type DocumentWorkspaceRuntimeSnapshot,
  createLocalFlowStateDataSourceBindings,
  usePresentationDataSources,
  type LocalFlowStateDataSourceState,
  type PresentationFlowRuntimeRegistrySnapshot,
} from '@cohesive/presentation-react'
import type { PresentationActionGroupOptions } from './presentation-action-group'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type {
  PresentationDesignSystem,
} from '@cohesive/presentation-tailwind'
import {
  ProjectedActivityStateBoundary,
  ProjectedStatusBlock,
} from './projected-activity-state'
import {
  ProjectedDocumentWorkspace,
  type ProjectedDocumentWorkspaceProps,
} from './projected-document-workspace'
import {
  ProjectedPresentationFlowLayer,
  type ProjectedPresentationFlowLayerRenderContext,
} from './projected-presentation-flow-layer'
import {
  createPromptDocumentPreviewDataSourceBindings,
  type PromptDocumentPreviewDataSourceState,
} from '@cohesive/presentation-core'
import type {
  ProjectedPromptChildViewRendererRegistry,
} from './projected-prompt-child-view-renderer'
import type {
  PresentationBadgeTargetInterpreterRegistry,
} from './presentation-badge-target-interpreter'

const emptyPresentationFlowEntries = [] as const

export type ProjectedJsonDocumentValidation =
  | { readonly ok: true; readonly value: unknown }
  | { readonly ok: false }

export interface ProjectedDocumentWorkspaceSummaryValueContext {
  readonly documentKind: ProjectedDocumentKind
  readonly editorText: string
  readonly fieldPaths: readonly string[]
  readonly parsedDocument: unknown | undefined
  readonly validation: ProjectedJsonDocumentValidation
}

export interface UseProjectedDocumentWorkspaceStateOptions<
  TProjection extends DocumentWorkspaceProfileProjection = DocumentWorkspaceProfileProjection,
> {
  readonly createSummaryDataSourceValue?: (
    context: ProjectedDocumentWorkspaceSummaryValueContext,
  ) => unknown
  readonly dataSources: PresentationDataSourceStateMap
  readonly formatDocument?: (value: unknown) => string
  readonly projection: TProjection
  readonly resourceId: string
  readonly validateDocumentText?: (text: string) => ProjectedJsonDocumentValidation
}

export interface ProjectedDocumentWorkspaceState<TMetadata = unknown> {
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly editorPath: string
  readonly editorText: string
  readonly isEditorDirty: boolean
  readonly isMetadataPending: boolean
  readonly jsonValidation: ProjectedJsonDocumentValidation
  readonly metadata: TMetadata | undefined
  readonly metadataError: unknown
  readonly metadataState: PresentationDataSourceState<TMetadata> | undefined
  readonly parsedEditorDocument: unknown | undefined
  readonly persistedEditorText: string
  readonly projectedDataSources: PresentationDataSourceStateMap
  readonly resource: ProjectedDocumentResource | undefined
  readonly resourceKey: string | null
  readonly resourceState: PresentationDataSourceState<ProjectedDocumentResource> | undefined
  readonly resetEditorState: () => void
  readonly setEditorText: (text: string) => void
  readonly title: string
}

export interface ProjectedDocumentWorkspaceShellProps<TProjectionContext> {
  readonly actionGroupOptions?: PresentationActionGroupOptions<TProjectionContext>
  readonly actionStatuses?: ProjectedDocumentActionStatusMap
  readonly actionRegionId?: string
  readonly childViewRegistry?: ProjectedPromptChildViewRendererRegistry<TProjectionContext>
  readonly className?: string
  readonly componentSet?: string
  readonly componentSystem: PresentationComponentSystem
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly fallbackDescription: string
  readonly fallbackTitle: string
  readonly flowRegistry?: PresentationFlowRuntimeRegistrySnapshot
  readonly headerContent?: ReactNode
  readonly localFlowState?: LocalFlowStateDataSourceState | null
  readonly metadataBadgeInterpreters?: PresentationBadgeTargetInterpreterRegistry
  readonly metadataEntityReferenceRole?: string | null
  readonly module: PresentationModuleDefinition | null
  readonly promptDocumentPreviewState?: PromptDocumentPreviewDataSourceState | null
  readonly projectionRenderContext: TProjectionContext
  readonly projectionRenderers: DocumentWorkspaceProjectionRendererRegistry<
    TProjectionContext,
    PresentationComponentSystem,
    PresentationDesignSystem
  >
  readonly renderDocumentViewState?: (content: ReactNode) => ReactNode
  readonly renderUnavailableProjection?: (viewId: string) => ReactNode
  readonly readMetadataBadgeRole?:
    ProjectedDocumentWorkspaceProps<TProjectionContext>['readMetadataBadgeRole']
  readonly readMetadataFieldRole?:
    ProjectedDocumentWorkspaceProps<TProjectionContext>['readMetadataFieldRole']
  readonly resource: unknown
  readonly resourceBlockedLabel?: string
  readonly resourcePendingLabel?: string
  readonly resourceState?: PresentationDataSourceState | null
  readonly runtime: DocumentWorkspaceRuntimeSnapshot
  readonly shouldRenderFlowEntry?: (
    context: ProjectedPresentationFlowLayerRenderContext,
  ) => boolean
}

/**
 * Projects generic document workspace runtime state into the standard document
 * editor surface and prompt layer.
 *
 * Product adapters still provide action runtimes, badge interpreters, prompt
 * child renderers, and projection renderers. This shell owns the reusable
 * mechanics around document resource state, local flow-state data sources, and
 * prompt-preview data sources.
 */
export function ProjectedDocumentWorkspaceShell<TProjectionContext>({
  actionGroupOptions,
  actionStatuses,
  actionRegionId,
  childViewRegistry,
  className,
  componentSet,
  componentSystem,
  dataSourceResolver,
  designSystem,
  fallbackDescription,
  fallbackTitle,
  flowRegistry,
  headerContent,
  localFlowState,
  metadataBadgeInterpreters,
  metadataEntityReferenceRole,
  module,
  promptDocumentPreviewState,
  projectionRenderContext,
  projectionRenderers,
  renderDocumentViewState,
  renderUnavailableProjection = renderDefaultUnavailableProjection,
  readMetadataBadgeRole,
  readMetadataFieldRole,
  resource,
  resourceBlockedLabel = 'Attach an authenticated context to load this document.',
  resourcePendingLabel = 'Loading document...',
  resourceState,
  runtime,
  shouldRenderFlowEntry,
}: ProjectedDocumentWorkspaceShellProps<TProjectionContext>) {
  const activeFlowEntries = useMemo(
    () => flowRegistry?.activeEntries ?? emptyPresentationFlowEntries,
    [flowRegistry?.activeEntries],
  )
  const hasRenderableFlowEntries = useMemo(
    () =>
      activeFlowEntries.some((entry) => {
        if (!entry.view) {
          return false
        }

        return shouldRenderFlowEntry?.({ ...entry, view: entry.view }) !== false
      }),
    [activeFlowEntries, shouldRenderFlowEntry],
  )
  const localFlowStateDataSourceBindings = useMemo(
    () =>
      createLocalFlowStateDataSourceBindings({
        activeEntries: activeFlowEntries,
        authorization: presentationDataSourceAuthorization.none(),
        module,
        state: localFlowState,
      }),
    [activeFlowEntries, localFlowState, module],
  )
  const promptDocumentPreviewDataSourceBindings = useMemo(
    () =>
      createPromptDocumentPreviewDataSourceBindings({
        activeEntries: activeFlowEntries,
        authorization: presentationDataSourceAuthorization.none(),
        module,
        state: promptDocumentPreviewState,
      }),
    [activeFlowEntries, module, promptDocumentPreviewState],
  )
  const localFlowStateDataSources = usePresentationDataSources(localFlowStateDataSourceBindings)
  const promptDocumentPreviewDataSources = usePresentationDataSources(
    promptDocumentPreviewDataSourceBindings,
  )
  const promptDataSourceResolver = useMemo(
    () =>
      createPresentationDataSourceResolver({
        ...dataSourceResolver.dataSources,
        ...localFlowStateDataSources,
        ...promptDocumentPreviewDataSources,
      }),
    [
      dataSourceResolver.dataSources,
      localFlowStateDataSources,
      promptDocumentPreviewDataSources,
    ],
  )
  const renderWorkspaceViewState = renderDocumentViewState ?? ((content: ReactNode) => (
    <ProjectedActivityStateBoundary
      componentSystem={componentSystem}
      state={createQueryActivityState({
        blockedLabel: resourceState?.blockedLabel ?? resourceBlockedLabel,
        error: resourceState?.error,
        isBlocked: Boolean(resourceState?.isBlocked),
        isPending: Boolean(resourceState?.isPending),
        pendingLabel: resourceState?.pendingLabel ?? resourcePendingLabel,
      })}
    >
      {content}
    </ProjectedActivityStateBoundary>
  ))

  return (
    <ProjectedDocumentWorkspace
      actionGroupOptions={actionGroupOptions}
      className={className}
      componentSystem={componentSystem}
      dataSourceResolver={dataSourceResolver}
      designSystem={designSystem}
      fallbackDescription={fallbackDescription}
      fallbackTitle={fallbackTitle}
      headerContent={headerContent}
      metadataBadgeInterpreters={metadataBadgeInterpreters}
      metadataEntityReferenceRole={metadataEntityReferenceRole}
      projectionRenderContext={projectionRenderContext}
      projectionRenderers={projectionRenderers}
      renderDocumentViewState={renderWorkspaceViewState}
      renderUnavailableProjection={renderUnavailableProjection}
      readMetadataBadgeRole={readMetadataBadgeRole}
      readMetadataFieldRole={readMetadataFieldRole}
      resource={resource}
      runtime={runtime}
    >
      {flowRegistry && childViewRegistry && hasRenderableFlowEntries ? (
        <ProjectedPresentationFlowLayer
          actionGroupOptions={actionGroupOptions}
          actionStatuses={actionStatuses}
          actionRegionId={actionRegionId}
          childViewRegistry={childViewRegistry}
          componentSet={componentSet}
          componentSystem={componentSystem}
          context={projectionRenderContext}
          dataSourceResolver={promptDataSourceResolver}
          designSystem={designSystem}
          flowRegistry={flowRegistry}
          module={module}
          shouldRenderEntry={shouldRenderFlowEntry}
        />
      ) : null}
    </ProjectedDocumentWorkspace>
  )
}

/**
 * Resolves a projected document resource and derives local editor state for
 * JSON-document workspaces.
 */
export function useProjectedDocumentWorkspaceState<
  TMetadata = unknown,
  TProjection extends DocumentWorkspaceProfileProjection = DocumentWorkspaceProfileProjection,
>({
  createSummaryDataSourceValue,
  dataSources,
  formatDocument = formatProjectedJsonDocument,
  projection,
  resourceId,
  validateDocumentText = validateProjectedJsonDocument,
}: UseProjectedDocumentWorkspaceStateOptions<TProjection>): ProjectedDocumentWorkspaceState<TMetadata> {
  const [editorState, setEditorState] = useState<{
    readonly resourceKey: string
    readonly text: string
  } | null>(null)
  const boundDataSourceResolver = useMemo(
    () => createPresentationDataSourceResolver(dataSources),
    [dataSources],
  )
  const resourceState = boundDataSourceResolver.resolve<ProjectedDocumentResource>(
    projection.dataSourceId,
  )
  const metadataState = projection.metadata
    ? boundDataSourceResolver.resolve<TMetadata>(projection.metadata.dataSourceId)
    : undefined
  const resource = resourceState?.data
  const resourceKey = resource ? createDocumentResourceEditorKey(projection, resource) : null
  const persistedEditorText = resource
    ? formatDocument(readDocumentResourceDocument(projection, resource))
    : ''
  const editorText =
    resource && editorState?.resourceKey === resourceKey
      ? editorState.text
      : resource
        ? persistedEditorText
        : ''
  const isEditorDirty = Boolean(resource && editorText !== persistedEditorText)
  const jsonValidation = validateDocumentText(editorText)
  const parsedEditorDocument = jsonValidation.ok ? jsonValidation.value : undefined
  const title = resource ? readDocumentResourceTitle(projection, resource) : projection.label
  const editorPath = projection.editorPath(resourceId)
  const summaryDataSourceBindings = useMemo(
    () =>
      resource && projection.summary && createSummaryDataSourceValue
        ? [
            presentationDataSourceBindings.localValue({
              authorization: presentationDataSourceAuthorization.none(),
              data: createSummaryDataSourceValue({
                documentKind: projection.documentKind,
                editorText,
                fieldPaths: projection.summary.fieldPaths,
                parsedDocument: parsedEditorDocument,
                validation: jsonValidation,
              }),
              dataSourceId: projection.summary.dataSourceId,
            }),
          ]
        : [],
    [
      createSummaryDataSourceValue,
      editorText,
      jsonValidation,
      parsedEditorDocument,
      projection.documentKind,
      projection.summary,
      resource,
    ],
  )
  const summaryDataSources = usePresentationDataSources(summaryDataSourceBindings)
  const projectedDataSources = useMemo(
    () => ({ ...dataSources, ...summaryDataSources }),
    [dataSources, summaryDataSources],
  )
  const dataSourceResolver = useMemo(
    () => createPresentationDataSourceResolver(projectedDataSources),
    [projectedDataSources],
  )
  const resetEditorState = useCallback(() => setEditorState(null), [])
  const setEditorText = useCallback((text: string) => {
    if (resourceKey) {
      setEditorState({ resourceKey, text })
    }
  }, [resourceKey])

  return {
    dataSourceResolver,
    editorPath,
    editorText,
    isEditorDirty,
    isMetadataPending: Boolean(metadataState?.isPending && !metadataState.data),
    jsonValidation,
    metadata: metadataState?.data,
    metadataError: metadataState?.error,
    metadataState,
    parsedEditorDocument,
    persistedEditorText,
    projectedDataSources,
    resource,
    resourceKey,
    resourceState,
    resetEditorState,
    setEditorText,
    title,
  }
}

function renderDefaultUnavailableProjection() {
  return <ProjectedStatusBlock label="This document view is not available." />
}

function formatProjectedJsonDocument(value: unknown) {
  return JSON.stringify(value ?? null, null, 2)
}

function validateProjectedJsonDocument(value: string): ProjectedJsonDocumentValidation {
  try {
    return { ok: true, value: JSON.parse(value) }
  } catch {
    return { ok: false }
  }
}
