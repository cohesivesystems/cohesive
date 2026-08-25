export type ProcessTaskType = string
export type ProcessTaskMetadataSelector = Partial<{
  readonly [K in keyof ProcessTaskMetadata]: ProcessTaskMetadata[K]
}>

/**
 * Stable diagnostic identifiers emitted while projecting declared Process
 * lifecycle evidence into a presentation task.
 *
 * These codes describe the completeness and internal consistency of evidence;
 * they are not a parallel catalog of canonical Process statuses.
 */
export const processTaskLifecycleDiagnosticCodes = {
  activeAndTerminal: 'process.task.lifecycle.active-and-terminal',
  failureWhileNonTerminal: 'process.task.lifecycle.failure-while-non-terminal',
  incomplete: 'process.task.lifecycle.incomplete',
  notDisclosed: 'process.task.lifecycle.not-disclosed',
  optimisticStart: 'process.task.lifecycle.optimistic-start',
  progressingWhileInactive: 'process.task.lifecycle.progressing-while-inactive',
  terminalAndProgressing: 'process.task.lifecycle.terminal-and-progressing',
} as const

/**
 * Runtime- or backend-declared lifecycle evidence used by generic Process task
 * presentation. Null means that the authority did not establish the fact.
 */
export interface ProcessTaskLifecycleDeclaration {
  readonly diagnosticCodes?: readonly string[] | null
  readonly isActive?: boolean | null
  readonly isFailure?: boolean | null
  readonly isProgressing?: boolean | null
  readonly isTerminal?: boolean | null
  readonly tone?: string | null
}

/**
 * Normalized lifecycle evidence retained on a Process task.
 *
 * Presentation runtimes consume these declared facts without interpreting
 * status strings, timestamps, labels, failure text, or target-specific enums.
 */
export interface ProcessTaskLifecycle {
  readonly diagnosticCodes: readonly string[]
  readonly isActive: boolean | null
  readonly isFailure: boolean | null
  readonly isProgressing: boolean | null
  readonly isTerminal: boolean | null
  readonly tone: string | null
}

export interface ProcessTaskMetadata {
  readonly correlationId?: string | null
  readonly ediSpecId?: string | null
  readonly mode?: string | null
  readonly modelId?: string | null
  readonly policyId?: string | null
  readonly projectionIds?: readonly string[] | null
  readonly shapeGraphId?: string | null
}

export interface ProcessTask {
  readonly id: string
  readonly type: ProcessTaskType
  readonly typeTone?: string | null
  readonly title: string
  readonly status: string
  readonly statusLabel: string
  readonly lifecycle: ProcessTaskLifecycle
  readonly startedAtUtc?: string | null
  readonly updatedAtUtc?: string | null
  readonly completedAtUtc?: string | null
  readonly failureMessage?: string | null
  readonly detailsHref?: string | null
  readonly sourceHref?: string | null
  readonly targetHref?: string | null
  readonly metadata: ProcessTaskMetadata
}

export interface ProcessTaskSelector {
  readonly activeOnly?: boolean
  readonly metadata?: ProcessTaskMetadataSelector
  readonly processType?: ProcessTaskType | null
}

export interface ProcessTaskStartToast {
  readonly description?: string
  readonly href?: string | null
  readonly hrefLabel?: string | null
  readonly title?: string
  readonly tone?: ProcessTaskToast['tone']
}

export interface ProcessTaskStartRegistration {
  readonly completedAtUtc?: string | null
  readonly detailsHref?: string | null
  readonly failureMessage?: string | null
  readonly invalidateQueryKeys?: readonly (readonly unknown[])[]
  readonly lifecycle: ProcessTaskLifecycle
  readonly metadata?: ProcessTaskMetadata
  readonly processId: string
  readonly processName?: string | null
  readonly processType: ProcessTaskType
  readonly processTypeLabel?: string | null
  readonly processTypeTone?: string | null
  readonly sourceHref?: string | null
  readonly startedAtUtc?: string | null
  readonly startedToast?: ProcessTaskStartToast | null
  readonly status?: string | null
  readonly statusLabel?: string | null
  readonly statusTone?: string | null
  readonly targetHref?: string | null
  readonly terminalInvalidateQueryKeys?: readonly (readonly unknown[])[]
  readonly updatedAtUtc?: string | null
}

export interface ProcessTaskToast {
  readonly id: string
  readonly taskId: string
  readonly title: string
  readonly description: string
  readonly tone: 'info' | 'success' | 'error'
  readonly href?: string | null
  readonly hrefLabel?: string | null
  readonly createdAt: number
}

export function isProcessTaskTerminal(task: ProcessTask) {
  return task.lifecycle.isTerminal === true &&
    task.lifecycle.isActive === false &&
    task.lifecycle.isProgressing === false
}

/**
 * Returns whether a task may still be active according to declared lifecycle
 * evidence. Unknown evidence remains potentially active so an active-only
 * admission check cannot accidentally admit a duplicate Process start.
 */
export function isProcessTaskPotentiallyActive(task: ProcessTask) {
  return !isProcessTaskTerminal(task)
}

export function findProcessTask(
  tasks: readonly ProcessTask[],
  selector: ProcessTaskSelector,
) {
  return findProcessTasks(tasks, selector)[0] ?? null
}

export function findProcessTasks(
  tasks: readonly ProcessTask[],
  selector: ProcessTaskSelector,
) {
  return tasks.filter((task) => matchesProcessTaskSelector(task, selector))
}

export function matchesProcessTaskSelector(
  task: ProcessTask,
  selector: ProcessTaskSelector,
) {
  if (selector.activeOnly && !isProcessTaskPotentiallyActive(task)) {
    return false
  }

  if (selector.processType && task.type !== selector.processType) {
    return false
  }

  return matchesProcessTaskMetadataSelector(task.metadata, selector.metadata)
}

/**
 * Normalizes target-declared lifecycle evidence and records incomplete or
 * contradictory facts without changing their meaning.
 */
export function createProcessTaskLifecycle(
  declaration?: ProcessTaskLifecycleDeclaration | null,
): ProcessTaskLifecycle {
  if (!declaration) {
    return {
      diagnosticCodes: [processTaskLifecycleDiagnosticCodes.notDisclosed],
      isActive: null,
      isFailure: null,
      isProgressing: null,
      isTerminal: null,
      tone: null,
    }
  }

  const lifecycle: Omit<ProcessTaskLifecycle, 'diagnosticCodes'> = {
    isActive: declaration.isActive ?? null,
    isFailure: declaration.isFailure ?? null,
    isProgressing: declaration.isProgressing ?? null,
    isTerminal: declaration.isTerminal ?? null,
    tone: declaration.tone ?? null,
  }
  const diagnosticCodes = new Set(declaration.diagnosticCodes ?? [])

  if (
    lifecycle.isActive === null ||
    lifecycle.isFailure === null ||
    lifecycle.isProgressing === null ||
    lifecycle.isTerminal === null
  ) {
    diagnosticCodes.add(processTaskLifecycleDiagnosticCodes.incomplete)
  }

  if (lifecycle.isActive === true && lifecycle.isTerminal === true) {
    diagnosticCodes.add(processTaskLifecycleDiagnosticCodes.activeAndTerminal)
  }

  if (lifecycle.isFailure === true && lifecycle.isTerminal === false) {
    diagnosticCodes.add(processTaskLifecycleDiagnosticCodes.failureWhileNonTerminal)
  }

  if (lifecycle.isProgressing === true && lifecycle.isActive === false) {
    diagnosticCodes.add(processTaskLifecycleDiagnosticCodes.progressingWhileInactive)
  }

  if (lifecycle.isProgressing === true && lifecycle.isTerminal === true) {
    diagnosticCodes.add(processTaskLifecycleDiagnosticCodes.terminalAndProgressing)
  }

  return {
    ...lifecycle,
    diagnosticCodes: Array.from(diagnosticCodes),
  }
}

function matchesProcessTaskMetadataSelector(
  metadata: ProcessTaskMetadata,
  selector: ProcessTaskMetadataSelector | null | undefined,
) {
  if (!selector) {
    return true
  }

  for (const [key, expected] of Object.entries(selector)) {
    if (expected === undefined) {
      continue
    }

    const actual = metadata[key as keyof ProcessTaskMetadata]
    if (Array.isArray(expected)) {
      if (!Array.isArray(actual) || !arraysEqual(actual, expected)) {
        return false
      }
      continue
    }

    if (actual !== expected) {
      return false
    }
  }

  return true
}

function arraysEqual(left: readonly unknown[], right: readonly unknown[]) {
  return left.length === right.length && left.every((value, index) => value === right[index])
}
