import { describe, expect, it } from 'vitest'

import {
  actionKinds,
  actionScopeKinds,
  collectionChromeSlotKinds,
  collectionChromeSlotPlacements,
  collectionRowActionKinds,
  collectionSelectionActionParameterSources,
  collectionSelectionModes,
  presentationBindingKinds,
  presentationValueKinds,
  type ActionDefinition,
  type CollectionChromeSlotDefinition,
  type CollectionSelectionMode,
  type FieldPresentationDefinition,
  type PresentationModuleDefinition,
  type ViewDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  createProjectedCollectionRuntime,
  type CollectionSelectionStateEntry,
} from './index'

describe('projected collection runtime', () => {
  it('projects columns, selection, row actions, and selection actions from collection IR', () => {
    const module = {
      Actions: [
        createNavigationAction('open-document', 'document-details', ['id']),
        createNavigationAction('compare-documents', 'document-compare', ['ids']),
      ],
      Fields: [
        {
          Field: 'Name',
          Id: 'name',
          Label: 'Name',
        } as unknown as FieldPresentationDefinition,
      ],
    } as unknown as PresentationModuleDefinition
    const selectedRowIds: string[] = ['doc-2']
    const selectionState = createSelectionState(selectedRowIds)
    const view = createCollectionView()
    const runtime = createProjectedCollectionRuntime({
      createHref: (routeId, parameters) => `${routeId}?${new URLSearchParams(
        Object.entries(parameters ?? {}).map(([key, value]) => [key, String(value)]),
      )}`,
      data: [
        { Id: 'doc-1', Name: 'Alpha', CanOpen: true },
        { Id: 'doc-2', Name: 'Beta', CanOpen: false },
      ],
      module,
      selectionState,
      view,
    })

    expect(runtime.columns.map((column) => [column.id, column.header])).toEqual([
      ['name-column', 'Name'],
    ])
    expect(runtime.columns[0]?.readValue(runtime.data[0])).toBe('Alpha')
    expect(runtime.selection.selectedRows).toEqual([
      { Id: 'doc-2', Name: 'Beta', CanOpen: false },
    ])
    expect(runtime.readRowLabel(runtime.data[0])).toBe('Alpha')

    const rowItems = runtime.actions.resolveRowActionItems(
      runtime.data[0],
      runtime.actions.rowActions,
    )
    expect(rowItems).toHaveLength(1)
    expect(rowItems[0]?.actionContext).toMatchObject({
      contextKind: 'collection-row',
      href: 'document-details?id=doc-1',
      parameters: { id: 'doc-1' },
    })

    const disabledRowItems = runtime.actions.resolveRowActionItems(
      runtime.data[1],
      runtime.actions.rowActions,
    )
    expect(disabledRowItems[0]?.isEnabled).toBe(false)

    const selectionItems = runtime.actions.resolveSelectionActionItems()
    expect(selectionItems).toHaveLength(1)
    expect(selectionItems[0]?.actionContext).toMatchObject({
      contextKind: 'collection-selection',
      href: 'document-compare?ids=doc-2',
      parameters: { ids: 'doc-2' },
    })

    runtime.selection.activateRow(runtime.data[0], 0)
    expect(selectedRowIds).toEqual(['doc-2', 'doc-1'])
  })
})

function createCollectionView(): ViewDefinition {
  return {
    Actions: [],
    Collection: {
      Annotations: [],
      Chrome: {
        Annotations: [],
        Slots: [
          createSlot({
            Columns: [
              {
                Annotations: [],
                FieldId: 'name',
                Id: 'name-column',
                IsVisible: true,
                Order: 0,
                ValuePath: 'Name',
              },
            ],
            Id: 'body',
            Kind: collectionChromeSlotKinds.body,
            Placement: collectionChromeSlotPlacements.inline,
            RowActions: [
              {
                ActionId: 'open-document',
                Annotations: [],
                Icon: 'external-link',
                Id: 'open',
                IsEnabled: {
                  Field: 'CanOpen',
                  Kind: presentationValueKinds.field,
                },
                Kind: collectionRowActionKinds.primary,
                Label: 'Open',
                Order: 0,
                Parameters: [
                  {
                    Annotations: [],
                    Name: 'id',
                    OmitWhenNull: false,
                    ValuePath: 'Id',
                  },
                ],
              },
            ],
            RowIdentityPath: 'Id',
            RowLabelPath: 'Name',
            SelectionActions: [
              {
                ActionId: 'compare-documents',
                Annotations: [],
                Id: 'compare',
                Label: 'Compare',
                MaximumSelectionCount: null,
                MinimumSelectionCount: 1,
                Order: 0,
                Parameters: [
                  {
                    Annotations: [],
                    Name: 'ids',
                    OmitWhenEmpty: false,
                    Source: collectionSelectionActionParameterSources.selectedRowIdentityList,
                  },
                ],
              },
            ],
            SelectionMode: collectionSelectionModes.multiple,
            StateId: 'documents.selection',
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

function createNavigationAction(
  Id: string,
  RouteId: string,
  parameterNames: readonly string[],
): ActionDefinition {
  return {
    Annotations: [],
    Binding: {
      Id,
      Kind: presentationBindingKinds.navigationRoute,
      RouteId,
    },
    Enablement: [],
    EndpointRequests: [],
    Id,
    Kind: actionKinds.navigationAction,
    Name: Id,
    Parameters: parameterNames.map((Name) => ({
      IsRequired: true,
      Name,
      Type: 'string',
    })),
    Scope: actionScopeKinds.view,
  } as unknown as ActionDefinition
}

function createSelectionState(selectedRowIds: string[]): CollectionSelectionStateEntry {
  const syncSelectedRowId = () => selectedRowIds[0] ?? null
  return {
    clearSelection: () => {
      selectedRowIds.splice(0)
    },
    get selectedRowId() {
      return syncSelectedRowId()
    },
    selectedRowIds,
    selectionStateId: 'documents.selection',
    selectRowId: (rowId) => {
      selectedRowIds.splice(0, selectedRowIds.length, rowId)
    },
    setSelectedRowIds: (rowIds) => {
      selectedRowIds.splice(0, selectedRowIds.length, ...rowIds)
    },
    toggleRowId: (rowId, mode: CollectionSelectionMode) => {
      if (mode !== collectionSelectionModes.multiple) {
        selectedRowIds.splice(0, selectedRowIds.length, rowId)
        return
      }

      const index = selectedRowIds.indexOf(rowId)
      if (index >= 0) {
        selectedRowIds.splice(index, 1)
      } else {
        selectedRowIds.push(rowId)
      }
    },
  }
}
