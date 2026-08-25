import type {
  NavigationRouteParameters,
  PresentationNavigationHrefFactory,
} from './navigation'
import type {
  ActionDefinition,
  ProcessTaskSelectorDefinition,
} from './module'
import {
  type ProcessTask,
  createProcessTaskLifecycle,
  processTaskLifecycleDiagnosticCodes,
  type ProcessTaskLifecycleDeclaration,
  type ProcessTaskMetadata,
  type ProcessTaskSelector,
  type ProcessTaskStartRegistration,
} from './process-task-model'
import {
  createPresentationEnumDiscriminator,
  matchesPresentationEnum,
} from './target-bindings'
import {
  actionEnablementCriterionKinds,
} from '@cohesivesystems/presentation-contracts'

type ProcessTaskScalarMetadataKey = {
  [K in keyof ProcessTaskMetadata]-?: NonNullable<ProcessTaskMetadata[K]> extends string
    ? K
    : never
}[keyof ProcessTaskMetadata]

export interface ProjectProcessTaskActionEnablementOptions<TContext> {
  readonly action: ActionDefinition | null | undefined
  readonly context: TContext
  readonly findActiveTask: (selector: ProcessTaskSelector) => ProcessTask | null
  readonly projectSelector?: (
    selector: ProcessTaskSelectorDefinition | null | undefined,
    context: TContext,
  ) => ProcessTaskSelector | null
  readonly selectors: readonly ProcessTaskSelectorDefinition[]
}

export interface ProjectedProcessTaskActionEnablement {
  readonly activeTask: ProcessTask | null
  readonly blockingCriterionId: string | null
  readonly isDisabled: boolean
  readonly message: string | null
  readonly selector: ProcessTaskSelectorDefinition | null
}

export interface ProjectProcessTaskStartRegistrationOptions {
  readonly action?: ActionDefinition | null
  readonly createHref?: PresentationNavigationHrefFactory | null
  readonly dataSourceQueryKey?: (dataSourceId: string) => readonly unknown[]
  readonly detailsHref?: string | null
  readonly failureMessage?: string | null
  readonly invalidateQueryKeys?: readonly (readonly unknown[])[]
  readonly lifecycle?: ProcessTaskLifecycleDeclaration | null
  readonly metadata?: ProcessTaskMetadata
  readonly processTypeLabel?: string | null
  readonly processTypeTone?: string | null
  readonly result: unknown
  readonly selector?: ProcessTaskSelectorDefinition | null
  readonly sourceHref?: string | null
  readonly startedToast?: ProcessTaskStartRegistration['startedToast']
  readonly targetHref?: string | null
  readonly terminalInvalidateQueryKeys?: readonly (readonly unknown[])[]
  readonly context?: Readonly<Record<string, unknown>>
}

/**
 * Interprets process-task-backed action enablement from presentation IR.
 *
 * The backend declares `NoActiveProcessTask` criteria; this projection resolves
 * those criteria against process-task selector definitions and the host's
 * active task lookup.
 */
export function projectProcessTaskActionEnablement<TContext>({
  action,
  context,
  findActiveTask,
  projectSelector = projectProcessTaskSelector,
  selectors,
}: ProjectProcessTaskActionEnablementOptions<TContext>): ProjectedProcessTaskActionEnablement {
  let firstSelector: ProcessTaskSelectorDefinition | null = null

  for (const criterion of action?.Enablement ?? []) {
    if (
      !matchesPresentationEnum(
        criterion.Kind,
        noActiveProcessTaskCriterionKind,
      ) ||
      !criterion.ProcessTaskSelectorId
    ) {
      continue
    }

    const selectorDefinition = selectors.find(
      (selector) => selector.Id === criterion.ProcessTaskSelectorId,
    ) ?? null
    firstSelector ??= selectorDefinition
    const selector = projectSelector(selectorDefinition, context)
    const activeTask = selector ? findActiveTask(selector) : null
    if (activeTask) {
      return {
        activeTask,
        blockingCriterionId: criterion.Id,
        isDisabled: true,
        message: criterion.Message ?? criterion.Name,
        selector: selectorDefinition,
      }
    }
  }

  return {
    activeTask: null,
    blockingCriterionId: null,
    isDisabled: false,
    message: null,
    selector: firstSelector,
  }
}

export function projectProcessTaskSelector<TContext>(
  selector: ProcessTaskSelectorDefinition | null | undefined,
  context: TContext,
): ProcessTaskSelector | null {
  if (!selector) {
    return null
  }

  const metadata = projectProcessTaskSelectorMetadata(selector, context)
  if (!metadata) {
    return null
  }

  return {
    activeOnly: selector.ActiveOnly,
    metadata,
    processType: selector.ProcessType,
  }
}

export function projectProcessTaskStartRegistration({
  action,
  createHref,
  dataSourceQueryKey,
  context = {},
  detailsHref,
  failureMessage,
  invalidateQueryKeys,
  lifecycle,
  metadata,
  processTypeLabel,
  processTypeTone,
  result,
  selector,
  sourceHref,
  startedToast,
  targetHref,
  terminalInvalidateQueryKeys,
}: ProjectProcessTaskStartRegistrationOptions): ProcessTaskStartRegistration | null {
  const processId = readStringPath(result, 'ProcessId') ?? readStringPath(result, 'processId')
  if (!processId) {
    return null
  }

  const selectorMetadata = selector
    ? projectProcessTaskSelectorMetadata(selector, context)
    : undefined
  const processType = selector?.ProcessType ?? readStringPath(result, 'ProcessType') ?? 'process'
  const actionInvalidateQueryKeys = projectActionInvalidationQueryKeys({
    action,
    dataSourceQueryKey,
  })
  return {
    completedAtUtc: readStringPath(result, 'CompletedAtUtc'),
    detailsHref: detailsHref ?? projectActionResultHref({ action, createHref, result }),
    failureMessage,
    invalidateQueryKeys: invalidateQueryKeys ?? actionInvalidateQueryKeys,
    lifecycle: createProcessTaskLifecycle(lifecycle ?? {
      diagnosticCodes: [processTaskLifecycleDiagnosticCodes.optimisticStart],
      isActive: true,
      isFailure: false,
      isProgressing: true,
      isTerminal: false,
      tone: 'info',
    }),
    metadata: {
      ...selectorMetadata,
      ...readConventionalProcessTaskMetadata(result),
      ...metadata,
    },
    processId,
    processName: readStringPath(result, 'ProcessName'),
    processType,
    processTypeLabel: processTypeLabel ?? formatProcessTypeLabel(processType),
    processTypeTone,
    sourceHref,
    startedAtUtc: readStringPath(result, 'StartedAtUtc'),
    startedToast,
    status: readStringPath(result, 'Status'),
    targetHref,
    terminalInvalidateQueryKeys: terminalInvalidateQueryKeys ?? actionInvalidateQueryKeys,
    updatedAtUtc: readStringPath(result, 'UpdatedAtUtc'),
  }
}

function projectActionInvalidationQueryKeys({
  action,
  dataSourceQueryKey,
}: {
  readonly action?: ActionDefinition | null
  readonly dataSourceQueryKey?: (dataSourceId: string) => readonly unknown[]
}) {
  if (!dataSourceQueryKey) {
    return undefined
  }

  const dataSourceIds = action?.Result?.InvalidateDataSourceIds ?? []
  if (dataSourceIds.length === 0) {
    return undefined
  }

  return dataSourceIds.map((dataSourceId) => dataSourceQueryKey(dataSourceId))
}

function projectActionResultHref({
  action,
  createHref,
  result,
}: {
  readonly action?: ActionDefinition | null
  readonly createHref?: PresentationNavigationHrefFactory | null
  readonly result: unknown
}) {
  const routeId = action?.Result?.NavigateToRouteId
  if (!routeId || !createHref) {
    return undefined
  }

  return createHref(routeId, projectProcessResultRouteParameters(result))
}

function projectProcessResultRouteParameters(result: unknown): NavigationRouteParameters {
  const record = result && typeof result === 'object' && !Array.isArray(result)
    ? result as Readonly<Record<string, unknown>>
    : {}
  const parameters: NavigationRouteParameters = {}

  for (const [key, value] of Object.entries(record)) {
    if (!isNavigationRouteParameterValue(value)) {
      continue
    }

    parameters[key] = value
    parameters[lowerFirst(key)] = value
  }

  parameters.id ??=
    readStringPath(result, 'Id') ??
    readStringPath(result, 'EntityId') ??
    readStringPath(result, 'ProcessId') ??
    readStringPath(result, 'ShapeGraphId') ??
    readStringPath(result, 'EdiSpecId')

  return parameters
}

function projectProcessTaskSelectorMetadata<TContext>(
  selector: ProcessTaskSelectorDefinition,
  context: TContext,
): ProcessTaskMetadata | null {
  const metadata: Partial<Record<ProcessTaskScalarMetadataKey, string | null | undefined>> = {}
  for (const match of selector.Matches ?? []) {
    const metadataKey = readProcessTaskMetadataKey(match.TaskPath)
    if (!metadataKey) {
      return null
    }

    const value = readProcessTaskSelectorValue(context, match.ValuePath)
    if (value === undefined) {
      return null
    }

    metadata[metadataKey] = value
  }

  return metadata
}

function readConventionalProcessTaskMetadata(result: unknown): ProcessTaskMetadata {
  return {
    correlationId: readStringPath(result, 'CorrelationId'),
    ediSpecId: readStringPath(result, 'EdiSpecId'),
    mode: readStringPath(result, 'Mode'),
    modelId: readStringPath(result, 'ModelId'),
    policyId: readStringPath(result, 'PolicyId'),
    projectionIds: readStringArrayPath(result, 'ProjectionIds'),
    shapeGraphId: readStringPath(result, 'ShapeGraphId'),
  }
}

function readProcessTaskMetadataKey(taskPath: string): ProcessTaskScalarMetadataKey | null {
  const metadataPrefix = 'metadata.'
  if (!taskPath.startsWith(metadataPrefix)) {
    return null
  }

  const metadataKey = taskPath.slice(metadataPrefix.length)
  return scalarProcessTaskMetadataKeys.has(metadataKey as ProcessTaskScalarMetadataKey)
    ? (metadataKey as ProcessTaskScalarMetadataKey)
    : null
}

function readProcessTaskSelectorValue(
  context: unknown,
  valuePath: string,
) {
  const value = readStringPath(context, valuePath)
  if (value !== undefined) {
    return value
  }

  switch (valuePath) {
    case 'resource.Id':
    case 'resource.id':
    case 'route.Id':
    case 'route.id':
      return readStringPath(context, 'resourceId')
    default:
      return undefined
  }
}

function readStringPath(source: unknown, path: string) {
  const value = readPath(source, path)
  return typeof value === 'string' ? value : undefined
}

function readStringArrayPath(source: unknown, path: string) {
  const value = readPath(source, path)
  return Array.isArray(value) && value.every((item): item is string => typeof item === 'string')
    ? value
    : undefined
}

function isNavigationRouteParameterValue(
  value: unknown,
): value is boolean | number | string {
  return (
    typeof value === 'boolean' ||
    typeof value === 'number' ||
    typeof value === 'string'
  )
}

function lowerFirst(value: string) {
  return value.length === 0
    ? value
    : `${value.slice(0, 1).toLocaleLowerCase()}${value.slice(1)}`
}

function readPath(source: unknown, path: string): unknown {
  if (!path) {
    return undefined
  }

  return path.split('.').reduce<unknown>((current, segment) => {
    if (!current || typeof current !== 'object') {
      return undefined
    }

    const record = current as Record<string, unknown>
    if (Object.prototype.hasOwnProperty.call(record, segment)) {
      return record[segment]
    }

    const match = Object.keys(record).find(
      (key) => key.toLocaleLowerCase() === segment.toLocaleLowerCase(),
    )
    return match ? record[match] : undefined
  }, source)
}

function formatProcessTypeLabel(processType: string) {
  return processType
    .split(/[-_\s]+/)
    .filter(Boolean)
    .map((part, index) =>
      index === 0
        ? `${part.slice(0, 1).toLocaleUpperCase()}${part.slice(1)}`
        : part,
    )
    .join(' ')
}

const noActiveProcessTaskCriterionKind = createPresentationEnumDiscriminator(
  actionEnablementCriterionKinds,
  'noActiveProcessTask',
  'NoActiveProcessTask',
)

const scalarProcessTaskMetadataKeys = new Set<ProcessTaskScalarMetadataKey>([
  'correlationId',
  'ediSpecId',
  'mode',
  'modelId',
  'policyId',
  'shapeGraphId',
])
