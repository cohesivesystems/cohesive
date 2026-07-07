import { createContext, useContext } from 'react'

import type {
  PresentationScopeAccess,
} from '@cohesive/presentation-core'

/**
 * React scope-selection state shared by a presentation host.
 */
export interface PresentationScopeSelectionContextValue<
  TScopeContext = unknown,
  TScopeMetadata = unknown,
> {
  /** Source context that produced the accessible scopes. */
  readonly scopeContext: TScopeContext | null

  /** Whether scope context is currently loading. */
  readonly isLoading: boolean

  /** Scopes accessible to the current principal. */
  readonly scopes: readonly PresentationScopeAccess<TScopeMetadata>[]

  /** Primary single selected scope id. */
  readonly selectedScopeId: string | null

  /** Selects the primary single scope. */
  readonly setSelectedScopeId: (scopeId: string) => void

  /** Reads selected scope ids for a named multi-scope purpose. */
  readonly getSelectedScopeIds: (
    purpose: string,
    fallbackScopeIds?: readonly string[]
  ) => readonly string[]

  /** Stores selected scope ids for a named multi-scope purpose. */
  readonly setSelectedScopeIds: (purpose: string, scopeIds: readonly string[]) => void

  /** Stable query/cache suffix for the primary single selected scope. */
  readonly singleScopeQuerySuffix: string
}

export const PresentationScopeSelectionContext =
  createContext<PresentationScopeSelectionContextValue | null>(null)

/**
 * Reads the nearest presentation scope-selection context.
 */
export function usePresentationScopeSelection<
  TScopeContext = unknown,
  TScopeMetadata = unknown,
>() {
  const value = useContext(PresentationScopeSelectionContext)
  if (!value) {
    throw new Error('usePresentationScopeSelection must be used inside PresentationScopeSelectionProvider.')
  }

  return value as PresentationScopeSelectionContextValue<TScopeContext, TScopeMetadata>
}
