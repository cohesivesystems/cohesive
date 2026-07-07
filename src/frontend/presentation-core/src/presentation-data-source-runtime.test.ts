import { describe, expect, it, vi } from 'vitest'

import {
  createPresentationDataSourceResolver,
  createPresentationViewActivityState,
  isPresentationViewFetching,
  readPresentationDataSourceItems,
  refreshPresentationDataSources,
  resolvePresentationViewPrimaryDataSourceId,
  type PresentationDataSourceStateMap,
  type ViewDefinition,
} from './index'

describe('presentation data-source runtime', () => {
  it('resolves data source state by semantic ids and paths', () => {
    const dataSources: PresentationDataSourceStateMap = {
      runs: {
        data: {
          Items: [{ Id: 'run-1' }],
          TotalCount: 1,
        },
      },
    }
    const resolver = createPresentationDataSourceResolver(dataSources)

    expect(resolver.read('runs')).toEqual({
      Items: [{ Id: 'run-1' }],
      TotalCount: 1,
    })
    expect(resolver.readPath('runs', 'totalCount')).toBe(1)
    expect(readPresentationDataSourceItems(resolver.resolve('runs'))).toEqual([
      { Id: 'run-1' },
    ])
  })

  it('resolves primary and activity state for a view', () => {
    const view = {
      Collection: null,
      DataSourceIds: ['fallback'],
      Subject: { DataSourceId: 'runs' },
    } as unknown as ViewDefinition
    const resolver = createPresentationDataSourceResolver({
      fallback: { data: [] },
      runs: {
        isPending: true,
        pendingLabel: 'Loading runs...',
      },
    })

    expect(resolvePresentationViewPrimaryDataSourceId(view)).toBe('runs')
    expect(resolver.resolveViewPrimary(view)).toEqual({
      isPending: true,
      pendingLabel: 'Loading runs...',
    })
    expect(createPresentationViewActivityState({ dataSourceResolver: resolver, view }))
      .toEqual({ kind: 'pending', label: 'Loading runs...' })
  })

  it('detects fetching and refreshes unique data sources', async () => {
    const refetchRuns = vi.fn()
    const refetchUsers = vi.fn()
    const view = {
      Collection: null,
      DataSourceIds: ['runs', 'users'],
      Subject: {},
    } as unknown as ViewDefinition
    const resolver = createPresentationDataSourceResolver({
      runs: { isFetching: true, refetch: refetchRuns },
      users: { refetch: refetchUsers },
    })

    expect(isPresentationViewFetching(view, resolver)).toBe(true)
    await refreshPresentationDataSources(resolver, ['runs', 'runs', 'users'])
    expect(refetchRuns).toHaveBeenCalledTimes(1)
    expect(refetchUsers).toHaveBeenCalledTimes(1)
  })
})
