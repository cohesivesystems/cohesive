import type {
  ActionDefinition,
  ActionEndpointRequestProjectionDefinition,
  ActionEndpointRequestValueBindingDefinition,
  PresentationValueDefinition,
} from './module'
import { presentationValueKinds } from '@cohesive/presentation-contracts'
import {
  readObjectPath,
  writeObjectPath,
} from './object-path'
import type { EndpointExecutionRequest } from './endpoint-executor'

export type PresentationActionEndpointExecutionRequest<
  TBody = unknown,
  TQuery = unknown,
> = EndpointExecutionRequest<TBody, TQuery>

export interface PresentationActionEndpointRequestProjectionOptions {
  readonly action: ActionDefinition | null
  readonly dataSourceId?: string | null
  readonly endpointId: string
  readonly sources: Readonly<Record<string, unknown>>
}

export interface RequiredPresentationActionEndpointRequestProjectionOptions
  extends PresentationActionEndpointRequestProjectionOptions {
  readonly actionId: string
}

/**
 * Lowers a Presentation IR action endpoint request projection into the concrete
 * endpoint request envelope used by the app transport.
 */
export function projectPresentationActionEndpointRequest({
  action,
  dataSourceId,
  endpointId,
  sources,
}: PresentationActionEndpointRequestProjectionOptions): PresentationActionEndpointExecutionRequest | null {
  const projection = selectActionEndpointRequestProjection({
    action,
    dataSourceId,
    endpointId,
  })
  if (!projection) {
    return null
  }

  return {
    body: projectRequestBody(projection.BodyFields, sources),
    routeParameters: projectRouteParameters(projection.RouteParameters, sources),
  }
}

/**
 * Projects a required action endpoint request, failing early when the action
 * lacks an endpoint request projection for the selected endpoint/data source.
 */
export function projectRequiredPresentationActionEndpointRequest({
  action,
  actionId,
  dataSourceId,
  endpointId,
  sources,
}: RequiredPresentationActionEndpointRequestProjectionOptions): PresentationActionEndpointExecutionRequest {
  const request = projectPresentationActionEndpointRequest({
    action,
    dataSourceId,
    endpointId,
    sources,
  })
  if (!request) {
    throw new Error(
      `No endpoint request projection is registered for action '${actionId}' and endpoint '${endpointId}'.`,
    )
  }

  return request
}

function selectActionEndpointRequestProjection({
  action,
  dataSourceId,
  endpointId,
}: {
  readonly action: ActionDefinition | null
  readonly dataSourceId?: string | null
  readonly endpointId: string
}): ActionEndpointRequestProjectionDefinition | null {
  const endpointRequests = action?.EndpointRequests ?? []
  const endpointMatches = endpointRequests.filter((candidate) =>
    candidate.EndpointId === endpointId,
  )
  if (!dataSourceId && endpointMatches.length === 1) {
    return endpointMatches[0]
  }

  return endpointRequests.find((candidate) =>
    candidate.EndpointId === endpointId && candidate.DataSourceId === dataSourceId,
  ) ??
    endpointRequests.find((candidate) =>
      candidate.EndpointId === endpointId && !candidate.DataSourceId,
    ) ??
    endpointRequests.find((candidate) =>
      !candidate.EndpointId && candidate.DataSourceId === dataSourceId,
    ) ??
    endpointRequests.find((candidate) =>
      !candidate.EndpointId && !candidate.DataSourceId,
    ) ??
    null
}

function projectRouteParameters(
  bindings: readonly ActionEndpointRequestValueBindingDefinition[],
  sources: Readonly<Record<string, unknown>>,
) {
  return Object.fromEntries(
    bindings.flatMap((binding) => {
      const value =
        resolvePresentationActionRequestValue(binding.Source, sources) ??
        resolveFallbackRouteParameterValue(binding.TargetPath, sources)
      if ((value === null || value === undefined) && binding.OmitWhenNull) {
        return []
      }

      return [[binding.TargetPath, value == null ? value : String(value)]]
    }),
  )
}

function resolveFallbackRouteParameterValue(
  targetPath: string,
  sources: Readonly<Record<string, unknown>>,
) {
  if (targetPath !== 'id') {
    return undefined
  }

  return readObjectPath(sources, 'route.id') ?? readObjectPath(sources, 'resource.Id')
}

function projectRequestBody(
  bindings: readonly ActionEndpointRequestValueBindingDefinition[],
  sources: Readonly<Record<string, unknown>>,
) {
  if (bindings.length === 0) {
    return undefined
  }

  const body: Record<string, unknown> = {}
  for (const binding of bindings) {
    const resolvedValue = resolvePresentationActionRequestValue(binding.Source, sources)
    const value = resolvedValue === undefined
      ? resolveFallbackRequestBodyValue(binding.TargetPath, sources)
      : resolvedValue
    if ((value === null || value === undefined) && binding.OmitWhenNull) {
      continue
    }

    if (value === undefined && isDocumentRequestBodyTarget(binding.TargetPath)) {
      throw new Error(
        `Unable to project request body field '${binding.TargetPath}' because its source did not resolve.`,
      )
    }

    writeObjectPath(body, binding.TargetPath, value)
  }

  return body
}

function resolveFallbackRequestBodyValue(
  targetPath: string,
  sources: Readonly<Record<string, unknown>>,
) {
  if (!isDocumentRequestBodyTarget(targetPath)) {
    return undefined
  }

  const documentValue = readObjectPath(sources, 'document.value')
  if (documentValue !== undefined) {
    return documentValue
  }

  return readObjectPath(sources, 'document.Document')
}

function isDocumentRequestBodyTarget(targetPath: string) {
  return targetPath.toLowerCase() === 'document'
}

function resolvePresentationActionRequestValue(
  value: PresentationValueDefinition,
  sources: Readonly<Record<string, unknown>>,
) {
  if (isPresentationValueKind(value.Kind, presentationValueKinds.literal, 'literal')) {
    return value.Literal
  }

  if (isPresentationValueKind(value.Kind, presentationValueKinds.field, 'field')) {
    return value.Field ? readObjectPath(sources, value.Field) : undefined
  }

  if (isPresentationValueKind(value.Kind, presentationValueKinds.state, 'state')) {
    return value.StateId ? sources[value.StateId] : undefined
  }

  if (isPresentationValueKind(value.Kind, presentationValueKinds.expression, 'expression')) {
    throw new Error('Action endpoint request expression values are not supported yet.')
  }

  return undefined
}

function isPresentationValueKind(
  value: unknown,
  numericKind: number,
  stringKind: string,
) {
  return value === numericKind ||
    (typeof value === 'string' && value.toLowerCase() === stringKind.toLowerCase())
}
