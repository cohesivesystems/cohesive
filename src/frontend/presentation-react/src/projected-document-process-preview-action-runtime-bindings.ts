import type {
  PresentationActionRuntimeBinding,
} from '@cohesivesystems/presentation-core'
import {
  createPresentationActionRuntimeBinding,
} from '@cohesivesystems/presentation-core'
import {
  resolveActionPendingLabel,
} from '@cohesivesystems/presentation-core'
import type {
  ProjectedActionStatusSource,
} from '@cohesivesystems/presentation-core'
import type {
  ProjectedDocumentProcessPreviewActionRuntime,
} from './projected-document-process-preview-action-runtime'
import type {
  ProjectedPromptDocumentPreviewResource,
} from '@cohesivesystems/presentation-core'
import {
  actionSemanticsKinds,
  documentWorkspaceActionKinds,
} from '@cohesivesystems/presentation-contracts'

export interface ProjectDocumentProcessPreviewActionRuntimeBindingsOptions<
  TLabel = string,
  TPreviewRequest extends object = object,
  TPreviewResponse = unknown,
  TPreviewValues extends Readonly<Record<string, unknown>> =
    Readonly<Record<string, never>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
> {
  readonly hasResource: boolean
  readonly isActionAvailable: boolean
  readonly isPreviewBlocked?: boolean
  readonly pendingLabels?: {
    readonly preview?: TLabel
    readonly start?: TLabel | ((
      runtime: ProjectedDocumentProcessPreviewActionRuntime<
        TPreviewRequest,
        TPreviewResponse,
        TPreviewValues,
        TPreviewResource
      >,
    ) => TLabel)
  }
  readonly runtime: ProjectedDocumentProcessPreviewActionRuntime<
    TPreviewRequest,
    TPreviewResponse,
    TPreviewValues,
    TPreviewResource
  >
}

/**
 * Projects document process-preview runtime state into adapter-neutral action
 * bindings. Product code can still provide presentation labels and additional
 * blocking policy while the runtime owns the IR flow mechanics.
 */
export function projectDocumentProcessPreviewActionRuntimeBindings<
  TExecuteContext = unknown,
  TLabel = string,
  TPreviewRequest extends object = object,
  TPreviewResponse = unknown,
  TPreviewValues extends Readonly<Record<string, unknown>> =
    Readonly<Record<string, never>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
>({
  hasResource,
  isActionAvailable,
  isPreviewBlocked = false,
  pendingLabels,
  runtime,
}: ProjectDocumentProcessPreviewActionRuntimeBindingsOptions<
  TLabel,
  TPreviewRequest,
  TPreviewResponse,
  TPreviewValues,
  TPreviewResource
>): readonly PresentationActionRuntimeBinding<TExecuteContext, TLabel>[] {
  const previewPendingLabel = resolveActionPendingLabel({
    action: runtime.actions.preview,
    data: runtime.activePreview,
    fallback: pendingLabels?.preview ?? ('Processing' as TLabel),
  })
  const isPreviewDisabled =
    isPreviewBlocked ||
    runtime.actionEnablement.preview.isDisabled
  const previewDisabledReason =
    runtime.disabledReasons.preview ??
    runtime.actionEnablement.preview.message ??
    runtime.processTask.message ??
    null
  const startDisabledReason =
    runtime.disabledReasons.start ??
    runtime.actionEnablement.start.message ??
    runtime.processTask.message ??
    null

  return [
    createPresentationActionRuntimeBinding<TExecuteContext, TLabel>({
      documentWorkspaceKind: documentWorkspaceActionKinds.processPreview,
      id: 'document-process-preview-flow',
      semanticsKind: actionSemanticsKinds.documentWorkspace,
      project: () => ({
        disabledReason: previewDisabledReason
          ? previewDisabledReason as TLabel
          : undefined,
        execute: () => runtime.requestPreview(),
        isDisabled:
          !runtime.canExecuteAction ||
          isPreviewDisabled ||
          runtime.preview.isPending ||
          runtime.start.isPending ||
          runtime.processTask.isDisabled,
        isHidden: !(runtime.hasAction && isActionAvailable && hasResource),
        isPending:
          runtime.preview.isPending ||
          runtime.start.isPending ||
          Boolean(runtime.processTask.activeTask),
        pendingLabel: previewPendingLabel,
      }),
    }),
    createPresentationActionRuntimeBinding<TExecuteContext, TLabel>({
      documentWorkspaceKind: documentWorkspaceActionKinds.processStart,
      id: 'accept-document-process-preview',
      semanticsKind: actionSemanticsKinds.documentWorkspace,
      project: () => ({
        disabledReason: startDisabledReason
          ? startDisabledReason as TLabel
          : undefined,
        execute: runtime.acceptPreview,
        isDisabled:
          !runtime.canAcceptPreview ||
          runtime.start.isPending ||
          runtime.actionEnablement.start.isDisabled ||
          Boolean(runtime.processTask.activeTask),
        isHidden: !runtime.activePreview,
        isPending:
          runtime.start.isPending ||
          Boolean(runtime.processTask.activeTask),
        pendingLabel: resolveActionPendingLabel({
          action: runtime.actions.start,
          data: runtime.activePreview,
          fallback: resolveStartPendingLabel(runtime, pendingLabels?.start),
        }),
      }),
    }),
    createPresentationActionRuntimeBinding<TExecuteContext, TLabel>({
      documentWorkspaceKind: documentWorkspaceActionKinds.processCancel,
      id: 'cancel-document-process-preview',
      semanticsKind: actionSemanticsKinds.documentWorkspace,
      project: () => ({
        execute: runtime.activePreview ? runtime.cancelPreview : runtime.cancelPrompt,
        isDisabled: runtime.start.isPending,
        isHidden: !(runtime.isPromptOpen || runtime.activePreview),
      }),
    }),
  ]
}

/**
 * Projects a document process-preview runtime into action status sources used
 * by document-profile action status notices.
 */
export function projectDocumentProcessPreviewActionStatusSources<
  TPreviewRequest extends object,
  TPreviewResponse,
  TPreviewValues extends Readonly<Record<string, unknown>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource,
>(
  runtime: ProjectedDocumentProcessPreviewActionRuntime<
    TPreviewRequest,
    TPreviewResponse,
    TPreviewValues,
    TPreviewResource
  >,
): readonly ProjectedActionStatusSource[] {
  return [
    {
      action: runtime.actions.preview,
      error: runtime.preview.error,
      isPending: runtime.preview.isPending,
    },
    {
      action: runtime.start.action,
      error: runtime.start.error,
      isPending: runtime.start.isPending,
    },
  ]
}

function resolveStartPendingLabel<
  TLabel,
  TPreviewRequest extends object,
  TPreviewResponse,
  TPreviewValues extends Readonly<Record<string, unknown>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource,
>(
  runtime: ProjectedDocumentProcessPreviewActionRuntime<
    TPreviewRequest,
    TPreviewResponse,
    TPreviewValues,
    TPreviewResource
  >,
  label:
    | TLabel
    | ((
      runtime: ProjectedDocumentProcessPreviewActionRuntime<
        TPreviewRequest,
        TPreviewResponse,
        TPreviewValues,
        TPreviewResource
      >,
    ) => TLabel)
    | undefined,
) {
  if (typeof label === 'function') {
    return (label as (
      runtime: ProjectedDocumentProcessPreviewActionRuntime<
        TPreviewRequest,
        TPreviewResponse,
        TPreviewValues,
        TPreviewResource
      >,
    ) => TLabel)(runtime)
  }

  return label
}
