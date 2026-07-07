import type {
  PresentationActionRuntimeBinding,
} from '@cohesive/presentation-core'
import {
  createPresentationActionRuntimeBinding,
} from '@cohesive/presentation-core'
import {
  resolveActionPendingLabel,
} from '@cohesive/presentation-core'
import type {
  ProjectedDocumentActionRuntime,
  ProjectedDocumentJsonValidationState,
} from './projected-document-action-runtime'
import {
  actionSemanticsKinds,
  documentWorkspaceActionKinds,
  localDocumentEditorActionKinds,
} from '@cohesive/presentation-contracts'

export interface ProjectDocumentActionRuntimeBindingsOptions<
  TLabel = string,
> {
  readonly formatDocument?: () => void
  readonly hasResource: boolean
  readonly isEditorDirty: boolean
  readonly jsonValidation: ProjectedDocumentJsonValidationState
  readonly pendingLabels?: {
    readonly save?: TLabel
  }
  readonly resetDocument?: () => void
  readonly runtime: ProjectedDocumentActionRuntime
}

/**
 * Projects generic document action runtime state into adapter-neutral action
 * bindings. The runtime owns the semantic document mechanics; UI adapters own
 * how these bindings become concrete controls.
 */
export function projectDocumentActionRuntimeBindings<
  TExecuteContext = unknown,
  TLabel = string,
>({
  formatDocument,
  hasResource,
  isEditorDirty,
  jsonValidation,
  pendingLabels,
  resetDocument,
  runtime,
}: ProjectDocumentActionRuntimeBindingsOptions<
  TLabel
>): readonly PresentationActionRuntimeBinding<TExecuteContext, TLabel>[] {
  const savePendingLabel = resolveActionPendingLabel({
    action: runtime.actions.save,
    fallback: pendingLabels?.save ?? ('Saving' as TLabel),
  })
  const saveCommitPendingLabel = resolveActionPendingLabel({
    action: runtime.actions.saveCommit,
    fallback: savePendingLabel,
  })

  return [
    createPresentationActionRuntimeBinding<TExecuteContext, TLabel>({
      documentWorkspaceKind: documentWorkspaceActionKinds.saveReview,
      id: 'document-save-preview-flow',
      semanticsKind: actionSemanticsKinds.documentWorkspace,
      project: () => ({
        execute: runtime.requestSaveReview,
        isDisabled:
          !isEditorDirty ||
          !jsonValidation.ok ||
          !runtime.save.endpointId ||
          runtime.save.isPending ||
          runtime.isSaveReviewOpen,
        isHidden: !hasResource,
        isPending: runtime.save.isPending,
        pendingLabel: savePendingLabel,
      }),
    }),
    createPresentationActionRuntimeBinding<TExecuteContext, TLabel>({
      documentWorkspaceKind: documentWorkspaceActionKinds.saveCommit,
      id: 'accept-save-review',
      semanticsKind: actionSemanticsKinds.documentWorkspace,
      project: () => ({
        execute: runtime.acceptSaveReview,
        isDisabled: runtime.save.isPending,
        isHidden: !runtime.isSaveReviewOpen,
        isPending: runtime.save.isPending,
        pendingLabel: saveCommitPendingLabel,
      }),
    }),
    createPresentationActionRuntimeBinding<TExecuteContext, TLabel>({
      documentWorkspaceKind: documentWorkspaceActionKinds.saveCancel,
      id: 'cancel-save-review',
      semanticsKind: actionSemanticsKinds.documentWorkspace,
      project: () => ({
        execute: runtime.cancelSaveReview,
        isDisabled: runtime.save.isPending,
        isHidden: !runtime.isSaveReviewOpen,
      }),
    }),
    createPresentationActionRuntimeBinding<TExecuteContext, TLabel>({
      documentWorkspaceKind: documentWorkspaceActionKinds.saveRevert,
      id: 'revert-save-review',
      semanticsKind: actionSemanticsKinds.documentWorkspace,
      project: () => ({
        execute: runtime.resetSaveReview,
        isDisabled: runtime.save.isPending,
        isHidden: !runtime.isSaveReviewOpen,
      }),
    }),
    createPresentationActionRuntimeBinding<TExecuteContext, TLabel>({
      id: 'reset-json-document',
      localDocumentEditorKind: localDocumentEditorActionKinds.reset,
      semanticsKind: actionSemanticsKinds.localDocumentEditor,
      project: () => ({
        execute: resetDocument ?? runtime.resetDocument,
        isHidden: !hasResource,
      }),
    }),
    createPresentationActionRuntimeBinding<TExecuteContext, TLabel>({
      id: 'format-json-document',
      localDocumentEditorKind: localDocumentEditorActionKinds.format,
      semanticsKind: actionSemanticsKinds.localDocumentEditor,
      project: () => ({
        execute: formatDocument ?? runtime.formatDocument,
        isDisabled: !jsonValidation.ok,
        isHidden: !hasResource,
      }),
    }),
  ]
}
