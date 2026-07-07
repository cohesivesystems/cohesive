import { describe, expect, it } from 'vitest'

import {
  createNavigationHref,
  createNavigationRoute,
  createNavigationRouteInstanceKey,
  doesRouteTemplateMatch,
  findNavigationRoute,
  resolveNavigationRouteId,
  toPathSegments,
  type NavigationDefinitionProjection,
} from './navigation'

describe('navigation route projection', () => {
  it('creates hrefs from semantic route parameters', () => {
    const navigation = createNavigationFixture()

    expect(createNavigationHref(navigation, 'run-detail', { runId: 'run 1' }))
      .toBe('/runs/run%201')
    expect(createNavigationHref(navigation, 'run-detail', {})).toBeNull()
    expect(createNavigationHref(navigation, 'missing', { runId: 'run-1' })).toBeNull()
  })

  it('matches route templates against concrete paths', () => {
    const navigation = createNavigationFixture()

    expect(resolveNavigationRouteId(navigation, '/runs/run-1')).toBe('run-detail')
    expect(createNavigationRouteInstanceKey(navigation, {
      pathname: '/runs/run-1',
      search: '?tab=events',
    })).toBe('run-detail')
    expect(doesRouteTemplateMatch('/runs/run-1?tab=events', '/runs/{runId}')).toBe(true)
    expect(toPathSegments('/runs/run-1?tab=events')).toEqual(['runs', 'run-1'])
  })

  it('creates conventional route definitions', () => {
    const route = createNavigationRoute({
      id: 'training-runs',
      pageHostId: 'training-runs-page',
      pathTemplate: '/training-runs',
    })

    expect(route.Label).toBe('Training Runs')
    expect(route.Parameters).toEqual([])
  })
})

function createNavigationFixture(): NavigationDefinitionProjection {
  const route = createNavigationRoute({
    id: 'run-detail',
    pageHostId: 'run-detail-page',
    parameterName: 'runId',
    pathTemplate: '/runs/{runId}',
  })

  return {
    Contexts: [],
    PageHosts: [],
    Routes: [route],
    Shell: {
      Frames: [],
      Regions: [],
      Slots: [],
    },
    Nodes: [],
    Actions: [],
  } as unknown as NavigationDefinitionProjection
}
