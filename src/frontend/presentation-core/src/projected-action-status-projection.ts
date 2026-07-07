import type {
  ActionDefinition,
} from './module'

export interface ProjectedDocumentActionStatus {
  readonly error?: unknown
  readonly isPending?: boolean
}

export type ProjectedDocumentActionStatusMap = Readonly<
  Record<string, ProjectedDocumentActionStatus | undefined>
>

export interface ProjectedActionStatusSource {
  readonly action?: Pick<ActionDefinition, 'Id'> | null
  readonly actionId?: string | null
  readonly error?: unknown
  readonly isPending?: boolean
}

/**
 * Projects action executor state into the action-id keyed status map consumed
 * by document-profile status notices.
 */
export function projectDocumentActionStatusMap(
  sources: readonly ProjectedActionStatusSource[],
): ProjectedDocumentActionStatusMap {
  const statuses: Record<string, ProjectedDocumentActionStatusMap[string]> = {}

  for (const source of sources) {
    const actionId = source.action?.Id ?? source.actionId
    if (!actionId) {
      continue
    }

    statuses[actionId] = {
      error: source.error,
      isPending: source.isPending,
    }
  }

  return statuses
}
