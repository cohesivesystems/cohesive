import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { useMemo } from 'react'

import type {
  PresentationActionEndpointExecutionRequest,
} from '@cohesivesystems/presentation-core'
import {
  findPresentationAction,
  type ActionDefinition,
  type PresentationBindingDefinition,
  type PresentationModuleDefinition,
  resolvePresentationActionEndpointBinding,
} from '@cohesivesystems/presentation-core'

export type { PresentationActionEndpointExecutionRequest } from '@cohesivesystems/presentation-core'

export type PresentationActionEndpointExecutor = <TResult = unknown>(
  endpointId: string,
  request: PresentationActionEndpointExecutionRequest,
) => Promise<TResult>

export interface PresentationActionRequestPreparationContext<TInput> {
  readonly action: ActionDefinition | null
  readonly actionId: string
  readonly binding: PresentationBindingDefinition | null
  readonly endpointId: string
  readonly input: TInput
  readonly module: PresentationModuleDefinition | null
}

export interface PresentationActionSuccessContext<TInput, TResult> {
  readonly action: ActionDefinition | null
  readonly actionId: string
  readonly endpointId: string
  readonly input: TInput
  readonly queryClient: QueryClient
  readonly result: TResult
}

export interface UsePresentationActionExecutorOptions<TInput, TResult> {
  readonly actionId: string
  readonly componentSet?: string | null
  readonly dataSourceId?: string | null
  readonly dataSourceQueryKey?: (dataSourceId: string) => readonly unknown[]
  readonly executeEndpoint: PresentationActionEndpointExecutor
  readonly invalidateDataSourceIds?:
    | readonly string[]
    | ((context: PresentationActionSuccessContext<TInput, TResult>) => readonly string[])
  readonly module: PresentationModuleDefinition | null
  readonly prepareRequest: (
    context: PresentationActionRequestPreparationContext<TInput>,
  ) => PresentationActionEndpointExecutionRequest
  readonly processResult?: (context: PresentationActionSuccessContext<TInput, TResult>) => void
  readonly setResultQueryData?: (context: PresentationActionSuccessContext<TInput, TResult>) => void
  readonly onSuccess?: (context: PresentationActionSuccessContext<TInput, TResult>) => void
}

export interface PresentationActionExecutor<TInput, TResult> {
  readonly action: ActionDefinition | null
  readonly endpointId: string | null
  readonly error: unknown
  readonly execute: (input: TInput) => void
  readonly executeAsync: (input: TInput) => Promise<TResult>
  readonly isPending: boolean
  readonly isSuccess: boolean
  readonly reset: () => void
}

/**
 * Runtime projection for endpoint-backed presentation actions. It resolves the
 * endpoint from the action definition/target bindings, prepares an endpoint
 * request from the caller's semantic input, executes it through the app's API
 * adapter, and applies result/query invalidation policy.
 */
export function usePresentationActionExecutor<TInput = void, TResult = unknown>({
  actionId,
  componentSet = 'cohesive.presentation.react',
  dataSourceId,
  dataSourceQueryKey,
  executeEndpoint,
  invalidateDataSourceIds,
  module,
  onSuccess,
  prepareRequest,
  processResult,
  setResultQueryData,
}: UsePresentationActionExecutorOptions<TInput, TResult>): PresentationActionExecutor<TInput, TResult> {
  const queryClient = useQueryClient()
  const action = useMemo(
    () => findPresentationAction<ActionDefinition>(module, actionId),
    [actionId, module],
  )
  const binding = useMemo(
    () =>
      resolvePresentationActionEndpointBinding({
        actionId,
        componentSet,
        dataSourceId,
        module,
      }),
    [actionId, componentSet, dataSourceId, module],
  )
  const endpointId = binding?.EndpointId ?? null
  const mutation = useMutation<TResult, unknown, TInput>({
    mutationFn: async (input) => {
      if (!endpointId) {
        throw new Error(`No endpoint binding is registered for action '${actionId}'.`)
      }

      return await executeEndpoint<TResult>(
        endpointId,
        prepareRequest({
          action,
          actionId,
          binding,
          endpointId,
          input,
          module,
        }),
      )
    },
    onSuccess: async (result, input) => {
      if (!endpointId) {
        return
      }

      const context = {
        action,
        actionId,
        endpointId,
        input,
        queryClient,
        result,
      } satisfies PresentationActionSuccessContext<TInput, TResult>

      setResultQueryData?.(context)
      processResult?.(context)
      await invalidatePresentationActionDataSources({
        action,
        context,
        dataSourceQueryKey,
        invalidateDataSourceIds,
        queryClient,
      })
      onSuccess?.(context)
    },
  })

  return {
    action,
    endpointId,
    error: mutation.error,
    execute: mutation.mutate,
    executeAsync: mutation.mutateAsync,
    isPending: mutation.isPending,
    isSuccess: mutation.isSuccess,
    reset: mutation.reset,
  }
}

async function invalidatePresentationActionDataSources<TInput, TResult>({
  action,
  context,
  dataSourceQueryKey,
  invalidateDataSourceIds,
  queryClient,
}: {
  readonly action: ActionDefinition | null
  readonly context: PresentationActionSuccessContext<TInput, TResult>
  readonly dataSourceQueryKey?: (dataSourceId: string) => readonly unknown[]
  readonly invalidateDataSourceIds?:
    | readonly string[]
    | ((context: PresentationActionSuccessContext<TInput, TResult>) => readonly string[])
  readonly queryClient: QueryClient
}) {
  if (!dataSourceQueryKey) {
    return
  }

  const dataSourceIds =
    typeof invalidateDataSourceIds === 'function'
      ? invalidateDataSourceIds(context)
      : (invalidateDataSourceIds ?? action?.Result?.InvalidateDataSourceIds ?? [])

  await Promise.all(
    Array.from(new Set(dataSourceIds)).map((dataSourceId) =>
      queryClient.invalidateQueries({ queryKey: dataSourceQueryKey(dataSourceId) }),
    ),
  )
}
