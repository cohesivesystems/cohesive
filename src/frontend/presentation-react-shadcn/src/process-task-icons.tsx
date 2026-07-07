import type { ReactNode } from 'react'

import type {
  DocumentProcessTaskNoticeDefinition,
  FieldPresentationDefinition,
  PresentationIconDiagnosticSubject,
  ProcessTask,
} from '@cohesivesystems/presentation-core'
import {
  resolvePresentationFieldValueIcon,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationIconModuleProjection,
} from './presentation-icon-registry'
import {
  renderPresentationIcon,
} from './presentation-icon-registry'

export const processIconIds = {
  metricCompleted: 'process.metric.completed',
  metricFailed: 'process.metric.failed',
  metricInProgress: 'process.metric.in-progress',
  metricLoaded: 'process.metric.loaded',
  noticeOpenDetails: 'process-task.notice.open-details',
  statusError: 'process.status.error',
  statusInfo: 'process.status.info',
  statusRunning: 'process.status.running',
  statusSuccess: 'process.status.success',
  statusWarning: 'process.status.warning',
  toastDismiss: 'process-task.toast.dismiss',
  toastOpen: 'process-task.toast.open',
} as const

export type ProcessStatusTone =
  | 'danger'
  | 'default'
  | 'info'
  | 'success'
  | 'warning'

export type ProcessToastTone =
  | 'error'
  | 'info'
  | 'success'

export function renderProcessIcon({
  className,
  fallbackIcon,
  icon,
  module,
}: {
  readonly className: string
  readonly fallbackIcon: string
  readonly icon: string
  readonly module?: PresentationIconModuleProjection | null
}) {
  return renderPresentationIcon({
    className,
    icon,
    module,
  }) ?? renderPresentationIcon({
    className,
    icon: fallbackIcon,
  })
}

export function renderProcessStatusIcon({
  className = 'size-4 shrink-0',
  module,
  status,
  tone,
}: {
  readonly className?: string
  readonly module?: PresentationIconModuleProjection | null
  readonly status?: ProcessTask['lifecycleStatus'] | string | null
  readonly tone?: ProcessStatusTone | string | null
}): ReactNode {
  const icon = resolveProcessStatusIconId({ status, tone })
  return renderProcessIcon({
    className: `${className} ${resolveProcessStatusIconToneClass(icon)}`,
    fallbackIcon: resolveProcessStatusFallbackIcon(icon),
    icon,
    module,
  })
}

export function renderProjectedProcessStatusIcon({
  className = 'size-4 shrink-0',
  field,
  module,
  value,
}: {
  readonly className?: string
  readonly field: FieldPresentationDefinition
  readonly module?: PresentationIconModuleProjection | null
  readonly value: unknown
}): ReactNode {
  const icon = resolvePresentationFieldValueIcon(field, value)
  if (!icon) {
    return null
  }

  return renderProcessIcon({
    className: `${className} ${resolveProcessStatusIconToneClass(icon)}`,
    fallbackIcon: resolveProcessStatusFallbackIcon(icon),
    icon,
    module,
  })
}

export function renderProcessToastToneIcon({
  className = 'mt-0.5 size-4 shrink-0',
  module,
  tone,
}: {
  readonly className?: string
  readonly module?: PresentationIconModuleProjection | null
  readonly tone: ProcessToastTone
}) {
  const icon = resolveProcessToastToneIconId(tone)
  return renderProcessIcon({
    className: `${className} ${resolveProcessStatusIconToneClass(icon)}`,
    fallbackIcon: resolveProcessStatusFallbackIcon(icon),
    icon,
    module,
  })
}

export function resolveProcessStatusIconId({
  status,
  tone,
}: {
  readonly status?: ProcessTask['lifecycleStatus'] | string | null
  readonly tone?: ProcessStatusTone | string | null
}) {
  if (tone === 'success' || status === 'success') {
    return processIconIds.statusSuccess
  }

  if (tone === 'danger' || tone === 'error' || status === 'error') {
    return processIconIds.statusError
  }

  if (
    tone === 'warning' ||
    status === 'paused' ||
    status === 'waiting'
  ) {
    return processIconIds.statusWarning
  }

  if (tone === 'info') {
    return processIconIds.statusInfo
  }

  return processIconIds.statusRunning
}

export function resolveProcessToastToneIconId(tone: ProcessToastTone) {
  if (tone === 'success') {
    return processIconIds.statusSuccess
  }

  if (tone === 'error') {
    return processIconIds.statusError
  }

  return processIconIds.statusInfo
}

export function resolveProcessTaskNoticeIconSubjects(
  task: ProcessTask,
  statusField?: FieldPresentationDefinition | null,
  notice?: DocumentProcessTaskNoticeDefinition | null,
): readonly PresentationIconDiagnosticSubject[] {
  const statusIcon = statusField
    ? resolvePresentationFieldValueIcon(statusField, task.status)
    : resolveProcessStatusIconId({ status: task.lifecycleStatus })
  const subjects: PresentationIconDiagnosticSubject[] = [
    ...(statusIcon ? [{
      details: {
        processTaskId: task.id,
        status: task.status,
        statusFieldId: statusField?.Id,
      },
      icon: statusIcon,
      id: `${task.id}:status`,
      kind: 'process-task-status-icon',
      label: task.statusLabel,
    }] : []),
  ]

  const actionSubjects = (notice?.Actions ?? []).flatMap((action) => {
    const icon = action.Placement.Icon
    if (!icon) {
      return []
    }

    return [{
      details: {
        actionId: action.Placement.ActionId,
        processTaskId: task.id,
      },
      icon,
      id: `${task.id}:${action.Placement.ActionId}`,
      kind: 'process-task-notice-icon',
      label: action.Placement.Label ?? action.Placement.ActionId,
    }]
  })

  subjects.push(...actionSubjects)

  if (actionSubjects.length === 0 && (task.detailsHref ?? task.targetHref ?? task.sourceHref)) {
    subjects.push({
      details: {
        processTaskId: task.id,
      },
      icon: processIconIds.noticeOpenDetails,
      id: `${task.id}:open-details`,
      kind: 'process-task-notice-icon',
      label: 'Open process details',
    })
  }

  return subjects
}

function resolveProcessStatusIconToneClass(icon: string) {
  if (icon === processIconIds.statusSuccess) {
    return 'text-teal-700'
  }

  if (icon === processIconIds.statusError) {
    return 'text-red-700'
  }

  if (icon === processIconIds.statusWarning) {
    return 'text-amber-700'
  }

  return 'text-sky-700'
}

function resolveProcessStatusFallbackIcon(icon: string) {
  if (icon === processIconIds.statusSuccess) {
    return 'check-circle-2'
  }

  if (icon === processIconIds.statusError) {
    return 'alert-circle'
  }

  if (icon === processIconIds.statusWarning || icon === processIconIds.statusInfo) {
    return 'clock-3'
  }

  return 'loader-circle'
}
