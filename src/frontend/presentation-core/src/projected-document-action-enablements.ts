import type {
  ActionDefinition,
  ActionEnablementCriterionDefinition,
} from './module'
import {
  createPresentationEnumDiscriminator,
  matchesPresentationEnum,
} from './target-bindings'
import {
  actionEnablementCriterionKinds,
} from '@cohesive/presentation-contracts'

export interface ProjectedDocumentLocalActionEnablementContext {
  readonly isDocumentDirty?: boolean
  readonly isDocumentValid?: boolean
  readonly pendingActionIds?: readonly string[]
}

export interface ProjectedDocumentActionEnablement {
  readonly blockingCriteria: readonly ActionEnablementCriterionDefinition[]
  readonly blockingCriterionId: string | null
  readonly isDisabled: boolean
  readonly message: string | null
}

export interface ProjectDocumentLocalActionEnablementOptions {
  readonly action: ActionDefinition | null | undefined
  readonly context?: ProjectedDocumentLocalActionEnablementContext | null
}

/**
 * Interprets local document action enablement criteria from presentation IR.
 *
 * The backend can declare criteria such as "document must be clean" while the
 * frontend adapter supplies the concrete local editor facts that only the
 * component host can observe.
 */
export function projectDocumentLocalActionEnablement({
  action,
  context,
}: ProjectDocumentLocalActionEnablementOptions): ProjectedDocumentActionEnablement {
  const blockingCriteria = (action?.Enablement ?? []).filter((criterion) =>
    isLocalDocumentCriterionBlocked(criterion, context),
  )
  const blockingCriterion = blockingCriteria[0] ?? null

  return {
    blockingCriteria,
    blockingCriterionId: blockingCriterion?.Id ?? null,
    isDisabled: blockingCriteria.length > 0,
    message: blockingCriterion?.Message ?? blockingCriterion?.Name ?? null,
  }
}

function isLocalDocumentCriterionBlocked(
  criterion: ActionEnablementCriterionDefinition,
  context: ProjectedDocumentLocalActionEnablementContext | null | undefined,
) {
  if (
    matchesPresentationEnum(criterion.Kind, localDocumentCleanCriterionKind)
  ) {
    return context?.isDocumentDirty === true
  }

  if (
    matchesPresentationEnum(criterion.Kind, localDocumentValidCriterionKind)
  ) {
    return context?.isDocumentValid === false
  }

  if (
    matchesPresentationEnum(criterion.Kind, noPendingActionCriterionKind)
  ) {
    const pendingActionIds = new Set(context?.pendingActionIds ?? [])
    return criterion.ReferencedActionId
      ? pendingActionIds.has(criterion.ReferencedActionId)
      : pendingActionIds.size > 0
  }

  return false
}

const localDocumentCleanCriterionKind = createPresentationEnumDiscriminator(
  actionEnablementCriterionKinds,
  'localDocumentClean',
  'LocalDocumentClean',
)

const localDocumentValidCriterionKind = createPresentationEnumDiscriminator(
  actionEnablementCriterionKinds,
  'localDocumentValid',
  'LocalDocumentValid',
)

const noPendingActionCriterionKind = createPresentationEnumDiscriminator(
  actionEnablementCriterionKinds,
  'noPendingAction',
  'NoPendingAction',
)
