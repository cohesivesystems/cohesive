import { Fragment, useMemo, type ReactNode } from 'react'

import {
  findPresentationField,
  findPresentationView,
  getPresentationViewProjectedFieldIds,
  type PresentationDataSourceResolver,
  type FieldPresentationDefinition,
  type ViewDefinition,
} from '@cohesive/presentation-core'
import {
  renderPresentationIcon,
} from './presentation-icon-registry'
import {
  projectPresentationIconDiagnostics,
} from './presentation-icon-diagnostics'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  usePresentationModule,
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesive/presentation-react'
import { fieldDisplayKinds } from '@cohesive/presentation-contracts'

export interface ProjectedMetricValue {
  readonly icon?: ReactNode
  readonly label?: string
  readonly value: ReactNode
  readonly variant?: 'number' | 'text'
}

export interface ProjectedMetricStripProps {
  readonly className?: string
  readonly componentSystem: PresentationComponentSystem
  readonly dataSourceResolver?: PresentationDataSourceResolver
  readonly fieldIds?: readonly string[]
  readonly iconByFieldId?: Readonly<Record<string, ReactNode>>
  readonly values?: Readonly<Record<string, ProjectedMetricValue>>
  readonly viewId?: string
}

const emptyMetricValues: Readonly<Record<string, ProjectedMetricValue>> = {}

export function ProjectedMetricStrip({
  className,
  componentSystem,
  dataSourceResolver,
  fieldIds,
  iconByFieldId,
  values,
  viewId,
}: ProjectedMetricStripProps) {
  const module = usePresentationModule()
  const resolvedValues = values ?? emptyMetricValues
  const view = viewId ? findPresentationView<ViewDefinition>(module, viewId) : null
  const items = useMemo(
    () => createMetricItems(module, view, resolvedValues, fieldIds, dataSourceResolver, iconByFieldId),
    [dataSourceResolver, fieldIds, iconByFieldId, module, resolvedValues, view],
  )
  const iconDiagnosticSource = `projected-metric-strip-icons:${view?.Id ?? viewId ?? 'adhoc'}`
  const iconDiagnostics = useMemo(
    () => projectPresentationIconDiagnostics({
      icons: resolveMetricIconSubjects(module, view, resolvedValues, fieldIds),
      module,
      source: iconDiagnosticSource,
      surfaceId: view?.Id ?? viewId,
      surfaceName: view?.Name,
    }),
    [
      fieldIds,
      iconDiagnosticSource,
      module,
      resolvedValues,
      view,
      viewId,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(iconDiagnosticSource, iconDiagnostics)

  if (items.length === 0) {
    return null
  }

  return componentSystem.metrics.MetricStrip({
    children: items.map((item) =>
      <Fragment key={item.id}>
        {componentSystem.metrics.MetricItem({
          icon: item.icon,
          id: item.id,
          label: item.label,
          value: item.value,
          variant: item.variant,
        })}
      </Fragment>,
    ),
    className,
  })
}

interface MetricItem extends ProjectedMetricValue {
  readonly id: string
  readonly label: string
}

function createMetricItems(
  module: ReturnType<typeof usePresentationModule>,
  view: ViewDefinition | null,
  values: Readonly<Record<string, ProjectedMetricValue>>,
  fieldIds?: readonly string[],
  dataSourceResolver?: PresentationDataSourceResolver,
  iconByFieldId?: Readonly<Record<string, ReactNode>>,
): readonly MetricItem[] {
  const projectedFieldIds = resolveMetricFieldIds(view, values, fieldIds)
  const orderedFieldIds = projectedFieldIds.length > 0 ? projectedFieldIds : Object.keys(values)

  return orderedFieldIds.flatMap((fieldId) => {
    const field = findPresentationField<FieldPresentationDefinition>(module, fieldId)
    const value = resolveMetricValue(module, fieldId, field, values[fieldId], dataSourceResolver, iconByFieldId)

    if (!value) {
      return []
    }

    return [{
      ...value,
      id: fieldId,
      label: value.label ?? field?.Label ?? fieldId,
      variant: value.variant ?? 'number',
    }]
  })
}

function resolveMetricValue(
  module: ReturnType<typeof usePresentationModule>,
  fieldId: string,
  field: FieldPresentationDefinition | null,
  value: ProjectedMetricValue | undefined,
  dataSourceResolver: PresentationDataSourceResolver | undefined,
  iconByFieldId: Readonly<Record<string, ReactNode>> | undefined,
) {
  if (!value) {
    return createMetricValue(module, fieldId, field, dataSourceResolver, iconByFieldId)
  }

  return {
    ...value,
    icon: value.icon ?? iconByFieldId?.[fieldId] ?? renderMetricFieldIcon(module, field),
  }
}

function createMetricValue(
  module: ReturnType<typeof usePresentationModule>,
  fieldId: string,
  field: FieldPresentationDefinition | null,
  dataSourceResolver: PresentationDataSourceResolver | undefined,
  iconByFieldId: Readonly<Record<string, ReactNode>> | undefined,
): ProjectedMetricValue | null {
  const value = readMetricFieldValue(field, dataSourceResolver)
  if (value === undefined) {
    return null
  }

  return {
    icon: iconByFieldId?.[fieldId] ?? renderMetricFieldIcon(module, field),
    value: renderMetricValue(value),
    variant: isTextMetric(field) ? 'text' : 'number',
  }
}

function resolveMetricIconSubjects(
  module: ReturnType<typeof usePresentationModule>,
  view: ViewDefinition | null,
  values: Readonly<Record<string, ProjectedMetricValue>>,
  fieldIds?: readonly string[],
) {
  return resolveMetricFieldIds(view, values, fieldIds).flatMap((fieldId) => {
    const field = findPresentationField<FieldPresentationDefinition>(module, fieldId)
    if (!field?.Icon) {
      return []
    }

    return [{
      details: {
        fieldId,
      },
      icon: field.Icon,
      id: fieldId,
      kind: 'metric-field-icon',
      label: field.Label,
    }]
  })
}

function resolveMetricFieldIds(
  view: ViewDefinition | null,
  values: Readonly<Record<string, ProjectedMetricValue>>,
  fieldIds?: readonly string[],
) {
  return fieldIds ?? (view ? getPresentationViewProjectedFieldIds(view) : Object.keys(values))
}

function renderMetricFieldIcon(
  module: ReturnType<typeof usePresentationModule>,
  field: FieldPresentationDefinition | null,
) {
  return renderPresentationIcon({
    className: 'size-4',
    icon: field?.Icon,
    module,
  })
}

function readMetricFieldValue(
  field: FieldPresentationDefinition | null,
  dataSourceResolver: PresentationDataSourceResolver | undefined,
) {
  if (!field || !dataSourceResolver) {
    return undefined
  }

  const source = field.Source
  if (source) {
    const sourceValue = dataSourceResolver.readPath(source.DataSourceId, source.FieldPath)
    if (sourceValue !== undefined) {
      return sourceValue
    }
  }

  const [root, ...path] = field.Field.split('.')
  return root ? dataSourceResolver.readPath(root, path.join('.')) : undefined
}

function renderMetricValue(value: unknown) {
  if (value === null || value === undefined || value === '') {
    return 'none'
  }

  return String(value)
}

function isTextMetric(field: FieldPresentationDefinition | null) {
  return field?.DisplayKind === fieldDisplayKinds.text
}
