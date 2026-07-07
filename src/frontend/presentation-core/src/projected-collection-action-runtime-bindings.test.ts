import { describe, expect, it } from 'vitest'

import {
  actionKinds,
  actionScopeKinds,
  presentationBindingKinds,
  type ActionDefinition,
  type PresentationModuleDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  projectProjectedCollectionActionRuntimeBindings,
  type ProjectedCollectionActionExecutionContext,
} from './index'

describe('projected collection action runtime bindings', () => {
  it('projects collection navigation actions to local navigation execution', () => {
    const navigated: string[] = []
    const [binding] = projectProjectedCollectionActionRuntimeBindings({
      navigateHref: (href) => navigated.push(href),
    })
    const action = createNavigationAction()
    const context = {
      action,
      actionRef: {
        actionId: action.Id,
      },
      contextKind: 'collection-row',
      href: '/documents/doc-1',
      parameters: { id: 'doc-1' },
      row: { Id: 'doc-1' },
      rowAction: {
        ActionId: action.Id,
      },
    } as unknown as ProjectedCollectionActionExecutionContext<{ Id: string }>

    expect(binding?.matches({
      action,
      actionId: action.Id,
      module: { Actions: [action] } as unknown as PresentationModuleDefinition,
    })).toBe(true)

    const runtime = binding?.project({
      action,
      actionId: action.Id,
      module: { Actions: [action] } as unknown as PresentationModuleDefinition,
    })
    expect(runtime?.canExecute?.(context)).toBe(true)
    runtime?.execute?.(context)
    expect(navigated).toEqual(['/documents/doc-1'])
  })
})

function createNavigationAction(): ActionDefinition {
  return {
    Annotations: [],
    Binding: {
      Id: 'open-document',
      Kind: presentationBindingKinds.navigationRoute,
      RouteId: 'document-details',
    },
    Enablement: [],
    EndpointRequests: [],
    Id: 'open-document',
    Kind: actionKinds.navigationAction,
    Name: 'Open document',
    Parameters: [],
    Scope: actionScopeKinds.view,
  } as unknown as ActionDefinition
}
