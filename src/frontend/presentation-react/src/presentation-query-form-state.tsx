import {
  useMemo,
  useState,
  type PropsWithChildren,
  type SetStateAction,
} from 'react'
import { useLocation } from 'react-router'

import {
  findPresentationInputForm,
  findPresentationQueryForm,
  type InputFormDefinition,
  type PresentationModuleDefinition,
  type QueryFormDefinition,
} from '@cohesive/presentation-core'
import { usePresentationModule } from './presentation-module-context'
import {
  PresentationQueryFormStateContext,
  type PresentationQueryFormStateEntry,
} from './presentation-query-form-state-context'
import { resolvePresentationQueryFormStateAdapters } from './presentation-query-form-state-adapter-registry'
import type {
  PresentationQueryFormState,
  PresentationQueryFormStateMap,
} from '@cohesive/presentation-core'

export interface PresentationQueryFormStateAdapter<TValue extends object = object> {
  readonly queryFormId: string

  createAppliedSearch?(context: PresentationQueryFormAppliedSearchContext<TValue>): string
  createDefaultValue(context: PresentationQueryFormStateAdapterContext): TValue
  createSearchKey?(context: PresentationQueryFormStateAdapterContext): string
  normalizeValue?(context: PresentationQueryFormAdapterValueContext<TValue>): TValue
  readValueFromSearch?(context: PresentationQueryFormStateAdapterContext): TValue
}

export type PresentationQueryFormStateAdapterRegistry = Readonly<
  Record<string, PresentationQueryFormStateAdapter | undefined>
>

export interface PresentationQueryFormStateAdapterContext {
  readonly choiceValuesByFieldId?: Readonly<Record<string, readonly string[]>>
  readonly inputForm: InputFormDefinition | null
  readonly module: PresentationModuleDefinition | null
  readonly queryForm: QueryFormDefinition | null
  readonly search: string
}

export interface PresentationQueryFormAdapterValueContext<TValue extends object>
  extends PresentationQueryFormStateAdapterContext {
  readonly choiceValuesByFieldId: Readonly<Record<string, readonly string[]>>
  readonly value: TValue
}

export type PresentationQueryFormAppliedSearchContext<TValue extends object> =
  PresentationQueryFormAdapterValueContext<TValue>

interface PresentationQueryFormStateProviderProps extends PropsWithChildren {
  readonly adapterRegistry: PresentationQueryFormStateAdapterRegistry
}

interface PresentationQueryFormInitialState {
  readonly initialValue: object
  readonly inputForm: InputFormDefinition | null
  readonly queryForm: QueryFormDefinition | null
  readonly queryFormId: string
}

interface PresentationQueryFormStoredState {
  readonly appliedValue: unknown
  readonly draftValue: unknown
}

export function PresentationQueryFormStateProvider({
  adapterRegistry,
  children,
}: PresentationQueryFormStateProviderProps) {
  const location = useLocation()
  const module = usePresentationModule()
  const adapters = useMemo(
    () => resolvePresentationQueryFormStateAdapters(module, adapterRegistry),
    [adapterRegistry, module],
  )
  const initialStates = useMemo(
    () =>
      adapters.map((adapter) =>
        createInitialQueryFormState(adapter, module, location.search),
      ),
    [adapters, location.search, module],
  )
  const stateKey = useMemo(
    () =>
      adapters.map((adapter) =>
        createQueryFormStateKey(adapter, module, location.search),
      ).join('|'),
    [adapters, location.search, module],
  )

  return (
    <PresentationQueryFormStateScope
      initialStates={initialStates}
      key={stateKey}
    >
      {children}
    </PresentationQueryFormStateScope>
  )
}

function PresentationQueryFormStateScope({
  children,
  initialStates,
}: PropsWithChildren<{
  readonly initialStates: readonly PresentationQueryFormInitialState[]
}>) {
  const [storedStates, setStoredStates] = useState<Record<string, PresentationQueryFormStoredState>>(() =>
    Object.fromEntries(
      initialStates.map((state) => [
        state.queryFormId,
        {
          appliedValue: state.initialValue,
          draftValue: state.initialValue,
        } satisfies PresentationQueryFormStoredState,
      ]),
    ),
  )
  const entries = useMemo(
    () =>
      Object.fromEntries(
        initialStates.map((state) => {
          const storedState = storedStates[state.queryFormId] ?? {
            appliedValue: state.initialValue,
            draftValue: state.initialValue,
          }
          return [
            state.queryFormId,
            {
              appliedValue: storedState.appliedValue,
              draftValue: storedState.draftValue,
              inputForm: state.inputForm,
              queryForm: state.queryForm,
              queryFormId: state.queryFormId,
              setAppliedValue: (update: SetStateAction<unknown>) =>
                setStoredStates((current) =>
                  updateStoredQueryFormState(current, state.queryFormId, 'appliedValue', update),
                ),
              setDraftValue: (update: SetStateAction<unknown>) =>
                setStoredStates((current) =>
                  updateStoredQueryFormState(current, state.queryFormId, 'draftValue', update),
                ),
            } satisfies PresentationQueryFormStateEntry,
          ]
        }),
      ),
    [initialStates, storedStates],
  )
  const queryFormStates = useMemo(
    () =>
      Object.fromEntries(
        Object.values(entries).flatMap((entry) =>
          entry.queryForm
            ? [[
                entry.queryFormId,
                {
                  appliedValue: entry.appliedValue,
                  draftValue: entry.draftValue,
                  queryForm: entry.queryForm,
                  queryFormId: entry.queryFormId,
                } satisfies PresentationQueryFormState,
              ]]
            : [],
        ),
      ) satisfies PresentationQueryFormStateMap,
    [entries],
  )
  const value = useMemo(
    () => ({
      entries,
      queryFormStates,
    }),
    [entries, queryFormStates],
  )

  return (
    <PresentationQueryFormStateContext.Provider value={value}>
      {children}
    </PresentationQueryFormStateContext.Provider>
  )
}

function createInitialQueryFormState(
  adapter: PresentationQueryFormStateAdapter,
  module: PresentationModuleDefinition | null,
  search: string,
): PresentationQueryFormInitialState {
  const adapterContext = createAdapterContext(adapter, module, search)
  return {
    initialValue: adapter.readValueFromSearch?.(adapterContext) ??
      adapter.createDefaultValue(adapterContext),
    inputForm: adapterContext.inputForm,
    queryForm: adapterContext.queryForm,
    queryFormId: adapter.queryFormId,
  }
}

function createQueryFormStateKey(
  adapter: PresentationQueryFormStateAdapter,
  module: PresentationModuleDefinition | null,
  search: string,
) {
  const adapterContext = createAdapterContext(adapter, module, search)
  return `${adapter.queryFormId}:${adapter.createSearchKey?.(adapterContext) ?? ''}`
}

function createAdapterContext(
  adapter: PresentationQueryFormStateAdapter,
  module: PresentationModuleDefinition | null,
  search: string,
): PresentationQueryFormStateAdapterContext {
  const queryForm = findPresentationQueryForm(module, adapter.queryFormId)
  return {
    inputForm: queryForm ? findPresentationInputForm(module, queryForm.FormId) : null,
    module,
    queryForm,
    search,
  }
}

function updateStoredQueryFormState(
  current: Record<string, PresentationQueryFormStoredState>,
  queryFormId: string,
  field: keyof PresentationQueryFormStoredState,
  update: SetStateAction<unknown>,
) {
  const currentState = current[queryFormId] ?? {
    appliedValue: {},
    draftValue: {},
  }
  const currentValue = currentState[field]
  const nextValue = typeof update === 'function'
    ? (update as (value: unknown) => unknown)(currentValue)
    : update

  return {
    ...current,
    [queryFormId]: {
      ...currentState,
      [field]: nextValue,
    },
  }
}
