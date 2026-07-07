export type ObjectPath = string

export interface ObjectPathOptions {
  readonly caseInsensitive?: boolean
}

/**
 * Parses a dot-separated object path into normalized path segments.
 */
export function parseObjectPath(path: ObjectPath | null | undefined) {
  return (path ?? '').split('.').map((segment) => segment.trim()).filter(Boolean)
}

/**
 * Reads a dot-separated object path.
 *
 * Case-insensitive segment fallback is enabled by default because generated API
 * payload casing can differ from semantic field metadata.
 */
export function readObjectPath(
  value: unknown,
  path: ObjectPath | null | undefined,
  options: ObjectPathOptions = {},
) {
  if (path === null || path === undefined) {
    return undefined
  }

  if (path === '') {
    return value
  }

  return parseObjectPath(path).reduce<unknown>((current, segment) => {
    return readObjectProperty(current, segment, options)
  }, value)
}

export function readObjectProperty(
  value: unknown,
  propertyName: string,
  options: ObjectPathOptions = {},
) {
  if (value === null || value === undefined || typeof value !== 'object') {
    return undefined
  }

  const record = value as Record<string, unknown>
  if (Object.prototype.hasOwnProperty.call(record, propertyName)) {
    return record[propertyName]
  }

  if (options.caseInsensitive === false) {
    return undefined
  }

  const match = Object.keys(record).find(
    (candidate) => candidate.toLocaleLowerCase() === propertyName.toLocaleLowerCase(),
  )
  return match ? record[match] : undefined
}

/**
 * Writes a value to a dot-separated object path, creating object parents as
 * needed. Empty paths are ignored.
 */
export function writeObjectPath(
  target: Record<string, unknown>,
  path: ObjectPath | null | undefined,
  value: unknown,
) {
  const segments = parseObjectPath(path)
  if (segments.length === 0) {
    return
  }

  let current = target
  for (const segment of segments.slice(0, -1)) {
    const next = current[segment]
    if (!next || typeof next !== 'object' || Array.isArray(next)) {
      current[segment] = {}
    }

    current = current[segment] as Record<string, unknown>
  }

  current[segments[segments.length - 1]] = value
}

export function writeObjectPathIfDefined(
  target: Record<string, unknown>,
  path: ObjectPath | null | undefined,
  value: unknown,
) {
  if (value === undefined || !path) {
    return
  }

  writeObjectPath(target, path, value)
}
