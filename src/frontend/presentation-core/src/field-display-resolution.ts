import type {
  FieldPresentationDefinition,
  FieldValueIconDefinition,
  FieldValueLabelDefinition,
  FieldValueToneDefinition,
} from './module'
import {
  readObjectPath,
} from './object-path'

export function resolvePresentationFieldValueLabel(
  field: FieldPresentationDefinition | null | undefined,
  value: unknown,
) {
  return readDisplayValueLabel(field?.Display?.ValueLabels, value)
}

export function resolvePresentationFieldValueTone(
  field: FieldPresentationDefinition | null | undefined,
  value: unknown,
  resource?: unknown,
) {
  return (
    readFirstDisplayFieldPathValue(field?.Display?.ToneFieldPaths, resource) ??
    readDisplayValueTone(field?.Display?.ValueTones, value) ??
    field?.Display?.Tone ??
    null
  )
}

export function resolvePresentationFieldValueIcon(
  field: FieldPresentationDefinition | null | undefined,
  value: unknown,
) {
  return readDisplayValueIcon(field?.Display?.ValueIcons, value)
}

function readDisplayValueLabel(
  labels: readonly FieldValueLabelDefinition[] | null | undefined,
  value: unknown,
) {
  if (!labels) {
    return null
  }

  const key = String(value)
  return labels.find((label) =>
    label.Value === key ||
    label.Value.toLocaleLowerCase() === key.toLocaleLowerCase(),
  )?.Label ?? null
}

function readDisplayValueTone(
  tones: readonly FieldValueToneDefinition[] | null | undefined,
  value: unknown,
) {
  if (!tones) {
    return null
  }

  const key = String(value)
  return tones.find((tone) =>
    tone.Value === key ||
    tone.Value.toLocaleLowerCase() === key.toLocaleLowerCase(),
  )?.Tone ?? null
}

function readDisplayValueIcon(
  icons: readonly FieldValueIconDefinition[] | null | undefined,
  value: unknown,
) {
  if (!icons) {
    return null
  }

  const key = String(value)
  return icons.find((icon) =>
    icon.Value === key ||
    icon.Value.toLocaleLowerCase() === key.toLocaleLowerCase(),
  )?.Icon ?? null
}

function readFirstDisplayFieldPathValue(
  fieldPaths: readonly string[] | null | undefined,
  resource: unknown,
) {
  if (!fieldPaths || fieldPaths.length === 0) {
    return null
  }

  for (const path of fieldPaths) {
    const value = readObjectPath(resource, path)
    if (value !== null && value !== undefined && value !== '') {
      return String(value)
    }
  }

  return null
}
