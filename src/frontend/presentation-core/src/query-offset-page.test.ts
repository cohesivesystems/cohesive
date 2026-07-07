import { describe, expect, it } from 'vitest'

import { queryOffsetPage } from './index'

describe('queryOffsetPage', () => {
  it('uses URLSearchParams limit and offset values', () => {
    const page = queryOffsetPage([1, 2, 3, 4, 5], new URLSearchParams('limit=2&offset=1'))

    expect(page).toEqual({
      Items: [2, 3],
      Limit: 2,
      Offset: 1,
    })
  })

  it('uses object query values and defaults invalid integers', () => {
    const page = queryOffsetPage(['a', 'b', 'c'], {
      Limit: 'invalid',
      Offset: 1,
    })

    expect(page).toEqual({
      Items: ['b', 'c'],
      Limit: 10,
      Offset: 1,
    })
  })

  it('defaults missing limit and offset to legacy mock behavior', () => {
    expect(queryOffsetPage([1, 2], {})).toEqual({
      Items: [1, 2],
      Limit: 10,
      Offset: 0,
    })
  })
})
