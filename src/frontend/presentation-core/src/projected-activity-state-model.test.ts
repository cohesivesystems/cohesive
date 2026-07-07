import { describe, expect, it } from 'vitest'

import {
  getApiErrorMessage,
  getErrorMessage,
} from './projected-activity-state-model'

describe('projected activity error messages', () => {
  it('prefers an explicit display message over transport details', () => {
    const error = new Error('HTTP 400 for /api/resource') as Error & {
      displayMessage: string
      responseBody: string
    }
    error.displayMessage = 'Sign in again, then retry.'
    error.responseBody = 'The operation requires exactly one selected tenant.'

    expect(getErrorMessage(error)).toBe('Sign in again, then retry.')
  })

  it('reads generated API problem Message values', () => {
    const error = new Error('HTTP 400 for /api/resource') as Error & {
      responseBody: string
    }
    error.responseBody = JSON.stringify({
      Code: 'BadRequest',
      Message: 'The relation definition payload is required.',
    })

    expect(getApiErrorMessage(error)).toBe('The relation definition payload is required.')
  })
})
