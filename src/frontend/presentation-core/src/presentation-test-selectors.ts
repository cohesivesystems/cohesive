import {
  presentationTestSelectors as generatedPresentationTestSelectors,
} from '@cohesive/presentation-contracts'
import type {
  FlowDefinition,
  FlowStateDefinition,
  FlowTransitionDefinition,
} from '@cohesive/presentation-contracts'

/**
 * Stable DOM attribute names emitted by Cohesive presentation renderers for
 * semantic UI automation.
 */
export const presentationTestAttributes = {
  actionId: generatedPresentationTestSelectors.actionIdAttribute,
  collectionSlotId: generatedPresentationTestSelectors.collectionSlotIdAttribute,
  fieldId: generatedPresentationTestSelectors.fieldIdAttribute,
  flowId: generatedPresentationTestSelectors.flowIdAttribute,
  flowStateId: generatedPresentationTestSelectors.flowStateIdAttribute,
  formId: generatedPresentationTestSelectors.formIdAttribute,
  projectionId: generatedPresentationTestSelectors.projectionIdAttribute,
  routeId: generatedPresentationTestSelectors.routeIdAttribute,
  rowId: generatedPresentationTestSelectors.rowIdAttribute,
  viewId: generatedPresentationTestSelectors.viewIdAttribute,
} as const

/**
 * Custom browser event names reserved for Cohesive presentation test drivers.
 */
export const presentationTestEvents = {
  setDocumentText: 'cohesive:presentation.test.set-document-text',
} as const

/**
 * Semantic presentation identities that can be projected onto a rendered
 * element as stable automation attributes.
 */
export interface PresentationTestAttributeSubject {
  /** Presentation action identifier represented by the element. */
  readonly actionId?: string | null

  /** Collection chrome, body, detail, or action slot identifier. */
  readonly collectionSlotId?: string | null

  /** Presentation field identifier represented by the element. */
  readonly fieldId?: string | null

  /** Presentation flow identifier represented by the element. */
  readonly flowId?: string | null

  /** Flow state identifier represented by the element. */
  readonly flowStateId?: string | null

  /** Presentation form identifier represented by the element. */
  readonly formId?: string | null

  /** Renderer-specific projection identifier represented by the element. */
  readonly projectionId?: string | null

  /** Navigation route identifier represented by the element. */
  readonly routeId?: string | null

  /** Data row identifier represented by the element. */
  readonly rowId?: string | null

  /** Presentation view identifier represented by the element. */
  readonly viewId?: string | null
}

/**
 * Payload for the test-only document text event.
 */
export interface PresentationSetDocumentTextTestEventDetail {
  /** Replacement document text supplied by the test driver. */
  readonly value: string
}

/**
 * Serializable semantic test plan generated from a presentation flow.
 */
export interface PresentationFlowTestPlan {
  /** Flow identifier that owns the generated plan. */
  readonly flowId: string

  /** Initial state identifier declared by the flow. */
  readonly initialStateId: string

  /** Human-readable flow name. */
  readonly name: string

  /** Flow states with selectors for locating their rendered surfaces. */
  readonly states: readonly PresentationFlowTestState[]

  /** Flow transitions with optional selectors for invoking actions. */
  readonly transitions: readonly PresentationFlowTestTransition[]
}

/**
 * Testable state node generated from a presentation flow state.
 */
export interface PresentationFlowTestState {
  /** Flow state identifier. */
  readonly id: string

  /** Presentation flow state kind from the IR. */
  readonly kind: FlowStateDefinition['Kind']

  /** Human-readable state name. */
  readonly name: string

  /** CSS selector that locates this state within the rendered flow. */
  readonly selector: string

  /** View rendered for this state, when the state declares one. */
  readonly viewId: string | null
}

/**
 * Testable transition edge generated from a presentation flow transition.
 */
export interface PresentationFlowTestTransition {
  /** Action invoked by this transition, when the transition is action-bound. */
  readonly actionId: string | null

  /** Event name that triggers the transition. */
  readonly event: string

  /** Source flow state identifier. */
  readonly fromStateId: string

  /** Guard expression or identifier declared by the transition, when present. */
  readonly guard: string | null

  /** Flow transition identifier. */
  readonly id: string

  /** CSS selector for the transition action, when the transition is action-bound. */
  readonly selector: string | null

  /** Destination flow state identifier. */
  readonly toStateId: string
}

/**
 * Creates DOM attributes for a semantic presentation subject.
 *
 * Null, undefined, and empty-string values are omitted so callers can spread the
 * result directly onto React elements.
 */
export function createPresentationTestAttributes({
  actionId,
  collectionSlotId,
  fieldId,
  flowId,
  flowStateId,
  formId,
  projectionId,
  routeId,
  rowId,
  viewId,
}: PresentationTestAttributeSubject): Record<string, string> {
  const entries: readonly (readonly [string, string | null | undefined])[] = [
    [presentationTestAttributes.actionId, actionId],
    [presentationTestAttributes.collectionSlotId, collectionSlotId],
    [presentationTestAttributes.fieldId, fieldId],
    [presentationTestAttributes.flowId, flowId],
    [presentationTestAttributes.flowStateId, flowStateId],
    [presentationTestAttributes.formId, formId],
    [presentationTestAttributes.projectionId, projectionId],
    [presentationTestAttributes.routeId, routeId],
    [presentationTestAttributes.rowId, rowId],
    [presentationTestAttributes.viewId, viewId],
  ]

  return Object.fromEntries(
    entries.filter((entry): entry is readonly [string, string] =>
      typeof entry[1] === 'string' && entry[1].length > 0),
  )
}

/**
 * Creates a CSS attribute selector for a Cohesive semantic test attribute.
 */
export function createPresentationAttributeSelector(
  attribute: string,
  value: string,
) {
  return `[${attribute}="${escapeCssAttributeValue(value)}"]`
}

/**
 * Selector factories for semantic presentation test attributes.
 */
export const presentationTestSelectors = {
  /** Locates an element by presentation action identifier. */
  action: (actionId: string) =>
    createPresentationAttributeSelector(
      presentationTestAttributes.actionId,
      actionId,
    ),

  /** Locates an element by collection slot identifier. */
  collectionSlot: (slotId: string) =>
    createPresentationAttributeSelector(
      presentationTestAttributes.collectionSlotId,
      slotId,
    ),

  /** Locates an element by presentation field identifier. */
  field: (fieldId: string) =>
    createPresentationAttributeSelector(
      presentationTestAttributes.fieldId,
      fieldId,
    ),

  /** Locates an element by presentation flow identifier. */
  flow: (flowId: string) =>
    createPresentationAttributeSelector(
      presentationTestAttributes.flowId,
      flowId,
    ),

  /** Locates a rendered flow state within a presentation flow. */
  flowState: (flowId: string, stateId: string) =>
    `${presentationTestSelectors.flow(flowId)}${createPresentationAttributeSelector(
      presentationTestAttributes.flowStateId,
      stateId,
    )}`,

  /** Locates an element by presentation form identifier. */
  form: (formId: string) =>
    createPresentationAttributeSelector(
      presentationTestAttributes.formId,
      formId,
    ),

  /** Locates an element by renderer-specific projection identifier. */
  projection: (projectionId: string) =>
    createPresentationAttributeSelector(
      presentationTestAttributes.projectionId,
      projectionId,
    ),

  /** Locates an element by navigation route identifier. */
  route: (routeId: string) =>
    createPresentationAttributeSelector(
      presentationTestAttributes.routeId,
      routeId,
    ),

  /** Locates an element by data row identifier. */
  row: (rowId: string) =>
    createPresentationAttributeSelector(
      presentationTestAttributes.rowId,
      rowId,
    ),

  /** Locates an element by presentation view identifier. */
  view: (viewId: string) =>
    createPresentationAttributeSelector(
      presentationTestAttributes.viewId,
      viewId,
    ),
} as const

/**
 * Generates a semantic test plan from a presentation flow definition.
 */
export function createPresentationFlowTestPlan(
  flow: FlowDefinition,
): PresentationFlowTestPlan {
  return {
    flowId: flow.Id,
    initialStateId: flow.InitialStateId,
    name: flow.Name,
    states: flow.States.map((state) => createPresentationFlowTestState(flow, state)),
    transitions: flow.Transitions.map(createPresentationFlowTestTransition),
  }
}

function createPresentationFlowTestState(
  flow: FlowDefinition,
  state: FlowStateDefinition,
): PresentationFlowTestState {
  return {
    id: state.Id,
    kind: state.Kind,
    name: state.Name,
    selector: presentationTestSelectors.flowState(flow.Id, state.Id),
    viewId: state.ViewId ?? null,
  }
}

function createPresentationFlowTestTransition(
  transition: FlowTransitionDefinition,
): PresentationFlowTestTransition {
  return {
    actionId: transition.ActionId ?? null,
    event: transition.Event,
    fromStateId: transition.FromStateId,
    guard: transition.Guard ?? null,
    id: transition.Id,
    selector: transition.ActionId
      ? presentationTestSelectors.action(transition.ActionId)
      : null,
    toStateId: transition.ToStateId,
  }
}

function escapeCssAttributeValue(value: string) {
  return value.replace(/\\/g, '\\\\').replace(/"/g, '\\"')
}
