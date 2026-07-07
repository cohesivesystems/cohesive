import { describe, expect, it } from 'vitest'

import {
  dataSourceKinds,
  dataSourcePaginationKinds,
  type DataSourceDefinition,
} from '@cohesive/presentation-contracts'
import {
  applyPresentationPaginationToRequest,
  createNextPresentationPaginationState,
  createPresentationPaginationSearch,
  inferPresentationPaginationBinding,
  presentationPaginationKinds,
  readPresentationPaginationStateFromSearch,
  resolvePresentationPageInfo,
  type PresentationPaginationBinding,
} from './index'

describe('presentation pagination runtime', () => {
  it('round-trips offset pagination through URL state and request projection', () => {
    const binding = {
      dataSourceId: 'orders',
      defaultPageSize: 25,
      kind: presentationPaginationKinds.offset,
      request: {
        limitField: 'Limit',
        offsetField: 'Offset',
      },
      response: {
        totalCountField: 'PageInfo.TotalCount',
      },
      url: {
        enabled: true,
        parameterPrefix: 'orders',
      },
    } satisfies PresentationPaginationBinding

    const state = readPresentationPaginationStateFromSearch(
      '?orders_page=3&orders_page_size=50&keep=true',
      binding,
    )

    expect(state).toEqual({
      kind: presentationPaginationKinds.offset,
      pageIndex: 2,
      pageSize: 50,
    })
    expect(applyPresentationPaginationToRequest({}, binding, state)).toEqual({
      Limit: 50,
      Offset: 100,
    })
    expect(createPresentationPaginationSearch('?orders_page=3&keep=true', binding, state))
      .toBe('?keep=true&orders_page=3&orders_page_size=50')
  })

  it('advances cursor pagination from response cursors and resolves page info', () => {
    const binding = {
      dataSourceId: 'runs',
      defaultPageSize: 2,
      kind: presentationPaginationKinds.cursor,
      request: {
        cursorField: 'ContinuationToken',
        limitField: 'Limit',
      },
      response: {
        cursorField: 'NextToken',
        hasNextPageField: 'PageInfo.HasNextPage',
        totalCountField: 'PageInfo.TotalCount',
      },
      url: {
        enabled: false,
        parameterPrefix: 'runs',
      },
    } satisfies PresentationPaginationBinding
    const initial = readPresentationPaginationStateFromSearch('', binding)
    const next = createNextPresentationPaginationState(binding, initial, {
      NextToken: 'cursor-2',
      PageInfo: {
        HasNextPage: true,
        TotalCount: 5,
      },
    })

    expect(next).toEqual({
      cursorHistory: [null, 'cursor-2'],
      kind: presentationPaginationKinds.cursor,
      pageIndex: 1,
      pageSize: 2,
    })
    expect(resolvePresentationPageInfo({
      binding,
      itemCount: 2,
      response: {
        NextToken: 'cursor-3',
        PageInfo: {
          HasNextPage: true,
          TotalCount: 5,
        },
      },
      state: next,
    })).toEqual({
      hasNextPage: true,
      itemCount: 2,
      pageIndex: 1,
      pageSize: 2,
      totalCount: 5,
      totalPageCount: 3,
    })
  })

  it('infers pagination bindings from declared data source pagination metadata', () => {
    const dataSource = {
      Id: 'documents',
      Kind: dataSourceKinds.collectionQuery,
      Name: 'Documents',
      Query: {
        Fields: [],
        Pagination: {
          Annotations: [],
          DefaultPageSize: 20,
          Kind: dataSourcePaginationKinds.pageNumber,
          Request: {
            LimitField: 'Limit',
            PageNumberField: 'PageNumber',
          },
          Response: {
            TotalCountField: 'TotalCount',
          },
          Url: {
            IsEnabled: true,
            ParameterPrefix: 'docs',
          },
        },
      },
    } as unknown as DataSourceDefinition

    expect(inferPresentationPaginationBinding({
      dataSource,
      defaultPageSize: 10,
      useUrl: true,
    })).toMatchObject({
      dataSourceId: 'documents',
      defaultPageSize: 20,
      kind: presentationPaginationKinds.pageNumber,
      request: {
        limitField: 'Limit',
        pageNumberField: 'PageNumber',
      },
      url: {
        enabled: true,
        parameterPrefix: 'docs',
      },
    })
  })
})
