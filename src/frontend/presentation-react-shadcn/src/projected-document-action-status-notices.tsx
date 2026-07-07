import {
  getErrorMessage,
  type DocumentProfileProjection,
  type ProjectedDocumentActionStatus,
  type ProjectedDocumentActionStatusMap,
  resolvePresentationContent,
} from '@cohesive/presentation-core'
import { documentActionStatusNoticeKinds } from '@cohesive/presentation-contracts'
import { ProjectedStatusBlock } from './projected-activity-state'

export type {
  ProjectedDocumentActionStatus,
  ProjectedDocumentActionStatusMap,
}

export interface ProjectedDocumentActionStatusNoticesProps {
  readonly actionStatuses: ProjectedDocumentActionStatusMap
  readonly profile: Pick<DocumentProfileProjection, 'ActionStatusNotices'> | null
  readonly region?: string
}

/**
 * Projects document-profile action status notices into status blocks.
 *
 * The profile declares which action statuses should surface in workspace
 * chrome. Runtime action executors only provide current status by action id.
 */
export function ProjectedDocumentActionStatusNotices({
  actionStatuses,
  profile,
  region,
}: ProjectedDocumentActionStatusNoticesProps) {
  const notices =
    profile?.ActionStatusNotices?.filter((notice) => !region || notice.Region === region) ?? []
  const renderedNotices = notices
    .map((notice) => {
      const status = actionStatuses[notice.ActionId]
      if (
        notice.Kind === documentActionStatusNoticeKinds.error &&
        status?.error
      ) {
        const errorMessage = getErrorMessage(status.error)
        const content = resolvePresentationContent(notice.Content, {
          ...status,
          errorMessage,
        })

        return (
          <ProjectedStatusBlock
            key={notice.Id}
            label={content.description ?? content.title ?? errorMessage}
            tone="error"
          />
        )
      }

      return null
    })
    .filter(Boolean)

  if (renderedNotices.length === 0) {
    return null
  }

  return <div className="grid gap-2">{renderedNotices}</div>
}
