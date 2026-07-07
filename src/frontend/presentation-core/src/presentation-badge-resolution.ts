import {
  findPresentationField,
} from './module'
import type {
  FieldPresentationDefinition,
  PresentationBadgeDefinition,
  PresentationModuleDefinition,
} from './module'
import {
  resolvePresentationContent,
} from './presentation-content-resolution'
import {
  formatPresentationValue,
  isEmptyPresentationValue,
  readPresentationFieldValue,
  resolvePresentationTemplate,
  resolvePresentationValue,
} from './presentation-value-resolution'

export interface ResolvedPresentationBadge {
  readonly id?: string
  readonly label: string
  readonly tone?: string | null
}

export function resolvePresentationBadges(
  badges: readonly PresentationBadgeDefinition[] | null | undefined,
  data: unknown,
  module: Pick<PresentationModuleDefinition, 'Fields'> | null,
): readonly ResolvedPresentationBadge[] {
  return badges?.flatMap((badge) =>
    resolvePresentationBadge(badge, data, module),
  ) ?? []
}

function resolvePresentationBadge(
  badge: PresentationBadgeDefinition,
  data: unknown,
  module: Pick<PresentationModuleDefinition, 'Fields'> | null,
): readonly ResolvedPresentationBadge[] {
  const field = badge.FieldId
    ? findPresentationField<FieldPresentationDefinition>(module, badge.FieldId)
    : null
  if (badge.FieldId && !field && !badge.Value && !badge.ValueTemplate && !badge.Content) {
    return []
  }

  const value = badge.Value
    ? resolvePresentationValue(badge.Value, data)
    : readPresentationFieldValue(data, field?.Field)
  const hasValue = Boolean(badge.Value || field?.Field)
  if (
    hasValue &&
    (
      (badge.OmitWhenEmpty && isEmptyPresentationValue(value)) ||
      (badge.OmitWhenZero && isZeroPresentationValue(value))
    )
  ) {
    return []
  }

  const formattedValue =
    formatPresentationBadgeFieldValue(value, field) ??
    formatPresentationValue(value)
  const label =
    resolvePresentationBadgeContentLabel(badge, data, field) ??
    resolvePresentationTemplate(badge.ValueTemplate, data) ??
    formatFieldBackedBadgeLabel(formattedValue, badge, field) ??
    badge.Name
  if (badge.OmitWhenEmpty && isEmptyPresentationValue(label)) {
    return []
  }

  return [{
    id: badge.Id,
    label,
    tone: badge.Tone,
  }]
}

function formatFieldBackedBadgeLabel(
  formattedValue: string | null,
  badge: PresentationBadgeDefinition,
  field: FieldPresentationDefinition | null,
) {
  if (!formattedValue) {
    return null
  }

  if (badge.Value || badge.ValueTemplate || !badge.FieldId) {
    return formattedValue
  }

  const label = field?.Label ?? badge.Name
  return label ? `${label}: ${formattedValue}` : formattedValue
}

function resolvePresentationBadgeContentLabel(
  badge: PresentationBadgeDefinition,
  data: unknown,
  field: FieldPresentationDefinition | null,
) {
  const content = badge.Content
  if (!content) {
    return null
  }

  const descriptionValue = resolvePresentationValue(content.Description, data)
  if (descriptionValue !== undefined && descriptionValue !== null) {
    return (
      formatPresentationBadgeFieldValue(descriptionValue, field) ??
      formatPresentationValue(descriptionValue)
    )
  }

  const resolvedContent = resolvePresentationContent(content, data)
  return resolvedContent.description ?? resolvedContent.title ?? resolvedContent.subtitle
}

function isZeroPresentationValue(value: unknown) {
  return value === 0 || value === '0'
}

function formatPresentationBadgeFieldValue(
  value: unknown,
  field: FieldPresentationDefinition | null,
) {
  const labels = field?.Display?.ValueLabels
  if (!labels || value === null || value === undefined) {
    return null
  }

  const key = String(value)
  return labels.find((label) =>
    label.Value === key ||
    label.Value.toLocaleLowerCase() === key.toLocaleLowerCase(),
  )?.Label ?? null
}
