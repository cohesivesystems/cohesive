import type { ReactNode } from 'react'

import type {
  PresentationBadgeDefinition,
  PresentationModuleDefinition,
  ProjectedDocumentFieldDefinitionLike,
  ResolvedPresentationBadge,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type {
  PresentationDesignSystem,
} from '@cohesivesystems/presentation-tailwind'

export interface PresentationBadgeTargetInterpretationContext {
  readonly badge: PresentationBadgeDefinition
  readonly componentSystem: PresentationComponentSystem
  readonly designSystem: PresentationDesignSystem
  readonly field: ProjectedDocumentFieldDefinitionLike | null
  readonly fieldId: string
  readonly module: PresentationModuleDefinition | null
  readonly resolvedBadge: ResolvedPresentationBadge | null
  readonly resource: unknown
  readonly value: unknown
}

export type PresentationBadgeTargetInterpreter = (
  context: PresentationBadgeTargetInterpretationContext,
) => ReactNode | undefined

export type PresentationBadgeTargetInterpreterRegistry =
  readonly PresentationBadgeTargetInterpreter[]

/**
 * Applies target-specific badge interpretations in registry order.
 *
 * Returning `undefined` means an interpreter does not handle the badge.
 * Returning `null` means the interpreter handled the badge and intentionally
 * suppresses rendering.
 */
export function interpretPresentationBadgeTarget(
  registry: PresentationBadgeTargetInterpreterRegistry | null | undefined,
  context: PresentationBadgeTargetInterpretationContext,
) {
  for (const interpreter of registry ?? []) {
    const rendered = interpreter(context)
    if (rendered !== undefined) {
      return rendered
    }
  }

  return undefined
}
