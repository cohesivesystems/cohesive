export type ProcessTaskType = string
export type ProcessLifecycleStatus = 'pending' | 'running' | 'waiting' | 'success' | 'error' | 'paused'
export type ProcessTaskMetadataSelector = Partial<{
  readonly [K in keyof ProcessTaskMetadata]: ProcessTaskMetadata[K]
}>

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
  readonly lifecycleStatus: ProcessLifecycleStatus
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
  return isTerminalProcessStatus(task.status, task.completedAtUtc)
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
  if (selector.activeOnly && isProcessTaskTerminal(task)) {
    return false
  }

  if (selector.processType && task.type !== selector.processType) {
    return false
  }

  return matchesProcessTaskMetadataSelector(task.metadata, selector.metadata)
}

export function isTerminalProcessStatus(status: string, completedAtUtc?: string | null) {
  if (['Completed', 'Failed', 'Cancelled', 'Canceled', 'Terminated'].includes(status)) {
    return true
  }

  if (['Pending', 'Running', 'Waiting', 'Suspended'].includes(status)) {
    return false
  }

  return Boolean(completedAtUtc)
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
