import type { PresentationDesignSystem } from '@cohesivesystems/presentation-tailwind'
import type {
  DocumentProfileProjection,
  ProcessTask,
  ProcessTaskSelector,
  ProcessTaskSelectorDefinition,
  ProjectedDocumentActionStatusMap,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  ProjectedDocumentActionStatusNotices,
} from './projected-document-action-status-notices'
import {
  ProjectedProcessTaskNotices,
  type ProjectedProcessTaskNoticeRenderer,
} from './projected-process-task-notices'

export interface ProjectedDocumentWorkspaceNoticesProps<TContext> {
  readonly actionStatuses: ProjectedDocumentActionStatusMap
  readonly componentSystem: PresentationComponentSystem
  readonly context: TContext
  readonly designSystem: PresentationDesignSystem
  readonly findActiveTask?: (selector: ProcessTaskSelector) => ProcessTask | null
  readonly profile: Pick<
    DocumentProfileProjection,
    'ActionStatusNotices' | 'ProcessTaskNotices' | 'ProcessTaskSelectors'
  > | null
  readonly projectSelector: (
    selector: ProcessTaskSelectorDefinition | null | undefined,
    context: TContext,
  ) => ProcessTaskSelector | null
  readonly region?: string
  readonly renderProcessTaskNotice?: ProjectedProcessTaskNoticeRenderer
}

/**
 * Projects all document-profile notices for a workspace chrome region.
 *
 * Action status notices are rendered by the shared presentation layer. Process
 * task notices resolve from IR selectors here, then delegate their concrete
 * visual interpretation to the host adapter.
 */
export function ProjectedDocumentWorkspaceNotices<TContext>({
  actionStatuses,
  componentSystem,
  context,
  designSystem,
  findActiveTask,
  profile,
  projectSelector,
  region,
  renderProcessTaskNotice,
}: ProjectedDocumentWorkspaceNoticesProps<TContext>) {
  return (
    <>
      <ProjectedDocumentActionStatusNotices
        actionStatuses={actionStatuses}
        profile={profile}
        region={region}
      />
      {findActiveTask ? (
        <ProjectedProcessTaskNotices
          componentSystem={componentSystem}
          context={context}
          designSystem={designSystem}
          findActiveTask={findActiveTask}
          profile={profile}
          projectSelector={projectSelector}
          region={region}
          renderNotice={renderProcessTaskNotice}
        />
      ) : null}
    </>
  )
}
