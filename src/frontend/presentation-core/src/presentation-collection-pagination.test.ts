import { describe, expect, it } from 'vitest'

import type {
  DataSourceDefinition,
  PresentationModuleDefinition,
  QueryFormDefinition,
} from './module'
import {
  createPresentationCollectionPaginationBindings,
  defaultPresentationCollectionPageSize,
} from './presentation-collection-pagination'
import {
  dataSourceKinds,
} from '@cohesive/presentation-contracts'

describe('createPresentationCollectionPaginationBindings', () => {
  it('uses the configured fallback page size when the IR has no default', () => {
    const [binding] = createPresentationCollectionPaginationBindings({
      dataSourceIds: ['orders'],
      defaultPageSize: 25,
      module: createModule({
        dataSources: [createCollectionQueryDataSource('orders')],
      }),
    })

    expect(binding?.defaultPageSize).toBe(25)
  })

  it('falls back to the standard collection page size for invalid configured values', () => {
    const [binding] = createPresentationCollectionPaginationBindings({
      dataSourceIds: ['orders'],
      defaultPageSize: 0,
      module: createModule({
        dataSources: [createCollectionQueryDataSource('orders')],
      }),
    })

    expect(binding?.defaultPageSize).toBe(defaultPresentationCollectionPageSize)
  })

  it('prefers synchronized query-form default limits over the configured fallback', () => {
    const [binding] = createPresentationCollectionPaginationBindings({
      dataSourceIds: ['orders'],
      defaultPageSize: 25,
      module: createModule({
        dataSources: [createCollectionQueryDataSource('orders')],
        queryForms: [createQueryForm('orders', 50)],
      }),
    })

    expect(binding?.defaultPageSize).toBe(50)
  })
})

function createModule({
  dataSources,
  queryForms = [],
}: {
  readonly dataSources: readonly DataSourceDefinition[]
  readonly queryForms?: readonly QueryFormDefinition[]
}): PresentationModuleDefinition {
  return {
    DataSources: dataSources,
    QueryForms: queryForms,
  } as PresentationModuleDefinition
}

function createCollectionQueryDataSource(id: string): DataSourceDefinition {
  return {
    Id: id,
    Kind: dataSourceKinds.collectionQuery,
    Query: {
      Fields: [
        {
          RequestPaths: ['Limit', 'Offset'],
        },
      ],
    },
  } as DataSourceDefinition
}

function createQueryForm(
  resultDataSourceId: string,
  defaultLimit: number,
): QueryFormDefinition {
  return {
    Target: {
      Result: {
        DataSourceId: resultDataSourceId,
        DefaultLimit: defaultLimit,
      },
      State: {
        ResultDataSourceId: resultDataSourceId,
        SynchronizedDataSourceIds: [],
      },
    },
  } as QueryFormDefinition
}
