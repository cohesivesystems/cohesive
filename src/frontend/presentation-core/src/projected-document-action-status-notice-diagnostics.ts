import type { DocumentProfileProjection } from './document-module'
import {
  findPresentationAction,
} from './module'
import type {
  DocumentActionStatusNoticeDefinition,
  PresentationModuleDefinition,
} from './module'
import {
  projectPresentationContentDiagnostics,
} from './presentation-content-diagnostics'
import type {
  PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'

export interface ProjectDocumentActionStatusNoticeDiagnosticsOptions {
  readonly module: Pick<PresentationModuleDefinition, 'Actions'> | null
  readonly profile: Pick<DocumentProfileProjection, 'ActionStatusNotices'> | null
  readonly region?: string
}

export function projectDocumentActionStatusNoticeDiagnostics({
  module,
  profile,
  region,
}: ProjectDocumentActionStatusNoticeDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  return getRegionActionStatusNotices(profile, region).flatMap((notice) => [
    ...projectDocumentActionStatusNoticeContentDiagnostics(notice),
    ...projectDocumentActionStatusNoticeActionDiagnostics(module, notice),
  ])
}

function projectDocumentActionStatusNoticeContentDiagnostics(
  notice: DocumentActionStatusNoticeDefinition,
): readonly PresentationProjectionDiagnostic[] {
  return projectPresentationContentDiagnostics({
    content: notice.Content,
    contentFallbackDescription: 'local action status error message fallback semantics',
    descriptionFallbackDescription: 'local action status error message fallback semantics',
    details: createDocumentActionStatusNoticeDiagnosticDetails(notice),
    diagnosticIdPrefix: `action-status-notice.${notice.Id}.content`,
    requireDescription: true,
    source: 'document-workspace-notice-projection',
    subject: createDocumentActionStatusNoticeDiagnosticSubject(notice),
    surfaceLabel: `Action status notice '${notice.Name}'`,
  })
}

function projectDocumentActionStatusNoticeActionDiagnostics(
  module: Pick<PresentationModuleDefinition, 'Actions'> | null,
  notice: DocumentActionStatusNoticeDefinition,
): readonly PresentationProjectionDiagnostic[] {
  if (findPresentationAction(module, notice.ActionId)) {
    return []
  }

  return [
    createDocumentActionStatusNoticeDiagnostic({
      message:
        `Action status notice '${notice.Name}' references action ` +
        `'${notice.ActionId}', but the presentation module does not define it.`,
      notice,
      reason: 'missing-action',
      severity: 'warning',
    }),
  ]
}

function getRegionActionStatusNotices(
  profile: Pick<DocumentProfileProjection, 'ActionStatusNotices'> | null,
  region?: string,
) {
  return profile?.ActionStatusNotices?.filter((notice) => !region || notice.Region === region) ?? []
}

function createDocumentActionStatusNoticeDiagnostic({
  message,
  notice,
  reason,
  severity,
}: {
  readonly message: string
  readonly notice: DocumentActionStatusNoticeDefinition
  readonly reason: string
  readonly severity: PresentationProjectionDiagnostic['severity']
}): PresentationProjectionDiagnostic {
  return {
    details: createDocumentActionStatusNoticeDiagnosticDetails(notice),
    id: `action-status-notice.${notice.Id}.${reason}`,
    message,
    severity,
    source: 'document-workspace-notice-projection',
    subject: createDocumentActionStatusNoticeDiagnosticSubject(notice),
  }
}

function createDocumentActionStatusNoticeDiagnosticDetails(
  notice: DocumentActionStatusNoticeDefinition,
) {
  return {
    actionId: notice.ActionId,
    kind: notice.Kind,
    noticeId: notice.Id,
    region: notice.Region,
  }
}

function createDocumentActionStatusNoticeDiagnosticSubject(
  notice: DocumentActionStatusNoticeDefinition,
) {
  return {
    id: notice.Id,
    kind: 'document-action-status-notice',
    name: notice.Name,
  }
}
