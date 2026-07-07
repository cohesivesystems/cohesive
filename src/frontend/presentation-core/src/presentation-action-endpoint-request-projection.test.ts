import { describe, expect, it } from 'vitest'

import {
  actionKinds,
  actionScopeKinds,
  presentationBindingKinds,
  presentationValueKinds,
  type ActionDefinition,
  type ActionEndpointRequestProjectionDefinition,
  type PresentationValueDefinition,
} from '@cohesive/presentation-contracts'
import {
  projectPresentationActionEndpointRequest,
  projectRequiredPresentationActionEndpointRequest,
} from './index'

describe('presentation action endpoint request projection', () => {
  it('projects body fields and route parameters from literal, field, state, and fallback values', () => {
    const action = createAction([
      {
        Annotations: [],
        BodyFields: [
          binding('document', fieldValue('document.value')),
          binding('meta.mode', literalValue('preview')),
          binding('state.selectedId', stateValue('selectedId')),
        ],
        DataSourceId: 'documents',
        EndpointId: 'preview',
        RouteParameters: [
          binding('id', fieldValue('missing.id')),
          binding('optional', stateValue('optional'), true),
        ],
      },
    ])

    expect(projectPresentationActionEndpointRequest({
      action,
      dataSourceId: 'documents',
      endpointId: 'preview',
      sources: {
        document: { value: 'updated draft' },
        route: { id: 'route-42' },
        selectedId: 'selection-1',
      },
    })).toEqual({
      body: {
        document: 'updated draft',
        meta: { mode: 'preview' },
        state: { selectedId: 'selection-1' },
      },
      routeParameters: {
        id: 'route-42',
      },
    })
  })

  it('throws for required document projections that cannot resolve a value', () => {
    const action = createAction([
      {
        Annotations: [],
        BodyFields: [
          binding('document', fieldValue('missing.document')),
        ],
        EndpointId: 'save',
        RouteParameters: [],
      },
    ])

    expect(() => projectRequiredPresentationActionEndpointRequest({
      action,
      actionId: 'save',
      endpointId: 'save',
      sources: {},
    })).toThrow("Unable to project request body field 'document'")
  })
})

function createAction(
  EndpointRequests: readonly ActionEndpointRequestProjectionDefinition[],
): ActionDefinition {
  return {
    Annotations: [],
    Binding: {
      EndpointId: 'preview',
      Id: 'preview',
      Kind: presentationBindingKinds.actionEndpoint,
    },
    Enablement: [],
    EndpointRequests,
    Id: 'preview',
    Kind: actionKinds.effectAction,
    Name: 'Preview',
    Parameters: [],
    Scope: actionScopeKinds.view,
  } as unknown as ActionDefinition
}

function binding(
  TargetPath: string,
  Source: PresentationValueDefinition,
  OmitWhenNull = false,
) {
  return {
    Annotations: [],
    OmitWhenNull,
    Source,
    TargetPath,
  }
}

function fieldValue(Field: string): PresentationValueDefinition {
  return {
    Field,
    Kind: presentationValueKinds.field,
  }
}

function literalValue(Literal: string): PresentationValueDefinition {
  return {
    Kind: presentationValueKinds.literal,
    Literal,
  }
}

function stateValue(StateId: string): PresentationValueDefinition {
  return {
    Kind: presentationValueKinds.state,
    StateId,
  }
}
