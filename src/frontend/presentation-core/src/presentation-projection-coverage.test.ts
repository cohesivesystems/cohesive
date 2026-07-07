import { describe, expect, it } from 'vitest'

import {
  collectionChromeSlotKinds,
  collectionChromeSlotPlacements,
  dataSourceKinds,
  inputFormTargetKinds,
  pageHostComponentRoles,
  type DataSourceDefinition,
  viewKinds,
  viewSubjectKinds,
  type ViewDefinition,
} from '@cohesive/presentation-contracts'
import {
  projectPresentationDataSourceCoverageDiagnostics,
  projectPresentationTraceCoverageDiagnostics,
  type PresentationProjectionTrace,
} from './index'

describe('presentation projection coverage diagnostics', () => {
  it('reports missing route and renderer bindings from projection trace coverage', () => {
    const trace = createTrace({
      pageHostRendererResolutionSource: null,
      rendererResolved: false,
    })
    const diagnostics = projectPresentationTraceCoverageDiagnostics({
      module: {
        Views: [
          {
            Actions: [],
            DataSourceIds: [],
            FieldIds: [],
            Id: 'documents',
            Name: 'Documents',
            Regions: [],
            Subject: {},
          } as unknown as ViewDefinition,
        ],
      },
      trace,
    })

    expect(diagnostics.map((diagnostic) => diagnostic.id)).toEqual([
      'page-host.page-host.missing-renderer',
      'view.documents.missing-renderer',
    ])
    expect(diagnostics.map((diagnostic) => diagnostic.interpretation?.target)).toEqual([
      'page-host-renderer',
      'view-renderer',
    ])
  })

  it('reports missing data-source definitions and frontend bindings', () => {
    const orders = {
      Id: 'orders',
      Kind: dataSourceKinds.collectionQuery,
      Name: 'Orders',
      Parameters: [],
    } as unknown as DataSourceDefinition

    expect(projectPresentationDataSourceCoverageDiagnostics({
      bindings: [],
      dataSourceIds: ['orders', 'missing'],
      module: {
        DataSources: [orders],
      },
      sourceId: 'data-source-test',
    }).map((diagnostic) => diagnostic.id)).toEqual([
      'data-source.orders.missing-binding',
      'data-source.missing.missing-definition',
    ])
  })

  it('treats collection chrome slots as the collection view source of truth', () => {
    const trace = createTrace({
      pageHostRendererResolutionSource: 'component-role',
      rendererResolved: true,
    })
    const diagnostics = projectPresentationTraceCoverageDiagnostics({
      collectionChromeSlotRendererKeys: ['body:inline', 'query-form:above'],
      module: {
        InputForms: [
          {
            Actions: [],
            Fields: [],
            Groups: [],
            Id: 'orders-query-form',
            StateDataSourceId: 'orders-query-draft',
            Target: {
              Annotations: [],
              DataSourceId: 'orders',
              Id: '',
              Kind: inputFormTargetKinds.relationQuery,
            },
            Name: '',
            Suggestions: [],
            Shapes: undefined,
            Validation: undefined,
            Annotations: [],
          },
        ],
        QueryForms: [
          {
            Annotations: [],
            FormId: 'orders-query-form',
            Id: 'orders-query',
            Target: undefined,
          },
        ],
        Views: [
          {
            Actions: [],
            Collection: {
              Annotations: [],
              Chrome: {
                Annotations: [],
                Slots: [
                  {
                    ActionIds: [],
                    Annotations: [],
                    ClearSelectionOnQueryChange: false,
                    Columns: [],
                    DataSourceIds: ['orders'],
                    FieldIds: [],
                    Id: 'query',
                    Kind: collectionChromeSlotKinds.queryForm,
                    Name: 'Query',
                    Placement: collectionChromeSlotPlacements.above,
                    QueryFormId: 'orders-query',
                    RowActions: [],
                    SelectionActions: [],
                    ActivateOnRowClick: false,
                    SelectOnRowClick: false,
                  },
                  {
                    ActionIds: [],
                    Annotations: [],
                    ClearSelectionOnQueryChange: false,
                    Columns: [],
                    DataSourceIds: ['orders'],
                    FieldIds: [],
                    Id: 'body',
                    Kind: collectionChromeSlotKinds.body,
                    Name: 'Body',
                    Placement: collectionChromeSlotPlacements.inline,
                    RowActions: [],
                    SelectionActions: [],
                    ActivateOnRowClick: false,
                    SelectOnRowClick: false,
                  },
                ],
              },
            },
            DataSourceIds: [],
            FieldIds: [],
            Id: 'documents',
            Kind: viewKinds.collection,
            Name: 'Documents',
            Regions: [],
            Subject: {
              Kind: viewSubjectKinds.dataSource,
            },
          } as unknown as ViewDefinition,
        ],
      },
      queryFormStateAdapterIds: ['orders-query'],
      trace,
    })

    expect(diagnostics.map((diagnostic) => diagnostic.id))
      .not.toContain('view.documents.duplicated-collection-outer-view-data.documents')
    expect(diagnostics.map((diagnostic) => diagnostic.id))
      .not.toContain('view.documents.missing-query-form-state-adapter.orders-query')
  })

  it('does not require generic view renderer bindings inside a document workspace page host', () => {
    const trace = createTrace({
      pageHostComponentRole: pageHostComponentRoles.documentWorkspace,
      pageHostDocumentProfileId: 'shape-graph',
      pageHostRendererResolutionSource: 'component-role',
      rendererResolved: false,
      views: [
        {
          id: 'shape-graph-document',
          kind: String(viewKinds.documentWorkspace),
          name: 'Shape Graph Document Workspace',
          semanticRole: 'workspace-view',
        },
        {
          id: 'shape-graph-json',
          kind: String(viewKinds.panel),
          name: 'Shape Graph JSON',
          semanticRole: 'document-view',
        },
      ],
    })
    const diagnostics = projectPresentationTraceCoverageDiagnostics({
      module: {
        Views: [],
      },
      trace,
    })

    expect(diagnostics.map((diagnostic) => diagnostic.id)).not.toContain(
      'view.shape-graph-document.missing-renderer',
    )
    expect(diagnostics.map((diagnostic) => diagnostic.id)).not.toContain(
      'view.shape-graph-json.missing-renderer',
    )
  })
})

function createTrace({
  pageHostComponentRole = null,
  pageHostDocumentProfileId = null,
  pageHostRendererResolutionSource,
  rendererResolved,
  views,
}: {
  readonly pageHostComponentRole?: string | null
  readonly pageHostDocumentProfileId?: string | null
  readonly pageHostRendererResolutionSource: string | null
  readonly rendererResolved: boolean
  readonly views?: readonly {
    readonly id: string
    readonly kind: string
    readonly name: string
    readonly semanticRole: string
  }[]
}): PresentationProjectionTrace {
  return {
    dataSourceIds: [],
    moduleAvailable: true,
    pageHost: {
      documentProfileId: pageHostDocumentProfileId,
      id: 'page-host',
      kind: 'View',
      viewId: 'documents',
      workspaceId: null,
    },
    pageHostRenderer: {
      componentKey: null,
      componentRole: pageHostComponentRole,
      rendererKey: null,
      resolutionSource: pageHostRendererResolutionSource,
      semanticRole: 'view',
      targetBindingSource: null,
    },
    pathname: '/documents',
    route: {
      id: 'documents-route',
      label: 'Documents',
      pageHostId: 'page-host',
      pathTemplate: '/documents',
    },
    surface: {
      id: 'surface',
      rootViewId: 'documents',
      workspaceId: null,
    },
    views: (views ?? [
      {
        id: 'documents',
        kind: 'Collection',
        name: 'Documents',
        semanticRole: 'collection',
      },
    ]).map((view) => ({
        actionCount: 0,
        componentKey: null,
        componentRole: null,
        dataSourceIds: [],
        fieldIds: [],
        id: view.id,
        kind: view.kind,
        name: view.name,
        regions: [],
        rendererResolved,
        resolutionSource: null,
        semanticRole: view.semanticRole,
        subjectDataSourceId: null,
      })),
  }
}
