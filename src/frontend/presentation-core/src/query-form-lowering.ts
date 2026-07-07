import type {
  InputFormDefinition,
  InputFormFieldDefinition,
  PresentationModuleDefinition,
  QueryFormDefinition,
} from './module'
import { readObjectPath } from './object-path'

export interface QueryFormFieldValueNormalizationContext {
  readonly field: InputFormFieldDefinition
  readonly inputForm: InputFormDefinition
  readonly queryForm: QueryFormDefinition
  readonly value: unknown
}

export interface LowerQueryFormEndpointRequestOptions {
  readonly normalizeFieldValue?: (context: QueryFormFieldValueNormalizationContext) => unknown
  readonly inputForm: InputFormDefinition
  readonly queryForm: QueryFormDefinition
  readonly value: object
}

export function findPresentationQueryFormForResultDataSource(
  module: PresentationModuleDefinition | null,
  dataSourceId: string,
) {
  return module?.QueryForms.find(
    (queryForm) =>
      queryForm.Target.Result.DataSourceId === dataSourceId ||
      queryForm.Target.State.ResultDataSourceId === dataSourceId ||
      queryForm.Target.State.SynchronizedDataSourceIds.includes(dataSourceId),
  ) ?? null
}

export function lowerQueryFormEndpointRequest({
  inputForm,
  normalizeFieldValue,
  queryForm,
  value,
}: LowerQueryFormEndpointRequestOptions): Record<string, unknown> {
  const fieldsById = new Map(inputForm.Fields.map((field) => [field.Id, field]))
  const request: Record<string, unknown> = {}

  for (const binding of queryForm.Target.Result.RequestBindings) {
    const field = fieldsById.get(binding.FieldId)
    if (!field) {
      continue
    }

    const rawFieldValue = readObjectPath(value, field.ValuePath)
    const normalizedFieldValue = normalizeFieldValue
      ? normalizeFieldValue({ field, inputForm, queryForm, value: rawFieldValue })
      : rawFieldValue
    const requestValue = binding.ValuePath
      ? readObjectPath(normalizedFieldValue, binding.ValuePath)
      : normalizedFieldValue

    if (binding.OmitWhenEmpty && isEmptyRequestValue(requestValue)) {
      continue
    }

    request[binding.RequestField] = requestValue
  }

  if (queryForm.Target.Result.DefaultLimit !== null && queryForm.Target.Result.DefaultLimit !== undefined) {
    request.Limit ??= queryForm.Target.Result.DefaultLimit
  }

  return request
}

function isEmptyRequestValue(value: unknown) {
  return value === null ||
    value === undefined ||
    value === '' ||
    (Array.isArray(value) && value.length === 0)
}
