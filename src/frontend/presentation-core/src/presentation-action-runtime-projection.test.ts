import { describe, expect, it } from 'vitest'

import {
  actionKinds,
  actionScopeKinds,
  presentationBindingKinds,
  type ActionDefinition,
  type PresentationModuleDefinition,
} from '@cohesive/presentation-contracts'
import {
  createPresentationActionRuntimeBinding,
  projectPresentationActionRuntimeRegistry,
} from './index'

describe('presentation action runtime projection', () => {
  it('uses the first matching projection and skips missing action ids', () => {
    const first = createAction('first')
    const second = createAction('second')
    const module = {
      Actions: [first, second],
    } as unknown as PresentationModuleDefinition

    expect(projectPresentationActionRuntimeRegistry({
      actionIds: ['first', 'missing', 'second'],
      module,
      projections: [
        createPresentationActionRuntimeBinding({
          actionId: 'first',
          id: 'first-specific',
          project: () => ({ label: 'first-specific' }),
        }),
        createPresentationActionRuntimeBinding({
          actionId: ['first', 'second'],
          id: 'fallback',
          project: ({ actionId }) => ({ label: `fallback-${actionId}` }),
        }),
      ],
    })).toEqual({
      first: { label: 'first-specific' },
      second: { label: 'fallback-second' },
    })
  })
})

function createAction(Id: string): ActionDefinition {
  return {
    Annotations: [],
    Binding: {
      Id,
      Kind: presentationBindingKinds.localState,
    },
    Enablement: [],
    EndpointRequests: [],
    Id,
    Kind: actionKinds.localStateAction,
    Name: Id,
    Parameters: [],
    Scope: actionScopeKinds.view,
  } as unknown as ActionDefinition
}
