import type {
  InputFormDefinition,
  QueryFormDefinition,
} from './module'

/**
 * Caller-owned state and commands used by a projected relation-query form.
 *
 * Query forms specialize generic input-form projection while preserving a
 * compact apply/reset contract for hosts that own query state.
 *
 * @typeParam TValue - Shape of the caller-owned query state object.
 */
export interface ProjectedQueryFormRuntime<TValue extends object = object> {
  /** Applies the current form value to the host query or data-source layer. */
  readonly apply: (context: ProjectedQueryFormActionContext<TValue>) => void

  /** Resets the caller-owned form value according to host policy. */
  readonly reset: (context: ProjectedQueryFormActionContext<TValue>) => void

  /** Updates the caller-owned form value, preserving React set-state semantics. */
  readonly setValue: (
    update: TValue | ((current: TValue) => TValue),
    context?: ProjectedQueryFormValueChangeContext<TValue>,
  ) => void

  /** Current caller-owned form value. */
  readonly value: TValue
}

/**
 * Semantic and runtime state passed to apply/reset handlers for a projected
 * query form.
 *
 * @typeParam TValue - Shape of the caller-owned query state object.
 */
export interface ProjectedQueryFormActionContext<TValue extends object = object> {
  /** Available choice values for each input-form field id at the time of action. */
  readonly choiceValuesByFieldId: Readonly<Record<string, readonly string[]>>

  /** Input form that declares groups, fields, and action placements. */
  readonly inputForm: InputFormDefinition

  /** Query-form binding that connects the input form to a state target. */
  readonly queryForm: QueryFormDefinition

  /** Shared or target state id affected by the query form. */
  readonly stateId: string

  /** Current caller-owned form value. */
  readonly value: TValue
}

/**
 * Semantic context supplied when a query-form field changes the draft value.
 *
 * Hosts use this to interpret the query form's execution policy, for example
 * applying live query forms as field values change while leaving manual query
 * forms in draft state until an action is invoked.
 */
export type ProjectedQueryFormValueChangeContext<TValue extends object = object> =
  ProjectedQueryFormActionContext<TValue>
