import { useMemo } from 'react'

import type {
  ProjectedDocumentResource,
} from '@cohesive/presentation-core'
import type {
  ActionDefinition,
  ActionPlacementDefinition,
  FlowDefinition,
  PresentationModuleDefinition,
  ViewDefinition,
} from '@cohesive/presentation-core'
import {
  findPresentationFlow,
  findPresentationView,
} from '@cohesive/presentation-core'
import {
  getPresentationViewProjectedActionPlacements,
} from '@cohesive/presentation-core'
import type {
  PresentationActionEndpointExecutor,
  PresentationActionSuccessContext,
} from './presentation-action-runtime'
import {
  usePresentationActionExecutor,
} from './presentation-action-runtime'
import {
  projectRequiredPresentationActionEndpointRequest,
} from '@cohesive/presentation-core'
import {
  advancePresentationFlowToStateKind,
  findPresentationFlowErrorSurfaceState,
  findPresentationFlowSurfaceState,
  isPresentationFlowSurfaceOpenForData,
  resolvePresentationActionPreparedFlow,
  usePresentationFlowRuntimeRegistry,
  type PresentationFlowRuntimeRegistrySnapshot,
  type PresentationFlowRuntimeSnapshot,
} from './presentation-flow-runtime'
import {
  type PresentationProjectionDiagnostic,
} from '@cohesive/presentation-core'
import {
  findDocumentSaveAction,
  findLocalDocumentEditorAction,
  findPromptCommitAction,
  findPromptDismissAction,
  findPromptLocalAction,
} from '@cohesive/presentation-core'
import {
  projectLocalDocumentEditorActionBindingDiagnostics,
} from '@cohesive/presentation-core'
import { flowStateKinds } from '@cohesive/presentation-contracts'

export interface ProjectedDocumentJsonValidationState {
  readonly ok: boolean
  readonly value?: unknown
}

export interface UseProjectedDocumentActionRuntimeOptions<
  TResource extends ProjectedDocumentResource = ProjectedDocumentResource,
> {
  readonly actionPlacements: readonly ActionPlacementDefinition[]
  readonly additionalDiagnosticActionPlacements?: readonly ActionPlacementDefinition[]
  readonly dataSourceId: string
  readonly dataSourceQueryKey?: (dataSourceId: string) => readonly unknown[]
  readonly executeEndpoint: PresentationActionEndpointExecutor
  readonly formatDocument?: (value: unknown) => string
  readonly jsonValidation: ProjectedDocumentJsonValidationState
  readonly module: PresentationModuleDefinition | null
  readonly onResetEditorState: () => void
  readonly onSetEditorText: (text: string) => void
  readonly resource: TResource | undefined
  readonly resourceId: string
  readonly resourceKey: string | null
  readonly setSaveResultQueryData?: (
    context: PresentationActionSuccessContext<void, TResource>,
  ) => void
}

export interface ProjectedDocumentActionRuntime {
  readonly actions: {
    readonly formatDocument: ActionDefinition | null
    readonly resetDocument: ActionDefinition | null
    readonly save: ActionDefinition | null
    readonly saveCommit: ActionDefinition | null
    readonly saveDismiss: ActionDefinition | null
    readonly saveRevert: ActionDefinition | null
  }
  readonly actionIds: {
    readonly formatDocument: string
    readonly resetDocument: string
    readonly save: string
    readonly saveCommit: string
    readonly saveDismiss: string
    readonly saveRevert: string
  }
  readonly acceptSaveReview: () => void
  readonly cancelSaveReview: () => void
  readonly diagnosticActionPlacements: readonly ActionPlacementDefinition[]
  readonly diagnostics: readonly PresentationProjectionDiagnostic[]
  readonly flowRegistry: PresentationFlowRuntimeRegistrySnapshot
  readonly formatDocument: () => void
  readonly isSaveReviewOpen: boolean
  readonly promptView: ViewDefinition | null
  readonly resetDocument: () => void
  readonly resetSaveReview: () => void
  readonly resetTransientState: () => void
  readonly requestSaveReview: () => void
  readonly save: {
    readonly action: ActionDefinition | null
    readonly endpointId: string | null
    readonly error: unknown
    readonly isPending: boolean
    readonly reset: () => void
  }
  readonly saveFlow: FlowDefinition | null
  readonly saveFlowId: string
  readonly saveFlowRuntime: PresentationFlowRuntimeSnapshot
}

/**
 * Generic runtime for document-local actions declared in the presentation IR.
 *
 * It owns save review flow mechanics and local editor actions, while callers
 * still supply endpoint execution, action rendering, and product-specific
 * extension actions.
 */
export function useProjectedDocumentActionRuntime<
  TResource extends ProjectedDocumentResource = ProjectedDocumentResource,
>({
  actionPlacements,
  additionalDiagnosticActionPlacements = emptyActionPlacements,
  dataSourceId,
  dataSourceQueryKey,
  executeEndpoint,
  formatDocument: formatDocumentValue = formatJsonDocumentValue,
  jsonValidation,
  module,
  onResetEditorState,
  onSetEditorText,
  resource,
  resourceId,
  resourceKey,
  setSaveResultQueryData,
}: UseProjectedDocumentActionRuntimeOptions<TResource>): ProjectedDocumentActionRuntime {
  const flowRegistry = usePresentationFlowRuntimeRegistry({ module })
  const saveAction = useMemo(
    () =>
      findDocumentSaveAction({
        actionPlacements,
        dataSourceId,
        module,
      }),
    [actionPlacements, dataSourceId, module],
  )
  const saveActionFlow = useMemo(
    () => resolvePresentationActionPreparedFlow(saveAction),
    [saveAction],
  )
  const saveFlowId = saveActionFlow?.flowId ?? ''
  const saveFlow = useMemo(
    () => findPresentationFlow<FlowDefinition>(module, saveFlowId),
    [module, saveFlowId],
  )
  const savePromptView = useMemo(() => {
    const promptViewId =
      saveActionFlow?.promptViewId ??
      findPresentationFlowSurfaceState(saveFlow)?.ViewId

    return promptViewId
      ? findPresentationView<ViewDefinition>(module, promptViewId)
      : null
  }, [module, saveActionFlow?.promptViewId, saveFlow])
  const saveCommitAction = useMemo(
    () => findPromptCommitAction({ module, view: savePromptView }),
    [module, savePromptView],
  )
  const saveDismissAction = useMemo(
    () => findPromptDismissAction({ module, view: savePromptView }),
    [module, savePromptView],
  )
  const saveRevertAction = useMemo(
    () => findPromptLocalAction({ intent: 'revert', module, view: savePromptView }),
    [module, savePromptView],
  )
  const resetDocumentAction = useMemo(
    () => findLocalDocumentEditorAction({ actionPlacements, intent: 'reset', module }),
    [actionPlacements, module],
  )
  const formatDocumentAction = useMemo(
    () => findLocalDocumentEditorAction({ actionPlacements, intent: 'format', module }),
    [actionPlacements, module],
  )
  const diagnosticActionPlacements = useMemo(
    () =>
      mergeActionPlacements(
        actionPlacements,
        getPresentationViewProjectedActionPlacements(savePromptView),
        additionalDiagnosticActionPlacements,
      ),
    [actionPlacements, additionalDiagnosticActionPlacements, savePromptView],
  )
  const diagnostics = useMemo(
    () =>
      projectLocalDocumentEditorActionBindingDiagnostics({
        actionPlacements: diagnosticActionPlacements,
        module,
        supportedIntents: supportedLocalDocumentEditorActionIntents,
      }),
    [diagnosticActionPlacements, module],
  )
  const saveFlowRuntime = flowRegistry.getRuntime(saveFlowId)
  const isSaveReviewOpen = isPresentationFlowSurfaceOpenForData({
    dataKey: 'resourceKey',
    dataValue: resourceKey,
    flowId: saveFlowId,
    runtime: saveFlowRuntime,
  })
  const saveActionId = optionalActionId(saveAction, 'document-save')
  const saveCommitActionId = optionalActionId(saveCommitAction, 'save-commit')
  const saveDismissActionId = optionalActionId(saveDismissAction, 'save-dismiss')
  const saveRevertActionId = optionalActionId(saveRevertAction, 'save-revert')
  const resetDocumentActionId = optionalActionId(resetDocumentAction, 'document-reset')
  const formatDocumentActionId = optionalActionId(formatDocumentAction, 'document-format')

  const save = usePresentationActionExecutor<void, TResource>({
    actionId: saveActionId,
    dataSourceId,
    dataSourceQueryKey,
    executeEndpoint,
    module,
    prepareRequest: ({ action, endpointId }) => {
      if (!resource) {
        throw new Error('A loaded resource is required before saving.')
      }
      if (!jsonValidation.ok) {
        throw new Error('Valid JSON is required before saving.')
      }

      return projectRequiredPresentationActionEndpointRequest({
        action,
        actionId: saveActionId,
        dataSourceId,
        endpointId,
        sources: {
          document: { value: jsonValidation.value },
          resource,
          route: { id: resourceId },
        },
      })
    },
    setResultQueryData: setSaveResultQueryData,
  })

  function resetTransientState() {
    save.reset()
    saveFlowRuntime.clearFlow()
  }

  function resetDocument() {
    resetTransientState()
    onResetEditorState()
  }

  function formatDocument() {
    if (jsonValidation.ok && resourceKey) {
      resetTransientState()
      onSetEditorText(formatDocumentValue(jsonValidation.value))
    }
  }

  function requestSaveReview() {
    if (!resource || !resourceKey || !jsonValidation.ok || !save.endpointId || save.isPending) {
      return
    }

    save.reset()
    const started = saveFlowRuntime.startFlow({
      actionId: saveActionId,
      data: { resourceKey },
      flowId: saveFlowId,
    })
    if (!started) {
      const surfaceState = findPresentationFlowSurfaceState(saveFlow)
      saveFlowRuntime.startFlow({
        data: { resourceKey },
        flowId: saveFlowId,
        stateId: surfaceState?.Id,
      })
    }
  }

  function acceptSaveReview() {
    if (!resource || !jsonValidation.ok || save.isPending) {
      return
    }

    saveFlowRuntime.dispatchAction(saveCommitActionId)
    void save.executeAsync(undefined)
      .then(() => {
        onResetEditorState()
        advancePresentationFlowToStateKind({
          flow: saveFlow,
          runtime: saveFlowRuntime,
          stateKinds: [flowStateKinds.terminal],
        })
        saveFlowRuntime.clearFlow()
      })
      .catch(() => {
        const failed = advancePresentationFlowToStateKind({
          flow: saveFlow,
          runtime: saveFlowRuntime,
          stateKinds: [flowStateKinds.error],
        })
        if (!failed) {
          const errorState = findPresentationFlowErrorSurfaceState(saveFlow)
          if (errorState) {
            saveFlowRuntime.transitionTo(errorState.Id, { flowId: saveFlowId })
          }
        }
      })
  }

  function cancelSaveReview() {
    save.reset()
    if (!saveFlowRuntime.dispatchAction(saveDismissActionId)) {
      saveFlowRuntime.clearFlow()
      return
    }
    saveFlowRuntime.clearFlow()
  }

  function resetSaveReview() {
    save.reset()
    if (!saveFlowRuntime.dispatchAction(saveRevertActionId)) {
      saveFlowRuntime.clearFlow()
    }
    saveFlowRuntime.clearFlow()
    onResetEditorState()
  }

  return {
    actions: {
      formatDocument: formatDocumentAction,
      resetDocument: resetDocumentAction,
      save: save.action,
      saveCommit: saveCommitAction,
      saveDismiss: saveDismissAction,
      saveRevert: saveRevertAction,
    },
    actionIds: {
      formatDocument: formatDocumentActionId,
      resetDocument: resetDocumentActionId,
      save: saveActionId,
      saveCommit: saveCommitActionId,
      saveDismiss: saveDismissActionId,
      saveRevert: saveRevertActionId,
    },
    acceptSaveReview,
    cancelSaveReview,
    diagnosticActionPlacements,
    diagnostics,
    flowRegistry,
    formatDocument,
    isSaveReviewOpen,
    promptView: savePromptView,
    resetDocument,
    resetSaveReview,
    resetTransientState,
    requestSaveReview,
    save: {
      action: save.action,
      endpointId: save.endpointId,
      error: save.error,
      isPending: save.isPending,
      reset: save.reset,
    },
    saveFlow,
    saveFlowId,
    saveFlowRuntime,
  }
}

const supportedLocalDocumentEditorActionIntents = ['reset', 'format'] as const
const emptyActionPlacements: readonly ActionPlacementDefinition[] = []

export function mergeActionPlacements(
  ...groups: readonly (readonly ActionPlacementDefinition[])[]
) {
  const seen = new Set<string>()
  return groups.flatMap((group) =>
    group.filter((placement) => {
      const key = `${placement.Region}:${placement.ActionId}`
      if (seen.has(key)) {
        return false
      }

      seen.add(key)
      return true
    }),
  )
}

export function optionalActionId(
  action: Pick<ActionDefinition, 'Id'> | null | undefined,
  fallback: string,
) {
  return action?.Id ?? `missing:${fallback}`
}

function formatJsonDocumentValue(value: unknown) {
  return JSON.stringify(value ?? null, null, 2)
}
