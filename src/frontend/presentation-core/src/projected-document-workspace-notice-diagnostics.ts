import type { DocumentProfileProjection } from './document-module'
import type {
  PresentationModuleDefinition,
  ProcessTaskSelectorDefinition,
} from './module'
import {
  projectDocumentActionStatusNoticeDiagnostics,
} from './projected-document-action-status-notice-diagnostics'
import {
  projectProcessTaskNoticeDiagnostics,
} from './projected-process-task-notice-diagnostics'
import type {
  ProcessTaskSelector,
} from './process-task-model'

export interface ProjectDocumentWorkspaceNoticeDiagnosticsOptions<TContext> {
  readonly context: TContext
  readonly module: Pick<PresentationModuleDefinition, 'Actions' | 'Fields'> | null
  readonly profile: Pick<
    DocumentProfileProjection,
    'ActionStatusNotices' | 'ProcessTaskNotices' | 'ProcessTaskSelectors'
  > | null
  readonly projectSelector: (
    selector: ProcessTaskSelectorDefinition | null | undefined,
    context: TContext,
  ) => ProcessTaskSelector | null
  readonly region?: string
}

export function projectDocumentWorkspaceNoticeDiagnostics<TContext>(
  options: ProjectDocumentWorkspaceNoticeDiagnosticsOptions<TContext>,
) {
  return [
    ...projectDocumentActionStatusNoticeDiagnostics({
      module: options.module,
      profile: options.profile,
      region: options.region,
    }),
    ...projectProcessTaskNoticeDiagnostics(options),
  ]
}
