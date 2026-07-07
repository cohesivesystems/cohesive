import { type ReactNode } from 'react'

import {
  findPresentationField,
  findPresentationView,
  getPresentationViewProjectedFieldIds,
  readObjectPath,
  type FieldPresentationDefinition,
  type PresentationHrefNavigator,
  type PresentationNavigationHrefFactory,
  type ViewDefinition,
} from '@cohesivesystems/presentation-core'
import {
  usePresentationModule,
  usePresentationNavigationRuntime,
} from '@cohesivesystems/presentation-react'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  renderProjectedFieldValue,
} from './projected-field-value-rendering'

export interface ProjectedRecordFieldRenderContext<TData extends object> {
  readonly field: FieldPresentationDefinition
  readonly record: TData
  readonly value: unknown
}

export interface ProjectedRecordDetailsProps<TData extends object> {
  readonly componentSystem: PresentationComponentSystem
  readonly createHref?: PresentationNavigationHrefFactory
  readonly data: TData
  readonly emptyMessage?: string
  readonly fieldRenderers?: Record<
    string,
    (context: ProjectedRecordFieldRenderContext<TData>) => ReactNode
  >
  readonly hiddenFieldLabels?: readonly string[]
  readonly navigateHref?: PresentationHrefNavigator
  readonly viewId: string
}

export function ProjectedRecordDetails<TData extends object>({
  componentSystem,
  createHref: createHrefOverride,
  data,
  emptyMessage = 'No details are available.',
  fieldRenderers,
  hiddenFieldLabels = [],
  navigateHref: navigateHrefOverride,
  viewId,
}: ProjectedRecordDetailsProps<TData>) {
  const module = usePresentationModule()
  const navigation = usePresentationNavigationRuntime()
  const createHref = createHrefOverride ?? navigation.createHref
  const navigateHref = navigateHrefOverride ?? navigation.navigateHref
  const view = findPresentationView<ViewDefinition>(module, viewId)
  const fields =
    (view ? getPresentationViewProjectedFieldIds(view) : []).flatMap((fieldId) => {
      const field = findPresentationField<FieldPresentationDefinition>(module, fieldId)
      return field ? [field] : []
    })
  const RecordDetailEmptyState = componentSystem.records.RecordDetailEmptyState
  const RecordDetailField = componentSystem.records.RecordDetailField
  const RecordDetails = componentSystem.records.RecordDetails

  if (!module || !view || fields.length === 0) {
    return (
      <RecordDetailEmptyState label={emptyMessage} viewId={viewId} />
    )
  }

  return (
    <RecordDetails viewId={viewId}>
      {fields.map((field) => {
        const value = readProjectedValue(data, field.Field)
        const renderer = fieldRenderers?.[field.Id] ?? fieldRenderers?.[field.Field]
        const hideLabel = isFieldLabelHidden(field, hiddenFieldLabels)
        const renderedValue = renderer
          ? renderer({ field, record: data, value })
          : renderProjectedFieldValue({
            componentSystem,
            createHref,
            emptyValueFallback: <span className="text-slate-400">n/a</span>,
            field,
            module,
            navigateHref,
            resource: data,
            value,
          })
        return (
          <RecordDetailField
            fieldId={field.Id}
            hideLabel={hideLabel}
            key={field.Id}
            label={field.Label}
            value={renderedValue}
          />
        )
      })}
    </RecordDetails>
  )
}

function isFieldLabelHidden(
  field: FieldPresentationDefinition,
  hiddenFieldLabels: readonly string[],
) {
  return hiddenFieldLabels.includes(field.Id) || hiddenFieldLabels.includes(field.Field)
}

function readProjectedValue<TData extends object>(row: TData, fieldPath: string) {
  const exactValue = readObjectPath(row, fieldPath)
  if (exactValue !== undefined) {
    return exactValue
  }

  const fieldName = fieldPath.split('.').at(-1) ?? fieldPath
  return readObjectProperty(row, fieldName)
}

function readObjectProperty<TData extends object>(row: TData, propertyName: string) {
  const record = row as Record<string, unknown>
  if (Object.prototype.hasOwnProperty.call(record, propertyName)) {
    return record[propertyName]
  }

  const match = Object.keys(record).find(
    (candidate) => candidate.toLocaleLowerCase() === propertyName.toLocaleLowerCase(),
  )
  return match ? record[match] : undefined
}
