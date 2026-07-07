import {
  createContext,
  useContext,
  type Dispatch,
  type SetStateAction,
} from 'react'

import type {
  InputFormDefinition,
  PresentationQueryFormStateMap,
  QueryFormDefinition,
} from '@cohesive/presentation-core'

export interface PresentationQueryFormStateEntry<TValue = unknown> {
  readonly appliedValue: TValue
  readonly draftValue: TValue
  readonly inputForm: InputFormDefinition | null
  readonly queryForm: QueryFormDefinition | null
  readonly queryFormId: string
  readonly setAppliedValue: Dispatch<SetStateAction<TValue>>
  readonly setDraftValue: Dispatch<SetStateAction<TValue>>
}

export interface PresentationQueryFormStateContextValue {
  readonly entries: Readonly<Record<string, PresentationQueryFormStateEntry>>
  readonly queryFormStates: PresentationQueryFormStateMap
}

export const PresentationQueryFormStateContext =
  createContext<PresentationQueryFormStateContextValue | null>(null)

export function usePresentationQueryFormState<TValue = unknown>(
  queryFormId: string,
) {
  const context = useRequiredPresentationQueryFormStateContext()
  return context.entries[queryFormId] as
    | PresentationQueryFormStateEntry<TValue>
    | undefined
}

export function usePresentationQueryFormStateMap() {
  return useRequiredPresentationQueryFormStateContext().queryFormStates
}

export function usePresentationQueryFormStateEntries() {
  return useRequiredPresentationQueryFormStateContext().entries
}

function useRequiredPresentationQueryFormStateContext() {
  const context = useContext(PresentationQueryFormStateContext)
  if (!context) {
    throw new Error('Presentation query form state must be used inside PresentationQueryFormStateProvider.')
  }

  return context
}
