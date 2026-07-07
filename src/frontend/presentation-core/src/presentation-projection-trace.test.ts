import { describe, expect, it } from 'vitest'

import {
  viewKinds,
  type ViewDefinition,
} from '@cohesive/presentation-contracts'
import {
  createNavigationRoute,
  createPageHost,
  createPresentationProjectionTrace,
  type NavigationDefinitionProjection,
} from './index'

describe('presentation projection trace', () => {
  it('builds a route, page-host, and nested view trace through resolver adapters', () => {
    const rootView = createView({
      DataSourceIds: ['orders'],
      FieldIds: ['OrderId'],
      Id: 'orders-root',
      Name: 'Orders',
      RegionViewIds: ['orders-detail'],
      SubjectDataSourceId: 'orders',
    })
    const detailView = createView({
      DataSourceIds: ['order-lines'],
      FieldIds: ['LineId'],
      Id: 'orders-detail',
      Name: 'Order detail',
    })
    const navigation = {
      PageHosts: [
        createPageHost({
          id: 'orders-host',
          viewId: 'orders-root',
        }),
      ],
      Routes: [
        createNavigationRoute({
          id: 'orders-route',
          pageHostId: 'orders-host',
          pathTemplate: '/orders',
        }),
      ],
    } as unknown as NavigationDefinitionProjection

    const trace = createPresentationProjectionTrace({
      module: {
        Views: [rootView, detailView],
      },
      navigation,
      pathname: '/orders',
      resolvePageHostRenderer: ({ pageHost, route }) => ({
        componentKey: null,
        componentRole: 'routed-surface',
        rendererKey: null,
        resolutionSource: pageHost && route ? 'component-role' : null,
        semanticRole: 'surface-root',
        targetBindingSource: 'target-page-host-binding',
      }),
      resolveViewRenderer: ({ view }) => ({
        componentKey: null,
        componentRole: view.Id === 'orders-root' ? 'surface-root' : null,
        rendererResolved: view.Id === 'orders-root',
        resolutionSource: view.Id === 'orders-root' ? 'semantic-role' : null,
        semanticRole: view.Id === 'orders-root' ? 'surface-root' : 'surface-section',
      }),
    })

    expect(trace).toMatchObject({
      dataSourceIds: ['orders', 'order-lines'],
      moduleAvailable: true,
      pageHost: {
        id: 'orders-host',
        viewId: 'orders-root',
      },
      pageHostRenderer: {
        componentRole: 'routed-surface',
        resolutionSource: 'component-role',
        targetBindingSource: 'target-page-host-binding',
      },
      route: {
        id: 'orders-route',
        pageHostId: 'orders-host',
      },
      surface: {
        id: 'orders-host',
        rootViewId: 'orders-root',
      },
    })
    expect(trace.views.map((view) => ({
      dataSourceIds: view.dataSourceIds,
      fieldIds: view.fieldIds,
      id: view.id,
      rendererResolved: view.rendererResolved,
      resolutionSource: view.resolutionSource,
      semanticRole: view.semanticRole,
    }))).toEqual([
      {
        dataSourceIds: ['orders'],
        fieldIds: ['OrderId'],
        id: 'orders-root',
        rendererResolved: true,
        resolutionSource: 'semantic-role',
        semanticRole: 'surface-root',
      },
      {
        dataSourceIds: ['order-lines'],
        fieldIds: ['LineId'],
        id: 'orders-detail',
        rendererResolved: false,
        resolutionSource: null,
        semanticRole: 'surface-section',
      },
    ])
  })
})

function createView({
  DataSourceIds = [],
  FieldIds = [],
  Id,
  Name,
  RegionViewIds = [],
  SubjectDataSourceId = null,
}: {
  readonly DataSourceIds?: readonly string[]
  readonly FieldIds?: readonly string[]
  readonly Id: string
  readonly Name: string
  readonly RegionViewIds?: readonly string[]
  readonly SubjectDataSourceId?: string | null
}): ViewDefinition {
  return {
    Actions: [],
    Annotations: [],
    DataSourceIds,
    FieldIds,
    Id,
    Kind: viewKinds.surface,
    Name,
    Regions: [
      {
        Actions: [],
        Annotations: [],
        DataSourceIds: [],
        Id: `${Id}.main`,
        Name: 'Main',
        ViewIds: RegionViewIds,
      },
    ],
    Subject: {
      DataSourceId: SubjectDataSourceId,
    },
  } as unknown as ViewDefinition
}
