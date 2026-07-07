import { useEffect, useMemo, useRef, useState } from 'react'

import {
  findPresentationAction,
  findPresentationFlow,
  findPresentationView,
  readObjectPath,
  type ActionDefinition,
  type FlowDefinition,
  type FlowStateDefinition,
  type FlowTransitionDefinition,
  type PresentationModuleDefinition,
  type ViewDefinition,
} from '@cohesivesystems/presentation-core'
import {
  flowStateKindLabels,
  flowStateKinds,
  preparationKinds,
} from '@cohesivesystems/presentation-contracts'

export interface PresentationFlowInstance {
  readonly data: Readonly<Record<string, unknown>>
  readonly flowId: string
  readonly stateId: string
}

export interface PresentationFlowStartOptions {
  readonly actionId?: string | null
  readonly data?: Readonly<Record<string, unknown>>
  readonly event?: string | null
  readonly flowId: string
  readonly stateId?: string | null
}

export interface PresentationFlowDispatchOptions {
  readonly actionId?: string | null
  readonly data?: Readonly<Record<string, unknown>>
}

export interface PresentationFlowTransitionToOptions {
  readonly data?: Readonly<Record<string, unknown>>
  readonly flowId?: string | null
}

export interface PresentationFlowTransitionContext {
  readonly flow: FlowDefinition
  readonly fromState: FlowStateDefinition | null
  readonly instance: PresentationFlowInstance
  readonly toState: FlowStateDefinition
  readonly transition: FlowTransitionDefinition | null
}

export interface PresentationFlowUnhandledEventContext {
  readonly actionId: string | null
  readonly event: string
  readonly flow: FlowDefinition
  readonly instance: PresentationFlowInstance
}

export interface UsePresentationFlowRuntimeOptions {
  readonly initialData?: Readonly<Record<string, unknown>>
  readonly initialFlowId?: string | null
  readonly initialStateId?: string | null
  readonly module: PresentationModuleDefinition | null
  readonly onTransition?: (context: PresentationFlowTransitionContext) => void
  readonly onUnhandledEvent?: (context: PresentationFlowUnhandledEventContext) => void
}

export interface PresentationActionPreparedFlow {
  readonly canCancel: boolean
  readonly flowId: string
  readonly promptViewId: string | null
  readonly requiresExplicitCommit: boolean
}

export interface PresentationFlowRuntimeSnapshot {
  readonly activeFlow: FlowDefinition | null
  readonly activeInstance: PresentationFlowInstance | null
  readonly activeState: FlowStateDefinition | null
  readonly activeView: ViewDefinition | null
  readonly clearFlow: () => void
  readonly dispatchAction: (actionId: string, options?: PresentationFlowDispatchOptions) => boolean
  readonly dispatchEvent: (event: string, options?: PresentationFlowDispatchOptions) => boolean
  readonly dispatchTransition: (transitionId: string, options?: PresentationFlowDispatchOptions) => boolean
  readonly isFlowActive: (flowId: string) => boolean
  readonly isStateActive: (stateId: string) => boolean
  readonly startFlow: (options: PresentationFlowStartOptions) => boolean
  readonly transitionTo: (stateId: string, options?: PresentationFlowTransitionToOptions) => boolean
}

export interface PresentationFlowRuntimeEntry {
  readonly flow: FlowDefinition
  readonly instance: PresentationFlowInstance
  readonly state: FlowStateDefinition
  readonly view: ViewDefinition | null
}

export interface PresentationFlowRuntimeRegistrySnapshot {
  readonly activeEntries: readonly PresentationFlowRuntimeEntry[]
  readonly clearFlow: (flowId: string) => void
  readonly getRuntime: (flowId: string) => PresentationFlowRuntimeSnapshot
}

/**
 * Tests flow-state kind values across generated numeric constants and backend
 * string enum serialization.
 */
export function isPresentationFlowStateKind(
  value: unknown,
  ...kinds: readonly (number | string)[]
) {
  return kinds.some((kind) => matchesFlowStateKind(value, kind))
}

/**
 * Returns true when a flow instance is active for a specific data key/value and
 * the active state is a visible prompt-like state.
 */
export function isPresentationFlowSurfaceOpenForData({
  dataKey,
  dataValue,
  flowId,
  runtime,
}: {
  readonly dataKey: string
  readonly dataValue: string | null
  readonly flowId: string
  readonly runtime: PresentationFlowRuntimeSnapshot
}) {
  if (!dataValue || !runtime.isFlowActive(flowId) || !runtime.activeState?.ViewId) {
    return false
  }

  return runtime.activeInstance?.data[dataKey] === dataValue &&
    isPresentationFlowStateKind(
      runtime.activeState.Kind,
      flowStateKinds.pending,
      flowStateKinds.prompt,
      flowStateKinds.error,
    )
}

/** Finds the first visible prompt/pending/error state in a flow definition. */
export function findPresentationFlowSurfaceState(
  flow: FlowDefinition | null | undefined,
) {
  return flow?.States.find((state) =>
    Boolean(state.ViewId) &&
    isPresentationFlowStateKind(
      state.Kind,
      flowStateKinds.pending,
      flowStateKinds.prompt,
      flowStateKinds.error,
    ),
  ) ?? null
}

/** Finds the first visible error state in a flow definition. */
export function findPresentationFlowErrorSurfaceState(
  flow: FlowDefinition | null | undefined,
) {
  return flow?.States.find((state) =>
    Boolean(state.ViewId) &&
    isPresentationFlowStateKind(state.Kind, flowStateKinds.error),
  ) ?? null
}

/** Finds the first state matching one of the requested semantic flow kinds. */
export function findPresentationFlowStateByKind(
  flow: FlowDefinition | null | undefined,
  ...kinds: readonly (number | string)[]
) {
  return flow?.States.find((state) =>
    isPresentationFlowStateKind(state.Kind, ...kinds),
  ) ?? null
}

/**
 * Advances an active runtime instance to a state with the requested semantic
 * kind, preferring a valid declared transition from the current state.
 */
export function advancePresentationFlowToStateKind({
  allowStateFallback = true,
  data,
  flow,
  runtime,
  stateKinds,
}: {
  readonly allowStateFallback?: boolean
  readonly data?: Readonly<Record<string, unknown>>
  readonly flow: FlowDefinition | null | undefined
  readonly runtime: PresentationFlowRuntimeSnapshot
  readonly stateKinds: readonly (number | string)[]
}) {
  if (!flow || !runtime.activeInstance) {
    return false
  }

  const transition = findPresentationFlowTransitionToStateKind({
    data: {
      ...runtime.activeInstance.data,
      ...(data ?? {}),
    },
    flow,
    fromStateId: runtime.activeInstance.stateId,
    stateKinds,
  })
  if (transition && runtime.dispatchTransition(transition.Id, { data })) {
    return true
  }

  if (!allowStateFallback) {
    return false
  }

  const state = findPresentationFlowStateByKind(flow, ...stateKinds)
  return state
    ? runtime.transitionTo(state.Id, { data, flowId: flow.Id })
    : false
}

/** Finds a valid transition from a state to any state with the requested kind. */
export function findPresentationFlowTransitionToStateKind({
  data,
  flow,
  fromStateId,
  stateKinds,
}: {
  readonly data: Readonly<Record<string, unknown>>
  readonly flow: FlowDefinition
  readonly fromStateId: string
  readonly stateKinds: readonly (number | string)[]
}) {
  const targetStateIds = new Set(
    flow.States
      .filter((state) => isPresentationFlowStateKind(state.Kind, ...stateKinds))
      .map((state) => state.Id),
  )
  return flow.Transitions.find((transition) =>
    transition.FromStateId === fromStateId &&
    targetStateIds.has(transition.ToStateId) &&
    matchesPresentationFlowTransitionGuard(transition, data),
  ) ?? null
}

/** Finds a valid action/event transition from the given flow state. */
export function findPresentationFlowTransition({
  actionId,
  data,
  event,
  flow,
  fromStateId,
}: {
  readonly actionId?: string | null
  readonly data: Readonly<Record<string, unknown>>
  readonly event?: string | null
  readonly flow: FlowDefinition
  readonly fromStateId: string
}) {
  const candidates = flow.Transitions.filter((transition) =>
    transition.FromStateId === fromStateId &&
    (
      (event ? transition.Event === event : false) ||
      (actionId ? transition.ActionId === actionId : false)
    ),
  )

  return candidates.find((transition) =>
    matchesPresentationFlowTransitionGuard(transition, data),
  ) ?? null
}

/**
 * Interprets client-resident Presentation IR flows into local React state.
 *
 * The runtime deliberately stays semantic: it tracks the active flow, resolves
 * transitions by event or action id, exposes the active prompt view, and leaves
 * endpoint execution, persistence, and custom rendering to host-specific escape
 * hatches. That lets app code migrate from imperative conditionals toward IR
 * projection without requiring every flow side effect to be generalized first.
 */
export function usePresentationFlowRuntime({
  initialData,
  initialFlowId,
  initialStateId,
  module,
  onTransition,
  onUnhandledEvent,
}: UsePresentationFlowRuntimeOptions): PresentationFlowRuntimeSnapshot {
  const [instance, setInstance] = useState<PresentationFlowInstance | null>(() => {
    if (!initialFlowId) {
      return null
    }

    const flow = findPresentationFlow<FlowDefinition>(module, initialFlowId)
    if (!flow) {
      return null
    }

    return {
      data: initialData ?? {},
      flowId: initialFlowId,
      stateId: initialStateId ?? flow.InitialStateId,
    }
  })

  const activeFlow = useMemo(
    () =>
      instance
        ? findPresentationFlow<FlowDefinition>(module, instance.flowId)
        : null,
    [instance, module],
  )
  const activeState = useMemo(
    () => activeFlow?.States.find((state) => state.Id === instance?.stateId) ?? null,
    [activeFlow, instance],
  )
  const activeView = useMemo(
    () =>
      activeState?.ViewId
        ? findPresentationView<ViewDefinition>(module, activeState.ViewId)
        : null,
    [activeState, module],
  )

  function startFlow(options: PresentationFlowStartOptions) {
    const flow = findPresentationFlow<FlowDefinition>(module, options.flowId)
    if (!flow) {
      return false
    }

    const data = options.data ?? {}
    const fromStateId = options.stateId ?? flow.InitialStateId
    const transition = options.stateId
      ? null
      : resolveFlowTransition({
          actionId: options.actionId,
          data,
          event: options.event,
          flow,
          fromStateId,
        })
    if (!options.stateId && (options.event || options.actionId) && !transition) {
      return false
    }
    const toStateId = transition?.ToStateId ?? fromStateId
    const toState = flow.States.find((state) => state.Id === toStateId)
    if (!toState) {
      return false
    }

    const nextInstance = {
      data,
      flowId: flow.Id,
      stateId: toState.Id,
    } satisfies PresentationFlowInstance
    setInstance(nextInstance)
    onTransition?.({
      flow,
      fromState: flow.States.find((state) => state.Id === fromStateId) ?? null,
      instance: nextInstance,
      toState,
      transition,
    })
    return true
  }

  function dispatchEvent(event: string, options?: PresentationFlowDispatchOptions) {
    if (!instance || !activeFlow) {
      return false
    }

    const data = { ...instance.data, ...(options?.data ?? {}) }
    const transition = resolveFlowTransition({
      actionId: options?.actionId,
      data,
      event,
      flow: activeFlow,
      fromStateId: instance.stateId,
    })
    if (!transition) {
      onUnhandledEvent?.({
        actionId: options?.actionId ?? null,
        event,
        flow: activeFlow,
        instance,
      })
      return false
    }

    return applyFlowState(activeFlow, transition.ToStateId, {
      data,
      transition,
    })
  }

  function dispatchAction(actionId: string, options?: PresentationFlowDispatchOptions) {
    const action = findPresentationAction<ActionDefinition>(module, actionId)
    return dispatchEvent(action?.Binding.Id ?? actionId, {
      ...options,
      actionId,
    })
  }

  function dispatchTransition(transitionId: string, options?: PresentationFlowDispatchOptions) {
    if (!instance || !activeFlow) {
      return false
    }

    const data = { ...instance.data, ...(options?.data ?? {}) }
    const transition = activeFlow.Transitions.find((candidate) =>
      candidate.Id === transitionId &&
      candidate.FromStateId === instance.stateId &&
      matchesPresentationFlowTransitionGuard(candidate, data),
    )
    if (!transition) {
      return false
    }

    return applyFlowState(activeFlow, transition.ToStateId, {
      data,
      transition,
    })
  }

  function transitionTo(stateId: string, options?: PresentationFlowTransitionToOptions) {
    const flowId = options?.flowId ?? instance?.flowId
    const flow = flowId ? findPresentationFlow<FlowDefinition>(module, flowId) : null
    if (!flow) {
      return false
    }

    return applyFlowState(flow, stateId, {
      data: {
        ...(instance?.flowId === flow.Id ? instance.data : {}),
        ...(options?.data ?? {}),
      },
      transition: null,
    })
  }

  function applyFlowState(
    flow: FlowDefinition,
    stateId: string,
    options: {
      readonly data: Readonly<Record<string, unknown>>
      readonly transition: FlowTransitionDefinition | null
    },
  ) {
    const toState = flow.States.find((state) => state.Id === stateId)
    if (!toState) {
      return false
    }

    const nextInstance = {
      data: options.data,
      flowId: flow.Id,
      stateId: toState.Id,
    } satisfies PresentationFlowInstance
    const fromState = instance?.flowId === flow.Id
      ? flow.States.find((state) => state.Id === instance.stateId) ?? null
      : null
    setInstance(nextInstance)
    onTransition?.({
      flow,
      fromState,
      instance: nextInstance,
      toState,
      transition: options.transition,
    })
    return true
  }

  return {
    activeFlow,
    activeInstance: instance,
    activeState,
    activeView,
    clearFlow: () => setInstance(null),
    dispatchAction,
    dispatchEvent,
    dispatchTransition,
    isFlowActive: (flowId) => instance?.flowId === flowId,
    isStateActive: (stateId) => instance?.stateId === stateId,
    startFlow,
    transitionTo,
  }
}

/**
 * Maintains the active instances for all client-resident Presentation IR flows
 * in a host. Callers can still ask for a flow-scoped runtime, but prompt
 * projection can iterate active entries instead of wiring each flow manually.
 */
export function usePresentationFlowRuntimeRegistry({
  module,
  onTransition,
  onUnhandledEvent,
}: Pick<UsePresentationFlowRuntimeOptions, 'module' | 'onTransition' | 'onUnhandledEvent'>): PresentationFlowRuntimeRegistrySnapshot {
  const [instances, setInstances] = useState<Readonly<Record<string, PresentationFlowInstance>>>({})
  const instancesRef = useRef(instances)

  useEffect(() => {
    instancesRef.current = instances
  }, [instances])

  const activeEntries = useMemo(
    () =>
      Object.values(instances)
        .map((instance) => resolvePresentationFlowRuntimeEntry(module, instance))
        .filter((entry): entry is PresentationFlowRuntimeEntry => entry !== null),
    [instances, module],
  )

  function setFlowInstance(instance: PresentationFlowInstance | null, flowId: string) {
    const current = instancesRef.current
    const next = { ...current }
    if (instance) {
      next[flowId] = instance
    } else {
      delete next[flowId]
    }
    instancesRef.current = next
    setInstances(next)
  }

  function clearFlow(flowId: string) {
    setFlowInstance(null, flowId)
  }

  function getRuntime(flowId: string): PresentationFlowRuntimeSnapshot {
    const instance = instances[flowId] ?? null
    const activeFlow = instance
      ? findPresentationFlow<FlowDefinition>(module, instance.flowId)
      : null
    const activeState =
      activeFlow?.States.find((state) => state.Id === instance?.stateId) ?? null
    const activeView =
      activeState?.ViewId
        ? findPresentationView<ViewDefinition>(module, activeState.ViewId)
        : null

    function startFlow(options: PresentationFlowStartOptions) {
      const flow = findPresentationFlow<FlowDefinition>(module, options.flowId)
      if (!flow) {
        return false
      }

      const data = options.data ?? {}
      const fromStateId = options.stateId ?? flow.InitialStateId
      const transition = options.stateId
        ? null
        : resolveFlowTransition({
            actionId: options.actionId,
            data,
            event: options.event,
            flow,
            fromStateId,
          })
      if (!options.stateId && (options.event || options.actionId) && !transition) {
        return false
      }

      const toStateId = transition?.ToStateId ?? fromStateId
      return applyRegistryFlowState(flow, toStateId, {
        data,
        previousInstance: instancesRef.current[flow.Id] ?? null,
        transition,
      })
    }

    function dispatchEvent(event: string, options?: PresentationFlowDispatchOptions) {
      const currentInstance = instancesRef.current[flowId] ?? null
      const flow = currentInstance
        ? findPresentationFlow<FlowDefinition>(module, currentInstance.flowId)
        : null
      if (!currentInstance || !flow) {
        return false
      }

      const data = { ...currentInstance.data, ...(options?.data ?? {}) }
      const transition = resolveFlowTransition({
        actionId: options?.actionId,
        data,
        event,
        flow,
        fromStateId: currentInstance.stateId,
      })
      if (!transition) {
        onUnhandledEvent?.({
          actionId: options?.actionId ?? null,
          event,
          flow,
          instance: currentInstance,
        })
        return false
      }

      return applyRegistryFlowState(flow, transition.ToStateId, {
        data,
        previousInstance: currentInstance,
        transition,
      })
    }

    function dispatchAction(actionId: string, options?: PresentationFlowDispatchOptions) {
      const action = findPresentationAction<ActionDefinition>(module, actionId)
      return dispatchEvent(action?.Binding.Id ?? actionId, {
        ...options,
        actionId,
      })
    }

    function dispatchTransition(
      transitionId: string,
      options?: PresentationFlowDispatchOptions,
    ) {
      const currentInstance = instancesRef.current[flowId] ?? null
      const flow = currentInstance
        ? findPresentationFlow<FlowDefinition>(module, currentInstance.flowId)
        : null
      if (!currentInstance || !flow) {
        return false
      }

      const data = { ...currentInstance.data, ...(options?.data ?? {}) }
      const transition = flow.Transitions.find((candidate) =>
        candidate.Id === transitionId &&
        candidate.FromStateId === currentInstance.stateId &&
        matchesPresentationFlowTransitionGuard(candidate, data),
      )
      if (!transition) {
        return false
      }

      return applyRegistryFlowState(flow, transition.ToStateId, {
        data,
        previousInstance: currentInstance,
        transition,
      })
    }

    function transitionTo(stateId: string, options?: PresentationFlowTransitionToOptions) {
      const currentInstance = instancesRef.current[flowId] ?? null
      const targetFlowId = options?.flowId ?? currentInstance?.flowId ?? flowId
      const flow = findPresentationFlow<FlowDefinition>(module, targetFlowId)
      if (!flow) {
        return false
      }

      return applyRegistryFlowState(flow, stateId, {
        data: {
          ...(currentInstance?.flowId === flow.Id ? currentInstance.data : {}),
          ...(options?.data ?? {}),
        },
        previousInstance: currentInstance?.flowId === flow.Id ? currentInstance : null,
        transition: null,
      })
    }

    function applyRegistryFlowState(
      flow: FlowDefinition,
      stateId: string,
      options: {
        readonly data: Readonly<Record<string, unknown>>
        readonly previousInstance: PresentationFlowInstance | null
        readonly transition: FlowTransitionDefinition | null
      },
    ) {
      const toState = flow.States.find((state) => state.Id === stateId)
      if (!toState) {
        return false
      }

      const nextInstance = {
        data: options.data,
        flowId: flow.Id,
        stateId: toState.Id,
      } satisfies PresentationFlowInstance
      setFlowInstance(nextInstance, flow.Id)
      onTransition?.({
        flow,
        fromState: options.previousInstance
          ? flow.States.find((state) => state.Id === options.previousInstance?.stateId) ?? null
          : null,
        instance: nextInstance,
        toState,
        transition: options.transition,
      })
      return true
    }

    return {
      activeFlow,
      activeInstance: instance,
      activeState,
      activeView,
      clearFlow: () => clearFlow(flowId),
      dispatchAction,
      dispatchEvent,
      dispatchTransition,
      isFlowActive: (candidateFlowId) =>
        instancesRef.current[flowId]?.flowId === candidateFlowId,
      isStateActive: (stateId) => instancesRef.current[flowId]?.stateId === stateId,
      startFlow,
      transitionTo,
    }
  }

  return {
    activeEntries,
    clearFlow,
    getRuntime,
  }
}

export function resolvePresentationActionPreparedFlow(
  action: ActionDefinition | null,
): PresentationActionPreparedFlow | null {
  const preparation = action?.Preparation
  if (!preparation?.FlowId || !isFlowPreparationKind(preparation.Kind)) {
    return null
  }

  return {
    canCancel: preparation.CanCancel,
    flowId: preparation.FlowId,
    promptViewId: preparation.PromptViewId ?? null,
    requiresExplicitCommit: preparation.RequiresExplicitCommit,
  }
}

function resolvePresentationFlowRuntimeEntry(
  module: PresentationModuleDefinition | null,
  instance: PresentationFlowInstance,
): PresentationFlowRuntimeEntry | null {
  const flow = findPresentationFlow<FlowDefinition>(module, instance.flowId)
  if (!flow) {
    return null
  }

  const state = flow.States.find((candidate) => candidate.Id === instance.stateId)
  if (!state) {
    return null
  }

  return {
    flow,
    instance,
    state,
    view: state.ViewId
      ? findPresentationView<ViewDefinition>(module, state.ViewId)
      : null,
  }
}

function resolveFlowTransition({
  actionId,
  data,
  event,
  flow,
  fromStateId,
}: {
  readonly actionId?: string | null
  readonly data: Readonly<Record<string, unknown>>
  readonly event?: string | null
  readonly flow: FlowDefinition
  readonly fromStateId: string
}) {
  return findPresentationFlowTransition({
    actionId,
    data,
    event,
    flow,
    fromStateId,
  })
}

export function matchesPresentationFlowTransitionGuard(
  transition: FlowTransitionDefinition,
  data: Readonly<Record<string, unknown>>,
) {
  if (!transition.Guard) {
    return true
  }

  const match = transition.Guard.match(
    /^\s*([A-Za-z0-9_.]+)\s*==\s*(true|false|null|'[^']*'|"[^"]*"|-?\d+(?:\.\d+)?)\s*$/,
  )
  if (!match) {
    return false
  }

  return readObjectPath(data, match[1]) === parseGuardLiteral(match[2])
}

function parseGuardLiteral(value: string) {
  if (value === 'true') {
    return true
  }
  if (value === 'false') {
    return false
  }
  if (value === 'null') {
    return null
  }
  if (
    (value.startsWith("'") && value.endsWith("'")) ||
    (value.startsWith('"') && value.endsWith('"'))
  ) {
    return value.slice(1, -1)
  }

  const numberValue = Number(value)
  return Number.isFinite(numberValue) ? numberValue : value
}

function isFlowPreparationKind(value: unknown) {
  return value === preparationKinds.prompt ||
    value === preparationKinds.previewFlow ||
    String(value).toLowerCase() === 'prompt' ||
    String(value).toLowerCase() === 'previewflow'
}

function matchesFlowStateKind(value: unknown, kind: string | number) {
  const label = typeof kind === 'number'
    ? flowStateKindLabels[kind as keyof typeof flowStateKindLabels]
    : kind
  const normalizedValue = String(value).toLocaleLowerCase()
  return (
    value === kind ||
    normalizedValue === String(kind).toLocaleLowerCase() ||
    normalizedValue === label?.toLocaleLowerCase()
  )
}
