import type { PresentationValueDefinition } from './module'

const presentationValueKind = {
  literal: new Set<string | number>(['Literal', 0]),
  field: new Set<string | number>(['Field', 1]),
} as const

export function resolvePresentationValue(
  value: PresentationValueDefinition | null | undefined,
  data: unknown,
) {
  if (!value) {
    return undefined
  }

  if (presentationValueKind.literal.has(value.Kind)) {
    return value.Literal
  }

  if (presentationValueKind.field.has(value.Kind)) {
    return readPresentationFieldValue(data, value.Field)
  }

  return undefined
}

export function resolvePresentationTemplate(
  template: string | null | undefined,
  data: unknown,
) {
  if (!template) {
    return null
  }

  return template.replace(/\{([^}]+)\}/g, (match, fieldPath: string) => {
    const value = readPresentationFieldValue(data, fieldPath)
    return value === null || value === undefined ? match : formatPresentationValue(value) ?? ''
  })
}

export function readPresentationFieldValue(
  data: unknown,
  fieldPath: string | null | undefined,
) {
  if (!fieldPath) {
    return undefined
  }

  const path = fieldPath.split('.').filter(Boolean)
  const exactValue = readPath(data, path)
  if (exactValue !== undefined || path.length <= 1) {
    return exactValue
  }

  return readPath(data, path.slice(1))
}

export function isEmptyPresentationValue(value: unknown) {
  return value === null || value === undefined || value === ''
}

export function formatPresentationValue(value: unknown) {
  if (value === null || value === undefined) {
    return null
  }

  if (typeof value === 'string') {
    return value
  }

  if (typeof value === 'number' || typeof value === 'bigint' || typeof value === 'boolean') {
    return value.toString()
  }

  return JSON.stringify(value)
}

function readPath(source: unknown, path: readonly string[]) {
  let current = source
  for (const segment of path) {
    if (!current || typeof current !== 'object') {
      return undefined
    }

    current = readObjectProperty(current as Record<string, unknown>, segment)
  }

  return current
}

function readObjectProperty(source: Record<string, unknown>, segment: string) {
  if (Object.prototype.hasOwnProperty.call(source, segment)) {
    return source[segment]
  }

  const match = Object.keys(source).find(
    (key) => key.toLocaleLowerCase() === segment.toLocaleLowerCase(),
  )
  return match ? source[match] : undefined
}
