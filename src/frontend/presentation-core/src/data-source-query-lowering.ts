import type {
  DataSourceDefinition,
  DataSourceQueryEndpointBindingDefinition,
  QueryFieldBindingDefinition,
  QueryLoweringDefinition,
} from '@cohesive/presentation-contracts'
import { queryLoweringKinds } from '@cohesive/presentation-contracts'
import {
  readObjectPath,
  writeObjectPath,
  writeObjectPathIfDefined,
} from './object-path'

export interface DataSourceQueryLoweringTransformContext {
  readonly binding: QueryFieldBindingDefinition
  readonly sourceValue: unknown
  readonly target: Record<string, unknown>
}

export type DataSourceQueryLoweringTransform = (value: unknown, context: DataSourceQueryLoweringTransformContext) => unknown

export interface LowerDataSourceQueryValueOptions<TRequest extends object> {
  readonly dataSource: Pick<DataSourceDefinition, 'Id' | 'Query'>
  readonly defaultRequest?: Partial<TRequest>
  readonly endpointId: string
  readonly transforms?: Readonly<Record<string, DataSourceQueryLoweringTransform>>
  readonly value: unknown
}

export interface CreateDataSourceEndpointQueryRequestOptions<TRequest extends object> {
  readonly dataSource: Pick<DataSourceDefinition, 'Id' | 'Query'>
  readonly defaultRequest?: Partial<TRequest>
  readonly endpointId: string
  readonly paginationRequest?: object | null
  readonly transforms?: Readonly<Record<string, DataSourceQueryLoweringTransform>>
  readonly value?: unknown
}

export interface CreateDataSourcePaginationRequestOptions {
  readonly cursor?: string | null
  readonly limit?: number | null
  readonly offset?: number | null
  readonly page?: number | null
  readonly pageNumber?: number | null
  readonly skip?: number | null
}

export function createDataSourceEndpointQueryRequest<TRequest extends object>({
  dataSource,
  defaultRequest,
  endpointId,
  paginationRequest,
  transforms,
  value,
}: CreateDataSourceEndpointQueryRequestOptions<TRequest>): TRequest {
  const loweredRequest = lowerDataSourceQueryValueToEndpointRequest<TRequest>({
    dataSource,
    defaultRequest,
    endpointId,
    transforms,
    value: value ?? {},
  })

  return removeUndefinedProperties({
    ...(defaultRequest ?? {}),
    ...(loweredRequest ?? {}),
    ...(paginationRequest ?? {}),
  }) as TRequest
}

export function createDataSourcePaginationRequest(
  dataSource: Pick<DataSourceDefinition, 'Query'>,
  {
    cursor,
    limit,
    offset,
    page,
    pageNumber,
    skip,
  }: CreateDataSourcePaginationRequestOptions,
): Record<string, unknown> {
  const request = dataSource.Query?.Pagination?.Request
  if (!request) {
    return {}
  }

  const target: Record<string, unknown> = {}
  writeObjectPathIfDefined(target, request.CursorField, cursor)
  writeObjectPathIfDefined(target, request.LimitField, limit)
  writeObjectPathIfDefined(target, request.OffsetField, offset)
  writeObjectPathIfDefined(target, request.PageField, page)
  writeObjectPathIfDefined(target, request.PageNumberField, pageNumber)
  writeObjectPathIfDefined(target, request.SkipField, skip)
  return target
}

export function lowerDataSourceQueryValueToEndpointRequest<TRequest extends object>({
  dataSource,
  defaultRequest,
  endpointId,
  transforms = {},
  value,
}: LowerDataSourceQueryValueOptions<TRequest>): TRequest | null {
  const endpointBinding = findDataSourceQueryEndpointBinding(dataSource, endpointId)
  const lowering = endpointBinding?.Lowerings.find(isPresentationValueToEndpointRequestLowering)

  if (!lowering) {
    return null
  }

  const request: Record<string, unknown> = {
    ...(defaultRequest ?? {}),
  }

  for (const fieldBinding of lowering.FieldBindings) {
    const sourceValue = readObjectPath(value, fieldBinding.SourcePath)
    const loweredValue = lowerFieldValue({
      binding: fieldBinding,
      sourceValue,
      target: request,
      transforms,
    })

    if (loweredValue !== undefined) {
      writeObjectPath(request, fieldBinding.TargetPath, loweredValue)
    }
  }

  return request as TRequest
}

export function findDataSourceQueryEndpointBinding(
  dataSource: Pick<DataSourceDefinition, 'Query'> | null | undefined,
  endpointId: string,
): DataSourceQueryEndpointBindingDefinition | null {
  return dataSource?.Query?.EndpointBindings.find(
    (binding) => binding.EndpointId === endpointId,
  ) ?? null
}

function lowerFieldValue({
  binding,
  sourceValue,
  target,
  transforms,
}: {
  readonly binding: QueryFieldBindingDefinition
  readonly sourceValue: unknown
  readonly target: Record<string, unknown>
  readonly transforms: Readonly<Record<string, DataSourceQueryLoweringTransform>>
}) {
  const initialValue =
    sourceValue === undefined || sourceValue === null
      ? parseDefaultValue(binding.DefaultValue)
      : sourceValue
  const transform = binding.Transform ? transforms[binding.Transform] : undefined

  if (!transform) {
    return initialValue
  }

  return transform(initialValue, {
    binding,
    sourceValue,
    target,
  })
}

function isPresentationValueToEndpointRequestLowering(
  lowering: QueryLoweringDefinition,
) {
  return (
    lowering.Kind === queryLoweringKinds.presentationValueToEndpointRequest ||
    String(lowering.Kind).toLocaleLowerCase() === 'presentationvaluetoendpointrequest'
  )
}

function parseDefaultValue(value: string | null | undefined) {
  if (value === null || value === undefined) {
    return undefined
  }

  const trimmed = value.trim()
  if (
    (trimmed.startsWith('[') && trimmed.endsWith(']')) ||
    (trimmed.startsWith('{') && trimmed.endsWith('}'))
  ) {
    try {
      return JSON.parse(trimmed)
    } catch {
      return value
    }
  }

  if (value === 'null') {
    return null
  }

  if (value === 'true') {
    return true
  }

  if (value === 'false') {
    return false
  }

  const numberValue = Number(value)
  return Number.isFinite(numberValue) && value.trim() !== '' ? numberValue : value
}

function removeUndefinedProperties(
  value: Record<string, unknown>,
): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries(value).filter(([, entryValue]) => entryValue !== undefined),
  )
}
