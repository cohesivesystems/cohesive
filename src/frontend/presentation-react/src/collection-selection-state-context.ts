import {
  createContext,
  useContext,
  useMemo,
} from 'react'

import type {
  CollectionSelectionStateContextValue,
  CollectionSelectionStateEntry,
} from '@cohesive/presentation-core'

export type {
  CollectionSelectionStateContextValue,
  CollectionSelectionStateEntry,
} from '@cohesive/presentation-core'

export const CollectionSelectionStateContext =
  createContext<CollectionSelectionStateContextValue | null>(null)

export function useCollectionSelectionState(
  selectionStateId: string | null | undefined,
): CollectionSelectionStateEntry | null {
  const context = useContext(CollectionSelectionStateContext)

  return useMemo(() => {
    if (!context || !selectionStateId) {
      return null
    }

    const selectedRowIds =
      context.selectedRowIdsByStateId[selectionStateId] ?? []

    return {
      clearSelection: () => context.clearSelection(selectionStateId),
      selectedRowId: selectedRowIds[0] ?? null,
      selectedRowIds,
      selectionStateId,
      selectRowId: (rowId, mode) =>
        context.selectRowId(selectionStateId, rowId, mode),
      setSelectedRowIds: (rowIds) =>
        context.setSelectedRowIds(selectionStateId, rowIds),
      toggleRowId: (rowId, mode) =>
        context.toggleRowId(selectionStateId, rowId, mode),
    } satisfies CollectionSelectionStateEntry
  }, [context, selectionStateId])
}

export function useCollectionSelectionStateMap() {
  return useContext(CollectionSelectionStateContext)
    ?.selectedRowIdsByStateId ?? {}
}
