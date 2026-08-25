import { Fragment, useMemo, type ReactNode } from 'react'

import {
  findPresentationAction,
  findPresentationField,
  resolvePresentationContent,
  resolvePresentationFieldValueTone,
} from '@cohesivesystems/presentation-core'
import {
  projectPresentationIconDiagnostics,
} from './presentation-icon-diagnostics'
import type {
  ActionPlacementDefinition,
  DocumentProcessTaskNoticeDefinition,
  DocumentProcessTaskNoticeActionDefinition,
  DocumentProcessTaskNoticeActionTargetKind,
  DocumentProfileProjection,
  FieldPresentationDefinition,
  ProcessTask,
  ProcessTaskSelector,
  ProcessTaskSelectorDefinition,
} from '@cohesivesystems/presentation-core'
import {
  usePresentationModule,
  usePresentationNavigationRuntime,
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesivesystems/presentation-react'
import type { PresentationDesignSystem } from '@cohesivesystems/presentation-tailwind'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  renderProcessIcon,
  renderProjectedProcessStatusIcon,
  renderProcessStatusIcon,
  resolveProcessTaskNoticeIconSubjects,
  processIconIds,
} from './process-task-icons'
import {
  documentProcessTaskNoticeActionTargetKinds,
} from '@cohesivesystems/presentation-contracts'

export interface ProjectedProcessTaskNoticeRenderContext {
  readonly notice: DocumentProcessTaskNoticeDefinition
  readonly selector: ProcessTaskSelector
  readonly selectorDefinition: ProcessTaskSelectorDefinition
  readonly task: ProcessTask
}

export type ProjectedProcessTaskNoticeRenderer = (
  context: ProjectedProcessTaskNoticeRenderContext,
) => ReactNode

export interface ProjectedProcessTaskNoticesProps<TContext> {
  readonly className?: string
  readonly componentSystem: PresentationComponentSystem
  readonly context: TContext
  readonly designSystem: PresentationDesignSystem
  readonly findActiveTask: (selector: ProcessTaskSelector) => ProcessTask | null
  readonly profile: Pick<
    DocumentProfileProjection,
    'ProcessTaskNotices' | 'ProcessTaskSelectors'
  > | null
  readonly projectSelector: (
    selector: ProcessTaskSelectorDefinition | null | undefined,
    context: TContext,
  ) => ProcessTaskSelector | null
  readonly region?: string
  readonly renderNotice?: ProjectedProcessTaskNoticeRenderer
}

/**
 * Projects document-profile process task notices into concrete task notice UI.
 *
 * The profile declares which notices exist and which selector supplies each
 * task. The host only provides the runtime document context, process-task
 * lookup service, and renderer for a resolved task notice.
 */
export function ProjectedProcessTaskNotices<TContext>({
  className,
  componentSystem,
  context,
  designSystem,
  findActiveTask,
  profile,
  projectSelector,
  region,
  renderNotice,
}: ProjectedProcessTaskNoticesProps<TContext>) {
  const notices = resolveProcessTaskNoticeRenderContexts({
    context,
    findActiveTask,
    profile,
    projectSelector,
    region,
  })
  if (notices.length === 0) {
    return null
  }

  const rootClassName = className ? `grid gap-2 ${className}` : 'grid gap-2'
  return (
    <div className={rootClassName}>
      {notices.map((notice) => (
        <Fragment key={notice.notice.Id}>
          {renderNotice?.(notice) ?? (
            <ProjectedProcessTaskNotice
              componentSystem={componentSystem}
              designSystem={designSystem}
              notice={notice.notice}
              task={notice.task}
            />
          )}
        </Fragment>
      ))}
    </div>
  )
}

export function ProjectedProcessTaskNotice({
  componentSystem,
  designSystem,
  notice,
  task,
}: {
  readonly componentSystem: PresentationComponentSystem
  readonly designSystem: PresentationDesignSystem
  readonly notice?: DocumentProcessTaskNoticeDefinition | null
  readonly task: ProcessTask
}) {
  const module = usePresentationModule()
  const statusField = useMemo(
    () => resolveProcessTaskNoticeStatusField(module, notice),
    [
      module,
      notice,
    ],
  )
  const iconDiagnostics = useMemo(
    () => projectPresentationIconDiagnostics({
      icons: resolveProcessTaskNoticeIconSubjects(task, statusField, notice),
      module,
      source: `projected-process-task-notice-icons:${task.id}`,
      surfaceId: task.id,
      surfaceName: task.title,
    }),
    [
      module,
      notice,
      statusField,
      task,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    `projected-process-task-notice-icons:${task.id}`,
    iconDiagnostics,
  )
  const content = resolveProcessTaskNoticeContent(notice, task)

  return componentSystem.processes.ProcessTaskNotice({
    actions: (
      <ProjectedProcessTaskActionButtons
        componentSystem={componentSystem}
        module={module}
        notice={notice}
        task={task}
      />
    ),
    className: resolveProcessTaskNoticeClassName({ designSystem, statusField, task }),
    description: content.description,
    icon: (
      <ProjectedProcessTaskStatusIcon
        module={module}
        statusField={statusField}
        task={task}
      />
    ),
    title: content.title,
  })
}

function resolveProcessTaskNoticeClassName({
  designSystem,
  statusField,
  task,
}: {
  readonly designSystem: PresentationDesignSystem
  readonly statusField?: FieldPresentationDefinition | null
  readonly task: ProcessTask
}) {
  const projectedTone = statusField
    ? resolvePresentationFieldValueTone(statusField, task.status)
    : task.lifecycle.tone
  const toneClassName = projectedTone
    ? designSystem.classNames.statusNotice.tone({ tone: projectedTone })
    : designSystem.classNames.statusNotice.tone({ tone: 'info' })

  return cn('rounded-lg border px-4 py-3 text-sm', toneClassName)
}

function resolveProcessTaskNoticeContent(
  notice: DocumentProcessTaskNoticeDefinition | null | undefined,
  task: ProcessTask,
) {
  const content = resolvePresentationContent(notice?.Content, task)
  const title = content.title ?? task.title
  const description = content.description ?? content.subtitle ??
    `${task.statusLabel}. Process ${task.id}.`

  return {
    description,
    title,
  }
}

function resolveProcessTaskNoticeRenderContexts<TContext>({
  context,
  findActiveTask,
  profile,
  projectSelector,
  region,
}: Omit<
  ProjectedProcessTaskNoticesProps<TContext>,
  'className' | 'componentSystem' | 'designSystem' | 'renderNotice'
>) {
  return getRegionProcessTaskNotices(profile, region).flatMap((notice) => {
    const selectorDefinition = findProcessTaskSelectorDefinition(
      profile,
      notice.ProcessTaskSelectorId,
    )
    const selector = projectSelector(selectorDefinition, context)
    const task = selector ? findActiveTask(selector) : null

    return task && selector && selectorDefinition
      ? [{
        notice,
        selector,
        selectorDefinition,
        task,
      }]
      : []
  })
}

function getRegionProcessTaskNotices(
  profile: Pick<DocumentProfileProjection, 'ProcessTaskNotices'> | null,
  region?: string,
) {
  return profile?.ProcessTaskNotices?.filter((notice) => !region || notice.Region === region) ?? []
}

function findProcessTaskSelectorDefinition(
  profile: Pick<DocumentProfileProjection, 'ProcessTaskSelectors'> | null,
  selectorId: string,
) {
  return profile?.ProcessTaskSelectors?.find((selector) => selector.Id === selectorId) ?? null
}

function ProjectedProcessTaskStatusIcon({
  module,
  statusField,
  task,
}: {
  readonly module: ReturnType<typeof usePresentationModule>
  readonly statusField?: FieldPresentationDefinition | null
  readonly task: ProcessTask
}) {
  const className = task.lifecycle.isProgressing === true
    ? 'mt-0.5 size-4 shrink-0 animate-spin'
    : 'mt-0.5 size-4 shrink-0'

  if (statusField) {
    return renderProjectedProcessStatusIcon({
      className,
      field: statusField,
      module,
      value: task.status,
    })
  }

  return renderProcessStatusIcon({
    className,
    isProgressing: task.lifecycle.isProgressing,
    module,
    tone: task.lifecycle.tone,
  })
}

function resolveProcessTaskNoticeStatusField(
  module: ReturnType<typeof usePresentationModule>,
  notice: DocumentProcessTaskNoticeDefinition | null | undefined,
) {
  return notice?.StatusFieldId
    ? findPresentationField<FieldPresentationDefinition>(module, notice.StatusFieldId)
    : null
}

function ProjectedProcessTaskActionButtons({
  componentSystem,
  module,
  notice,
  task,
}: {
  readonly componentSystem: PresentationComponentSystem
  readonly module: ReturnType<typeof usePresentationModule>
  readonly notice?: DocumentProcessTaskNoticeDefinition | null
  readonly task: ProcessTask
}) {
  const actions = notice?.Actions ?? []
  if (actions.length > 0) {
    return (
      <>
        {actions.map((action) => (
          <ProjectedProcessTaskActionButton
            action={action}
            componentSystem={componentSystem}
            key={action.Placement.ActionId}
            module={module}
            task={task}
          />
        ))}
      </>
    )
  }

  return (
    <LegacyProcessTaskActionButton
      componentSystem={componentSystem}
      module={module}
      task={task}
    />
  )
}

function ProjectedProcessTaskActionButton({
  action,
  componentSystem,
  module,
  task,
}: {
  readonly action: DocumentProcessTaskNoticeActionDefinition
  readonly componentSystem: PresentationComponentSystem
  readonly module: ReturnType<typeof usePresentationModule>
  readonly task: ProcessTask
}) {
  const { navigateHref } = usePresentationNavigationRuntime()
  const ActionButton = componentSystem.actions.ActionButton
  const href = resolveProcessTaskNoticeActionHref(action, task)
  if (!href) {
    return null
  }

  const placement = action.Placement
  const actionDefinition = findPresentationAction(module, placement.ActionId)
  const label = placement.Label ?? actionDefinition?.Name ?? placement.ActionId
  const icon = placement.Icon
    ? renderProcessIcon({
      className: 'size-4',
      fallbackIcon: 'chevron-right',
      icon: placement.Icon,
      module,
    })
    : null
  const iconOnly = isIconOnlyActionPlacement(placement)

  return (
    <ActionButton
      aria-label={iconOnly ? label : undefined}
      onClick={() => navigateHref(href)}
      size={iconOnly ? 'icon-sm' : 'sm'}
      type="button"
      variant={resolveProcessTaskNoticeActionVariant(placement)}
    >
      {icon}
      {iconOnly ? null : label}
    </ActionButton>
  )
}

function LegacyProcessTaskActionButton({
  componentSystem,
  module,
  task,
}: {
  readonly componentSystem: PresentationComponentSystem
  readonly module: ReturnType<typeof usePresentationModule>
  readonly task: ProcessTask
}) {
  const { navigateHref } = usePresentationNavigationRuntime()
  const ActionButton = componentSystem.actions.ActionButton
  const targetHref = task.detailsHref ?? task.targetHref ?? task.sourceHref
  if (!targetHref) {
    return null
  }

  return (
    <ActionButton
      aria-label="Open process details"
      onClick={() => navigateHref(targetHref)}
      size="icon-sm"
      type="button"
      variant="ghost"
    >
      {renderProcessIcon({
        className: 'size-4',
        fallbackIcon: 'chevron-right',
        icon: processIconIds.noticeOpenDetails,
        module,
      })}
    </ActionButton>
  )
}

function resolveProcessTaskNoticeActionHref(
  action: DocumentProcessTaskNoticeActionDefinition,
  task: ProcessTask,
) {
  const targetPreference =
    action.TargetPreference.length > 0
      ? action.TargetPreference
      : [
        documentProcessTaskNoticeActionTargetKinds.details,
        documentProcessTaskNoticeActionTargetKinds.target,
        documentProcessTaskNoticeActionTargetKinds.source,
      ]

  for (const target of targetPreference) {
    const href = readProcessTaskNoticeActionTargetHref(target, task)
    if (href) {
      return href
    }
  }

  return null
}

function readProcessTaskNoticeActionTargetHref(
  target: DocumentProcessTaskNoticeActionTargetKind,
  task: ProcessTask,
) {
  if (matchesNoticeActionTarget(target, documentProcessTaskNoticeActionTargetKinds.details, 'Details')) {
    return task.detailsHref ?? null
  }

  if (matchesNoticeActionTarget(target, documentProcessTaskNoticeActionTargetKinds.target, 'Target')) {
    return task.targetHref ?? null
  }

  if (matchesNoticeActionTarget(target, documentProcessTaskNoticeActionTargetKinds.source, 'Source')) {
    return task.sourceHref ?? null
  }

  return null
}

function matchesNoticeActionTarget(
  value: DocumentProcessTaskNoticeActionTargetKind,
  numericValue: DocumentProcessTaskNoticeActionTargetKind,
  label: string,
) {
  return value === numericValue ||
    String(value) === String(numericValue) ||
    value?.toString().toLocaleLowerCase() === label.toLocaleLowerCase()
}

function isIconOnlyActionPlacement(placement: ActionPlacementDefinition) {
  return placement.Intent?.toLocaleLowerCase() === 'icon'
}

function resolveProcessTaskNoticeActionVariant(
  placement: ActionPlacementDefinition,
): 'default' | 'ghost' | 'outline' {
  const intent = placement.Intent?.toLocaleLowerCase()
  if (intent === 'primary') {
    return 'default'
  }

  if (intent === 'ghost' || intent === 'icon') {
    return 'ghost'
  }

  return 'outline'
}

function cn(...values: readonly (false | null | string | undefined)[]) {
  return values.filter(Boolean).join(' ')
}
