import { describe, expect, it } from 'vitest'

import {
  actionKinds,
  actionScopeKinds,
  collectionChromeSlotKinds,
  collectionChromeSlotPlacements,
  collectionRowActionKinds,
  presentationBindingKinds,
  type ActionDefinition,
  type CollectionChromeSlotDefinition,
  type PresentationModuleDefinition,
  type ViewDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  createPresentationProjectionDiagnostic,
  createProjectedCollectionRuntime,
  projectProjectedCollectionActionRuntimeDiagnostics,
} from './index'

describe('projected collection action runtime diagnostics', () => {
  it('reports missing runtimes and accepts an injected icon diagnostic projector', () => {
    const action = createAction('open-document')
    const view = createView()
    const collectionRuntime = createProjectedCollectionRuntime({
      data: [{ Id: 'doc-1' }],
      module: {
        Actions: [action],
        Fields: [],
      } as unknown as PresentationModuleDefinition,
      view,
    })

    const diagnostics = projectProjectedCollectionActionRuntimeDiagnostics({
      actionRuntimes: {},
      collectionRuntime,
      module: {
        Actions: [action],
        Targets: [],
      } as unknown as PresentationModuleDefinition,
      projectActionIconDiagnostics: ({ actionPlacements, source, surfaceId }) => [
        createPresentationProjectionDiagnostic({
          details: {
            actionCount: actionPlacements.length,
            surfaceId,
          },
          id: 'icon-diagnostic',
          message: source,
          severity: 'info',
          source,
        }),
      ],
      view,
    })

    expect(diagnostics.map((diagnostic) => diagnostic.id)).toEqual([
      'icon-diagnostic',
      'collection-action-runtime.documents-view.open-document.missing-runtime.collection-row-action.body.open',
    ])
    expect(diagnostics[1]).toMatchObject({
      interpretation: {
        status: 'unbound',
        target: 'collection-action-runtime-registry',
      },
      severity: 'warning',
    })
  })
})

function createView(): ViewDefinition {
  return {
    Actions: [],
    Collection: {
      Annotations: [],
      Chrome: {
        Annotations: [],
        Slots: [
          createSlot({
            Id: 'body',
            Kind: collectionChromeSlotKinds.body,
            RowActions: [
              {
                ActionId: 'open-document',
                Annotations: [],
                Icon: 'external-link',
                Id: 'open',
                Kind: collectionRowActionKinds.primary,
                Label: 'Open',
                Order: 0,
                Parameters: [],
              },
            ],
          }),
        ],
      },
    },
    Id: 'documents-view',
    Name: 'Documents',
  } as unknown as ViewDefinition
}

function createSlot(
  overrides: Partial<CollectionChromeSlotDefinition>,
): CollectionChromeSlotDefinition {
  return {
    ActionIds: [],
    ActivateOnRowClick: false,
    Annotations: [],
    ClearSelectionOnQueryChange: true,
    Columns: [],
    DataSourceIds: ['documents'],
    FieldIds: [],
    Id: 'slot',
    Kind: collectionChromeSlotKinds.body,
    Name: 'Slot',
    Placement: collectionChromeSlotPlacements.inline,
    RowActions: [],
    SelectOnRowClick: false,
    SelectionActions: [],
    ...overrides,
  } as unknown as CollectionChromeSlotDefinition
}

function createAction(Id: string): ActionDefinition {
  return {
    Annotations: [],
    Binding: {
      Id,
      Kind: presentationBindingKinds.navigationRoute,
    },
    Enablement: [],
    EndpointRequests: [],
    Id,
    Kind: actionKinds.navigationAction,
    Name: Id,
    Parameters: [],
    Scope: actionScopeKinds.view,
  } as unknown as ActionDefinition
}
