import { describe, expect, it } from 'vitest'

import {
  parseObjectPath,
  readObjectPath,
  readObjectProperty,
  writeObjectPath,
} from './object-path'

describe('parseObjectPath', () => {
  it('returns normalized non-empty path segments', () => {
    expect(parseObjectPath(' result..items. ')).toEqual(['result', 'items'])
  })
})

describe('readObjectProperty', () => {
  it('reads exact properties before case-insensitive fallback', () => {
    const value = {
      Name: 'semantic',
      name: 'exact',
    }

    expect(readObjectProperty(value, 'name')).toBe('exact')
  })

  it('falls back to case-insensitive generated payload properties', () => {
    const value = {
      TotalCount: 42,
    }

    expect(readObjectProperty(value, 'totalCount')).toBe(42)
  })

  it('can disable case-insensitive fallback', () => {
    const value = {
      TotalCount: 42,
    }

    expect(readObjectProperty(value, 'totalCount', { caseInsensitive: false })).toBeUndefined()
  })

  it('returns undefined for nullish or scalar values', () => {
    expect(readObjectProperty(null, 'name')).toBeUndefined()
    expect(readObjectProperty('value', 'name')).toBeUndefined()
  })
})

describe('readObjectPath', () => {
  it('reads nested dot-separated paths', () => {
    const value = {
      Result: {
        Items: [{ Id: 'run-1' }],
      },
    }

    expect(readObjectPath(value, 'result.items')).toEqual([
      { Id: 'run-1' },
    ])
  })

  it('returns the original value for an empty path', () => {
    const value = { id: 'resource-1' }

    expect(readObjectPath(value, '')).toBe(value)
  })

  it('returns undefined for nullish paths', () => {
    expect(readObjectPath({ id: 'resource-1' }, null)).toBeUndefined()
  })

  it('returns undefined when a segment cannot be resolved', () => {
    const value = {
      Result: {},
    }

    expect(readObjectPath(value, 'result.items.count')).toBeUndefined()
  })
})

describe('writeObjectPath', () => {
  it('writes nested dot-separated paths', () => {
    const target: Record<string, unknown> = {}

    writeObjectPath(target, 'request.filter.status', 'open')

    expect(target).toEqual({
      request: {
        filter: {
          status: 'open',
        },
      },
    })
  })

  it('overwrites non-object and array parents', () => {
    const target: Record<string, unknown> = {
      request: [],
    }

    writeObjectPath(target, 'request.filter.status', 'open')

    expect(target).toEqual({
      request: {
        filter: {
          status: 'open',
        },
      },
    })
  })

  it('ignores empty paths', () => {
    const target: Record<string, unknown> = {}

    writeObjectPath(target, '', 'ignored')

    expect(target).toEqual({})
  })
})
