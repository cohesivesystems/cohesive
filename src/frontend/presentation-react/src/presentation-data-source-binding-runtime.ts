import { useQueries } from '@tanstack/react-query'
import { useMemo } from 'react'

import {
  findPresentationDataSource,
  isTanStackQueryBinding,
  resolveDataSourceAuthorization,
  type DataSourceDefinition,
  type PresentationDataSourceBinding,
  type PresentationDataSourceState,
  type PresentationDataSourceStateMap,
  type PresentationLocalValueDataSourceBinding,
  type PresentationTanStackQueryDataSourceBinding,
} from '@cohesivesystems/presentation-core'
import { usePresentationModule } from './presentation-module-context'

interface PresentationAsyncDataSourceResult {
  readonly data: unknown
  readonly error: unknown
  readonly isFetching: boolean
  readonly isPending: boolean
  readonly refetch: () => Promise<unknown>
}

export function usePresentationDataSources(
  bindings: readonly PresentationDataSourceBinding[],
) {
  const module = usePresentationModule()
  const queryBindings = useMemo(
    () => bindings.filter(isTanStackQueryBinding),
    [bindings],
  )
  const queryResults = useQueries({
    queries: queryBindings.map((binding) => {
      const authorization = resolveDataSourceAuthorization(binding)
      return {
        enabled: (binding.enabled ?? true) && !authorization.isBlocked,
        queryFn: binding.queryFn,
        queryKey: binding.queryKey,
        refetchInterval: binding.refetchInterval,
        retry: binding.retry,
        staleTime: binding.staleTime,
      }
    }),
  })
  const dataSources = useMemo<PresentationDataSourceStateMap>(() => {
    const definitionsById = new Map(
      module?.DataSources.map((definition) => [definition.Id, definition] as const) ?? [],
    )
    const asyncResultsByDataSourceId = new Map(
      queryBindings.map((binding, index) => [binding.dataSourceId, queryResults[index]] as const),
    )
    return Object.fromEntries(
      bindings.map((binding) => {
        const definition =
          definitionsById.get(binding.dataSourceId) ??
          findPresentationDataSource(module, binding.dataSourceId)
        const state = isTanStackQueryBinding(binding)
          ? createAsyncDataSourceState(
              binding,
              asyncResultsByDataSourceId.get(binding.dataSourceId) as PresentationAsyncDataSourceResult,
              definition,
            )
          : createLocalValueDataSourceState(binding, definition)
        return [binding.dataSourceId, state]
      }),
    )
  }, [bindings, module, queryBindings, queryResults])
  return dataSources
}

function createAsyncDataSourceState(
  binding: PresentationTanStackQueryDataSourceBinding,
  result: PresentationAsyncDataSourceResult,
  definition: DataSourceDefinition | null,
): PresentationDataSourceState {
  const authorization = resolveDataSourceAuthorization(binding)
  return {
    blockedLabel: authorization.blockedLabel,
    data: result.data === undefined ? binding.fallbackData : result.data,
    definition,
    emptyMessage: binding.emptyMessage,
    error: result.error,
    isBlocked: authorization.isBlocked,
    isFetching: result.isFetching,
    isPending: result.isPending,
    pendingLabel: binding.pendingLabel,
    refetch: result.refetch,
  }
}

function createLocalValueDataSourceState(
  binding: PresentationLocalValueDataSourceBinding,
  definition: DataSourceDefinition | null,
): PresentationDataSourceState {
  const authorization = resolveDataSourceAuthorization(binding)
  return {
    blockedLabel: authorization.blockedLabel,
    data: binding.data,
    definition,
    emptyMessage: binding.emptyMessage,
    error: binding.error,
    isBlocked: authorization.isBlocked,
    isFetching: binding.isFetching,
    isPending: binding.isPending,
    pendingLabel: binding.pendingLabel,
    refetch: binding.refetch,
  }
}
