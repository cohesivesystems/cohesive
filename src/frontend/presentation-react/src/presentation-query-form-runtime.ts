import type { Dispatch, SetStateAction } from 'react'

import type {
  ProjectedQueryFormActionContext,
  ProjectedQueryFormRuntime,
  ProjectedQueryFormValueChangeContext,
  QueryFormDefinition,
} from '@cohesive/presentation-core'
import { queryFormExecutionModes } from '@cohesive/presentation-contracts'

const debouncedDraftExecutionTimers = new Map<string, ReturnType<typeof setTimeout>>()

export type {
  PresentationQueryFormState,
  PresentationQueryFormStateMap,
  ProjectedQueryFormActionContext,
  ProjectedQueryFormRuntime,
  ProjectedQueryFormValueChangeContext,
} from '@cohesive/presentation-core'

export {
  findPresentationQueryFormStateForDataSource,
  readPresentationQueryFormAppliedValue,
  readPresentationQueryFormDraftValue,
} from '@cohesive/presentation-core'

export interface PresentationQueryFormValueContext<TValue extends object> {
  readonly choiceValuesByFieldId: Readonly<Record<string, readonly string[]>>
  readonly queryForm: QueryFormDefinition
  readonly value: TValue
}

export interface CreatePresentationQueryFormRuntimeOptions<TValue extends object> {
  readonly applyValue: (context: PresentationQueryFormValueContext<TValue>) => void
  readonly createDefaultValue: (context: PresentationQueryFormValueContext<TValue>) => TValue
  readonly normalizeValue?: (context: PresentationQueryFormValueContext<TValue>) => TValue
  readonly queryForm: QueryFormDefinition | null
  readonly setDraftValue: Dispatch<SetStateAction<TValue>>
  readonly value: TValue
}

/**
 * Adapts caller-owned state into the projected query-form runtime contract.
 *
 * The presentation IR defines when a query form has an applied result state,
 * URL state, and execution policy. This helper keeps the generic form actions
 * semantic: apply normalizes the draft and commits it to the result state,
 * reset computes the form default from the available choices and commits that
 * same value. Concrete hosts provide the interpretation for the commit itself,
 * such as URL updates, pagination reset, or data-source invalidation.
 */
export function createPresentationQueryFormRuntime<TValue extends object>({
  applyValue,
  createDefaultValue,
  normalizeValue,
  queryForm,
  setDraftValue,
  value,
}: CreatePresentationQueryFormRuntimeOptions<TValue>): ProjectedQueryFormRuntime<TValue> | null {
  if (!queryForm) {
    return null
  }

  const normalize = (context: PresentationQueryFormValueContext<TValue>) =>
    normalizeValue ? normalizeValue(context) : context.value
  const commit = (context: PresentationQueryFormValueContext<TValue>) => {
    const nextValue = normalize(context)
    setDraftValue(nextValue)
    applyValue({ ...context, value: nextValue })
  }

  return {
    apply: (context) =>
      commit(createQueryFormValueContext(queryForm, context, context.value)),
    reset: (context) => {
      const resetContext = createQueryFormValueContext(queryForm, context, context.value)
      const nextValue = createDefaultValue(resetContext)
      commit({ ...resetContext, value: nextValue })
    },
    setValue: (update, context) => {
      const nextValue = resolveSetStateValue(update, value)
      setDraftValue(nextValue)

      if (context) {
        executeQueryFormDraftChange(
          queryForm,
          commit,
          createQueryFormValueContext(queryForm, context, nextValue),
        )
      }
    },
    value,
  }
}

export function shouldExecuteQueryFormOnDraftChange(queryForm: QueryFormDefinition | null) {
  return queryForm?.Target.State.Execution.Mode === queryFormExecutionModes.live ||
    queryForm?.Target.State.Execution.Mode === queryFormExecutionModes.debouncedLive
}

function createQueryFormValueContext<TValue extends object>(
  queryForm: QueryFormDefinition,
  context: ProjectedQueryFormActionContext<TValue> | ProjectedQueryFormValueChangeContext<TValue>,
  value: TValue,
): PresentationQueryFormValueContext<TValue> {
  return {
    choiceValuesByFieldId: context.choiceValuesByFieldId,
    queryForm,
    value,
  }
}

function executeQueryFormDraftChange<TValue extends object>(
  queryForm: QueryFormDefinition,
  commit: (context: PresentationQueryFormValueContext<TValue>) => void,
  context: PresentationQueryFormValueContext<TValue>,
) {
  const execution = queryForm.Target.State.Execution
  if (execution.Mode === queryFormExecutionModes.live) {
    commit(context)
    return
  }

  if (execution.Mode === queryFormExecutionModes.debouncedLive) {
    const key = queryForm.Target.State.StateId || queryForm.Id
    const previousTimer = debouncedDraftExecutionTimers.get(key)
    if (previousTimer) {
      clearTimeout(previousTimer)
    }

    debouncedDraftExecutionTimers.set(
      key,
      setTimeout(() => {
        debouncedDraftExecutionTimers.delete(key)
        commit(context)
      }, execution.DebounceMilliseconds ?? 250),
    )
  }
}

function resolveSetStateValue<TValue extends object>(
  update: TValue | ((current: TValue) => TValue),
  current: TValue,
) {
  return typeof update === 'function'
    ? (update as (current: TValue) => TValue)(current)
    : update
}
