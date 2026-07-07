import type {
  PresentationContentDefinition,
} from './module'
import {
  formatPresentationValue,
  resolvePresentationTemplate,
  resolvePresentationValue,
} from './presentation-value-resolution'

export interface ProjectedPresentationContent {
  readonly description: string | null
  readonly subtitle: string | null
  readonly title: string | null
}

export function resolvePresentationContent(
  content: PresentationContentDefinition | null | undefined,
  data: unknown,
): ProjectedPresentationContent {
  return {
    description: resolvePresentationContentDescription(content, data),
    subtitle: resolvePresentationContentText(content?.Subtitle, data),
    title: resolvePresentationContentText(content?.Title, data),
  }
}

function resolvePresentationContentDescription(
  content: PresentationContentDefinition | null | undefined,
  data: unknown,
) {
  return (
    resolvePresentationContentText(content?.Description, data) ??
    resolveNonBlankPresentationText(resolvePresentationTemplate(content?.DescriptionTemplate, data))
  )
}

function resolvePresentationContentText(
  value: PresentationContentDefinition['Title'],
  data: unknown,
) {
  return resolveNonBlankPresentationText(formatPresentationValue(resolvePresentationValue(value, data)))
}

export function resolveNonBlankPresentationText(value: string | null | undefined) {
  return value && value.trim().length > 0 ? value : null
}
