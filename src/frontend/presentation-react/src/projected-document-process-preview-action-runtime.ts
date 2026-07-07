import { useMemo, useState } from 'react'

import type {
  ProjectedDocumentResource,
  PresentationNavigationHrefFactory,
} from '@cohesive/presentation-core'
import { useProjectedInputFormEndpointRuntime } from './input-form-endpoint-runtime'
import type {
  ActionDefinition,
  ActionPlacementDefinition,
  FlowDefinition,
  InputFormDefinition,
  PresentationModuleDefinition,
  PromptDocumentPreviewDefinition,
  ProcessTaskSelectorDefinition,
  ViewDefinition,
} from '@cohesive/presentation-core'
import {
  findPresentationFlow,
  findPresentationInputFormForView,
  findPresentationView,
} from '@cohesive/presentation-core'
import {
  getPresentationViewProjectedActionPlacements,
} from '@cohesive/presentation-core'
import {
  projectRequiredPresentationActionEndpointRequest,
} from '@cohesive/presentation-core'
import {
  projectDocumentLocalActionEnablement,
  type ProjectedDocumentActionEnablement,
  type ProjectedDocumentLocalActionEnablementContext,
} from '@cohesive/presentation-core'
import type {
  PresentationActionEndpointExecutionRequest,
  PresentationActionEndpointExecutor,
  PresentationActionSuccessContext,
} from './presentation-action-runtime'
import {
  usePresentationActionExecutor,
} from './presentation-action-runtime'
import {
  advancePresentationFlowToStateKind,
  findPresentationFlowTransition,
  findPresentationFlowSurfaceState,
  isPresentationFlowSurfaceOpenForData,
  resolvePresentationActionPreparedFlow,
  type PresentationFlowRuntimeRegistrySnapshot,
  type PresentationFlowRuntimeSnapshot,
} from './presentation-flow-runtime'
import type {
  PresentationProjectionDiagnostic,
  ProjectedInputFormActionContext,
  ProjectedInputFormRuntime,
} from '@cohesive/presentation-core'
import {
  findDocumentProcessPreviewAction,
  findInputFormActionPlacement,
  findPromptCommitAction,
  findPromptDismissAction,
} from '@cohesive/presentation-core'
import {
  findPromptDocumentPreviewView,
  projectPromptDocumentPreviewData,
  type ProjectedPromptDocumentPreviewData,
  type ProjectedPromptDocumentPreviewResource,
} from '@cohesive/presentation-core'
import {
  type ProcessTask,
  type ProcessTaskSelector,
  type ProcessTaskStartRegistration,
} from '@cohesive/presentation-core'
import {
  projectProcessTaskActionEnablement,
  projectProcessTaskStartRegistration,
  projectProcessTaskSelector,
  type ProjectedProcessTaskActionEnablement,
} from '@cohesive/presentation-core'
import { flowStateKinds } from '@cohesive/presentation-contracts'

/**
 * Active preview data produced by a document-scoped process preview action.
 *
 * The state includes the preview endpoint request/response, the projected
 * preview resource, formatted document text, caller-provided preview values,
 * and the document `resourceKey` used to correlate the preview with the active
 * workspace resource.
 *
 * @typeParam TPreviewResponse - Response returned by the preview endpoint.
 * @typeParam TPreviewRequest - Request object submitted to the preview endpoint.
 * @typeParam TPreviewValues - Additional values projected by the host runtime.
 * @typeParam TPreviewResource - Resource shape rendered by the prompt preview.
 */
export type ProjectedDocumentProcessPreviewState<
  TPreviewResponse = unknown,
  TPreviewRequest extends object = object,
  TPreviewValues extends Readonly<Record<string, unknown>> =
    Readonly<Record<string, never>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
> = ProjectedPromptDocumentPreviewData<
  TPreviewResponse,
  TPreviewRequest,
  TPreviewResource,
  TPreviewValues & { readonly resourceKey: string }
>

/**
 * Success callback context for the action that starts a process after the user
 * accepts a document preview.
 *
 * @typeParam TPreviewRequest - Request object submitted to the preview endpoint.
 * @typeParam TPreviewResponse - Response returned by the preview endpoint.
 * @typeParam TStartInput - Input sent to the start endpoint.
 * @typeParam TStartResult - Result returned by the start endpoint.
 * @typeParam TPreviewValues - Additional values projected by the host runtime.
 * @typeParam TPreviewResource - Resource shape rendered by the prompt preview.
 */
export type ProjectedDocumentProcessStartSuccessContext<
  TPreviewRequest extends object,
  TPreviewResponse,
  TStartInput,
  TStartResult,
  TPreviewValues extends Readonly<Record<string, unknown>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource,
> = PresentationActionSuccessContext<TStartInput, TStartResult> & {
  readonly preview: ProjectedDocumentProcessPreviewState<
    TPreviewResponse,
    TPreviewRequest,
    TPreviewValues,
    TPreviewResource
  > | null
}

/**
 * Context used to register a started process task after the start endpoint
 * succeeds.
 *
 * @typeParam TPreviewRequest - Request object submitted to the preview endpoint.
 * @typeParam TPreviewResponse - Response returned by the preview endpoint.
 * @typeParam TStartInput - Input sent to the start endpoint.
 * @typeParam TStartResult - Result returned by the start endpoint.
 * @typeParam TPreviewValues - Additional values projected by the host runtime.
 * @typeParam TPreviewResource - Resource shape rendered by the prompt preview.
 */
export type ProjectedDocumentProcessTaskStartRegistrationContext<
  TPreviewRequest extends object,
  TPreviewResponse,
  TStartInput,
  TStartResult,
  TPreviewValues extends Readonly<Record<string, unknown>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource,
> = ProjectedDocumentProcessStartSuccessContext<
  TPreviewRequest,
  TPreviewResponse,
  TStartInput,
  TStartResult,
  TPreviewValues,
  TPreviewResource
> & {
  readonly actionEnablement: ProjectedProcessTaskActionEnablement
  readonly selector: ProcessTaskSelectorDefinition | null
}

/**
 * Options for the document process-preview action runtime hook.
 *
 * @typeParam TPreviewRequest - Request object submitted to the preview endpoint.
 * @typeParam TPreviewResponse - Response returned by the preview endpoint.
 * @typeParam TStartInput - Input sent to the start endpoint.
 * @typeParam TStartResult - Result returned by the start endpoint.
 * @typeParam TPreviewValues - Additional values projected by the host runtime.
 * @typeParam TResource - Document resource shape owned by the workspace.
 * @typeParam TPreviewResource - Resource shape rendered by the prompt preview.
 */
export interface UseProjectedDocumentProcessPreviewActionRuntimeOptions<
  TPreviewRequest extends object,
  TPreviewResponse,
  TStartInput,
  TStartResult,
  TPreviewValues extends Readonly<Record<string, unknown>> =
    Readonly<Record<string, never>>,
  TResource extends ProjectedDocumentResource = ProjectedDocumentResource,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
> {
  /** Optional process-preview action id override. */
  readonly actionId?: string | null

  /** Action placements declared for the document workspace or active surface. */
  readonly actionPlacements: readonly ActionPlacementDefinition[]

  /** Creates flow data used when advancing from process start success. */
  readonly createProcessFlowData?: (
    context: ProjectedDocumentProcessStartSuccessContext<
      TPreviewRequest,
      TPreviewResponse,
      TStartInput,
      TStartResult,
      TPreviewValues,
      TPreviewResource
    >,
  ) => Readonly<Record<string, unknown>>

  /** Creates the start endpoint input from the accepted preview. */
  readonly createStartInput?: (
    preview: ProjectedDocumentProcessPreviewState<
      TPreviewResponse,
      TPreviewRequest,
      TPreviewValues,
      TPreviewResource
    >,
  ) => TStartInput

  /** Creates flow data dispatched when the user accepts the preview. */
  readonly createStartFlowData?: (
    preview: ProjectedDocumentProcessPreviewState<
      TPreviewResponse,
      TPreviewRequest,
      TPreviewValues,
      TPreviewResource
    >,
  ) => Readonly<Record<string, unknown>>

  /** Creates a process task registration from a successful start action. */
  readonly createProcessTaskStartRegistration?: (
    context: ProjectedDocumentProcessTaskStartRegistrationContext<
      TPreviewRequest,
      TPreviewResponse,
      TStartInput,
      TStartResult,
      TPreviewValues,
      TPreviewResource
    >,
  ) => ProcessTaskStartRegistration | null

  /** Data source id associated with the preview/start action bindings. */
  readonly dataSourceId: string

  /** Query key factory used for action invalidation and process task links. */
  readonly dataSourceQueryKey?: (dataSourceId: string) => readonly unknown[]

  /** Default value or value factory for the prompt input form. */
  readonly defaultValue?: TPreviewRequest | (() => TPreviewRequest)

  /** Host endpoint executor used by both preview and start actions. */
  readonly executeEndpoint: PresentationActionEndpointExecutor

  /** Finds an already-active process task matching a projected selector. */
  readonly findActiveTask?: (selector: ProcessTaskSelector) => ProcessTask | null

  /** Optional flow id override for the process-preview flow. */
  readonly flowId?: string | null

  /** Runtime registry that owns flow instances for prompt/process transitions. */
  readonly flowRegistry: PresentationFlowRuntimeRegistrySnapshot

  /** Optional href factory used when projecting process task links. */
  readonly createHref?: PresentationNavigationHrefFactory | null

  /** Optional state key for persisting prompt input form state. */
  readonly inputStateKey?: string | null

  /** Whether the process-preview action is available for the current document. */
  readonly isActionAvailable?: boolean

  /** Whether preview execution is temporarily blocked by the host. */
  readonly isPreviewBlocked?: boolean

  /** Whether confirmed process start is temporarily blocked by the host. */
  readonly isStartBlocked?: boolean

  /** Local document action enablement context for preview/start actions. */
  readonly localActionEnablement?: ProjectedDocumentLocalActionEnablementContext | null

  /** Active presentation module containing actions, flows, forms, and views. */
  readonly module: PresentationModuleDefinition | null

  /** Hook called after the process start action succeeds. */
  readonly processStartResult?: (
    context: ProjectedDocumentProcessStartSuccessContext<
      TPreviewRequest,
      TPreviewResponse,
      TStartInput,
      TStartResult,
      TPreviewValues,
      TPreviewResource
    >,
  ) => void

  /** Projects the preview resource rendered in the prompt document preview. */
  readonly projectPreviewResource?: (context: {
    readonly request: TPreviewRequest
    readonly resource: TResource
    readonly resourceKey: string
    readonly response: TPreviewResponse
  }) => TPreviewResource | null | undefined

  /** Projects additional preview data available to preview templates. */
  readonly projectPreviewValues?: (context: {
    readonly request: TPreviewRequest
    readonly resource: TResource
    readonly resourceKey: string
    readonly response: TPreviewResponse
  }) => TPreviewValues

  /** Host context used to project process task selectors and enablement. */
  readonly processTaskContext?: unknown

  /** Process task selector definitions declared for the preview action. */
  readonly processTaskSelectors?: readonly ProcessTaskSelectorDefinition[]

  /** Converts selector definitions into runtime process task selectors. */
  readonly projectProcessTaskSelector?: (
    selector: ProcessTaskSelectorDefinition | null | undefined,
    context: unknown,
  ) => ProcessTaskSelector | null

  /** Prepares the preview endpoint request from prompt input form state. */
  readonly preparePreviewRequest?: (
    context: ProjectedDocumentProcessPreviewRequestContext<
      TPreviewRequest,
      TResource
    >,
  ) => PresentationActionEndpointExecutionRequest

  /** Prepares the process start endpoint request from accepted preview input. */
  readonly prepareStartRequest?: (
    context: ProjectedDocumentProcessStartRequestContext<TStartInput, TResource>,
  ) => PresentationActionEndpointExecutionRequest

  /** Current document resource, when loaded. */
  readonly resource: TResource | undefined

  /** Stable document resource id from the active route or workspace. */
  readonly resourceId: string

  /** Stable document resource key used to correlate flow instances and preview state. */
  readonly resourceKey: string | null

  /** Registers a started process task with the host process task runtime. */
  readonly registerProcessTaskStart?: (started: ProcessTaskStartRegistration) => void

  /** Message used when the preview action is unavailable. */
  readonly unavailableMessage?: string

  /** Host validation hook run immediately before preview request projection. */
  readonly validatePreviewRequest?: () => void

  /** Host validation hook run immediately before start request projection. */
  readonly validateStartRequest?: () => void
}

/**
 * Request-preparation context for the preview endpoint.
 *
 * @typeParam TPreviewRequest - Request object submitted to the preview endpoint.
 * @typeParam TResource - Document resource shape owned by the workspace.
 */
export interface ProjectedDocumentProcessPreviewRequestContext<
  TPreviewRequest extends object,
  TResource extends ProjectedDocumentResource,
> {
  /** Preview action definition resolved from the presentation module. */
  readonly action: ActionDefinition | null

  /** Input form action context captured from the projected form runtime. */
  readonly actionContext: ProjectedInputFormActionContext<TPreviewRequest>

  /** Preview action id used for endpoint request projection. */
  readonly actionId: string

  /** Data source id associated with the preview action binding. */
  readonly dataSourceId: string

  /** Endpoint id selected by the active action target binding. */
  readonly endpointId: string

  /** Input form definition rendered in the process preview prompt. */
  readonly inputForm: InputFormDefinition

  /** Active presentation module used for endpoint request projection. */
  readonly module: PresentationModuleDefinition | null

  /** Current document resource. */
  readonly resource: TResource

  /** Stable document resource id from the active route or workspace. */
  readonly resourceId: string

  /** Stable document resource key used to correlate preview state. */
  readonly resourceKey: string

  /** Current prompt input form value. */
  readonly value: TPreviewRequest
}

/**
 * Request-preparation context for the confirmed process start endpoint.
 *
 * @typeParam TStartInput - Input sent to the start endpoint.
 * @typeParam TResource - Document resource shape owned by the workspace.
 */
export interface ProjectedDocumentProcessStartRequestContext<
  TStartInput,
  TResource extends ProjectedDocumentResource,
> {
  /** Start action definition resolved from the prompt commit action. */
  readonly action: ActionDefinition | null

  /** Start action id used for endpoint request projection. */
  readonly actionId: string

  /** Data source id associated with the start action binding. */
  readonly dataSourceId: string

  /** Endpoint id selected by the active action target binding. */
  readonly endpointId: string

  /** Start endpoint input projected from the accepted preview. */
  readonly input: TStartInput

  /** Active presentation module used for endpoint request projection. */
  readonly module: PresentationModuleDefinition | null

  /** Current document resource. */
  readonly resource: TResource

  /** Stable document resource id from the active route or workspace. */
  readonly resourceId: string
}

/**
 * Runtime state and commands for a document process-preview action.
 *
 * @typeParam TPreviewRequest - Request object submitted to the preview endpoint.
 * @typeParam TPreviewResponse - Response returned by the preview endpoint.
 * @typeParam TPreviewValues - Additional values projected by the host runtime.
 * @typeParam TPreviewResource - Resource shape rendered by the prompt preview.
 */
export interface ProjectedDocumentProcessPreviewActionRuntime<
  TPreviewRequest extends object,
  TPreviewResponse,
  TPreviewValues extends Readonly<Record<string, unknown>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource,
> {
  /** Resolved presentation actions participating in the preview prompt flow. */
  readonly actions: {
    /** Prompt dismiss action. */
    readonly dismiss: ActionDefinition | null

    /** Action that requests the preview. */
    readonly preview: ActionDefinition | null

    /** Prompt commit action that starts the process. */
    readonly start: ActionDefinition | null
  }

  /** Local enablement results for preview and start actions. */
  readonly actionEnablement: {
    /** Enablement result for the preview action. */
    readonly preview: ProjectedDocumentActionEnablement

    /** Enablement result for the process start action. */
    readonly start: ProjectedDocumentActionEnablement
  }

  /** Stable action ids used for flow transitions and diagnostics. */
  readonly actionIds: {
    /** Dismiss action id or a diagnostic fallback id. */
    readonly dismiss: string

    /** Preview action id or a diagnostic fallback id. */
    readonly preview: string

    /** Start action id or a diagnostic fallback id. */
    readonly start: string
  }

  /** Accepts the active preview and invokes the process start action. */
  readonly acceptPreview: () => void

  /** Preview data for the active resource key, or `null` when none is active. */
  readonly activePreview: ProjectedDocumentProcessPreviewState<
    TPreviewResponse,
    TPreviewRequest,
    TPreviewValues,
    TPreviewResource
  > | null

  /** Whether the preview request action can currently execute. */
  readonly canExecuteAction: boolean

  /** Whether the active preview can currently be accepted. */
  readonly canAcceptPreview: boolean

  /** Cancels the active preview and clears preview state. */
  readonly cancelPreview: () => void

  /** Cancels the prompt flow without accepting the preview. */
  readonly cancelPrompt: () => void

  /** Action placements used for projection diagnostics in the prompt view. */
  readonly diagnosticActionPlacements: readonly ActionPlacementDefinition[]

  /** Projection diagnostics for missing action, flow, form, and endpoint pieces. */
  readonly diagnostics: readonly PresentationProjectionDiagnostic[]

  /** User-facing disabled reasons for preview and start commands. */
  readonly disabledReasons: {
    /** Reason preview execution is disabled, or `null` when enabled. */
    readonly preview: string | null

    /** Reason process start is disabled, or `null` when enabled. */
    readonly start: string | null
  }

  /** Process-preview flow definition resolved from the action or override. */
  readonly flow: FlowDefinition | null

  /** Process-preview flow id used by the runtime. */
  readonly flowId: string

  /** Runtime snapshot for the active process-preview flow instance. */
  readonly flowRuntime: PresentationFlowRuntimeSnapshot

  /** Whether a process-preview action was resolved. */
  readonly hasAction: boolean

  /** Prompt input form definition and projected runtime adapter. */
  readonly input: {
    /** Input form definition used by the preview prompt. */
    readonly form: InputFormDefinition | null

    /** Input form runtime adapter, or `null` when no form is projected. */
    readonly runtime: ProjectedInputFormRuntime<TPreviewRequest> | null
  }

  /** Whether the preview prompt surface is currently open for the active resource. */
  readonly isPromptOpen: boolean

  /** Preview endpoint execution state. */
  readonly preview: {
    /** Preview endpoint id selected by action target bindings. */
    readonly endpointId: string | null

    /** Last preview endpoint execution error. */
    readonly error: unknown

    /** Whether the preview endpoint is currently executing. */
    readonly isPending: boolean

    /** Resets preview form/runtime state. */
    readonly reset: () => void

    /** Resets only the preview endpoint execution state. */
    readonly resetExecution: () => void
  }

  /** Process task selector and active-task enablement projection. */
  readonly processTask: ProjectedProcessTaskActionEnablement

  /** Prompt view resolved for the process-preview flow. */
  readonly promptView: ViewDefinition | null

  /** Starts preview request execution, optionally with an explicit form action context. */
  readonly requestPreview: (
    actionContext?: ProjectedInputFormActionContext<TPreviewRequest>,
  ) => void

  /** Resets preview endpoint state, start action state, flow state, and active preview. */
  readonly reset: () => void

  /** Process start endpoint execution state. */
  readonly start: {
    /** Start action definition resolved from the prompt commit action. */
    readonly action: ActionDefinition | null

    /** Start endpoint id selected by action target bindings. */
    readonly endpointId: string | null

    /** Last start endpoint execution error. */
    readonly error: unknown

    /** Whether the start endpoint is currently executing. */
    readonly isPending: boolean

    /** Resets the start endpoint execution state. */
    readonly reset: () => void
  }
}

/**
 * Generic runtime for document-scoped process-preview flows declared in the
 * presentation IR.
 *
 * It resolves the preview flow, projects the prompt input form, executes the
 * preview endpoint, manages prompt preview state, and starts the confirmed
 * process. Product code supplies domain validation and process-result
 * interpretation.
 */
export function useProjectedDocumentProcessPreviewActionRuntime<
  TPreviewRequest extends object,
  TPreviewResponse,
  TStartInput,
  TStartResult,
  TPreviewValues extends Readonly<Record<string, unknown>> =
    Readonly<Record<string, never>>,
  TResource extends ProjectedDocumentResource = ProjectedDocumentResource,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource =
    ProjectedPromptDocumentPreviewResource,
>({
  actionId: runtimeActionId,
  actionPlacements,
  createProcessFlowData = createDefaultProcessFlowData,
  createProcessTaskStartRegistration,
  createStartInput,
  createStartFlowData = createDefaultStartFlowData,
  dataSourceId,
  dataSourceQueryKey,
  defaultValue,
  executeEndpoint,
  findActiveTask,
  flowId: runtimeFlowId,
  flowRegistry,
  createHref,
  inputStateKey,
  isActionAvailable = true,
  isPreviewBlocked = false,
  isStartBlocked = false,
  localActionEnablement,
  module,
  processStartResult,
  processTaskContext,
  processTaskSelectors = emptyProcessTaskSelectors,
  projectPreviewResource,
  projectPreviewValues,
  projectProcessTaskSelector: projectTaskSelector = projectProcessTaskSelector,
  preparePreviewRequest,
  prepareStartRequest,
  resource,
  resourceId,
  resourceKey,
  registerProcessTaskStart,
  unavailableMessage = 'This document does not expose a process preview action.',
  validatePreviewRequest,
  validateStartRequest,
}: UseProjectedDocumentProcessPreviewActionRuntimeOptions<
  TPreviewRequest,
  TPreviewResponse,
  TStartInput,
  TStartResult,
  TPreviewValues,
  TResource,
  TPreviewResource
>): ProjectedDocumentProcessPreviewActionRuntime<
  TPreviewRequest,
  TPreviewResponse,
  TPreviewValues,
  TPreviewResource
> {
  const [preview, setPreview] = useState<ProjectedDocumentProcessPreviewState<
    TPreviewResponse,
    TPreviewRequest,
    TPreviewValues,
    TPreviewResource
  > | null>(null)
  const previewAction = useMemo(
    () =>
      findDocumentProcessPreviewAction({
        actionId: runtimeActionId,
        actionPlacements,
        dataSourceId,
        flowId: runtimeFlowId,
        module,
      }),
    [actionPlacements, dataSourceId, module, runtimeActionId, runtimeFlowId],
  )
  const actionFlow = useMemo(
    () => resolvePresentationActionPreparedFlow(previewAction),
    [previewAction],
  )
  const flowId = runtimeFlowId ?? actionFlow?.flowId ?? ''
  const flow = useMemo(
    () => findPresentationFlow<FlowDefinition>(module, flowId),
    [flowId, module],
  )
  const promptView = useMemo(() => {
    const promptViewId =
      actionFlow?.promptViewId ??
      findPresentationFlowSurfaceState(flow)?.ViewId

    return promptViewId
      ? findPresentationView<ViewDefinition>(module, promptViewId)
      : null
  }, [actionFlow?.promptViewId, flow, module])
  const diagnosticActionPlacements = useMemo(
    () => getPresentationViewProjectedActionPlacements(promptView),
    [promptView],
  )
  const inputForm = useMemo(
    () => findPresentationInputFormForView(module, promptView),
    [module, promptView],
  )
  const previewDefinition = useMemo(
    () => findPromptDocumentPreviewView(module, promptView)?.PromptDocumentPreview ?? null,
    [module, promptView],
  )
  const startAction = useMemo(
    () => findPromptCommitAction({ module, view: promptView }),
    [module, promptView],
  )
  const dismissAction = useMemo(
    () => findPromptDismissAction({ module, view: promptView }),
    [module, promptView],
  )
  const inputFormActionPlacement = useMemo(
    () => findInputFormActionPlacement(inputForm, previewAction),
    [inputForm, previewAction],
  )
  const flowRuntime = flowRegistry.getRuntime(flowId)
  const activePreview =
    resourceKey !== null && preview?.resourceKey === resourceKey
      ? preview
      : null
  const isPromptOpen = isPresentationFlowSurfaceOpenForData({
    dataKey: 'resourceKey',
    dataValue: resourceKey,
    flowId,
    runtime: flowRuntime,
  })
  const previewActionId = optionalActionId(previewAction, 'document-process-preview')
  const startActionId = optionalActionId(startAction, 'document-process-start')
  const dismissActionId = optionalActionId(dismissAction, 'document-process-dismiss')
  const hasAction = Boolean(previewAction)
  const processTaskActionEnablement = projectProcessTaskActionEnablement({
    action: previewAction,
    context: processTaskContext ?? {
      resource,
      resourceId,
      route: { id: resourceId },
    },
    findActiveTask: findActiveTask ?? noActiveProcessTask,
    projectSelector: projectTaskSelector,
    selectors: processTaskSelectors,
  })
  const previewActionEnablement = projectDocumentLocalActionEnablement({
    action: previewAction,
    context: localActionEnablement,
  })
  const startActionEnablement = projectDocumentLocalActionEnablement({
    action: startAction,
    context: localActionEnablement,
  })

  const processStart = usePresentationActionExecutor<TStartInput, TStartResult>({
    actionId: startActionId,
    dataSourceId,
    dataSourceQueryKey,
    executeEndpoint,
    module,
    prepareRequest: ({ action, actionId, endpointId, input, module: requestModule }) => {
      if (!hasAction || !isActionAvailable || !resource) {
        throw new Error(unavailableMessage)
      }
      if (startActionEnablement.isDisabled) {
        throw new Error(startActionEnablement.message ?? unavailableMessage)
      }

      validateStartRequest?.()

      return prepareStartRequest?.({
        action,
        actionId,
        dataSourceId,
        endpointId,
        input,
        module: requestModule,
        resource,
        resourceId,
      }) ?? projectRequiredPresentationActionEndpointRequest({
        action,
        actionId,
        dataSourceId,
        endpointId,
        sources: {
          input,
          resource,
          route: { id: resourceId },
        },
      })
    },
    processResult: (context) => {
      const successContext = {
        ...context,
        preview: activePreview,
      } satisfies ProjectedDocumentProcessStartSuccessContext<
        TPreviewRequest,
        TPreviewResponse,
        TStartInput,
        TStartResult,
        TPreviewValues,
        TPreviewResource
      >
      const registration = createProcessTaskStartRegistration?.({
        ...successContext,
        actionEnablement: processTaskActionEnablement,
        selector: processTaskActionEnablement.selector,
      }) ?? projectProcessTaskStartRegistration({
        action: context.action,
        context: {
          input: context.input,
          resource,
          resourceId,
          result: context.result,
          route: { id: resourceId },
        },
        createHref,
        dataSourceQueryKey,
        result: context.result,
        selector: processTaskActionEnablement.selector,
      })
      if (registration) {
        registerProcessTaskStart?.(registration)
      }

      processStartResult?.(successContext)
      advancePresentationFlowToStateKind({
        allowStateFallback: false,
        data: createProcessFlowData(successContext),
        flow,
        runtime: flowRuntime,
        stateKinds: [flowStateKinds.process],
      })
    },
    onSuccess: () => {
      setPreview(null)
    },
  })

  const previewEndpoint = useProjectedInputFormEndpointRuntime<
    TPreviewRequest,
    TPreviewResponse
  >({
    dataSourceId,
    defaultValue,
    defaultValueSources: {
      resource,
      route: { id: resourceId },
    },
    executeEndpoint,
    inputForm,
    invalidateDataSourceIds: [],
    module,
    prepareRequest: (context) => {
      if (!hasAction || !isActionAvailable || !resource || !resourceKey) {
        throw new Error(unavailableMessage)
      }
      if (previewActionEnablement.isDisabled) {
        throw new Error(previewActionEnablement.message ?? unavailableMessage)
      }

      validatePreviewRequest?.()

      return preparePreviewRequest?.({
        action: context.action,
        actionContext: context.actionContext,
        actionId: previewActionId,
        dataSourceId,
        endpointId: context.endpointId,
        inputForm: context.inputForm,
        module: context.module,
        resource,
        resourceId,
        resourceKey,
        value: context.value,
      }) ?? projectRequiredPresentationActionEndpointRequest({
        action: context.action,
        actionId: previewActionId,
        dataSourceId,
        endpointId: context.endpointId,
        sources: {
          input: context.value,
          resource,
          route: { id: resourceId },
        },
      })
    },
    onSuccess: ({ result, value }) => {
      if (!resource || !resourceKey) {
        return
      }

      const projectedPreview = projectPromptDocumentPreviewData({
        definition: previewDefinition,
        request: value,
        resource: projectPreviewResource?.({
          request: value,
          resource,
          resourceKey,
          response: result,
        }) ?? undefined,
        response: result,
        values: {
          ...(projectPreviewValues?.({
            request: value,
            resource,
            resourceKey,
            response: result,
          }) ?? {}),
          resourceKey,
        } as TPreviewValues & { readonly resourceKey: string },
      })

      advancePresentationFlowToStateKind({
        allowStateFallback: false,
        data: projectedPreview,
        flow,
        runtime: flowRuntime,
        stateKinds: [flowStateKinds.prompt],
      })
      setPreview(projectedPreview)
    },
    stateKey: inputStateKey ?? null,
  })

  const canStartPreviewTransition = Boolean(
    resourceKey &&
      canStartFlowAction({
        action: previewAction,
        actionId: previewActionId,
        data: { resourceKey },
        flow,
      }),
  )
  const previewDisabledReason = resolveProcessPreviewDisabledReason({
    canStartPreviewTransition,
    flow,
    inputForm,
    inputFormActionPlacement,
    isActionAvailable,
    previewAction,
    previewActionEnablement,
    previewDefinition,
    previewEndpointId: previewEndpoint.endpointId,
    processStartEndpointId: processStart.endpointId,
    processTaskActionEnablement,
    promptView,
    resourceKey,
  })
  const activePreviewStartFlowData = activePreview
    ? createStartFlowData(activePreview)
    : null
  const canDispatchStartTransition = Boolean(
    activePreviewStartFlowData &&
      canDispatchFlowAction({
        action: startAction,
        actionId: startActionId,
        data: activePreviewStartFlowData,
        flow,
        runtime: flowRuntime,
      }),
  )
  const startDisabledReason = resolveProcessStartDisabledReason({
    activePreview,
    canDispatchStartTransition,
    isStartBlocked,
    processStartEndpointId: processStart.endpointId,
    processTaskActionEnablement,
    startAction,
    startActionEnablement,
  })
  const diagnostics = projectProcessPreviewRuntimeDiagnostics({
    actionId: runtimeActionId,
    dismissAction,
    flow,
    flowId,
    inputForm,
    inputFormActionPlacement,
    previewAction,
    previewDefinition,
    previewEndpointId: previewEndpoint.endpointId,
    promptView,
    source: 'document-process-preview-action-runtime',
    startAction,
    startEndpointId: processStart.endpointId,
  })
  const canExecuteAction = previewDisabledReason === null
  const canAcceptPreview = Boolean(activePreview && startDisabledReason === null)
  const inputRuntime = previewEndpoint.runtime
    ? {
        invokeAction: requestPreview,
        setValue: previewEndpoint.runtime.setValue,
        value: previewEndpoint.runtime.value,
      } satisfies ProjectedInputFormRuntime<TPreviewRequest>
    : null

  function reset() {
    processStart.reset()
    previewEndpoint.reset()
    flowRuntime.clearFlow()
    setPreview(null)
  }

  function requestPreview(
    actionContext?: ProjectedInputFormActionContext<TPreviewRequest>,
  ) {
    if (
      !hasAction ||
      !isActionAvailable ||
      !canExecuteAction ||
      !resource ||
      !resourceKey ||
      isPreviewBlocked ||
      previewEndpoint.isPending ||
      processStart.isPending
    ) {
      return
    }

    processStart.reset()
    previewEndpoint.resetExecution()
    setPreview(null)

    const started = flowRuntime.startFlow({
      actionId: previewActionId,
      data: { resourceKey },
      flowId,
    })
    if (!started) {
      return
    }

    requestInputFormPreview(actionContext)
  }

  function cancelPrompt() {
    previewEndpoint.reset()
    if (!flowRuntime.dispatchAction(dismissActionId)) {
      flowRuntime.clearFlow()
      return
    }
    flowRuntime.clearFlow()
  }

  function cancelPreview() {
    processStart.reset()
    if (!flowRuntime.dispatchAction(dismissActionId)) {
      flowRuntime.clearFlow()
      setPreview(null)
      return
    }
    flowRuntime.clearFlow()
    setPreview(null)
  }

  function acceptPreview() {
    if (processStart.isPending || !canAcceptPreview || !activePreview) {
      return
    }

    const startFlowData = createStartFlowData(activePreview)
    if (!flowRuntime.dispatchAction(startActionId, { data: startFlowData })) {
      return
    }

    processStart.execute(
      createStartInput?.(activePreview) ??
        projectDefaultProcessStartInput<
          TStartInput,
          TPreviewResponse,
          TPreviewRequest,
          TPreviewValues,
          TPreviewResource
        >(activePreview),
    )
  }

  function requestInputFormPreview(
    actionContext?: ProjectedInputFormActionContext<TPreviewRequest>,
  ) {
    if (!inputForm || !previewEndpoint.runtime) {
      return
    }

    if (!inputFormActionPlacement) {
      return
    }

    previewEndpoint.runtime.invokeAction(
      actionContext ?? {
        action: previewAction,
        choiceValuesByFieldId: Object.fromEntries(
          inputForm.Fields.map((field) => [field.Id, []]),
        ),
        inputForm,
        placement: inputFormActionPlacement,
        target: {
          stateId:
            inputForm.SharedStateId ??
            inputForm.Target.Id ??
            inputForm.StateDataSourceId,
        },
        value: previewEndpoint.value,
      },
    )
  }

  return {
    actions: {
      dismiss: dismissAction,
      preview: previewAction,
      start: processStart.action,
    },
    actionEnablement: {
      preview: previewActionEnablement,
      start: startActionEnablement,
    },
    actionIds: {
      dismiss: dismissActionId,
      preview: previewActionId,
      start: startActionId,
    },
    acceptPreview,
    activePreview,
    canAcceptPreview,
    canExecuteAction,
    cancelPreview,
    cancelPrompt,
    diagnosticActionPlacements,
    diagnostics,
    disabledReasons: {
      preview: previewDisabledReason,
      start: startDisabledReason,
    },
    flow,
    flowId,
    flowRuntime,
    hasAction,
    input: {
      form: inputForm,
      runtime: inputRuntime,
    },
    isPromptOpen,
    preview: {
      endpointId: previewEndpoint.endpointId,
      error: previewEndpoint.error,
      isPending: previewEndpoint.isPending,
      reset: previewEndpoint.reset,
      resetExecution: previewEndpoint.resetExecution,
    },
    processTask: processTaskActionEnablement,
    promptView,
    requestPreview,
    reset,
    start: {
      action: processStart.action,
      endpointId: processStart.endpointId,
      error: processStart.error,
      isPending: processStart.isPending,
      reset: processStart.reset,
    },
  }
}

function createDefaultStartFlowData<
  TPreviewResponse,
  TPreviewRequest extends object,
  TPreviewValues extends Readonly<Record<string, unknown>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource,
>(
  preview: ProjectedDocumentProcessPreviewState<
    TPreviewResponse,
    TPreviewRequest,
    TPreviewValues,
    TPreviewResource
  >,
) {
  return {
    request: preview.request,
    resourceKey: preview.resourceKey,
    ...readRecord(preview.response),
  }
}

function createDefaultProcessFlowData<
  TPreviewRequest extends object,
  TPreviewResponse,
  TStartInput,
  TStartResult,
  TPreviewValues extends Readonly<Record<string, unknown>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource,
>(
  context: ProjectedDocumentProcessStartSuccessContext<
    TPreviewRequest,
    TPreviewResponse,
    TStartInput,
    TStartResult,
    TPreviewValues,
    TPreviewResource
  >,
) {
  return readRecord(context.result) ?? { result: context.result }
}

function readRecord(value: unknown) {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? value as Readonly<Record<string, unknown>>
    : null
}

function projectDefaultProcessStartInput<
  TStartInput,
  TPreviewResponse,
  TPreviewRequest extends object,
  TPreviewValues extends Readonly<Record<string, unknown>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource,
>(
  preview: ProjectedDocumentProcessPreviewState<
    TPreviewResponse,
    TPreviewRequest,
    TPreviewValues,
    TPreviewResource
  >,
): TStartInput {
  const response = readRecord(preview.response)
  const startRequest =
    readCaseInsensitiveField(response, 'StartRequest') ??
    readCaseInsensitiveField(response, 'Request')

  return (startRequest ?? preview.request) as TStartInput
}

function readCaseInsensitiveField(
  record: Readonly<Record<string, unknown>> | null,
  field: string,
) {
  if (!record) {
    return undefined
  }

  if (Object.prototype.hasOwnProperty.call(record, field)) {
    return record[field]
  }

  const match = Object.keys(record).find(
    (candidate) => candidate.toLowerCase() === field.toLowerCase(),
  )
  return match ? record[match] : undefined
}

function optionalActionId(
  action: Pick<ActionDefinition, 'Id'> | null | undefined,
  fallback: string,
) {
  return action?.Id ?? `missing:${fallback}`
}

const emptyProcessTaskSelectors: readonly ProcessTaskSelectorDefinition[] = []

function noActiveProcessTask() {
  return null
}

function resolveProcessPreviewDisabledReason({
  canStartPreviewTransition,
  flow,
  inputForm,
  inputFormActionPlacement,
  isActionAvailable,
  previewAction,
  previewActionEnablement,
  previewDefinition,
  previewEndpointId,
  processStartEndpointId,
  processTaskActionEnablement,
  promptView,
  resourceKey,
}: {
  readonly canStartPreviewTransition: boolean
  readonly flow: FlowDefinition | null
  readonly inputForm: InputFormDefinition | null
  readonly inputFormActionPlacement: ActionPlacementDefinition | null
  readonly isActionAvailable: boolean
  readonly previewAction: ActionDefinition | null
  readonly previewActionEnablement: ProjectedDocumentActionEnablement
  readonly previewDefinition: PromptDocumentPreviewDefinition | null
  readonly previewEndpointId: string | null
  readonly processStartEndpointId: string | null
  readonly processTaskActionEnablement: ProjectedProcessTaskActionEnablement
  readonly promptView: ViewDefinition | null
  readonly resourceKey: string | null
}) {
  if (!previewAction || !isActionAvailable) {
    return 'This document does not expose a process preview action.'
  }

  if (!flow) {
    return `Action '${previewAction.Name}' does not resolve a preview flow.`
  }

  if (!promptView) {
    return `Preview flow '${flow.Name}' does not resolve a prompt view.`
  }

  if (!inputForm) {
    return `Prompt view '${promptView.Name}' does not resolve an input form.`
  }

  if (!inputFormActionPlacement) {
    return `Input form '${inputForm.Name}' does not place the preview action.`
  }

  if (!previewDefinition) {
    return `Prompt view '${promptView.Name}' does not declare a document preview surface.`
  }

  if (!previewEndpointId) {
    return `No preview endpoint is bound for action '${previewAction.Name}'.`
  }

  if (!processStartEndpointId) {
    return 'No process start endpoint is bound for the preview confirmation action.'
  }

  if (!resourceKey) {
    return 'A loaded resource is required before previewing this process.'
  }

  if (!canStartPreviewTransition) {
    return `Preview flow '${flow.Name}' cannot start from its initial state.`
  }

  if (previewActionEnablement.isDisabled) {
    return previewActionEnablement.message
  }

  if (processTaskActionEnablement.isDisabled) {
    return processTaskActionEnablement.message
  }

  return null
}

function resolveProcessStartDisabledReason<
  TPreviewResponse,
  TPreviewRequest extends object,
  TPreviewValues extends Readonly<Record<string, unknown>>,
  TPreviewResource extends ProjectedPromptDocumentPreviewResource,
>({
  activePreview,
  canDispatchStartTransition,
  isStartBlocked,
  processStartEndpointId,
  processTaskActionEnablement,
  startAction,
  startActionEnablement,
}: {
  readonly activePreview: ProjectedDocumentProcessPreviewState<
    TPreviewResponse,
    TPreviewRequest,
    TPreviewValues,
    TPreviewResource
  > | null
  readonly canDispatchStartTransition: boolean
  readonly isStartBlocked: boolean
  readonly processStartEndpointId: string | null
  readonly processTaskActionEnablement: ProjectedProcessTaskActionEnablement
  readonly startAction: ActionDefinition | null
  readonly startActionEnablement: ProjectedDocumentActionEnablement
}) {
  if (!activePreview) {
    return 'A preview is required before starting this process.'
  }

  if (!startAction) {
    return 'The preview prompt does not declare a process start action.'
  }

  if (!processStartEndpointId) {
    return `No process start endpoint is bound for action '${startAction.Name}'.`
  }

  if (!canDispatchStartTransition) {
    return 'The preview flow is not in a startable state.'
  }

  if (startActionEnablement.isDisabled) {
    return startActionEnablement.message
  }

  if (processTaskActionEnablement.isDisabled) {
    return processTaskActionEnablement.message
  }

  if (isStartBlocked) {
    return 'Starting this process is currently blocked.'
  }

  return null
}

function canDispatchFlowAction({
  action,
  actionId,
  data,
  flow,
  runtime,
}: {
  readonly action: ActionDefinition | null
  readonly actionId: string
  readonly data: Readonly<Record<string, unknown>>
  readonly flow: FlowDefinition | null
  readonly runtime: PresentationFlowRuntimeSnapshot
}) {
  const instance = runtime.activeInstance
  if (!flow || !instance || instance.flowId !== flow.Id) {
    return false
  }

  return Boolean(
    findPresentationFlowTransition({
      actionId,
      data: {
        ...instance.data,
        ...data,
      },
      event: action?.Binding.Id ?? actionId,
      flow,
      fromStateId: instance.stateId,
    }),
  )
}

function canStartFlowAction({
  action,
  actionId,
  data,
  flow,
}: {
  readonly action: ActionDefinition | null
  readonly actionId: string
  readonly data: Readonly<Record<string, unknown>>
  readonly flow: FlowDefinition | null
}) {
  if (!flow) {
    return false
  }

  return Boolean(
    findPresentationFlowTransition({
      actionId,
      data,
      event: action?.Binding.Id ?? actionId,
      flow,
      fromStateId: flow.InitialStateId,
    }),
  )
}

function projectProcessPreviewRuntimeDiagnostics({
  actionId,
  dismissAction,
  flow,
  flowId,
  inputForm,
  inputFormActionPlacement,
  previewAction,
  previewDefinition,
  previewEndpointId,
  promptView,
  source,
  startAction,
  startEndpointId,
}: {
  readonly actionId?: string | null
  readonly dismissAction: ActionDefinition | null
  readonly flow: FlowDefinition | null
  readonly flowId: string
  readonly inputForm: InputFormDefinition | null
  readonly inputFormActionPlacement: ActionPlacementDefinition | null
  readonly previewAction: ActionDefinition | null
  readonly previewDefinition: PromptDocumentPreviewDefinition | null
  readonly previewEndpointId: string | null
  readonly promptView: ViewDefinition | null
  readonly source: string
  readonly startAction: ActionDefinition | null
  readonly startEndpointId: string | null
}): readonly PresentationProjectionDiagnostic[] {
  if (!actionId && !previewAction) {
    return []
  }

  return [
    ...diagnoseProcessPreviewAction({ actionId, previewAction, source }),
    ...diagnoseProcessPreviewFlow({
      flow,
      flowId,
      inputForm,
      inputFormActionPlacement,
      previewAction,
      previewDefinition,
      previewEndpointId,
      promptView,
      source,
      startAction,
      startEndpointId,
    }),
    ...diagnoseProcessPreviewDismissAction({
      dismissAction,
      promptView,
      source,
    }),
  ]
}

function diagnoseProcessPreviewAction({
  actionId,
  previewAction,
  source,
}: {
  readonly actionId?: string | null
  readonly previewAction: ActionDefinition | null
  readonly source: string
}) {
  if (!actionId || previewAction) {
    return []
  }

  return [
    createProcessPreviewRuntimeDiagnostic({
      details: { actionId },
      id: `process-preview.${actionId}.missing-action`,
      message:
        `Document action runtime profile references process preview action '${actionId}', ` +
        'but the action is not placed or cannot be interpreted as a document process preview.',
      severity: 'error',
      source,
      subject: {
        id: actionId,
        kind: 'action',
      },
    }),
  ]
}

function diagnoseProcessPreviewFlow({
  flow,
  flowId,
  inputForm,
  inputFormActionPlacement,
  previewAction,
  previewDefinition,
  previewEndpointId,
  promptView,
  source,
  startAction,
  startEndpointId,
}: {
  readonly flow: FlowDefinition | null
  readonly flowId: string
  readonly inputForm: InputFormDefinition | null
  readonly inputFormActionPlacement: ActionPlacementDefinition | null
  readonly previewAction: ActionDefinition | null
  readonly previewDefinition: PromptDocumentPreviewDefinition | null
  readonly previewEndpointId: string | null
  readonly promptView: ViewDefinition | null
  readonly source: string
  readonly startAction: ActionDefinition | null
  readonly startEndpointId: string | null
}) {
  if (!previewAction) {
    return []
  }

  const diagnostics: PresentationProjectionDiagnostic[] = []
  if (!flow) {
    diagnostics.push(createProcessPreviewRuntimeDiagnostic({
      details: { actionId: previewAction.Id, flowId },
      id: `process-preview.${previewAction.Id}.missing-flow`,
      message:
        `Process preview action '${previewAction.Name}' references flow '${flowId}', ` +
        'but that flow is not present in the presentation module.',
      severity: 'error',
      source,
      subject: { id: previewAction.Id, kind: 'action', name: previewAction.Name },
    }))
    return diagnostics
  }

  if (!promptView) {
    diagnostics.push(createProcessPreviewRuntimeDiagnostic({
      details: { actionId: previewAction.Id, flowId: flow.Id },
      id: `process-preview.${previewAction.Id}.missing-prompt-view`,
      message:
        `Process preview flow '${flow.Name}' does not resolve a prompt view for ` +
        `action '${previewAction.Name}'.`,
      severity: 'error',
      source,
      subject: { id: flow.Id, kind: 'flow', name: flow.Name },
    }))
  }

  if (!inputForm) {
    diagnostics.push(createProcessPreviewRuntimeDiagnostic({
      details: { actionId: previewAction.Id, flowId: flow.Id, viewId: promptView?.Id },
      id: `process-preview.${previewAction.Id}.missing-input-form`,
      message:
        `Process preview prompt '${promptView?.Name ?? flow.Name}' does not resolve ` +
        'an input form.',
      severity: 'error',
      source,
      subject: { id: promptView?.Id ?? flow.Id, kind: promptView ? 'view' : 'flow' },
    }))
  }

  if (inputForm && !inputFormActionPlacement) {
    diagnostics.push(createProcessPreviewRuntimeDiagnostic({
      details: { actionId: previewAction.Id, inputFormId: inputForm.Id },
      id: `process-preview.${previewAction.Id}.missing-input-form-action`,
      message:
        `Input form '${inputForm.Name}' does not place process preview action ` +
        `'${previewAction.Name}'.`,
      severity: 'error',
      source,
      subject: { id: inputForm.Id, kind: 'input-form', name: inputForm.Name },
    }))
  }

  if (!previewDefinition) {
    diagnostics.push(createProcessPreviewRuntimeDiagnostic({
      details: { actionId: previewAction.Id, flowId: flow.Id, viewId: promptView?.Id },
      id: `process-preview.${previewAction.Id}.missing-preview-surface`,
      message:
        `Process preview prompt '${promptView?.Name ?? flow.Name}' does not declare ` +
        'a document preview surface.',
      severity: 'error',
      source,
      subject: { id: promptView?.Id ?? flow.Id, kind: promptView ? 'view' : 'flow' },
    }))
  }

  if (!previewEndpointId) {
    diagnostics.push(createProcessPreviewRuntimeDiagnostic({
      details: { actionId: previewAction.Id, inputFormId: inputForm?.Id },
      id: `process-preview.${previewAction.Id}.missing-preview-endpoint`,
      message:
        `No endpoint binding is registered for process preview action ` +
        `'${previewAction.Name}'.`,
      severity: 'error',
      source,
      subject: { id: previewAction.Id, kind: 'action', name: previewAction.Name },
    }))
  }

  if (!startAction) {
    diagnostics.push(createProcessPreviewRuntimeDiagnostic({
      details: { actionId: previewAction.Id, flowId: flow.Id, viewId: promptView?.Id },
      id: `process-preview.${previewAction.Id}.missing-start-action`,
      message:
        `Process preview prompt '${promptView?.Name ?? flow.Name}' does not declare ` +
        'a process start action.',
      severity: 'error',
      source,
      subject: { id: promptView?.Id ?? flow.Id, kind: promptView ? 'view' : 'flow' },
    }))
  } else if (!startEndpointId) {
    diagnostics.push(createProcessPreviewRuntimeDiagnostic({
      details: { actionId: startAction.Id, flowId: flow.Id },
      id: `process-preview.${startAction.Id}.missing-start-endpoint`,
      message:
        `No endpoint binding is registered for process start action ` +
        `'${startAction.Name}'.`,
      severity: 'error',
      source,
      subject: { id: startAction.Id, kind: 'action', name: startAction.Name },
    }))
  }

  return diagnostics
}

function diagnoseProcessPreviewDismissAction({
  dismissAction,
  promptView,
  source,
}: {
  readonly dismissAction: ActionDefinition | null
  readonly promptView: ViewDefinition | null
  readonly source: string
}) {
  if (!promptView || dismissAction) {
    return []
  }

  return [
    createProcessPreviewRuntimeDiagnostic({
      details: { viewId: promptView.Id },
      id: `process-preview.${promptView.Id}.missing-dismiss-action`,
      message:
        `Process preview prompt '${promptView.Name}' does not declare a dismiss action.`,
      severity: 'warning',
      source,
      subject: { id: promptView.Id, kind: 'view', name: promptView.Name },
    }),
  ]
}

function createProcessPreviewRuntimeDiagnostic({
  details,
  id,
  message,
  severity,
  source,
  subject,
}: PresentationProjectionDiagnostic): PresentationProjectionDiagnostic {
  return {
    details,
    id,
    message,
    severity,
    source,
    subject,
  }
}
