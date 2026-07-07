import type {
  ActionDefinition,
} from './module'
import {
  formatPresentationValue,
  resolvePresentationValue,
} from './presentation-value-resolution'

export interface ResolveActionPendingLabelOptions<TLabel> {
  readonly action: Pick<ActionDefinition, 'RuntimePresentation'> | null | undefined
  readonly data?: unknown
  readonly fallback?: TLabel
}

/**
 * Resolves a pending action label from presentation IR runtime metadata.
 *
 * Label variants are checked before the default pending label, which lets a
 * process-preview action describe response-dependent labels such as overwrite
 * vs create without product-specific frontend branches.
 */
export function resolveActionPendingLabel<TLabel = string>({
  action,
  data,
  fallback,
}: ResolveActionPendingLabelOptions<TLabel>): TLabel | undefined {
  const runtimePresentation = action?.RuntimePresentation
  const variant = runtimePresentation?.PendingLabelVariants.find((candidate) =>
    actionRuntimeLabelVariantMatches({
      data,
      expectedValue: candidate.ExpectedValue,
      value: resolvePresentationValue(candidate.Condition, data),
    }),
  )
  const label = variant?.Label ?? runtimePresentation?.PendingLabel

  return label === null || label === undefined || label === ''
    ? fallback
    : label as TLabel
}

function actionRuntimeLabelVariantMatches({
  data,
  expectedValue,
  value,
}: {
  readonly data: unknown
  readonly expectedValue: string
  readonly value: unknown
}) {
  if (data === null || data === undefined) {
    return false
  }

  const formatted = formatPresentationValue(value)
  return formatted?.toLocaleLowerCase() === expectedValue.toLocaleLowerCase()
}
