export function formatBadgeValue(value: unknown) {
  if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
    return value.toString()
  }

  return null
}
