import { describe, expect, it } from 'vitest'

import {
  collectionChromeSlotKinds,
  collectionSelectionActionParameterSources,
  viewChromeSlotKinds,
  type ViewDefinition,
} from '@cohesive/presentation-contracts'
import {
  createPresentationSurfaceFromRootView,
  getPresentationSurfaceSemanticNodes,
  getPresentationSurfaceViewTree,
  getPresentationViewProjectedActions,
  getPresentationViewProjectedDataSourceIds,
  getPresentationViewSemanticRole,
} from './presentation-semantics'

describe('presentation semantic projection', () => {
  it('projects data sources from view subject, explicit ids, and collection chrome', () => {
    const view = {
      Collection: {
        Chrome: {
          Slots: [
            {
              DataSourceIds: ['collection-source', 'view-source'],
            },
          ],
        },
      },
      DataSourceIds: ['view-source'],
      Subject: {
        DataSourceId: 'subject-source',
      },
    } as unknown as Pick<ViewDefinition, 'Collection' | 'DataSourceIds' | 'Subject'>

    expect(getPresentationViewProjectedDataSourceIds(view)).toEqual([
      'view-source',
      'subject-source',
      'collection-source',
    ])
  })

  it('projects view, chrome, and collection action references', () => {
    const view = {
      Actions: [
        { ActionId: 'refresh', Region: 'header' },
      ],
      Chrome: {
        Slots: [
          {
            Actions: [
              { ActionId: 'export', Region: 'header' },
            ],
            Id: 'view-actions',
            Kind: viewChromeSlotKinds.actions,
          },
        ],
      },
      Collection: {
        Chrome: {
          Slots: [
            {
              ActionIds: ['bulk-refresh'],
              Columns: [],
              DataSourceIds: [],
              FieldIds: [],
              Id: 'body',
              Kind: collectionChromeSlotKinds.body,
              Placement: 0,
              QueryFormId: null,
              RowActions: [
                {
                  ActionId: 'open-row',
                  Icon: null,
                  Id: 'open-row',
                  Label: null,
                  Parameters: [{ ValuePath: 'Id' }],
                },
              ],
              RowIdentityPath: 'Id',
              SelectionActions: [
                {
                  ActionId: 'delete-selected',
                  Icon: null,
                  Id: 'delete-selected',
                  Label: null,
                  Parameters: [
                    {
                      Source: collectionSelectionActionParameterSources.selectedRowValue,
                      ValuePath: 'Id',
                    },
                  ],
                },
              ],
              StateId: 'runs-selection',
            },
          ],
        },
      },
      Id: 'runs-view',
    } as unknown as Pick<ViewDefinition, 'Actions' | 'Chrome' | 'Collection' | 'Id'>

    expect(getPresentationViewProjectedActions(view).map((action) => action.kind))
      .toEqual([
        'view-action-placement',
        'view-chrome-action-placement',
        'collection-slot-action',
        'collection-row-action',
        'collection-selection-action',
      ])
  })

  it('walks surface view trees and semantic nodes', () => {
    const childView = createView({ id: 'child-view', role: 'detail' })
    const rootView = createView({
      childViewIds: ['child-view'],
      id: 'root-view',
      role: 'page',
    })
    const surface = createPresentationSurfaceFromRootView(rootView)
    const module = {
      Fields: [],
      Views: [rootView, childView],
      Workspaces: [],
    }

    expect(getPresentationViewSemanticRole(rootView)).toBe('surface-root')
    expect(getPresentationSurfaceViewTree(module, surface).map((view) => view.Id))
      .toEqual(['root-view', 'child-view'])
    expect(getPresentationSurfaceSemanticNodes(module, surface).map((node) => node.kind))
      .toContain('surface')
  })

  it('preserves explicit tabbed-surface design roles over the generic surface role', () => {
    expect(getPresentationViewSemanticRole(createView({
      id: 'default-tabs',
      kind: 'TabbedSurface',
      role: 'tabs',
    }))).toBe('surface-section')
    expect(getPresentationViewSemanticRole(createView({
      id: 'relation-tabs',
      kind: 'TabbedSurface',
      role: 'relation-workbench-tabs',
    }))).toBe('relation-workbench-tabs')
  })
})

function createView({
  childViewIds = [],
  id,
  kind = 0,
  role,
}: {
  readonly childViewIds?: readonly string[]
  readonly id: string
  readonly kind?: string | number
  readonly role: string
}): ViewDefinition {
  return {
    Actions: [],
    Chrome: { Slots: [] },
    Collection: null,
    DataSourceIds: [],
    Design: { Role: role },
    FieldIds: [],
    Id: id,
    Kind: kind,
    Regions: [
      {
        DataSourceIds: [],
        Id: 'content',
        ViewIds: [...childViewIds],
      },
    ],
    Subject: {},
  } as unknown as ViewDefinition
}
