import type { DocumentProfileProjection } from './document-module'
import {
  findPresentationAction,
  findPresentationField,
} from './module'
import type {
  DocumentProcessTaskNoticeDefinition,
  FieldPresentationDefinition,
  PresentationModuleDefinition,
  ProcessTaskSelectorDefinition,
} from './module'
import type {
  PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'
import {
  projectPresentationContentDiagnostics,
} from './presentation-content-diagnostics'
import type {
  ProcessTaskSelector,
} from './process-task-model'

export interface ProjectProcessTaskNoticeDiagnosticsOptions<TContext> {
  readonly context: TContext
  readonly module: Pick<PresentationModuleDefinition, 'Actions' | 'Fields'> | null
  readonly profile: Pick<
    DocumentProfileProjection,
    'ProcessTaskNotices' | 'ProcessTaskSelectors'
  > | null
  readonly projectSelector: (
    selector: ProcessTaskSelectorDefinition | null | undefined,
    context: TContext,
  ) => ProcessTaskSelector | null
  readonly region?: string
}

export function projectProcessTaskNoticeDiagnostics<TContext>({
  context,
  module,
  profile,
  projectSelector,
  region,
}: ProjectProcessTaskNoticeDiagnosticsOptions<TContext>): readonly PresentationProjectionDiagnostic[] {
  return getRegionProcessTaskNotices(profile, region).flatMap((notice) => {
    const diagnostics: PresentationProjectionDiagnostic[] = [
      ...projectProcessTaskNoticeContentDiagnostics(notice),
      ...projectProcessTaskNoticeStatusFieldDiagnostics(module, notice),
      ...projectProcessTaskNoticeActionDiagnostics(module, notice),
    ]
    const selectorDefinition = findProcessTaskSelectorDefinition(
      profile,
      notice.ProcessTaskSelectorId,
    )
    if (!selectorDefinition) {
      diagnostics.push(
        createProcessTaskNoticeDiagnostic({
          message:
            `Process task notice '${notice.Name}' references selector ` +
            `'${notice.ProcessTaskSelectorId}', but the document profile does not define it.`,
          notice,
          reason: 'missing-selector',
          severity: 'error',
        }),
      )
      return diagnostics
    }

    const selector = projectSelector(selectorDefinition, context)
    if (!selector) {
      diagnostics.push(
        createProcessTaskNoticeDiagnostic({
          message:
            `Process task notice '${notice.Name}' uses selector ` +
            `'${selectorDefinition.Name}', but this frontend context cannot project it.`,
          notice,
          reason: 'unprojectable-selector',
          severity: 'warning',
        }),
      )
      return diagnostics
    }

    return diagnostics
  })
}

function projectProcessTaskNoticeContentDiagnostics(
  notice: DocumentProcessTaskNoticeDefinition,
): readonly PresentationProjectionDiagnostic[] {
  return projectPresentationContentDiagnostics({
    content: notice.Content,
    contentFallbackDescription: 'local process title and status text fallback semantics',
    descriptionFallbackDescription: 'local process status text fallback semantics',
    details: createProcessTaskNoticeDiagnosticDetails(notice),
    diagnosticIdPrefix: `process-task-notice.${notice.Id}.content`,
    requireDescription: true,
    requireTitle: true,
    source: 'document-workspace-notice-projection',
    subject: createProcessTaskNoticeDiagnosticSubject(notice),
    surfaceLabel: `Process task notice '${notice.Name}'`,
    titleFallbackDescription: 'the local process task title',
  })
}

function projectProcessTaskNoticeActionDiagnostics(
  module: Pick<PresentationModuleDefinition, 'Actions'> | null,
  notice: DocumentProcessTaskNoticeDefinition,
): readonly PresentationProjectionDiagnostic[] {
  if (notice.Actions.length === 0) {
    return [
      createProcessTaskNoticeDiagnostic({
        message:
          `Process task notice '${notice.Name}' does not declare actions; ` +
          'the frontend will use local process detail navigation fallback semantics.',
        notice,
        reason: 'missing-actions',
        severity: 'warning',
      }),
    ]
  }

  return notice.Actions.flatMap((action) => {
    const diagnostics: PresentationProjectionDiagnostic[] = []

    if (!findPresentationAction(module, action.Placement.ActionId)) {
      diagnostics.push(
        createProcessTaskNoticeDiagnostic({
          message:
            `Process task notice '${notice.Name}' references action ` +
            `'${action.Placement.ActionId}', but the presentation module does not define it.`,
          notice,
          reason: `missing-action.${action.Placement.ActionId}`,
          severity: 'warning',
        }),
      )
    }

    if (!action.Placement.Icon) {
      diagnostics.push(
        createProcessTaskNoticeDiagnostic({
          message:
            `Process task notice '${notice.Name}' action '${action.Placement.ActionId}' ` +
            'does not declare an icon.',
          notice,
          reason: `missing-action-icon.${action.Placement.ActionId}`,
          severity: 'warning',
        }),
      )
    }

    if (action.TargetPreference.length === 0) {
      diagnostics.push(
        createProcessTaskNoticeDiagnostic({
          message:
            `Process task notice '${notice.Name}' action '${action.Placement.ActionId}' ` +
            'does not declare process-task link target preferences.',
          notice,
          reason: `missing-action-targets.${action.Placement.ActionId}`,
          severity: 'warning',
        }),
      )
    }

    return diagnostics
  })
}

function projectProcessTaskNoticeStatusFieldDiagnostics(
  module: Pick<PresentationModuleDefinition, 'Fields'> | null,
  notice: DocumentProcessTaskNoticeDefinition,
): readonly PresentationProjectionDiagnostic[] {
  if (!notice.StatusFieldId) {
    return [
      createProcessTaskNoticeDiagnostic({
        message:
          `Process task notice '${notice.Name}' does not declare a status field; ` +
          'the frontend will use local process status icon and tone fallback semantics.',
        notice,
        reason: 'missing-status-field',
        severity: 'warning',
      }),
    ]
  }

  const field = findPresentationField<FieldPresentationDefinition>(module, notice.StatusFieldId)
  if (!field) {
    return [
      createProcessTaskNoticeDiagnostic({
        message:
          `Process task notice '${notice.Name}' references status field ` +
          `'${notice.StatusFieldId}', but the presentation module does not define it.`,
        notice,
        reason: 'missing-status-field-definition',
        severity: 'warning',
      }),
    ]
  }

  const diagnostics: PresentationProjectionDiagnostic[] = []

  if ((field.Display?.ValueIcons?.length ?? 0) === 0) {
    diagnostics.push(
      createProcessTaskNoticeDiagnostic({
        message:
          `Process task notice '${notice.Name}' uses status field '${notice.StatusFieldId}', ` +
          'but that field does not declare value icons; the frontend will use local fallback semantics.',
        notice,
        reason: 'status-field-without-value-icons',
        severity: 'warning',
      }),
    )
  }

  if ((field.Display?.ValueTones?.length ?? 0) === 0) {
    diagnostics.push(
      createProcessTaskNoticeDiagnostic({
        message:
          `Process task notice '${notice.Name}' uses status field '${notice.StatusFieldId}', ` +
          'but that field does not declare value tones; the frontend will use local fallback semantics.',
        notice,
        reason: 'status-field-without-value-tones',
        severity: 'warning',
      }),
    )
  }

  return diagnostics
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

function createProcessTaskNoticeDiagnostic({
  message,
  notice,
  reason,
  severity,
}: {
  readonly message: string
  readonly notice: DocumentProcessTaskNoticeDefinition
  readonly reason: string
  readonly severity: PresentationProjectionDiagnostic['severity']
}): PresentationProjectionDiagnostic {
  return {
    details: createProcessTaskNoticeDiagnosticDetails(notice),
    id: `process-task-notice.${notice.Id}.${reason}`,
    message,
    severity,
    source: 'document-workspace-notice-projection',
    subject: createProcessTaskNoticeDiagnosticSubject(notice),
  }
}

function createProcessTaskNoticeDiagnosticDetails(
  notice: DocumentProcessTaskNoticeDefinition,
) {
  return {
    noticeId: notice.Id,
    processTaskSelectorId: notice.ProcessTaskSelectorId,
    region: notice.Region,
    statusFieldId: notice.StatusFieldId,
  }
}

function createProcessTaskNoticeDiagnosticSubject(
  notice: DocumentProcessTaskNoticeDefinition,
) {
  return {
    id: notice.Id,
    kind: 'document-process-task-notice',
    name: notice.Name,
  }
}
