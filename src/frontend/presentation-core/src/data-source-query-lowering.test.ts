import { describe, expect, it } from 'vitest'

import { queryLoweringKinds, type DataSourceDefinition } from '@cohesive/presentation-contracts'
import {
  createDataSourceEndpointQueryRequest,
  createDataSourcePaginationRequest,
  findDataSourceQueryEndpointBinding,
  lowerDataSourceQueryValueToEndpointRequest,
} from './data-source-query-lowering'

describe('data source query lowering', () => {
  it('lowers presentation values into endpoint requests', () => {
    const dataSource = createQueryDataSource()

    expect(lowerDataSourceQueryValueToEndpointRequest({
      dataSource,
      defaultRequest: { includeArchived: false },
      endpointId: 'searchRuns',
      transforms: {
        trim: (value) => typeof value === 'string' ? value.trim() : value,
      },
      value: {
        filters: {
          status: ' ready ',
        },
      },
    })).toEqual({
      includeArchived: false,
      query: {
        status: 'ready',
        take: 25,
      },
    })
  })

  it('merges lowered query and pagination requests without undefined values', () => {
    const dataSource = createQueryDataSource()

    expect(createDataSourceEndpointQueryRequest({
      dataSource,
      endpointId: 'searchRuns',
      paginationRequest: { cursor: undefined, page: 2 },
      value: {
        filters: {
          status: 'active',
        },
      },
    })).toEqual({
      page: 2,
      query: {
        status: 'active',
        take: 25,
      },
    })
  })

  it('creates pagination requests from declared field bindings', () => {
    const dataSource = createQueryDataSource()

    expect(createDataSourcePaginationRequest(dataSource, {
      cursor: 'next',
      limit: 50,
      offset: undefined,
    })).toEqual({
      paging: {
        cursor: 'next',
        limit: 50,
      },
    })
    expect(findDataSourceQueryEndpointBinding(dataSource, 'missing')).toBeNull()
  })
})

function createQueryDataSource(): DataSourceDefinition {
  return {
    Id: 'runs',
    Query: {
      EndpointBindings: [
        {
          EndpointId: 'searchRuns',
          Lowerings: [
            {
              FieldBindings: [
                {
                  DefaultValue: null,
                  SourcePath: 'filters.status',
                  TargetPath: 'query.status',
                  Transform: 'trim',
                },
                {
                  DefaultValue: '25',
                  SourcePath: 'filters.take',
                  TargetPath: 'query.take',
                  Transform: null,
                },
              ],
              Kind: queryLoweringKinds.presentationValueToEndpointRequest,
            },
          ],
        },
      ],
      Pagination: {
        Request: {
          CursorField: 'paging.cursor',
          LimitField: 'paging.limit',
          OffsetField: 'paging.offset',
        },
      },
    },
  } as unknown as DataSourceDefinition
}
