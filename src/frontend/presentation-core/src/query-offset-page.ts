export interface QueryOffsetPageResult<TItem> {
  readonly Items: TItem[]
  readonly Limit: number
  readonly Offset: number
}

export type QueryOffsetPageInput =
  | URLSearchParams
  | {
      readonly Limit?: unknown
      readonly Offset?: unknown
      readonly limit?: unknown
      readonly offset?: unknown
    }

export function queryOffsetPage<TItem>(
  items: readonly TItem[],
  query: QueryOffsetPageInput,
): QueryOffsetPageResult<TItem> {
  const limit = readQueryInteger(query, 'limit', 'Limit', 10)
  const offset = readQueryInteger(query, 'offset', 'Offset', 0)
  return {
    Items: items.slice(offset, offset + limit),
    Limit: limit,
    Offset: offset,
  }
}

function readQueryInteger(
  query: QueryOffsetPageInput,
  lowerName: 'limit' | 'offset',
  upperName: 'Limit' | 'Offset',
  fallback: number,
) {
  if (query instanceof URLSearchParams) {
    return readInteger(query.get(lowerName) ?? query.get(upperName), fallback)
  }

  return readInteger(query[lowerName] ?? query[upperName], fallback)
}

function readInteger(value: unknown, fallback: number) {
  if (value === null || value === undefined || value === '') {
    return fallback
  }

  const text = Array.isArray(value) ? value[0] : value
  const parsed = Number.parseInt(String(text), 10)
  return Number.isFinite(parsed) ? parsed : fallback
}
