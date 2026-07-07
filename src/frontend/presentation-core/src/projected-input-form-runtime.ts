import type {
  ActionDefinition,
  ActionPlacementDefinition,
  DataSourceQueryDefinition,
  InputFormDefinition,
  QueryFormDefinition,
} from './module'

/**
 * Caller-owned state and commands used by a projected input form.
 *
 * The form renderer owns generic control projection. The host owns how a placed
 * action interprets the current draft value for a query, transition, process,
 * endpoint request, enrichment request, or local state update.
 *
 * @typeParam TValue - Shape of the caller-owned form state object.
 */
export interface ProjectedInputFormRuntime<TValue extends object = object> {
  /** Invokes a form action against the current draft value. */
  readonly invokeAction: (context: ProjectedInputFormActionContext<TValue>) => void

  /** Updates the caller-owned form value, preserving React set-state semantics. */
  readonly setValue: (
    update: TValue | ((current: TValue) => TValue),
    context?: ProjectedInputFormValueChangeContext<TValue>,
  ) => void

  /** Current caller-owned form value. */
  readonly value: TValue
}

/**
 * Target interpretation attached to the projected input form.
 */
export interface ProjectedInputFormTargetContext {
  /** Optional relation-query field metadata used when a query form specializes this input form. */
  readonly queryDefinition?: DataSourceQueryDefinition | null

  /** Optional query-form specialization over this input form. */
  readonly queryForm?: QueryFormDefinition | null

  /** Shared or target state id affected by the input form. */
  readonly stateId: string
}

/**
 * Semantic and runtime state passed to input-form action handlers.
 *
 * @typeParam TValue - Shape of the caller-owned form state object.
 */
export interface ProjectedInputFormActionContext<TValue extends object = object> {
  /** Presentation action resolved from the action placement, when available. */
  readonly action: ActionDefinition | null

  /** Available choice values for each input-form field id at the time of action. */
  readonly choiceValuesByFieldId: Readonly<Record<string, readonly string[]>>

  /** Input form that declares groups, fields, and action placements. */
  readonly inputForm: InputFormDefinition

  /** Action placement selected by the user. */
  readonly placement: ActionPlacementDefinition

  /** Target interpretation attached to this form instance. */
  readonly target: ProjectedInputFormTargetContext

  /** Current caller-owned form value. */
  readonly value: TValue
}

/**
 * Semantic context supplied when a field changes the caller-owned form value.
 */
export interface ProjectedInputFormValueChangeContext<TValue extends object = object> {
  /** Available choice values for each input-form field id at the time of change. */
  readonly choiceValuesByFieldId: Readonly<Record<string, readonly string[]>>

  /** Input form that declares groups, fields, and action placements. */
  readonly inputForm: InputFormDefinition

  /** Target interpretation attached to this form instance. */
  readonly target: ProjectedInputFormTargetContext

  /** Current caller-owned form value before the field update is applied. */
  readonly value: TValue
}
