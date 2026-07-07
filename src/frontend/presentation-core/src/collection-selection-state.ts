import type {
  CollectionSelectionMode,
} from './module'

export interface CollectionSelectionStateEntry {
  readonly clearSelection: () => void
  readonly selectedRowId: string | null
  readonly selectedRowIds: readonly string[]
  readonly selectionStateId: string
  readonly selectRowId: (rowId: string, mode: CollectionSelectionMode) => void
  readonly setSelectedRowIds: (rowIds: readonly string[]) => void
  readonly toggleRowId: (rowId: string, mode: CollectionSelectionMode) => void
}

export interface CollectionSelectionStateContextValue {
  readonly selectedRowIdsByStateId: Readonly<Record<string, readonly string[]>>
  readonly clearSelection: (selectionStateId: string) => void
  readonly selectRowId: (
    selectionStateId: string,
    rowId: string,
    mode: CollectionSelectionMode,
  ) => void
  readonly setSelectedRowIds: (
    selectionStateId: string,
    rowIds: readonly string[],
  ) => void
  readonly toggleRowId: (
    selectionStateId: string,
    rowId: string,
    mode: CollectionSelectionMode,
  ) => void
}
