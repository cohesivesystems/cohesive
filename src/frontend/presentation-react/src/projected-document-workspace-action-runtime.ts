import { useMemo, type ReactNode } from 'react'

import type {
  DocumentWorkspaceProfileProjection,
  ProjectedDocumentResource,
} from '@cohesivesystems/presentation-core'
import type {
  ActionDefinition,
  ActionPlacementDefinition,
  PresentationModuleDefinition,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationActionRenderContext,
  PresentationActionGroupOptions,
} from './presentation-action-group-runtime-options'
import {
  createPresentationActionGroupRuntimeOptions,
} from './presentation-action-group-runtime-options'
import type {
  PresentationActionEndpointExecutor,
  PresentationActionSuccessContext,
} from './presentation-action-runtime'
import {
  projectPresentationActionRuntimeBindingDiagnostics,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationActionRuntimeRegistry,
} from '@cohesivesystems/presentation-core'
import {
  projectPresentationActionRuntimeRegistry,
} from '@cohesivesystems/presentation-core'
import {
  projectPresentationNavigationActionRuntimeBindings,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationFlowRuntimeRegistrySnapshot,
} from './presentation-flow-runtime'
import {
  mergePresentationProjectionDiagnostics,
  type PresentationProjectionDiagnostic,
} from '@cohesivesystems/presentation-core'
import {
  projectDocumentActionRuntimeBindings,
} from './projected-document-action-runtime-bindings'
import type {
  ProjectedDocumentActionRuntimeProfileSpec,
} from '@cohesivesystems/presentation-core'
import {
  mergeActionPlacements,
  useProjectedDocumentActionRuntime,
  type ProjectedDocumentJsonValidationState,
} from './projected-document-action-runtime'
import {
  useProjectedDocumentProcessPreviewCapabilityRuntime,
  type ProjectedDocumentProcessPreviewCapabilityRuntime,
  type ProjectedDocumentProcessPreviewCapabilityState,
} from './projected-document-process-preview-capability-runtime'
import {
  projectDocumentActionStatusMap,
  type ProjectedDocumentActionStatusMap,
  type ProjectedActionStatusSource,
} from '@cohesivesystems/presentation-core'
import type {
  ProjectedInputFormRuntime,
} from '@cohesivesystems/presentation-core'
import type {
  ProcessTask,
  ProcessTaskSelector,
  ProcessTaskStartRegistration,
} from '@cohesivesystems/presentation-core'
import type {
  ProjectedPromptDocumentPreviewResource,
} from '@cohesivesystems/presentation-core'

type DocumentWorkspaceActionExecuteContext<TActionContext> =
  PresentationActionRenderContext<TActionContext>

type DocumentWorkspaceActionRuntimeRegistry<TActionContext> =
  PresentationActionRuntimeRegistry<
    DocumentWorkspaceActionExecuteContext<TActionContext>,
    ReactNode
  >

export interface UseProjectedDocumentWorkspaceActionRuntimeOptions<
  TActionContext,
  TResource extends ProjectedDocumentResource = ProjectedDocumentResource,
> {
  readonly actionPlacements: readonly ActionPlacementDefinition[]
  readonly createActionGroupOptions?: (context: {
    readonly runtimes: DocumentWorkspaceActionRuntimeRegistry<TActionContext>
  }) => PresentationActionGroupOptions<TActionContext>
  readonly createHref?: ((routeId: string) => string | null) | null
  readonly dataSourceQueryKey?: (dataSourceId: string) => readonly unknown[]
  readonly executeEndpoint: PresentationActionEndpointExecutor
  readonly findActiveTask?: (selector: ProcessTaskSelector) => ProcessTask | null
  readonly formatDocument?: (value: unknown) => string
  readonly isEditorDirty: boolean
  readonly jsonValidation: ProjectedDocumentJsonValidationState
  readonly module: PresentationModuleDefinition | null
  readonly navigateHref: (href: string) => void
  readonly onResetEditorState: () => void
  readonly onSetEditorText: (text: string) => void
  readonly processActionRuntimeProfile?: Pick<
    ProjectedDocumentActionRuntimeProfileSpec,
    'actionId' | 'flowId'
  > | null
  readonly processInputStateKey?: string | null
  readonly projection: Pick<
    DocumentWorkspaceProfileProjection,
    'dataSourceId' | 'profile'
  >
  readonly registerProcessTaskStart?: (started: ProcessTaskStartRegistration) => void
  readonly resolveNavigationHref: (action: ActionDefinition) => string | null
  readonly resource: TResource | undefined
  readonly resourceId: string
  readonly resourceKey: string | null
  readonly setSaveResultQueryData?: (
    context: PresentationActionSuccessContext<void, TResource>,
  ) => void
  readonly source?: string
}

export interface ProjectedDocumentWorkspaceActionRuntimeState<
  TActionContext,
  TPreviewRequest extends object,
  TPreviewResponse,
  TPreviewValues extends Readonly<Record<string, unknown>> =
    Readonly<Record<string, never>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
> {
  readonly actionGroupOptions: PresentationActionGroupOptions<TActionContext>
  readonly actionRuntimes: DocumentWorkspaceActionRuntimeRegistry<TActionContext>
  readonly actionStatuses: ProjectedDocumentActionStatusMap
  readonly activeProcessPreview: ProjectedDocumentProcessPreviewCapabilityState<
    TPreviewResponse,
    TPreviewRequest,
    TPreviewValues,
    TPreviewResource
  > | null
  readonly acceptProcessPreview: () => void
  readonly cancelProcessPreview: () => void
  readonly cancelProcessPrompt: () => void
  readonly diagnostics: readonly PresentationProjectionDiagnostic[]
  readonly flowRegistry: PresentationFlowRuntimeRegistrySnapshot
  readonly processInput: {
    readonly runtime: ProjectedInputFormRuntime<TPreviewRequest> | null
  }
  readonly processPreview: {
    readonly error: unknown
    readonly isPending: boolean
  }
  readonly processStart: {
    readonly error: unknown
    readonly isPending: boolean
  }
  readonly processPreviewCapability: ProjectedDocumentProcessPreviewCapabilityRuntime<
    DocumentWorkspaceActionExecuteContext<TActionContext>,
    ReactNode,
    TPreviewRequest,
    TPreviewResponse,
    TPreviewValues,
    TPreviewResource
  >
  readonly promptDocumentPreview: {
    readonly data: ProjectedDocumentProcessPreviewCapabilityState<
      TPreviewResponse,
      TPreviewRequest,
      TPreviewValues,
      TPreviewResource
    > | null
    readonly error: unknown
    readonly isPending: boolean
  }
  readonly resetTransientState: () => void
  readonly save: {
    readonly error: unknown
    readonly isPending: boolean
  }
}

/**
 * Composes the standard projected document workspace action runtime.
 *
 * This hook joins document-local actions, parameterless navigation actions,
 * process-preview actions, runtime diagnostics, and action-group projection
 * without knowing any product-specific document type.
 */
export function useProjectedDocumentWorkspaceActionRuntime<
  TActionContext,
  TPreviewRequest extends object,
  TPreviewResponse,
  TStartInput = TPreviewRequest,
  TStartResult = unknown,
  TPreviewValues extends Readonly<Record<string, unknown>> =
    Readonly<Record<string, never>>,
  TResource extends ProjectedDocumentResource = ProjectedDocumentResource,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
>({
  actionPlacements,
  createActionGroupOptions = ({ runtimes }) =>
    createPresentationActionGroupRuntimeOptions<TActionContext>({ runtimes }),
  createHref,
  dataSourceQueryKey,
  executeEndpoint,
  findActiveTask,
  formatDocument,
  isEditorDirty,
  jsonValidation,
  module,
  navigateHref,
  onResetEditorState,
  onSetEditorText,
  processActionRuntimeProfile,
  processInputStateKey,
  projection,
  registerProcessTaskStart,
  resolveNavigationHref,
  resource,
  resourceId,
  resourceKey,
  setSaveResultQueryData,
  source = 'projected-document-workspace-action-runtime',
}: UseProjectedDocumentWorkspaceActionRuntimeOptions<
  TActionContext,
  TResource
>): ProjectedDocumentWorkspaceActionRuntimeState<
  TActionContext,
  TPreviewRequest,
  TPreviewResponse,
  TPreviewValues,
  TPreviewResource
> {
  const formatDocumentValue = formatDocument ?? formatJsonDocumentValue
  const documentActionRuntime = useProjectedDocumentActionRuntime<TResource>({
    actionPlacements,
    dataSourceId: projection.dataSourceId,
    dataSourceQueryKey,
    executeEndpoint,
    formatDocument: formatDocumentValue,
    jsonValidation,
    module,
    onResetEditorState,
    onSetEditorText,
    resource,
    resourceId,
    resourceKey,
    setSaveResultQueryData,
  })
  const flowRegistry = documentActionRuntime.flowRegistry
  const processPreviewCapability = useProjectedDocumentProcessPreviewCapabilityRuntime<
    TPreviewRequest,
    TPreviewResponse,
    TStartInput,
    TStartResult,
    DocumentWorkspaceActionExecuteContext<TActionContext>,
    ReactNode,
    TPreviewValues,
    TResource,
    TPreviewResource
  >({
    actionId: processActionRuntimeProfile?.actionId ?? null,
    actionPlacements,
    createHref,
    dataSourceId: projection.dataSourceId,
    dataSourceQueryKey,
    executeEndpoint,
    findActiveTask,
    flowRegistry,
    flowId: processActionRuntimeProfile?.flowId ?? null,
    hasResource: Boolean(resource),
    inputStateKey: processInputStateKey ?? null,
    localActionEnablement: {
      isDocumentDirty: isEditorDirty,
      isDocumentValid: jsonValidation.ok,
      pendingActionIds:
        documentActionRuntime.save.isPending && documentActionRuntime.actions.save
          ? [documentActionRuntime.actions.save.Id]
          : [],
    },
    module,
    processTaskContext: { resourceId },
    processTaskSelectors: projection.profile.ProcessTaskSelectors ?? [],
    registerProcessTaskStart,
    resource,
    resourceId,
    resourceKey,
  })
  const diagnosticActionPlacements = useMemo(
    () =>
      mergeActionPlacements(
        documentActionRuntime.diagnosticActionPlacements,
        processPreviewCapability.diagnosticActionPlacements,
      ),
    [
      documentActionRuntime.diagnosticActionPlacements,
      processPreviewCapability.diagnosticActionPlacements,
    ],
  )

  function resetTransientState() {
    documentActionRuntime.resetTransientState()
    processPreviewCapability.reset()
  }

  function resetDocument() {
    resetTransientState()
    onResetEditorState()
  }

  function formatCurrentDocument() {
    if (jsonValidation.ok && resourceKey) {
      resetTransientState()
      onSetEditorText(formatDocumentValue(jsonValidation.value))
    }
  }

  const actionRuntimes = projectPresentationActionRuntimeRegistry<
    DocumentWorkspaceActionExecuteContext<TActionContext>,
    ReactNode
  >({
    module,
    projections: [
      ...projectPresentationNavigationActionRuntimeBindings<
        DocumentWorkspaceActionExecuteContext<TActionContext>,
        ReactNode
      >({
        navigateHref,
        resolveHref: resolveNavigationHref,
      }),
      ...projectDocumentActionRuntimeBindings<
        DocumentWorkspaceActionExecuteContext<TActionContext>,
        ReactNode
      >({
        formatDocument: formatCurrentDocument,
        hasResource: Boolean(resource),
        isEditorDirty,
        jsonValidation,
        resetDocument,
        runtime: documentActionRuntime,
      }),
      ...processPreviewCapability.actionBindings,
    ],
  })
  const actionStatuses = projectDocumentActionStatusMap([
    {
      action: documentActionRuntime.save.action,
      error: documentActionRuntime.save.error,
      isPending: documentActionRuntime.save.isPending,
    },
    ...processPreviewCapability.actionStatuses,
  ] satisfies readonly ProjectedActionStatusSource[])
  const diagnostics = mergePresentationProjectionDiagnostics(
    documentActionRuntime.diagnostics,
    processPreviewCapability.diagnostics,
    projectPresentationActionRuntimeBindingDiagnostics({
      actionPlacements: diagnosticActionPlacements,
      module,
      runtimes: actionRuntimes,
      source,
    }),
  )

  return {
    actionGroupOptions: createActionGroupOptions({ runtimes: actionRuntimes }),
    actionRuntimes,
    actionStatuses,
    activeProcessPreview: processPreviewCapability.activePreview,
    acceptProcessPreview: processPreviewCapability.startPreview,
    cancelProcessPreview: processPreviewCapability.cancelPreview,
    cancelProcessPrompt: processPreviewCapability.cancelPrompt,
    diagnostics,
    flowRegistry,
    processInput: {
      runtime: processPreviewCapability.input.runtime,
    },
    processPreview: {
      error: processPreviewCapability.preview.error,
      isPending: processPreviewCapability.preview.isPending,
    },
    processPreviewCapability,
    processStart: {
      error: processPreviewCapability.start.error,
      isPending: processPreviewCapability.start.isPending,
    },
    promptDocumentPreview: {
      data: processPreviewCapability.activePreview,
      error: processPreviewCapability.preview.error,
      isPending: processPreviewCapability.preview.isPending,
    },
    resetTransientState,
    save: {
      error: documentActionRuntime.save.error,
      isPending: documentActionRuntime.save.isPending,
    },
  }
}

function formatJsonDocumentValue(value: unknown) {
  return JSON.stringify(value ?? null, null, 2)
}
