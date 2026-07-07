import type {
  InputFormDefinition,
  QueryFormDefinition,
  QueryFormUrlPolicyDefinition,
} from './module'
import {
  readObjectPath,
  writeObjectPath,
} from './object-path'

export interface QueryFormUrlFieldBinding {
  readonly fieldId: string
  readonly valuePath: string
}

export interface QueryFormUrlFieldCodec<TValue extends object> {
  readonly deleteParams?: (context: QueryFormUrlFieldDeleteContext<TValue>) => void
  readonly read?: (context: QueryFormUrlFieldReadContext<TValue>) => unknown
  readonly write?: (context: QueryFormUrlFieldWriteContext<TValue>) => void
}

export type QueryFormUrlFieldCodecs<TValue extends object> = Readonly<
  Record<string, QueryFormUrlFieldCodec<TValue> | undefined>
>

export interface QueryFormUrlFieldDeleteContext<TValue extends object> {
  readonly defaultValue: TValue
  readonly field: QueryFormUrlFieldBinding
  readonly key: string
  readonly params: URLSearchParams
  readonly queryForm: QueryFormDefinition | null
}

export interface QueryFormUrlFieldReadContext<TValue extends object>
  extends QueryFormUrlFieldDeleteContext<TValue> {
  readonly defaultFieldValue: unknown
}

export interface QueryFormUrlFieldWriteContext<TValue extends object>
  extends QueryFormUrlFieldReadContext<TValue> {
  readonly allValues: readonly string[]
  readonly fieldValue: unknown
  readonly value: TValue
}

export interface QueryFormUrlValueOptions<TValue extends object> {
  readonly defaultValue: TValue
  readonly fieldCodecs?: QueryFormUrlFieldCodecs<TValue>
  readonly fields: readonly QueryFormUrlFieldBinding[]
  readonly policy?: QueryFormUrlPolicyDefinition | null
  readonly queryForm?: QueryFormDefinition | null
  readonly search: string
}

export interface CreateQueryFormUrlSearchOptions<TValue extends object>
  extends QueryFormUrlValueOptions<TValue> {
  readonly allValuesByFieldId?: Readonly<Record<string, readonly string[] | undefined>>
  readonly value: TValue
}

export function createQueryFormUrlFields(
  inputForm: InputFormDefinition | null,
  queryForm: QueryFormDefinition | null,
): readonly QueryFormUrlFieldBinding[] {
  if (!inputForm) {
    return []
  }

  const targetFieldIds = new Set([
    ...(queryForm?.Target.Predicates.map((predicate) => predicate.FieldId) ?? []),
    ...(queryForm?.Target.Result.RequestBindings.map((binding) => binding.FieldId) ?? []),
  ])
  const includeAllFields = targetFieldIds.size === 0

  return inputForm.Fields
    .filter((field) =>
      field.ValuePath &&
      (includeAllFields || targetFieldIds.has(field.Id) || targetFieldIds.has(field.FieldId))
    )
    .map((field) => ({
      fieldId: field.Id,
      valuePath: field.ValuePath,
    }))
}

export function readQueryFormUrlValue<TValue extends object>({
  defaultValue,
  fieldCodecs = {},
  fields,
  policy,
  queryForm = null,
  search,
}: QueryFormUrlValueOptions<TValue>): TValue {
  const urlPolicy = resolveQueryFormUrlPolicy(queryForm, policy)
  if (!urlPolicy) {
    return clonePlainValue(defaultValue)
  }

  const params = new URLSearchParams(search)
  const result = clonePlainValue(defaultValue)

  for (const field of fields) {
    const key = createQueryFormUrlFieldParamKey(queryForm, urlPolicy, field.fieldId)
    const defaultFieldValue = readObjectPath(defaultValue, field.valuePath)
    const codec = fieldCodecs[field.fieldId]
    const nextValue = codec?.read
      ? codec.read({
          defaultFieldValue,
          defaultValue,
          field,
          key,
          params,
          queryForm,
        })
      : readDefaultQueryFormUrlFieldValue(params, key, defaultFieldValue)

    if (nextValue !== undefined) {
      writeObjectPath(result as Record<string, unknown>, field.valuePath, nextValue)
    }
  }

  return result
}

export function createQueryFormUrlSearch<TValue extends object>({
  allValuesByFieldId = {},
  defaultValue,
  fieldCodecs = {},
  fields,
  policy,
  queryForm = null,
  search,
  value,
}: CreateQueryFormUrlSearchOptions<TValue>) {
  const urlPolicy = resolveQueryFormUrlPolicy(queryForm, policy)
  if (!urlPolicy) {
    return normalizeSearch(search)
  }

  const params = new URLSearchParams(search)

  for (const field of fields) {
    const key = createQueryFormUrlFieldParamKey(queryForm, urlPolicy, field.fieldId)
    const codec = fieldCodecs[field.fieldId]
    if (codec?.deleteParams) {
      codec.deleteParams({ defaultValue, field, key, params, queryForm })
    } else {
      params.delete(key)
    }
  }

  for (const field of fields) {
    const key = createQueryFormUrlFieldParamKey(queryForm, urlPolicy, field.fieldId)
    const defaultFieldValue = readObjectPath(defaultValue, field.valuePath)
    const fieldValue = readObjectPath(value, field.valuePath)
    const codec = fieldCodecs[field.fieldId]

    if (codec?.write) {
      codec.write({
        allValues: allValuesByFieldId[field.fieldId] ?? [],
        defaultFieldValue,
        defaultValue,
        field,
        fieldValue,
        key,
        params,
        queryForm,
        value,
      })
    } else {
      writeDefaultQueryFormUrlFieldValue(
        params,
        key,
        fieldValue,
        defaultFieldValue,
        allValuesByFieldId[field.fieldId] ?? [],
      )
    }
  }

  const nextSearch = params.toString()
  return nextSearch.length > 0 ? `?${nextSearch}` : ''
}

export function createQueryFormUrlSearchKey<TValue extends object>(
  options: QueryFormUrlValueOptions<TValue> &
    Pick<CreateQueryFormUrlSearchOptions<TValue>, 'allValuesByFieldId'>,
) {
  const value = readQueryFormUrlValue(options)
  return createQueryFormUrlSearch({
    ...options,
    search: '',
    value,
  })
}

export function createQueryFormUrlFieldParamKey(
  queryForm: QueryFormDefinition | null,
  policy: QueryFormUrlPolicyDefinition,
  fieldId: string,
) {
  const prefix = toQueryParamSegment(
    policy.ParameterPrefix?.trim() || queryForm?.Id || 'query',
  )
  return `${prefix}_${toQueryParamSegment(fieldId)}`
}

function resolveQueryFormUrlPolicy(
  queryForm: QueryFormDefinition | null,
  policy: QueryFormUrlPolicyDefinition | null | undefined,
) {
  const resolvedPolicy = policy ?? queryForm?.Target.State.Url ?? null
  return resolvedPolicy?.IsEnabled && resolvedPolicy.IncludeAppliedFilters
    ? resolvedPolicy
    : null
}

function readDefaultQueryFormUrlFieldValue(
  params: URLSearchParams,
  key: string,
  defaultValue: unknown,
) {
  const values = params.getAll(key).map((value) => value.trim()).filter(Boolean)
  if (values.length === 0) {
    return defaultValue
  }

  if (Array.isArray(defaultValue)) {
    return normalizeStringSet(values)
  }

  if (typeof defaultValue === 'boolean') {
    const value = values[0]?.toLocaleLowerCase()
    if (value === 'true') {
      return true
    }
    if (value === 'false') {
      return false
    }
    return defaultValue
  }

  if (typeof defaultValue === 'number') {
    const parsed = Number.parseFloat(values[0] ?? '')
    return Number.isFinite(parsed) ? parsed : defaultValue
  }

  return values[0] ?? defaultValue
}

function writeDefaultQueryFormUrlFieldValue(
  params: URLSearchParams,
  key: string,
  value: unknown,
  defaultValue: unknown,
  allValues: readonly string[],
) {
  if (Array.isArray(value)) {
    const selectedValues = normalizeStringSet(value)
    if (
      selectedValues.length === 0 ||
      areStringSetsEqual(selectedValues, normalizeStringSet(allValues)) ||
      areStringSetsEqual(selectedValues, normalizeStringSet(asStringArray(defaultValue)))
    ) {
      return
    }

    selectedValues.forEach((selectedValue) => params.append(key, selectedValue))
    return
  }

  if (typeof value === 'string') {
    const nextValue = value.trim()
    const defaultText = typeof defaultValue === 'string' ? defaultValue.trim() : ''
    if (nextValue && nextValue !== defaultText) {
      params.set(key, nextValue)
    }
    return
  }

  if (typeof value === 'boolean') {
    if (value !== defaultValue) {
      params.set(key, String(value))
    }
    return
  }

  if (typeof value === 'number' && Number.isFinite(value)) {
    if (value !== defaultValue) {
      params.set(key, String(value))
    }
  }
}

function clonePlainValue<TValue>(value: TValue): TValue {
  if (Array.isArray(value)) {
    return value.map(clonePlainValue) as TValue
  }

  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value).map(([key, entryValue]) => [key, clonePlainValue(entryValue)]),
    ) as TValue
  }

  return value
}

function normalizeSearch(search: string) {
  if (!search) {
    return ''
  }

  return search.startsWith('?') ? search : `?${search}`
}

function toQueryParamSegment(value: string) {
  return value
    .trim()
    .replace(/[^a-zA-Z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .toLocaleLowerCase()
}

function asStringArray(value: unknown) {
  return Array.isArray(value) ? value : []
}

function normalizeStringSet(values: readonly unknown[]) {
  return Array.from(
    new Set(
      values
        .map((value) => String(value).trim())
        .filter((value) => value.length > 0),
    ),
  )
}

function areStringSetsEqual(left: readonly string[], right: readonly string[]) {
  if (left.length === 0 || right.length === 0 || left.length !== right.length) {
    return false
  }

  const rightValues = new Set(right)
  return left.every((value) => rightValues.has(value))
}
