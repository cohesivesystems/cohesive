export function resolvePresentationEnumLabel(
  value: unknown,
  labels: Readonly<Record<number | string, string>> | undefined,
  {
    fallback = 'Unknown',
  }: {
    readonly fallback?: string
  } = {},
) {
  if (value === null || value === undefined || value === '') {
    return fallback
  }

  const key = String(value)
  return labels?.[key] ?? String(value)
}

export function formatPresentationVersion(
  value: unknown,
  {
    fallback = 'n/a',
    prefix = 'v',
  }: {
    readonly fallback?: string
    readonly prefix?: string
  } = {},
) {
  return value === null || value === undefined || value === ''
    ? fallback
    : `${prefix}${value}`
}

export function formatPresentationDateTime(
  value: unknown,
  {
    fallback = 'n/a',
  }: {
    readonly fallback?: string
  } = {},
) {
  if (typeof value !== 'string') {
    return fallback
  }

  const parsed = new Date(value)
  if (Number.isNaN(parsed.getTime())) {
    return value
  }

  return parsed.toLocaleString(undefined, {
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
    month: 'short',
  })
}

export function formatPresentationOptionalValue(
  value: unknown,
  fallback = 'n/a',
) {
  return value === null || value === undefined || value === ''
    ? fallback
    : String(value)
}
