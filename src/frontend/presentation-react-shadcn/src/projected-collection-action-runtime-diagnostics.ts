import {
  projectProjectedCollectionActionRuntimeDiagnostics as projectCoreProjectedCollectionActionRuntimeDiagnostics,
  type ProjectProjectedCollectionActionRuntimeDiagnosticsOptions,
} from '@cohesive/presentation-core'

import {
  projectPresentationActionIconDiagnostics,
} from './presentation-icon-diagnostics'

export type {
  ProjectProjectedCollectionActionIconDiagnosticsOptions,
  ProjectProjectedCollectionActionRuntimeDiagnosticsOptions,
} from '@cohesive/presentation-core'

export function projectProjectedCollectionActionRuntimeDiagnostics<TData extends object>(
  options: ProjectProjectedCollectionActionRuntimeDiagnosticsOptions<TData>,
) {
  return projectCoreProjectedCollectionActionRuntimeDiagnostics({
    ...options,
    projectActionIconDiagnostics: projectPresentationActionIconDiagnostics,
  })
}
