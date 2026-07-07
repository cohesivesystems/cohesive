import type {
  DataSourceDefinition,
} from './module'
import {
  readObjectPath,
} from './object-path'
import {
  dataSourcePaginationKindLabels,
  dataSourcePaginationKinds,
  type DataSourcePaginationDefinition,
  type DataSourcePaginationKind,
} from '@cohesive/presentation-contracts'

export const presentationPaginationKinds = {
  cursor: 'cursor',
  offset: 'offset',
  pageNumber: 'page-number',
} as const

export type PresentationPaginationKind =
  (typeof presentationPaginationKinds)[keyof typeof presentationPaginationKinds]

export type PresentationPaginationState =
  | PresentationCursorPaginationState
  | PresentationOffsetPaginationState
  | PresentationPageNumberPaginationState

export interface PresentationCursorPaginationState {
  readonly cursorHistory: readonly (string | null)[]
  readonly kind: typeof presentationPaginationKinds.cursor
  readonly pageIndex: number
  readonly pageSize: number
}

export interface PresentationOffsetPaginationState {
  readonly kind: typeof presentationPaginationKinds.offset
  readonly pageIndex: number
  readonly pageSize: number
}

export interface PresentationPageNumberPaginationState {
  readonly kind: typeof presentationPaginationKinds.pageNumber
  readonly pageNumber: number
  readonly pageSize: number
}

export interface PresentationPaginationUrlPolicy {
  readonly enabled: boolean
  readonly parameterPrefix: string
}

export interface PresentationPaginationBinding {
  readonly dataSourceId: string
  readonly defaultPageSize: number
  readonly kind: PresentationPaginationKind
  readonly request: PresentationPaginationRequestBinding
  readonly response: PresentationPaginationResponseBinding
  readonly url: PresentationPaginationUrlPolicy
}

export interface PresentationPaginationRequestBinding {
  readonly cursorField?: string | null
  readonly limitField?: string | null
  readonly offsetField?: string | null
  readonly pageField?: string | null
  readonly pageNumberField?: string | null
  readonly skipField?: string | null
}

export interface PresentationPaginationResponseBinding {
  readonly cursorField?: string | null
  readonly hasNextPageField?: string | null
  readonly limitField?: string | null
  readonly offsetField?: string | null
  readonly pageField?: string | null
  readonly pageNumberField?: string | null
  readonly totalCountDataSourceId?: string | null
  readonly totalCountField?: string | null
}

export interface PresentationPaginationRuntime {
  readonly binding: PresentationPaginationBinding
  readonly canGoPreviousPage: boolean
  readonly dataSourceId: string
  readonly goToFirstPage: () => void
  readonly goToNextPage: (response?: unknown) => void
  readonly goToPreviousPage: () => void
  readonly pageIndex: number
  readonly pageSize: number
  readonly state: PresentationPaginationState
}

export interface ResolvedPresentationPageInfo {
  readonly hasNextPage: boolean
  readonly itemCount: number
  readonly pageIndex: number
  readonly pageSize: number
  readonly totalCount: number | null
  readonly totalPageCount: number | null
}

export function createInitialPresentationPaginationState(
  binding: PresentationPaginationBinding,
): PresentationPaginationState {
  switch (binding.kind) {
    case presentationPaginationKinds.cursor:
      return {
        cursorHistory: [null],
        kind: presentationPaginationKinds.cursor,
        pageIndex: 0,
        pageSize: binding.defaultPageSize,
      }
    case presentationPaginationKinds.offset:
      return {
        kind: presentationPaginationKinds.offset,
        pageIndex: 0,
        pageSize: binding.defaultPageSize,
      }
    case presentationPaginationKinds.pageNumber:
      return {
        kind: presentationPaginationKinds.pageNumber,
        pageNumber: 1,
        pageSize: binding.defaultPageSize,
      }
  }
}

export function readPresentationPaginationStateFromSearch(
  search: string,
  binding: PresentationPaginationBinding,
): PresentationPaginationState {
  if (!binding.url.enabled) {
    return createInitialPresentationPaginationState(binding)
  }

  const params = new URLSearchParams(search)
  const pageIndex = readPageIndex(params, binding.url.parameterPrefix)
  const pageSize = readPositiveInteger(
    params.get(createPaginationParam(binding.url.parameterPrefix, 'page_size')),
  ) ?? binding.defaultPageSize

  switch (binding.kind) {
    case presentationPaginationKinds.cursor:
      return normalizePresentationPaginationState({
        cursorHistory: [
          null,
          ...params
            .getAll(createPaginationParam(binding.url.parameterPrefix, 'cursor'))
            .map((cursor) => cursor.trim())
            .filter((cursor) => cursor.length > 0),
        ],
        kind: presentationPaginationKinds.cursor,
        pageIndex,
        pageSize,
      })
    case presentationPaginationKinds.offset:
      return normalizePresentationPaginationState({
        kind: presentationPaginationKinds.offset,
        pageIndex,
        pageSize,
      })
    case presentationPaginationKinds.pageNumber:
      return normalizePresentationPaginationState({
        kind: presentationPaginationKinds.pageNumber,
        pageNumber: pageIndex + 1,
        pageSize,
      })
  }
}

export function createPresentationPaginationSearch(
  search: string,
  binding: PresentationPaginationBinding,
  state: PresentationPaginationState,
) {
  if (!binding.url.enabled) {
    return search
  }

  const normalizedState = normalizePresentationPaginationState(state)
  const params = new URLSearchParams(search)
  deletePresentationPaginationParams(params, binding.url.parameterPrefix)

  const pageIndex = getPresentationPaginationPageIndex(normalizedState)
  if (pageIndex > 0) {
    params.set(createPaginationParam(binding.url.parameterPrefix, 'page'), String(pageIndex + 1))
  }

  if (normalizedState.pageSize !== binding.defaultPageSize) {
    params.set(
      createPaginationParam(binding.url.parameterPrefix, 'page_size'),
      String(normalizedState.pageSize),
    )
  }

  if (normalizedState.kind === presentationPaginationKinds.cursor && pageIndex > 0) {
    normalizedState.cursorHistory.slice(1, pageIndex + 1).forEach((cursor) => {
      if (cursor) {
        params.append(createPaginationParam(binding.url.parameterPrefix, 'cursor'), cursor)
      }
    })
  }

  const value = params.toString()
  return value.length > 0 ? `?${value}` : ''
}

export function deletePresentationPaginationParams(
  params: URLSearchParams,
  parameterPrefix: string,
) {
  params.delete(createPaginationParam(parameterPrefix, 'cursor'))
  params.delete(createPaginationParam(parameterPrefix, 'page'))
  params.delete(createPaginationParam(parameterPrefix, 'page_size'))
}

export function applyPresentationPaginationToRequest<TRequest extends object>(
  request: TRequest,
  binding: PresentationPaginationBinding | null | undefined,
  state: PresentationPaginationState | null | undefined,
): TRequest {
  if (!binding || !state) {
    return request
  }

  const normalizedState = normalizePresentationPaginationState(state)
  const target = { ...request } as Record<string, unknown>
  writeRequestField(target, binding.request.limitField, normalizedState.pageSize)

  if (normalizedState.kind === presentationPaginationKinds.cursor) {
    writeRequestField(
      target,
      binding.request.cursorField,
      normalizedState.cursorHistory[normalizedState.pageIndex] ?? null,
    )
  } else if (normalizedState.kind === presentationPaginationKinds.offset) {
    const offset = normalizedState.pageIndex * normalizedState.pageSize
    writeRequestField(target, binding.request.offsetField, offset)
    writeRequestField(target, binding.request.skipField, offset)
  } else {
    writeRequestField(target, binding.request.pageField, normalizedState.pageNumber)
    writeRequestField(target, binding.request.pageNumberField, normalizedState.pageNumber)
  }

  return target as TRequest
}

export function createNextPresentationPaginationState(
  binding: PresentationPaginationBinding,
  state: PresentationPaginationState,
  response?: unknown,
): PresentationPaginationState {
  const normalizedState = normalizePresentationPaginationState(state)
  switch (normalizedState.kind) {
    case presentationPaginationKinds.cursor: {
      const nextCursor = readStringPath(response, binding.response.cursorField)
      if (!nextCursor) {
        return normalizedState
      }

      return normalizePresentationPaginationState({
        ...normalizedState,
        cursorHistory: [
          ...normalizedState.cursorHistory.slice(0, normalizedState.pageIndex + 1),
          nextCursor,
        ],
        pageIndex: normalizedState.pageIndex + 1,
      })
    }
    case presentationPaginationKinds.offset:
      return normalizePresentationPaginationState({
        ...normalizedState,
        pageIndex: normalizedState.pageIndex + 1,
      })
    case presentationPaginationKinds.pageNumber:
      return normalizePresentationPaginationState({
        ...normalizedState,
        pageNumber: normalizedState.pageNumber + 1,
      })
  }
}

export function createPreviousPresentationPaginationState(
  state: PresentationPaginationState,
): PresentationPaginationState {
  const normalizedState = normalizePresentationPaginationState(state)
  if (normalizedState.kind === presentationPaginationKinds.pageNumber) {
    return normalizePresentationPaginationState({
      ...normalizedState,
      pageNumber: Math.max(1, normalizedState.pageNumber - 1),
    })
  }

  return normalizePresentationPaginationState({
    ...normalizedState,
    pageIndex: Math.max(0, normalizedState.pageIndex - 1),
  })
}

export function normalizePresentationPaginationState(
  state: PresentationPaginationState,
): PresentationPaginationState {
  const pageSize = Math.max(1, state.pageSize)
  switch (state.kind) {
    case presentationPaginationKinds.cursor: {
      const cursorHistory = [
        null,
        ...state.cursorHistory
          .slice(1)
          .map((cursor) => cursor?.trim() ?? '')
          .filter((cursor) => cursor.length > 0),
      ]
      return {
        ...state,
        cursorHistory,
        pageIndex: clamp(state.pageIndex, 0, cursorHistory.length - 1),
        pageSize,
      }
    }
    case presentationPaginationKinds.offset:
      return {
        ...state,
        pageIndex: Math.max(0, state.pageIndex),
        pageSize,
      }
    case presentationPaginationKinds.pageNumber:
      return {
        ...state,
        pageNumber: Math.max(1, state.pageNumber),
        pageSize,
      }
  }
}

export function resolvePresentationPageInfo({
  binding,
  itemCount,
  readDataSource,
  response,
  state,
  totalCount,
}: {
  readonly binding: PresentationPaginationBinding
  readonly itemCount: number
  readonly response?: unknown
  readonly readDataSource?: (dataSourceId: string) => unknown
  readonly state: PresentationPaginationState
  readonly totalCount?: number | null
}): ResolvedPresentationPageInfo {
  const normalizedState = normalizePresentationPaginationState(state)
  const pageIndex = getPresentationPaginationPageIndex(normalizedState)
  const pageSize = normalizedState.pageSize
  const projectedTotalCount = readPaginationTotalCount({
    binding,
    readDataSource,
    response,
  })
  const resolvedTotalCount =
    totalCount !== undefined ? totalCount : projectedTotalCount
  const totalPageCount =
    typeof resolvedTotalCount === 'number'
      ? Math.max(1, Math.ceil(resolvedTotalCount / pageSize))
      : null
  const hasNextPage = resolveHasNextPage({
    binding,
    itemCount,
    pageIndex,
    response,
    totalPageCount,
  })

  return {
    hasNextPage,
    itemCount,
    pageIndex,
    pageSize,
    totalCount: typeof resolvedTotalCount === 'number' ? resolvedTotalCount : null,
    totalPageCount,
  }
}

export function getPresentationPaginationPageIndex(state: PresentationPaginationState) {
  return state.kind === presentationPaginationKinds.pageNumber
    ? state.pageNumber - 1
    : state.pageIndex
}

export function inferPresentationPaginationBinding({
  dataSource,
  defaultPageSize,
  parameterPrefix,
  useUrl,
}: {
  readonly dataSource: DataSourceDefinition
  readonly defaultPageSize: number
  readonly parameterPrefix?: string
  readonly useUrl: boolean
}): PresentationPaginationBinding | null {
  if (dataSource.Query?.Pagination) {
    return projectDataSourcePaginationDefinition({
      dataSource,
      defaultPageSize,
      pagination: dataSource.Query.Pagination,
      parameterPrefix,
      useUrl,
    })
  }

  const requestPaths = new Set(
    dataSource.Query?.Fields.flatMap((field) => field.RequestPaths) ?? [],
  )
  const request = inferRequestBinding(requestPaths)
  const kind = inferPaginationKind(request)
  if (!kind) {
    return null
  }

  return {
    dataSourceId: dataSource.Id,
    defaultPageSize,
    kind,
    request,
    response: inferResponseBinding(kind),
    url: {
      enabled: useUrl,
      parameterPrefix: parameterPrefix ?? createDataSourcePaginationParameterPrefix(dataSource.Id),
    },
  }
}

function projectDataSourcePaginationDefinition({
  dataSource,
  defaultPageSize,
  pagination,
  parameterPrefix,
  useUrl,
}: {
  readonly dataSource: DataSourceDefinition
  readonly defaultPageSize: number
  readonly pagination: DataSourcePaginationDefinition
  readonly parameterPrefix?: string
  readonly useUrl: boolean
}): PresentationPaginationBinding | null {
  const kind = projectPaginationKind(pagination)
  if (!kind) {
    return null
  }

  return {
    dataSourceId: dataSource.Id,
    defaultPageSize: pagination.DefaultPageSize ?? defaultPageSize,
    kind,
    request: {
      cursorField: pagination.Request.CursorField,
      limitField: pagination.Request.LimitField,
      offsetField: pagination.Request.OffsetField,
      pageField: pagination.Request.PageField,
      pageNumberField: pagination.Request.PageNumberField,
      skipField: pagination.Request.SkipField,
    },
    response: {
      cursorField: pagination.Response.CursorField,
      hasNextPageField: pagination.Response.HasNextPageField,
      limitField: pagination.Response.LimitField,
      offsetField: pagination.Response.OffsetField,
      pageField: pagination.Response.PageField,
      pageNumberField: pagination.Response.PageNumberField,
      totalCountDataSourceId: pagination.Response.TotalCountDataSourceId,
      totalCountField: pagination.Response.TotalCountField,
    },
    url: {
      enabled: useUrl && (pagination.Url?.IsEnabled ?? true),
      parameterPrefix:
        parameterPrefix ??
        pagination.Url?.ParameterPrefix ??
        createDataSourcePaginationParameterPrefix(dataSource.Id),
    },
  }
}

function readPaginationTotalCount({
  binding,
  readDataSource,
  response,
}: {
  readonly binding: PresentationPaginationBinding
  readonly readDataSource?: (dataSourceId: string) => unknown
  readonly response?: unknown
}) {
  const field = binding.response.totalCountField
  if (!field) {
    return null
  }

  const totalCountSourceId = binding.response.totalCountDataSourceId
  if (totalCountSourceId) {
    return readNumberPath(readDataSource?.(totalCountSourceId), field)
  }

  return readNumberPath(response, field)
}

function projectPaginationKind(
  pagination: DataSourcePaginationDefinition,
): PresentationPaginationKind | null {
  if (matchesDataSourcePaginationKind(pagination.Kind, dataSourcePaginationKinds.cursor, 'cursor')) {
    return presentationPaginationKinds.cursor
  }

  if (matchesDataSourcePaginationKind(pagination.Kind, dataSourcePaginationKinds.offset, 'offset')) {
    return presentationPaginationKinds.offset
  }

  if (matchesDataSourcePaginationKind(pagination.Kind, dataSourcePaginationKinds.pageNumber, 'pageNumber')) {
    return presentationPaginationKinds.pageNumber
  }

  return null
}

function matchesDataSourcePaginationKind(
  value: unknown,
  numericValue: DataSourcePaginationKind,
  camelLabel: string,
) {
  const pascalLabel = dataSourcePaginationKindLabels[numericValue]
  return (
    value === numericValue ||
    String(value) === String(numericValue) ||
    String(value) === pascalLabel ||
    String(value) === camelLabel
  )
}

export function createDataSourcePaginationParameterPrefix(dataSourceId: string) {
  return dataSourceId
    .replace(/^[^a-zA-Z]+/, '')
    .replace(/[^a-zA-Z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .toLocaleLowerCase()
}

function inferRequestBinding(
  requestPaths: ReadonlySet<string>,
): PresentationPaginationRequestBinding {
  return {
    cursorField: findFieldPath(requestPaths, ['ContinuationToken', 'Cursor']),
    limitField: findFieldPath(requestPaths, ['Limit']),
    offsetField: findFieldPath(requestPaths, ['Offset']),
    pageField: findFieldPath(requestPaths, ['Page']),
    pageNumberField: findFieldPath(requestPaths, ['PageNumber']),
    skipField: findFieldPath(requestPaths, ['Skip']),
  }
}

function inferPaginationKind(
  request: PresentationPaginationRequestBinding,
): PresentationPaginationKind | null {
  if (request.cursorField) {
    return presentationPaginationKinds.cursor
  }

  if (request.offsetField || request.skipField) {
    return presentationPaginationKinds.offset
  }

  if (request.pageField || request.pageNumberField) {
    return presentationPaginationKinds.pageNumber
  }

  return null
}

function inferResponseBinding(
  kind: PresentationPaginationKind,
): PresentationPaginationResponseBinding {
  if (kind === presentationPaginationKinds.cursor) {
    return {
      cursorField: 'ContinuationToken',
      hasNextPageField: 'PageInfo.HasNextPage',
      limitField: 'Limit',
      totalCountField: 'PageInfo.TotalCount',
    }
  }

  if (kind === presentationPaginationKinds.offset) {
    return {
      hasNextPageField: 'PageInfo.HasNextPage',
      limitField: 'Limit',
      offsetField: 'Offset',
      totalCountField: 'PageInfo.TotalCount',
    }
  }

  return {
    hasNextPageField: 'PageInfo.HasNextPage',
    limitField: 'Limit',
    pageNumberField: 'PageNumber',
    totalCountField: 'PageInfo.TotalCount',
  }
}

function resolveHasNextPage({
  binding,
  itemCount,
  pageIndex,
  response,
  totalPageCount,
}: {
  readonly binding: PresentationPaginationBinding
  readonly itemCount: number
  readonly pageIndex: number
  readonly response?: unknown
  readonly totalPageCount: number | null
}) {
  const hasNextPage = readBooleanPath(response, binding.response.hasNextPageField)
  if (typeof hasNextPage === 'boolean') {
    return hasNextPage
  }

  if (binding.kind === presentationPaginationKinds.cursor) {
    return Boolean(readStringPath(response, binding.response.cursorField))
  }

  if (totalPageCount !== null) {
    return pageIndex + 1 < totalPageCount
  }

  return itemCount >= binding.defaultPageSize
}

function readPageIndex(params: URLSearchParams, parameterPrefix: string) {
  const requestedPage = readPositiveInteger(
    params.get(createPaginationParam(parameterPrefix, 'page')),
  )
  return requestedPage ? requestedPage - 1 : 0
}

function createPaginationParam(parameterPrefix: string, name: string) {
  return `${parameterPrefix}_${name}`
}

function findFieldPath(paths: ReadonlySet<string>, names: readonly string[]) {
  for (const name of names) {
    const path = Array.from(paths).find(
      (candidate) => candidate.toLocaleLowerCase() === name.toLocaleLowerCase(),
    )
    if (path) {
      return path
    }
  }

  return null
}

function writeRequestField(
  target: Record<string, unknown>,
  field: string | null | undefined,
  value: unknown,
) {
  if (!field || value === null || value === undefined || value === '') {
    return
  }

  target[field] = value
}

function readBooleanPath(value: unknown, path: string | null | undefined) {
  const raw = readObjectPath(value, path)
  return typeof raw === 'boolean' ? raw : null
}

function readNumberPath(value: unknown, path: string | null | undefined) {
  const raw = readObjectPath(value, path)
  return typeof raw === 'number' && Number.isFinite(raw) ? raw : null
}

function readStringPath(value: unknown, path: string | null | undefined) {
  const raw = readObjectPath(value, path)
  return typeof raw === 'string' && raw.trim() ? raw.trim() : null
}

function readPositiveInteger(value: string | null) {
  if (!value) {
    return null
  }

  const parsed = Number.parseInt(value, 10)
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null
}

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max)
}
