import type {
  ProjectedDocumentResource,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationActionRuntimeBinding,
} from '@cohesivesystems/presentation-core'
import type {
  ProjectedActionStatusSource,
} from '@cohesivesystems/presentation-core'
import {
  useProjectedDocumentProcessPreviewActionRuntime,
  type ProjectedDocumentProcessPreviewActionRuntime,
  type ProjectedDocumentProcessPreviewState,
  type UseProjectedDocumentProcessPreviewActionRuntimeOptions,
} from './projected-document-process-preview-action-runtime'
import {
  projectDocumentProcessPreviewActionRuntimeBindings,
  projectDocumentProcessPreviewActionStatusSources,
  type ProjectDocumentProcessPreviewActionRuntimeBindingsOptions,
} from './projected-document-process-preview-action-runtime-bindings'
import type {
  ProjectedInputFormRuntime,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationProjectionDiagnostic,
} from '@cohesivesystems/presentation-core'
import type {
  ProjectedPromptDocumentPreviewResource,
} from '@cohesivesystems/presentation-core'

export type ProjectedDocumentProcessPreviewCapabilityState<
  TPreviewResponse = unknown,
  TPreviewRequest extends object = object,
  TPreviewValues extends Readonly<Record<string, unknown>> =
    Readonly<Record<string, never>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
> = ProjectedDocumentProcessPreviewState<
  TPreviewResponse,
  TPreviewRequest,
  TPreviewValues,
  TPreviewResource
>

export interface UseProjectedDocumentProcessPreviewCapabilityRuntimeOptions<
  TPreviewRequest extends object,
  TPreviewResponse,
  TStartInput,
  TStartResult,
  TLabel = string,
  TPreviewValues extends Readonly<Record<string, unknown>> =
    Readonly<Record<string, never>>,
  TResource extends ProjectedDocumentResource = ProjectedDocumentResource,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
> extends UseProjectedDocumentProcessPreviewActionRuntimeOptions<
    TPreviewRequest,
    TPreviewResponse,
    TStartInput,
    TStartResult,
    TPreviewValues,
    TResource,
    TPreviewResource
  > {
  readonly hasResource: boolean
  readonly pendingLabels?: ProjectDocumentProcessPreviewActionRuntimeBindingsOptions<
    TLabel,
    TPreviewRequest,
    TPreviewResponse,
    TPreviewValues,
    TPreviewResource
  >['pendingLabels']
}

export interface ProjectedDocumentProcessPreviewCapabilityRuntime<
  TExecuteContext,
  TLabel,
  TPreviewRequest extends object,
  TPreviewResponse,
  TPreviewValues extends Readonly<Record<string, unknown>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource,
> {
  readonly actionBindings: readonly PresentationActionRuntimeBinding<TExecuteContext, TLabel>[]
  readonly actionRuntime: ProjectedDocumentProcessPreviewActionRuntime<
    TPreviewRequest,
    TPreviewResponse,
    TPreviewValues,
    TPreviewResource
  >
  readonly actionStatuses: readonly ProjectedActionStatusSource[]
  readonly activePreview: ProjectedDocumentProcessPreviewState<
    TPreviewResponse,
    TPreviewRequest,
    TPreviewValues,
    TPreviewResource
  > | null
  readonly cancelPreview: () => void
  readonly cancelPrompt: () => void
  readonly diagnosticActionPlacements: ProjectedDocumentProcessPreviewActionRuntime<
    TPreviewRequest,
    TPreviewResponse,
    TPreviewValues,
    TPreviewResource
  >['diagnosticActionPlacements']
  readonly diagnostics: readonly PresentationProjectionDiagnostic[]
  readonly input: {
    readonly runtime: ProjectedInputFormRuntime<TPreviewRequest> | null
  }
  readonly preview: {
    readonly error: unknown
    readonly isPending: boolean
  }
  readonly reset: () => void
  readonly start: {
    readonly error: unknown
    readonly isPending: boolean
  }
  readonly startPreview: () => void
}

/**
 * Projects a document-scoped process-preview action into the full runtime
 * surface expected by document workspace hosts.
 *
 * The lower-level action runtime owns flow state and endpoint execution; this
 * capability wrapper adds action bindings and status sources so product hosts
 * can consume any IR-declared process-preview action without writing a
 * per-process capability wrapper.
 */
export function useProjectedDocumentProcessPreviewCapabilityRuntime<
  TPreviewRequest extends object,
  TPreviewResponse,
  TStartInput,
  TStartResult,
  TExecuteContext = unknown,
  TLabel = string,
  TPreviewValues extends Readonly<Record<string, unknown>> =
    Readonly<Record<string, never>>,
  TResource extends ProjectedDocumentResource = ProjectedDocumentResource,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
>({
  hasResource,
  isActionAvailable = true,
  isPreviewBlocked = false,
  pendingLabels,
  ...runtimeOptions
}: UseProjectedDocumentProcessPreviewCapabilityRuntimeOptions<
  TPreviewRequest,
  TPreviewResponse,
  TStartInput,
  TStartResult,
  TLabel,
  TPreviewValues,
  TResource,
  TPreviewResource
>): ProjectedDocumentProcessPreviewCapabilityRuntime<
  TExecuteContext,
  TLabel,
  TPreviewRequest,
  TPreviewResponse,
  TPreviewValues,
  TPreviewResource
> {
  const actionRuntime = useProjectedDocumentProcessPreviewActionRuntime<
    TPreviewRequest,
    TPreviewResponse,
    TStartInput,
    TStartResult,
    TPreviewValues,
    TResource,
    TPreviewResource
  >({
    ...runtimeOptions,
    isActionAvailable,
    isPreviewBlocked,
  })
  const actionBindings = projectDocumentProcessPreviewActionRuntimeBindings<
    TExecuteContext,
    TLabel,
    TPreviewRequest,
    TPreviewResponse,
    TPreviewValues,
    TPreviewResource
  >({
    hasResource,
    isActionAvailable,
    isPreviewBlocked,
    pendingLabels,
    runtime: actionRuntime,
  })

  return {
    actionBindings,
    actionRuntime,
    actionStatuses: projectDocumentProcessPreviewActionStatusSources(actionRuntime),
    activePreview: actionRuntime.activePreview,
    cancelPreview: actionRuntime.cancelPreview,
    cancelPrompt: actionRuntime.cancelPrompt,
    diagnosticActionPlacements: actionRuntime.diagnosticActionPlacements,
    diagnostics: actionRuntime.diagnostics,
    input: {
      runtime: actionRuntime.input.runtime,
    },
    preview: {
      error: actionRuntime.preview.error,
      isPending: actionRuntime.preview.isPending,
    },
    reset: actionRuntime.reset,
    start: {
      error: actionRuntime.start.error,
      isPending: actionRuntime.start.isPending,
    },
    startPreview: actionRuntime.acceptPreview,
  }
}
