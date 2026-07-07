import { describe, expect, it } from 'vitest'

import {
  actionKinds,
  actionScopeKinds,
  presentationBindingKinds,
  type ActionDefinition,
  type ActionPlacementDefinition,
  type PresentationModuleDefinition,
} from '@cohesive/presentation-contracts'
import { projectPresentationActionRuntimeBindingDiagnostics } from './index'

describe('presentation action runtime diagnostics', () => {
  it('reports missing action definitions and placed actions without execute bindings', () => {
    const unbound = createAction('unbound')
    const bound = createAction('bound')
    const placements = [
      placement('missing'),
      placement('unbound'),
      placement('bound'),
    ]

    const diagnostics = projectPresentationActionRuntimeBindingDiagnostics({
      actionPlacements: placements,
      module: {
        Actions: [unbound, bound],
      } as unknown as PresentationModuleDefinition,
      runtimes: {
        bound: {
          execute: () => undefined,
        },
      },
      source: 'test-view',
    })

    expect(diagnostics.map((diagnostic) => diagnostic.id)).toEqual([
      'action-runtime.missing.missing-action',
      'action-runtime.unbound.missing-execute-binding',
    ])
    expect(diagnostics.map((diagnostic) => diagnostic.severity)).toEqual(['error', 'warning'])
  })
})

function placement(ActionId: string): ActionPlacementDefinition {
  return {
    ActionId,
    Intent: 'primary',
    Region: 'toolbar',
  }
}

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
