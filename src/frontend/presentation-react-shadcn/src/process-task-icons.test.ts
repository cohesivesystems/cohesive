import { describe, expect, it } from 'vitest'

import {
  processIconIds,
  resolveProcessStatusIconId,
} from './process-task-icons'

describe('Process task status icon projection', () => {
  it('uses declared progressing evidence for the running icon', () => {
    expect(resolveProcessStatusIconId({ isProgressing: true })).toBe(
      processIconIds.statusRunning,
    )
  })

  it('uses declared presentation tone for terminal appearance', () => {
    expect(resolveProcessStatusIconId({ tone: 'success' })).toBe(
      processIconIds.statusSuccess,
    )
    expect(resolveProcessStatusIconId({ tone: 'danger' })).toBe(
      processIconIds.statusError,
    )
  })

  it('does not present undisclosed evidence as running', () => {
    expect(resolveProcessStatusIconId({})).toBe(processIconIds.statusInfo)
  })
})
