import { describe, expect, it } from 'vitest'

import {
  actionKinds,
  actionScopeKinds,
  actionSemanticsKinds,
  documentWorkspaceActionKinds,
  preparationKinds,
  presentationBindingKinds,
  type ActionDefinition,
  type PresentationModuleDefinition,
} from '@cohesive/presentation-contracts'
import { createPresentationActionRuntimeBinding } from './index'

describe('presentation action runtime binding', () => {
  it('matches action semantics across generated numeric values and labels', () => {
    const action = {
      Binding: {
        Id: 'preview',
        Kind: 'ActionEndpoint',
      },
      Id: 'preview',
      Kind: 'ProcessStartAction',
      Preparation: {
        Kind: 'PreviewFlow',
      },
      Scope: 'View',
      Semantics: {
        DocumentWorkspace: {
          Kind: 'ProcessPreview',
        },
        Kind: 'DocumentWorkspace',
      },
    } as unknown as ActionDefinition
    const binding = createPresentationActionRuntimeBinding({
      bindingKind: presentationBindingKinds.actionEndpoint,
      documentWorkspaceKind: documentWorkspaceActionKinds.processPreview,
      id: 'preview-runtime',
      kind: actionKinds.processStartAction,
      predicate: ({ actionId }) => actionId === 'preview',
      preparationKind: preparationKinds.previewFlow,
      project: () => ({ label: 'Preview' }),
      scope: actionScopeKinds.view,
      semanticsKind: actionSemanticsKinds.documentWorkspace,
    })

    expect(binding.matches({
      action,
      actionId: 'preview',
      module: { Actions: [action] } as unknown as PresentationModuleDefinition,
    })).toBe(true)
    expect(binding.project({
      action,
      actionId: 'preview',
      module: { Actions: [action] } as unknown as PresentationModuleDefinition,
    })).toEqual({ label: 'Preview' })
  })
})
