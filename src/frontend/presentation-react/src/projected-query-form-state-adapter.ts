import type {
  InputFormDefinition,
  InputFormFieldDefinition,
  PresentationValueDefinition,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationQueryFormStateAdapter,
  PresentationQueryFormStateAdapterContext,
} from './presentation-query-form-state'
import {
  createQueryFormUrlFields,
  createQueryFormUrlSearch,
  createQueryFormUrlSearchKey,
  readObjectPath,
  readQueryFormUrlValue,
  type QueryFormUrlFieldCodecs,
  writeObjectPath,
} from '@cohesivesystems/presentation-core'
import {
  createPresentationEnumDiscriminator,
  matchesPresentationEnum,
} from '@cohesivesystems/presentation-core'
import {
  inputFormChoiceDefaultSelections,
  inputFormFieldControlKinds,
  inputFormFieldKinds,
  presentationValueKinds,
} from '@cohesivesystems/presentation-contracts'
import {
  createDefaultDateTimeFilter,
  isDateTimePreset,
  normalizeDateTimeFilter,
  type DateTimeFilterValue,
} from '@cohesivesystems/presentation-core'

type ProjectedQueryFormValue = Record<string, unknown>

const dateTimeRangeParamValue = 'range'

/**
 * Creates the default frontend interpretation for an IR-declared query form.
 *
 * The adapter derives draft/applied state shape, normalization, and URL state
 * from the query form's backing input-form fields. Targets can still override a
 * query form by registering an adapter with the same query form id.
 */
export function createProjectedQueryFormStateAdapter(
  queryFormId: string,
): PresentationQueryFormStateAdapter<ProjectedQueryFormValue> {
  return {
    createAppliedSearch: ({
      choiceValuesByFieldId,
      inputForm,
      queryForm,
      search,
      value,
    }) =>
      createQueryFormUrlSearch({
        allValuesByFieldId: choiceValuesByFieldId,
        defaultValue: createProjectedQueryFormDefaultValue({
          choiceValuesByFieldId,
          inputForm,
        }),
        fieldCodecs: createProjectedQueryFormUrlFieldCodecs(inputForm),
        fields: createQueryFormUrlFields(inputForm, queryForm),
        queryForm,
        search,
        value: normalizeProjectedQueryFormValue({
          choiceValuesByFieldId,
          inputForm,
          value,
        }),
      }),
    createDefaultValue: (context) =>
      createProjectedQueryFormDefaultValue(context),
    createSearchKey: ({
      choiceValuesByFieldId = {},
      inputForm,
      queryForm,
      search,
    }) =>
      createQueryFormUrlSearchKey({
        allValuesByFieldId: choiceValuesByFieldId,
        defaultValue: createProjectedQueryFormDefaultValue({
          choiceValuesByFieldId,
          inputForm,
        }),
        fieldCodecs: createProjectedQueryFormUrlFieldCodecs(inputForm),
        fields: createQueryFormUrlFields(inputForm, queryForm),
        queryForm,
        search,
      }),
    normalizeValue: ({ choiceValuesByFieldId, inputForm, value }) =>
      normalizeProjectedQueryFormValue({
        choiceValuesByFieldId,
        inputForm,
        value,
      }),
    queryFormId,
    readValueFromSearch: ({
      choiceValuesByFieldId = {},
      inputForm,
      queryForm,
      search,
    }) =>
      normalizeProjectedQueryFormValue({
        choiceValuesByFieldId,
        inputForm,
        value: readQueryFormUrlValue({
          defaultValue: createProjectedQueryFormDefaultValue({
            choiceValuesByFieldId,
            inputForm,
          }),
          fieldCodecs: createProjectedQueryFormUrlFieldCodecs(inputForm),
          fields: createQueryFormUrlFields(inputForm, queryForm),
          queryForm,
          search,
        }),
      }),
  }
}

function createProjectedQueryFormDefaultValue({
  choiceValuesByFieldId = {},
  inputForm,
}: Pick<
  PresentationQueryFormStateAdapterContext,
  'choiceValuesByFieldId' | 'inputForm'
>): ProjectedQueryFormValue {
  if (!inputForm) {
    return {}
  }

  const value: ProjectedQueryFormValue = {}
  for (const field of inputForm.Fields) {
    writeObjectPath(
      value,
      field.ValuePath,
      createDefaultInputFormFieldValue(
        field,
        readChoiceValues(choiceValuesByFieldId, field),
      ),
    )
  }

  return applyLiteralInputFormDefaultValues(inputForm, value)
}

function normalizeProjectedQueryFormValue({
  choiceValuesByFieldId,
  inputForm,
  value,
}: {
  readonly choiceValuesByFieldId: Readonly<Record<string, readonly string[]>>
  readonly inputForm: InputFormDefinition | null
  readonly value: ProjectedQueryFormValue
}) {
  if (!inputForm) {
    return value
  }

  const nextValue = clonePlainObject(value)
  for (const field of inputForm.Fields) {
    const choiceValues = readChoiceValues(choiceValuesByFieldId, field)
    const currentValue = readObjectPath(nextValue, field.ValuePath)
    writeObjectPath(
      nextValue,
      field.ValuePath,
      normalizeInputFormFieldValue(field, currentValue, choiceValues),
    )
  }

  return nextValue
}

function createDefaultInputFormFieldValue(
  field: InputFormFieldDefinition,
  choiceValues: readonly string[],
): unknown {
  if (isInputFormFieldKind(field, 'multiSelect', 'MultiSelect')) {
    return createDefaultChoiceSelection(field, choiceValues)
  }

  if (isInputFormFieldKind(field, 'select', 'Select')) {
    return createDefaultSingleChoice(field, choiceValues)
  }

  if (isInputFormFieldKind(field, 'dateTimeRange', 'DateTimeRange')) {
    return isDateTimeFilterControl(field)
      ? createDefaultDateTimeFilter()
      : { after: null, before: null }
  }

  if (isInputFormFieldKind(field, 'number', 'Number')) {
    return readDefaultNumber(field.DefaultValue)
  }

  if (isInputFormFieldKind(field, 'numberRange', 'NumberRange')) {
    return { maximum: null, minimum: null }
  }

  if (isInputFormFieldKind(field, 'boolean', 'Boolean')) {
    return field.DefaultValue === 'true'
  }

  return field.DefaultValue ?? ''
}

function normalizeInputFormFieldValue(
  field: InputFormFieldDefinition,
  value: unknown,
  choiceValues: readonly string[],
): unknown {
  if (isInputFormFieldKind(field, 'multiSelect', 'MultiSelect')) {
    const selectedValues = normalizeStringSet(value)
    const availableValues = normalizeStringSet(choiceValues)
    const availableSet = new Set(availableValues)
    const visibleSelectedValues = availableSet.size > 0
      ? selectedValues.filter((selectedValue) => availableSet.has(selectedValue))
      : selectedValues

    return visibleSelectedValues.length > 0
      ? visibleSelectedValues
      : createDefaultChoiceSelection(field, availableValues)
  }

  if (isInputFormFieldKind(field, 'select', 'Select')) {
    const selectedValue = typeof value === 'string' ? value.trim() : ''
    const availableValues = normalizeStringSet(choiceValues)
    return selectedValue && (availableValues.length === 0 || availableValues.includes(selectedValue))
      ? selectedValue
      : createDefaultSingleChoice(field, availableValues)
  }

  if (isInputFormFieldKind(field, 'dateTimeRange', 'DateTimeRange')) {
    return isDateTimeFilterControl(field)
      ? coerceDateTimeFilterValue(value)
      : normalizePlainDateTimeRange(value)
  }

  if (isInputFormFieldKind(field, 'dateTime', 'DateTime') ||
      isInputFormFieldKind(field, 'date', 'Date')) {
    return typeof value === 'string' ? value.trim() : ''
  }

  if (isInputFormFieldKind(field, 'number', 'Number')) {
    return typeof value === 'number' && Number.isFinite(value)
      ? value
      : readDefaultNumber(field.DefaultValue)
  }

  if (isInputFormFieldKind(field, 'numberRange', 'NumberRange')) {
    return normalizeNumberRange(value)
  }

  if (isInputFormFieldKind(field, 'boolean', 'Boolean')) {
    return value === true
  }

  return typeof value === 'string' ? value.trim() : field.DefaultValue ?? ''
}

function createProjectedQueryFormUrlFieldCodecs(
  inputForm: InputFormDefinition | null,
): QueryFormUrlFieldCodecs<ProjectedQueryFormValue> {
  if (!inputForm) {
    return {}
  }

  return Object.fromEntries(
    inputForm.Fields
      .filter((field) =>
        isInputFormFieldKind(field, 'dateTimeRange', 'DateTimeRange') &&
        isDateTimeFilterControl(field)
      )
      .map((field) => [
        field.Id,
        {
          deleteParams: ({ key, params }) =>
            deleteDateTimeFilterParams(params, key),
          read: ({ defaultFieldValue, key, params }) =>
            readDateTimeFilterParams(
              params,
              key,
              coerceDateTimeFilterValue(defaultFieldValue),
            ),
          write: ({ fieldValue, key, params }) =>
            writeDateTimeFilterParams(
              params,
              key,
              coerceDateTimeFilterValue(fieldValue),
            ),
        },
      ]),
  ) satisfies QueryFormUrlFieldCodecs<ProjectedQueryFormValue>
}

function readDateTimeFilterParams(
  params: URLSearchParams,
  key: string,
  defaults: DateTimeFilterValue,
): DateTimeFilterValue {
  const value = params.get(key)?.trim() ?? ''
  const timezone = params.get(createDateTimeFilterParam(key, 'tz'))?.trim() || defaults.timezone

  if (value === dateTimeRangeParamValue) {
    return normalizeDateTimeFilter({
      ...defaults,
      afterLocal: params.get(createDateTimeFilterParam(key, 'after'))?.trim() ?? '',
      beforeLocal: params.get(createDateTimeFilterParam(key, 'before'))?.trim() ?? '',
      mode: 'range',
      timezone,
    })
  }

  if (value && isDateTimePreset(value)) {
    return normalizeDateTimeFilter({
      ...defaults,
      mode: 'preset',
      preset: value,
      timezone,
    })
  }

  return defaults
}

function writeDateTimeFilterParams(
  params: URLSearchParams,
  key: string,
  value: DateTimeFilterValue,
) {
  const normalizedValue = normalizeDateTimeFilter(value)

  if (normalizedValue.mode === 'range') {
    params.set(key, dateTimeRangeParamValue)
    writeStringParam(params, createDateTimeFilterParam(key, 'after'), normalizedValue.afterLocal)
    writeStringParam(params, createDateTimeFilterParam(key, 'before'), normalizedValue.beforeLocal)
    writeStringParam(params, createDateTimeFilterParam(key, 'tz'), normalizedValue.timezone)
    return
  }

  if (normalizedValue.preset !== 'today') {
    params.set(key, normalizedValue.preset)
  }
}

function deleteDateTimeFilterParams(params: URLSearchParams, key: string) {
  params.delete(key)
  params.delete(createDateTimeFilterParam(key, 'after'))
  params.delete(createDateTimeFilterParam(key, 'before'))
  params.delete(createDateTimeFilterParam(key, 'tz'))
}

function createDateTimeFilterParam(key: string, suffix: 'after' | 'before' | 'tz') {
  return `${key}_${suffix}`
}

function writeStringParam(params: URLSearchParams, key: string, value: string) {
  const trimmedValue = value.trim()
  if (trimmedValue) {
    params.set(key, trimmedValue)
  }
}

function createDefaultChoiceSelection(
  field: InputFormFieldDefinition,
  choiceValues: readonly string[],
) {
  if (isInputFormChoiceDefaultSelection(field, 'all', 'All')) {
    return [...choiceValues]
  }

  if (isInputFormChoiceDefaultSelection(field, 'first', 'First')) {
    return choiceValues[0] ? [choiceValues[0]] : []
  }

  return normalizeStringSet(field.DefaultValue)
}

function createDefaultSingleChoice(
  field: InputFormFieldDefinition,
  choiceValues: readonly string[],
) {
  if (isInputFormChoiceDefaultSelection(field, 'first', 'First')) {
    return choiceValues[0] ?? ''
  }

  return field.DefaultValue ?? ''
}

function readChoiceValues(
  choiceValuesByFieldId: Readonly<Record<string, readonly string[]>>,
  field: InputFormFieldDefinition,
) {
  return choiceValuesByFieldId[field.Id] ??
    choiceValuesByFieldId[field.FieldId] ??
    []
}

function coerceDateTimeFilterValue(value: unknown): DateTimeFilterValue {
  const fallback = createDefaultDateTimeFilter()
  if (!value || typeof value !== 'object') {
    return fallback
  }

  const record = value as Record<string, unknown>
  return normalizeDateTimeFilter({
    afterLocal: readOptionalString(record.afterLocal) || fallback.afterLocal,
    beforeLocal: readOptionalString(record.beforeLocal) || fallback.beforeLocal,
    mode: record.mode === 'range' ? 'range' : 'preset',
    preset: readOptionalString(record.preset) as DateTimeFilterValue['preset'],
    timezone: readOptionalString(record.timezone) || fallback.timezone,
  })
}

function normalizePlainDateTimeRange(value: unknown) {
  const range = value && typeof value === 'object'
    ? value as Record<string, unknown>
    : {}
  return {
    after: readOptionalString(range.after) || null,
    before: readOptionalString(range.before) || null,
  }
}

function normalizeNumberRange(value: unknown) {
  const range = value && typeof value === 'object'
    ? value as Record<string, unknown>
    : {}
  return {
    maximum: readOptionalNumber(range.maximum),
    minimum: readOptionalNumber(range.minimum),
  }
}

function readDefaultNumber(value: string | null | undefined) {
  return readOptionalNumber(value) ?? null
}

function readOptionalNumber(value: unknown) {
  if (typeof value === 'string' && value.trim() === '') {
    return null
  }

  const numberValue = typeof value === 'number'
    ? value
    : Number(value)
  return Number.isFinite(numberValue) ? numberValue : null
}

function readOptionalString(value: unknown) {
  return typeof value === 'string' ? value : ''
}

function normalizeStringSet(value: unknown) {
  const values = Array.isArray(value) ? value : typeof value === 'string' ? [value] : []
  return Array.from(
    new Set(
      values
        .map((entry) => typeof entry === 'string' ? entry.trim() : '')
        .filter((entry) => entry.length > 0),
    ),
  )
}

function applyLiteralInputFormDefaultValues(
  inputForm: InputFormDefinition,
  value: ProjectedQueryFormValue,
) {
  const defaultValues = inputForm.DefaultValues ?? []
  if (defaultValues.length === 0) {
    return value
  }

  const target = clonePlainObject(value)
  for (const binding of defaultValues) {
    const resolvedValue = resolveLiteralDefaultValue(binding.Source)
    if ((resolvedValue === null || resolvedValue === undefined) && binding.OmitWhenNull) {
      continue
    }

    if (resolvedValue !== undefined) {
      writeObjectPath(target, binding.TargetPath, resolvedValue)
    }
  }

  return target
}

function resolveLiteralDefaultValue(value: PresentationValueDefinition) {
  if (isPresentationValueKind(value.Kind, presentationValueKinds.literal, 'literal')) {
    return parseDefaultValue(value.Literal)
  }

  return undefined
}

function parseDefaultValue(value: string | null | undefined) {
  if (value === null || value === undefined) {
    return undefined
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
  return Number.isFinite(numberValue) && value.trim() !== ''
    ? numberValue
    : value
}

function isInputFormFieldKind(
  field: InputFormFieldDefinition,
  kind: keyof typeof inputFormFieldKinds,
  label: string,
) {
  return matchesPresentationEnum(
    field.Kind,
    createPresentationEnumDiscriminator(inputFormFieldKinds, kind, label),
  )
}

function isInputFormChoiceDefaultSelection(
  field: InputFormFieldDefinition,
  selection: keyof typeof inputFormChoiceDefaultSelections,
  label: string,
) {
  const defaultSelection = field.ChoiceSource?.DefaultSelection
  return defaultSelection !== null &&
    defaultSelection !== undefined &&
    matchesPresentationEnum(
      defaultSelection,
      createPresentationEnumDiscriminator(inputFormChoiceDefaultSelections, selection, label),
    )
}

function isDateTimeFilterControl(field: InputFormFieldDefinition) {
  const control = field.Display?.Control
  return control === inputFormFieldControlKinds.dateTimeFilter ||
    control?.toString().toLocaleLowerCase() === 'datetimefilter'
}

function isPresentationValueKind(
  value: unknown,
  numericKind: number,
  stringKind: string,
) {
  return value === numericKind ||
    (typeof value === 'string' && value.toLowerCase() === stringKind.toLowerCase())
}

function clonePlainObject(value: ProjectedQueryFormValue) {
  return clonePlainValue(value) as ProjectedQueryFormValue
}

function clonePlainValue(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(clonePlainValue)
  }

  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value).map(([key, entry]) => [key, clonePlainValue(entry)]),
    )
  }

  return value
}
