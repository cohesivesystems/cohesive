import { describe, expect, it } from 'vitest'

import {
  actionKinds,
  actionScopeKinds,
  presentationBindingKinds,
  presentationTargetKinds,
  type ActionDefinition,
  type PresentationBindingDefinition,
  type PresentationModuleDefinition,
} from '@cohesivesystems/presentation-contracts'
import { resolvePresentationActionEndpointBinding } from './index'

describe('presentation action endpoint binding resolution', () => {
  it('prefers target data-source endpoint bindings over generic bindings', () => {
    const module = {
      Actions: [
        createAction({
          Binding: {
            EndpointId: 'fallback-endpoint',
            Id: 'preview',
            Kind: presentationBindingKinds.apiEndpoint,
          },
          Id: 'preview',
        }),
      ],
      Targets: [
        {
          Bindings: [
            {
              DataSourceId: 'documents',
              EndpointId: 'document-preview-endpoint',
              Id: 'preview',
              Kind: presentationBindingKinds.actionEndpoint,
            },
            {
              EndpointId: 'generic-preview-endpoint',
              Id: 'preview',
              Kind: presentationBindingKinds.actionEndpoint,
            },
          ],
          ComponentSet: 'react-shadcn',
          Target: presentationTargetKinds.react,
        },
      ],
    } as unknown as PresentationModuleDefinition

    expect(resolvePresentationActionEndpointBinding({
      actionId: 'preview',
      componentSet: 'react-shadcn',
      dataSourceId: 'documents',
      module,
    })?.EndpointId).toBe('document-preview-endpoint')

    expect(resolvePresentationActionEndpointBinding({
      actionId: 'preview',
      componentSet: 'react-shadcn',
      dataSourceId: 'runs',
      module,
    })?.EndpointId).toBe('generic-preview-endpoint')
  })

  it('falls back to the action definition endpoint binding', () => {
    const fallback = {
      EndpointId: 'action-definition-endpoint',
      Id: 'save',
      Kind: presentationBindingKinds.apiEndpoint,
    } satisfies PresentationBindingDefinition
    const module = {
      Actions: [
        createAction({
          Binding: fallback,
          Id: 'save',
        }),
      ],
      Targets: [],
    } as unknown as PresentationModuleDefinition

    expect(resolvePresentationActionEndpointBinding({
      actionId: 'save',
      module,
    })).toBe(fallback)
  })
})

function createAction({
  Binding,
  Id,
}: {
  readonly Binding: PresentationBindingDefinition
  readonly Id: string
}): ActionDefinition {
  return {
    Annotations: [],
    Binding,
    Enablement: [],
    EndpointRequests: [],
    Id,
    Kind: actionKinds.effectAction,
    Name: Id,
    Parameters: [],
    Scope: actionScopeKinds.view,
  } as unknown as ActionDefinition
}
