import type { QueryFormDefinition } from './module'

export interface PresentationQueryFormState<TValue = unknown> {
  readonly appliedValue: TValue
  readonly draftValue: TValue
  readonly queryForm: QueryFormDefinition
  readonly queryFormId: string
}

export type PresentationQueryFormStateMap = Readonly<
  Record<string, PresentationQueryFormState>
>

export interface PresentationQueryFormStateContext {
  readonly queryFormStates?: PresentationQueryFormStateMap
}

export function readPresentationQueryFormDraftValue<TValue = unknown>(
  context: PresentationQueryFormStateContext,
  dataSourceId: string,
): TValue | undefined {
  return findPresentationQueryFormStateForDataSource(context, dataSourceId, 'draft')
    ?.draftValue as TValue | undefined
}

export function readPresentationQueryFormAppliedValue<TValue = unknown>(
  context: PresentationQueryFormStateContext,
  dataSourceId: string,
): TValue | undefined {
  return findPresentationQueryFormStateForDataSource(context, dataSourceId, 'applied')
    ?.appliedValue as TValue | undefined
}

export function findPresentationQueryFormStateForDataSource(
  { queryFormStates = {} }: PresentationQueryFormStateContext,
  dataSourceId: string,
  phase: 'applied' | 'draft',
) {
  return Object.values(queryFormStates).find((state) =>
    phase === 'draft'
      ? state.queryForm.Target.State.DraftDataSourceId === dataSourceId
      : isAppliedQueryFormDataSource(state.queryForm, dataSourceId),
  ) ?? null
}

function isAppliedQueryFormDataSource(
  queryForm: QueryFormDefinition,
  dataSourceId: string,
) {
  const state = queryForm.Target.State
  return state.AppliedDataSourceId === dataSourceId ||
    state.ResultDataSourceId === dataSourceId ||
    state.SynchronizedDataSourceIds.includes(dataSourceId)
}
