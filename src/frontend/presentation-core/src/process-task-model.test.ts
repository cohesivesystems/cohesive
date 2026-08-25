import { describe, expect, it } from 'vitest'

import {
  createProcessTaskLifecycle,
  findProcessTasks,
  isProcessTaskTerminal,
  processTaskLifecycleDiagnosticCodes,
  type ProcessTask,
  type ProcessTaskLifecycleDeclaration,
} from './process-task-model'

describe('Process task lifecycle evidence', () => {
  it('uses declared terminal evidence without interpreting status or timestamps', () => {
    const task = createTask({
      completedAtUtc: '2026-08-25T12:00:00Z',
      lifecycle: {
        isActive: true,
        isFailure: false,
        isProgressing: false,
        isTerminal: false,
      },
      status: 'Completed',
    })

    expect(isProcessTaskTerminal(task)).toBe(false)
  })

  it('keeps undisclosed lifecycle evidence in active-only admission results', () => {
    const task = createTask({ lifecycle: null })

    expect(findProcessTasks([task], { activeOnly: true })).toEqual([task])
    expect(task.lifecycle.diagnosticCodes).toContain(
      processTaskLifecycleDiagnosticCodes.notDisclosed,
    )
  })

  it('requires coherent terminal evidence before releasing active-only admission', () => {
    const terminalTask = createTask({
      lifecycle: {
        isActive: false,
        isFailure: false,
        isProgressing: false,
        isTerminal: true,
      },
    })
    const contradictoryTask = createTask({
      lifecycle: {
        isActive: true,
        isFailure: false,
        isProgressing: false,
        isTerminal: true,
      },
    })

    expect(findProcessTasks([terminalTask], { activeOnly: true })).toEqual([])
    expect(findProcessTasks([contradictoryTask], { activeOnly: true })).toEqual([
      contradictoryTask,
    ])
  })

  it('diagnoses contradictory evidence without rewriting declared facts', () => {
    const lifecycle = createProcessTaskLifecycle({
      isActive: true,
      isFailure: true,
      isProgressing: true,
      isTerminal: true,
      tone: 'danger',
    })

    expect(lifecycle).toMatchObject({
      isActive: true,
      isFailure: true,
      isProgressing: true,
      isTerminal: true,
      tone: 'danger',
    })
    expect(lifecycle.diagnosticCodes).toEqual([
      processTaskLifecycleDiagnosticCodes.activeAndTerminal,
      processTaskLifecycleDiagnosticCodes.terminalAndProgressing,
    ])
  })

  it('diagnoses partially disclosed evidence', () => {
    const lifecycle = createProcessTaskLifecycle({ isTerminal: false })

    expect(lifecycle.isTerminal).toBe(false)
    expect(lifecycle.isActive).toBeNull()
    expect(lifecycle.diagnosticCodes).toEqual([
      processTaskLifecycleDiagnosticCodes.incomplete,
    ])
  })
})

function createTask({
  completedAtUtc = null,
  lifecycle,
  status = 'Unknown',
}: {
  readonly completedAtUtc?: string | null
  readonly lifecycle?: ProcessTaskLifecycleDeclaration | null
  readonly status?: string
}): ProcessTask {
  return {
    completedAtUtc,
    id: 'process-1',
    lifecycle: createProcessTaskLifecycle(lifecycle),
    metadata: {},
    status,
    statusLabel: status,
    title: 'Process',
    type: 'process',
  }
}
