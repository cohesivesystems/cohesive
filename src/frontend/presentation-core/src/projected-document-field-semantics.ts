import type {
  FieldPresentationDefinition,
} from './module'
import {
  fieldDisplayKinds,
} from '@cohesive/presentation-contracts'

export function isProjectedDocumentBadgeField(field: FieldPresentationDefinition) {
  return matchesFieldDisplayKind(field, fieldDisplayKinds.badge, 'Badge') ||
    matchesFieldDisplayKind(field, fieldDisplayKinds.status, 'Status')
}

export function isProjectedDocumentEntityReferenceField(field: FieldPresentationDefinition) {
  return matchesFieldDisplayKind(field, fieldDisplayKinds.entityReference, 'EntityReference') ||
    (field.Capabilities.includes('navigate') &&
      matchesFieldDisplayKind(field, fieldDisplayKinds.link, 'Link'))
}

export function matchesFieldDisplayKind(
  field: FieldPresentationDefinition,
  numericValue: string | number,
  label: string,
) {
  return field.DisplayKind === numericValue ||
    String(field.DisplayKind) === String(numericValue) ||
    String(field.DisplayKind) === label
}