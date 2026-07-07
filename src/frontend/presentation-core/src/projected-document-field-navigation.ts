import type {
  FieldPresentationDefinition,
  PresentationModuleDefinition,
} from './module'
import type {
  NavigationRouteParameters,
  PresentationNavigationHrefFactory,
} from './navigation'
import {
  readObjectPath,
} from './object-path'
import {
  createPresentationEnumDiscriminator,
  matchesPresentationEnum,
} from './target-bindings'
import {
  presentationBindingKinds,
} from '@cohesive/presentation-contracts'

/**
 * Inputs used to project a document field value into a navigation binding.
 */
export interface ProjectDocumentFieldNavigationBindingOptions {
  /** Optional route-to-href factory supplied by the host navigation runtime. */
  readonly createHref?: PresentationNavigationHrefFactory

  /** Presentation field whose target binding may declare a navigation route. */
  readonly field: FieldPresentationDefinition

  /** Presentation module target bindings used to find the field route binding. */
  readonly module: Pick<PresentationModuleDefinition, 'Targets'> | null

  /** Resource that contains route parameter source fields. */
  readonly resource: unknown

  /** Field value rendered as the navigation label and default route parameter. */
  readonly value: unknown
}

/**
 * Concrete navigation binding projected from a document field.
 */
export interface ProjectedDocumentFieldNavigationBinding {
  /** Host-generated href, or `null` when no href factory was supplied. */
  readonly href: string | null

  /** Scalar label rendered for the navigable field value. */
  readonly label: string

  /** Required route parameter names that could not be resolved. */
  readonly missingParameterNames: readonly string[]

  /** Route parameter values projected from the field value and resource. */
  readonly parameters: NavigationRouteParameters

  /** Human-facing prefix rendered before the navigation label. */
  readonly prefix: string

  /** Semantic navigation route id selected by the field target binding. */
  readonly routeId: string
}

/**
 * Route projection for a document field before href generation is applied.
 */
export interface ProjectedDocumentFieldNavigationProjection {
  /** Scalar label rendered for the navigable field value. */
  readonly label: string

  /** Required route parameter names that could not be resolved. */
  readonly missingParameterNames: readonly string[]

  /** Route parameter values projected from the field value and resource. */
  readonly parameters: NavigationRouteParameters

  /** Human-facing prefix rendered before the navigation label. */
  readonly prefix: string

  /** Semantic navigation route id selected by the field target binding. */
  readonly routeId: string
}

interface DocumentFieldNavigationBindingOptions {
  readonly labelPrefix?: string | null
  readonly parameters?: readonly DocumentFieldNavigationParameterBinding[]
}

interface DocumentFieldNavigationParameterBinding {
  readonly fieldPath?: string | null
  readonly name: string
  readonly source?: 'resource' | 'value' | string | null
}

/**
 * Projects a document field into a complete navigation binding.
 *
 * Returns `null` when the field has no navigation route binding or the field
 * value cannot be rendered as a scalar navigation label.
 */
export function projectDocumentFieldNavigationBinding({
  createHref,
  field,
  module,
  resource,
  value,
}: ProjectDocumentFieldNavigationBindingOptions): ProjectedDocumentFieldNavigationBinding | null {
  const projection = projectDocumentFieldNavigation({
    field,
    module,
    resource,
    value,
  })
  if (!projection) {
    return null
  }

  return {
    ...projection,
    href: createHref?.(projection.routeId, projection.parameters) ?? null,
  }
}

/**
 * Projects a document field into route id, label, prefix, and route parameters.
 *
 * The projection uses target binding options to map route parameters from the
 * current field value or backing resource. If no parameter mapping is declared,
 * it defaults to an `id` parameter sourced from the field value.
 */
export function projectDocumentFieldNavigation({
  field,
  module,
  resource,
  value,
}: Omit<ProjectDocumentFieldNavigationBindingOptions, 'createHref'>): ProjectedDocumentFieldNavigationProjection | null {
  const binding = findDocumentFieldNavigationRouteBinding(module, field)
  if (!binding?.RouteId) {
    return null
  }

  const label = readNavigationScalar(value)
  if (!label) {
    return null
  }

  const options = readDocumentFieldNavigationBindingOptions(binding.Options)
  const parameterProjection = projectNavigationRouteParameters({
    options,
    resource,
    value,
  })

  return {
    label,
    missingParameterNames: parameterProjection.missingParameterNames,
    parameters: parameterProjection.parameters,
    prefix: options.labelPrefix ?? field.Label,
    routeId: binding.RouteId,
  }
}

/**
 * Finds the navigation-route target binding declared for a presentation field.
 *
 * Route-bearing bindings are preferred when multiple target bindings share the
 * same field id; otherwise the first matching binding is returned.
 */
export function findDocumentFieldNavigationRouteBinding(
  module: Pick<PresentationModuleDefinition, 'Targets'> | null,
  field: FieldPresentationDefinition,
) {
  const bindings = module?.Targets
    .flatMap((target) => target.Bindings)
    .filter((binding) =>
      matchesPresentationEnum(binding.Kind, navigationRouteBindingKind) &&
      binding.Id === field.Id) ?? []

  return bindings.find((binding) => binding.RouteId) ?? bindings[0] ?? null
}

function projectNavigationRouteParameters({
  options,
  resource,
  value,
}: {
  readonly options: DocumentFieldNavigationBindingOptions
  readonly resource: unknown
  readonly value: unknown
}): {
  readonly missingParameterNames: readonly string[]
  readonly parameters: NavigationRouteParameters
} {
  const parameterBindings = options.parameters?.length
    ? options.parameters
    : [{ name: 'id', source: 'value' }]
  const parameters: NavigationRouteParameters = {}
  const missingParameterNames: string[] = []

  for (const parameter of parameterBindings) {
    const parameterValue = projectNavigationRouteParameterValue({
      parameter,
      resource,
      value,
    })
    if (parameterValue === null || parameterValue === undefined) {
      missingParameterNames.push(parameter.name)
      continue
    }

    parameters[parameter.name] = parameterValue
  }

  return {
    missingParameterNames,
    parameters,
  }
}

function projectNavigationRouteParameterValue({
  parameter,
  resource,
  value,
}: {
  readonly parameter: DocumentFieldNavigationParameterBinding
  readonly resource: unknown
  readonly value: unknown
}) {
  const source = parameter.source ?? 'value'
  const resolvedValue =
    source === 'resource' && parameter.fieldPath
      ? readObjectPath(resource, parameter.fieldPath)
      : value

  return readNavigationScalar(resolvedValue)
}

function readDocumentFieldNavigationBindingOptions(
  value: unknown,
): DocumentFieldNavigationBindingOptions {
  if (!value || typeof value !== 'object') {
    return {}
  }

  const source = value as Record<string, unknown>
  const labelPrefix = readOptionalString(source.labelPrefix ?? source.LabelPrefix)
  const rawParameters = source.parameters ?? source.Parameters
  const parameters = Array.isArray(rawParameters)
    ? rawParameters
      .map(readDocumentFieldNavigationParameterBinding)
      .filter((parameter): parameter is DocumentFieldNavigationParameterBinding =>
        Boolean(parameter))
    : undefined

  return {
    labelPrefix,
    parameters,
  }
}

function readDocumentFieldNavigationParameterBinding(
  value: unknown,
): DocumentFieldNavigationParameterBinding | null {
  if (!value || typeof value !== 'object') {
    return null
  }

  const source = value as Record<string, unknown>
  const name = readOptionalString(source.name ?? source.Name)
  if (!name) {
    return null
  }

  return {
    fieldPath: readOptionalString(source.fieldPath ?? source.FieldPath),
    name,
    source: readOptionalString(source.source ?? source.Source),
  }
}

function readNavigationScalar(value: unknown) {
  if (typeof value === 'string' && value.length > 0) {
    return value
  }

  if (typeof value === 'number' || typeof value === 'bigint' || typeof value === 'boolean') {
    return value.toString()
  }

  return null
}

function readOptionalString(value: unknown) {
  return typeof value === 'string' && value.length > 0 ? value : null
}

const navigationRouteBindingKind = createPresentationEnumDiscriminator(
  presentationBindingKinds,
  'navigationRoute',
  'NavigationRoute',
)
