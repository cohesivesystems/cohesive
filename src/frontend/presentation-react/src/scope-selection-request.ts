import { useLayoutEffect } from 'react'

import type {
  PresentationScopeRequestSelection,
  PresentationScopeRequestStore,
} from '@cohesive/presentation-core'

/**
 * Synchronizes a mounted route or surface with the current request-scope store.
 */
export function usePresentationScopeRequestSelection(
  store: PresentationScopeRequestStore,
  selection: PresentationScopeRequestSelection,
) {
  const selectionKey = store.formatQuerySuffix(selection)

  useLayoutEffect(() => {
    store.setSelection(selection)
    return () => store.setSelection({ mode: 'default' })
  }, [selection, selectionKey, store])
}
