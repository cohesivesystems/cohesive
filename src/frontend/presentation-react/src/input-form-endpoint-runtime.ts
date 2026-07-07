import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'

import {
  findPresentationAction,
  type ActionDefinition,
  type InputFormDefinition,
  type PresentationValueDefinition,
  type PresentationModuleDefinition,
  resolvePresentationActionEndpointBinding,
  readObjectPath,
  type ProjectedInputFormActionContext,
  type ProjectedInputFormRuntime,
  writeObjectPath,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationActionEndpointExecutionRequest,
  PresentationActionEndpointExecutor,
} from './presentation-action-runtime'
import {
  inputFormTargetKinds,
  presentationValueKinds,
} from '@cohesivesystems/presentation-contracts'

type InputFormEndpointRequestOption<TValue extends object, TOption> =
  | TOption
  | ((context: ProjectedInputFormEndpointRequestContext<TValue>) => TOption)

/**
 * Request-time context used to lower a projected input-form value into a concrete
 * endpoint execution request.
 *
 * @typeParam TValue - Shape of the projected input form draft value.
 */
export interface ProjectedInputFormEndpointRequestContext<TValue extends object = object> {
  /** Presentation action resolved from the submitted form placement, when available. */
  readonly action: ActionDefinition | null

  /** Full action context emitted by ProjectedInputForm when the user submits the form. */
  readonly actionContext: ProjectedInputFormActionContext<TValue>

  /** Endpoint selected from the action target binding or the input-form target. */
  readonly endpointId: string

  /** Backend-declared input form being submitted. */
  readonly inputForm: InputFormDefinition

  /** Presentation module that owns the form, actions, and target bindings. */
  readonly module: PresentationModuleDefinition | null

  /** Current draft value owned by the projected form runtime. */
  readonly value: TValue
}

/**
 * Success-time context supplied after an endpoint-backed input form completes.
 *
 * @typeParam TValue - Shape of the submitted form value.
 * @typeParam TResult - Result returned by the endpoint executor.
 */
export interface ProjectedInputFormEndpointSuccessContext<
  TValue extends object = object,
  TResult = unknown,
> extends ProjectedInputFormEndpointRequestContext<TValue> {
  /** React Query client used by the runtime for optional cache writes or invalidation. */
  readonly queryClient: QueryClient

  /** Concrete request sent to the endpoint executor. */
  readonly request: PresentationActionEndpointExecutionRequest

  /** Endpoint result returned by the application API adapter. */
  readonly result: TResult
}

/**
 * Options for projecting an InputFormDefinition with an EndpointRequest target
 * into mutable React state plus an executable ProjectedInputFormRuntime.
 *
 * @typeParam TValue - Shape of the form draft and lowered endpoint body.
 * @typeParam TResult - Result returned by the endpoint executor.
 */
export interface UseProjectedInputFormEndpointRuntimeOptions<
  TValue extends object = object,
  TResult = unknown,
> {
  /** Presentation target component set used when resolving action endpoint bindings. */
  readonly componentSet?: string | null

  /** Optional data source specialization used when resolving an action endpoint binding. */
  readonly dataSourceId?: string | null

  /** Maps presentation data source ids to query keys for cache invalidation. */
  readonly dataSourceQueryKey?: (dataSourceId: string) => readonly unknown[]

  /** Optional baseline body merged before form fields are lowered into the request body. */
  readonly defaultBody?: InputFormEndpointRequestOption<TValue, Readonly<Record<string, unknown>>>

  /** Initial form value; field DefaultValue declarations are applied over this value. */
  readonly defaultValue?: TValue | (() => TValue)

  /** Runtime context used by form-level DefaultValues bindings, such as resource or route data. */
  readonly defaultValueSources?: Readonly<Record<string, unknown>>

  /** Application API adapter used to execute the resolved endpoint. */
  readonly executeEndpoint: PresentationActionEndpointExecutor

  /** Input form to project. Null yields a null runtime. */
  readonly inputForm: InputFormDefinition | null

  /** Data source ids to invalidate after success; defaults to action or form target policy. */
  readonly invalidateDataSourceIds?:
    | readonly string[]
    | ((context: ProjectedInputFormEndpointSuccessContext<TValue, TResult>) => readonly string[])

  /** Presentation module that owns form definitions, actions, and target bindings. */
  readonly module: PresentationModuleDefinition | null

  /**
   * Optional full request preparer. When omitted, the runtime lowers the form
   * value into request.body and resolves routeParameters from options.
   */
  readonly prepareRequest?: (
    context: ProjectedInputFormEndpointRequestContext<TValue>,
  ) => PresentationActionEndpointExecutionRequest

  /** Optional result side effect that runs before cache invalidation and onSuccess. */
  readonly processResult?: (context: ProjectedInputFormEndpointSuccessContext<TValue, TResult>) => void

  /** Route parameters passed to the endpoint executor, such as entity ids. */
  readonly routeParameters?: InputFormEndpointRequestOption<
    TValue,
    Readonly<Record<string, string | null | undefined>>
  >

  /** Optional direct cache write for endpoint results before invalidation runs. */
  readonly setResultQueryData?: (
    context: ProjectedInputFormEndpointSuccessContext<TValue, TResult>,
  ) => void

  /** Identity of the current form state instance; changing it resets draft value. */
  readonly stateKey?: string | null

  /** Final success callback after result handling and invalidation. */
  readonly onSuccess?: (context: ProjectedInputFormEndpointSuccessContext<TValue, TResult>) => void
}

/**
 * Runtime state returned by useProjectedInputFormEndpointRuntime.
 *
 * @typeParam TValue - Shape of the form draft and lowered endpoint body.
 * @typeParam TResult - Result returned by the endpoint executor.
 */
export interface ProjectedInputFormEndpointRuntime<
  TValue extends object = object,
  TResult = unknown,
> {
  /** Endpoint currently resolved for the form's primary action, when available. */
  readonly endpointId: string | null

  /** Last endpoint execution error, if any. */
  readonly error: unknown

  /** Whether the endpoint request is currently in flight. */
  readonly isPending: boolean

  /** Whether the most recent endpoint request completed successfully. */
  readonly isSuccess: boolean

  /** Clears endpoint state and resets the form draft to its initial value. */
  readonly reset: () => void

  /** Clears only the last endpoint execution state, preserving the current draft value. */
  readonly resetExecution: () => void

  /** Resets only the form draft value. */
  readonly resetValue: () => void

  /** Most recent endpoint result. */
  readonly result: TResult | undefined

  /** Runtime consumed directly by ProjectedInputForm; null when the form is unsupported. */
  readonly runtime: ProjectedInputFormRuntime<TValue> | null

  /** Current draft form value. */
  readonly value: TValue
}

interface InputFormEndpointMutationResult<TValue extends object, TResult> {
  readonly context: ProjectedInputFormEndpointRequestContext<TValue>
  readonly request: PresentationActionEndpointExecutionRequest
  readonly result: TResult
}

/**
 * Creates a ProjectedInputFormRuntime for input forms whose target kind is
 * EndpointRequest.
 *
 * The hook is intentionally target-generic: it resolves the concrete endpoint
 * from action bindings or the input-form target, owns keyed draft state, lowers
 * fields by ValuePath into an endpoint body, executes through the provided API
 * adapter, and applies optional cache/result policies.
 *
 * @typeParam TValue - Shape of the form draft and lowered endpoint body.
 * @typeParam TResult - Result returned by the endpoint executor.
 */
export function useProjectedInputFormEndpointRuntime<
  TValue extends object = object,
  TResult = unknown,
>({
  componentSet = 'cohesive.presentation.react',
  dataSourceId,
  dataSourceQueryKey,
  defaultBody,
  defaultValue,
  defaultValueSources,
  executeEndpoint,
  inputForm,
  invalidateDataSourceIds,
  module,
  onSuccess,
  prepareRequest,
  processResult,
  routeParameters,
  setResultQueryData,
  stateKey,
}: UseProjectedInputFormEndpointRuntimeOptions<TValue, TResult>): ProjectedInputFormEndpointRuntime<TValue, TResult> {
  const queryClient = useQueryClient()
  const initialValue = useMemo(
    () =>
      createInitialInputFormValue({
        defaultValue: resolveInitialValue(defaultValue),
        inputForm,
        sources: defaultValueSources,
      }),
    [defaultValue, defaultValueSources, inputForm],
  )
  const effectiveStateKey = stateKey ?? inputForm?.Id ?? ''
  const [state, setState] = useState<{
    readonly key: string
    readonly value: TValue
  }>(() => ({
    key: effectiveStateKey,
    value: initialValue,
  }))
  const value = state.key === effectiveStateKey ? state.value : initialValue
  const setRuntimeValue: ProjectedInputFormRuntime<TValue>['setValue'] = (update) => {
    setState((current) => {
      const currentValue = current.key === effectiveStateKey ? current.value : initialValue
      const nextValue = typeof update === 'function'
        ? (update as (value: TValue) => TValue)(currentValue)
        : update

      return {
        key: effectiveStateKey,
        value: nextValue,
      }
    })
  }

  const endpointId = useMemo(
    () =>
      inputForm
        ? resolveInputFormEndpointId({
            actionId: inputForm.Actions[0]?.ActionId ?? null,
            componentSet,
            dataSourceId: dataSourceId ?? inputForm.Target.DataSourceId,
            inputForm,
            module,
          })
        : null,
    [componentSet, dataSourceId, inputForm, module],
  )

  const mutation = useMutation<
    InputFormEndpointMutationResult<TValue, TResult>,
    unknown,
    ProjectedInputFormActionContext<TValue>
  >({
    mutationFn: async (actionContext) => {
      if (!inputForm || !isEndpointRequestInputForm(inputForm)) {
        throw new Error('The projected input form is not bound to an endpoint request target.')
      }

      const resolvedEndpointId = resolveInputFormEndpointId({
        actionId: actionContext.placement.ActionId,
        componentSet,
        dataSourceId: dataSourceId ?? inputForm.Target.DataSourceId,
        inputForm,
        module,
      })
      if (!resolvedEndpointId) {
        throw new Error(`No endpoint binding is registered for input form '${inputForm.Id}'.`)
      }

      const action = findPresentationAction<ActionDefinition>(
        module,
        actionContext.placement.ActionId,
      )
      const context = {
        action,
        actionContext,
        endpointId: resolvedEndpointId,
        inputForm,
        module,
        value: actionContext.value,
      } satisfies ProjectedInputFormEndpointRequestContext<TValue>
      const request = prepareRequest?.(context) ?? createDefaultEndpointRequest({
        context,
        defaultBody,
        routeParameters,
      })
      const result = await executeEndpoint<TResult>(resolvedEndpointId, request)

      return {
        context,
        request,
        result,
      }
    },
    onSuccess: async ({ context, request, result }) => {
      const successContext = {
        ...context,
        queryClient,
        request,
        result,
      } satisfies ProjectedInputFormEndpointSuccessContext<TValue, TResult>

      setResultQueryData?.(successContext)
      processResult?.(successContext)
      await invalidateInputFormDataSources({
        context: successContext,
        dataSourceQueryKey,
        inputForm,
        invalidateDataSourceIds,
        queryClient,
      })
      onSuccess?.(successContext)
    },
  })

  const runtime = inputForm && isEndpointRequestInputForm(inputForm)
    ? {
        invokeAction: (context) => {
          if (!mutation.isPending) {
            mutation.mutate(context)
          }
        },
        setValue: setRuntimeValue,
        value,
      } satisfies ProjectedInputFormRuntime<TValue>
    : null

  function resetValue() {
    setState({
      key: effectiveStateKey,
      value: initialValue,
    })
  }

  return {
    endpointId,
    error: mutation.error,
    isPending: mutation.isPending,
    isSuccess: mutation.isSuccess,
    reset: () => {
      mutation.reset()
      resetValue()
    },
    resetExecution: mutation.reset,
    resetValue,
    result: mutation.data?.result,
    runtime,
    value,
  }
}

/**
 * Lowers a projected input-form value into an endpoint request body.
 *
 * Each field writes the value found at its ValuePath into the same target path.
 * Missing, null, or empty values are replaced by the field's DefaultValue when
 * present. The optional defaultBody is copied first, so host-specific request
 * defaults such as concurrency tokens can be supplied outside the form surface.
 *
 * @typeParam TValue - Shape of the source form value.
 */
export function createInputFormEndpointRequestBody<TValue extends object>({
  defaultBody,
  inputForm,
  value,
}: {
  readonly defaultBody?: Readonly<Record<string, unknown>>
  readonly inputForm: InputFormDefinition
  readonly value: TValue
}) {
  const body: Record<string, unknown> = {
    ...(defaultBody ?? {}),
  }

  for (const field of inputForm.Fields) {
    const fieldValue = readInputFormEndpointFieldValue(value, field.ValuePath, field.DefaultValue)
    if (fieldValue !== undefined) {
      writeObjectPath(body, field.ValuePath, fieldValue)
    }
  }

  return body
}

function createDefaultEndpointRequest<TValue extends object>({
  context,
  defaultBody,
  routeParameters,
}: {
  readonly context: ProjectedInputFormEndpointRequestContext<TValue>
  readonly defaultBody?: InputFormEndpointRequestOption<TValue, Readonly<Record<string, unknown>>>
  readonly routeParameters?: InputFormEndpointRequestOption<
    TValue,
    Readonly<Record<string, string | null | undefined>>
  >
}): PresentationActionEndpointExecutionRequest {
  return {
    body: createInputFormEndpointRequestBody({
      defaultBody: resolveOption(defaultBody, context),
      inputForm: context.inputForm,
      value: context.value,
    }),
    routeParameters: resolveOption(routeParameters, context),
  }
}

function resolveInputFormEndpointId({
  actionId,
  componentSet,
  dataSourceId,
  inputForm,
  module,
}: {
  readonly actionId: string | null
  readonly componentSet: string | null
  readonly dataSourceId?: string | null
  readonly inputForm: InputFormDefinition
  readonly module: PresentationModuleDefinition | null
}) {
  const actionEndpointId = actionId
    ? resolvePresentationActionEndpointBinding({
        actionId,
        componentSet,
        dataSourceId,
        module,
      })?.EndpointId
    : null

  return actionEndpointId ?? inputForm.Target.EndpointId ?? null
}

function createInitialInputFormValue<TValue extends object>({
  defaultValue,
  inputForm,
  sources,
}: {
  readonly defaultValue: TValue
  readonly inputForm: InputFormDefinition | null
  readonly sources?: Readonly<Record<string, unknown>>
}): TValue {
  if (!inputForm) {
    return defaultValue
  }

  const value = createInputFormEndpointRequestBody({
    defaultBody: defaultValue as Readonly<Record<string, unknown>>,
    inputForm,
    value: defaultValue,
  }) as TValue

  return applyInputFormDefaultValueBindings({
    inputForm,
    sources,
    value,
  })
}

function resolveInitialValue<TValue extends object>(
  defaultValue: TValue | (() => TValue) | undefined,
): TValue {
  if (typeof defaultValue === 'function') {
    return (defaultValue as () => TValue)()
  }

  return (defaultValue ?? {}) as TValue
}

function readInputFormEndpointFieldValue(
  value: object,
  path: string,
  defaultValue: string | null | undefined,
) {
  const valueAtPath = readObjectPath(value, path)
  if (valueAtPath === undefined || valueAtPath === null || valueAtPath === '') {
    return parseInputFormDefaultValue(defaultValue)
  }

  return valueAtPath
}

function parseInputFormDefaultValue(value: string | null | undefined) {
  if (value === null || value === undefined) {
    return undefined
  }

  if (value === 'null') {
    return null
  }

  if (value === 'true') {
    return true
  }

  if (value === 'false') {
    return false
  }

  const numberValue = Number(value)
  return Number.isFinite(numberValue) && value.trim() !== '' ? numberValue : value
}

function applyInputFormDefaultValueBindings<TValue extends object>({
  inputForm,
  sources,
  value,
}: {
  readonly inputForm: InputFormDefinition
  readonly sources?: Readonly<Record<string, unknown>>
  readonly value: TValue
}): TValue {
  const defaultValues = inputForm.DefaultValues ?? []
  if (defaultValues.length === 0) {
    return value
  }

  const target = { ...(value as Readonly<Record<string, unknown>>) }
  for (const binding of defaultValues) {
    const resolved = resolveInputFormDefaultValue(binding.Source, sources)
    if ((resolved === null || resolved === undefined) && binding.OmitWhenNull) {
      continue
    }

    writeObjectPath(target, binding.TargetPath, resolved)
  }

  return target as TValue
}

function resolveInputFormDefaultValue(
  value: PresentationValueDefinition,
  sources: Readonly<Record<string, unknown>> | undefined,
) {
  if (isPresentationValueKind(value.Kind, presentationValueKinds.literal, 'literal')) {
    return parseInputFormDefaultValue(value.Literal)
  }

  if (isPresentationValueKind(value.Kind, presentationValueKinds.field, 'field')) {
    return value.Field ? readObjectPath(sources ?? {}, value.Field) : undefined
  }

  if (isPresentationValueKind(value.Kind, presentationValueKinds.state, 'state')) {
    return value.StateId ? sources?.[value.StateId] : undefined
  }

  if (isPresentationValueKind(value.Kind, presentationValueKinds.expression, 'expression')) {
    throw new Error('Input form default expression values are not supported yet.')
  }

  return undefined
}

function isPresentationValueKind(
  value: unknown,
  numericKind: number,
  stringKind: string,
) {
  return value === numericKind ||
    (typeof value === 'string' && value.toLowerCase() === stringKind.toLowerCase())
}

function isEndpointRequestInputForm(inputForm: InputFormDefinition) {
  return inputForm.Target.Kind === inputFormTargetKinds.endpointRequest ||
    String(inputForm.Target.Kind).toLowerCase() === 'endpointrequest'
}

function resolveOption<TValue extends object, TOption>(
  option: InputFormEndpointRequestOption<TValue, TOption> | undefined,
  context: ProjectedInputFormEndpointRequestContext<TValue>,
) {
  return typeof option === 'function'
    ? (option as (context: ProjectedInputFormEndpointRequestContext<TValue>) => TOption)(context)
    : option
}

async function invalidateInputFormDataSources<TValue extends object, TResult>({
  context,
  dataSourceQueryKey,
  inputForm,
  invalidateDataSourceIds,
  queryClient,
}: {
  readonly context: ProjectedInputFormEndpointSuccessContext<TValue, TResult>
  readonly dataSourceQueryKey?: (dataSourceId: string) => readonly unknown[]
  readonly inputForm: InputFormDefinition | null
  readonly invalidateDataSourceIds?:
    | readonly string[]
    | ((context: ProjectedInputFormEndpointSuccessContext<TValue, TResult>) => readonly string[])
  readonly queryClient: QueryClient
}) {
  if (!dataSourceQueryKey) {
    return
  }

  const dataSourceIds = typeof invalidateDataSourceIds === 'function'
    ? invalidateDataSourceIds(context)
    : (
        invalidateDataSourceIds ??
        context.action?.Result?.InvalidateDataSourceIds ??
        (inputForm?.Target.DataSourceId ? [inputForm.Target.DataSourceId] : [])
      )

  await Promise.all(
    Array.from(new Set(dataSourceIds)).map((dataSourceId) =>
      queryClient.invalidateQueries({ queryKey: dataSourceQueryKey(dataSourceId) }),
    ),
  )
}
