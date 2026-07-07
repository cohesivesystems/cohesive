import {
  useMemo,
  useState,
  type PropsWithChildren,
} from 'react'

import type {
  CollectionSelectionMode,
} from '@cohesive/presentation-core'
import {
  CollectionSelectionStateContext,
  type CollectionSelectionStateContextValue,
} from './collection-selection-state-context'
import {
  collectionSelectionModes,
} from '@cohesive/presentation-contracts'

export function CollectionSelectionStateProvider({
  children,
}: PropsWithChildren) {
  const [selectedRowIdsByStateId, setSelectedRowIdsByStateId] =
    useState<Record<string, readonly string[]>>({})

  const value = useMemo<CollectionSelectionStateContextValue>(
    () => ({
      clearSelection: (selectionStateId) => {
        setSelectedRowIdsByStateId((current) =>
          setSelectionState(current, selectionStateId, []))
      },
      selectedRowIdsByStateId,
      selectRowId: (selectionStateId, rowId, mode) => {
        setSelectedRowIdsByStateId((current) =>
          setSelectionState(current, selectionStateId, selectRowId(current, selectionStateId, rowId, mode)))
      },
      setSelectedRowIds: (selectionStateId, rowIds) => {
        setSelectedRowIdsByStateId((current) =>
          setSelectionState(current, selectionStateId, uniqueRowIds(rowIds)))
      },
      toggleRowId: (selectionStateId, rowId, mode) => {
        setSelectedRowIdsByStateId((current) =>
          setSelectionState(current, selectionStateId, toggleRowId(current, selectionStateId, rowId, mode)))
      },
    }),
    [selectedRowIdsByStateId],
  )

  return (
    <CollectionSelectionStateContext.Provider value={value}>
      {children}
    </CollectionSelectionStateContext.Provider>
  )
}

function selectRowId(
  current: Readonly<Record<string, readonly string[]>>,
  selectionStateId: string,
  rowId: string,
  mode: CollectionSelectionMode,
) {
  if (isMultipleSelectionMode(mode)) {
    return uniqueRowIds([...(current[selectionStateId] ?? []), rowId])
  }

  return [rowId]
}

function toggleRowId(
  current: Readonly<Record<string, readonly string[]>>,
  selectionStateId: string,
  rowId: string,
  mode: CollectionSelectionMode,
) {
  if (!isMultipleSelectionMode(mode)) {
    return [rowId]
  }

  const currentRowIds = current[selectionStateId] ?? []
  return currentRowIds.includes(rowId)
    ? currentRowIds.filter((candidate) => candidate !== rowId)
    : [...currentRowIds, rowId]
}

function setSelectionState(
  current: Readonly<Record<string, readonly string[]>>,
  selectionStateId: string,
  selectedRowIds: readonly string[],
) {
  return {
    ...current,
    [selectionStateId]: selectedRowIds,
  }
}

function uniqueRowIds(rowIds: readonly string[]) {
  return Array.from(new Set(rowIds))
}

function isMultipleSelectionMode(mode: CollectionSelectionMode) {
  return mode === collectionSelectionModes.multiple ||
    String(mode).toLocaleLowerCase() === 'multiple'
}
