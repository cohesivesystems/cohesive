import { describe, expect, it } from 'vitest'

import {
  createPresentationFlowTestPlan,
  createPresentationTestAttributes,
  presentationTestAttributes,
  presentationTestEvents,
  presentationTestSelectors,
} from './presentation-test-selectors'
import {
  flowStateKinds,
  presentationTestSelectors as generatedPresentationTestSelectors,
  residencyHints,
} from '@cohesivesystems/presentation-contracts'
import type { FlowDefinition } from '@cohesivesystems/presentation-contracts'

describe('presentation test selectors', () => {
  it('uses generated presentation test attribute constants', () => {
    expect(presentationTestAttributes).toEqual({
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
    })
  })

  it('creates stable data attributes from semantic ids', () => {
    expect(
      createPresentationTestAttributes({
        actionId: 'save',
        fieldId: null,
        viewId: 'editor',
      }),
    ).toEqual({
      'data-presentation-action-id': 'save',
      'data-presentation-view-id': 'editor',
    })
  })

  it('escapes css attribute selectors', () => {
    expect(presentationTestSelectors.view('quoted"view')).toBe(
      '[data-presentation-view-id="quoted\\"view"]',
    )
  })

  it('declares stable semantic test events for projected tools', () => {
    expect(presentationTestEvents.setDocumentText).toBe(
      'cohesive:presentation.test.set-document-text',
    )
  })

  it('projects flow states and action transitions into a test plan', () => {
    const flow: FlowDefinition = {
      Annotations: [],
      Id: 'save-flow',
      InitialStateId: 'editing',
      Name: 'Save Flow',
      Residency: residencyHints.client,
      States: [
        {
          Id: 'editing',
          Kind: flowStateKinds.idle,
          Name: 'Editing',
          ViewId: null,
        },
        {
          Id: 'review',
          Kind: flowStateKinds.prompt,
          Name: 'Review',
          ViewId: 'save-review',
        },
      ],
      Transitions: [
        {
          ActionId: 'save',
          Event: 'save-requested',
          FromStateId: 'editing',
          Guard: null,
          Id: 'save-requested',
          ToStateId: 'review',
        },
      ],
      Variables: [],
    }

    expect(createPresentationFlowTestPlan(flow)).toEqual({
      flowId: 'save-flow',
      initialStateId: 'editing',
      name: 'Save Flow',
      states: [
        {
          id: 'editing',
          kind: flowStateKinds.idle,
          name: 'Editing',
          selector:
            '[data-presentation-flow-id="save-flow"][data-presentation-flow-state-id="editing"]',
          viewId: null,
        },
        {
          id: 'review',
          kind: flowStateKinds.prompt,
          name: 'Review',
          selector:
            '[data-presentation-flow-id="save-flow"][data-presentation-flow-state-id="review"]',
          viewId: 'save-review',
        },
      ],
      transitions: [
        {
          actionId: 'save',
          event: 'save-requested',
          fromStateId: 'editing',
          guard: null,
          id: 'save-requested',
          selector: '[data-presentation-action-id="save"]',
          toStateId: 'review',
        },
      ],
    })
  })
})
