import { describe, expect, it } from 'vitest'

import {
  processTaskLifecycleDiagnosticCodes,
  projectProcessTaskStartRegistration,
} from './index'

describe('Process task start registration projection', () => {
  it('declares successful registration as provisional active evidence', () => {
    const registration = projectProcessTaskStartRegistration({
      result: { ProcessId: 'process-1' },
    })

    expect(registration?.lifecycle).toEqual({
      diagnosticCodes: [processTaskLifecycleDiagnosticCodes.optimisticStart],
      isActive: true,
      isFailure: false,
      isProgressing: true,
      isTerminal: false,
      tone: 'info',
    })
  })

  it('preserves explicitly supplied lifecycle evidence', () => {
    const registration = projectProcessTaskStartRegistration({
      lifecycle: {
        diagnosticCodes: ['runtime.lifecycle.authoritative'],
        isActive: false,
        isFailure: false,
        isProgressing: false,
        isTerminal: true,
        tone: 'success',
      },
      result: { ProcessId: 'process-1' },
    })

    expect(registration?.lifecycle).toEqual({
      diagnosticCodes: ['runtime.lifecycle.authoritative'],
      isActive: false,
      isFailure: false,
      isProgressing: false,
      isTerminal: true,
      tone: 'success',
    })
  })
})
