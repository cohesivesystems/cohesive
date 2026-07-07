import {
  findPresentationDataSource,
  type PresentationBindingDefinition,
  type DataSourceDefinition,
  type PresentationModuleDefinition,
} from './module'
import {
  isTanStackQueryBinding,
  presentationDataSourceBindings,
  presentationDataSourceAuthorization,
  type PresentationLocalValueDataSourceBinding,
  type PresentationLocalValueDataSourceBindingInput,
  type PresentationDataSourceAuthorizationRequirement,
  type PresentationDataSourceBinding,
  type PresentationTanStackQueryDataSourceBindingInput,
  type PresentationTanStackQueryDataSourceBinding,
} from './presentation-data-source-binding-model'
import {
  createDataSourceEndpointQueryRequest,
  createDataSourcePaginationRequest,
  findDataSourceQueryEndpointBinding,
  type DataSourceQueryLoweringTransform,
} from './data-source-query-lowering'
import {
  aggregateOperators,
  cachePolicyKinds,
  dataSourceAggregateMaterializationKinds,
  dataSourceAggregatePredicateKinds,
  dataSourceKindLabels,
  dataSourceKinds,
  presentationBindingKinds,
  type AggregateOperator,
  type DataSourceAggregatePredicate,
  type DataSourceAggregateQuery,
  type DataSourceKind,
  type ParameterDefinition,
} from '@cohesive/presentation-contracts'
import {
  readPresentationQueryFormAppliedValue,
  type PresentationQueryFormStateMap,
} from './presentation-query-form-state'
import {
  readObjectPath,
  writeObjectPath,
} from './object-path'
import type {
  PresentationActionResultStateValues,
} from './presentation-action-result-state'

/**
 * Runtime inputs available while projecting semantic data-source definitions
 * into concrete frontend bindings.
 */
export interface PresentationDataSourceProjectionContext {
  /** Current query-form states keyed by query form or data-source state id. */
  readonly queryFormStates?: PresentationQueryFormStateMap

  /** Route parameters supplied by the active presentation/navigation host. */
  readonly routeParameters?: Readonly<Record<string, string | undefined>>

  /** Additional cache-key fragments supplied by the app or route host. */
  readonly queryKeyParts?: readonly unknown[]

  /** Workspace id used to scope local state or cache identity, when available. */
  readonly workspaceId?: string | null
}

/**
 * Context passed to a registry factory while interpreting one semantic
 * data-source definition.
 */
export interface PresentationDataSourceBindingFactoryContext {
  /** Backend target binding attached to the data source, when one is declared. */
  readonly binding: PresentationBindingDefinition | null

  /** Projection context shared by all data sources in the current host. */
  readonly context: PresentationDataSourceProjectionContext

  /** Data-source definition being projected. */
  readonly dataSource: DataSourceDefinition

  /** Presentation module that owns the data source and related query metadata. */
  readonly module: PresentationModuleDefinition | null
}

/**
 * Factory that projects one data source into a frontend binding.
 *
 * Returning `null` or `undefined` lets later registry fallbacks attempt the
 * projection.
 */
export type PresentationDataSourceBindingFactory = (
  context: PresentationDataSourceBindingFactoryContext,
) => PresentationDataSourceBinding | null | undefined

/**
 * Factory that produces TanStack Query binding input before target-level
 * authorization is applied.
 */
export type PresentationTanStackQueryDataSourceBindingFactory = (
  context: PresentationDataSourceBindingFactoryContext,
) => PresentationTargetInterpretedTanStackQueryDataSourceBindingInput | null | undefined

/**
 * Executes a backend endpoint id with the already-projected route/query
 * request payload.
 *
 * @typeParam TResult - Result shape returned by the endpoint executor.
 */
export type PresentationDataSourceEndpointExecutor = <TResult = unknown>(
  endpointId: string,
  request: PresentationDataSourceEndpointExecutionRequest,
) => Promise<TResult>

/**
 * Request envelope passed to endpoint executors from projected data-source
 * bindings.
 *
 * @typeParam TQuery - Query payload shape produced by query lowering.
 */
export interface PresentationDataSourceEndpointExecutionRequest<TQuery = unknown> {
  /** Lowered query payload for endpoint-query data sources. */
  readonly query?: TQuery | null

  /** Route parameters for endpoint-fetch data sources. */
  readonly routeParameters?: Readonly<Record<string, string | null | undefined>>
}

/**
 * Options read from backend target bindings that steer frontend endpoint and
 * authorization interpretation.
 */
export interface PresentationDataSourceTargetBindingOptions {
  /** Authorization policy id resolved by the target interpretation. */
  readonly authorizationPolicyId?: string | null

  /** Endpoint executor target id resolved by the API endpoint interpretation. */
  readonly executionTargetId?: string | null
}

/**
 * Runtime behavior defaults read from data-source annotations and applied to
 * query bindings.
 */
export interface PresentationDataSourceBindingRuntimeOptions {
  /** Message shown when a successful query returns no presentable items. */
  readonly emptyMessage?: string

  /** Data used before a query resolves or when a source cannot be materialized. */
  readonly fallbackData?: unknown

  /** Loading label shown while the binding is pending. */
  readonly pendingLabel?: string

  /** TanStack Query refetch interval in milliseconds, or `false` to disable it. */
  readonly refetchInterval?: number | false

  /** TanStack Query retry setting. */
  readonly retry?: boolean | number

  /** TanStack Query stale time in milliseconds. */
  readonly staleTime?: number
}

/**
 * Local-value binding input whose authorization can be supplied by the active
 * target interpretation instead of every feature-owned binding factory.
 */
export type PresentationTargetInterpretedLocalValueDataSourceBindingInput = Omit<
  PresentationLocalValueDataSourceBindingInput,
  'authorization'
> & {
  readonly authorization?: PresentationDataSourceAuthorizationRequirement
}

// TODO: extract
/**
 * TanStack Query binding input whose authorization can be supplied by the
 * target interpretation. Feature registries still own request/query shaping.
 */
export type PresentationTargetInterpretedTanStackQueryDataSourceBindingInput = Omit<
  PresentationTanStackQueryDataSourceBindingInput,
  'authorization'
> & {
  readonly authorization?: PresentationDataSourceAuthorizationRequirement
}

/** Resolves a data-source authorization requirement for a target binding. */
export type PresentationDataSourceAuthorizationFactory = (
  context: PresentationDataSourceBindingFactoryContext,
) => PresentationDataSourceAuthorizationRequirement

/** Endpoint executors keyed by backend-declared execution target id. */
export type PresentationDataSourceEndpointExecutorMap = Readonly<Record<
  string,
  PresentationDataSourceEndpointExecutor
>>

/** Authorization factories keyed by backend-declared authorization policy id. */
export type PresentationDataSourceAuthorizationPolicyMap = Readonly<Record<
  string,
  PresentationDataSourceAuthorizationFactory
>>

/** Target-specific interpretation shared by all data sources of one binding kind. */
export interface PresentationDataSourceBindingTargetInterpretation {
  /** Default authorization policy for this target binding kind. */
  readonly authorization?: PresentationDataSourceAuthorizationFactory
}

/**
 * Target interpretation for API endpoint data sources. This owns endpoint
 * execution and cross-cutting query defaults; semantic registries can still
 * provide per-source query payloads, labels, and cache keys.
 */
export interface PresentationDataSourceApiEndpointTargetInterpretation
  extends PresentationDataSourceBindingTargetInterpretation {
  /** Creates a query key for endpoint-backed data sources. */
  readonly createQueryKey?: (context: PresentationDataSourceApiEndpointFactoryContext) => readonly unknown[]

  /** Default executor for API endpoint data sources. */
  readonly executeEndpoint?: PresentationDataSourceEndpointExecutor

  /** Executors keyed by target binding `executionTargetId`. */
  readonly executeEndpointByTargetId?: PresentationDataSourceEndpointExecutorMap

  /** Enables or disables endpoint-backed query execution. */
  readonly isEnabled?: (context: PresentationDataSourceApiEndpointFactoryContext) => boolean

  /** Default pending label for endpoint-backed query bindings. */
  readonly pendingLabel?:
    | string
    | ((context: PresentationDataSourceApiEndpointFactoryContext) => string)

  /** Default retry behavior for endpoint-backed query bindings. */
  readonly retry?: boolean
}

/** Target interpretation for backend-declared query lowering transform ids. */
export interface PresentationDataSourceQueryLoweringTargetInterpretation {
  /** Query-lowering transforms keyed by backend-declared transform id. */
  readonly transformsById?: Readonly<Record<string, DataSourceQueryLoweringTransform>>
}

/**
 * Frontend interpretation of backend data-source target bindings for one
 * concrete presentation target.
 */
export interface PresentationDataSourceTargetInterpretation {
  /** Interpretation for API endpoint data-source bindings. */
  readonly apiEndpoint?: PresentationDataSourceApiEndpointTargetInterpretation

  /** Named authorization policies available to target bindings. */
  readonly authorizationPoliciesById?: PresentationDataSourceAuthorizationPolicyMap

  /** Fallback authorization policy for bindings without a narrower policy. */
  readonly defaultAuthorization?: PresentationDataSourceAuthorizationFactory

  /** Interpretation for frontend-owned local state data-source bindings. */
  readonly localState?: PresentationDataSourceBindingTargetInterpretation

  /** Interpretation for backend-declared query-lowering transforms. */
  readonly queryLowering?: PresentationDataSourceQueryLoweringTargetInterpretation
}

/** Options used when invoking a projected endpoint through the target. */
export interface ExecutePresentationDataSourceTargetEndpointOptions {
  /** Binding whose target options select endpoint executor and authorization policy. */
  readonly binding?: PresentationBindingDefinition | null
}

/**
 * Registry of data-source binding factories used to project semantic data
 * sources onto frontend runtime bindings.
 */
export interface PresentationDataSourceBindingProjectionRegistry {
  /** Generic factory for API endpoint bindings. */
  readonly apiEndpoint?: PresentationDataSourceBindingFactory

  /** Exact factories keyed by data-source id. */
  readonly byDataSourceId?: Readonly<Record<string, PresentationDataSourceBindingFactory>>

  /** Exact factories keyed by endpoint id. */
  readonly byEndpointId?: Readonly<Record<string, PresentationDataSourceBindingFactory>>

  /** Default authorization policy used by unbound or projected bindings. */
  readonly defaultAuthorization?: (
    context: PresentationDataSourceBindingFactoryContext,
  ) => PresentationDataSourceAuthorizationRequirement

  /** Last-resort factory when no exact or generic API factory matches. */
  readonly fallback?: PresentationDataSourceBindingFactory
}

/**
 * Options for adapting target-interpreted TanStack Query binding factories into
 * a full data-source projection registry.
 */
export interface TanStackQueryDataSourceBindingProjectionRegistryOptions {
  /** Generic endpoint factory returning TanStack Query binding input. */
  readonly apiEndpoint?: PresentationTanStackQueryDataSourceBindingFactory

  /** Exact factories keyed by data-source id. */
  readonly byDataSourceId?: Readonly<Record<string, PresentationDataSourceBindingFactory>>

  /** Exact endpoint factories keyed by endpoint id. */
  readonly byEndpointId?: Readonly<Record<string, PresentationTanStackQueryDataSourceBindingFactory>>

  /** Authorization policy applied when a factory does not provide one. */
  readonly defaultAuthorization?: (
    context: PresentationDataSourceBindingFactoryContext,
  ) => PresentationDataSourceAuthorizationRequirement

  /** Active target interpretation that supplies authorization and execution defaults. */
  readonly targetInterpretation?: PresentationDataSourceTargetInterpretation
}

/**
 * Options for the generic API endpoint TanStack Query projector.
 */
export interface ApiEndpointTanStackQueryDataSourceBindingProjectionRegistryOptions {
  /** Optional target-specific cache key builder. */
  readonly createQueryKey?: (
    context: PresentationDataSourceApiEndpointFactoryContext,
  ) => readonly unknown[]

  /** Optional endpoint executor overriding the active target interpretation. */
  readonly executeEndpoint?: PresentationDataSourceEndpointExecutor

  /** Optional query enablement guard. */
  readonly isEnabled?: (context: PresentationDataSourceApiEndpointFactoryContext) => boolean

  /** Pending label or label factory for generated query bindings. */
  readonly pendingLabel?: string | ((context: PresentationDataSourceApiEndpointFactoryContext) => string)

  /** Retry behavior for generated query bindings. */
  readonly retry?: boolean

  /** Default authorization policy applied to generated query bindings. */
  readonly defaultAuthorization?: (
    context: PresentationDataSourceBindingFactoryContext,
  ) => PresentationDataSourceAuthorizationRequirement

  /** Active target interpretation that supplies defaults not provided here. */
  readonly targetInterpretation?: PresentationDataSourceTargetInterpretation
}

/**
 * Factory context for API endpoint data-source bindings after endpoint id and
 * route parameters have been resolved.
 */
export interface PresentationDataSourceApiEndpointFactoryContext
  extends PresentationDataSourceBindingFactoryContext {
  /** Resolved API endpoint id. */
  readonly endpointId: string

  /** Route parameters projected for this data source. */
  readonly routeParameters: Readonly<Record<string, string | null | undefined>>
}

/** Options for projecting a list of semantic data-source ids. */
export interface ProjectPresentationDataSourceBindingsOptions {
  /** Shared runtime projection context. */
  readonly context?: PresentationDataSourceProjectionContext

  /** Fallback data-source definitions when the module does not contain an id. */
  readonly dataSourceDefinitions?: readonly DataSourceDefinition[]

  /** Data-source ids to project, de-duplicated before projection. */
  readonly dataSourceIds: readonly string[]

  /** Presentation module that owns data-source definitions and target bindings. */
  readonly module: PresentationModuleDefinition | null

  /** Projection registry used to resolve concrete frontend bindings. */
  readonly registry: PresentationDataSourceBindingProjectionRegistry
}

/**
 * Options for creating endpoint-query binding input from backend query
 * lowering.
 *
 * @typeParam TRequest - Lowered endpoint query request shape.
 */
export interface CreateDataSourceEndpointQueryBindingInputOptions<
  TRequest extends object,
> {
  /** Factory context for the data source being projected. */
  readonly context: PresentationDataSourceBindingFactoryContext

  /** Base request values merged into the lowered query request. */
  readonly defaultRequest?: Partial<TRequest>

  /** Empty-state message for the generated query binding. */
  readonly emptyMessage?: string

  /** Query enablement override. */
  readonly enabled?: boolean

  /** Endpoint id to execute. */
  readonly endpointId: string

  /** Fallback data for the generated query binding. */
  readonly fallbackData?: unknown

  /** Pagination request merged into the lowered query request. */
  readonly paginationRequest?: object | null

  /** Pending label for the generated query binding. */
  readonly pendingLabel?: string

  /** Additional query-key fragments. */
  readonly queryKeyParts?: readonly unknown[]

  /** Refetch interval in milliseconds, or `false` to disable it. */
  readonly refetchInterval?: number | false

  /** Retry behavior for the generated query binding. */
  readonly retry?: boolean | number

  /** Stale time in milliseconds. */
  readonly staleTime?: number

  /** Target interpretation used for endpoint execution and authorization. */
  readonly targetInterpretation: PresentationDataSourceTargetInterpretation

  /** Per-source query lowering transforms. */
  readonly transforms?: Readonly<Record<string, DataSourceQueryLoweringTransform>>

  /** Source value used by query lowering. */
  readonly value?: unknown
}

/** Options for creating endpoint-fetch binding input from a data source. */
export interface CreateDataSourceEndpointFetchBindingInputOptions {
  /** Factory context for the data source being projected. */
  readonly context: PresentationDataSourceBindingFactoryContext

  /** Empty-state message for the generated query binding. */
  readonly emptyMessage?: string

  /** Query enablement override. */
  readonly enabled?: boolean

  /** Endpoint id to execute, defaulting to the data-source binding endpoint. */
  readonly endpointId?: string

  /** Fallback data for the generated query binding. */
  readonly fallbackData?: unknown

  /** Pending label for the generated query binding. */
  readonly pendingLabel?: string

  /** Additional query-key fragments. */
  readonly queryKeyParts?: readonly unknown[]

  /** Refetch interval in milliseconds, or `false` to disable it. */
  readonly refetchInterval?: number | false

  /** Retry behavior for the generated query binding. */
  readonly retry?: boolean | number

  /** Route parameters to pass to the endpoint executor. */
  readonly routeParameters?: Readonly<Record<string, string | null | undefined>>

  /** Stale time in milliseconds. */
  readonly staleTime?: number

  /** Target interpretation used for endpoint execution and authorization. */
  readonly targetInterpretation: PresentationDataSourceTargetInterpretation
}

/** Options for projecting an aggregate-query data source. */
export interface CreateDataSourceAggregateQueryBindingOptions {
  /** Factory context for the aggregate data source being projected. */
  readonly context: PresentationDataSourceBindingFactoryContext

  /** Additional query-key fragments. */
  readonly queryKeyParts?: readonly unknown[]

  /** Target interpretation used to materialize source data. */
  readonly targetInterpretation: PresentationDataSourceTargetInterpretation

  /** Per-source query lowering transforms used to query the source data source. */
  readonly transforms?: Readonly<Record<string, DataSourceQueryLoweringTransform>>

  /** Source value used by query lowering. */
  readonly value?: unknown
}

/** Options for creating a collection-query projection registry. */
export interface CreateCollectionQueryDataSourceBindingProjectionRegistryOptions {
  /** Pagination requests keyed by data-source id. */
  readonly paginationRequestsByDataSourceId?: Readonly<Record<string, object | null | undefined>>

  /** Static or computed query-key fragments. */
  readonly queryKeyParts?:
    | readonly unknown[]
    | ((context: PresentationDataSourceBindingFactoryContext) => readonly unknown[])

  /** Optional local-value override for collection data sources. */
  readonly resolveLocalValue?: (
    context: PresentationDataSourceBindingFactoryContext,
  ) => PresentationTargetInterpretedLocalValueDataSourceBindingInput | null | undefined

  /** Optional per-source query-lowering transform resolver. */
  readonly resolveTransforms?: (
    context: PresentationDataSourceBindingFactoryContext,
  ) => Readonly<Record<string, DataSourceQueryLoweringTransform>> | undefined

  /** Optional source value resolver for query lowering. */
  readonly resolveValue?: (
    context: PresentationDataSourceBindingFactoryContext,
  ) => unknown

  /** Target interpretation used for generated bindings. */
  readonly targetInterpretation: PresentationDataSourceTargetInterpretation
}

/** Options for creating an aggregate-query projection registry. */
export interface CreateAggregateQueryDataSourceBindingProjectionRegistryOptions {
  /** Static or computed query-key fragments. */
  readonly queryKeyParts?:
    | readonly unknown[]
    | ((context: PresentationDataSourceBindingFactoryContext) => readonly unknown[])

  /** Optional per-source query-lowering transform resolver. */
  readonly resolveTransforms?: (
    context: PresentationDataSourceBindingFactoryContext,
  ) => Readonly<Record<string, DataSourceQueryLoweringTransform>> | undefined

  /** Optional source value resolver for query lowering. */
  readonly resolveValue?: (
    context: PresentationDataSourceBindingFactoryContext,
  ) => unknown

  /** Target interpretation used for generated bindings. */
  readonly targetInterpretation: PresentationDataSourceTargetInterpretation
}

/** Options for creating endpoint-fetch projection registries. */
export interface CreateApiEndpointFetchDataSourceBindingProjectionRegistryOptions {
  /** Static or computed query-key fragments. */
  readonly queryKeyParts?:
    | readonly unknown[]
    | ((context: PresentationDataSourceBindingFactoryContext) => readonly unknown[])

  /** Target interpretation used for generated bindings. */
  readonly targetInterpretation: PresentationDataSourceTargetInterpretation
}

/** Options for creating local-state projection registries. */
export interface CreateLocalStateDataSourceBindingProjectionRegistryOptions {
  /** Resolves a local-value binding input for a local-state data source. */
  readonly resolveLocalValue: (
    context: PresentationDataSourceBindingFactoryContext,
  ) => PresentationTargetInterpretedLocalValueDataSourceBindingInput | null | undefined

  /** Target interpretation used for authorization defaults. */
  readonly targetInterpretation: PresentationDataSourceTargetInterpretation
}

/** Options for projecting action-result state values as data-source bindings. */
export interface CreateActionResultStateDataSourceBindingProjectionRegistryOptions {
  /** Route-scoped data-source values written by action result policies. */
  readonly values?: PresentationActionResultStateValues

  /** Target interpretation used for authorization defaults. */
  readonly targetInterpretation: PresentationDataSourceTargetInterpretation
}

/** Static or factory-produced query-key fragments used by binding projectors. */
export type DataSourceQueryKeyParts =
  | readonly unknown[]
  | ((context: PresentationDataSourceBindingFactoryContext) => readonly unknown[])

/**
 * Per-data-source interpretation hooks used by collection and aggregate query
 * projectors.
 */
export interface DataSourceQueryInterpretation {
  /** Optional local-value override for the data source. */
  readonly localValue?: (context: PresentationDataSourceBindingFactoryContext) => PresentationTargetInterpretedLocalValueDataSourceBindingInput | null | undefined

  /** Normalizes a source value before query lowering. */
  readonly normalizeValue?: (value: unknown, context: PresentationDataSourceBindingFactoryContext) => unknown

  /** Query-key fragments for this data source. */
  readonly queryKeyParts?: DataSourceQueryKeyParts

  /** Reads the source value used by query lowering. */
  readonly readValue?: (context: PresentationDataSourceBindingFactoryContext) => unknown

  /** Per-data-source query lowering transforms or transform resolver. */
  readonly transforms?:
    | Readonly<Record<string, DataSourceQueryLoweringTransform>>
    | ((context: PresentationDataSourceBindingFactoryContext) => Readonly<Record<string, DataSourceQueryLoweringTransform>> | undefined)
}

/** Registry of query interpretation hooks keyed by data-source id. */
export interface DataSourceQueryInterpretationRegistry {
  /** Per-source interpretation hooks keyed by data-source id. */
  readonly byDataSourceId?: Readonly<Record<string, DataSourceQueryInterpretation>>

  /** Default query-key fragments when a source has no override. */
  readonly defaultQueryKeyParts?: DataSourceQueryKeyParts

  /** Shared query lowering transforms or transform resolver. */
  readonly transforms?:
    | Readonly<Record<string, DataSourceQueryLoweringTransform>>
    | ((context: PresentationDataSourceBindingFactoryContext) => Readonly<Record<string, DataSourceQueryLoweringTransform>> | undefined)
}

/**
 * Resolvers adapted from a query interpretation registry to the option shape
 * consumed by collection and aggregate binding registry factories.
 */
export type DataSourceQueryInterpretationResolvers = Pick<
  CreateCollectionQueryDataSourceBindingProjectionRegistryOptions,
  'queryKeyParts' | 'resolveLocalValue' | 'resolveTransforms' | 'resolveValue'
> &
  Pick<
    CreateAggregateQueryDataSourceBindingProjectionRegistryOptions,
    'queryKeyParts' | 'resolveTransforms' | 'resolveValue'
  >

/** Preserves target interpretation objects as a named projection concept. */
export function createPresentationDataSourceTargetInterpretation(
  interpretation: PresentationDataSourceTargetInterpretation,
): PresentationDataSourceTargetInterpretation {
  return interpretation
}

/**
 * Resolves authorization from the target interpretation based on the backend
 * binding kind declared for the data source.
 */
export function resolvePresentationDataSourceTargetAuthorization(
  targetInterpretation: PresentationDataSourceTargetInterpretation | undefined,
  context: PresentationDataSourceBindingFactoryContext,
): PresentationDataSourceAuthorizationRequirement {
  const options = readPresentationDataSourceTargetBindingOptions(context.binding)
  if (options.authorizationPolicyId) {
    return (
      targetInterpretation?.authorizationPoliciesById?.[options.authorizationPolicyId]?.(context) ??
      presentationDataSourceAuthorization.required({
        blockedLabel: `No frontend authorization policy is registered for '${options.authorizationPolicyId}'.`,
        isAuthorized: false,
      })
    )
  }

  if (isApiEndpointBinding(context.binding)) {
    return (
      targetInterpretation?.apiEndpoint?.authorization?.(context) ??
      targetInterpretation?.defaultAuthorization?.(context) ??
      presentationDataSourceAuthorization.none()
    )
  }

  if (isLocalStateBinding(context.binding)) {
    return (
      targetInterpretation?.localState?.authorization?.(context) ??
      targetInterpretation?.defaultAuthorization?.(context) ??
      presentationDataSourceAuthorization.none()
    )
  }

  return (
    targetInterpretation?.defaultAuthorization?.(context) ??
    presentationDataSourceAuthorization.none()
  )
}

/** Creates a local-value binding with authorization supplied by the target. */
export function createTargetInterpretedLocalValueDataSourceBinding(
  context: PresentationDataSourceBindingFactoryContext,
  targetInterpretation: PresentationDataSourceTargetInterpretation | undefined,
  binding: PresentationTargetInterpretedLocalValueDataSourceBindingInput,
): PresentationLocalValueDataSourceBinding {
  const { authorization, ...bindingWithoutAuthorization } = binding
  return presentationDataSourceBindings.localValue({
    ...bindingWithoutAuthorization,
    authorization:
      authorization ??
      resolvePresentationDataSourceTargetAuthorization(targetInterpretation, context),
  })
}

/** Creates a TanStack Query binding with authorization supplied by the target. */
export function createTargetInterpretedTanStackQueryDataSourceBinding(
  context: PresentationDataSourceBindingFactoryContext,
  targetInterpretation: PresentationDataSourceTargetInterpretation | undefined,
  binding: PresentationTargetInterpretedTanStackQueryDataSourceBindingInput,
  defaultAuthorization?: PresentationDataSourceAuthorizationFactory,
): PresentationTanStackQueryDataSourceBinding {
  const { authorization, ...bindingWithoutAuthorization } = binding
  return presentationDataSourceBindings.tanstackQuery({
    ...bindingWithoutAuthorization,
    authorization:
      authorization ??
      defaultAuthorization?.(context) ??
      resolvePresentationDataSourceTargetAuthorization(targetInterpretation, context),
  })
}

/** Creates endpoint-query binding input from backend-declared query lowering. */
export function createDataSourceEndpointQueryBindingInput<
  TRequest extends object = Record<string, unknown>,
  TResult = unknown,
>({
  context,
  defaultRequest,
  emptyMessage,
  enabled,
  endpointId,
  fallbackData,
  paginationRequest,
  pendingLabel,
  queryKeyParts = [],
  refetchInterval,
  retry,
  staleTime,
  targetInterpretation,
  transforms,
  value,
}: CreateDataSourceEndpointQueryBindingInputOptions<TRequest>): PresentationTargetInterpretedTanStackQueryDataSourceBindingInput {
  const loweredTransforms = mergeDataSourceQueryLoweringTransforms(
    targetInterpretation.queryLowering?.transformsById,
    transforms,
  )
  const query = createDataSourceEndpointQueryRequest<TRequest>({
    dataSource: context.dataSource,
    defaultRequest,
    endpointId,
    paginationRequest,
    transforms: loweredTransforms,
    value,
  })
  const runtimeOptions = readDataSourceBindingRuntimeOptions(context.dataSource)

  return {
    dataSourceId: context.dataSource.Id,
    emptyMessage: emptyMessage ?? runtimeOptions.emptyMessage,
    enabled,
    fallbackData: fallbackData === undefined ? runtimeOptions.fallbackData : fallbackData,
    pendingLabel: pendingLabel ?? runtimeOptions.pendingLabel,
    queryFn: () =>
      executePresentationDataSourceTargetEndpoint<TResult>(
        targetInterpretation,
        endpointId,
        { query },
        { binding: context.binding },
      ),
    queryKey: [
      'presentation-data-source-endpoint-query',
      context.dataSource.Id,
      endpointId,
      query,
      ...queryKeyParts,
    ],
    refetchInterval: refetchInterval ?? runtimeOptions.refetchInterval,
    retry: retry ?? runtimeOptions.retry,
    staleTime: staleTime ?? runtimeOptions.staleTime,
  }
}

/** Creates a fully wrapped endpoint-query binding from backend query lowering. */
export function createDataSourceEndpointQueryBinding<
  TRequest extends object = Record<string, unknown>,
  TResult = unknown,
>(
  options: CreateDataSourceEndpointQueryBindingInputOptions<TRequest>,
): PresentationTanStackQueryDataSourceBinding {
  return createTargetInterpretedTanStackQueryDataSourceBinding(
    options.context,
    options.targetInterpretation,
    createDataSourceEndpointQueryBindingInput<TRequest, TResult>(options),
  )
}

/** Creates a default binding registry for backend-declared collection-query data sources. */
export function createCollectionQueryDataSourceBindingProjectionRegistry({
  paginationRequestsByDataSourceId = {},
  queryKeyParts = [],
  resolveLocalValue,
  resolveTransforms,
  resolveValue,
  targetInterpretation,
}: CreateCollectionQueryDataSourceBindingProjectionRegistryOptions): PresentationDataSourceBindingProjectionRegistry {
  return {
    apiEndpoint: (context) => {
      if (
        !isCollectionQueryDataSource(context.dataSource) ||
        !context.dataSource.Query
      ) {
        return null
      }

      const localValue = resolveLocalValue?.(context)
      if (localValue) {
        return createTargetInterpretedLocalValueDataSourceBinding(
          context,
          targetInterpretation,
          localValue,
        )
      }

      const endpointId = readPresentationDataSourceEndpointId(context.binding)
      if (!endpointId) {
        return null
      }

      return createDataSourceEndpointQueryBinding({
        context,
        endpointId,
        paginationRequest: paginationRequestsByDataSourceId[context.dataSource.Id],
        queryKeyParts: resolveQueryKeyParts(queryKeyParts, context),
        targetInterpretation,
        transforms: resolveTransforms?.(context),
        value:
          resolveValue?.(context) ??
          readPresentationQueryFormAppliedValue(context.context, context.dataSource.Id),
      })
    },
  }
}

/** Creates a default binding registry for backend-declared aggregate-query data sources. */
export function createAggregateQueryDataSourceBindingProjectionRegistry({
  queryKeyParts = [],
  resolveTransforms,
  resolveValue,
  targetInterpretation,
}: CreateAggregateQueryDataSourceBindingProjectionRegistryOptions): PresentationDataSourceBindingProjectionRegistry {
  return {
    fallback: (context) => {
      if (
        !isAggregateQueryDataSource(context.dataSource) ||
        !context.dataSource.Aggregation
      ) {
        return null
      }

      return createDataSourceAggregateQueryBinding({
        context,
        queryKeyParts: resolveQueryKeyParts(queryKeyParts, context),
        targetInterpretation,
        transforms: resolveTransforms?.(context),
        value: resolveValue?.(context),
      })
    },
  }
}

/** Creates a default fetch binding registry for endpoint-backed data sources. */
export function createApiEndpointFetchDataSourceBindingProjectionRegistry({
  queryKeyParts = [],
  targetInterpretation,
}: CreateApiEndpointFetchDataSourceBindingProjectionRegistryOptions): PresentationDataSourceBindingProjectionRegistry {
  return {
    apiEndpoint: (context) => {
      if (context.dataSource.Query || isPromptPreviewDataSource(context.dataSource)) {
        return null
      }

      const endpointId = readPresentationDataSourceEndpointId(context.binding)
      if (!endpointId) {
        return null
      }

      return createDataSourceEndpointFetchBinding({
        context,
        endpointId,
        queryKeyParts: resolveQueryKeyParts(queryKeyParts, context),
        targetInterpretation,
      })
    },
  }
}

/** Creates a default binding registry for frontend-owned local-state data sources. */
export function createLocalStateDataSourceBindingProjectionRegistry({
  resolveLocalValue,
  targetInterpretation,
}: CreateLocalStateDataSourceBindingProjectionRegistryOptions): PresentationDataSourceBindingProjectionRegistry {
  return {
    fallback: (context) => {
      if (!isLocalStateDataSource(context.dataSource)) {
        return null
      }

      const localValue = resolveLocalValue(context)
      return localValue
        ? createTargetInterpretedLocalValueDataSourceBinding(
            context,
            targetInterpretation,
            localValue,
          )
        : null
    },
  }
}

/**
 * Creates a binding registry that exposes action-result state writes as local
 * values for their target data sources.
 */
export function createActionResultStateDataSourceBindingProjectionRegistry({
  targetInterpretation,
  values = {},
}: CreateActionResultStateDataSourceBindingProjectionRegistryOptions): PresentationDataSourceBindingProjectionRegistry {
  return {
    fallback: (context) => {
      if (!hasOwn(values, context.dataSource.Id)) {
        return null
      }

      return createTargetInterpretedLocalValueDataSourceBinding(
        context,
        targetInterpretation,
        {
          data: values[context.dataSource.Id],
          dataSourceId: context.dataSource.Id,
        },
      )
    },
  }
}

/** Adapts data-source query interpretation metadata to collection/aggregate projector hooks. */
export function createDataSourceQueryInterpretationResolvers({
  byDataSourceId = {},
  defaultQueryKeyParts = [],
  transforms,
}: DataSourceQueryInterpretationRegistry): DataSourceQueryInterpretationResolvers {
  return {
    queryKeyParts: (context) =>
      resolveQueryKeyParts(
        byDataSourceId[context.dataSource.Id]?.queryKeyParts ?? defaultQueryKeyParts,
        context,
      ),
    resolveLocalValue: (context) =>
      byDataSourceId[context.dataSource.Id]?.localValue?.(context),
    resolveTransforms: (context) =>
      resolveDataSourceQueryInterpretationTransforms(
        byDataSourceId[context.dataSource.Id],
        transforms,
        context,
      ),
    resolveValue: (context) =>
      resolveDataSourceQueryInterpretationValue(
        byDataSourceId[context.dataSource.Id],
        context,
      ),
  }
}

/** Creates endpoint-fetch binding input from an API endpoint data source. */
export function createDataSourceEndpointFetchBindingInput<TResult = unknown>({
  context,
  emptyMessage,
  enabled,
  endpointId,
  fallbackData,
  pendingLabel,
  queryKeyParts = [],
  refetchInterval,
  retry,
  routeParameters,
  staleTime,
  targetInterpretation,
}: CreateDataSourceEndpointFetchBindingInputOptions): PresentationTargetInterpretedTanStackQueryDataSourceBindingInput {
  const runtimeOptions = readDataSourceBindingRuntimeOptions(context.dataSource)
  const resolvedEndpointId = endpointId ?? readPresentationDataSourceEndpointId(context.binding)
  const resolvedRouteParameters =
    routeParameters ??
    projectDataSourceRouteParameters(
      context.dataSource.Parameters,
      context.context.routeParameters,
    )
  const hasRequiredRouteParameters = hasRequiredDataSourceRouteParameters(
    context.dataSource.Parameters,
    resolvedRouteParameters,
  )

  return {
    dataSourceId: context.dataSource.Id,
    emptyMessage: emptyMessage ?? runtimeOptions.emptyMessage,
    enabled: Boolean(resolvedEndpointId) && (enabled ?? true) && hasRequiredRouteParameters,
    fallbackData: fallbackData === undefined ? runtimeOptions.fallbackData : fallbackData,
    pendingLabel: pendingLabel ?? runtimeOptions.pendingLabel,
    queryFn: () =>
      resolvedEndpointId
        ? executePresentationDataSourceTargetEndpoint<TResult>(
            targetInterpretation,
            resolvedEndpointId,
            { routeParameters: resolvedRouteParameters },
            { binding: context.binding },
          )
        : Promise.reject(
            new Error(`Data source '${context.dataSource.Id}' is not bound to an API endpoint.`),
          ),
    queryKey: [
      'presentation-data-source-endpoint-fetch',
      context.dataSource.Id,
      resolvedEndpointId,
      resolvedRouteParameters,
      ...queryKeyParts,
    ],
    refetchInterval: refetchInterval ?? runtimeOptions.refetchInterval,
    retry: retry ?? runtimeOptions.retry,
    staleTime: staleTime ?? runtimeOptions.staleTime,
  }
}

/** Creates a fully wrapped endpoint-fetch binding from an API endpoint data source. */
export function createDataSourceEndpointFetchBinding<TResult = unknown>(
  options: CreateDataSourceEndpointFetchBindingInputOptions,
): PresentationTanStackQueryDataSourceBinding {
  return createTargetInterpretedTanStackQueryDataSourceBinding(
    options.context,
    options.targetInterpretation,
    createDataSourceEndpointFetchBindingInput<TResult>(options),
  )
}

/** Creates a binding that materializes a source data source and evaluates aggregate measures. */
export function createDataSourceAggregateQueryBinding({
  context,
  queryKeyParts = [],
  targetInterpretation,
  transforms,
  value,
}: CreateDataSourceAggregateQueryBindingOptions): PresentationTanStackQueryDataSourceBinding | null {
  const aggregation = context.dataSource.Aggregation
  if (!aggregation) {
    return null
  }

  const sourceDataSource = findPresentationDataSource(
    context.module,
    aggregation.SourceDataSourceId,
  )
  const sourceEndpointId = readPresentationDataSourceEndpointId(sourceDataSource?.Binding)
  if (!sourceDataSource || !sourceEndpointId) {
    return createTargetInterpretedTanStackQueryDataSourceBinding(
      context,
      targetInterpretation,
      {
        authorization: presentationDataSourceAuthorization.required({
          blockedLabel: `Aggregate data source '${context.dataSource.Id}' cannot resolve source '${aggregation.SourceDataSourceId}'.`,
          isAuthorized: false,
        }),
        dataSourceId: context.dataSource.Id,
        queryFn: () => Promise.resolve(evaluateDataSourceAggregateQuery(aggregation, [])),
        queryKey: [
          'presentation-data-source-aggregate-query',
          context.dataSource.Id,
          aggregation.SourceDataSourceId,
          'missing-source',
          ...queryKeyParts,
        ],
      },
    )
  }

  const appliedValue =
    value ?? readPresentationQueryFormAppliedValue(context.context, context.dataSource.Id)
  const runtimeOptions = readDataSourceBindingRuntimeOptions(context.dataSource)
  const fallbackData =
    runtimeOptions.fallbackData === undefined
      ? evaluateDataSourceAggregateQuery(aggregation, [])
      : runtimeOptions.fallbackData
  const sourceAuthorization = resolvePresentationDataSourceTargetAuthorization(
    targetInterpretation,
    {
      ...context,
      binding: sourceDataSource.Binding ?? null,
      dataSource: sourceDataSource,
    },
  )
  const loweredTransforms = mergeDataSourceQueryLoweringTransforms(
    targetInterpretation.queryLowering?.transformsById,
    transforms,
  )

  return createTargetInterpretedTanStackQueryDataSourceBinding(
    context,
    targetInterpretation,
    {
      authorization: sourceAuthorization,
      dataSourceId: context.dataSource.Id,
      fallbackData,
      pendingLabel: runtimeOptions.pendingLabel,
      queryFn: async () => {
        const items = await materializeDataSourceAggregateItems({
          aggregation,
          sourceDataSource,
          sourceEndpointId,
          targetInterpretation,
          transforms: loweredTransforms,
          value: appliedValue,
        })
        return evaluateDataSourceAggregateQuery(aggregation, items)
      },
      queryKey: [
        'presentation-data-source-aggregate-query',
        context.dataSource.Id,
        sourceDataSource.Id,
        appliedValue ?? {},
        aggregation,
        ...queryKeyParts,
      ],
      refetchInterval: runtimeOptions.refetchInterval,
      retry: runtimeOptions.retry,
      staleTime: runtimeOptions.staleTime,
    },
  )
}

/** Executes an API endpoint through the active target interpretation. */
export function executePresentationDataSourceTargetEndpoint<TResult = unknown>(
  targetInterpretation: PresentationDataSourceTargetInterpretation,
  endpointId: string,
  request: PresentationDataSourceEndpointExecutionRequest = {},
  options: ExecutePresentationDataSourceTargetEndpointOptions = {},
): Promise<TResult> {
  const executeEndpoint = resolvePresentationDataSourceTargetEndpointExecutor(
    targetInterpretation,
    options.binding,
  )
  if (!executeEndpoint) {
    const executionTargetId = readPresentationDataSourceTargetBindingOptions(
      options.binding,
    ).executionTargetId
    return Promise.reject(
      new Error(
        executionTargetId
          ? `No API endpoint executor is registered for presentation data-source execution target '${executionTargetId}'.`
          : `No API endpoint executor is registered for presentation data-source endpoint '${endpointId}'.`,
      ),
    )
  }

  return executeEndpoint<TResult>(endpointId, request)
}

/**
 * Composes independently owned data-source binding registries into one
 * projection registry. Later registries override earlier factories for the same
 * data-source or endpoint id so app-level composition can replace defaults at
 * narrower ownership boundaries.
 */
export function mergePresentationDataSourceBindingProjectionRegistries(
  ...registries: readonly PresentationDataSourceBindingProjectionRegistry[]
): PresentationDataSourceBindingProjectionRegistry {
  let defaultAuthorization: PresentationDataSourceBindingProjectionRegistry['defaultAuthorization']
  for (const registry of registries) {
    defaultAuthorization = registry.defaultAuthorization ?? defaultAuthorization
  }

  return {
    apiEndpoint: resolveApiEndpointFactory(registries),
    byDataSourceId: Object.assign(
      {},
      ...registries.map((registry) => registry.byDataSourceId ?? {}),
    ),
    byEndpointId: Object.assign(
      {},
      ...registries.map((registry) => registry.byEndpointId ?? {}),
    ),
    defaultAuthorization,
    fallback: resolveFallbackFactory(registries),
  }
}

/**
 * Projects semantic data-source definitions onto concrete frontend binding
 * plans. React hooks are deliberately not used here; hook-backed realization is
 * handled later by PresentationDataSourceBinder.
 */
export function projectPresentationDataSourceBindings({
  context = {},
  dataSourceDefinitions = [],
  dataSourceIds,
  module,
  registry,
}: ProjectPresentationDataSourceBindingsOptions): readonly PresentationDataSourceBinding[] {
  const uniqueDataSourceIds = Array.from(new Set(dataSourceIds))
  const fallbackDataSourcesById = new Map(
    dataSourceDefinitions.map((dataSource) => [dataSource.Id, dataSource] as const),
  )

  return uniqueDataSourceIds.flatMap((dataSourceId) => {
    const dataSource =
      findPresentationDataSource(module, dataSourceId) ??
      fallbackDataSourcesById.get(dataSourceId) ??
      null
    if (!dataSource) {
      return []
    }

    const binding = dataSource.Binding ?? null
    const factoryContext = {
      binding,
      context,
      dataSource,
      module,
    } satisfies PresentationDataSourceBindingFactoryContext
    const factory = resolveDataSourceBindingFactory(registry, dataSource)
    const projectedBinding =
      factory?.(factoryContext) ??
      createUnboundDataSourceBinding(dataSource, registry.defaultAuthorization?.(factoryContext))

    return [applyDataSourceDefaults(projectedBinding, dataSource)]
  })
}

/**
 * Adapts TanStack Query binding-input factories into a full projection
 * registry by applying target-level authorization to every produced binding.
 */
export function createTanStackQueryDataSourceBindingProjectionRegistry({
  apiEndpoint,
  byDataSourceId,
  byEndpointId,
  defaultAuthorization,
  targetInterpretation,
}: TanStackQueryDataSourceBindingProjectionRegistryOptions): PresentationDataSourceBindingProjectionRegistry {
  return {
    apiEndpoint: apiEndpoint
      ? (context) => {
          const binding = apiEndpoint(context)
          return binding
            ? createTargetInterpretedTanStackQueryDataSourceBinding(
                context,
                targetInterpretation,
                binding,
                defaultAuthorization,
              )
            : binding
        }
      : undefined,
    byDataSourceId,
    byEndpointId: mapFactories(byEndpointId, (factory) => (context) => {
      const binding = factory(context)
      return binding
        ? createTargetInterpretedTanStackQueryDataSourceBinding(
            context,
            targetInterpretation,
            binding,
            defaultAuthorization,
          )
        : binding
    }),
    defaultAuthorization,
  }
}

/**
 * Creates a generic TanStack Query projection registry for API endpoint data
 * sources. The registry resolves endpoint ids from backend bindings, projects
 * route parameters, applies runtime annotations, and executes through the
 * active target interpretation.
 */
export function createApiEndpointTanStackQueryDataSourceBindingProjectionRegistry({
  createQueryKey,
  defaultAuthorization,
  executeEndpoint,
  isEnabled,
  pendingLabel,
  retry,
  targetInterpretation,
}: ApiEndpointTanStackQueryDataSourceBindingProjectionRegistryOptions): PresentationDataSourceBindingProjectionRegistry {
  return createTanStackQueryDataSourceBindingProjectionRegistry({
    apiEndpoint: (context) => {
      const endpointId = readPresentationDataSourceEndpointId(context.binding)
      if (!endpointId) {
        return null
      }

      const endpointExecutor =
        executeEndpoint ??
        resolvePresentationDataSourceTargetEndpointExecutor(
          targetInterpretation,
          context.binding,
        )
      if (!endpointExecutor) {
        return null
      }

      const routeParameters = projectDataSourceRouteParameters(
        context.dataSource.Parameters,
        context.context.routeParameters,
      )
      const apiContext = {
        ...context,
        endpointId,
        routeParameters,
      } satisfies PresentationDataSourceApiEndpointFactoryContext
      const runtimeOptions = readDataSourceBindingRuntimeOptions(context.dataSource)
      const targetIsEnabled = targetInterpretation?.apiEndpoint?.isEnabled?.(apiContext)
      const projectedIsEnabled = isEnabled?.(apiContext)
      return {
        dataSourceId: context.dataSource.Id,
        enabled:
          (targetIsEnabled ?? true) &&
          (projectedIsEnabled ?? true) &&
          hasRequiredDataSourceRouteParameters(context.dataSource.Parameters, routeParameters),
        emptyMessage: runtimeOptions.emptyMessage,
        fallbackData: runtimeOptions.fallbackData,
        pendingLabel: resolveApiEndpointDataSourcePendingLabel(
          pendingLabel ??
            runtimeOptions.pendingLabel ??
            targetInterpretation?.apiEndpoint?.pendingLabel,
          apiContext,
        ),
        queryFn: () => endpointExecutor(endpointId, { routeParameters }),
        queryKey:
          createQueryKey?.(apiContext) ??
          targetInterpretation?.apiEndpoint?.createQueryKey?.(apiContext) ??
          createDefaultApiEndpointDataSourceQueryKey(apiContext),
        refetchInterval: runtimeOptions.refetchInterval,
        retry: retry ?? runtimeOptions.retry ?? targetInterpretation?.apiEndpoint?.retry ?? false,
        staleTime: runtimeOptions.staleTime,
      }
    },
    defaultAuthorization,
    targetInterpretation,
  })
}

function resolveApiEndpointFactory(
  registries: readonly PresentationDataSourceBindingProjectionRegistry[],
): PresentationDataSourceBindingFactory | undefined {
  const factories = registries.flatMap((registry) =>
    registry.apiEndpoint ? [registry.apiEndpoint] : [],
  )
  if (factories.length === 0) {
    return undefined
  }

  return (context) => {
    for (let i = factories.length - 1; i >= 0; i -= 1) {
      const binding = factories[i]?.(context)
      if (binding) {
        return binding
      }
    }

    return null
  }
}

function resolveFallbackFactory(
  registries: readonly PresentationDataSourceBindingProjectionRegistry[],
): PresentationDataSourceBindingFactory | undefined {
  const factories = registries.flatMap((registry) =>
    registry.fallback ? [registry.fallback] : [],
  )
  if (factories.length === 0) {
    return undefined
  }

  return (context) => {
    for (let i = factories.length - 1; i >= 0; i -= 1) {
      const binding = factories[i]?.(context)
      if (binding) {
        return binding
      }
    }

    return null
  }
}

function resolveDataSourceBindingFactory(
  registry: PresentationDataSourceBindingProjectionRegistry,
  dataSource: DataSourceDefinition,
): PresentationDataSourceBindingFactory | undefined {
  const endpointId = isApiEndpointBinding(dataSource.Binding)
    ? dataSource.Binding?.EndpointId
    : null
  const exactFactory =
    registry.byDataSourceId?.[dataSource.Id] ??
    (endpointId ? registry.byEndpointId?.[endpointId] : undefined)
  if (exactFactory) {
    return exactFactory
  }

  const genericFactories = [
    endpointId ? registry.apiEndpoint : undefined,
    registry.fallback,
  ].filter((factory): factory is PresentationDataSourceBindingFactory => Boolean(factory))
  if (genericFactories.length === 0) {
    return undefined
  }

  return (context) => {
    for (const factory of genericFactories) {
      const binding = factory(context)
      if (binding) {
        return binding
      }
    }

    return null
  }
}

function isAggregateQueryDataSource(
  dataSource: Pick<DataSourceDefinition, 'Kind'>,
) {
  return matchesDataSourceKind(
    dataSource.Kind,
    dataSourceKinds.aggregateQuery,
    'aggregateQuery',
  )
}

function isCollectionQueryDataSource(
  dataSource: Pick<DataSourceDefinition, 'Kind'>,
) {
  return matchesDataSourceKind(
    dataSource.Kind,
    dataSourceKinds.collectionQuery,
    'collectionQuery',
  )
}

function isLocalStateDataSource(
  dataSource: Pick<DataSourceDefinition, 'Kind'>,
) {
  return matchesDataSourceKind(
    dataSource.Kind,
    dataSourceKinds.localState,
    'localState',
  )
}

function isPromptPreviewDataSource(
  dataSource: Pick<DataSourceDefinition, 'Kind'>,
) {
  return matchesDataSourceKind(
    dataSource.Kind,
    dataSourceKinds.promptPreview,
    'promptPreview',
  )
}

function matchesDataSourceKind(
  value: unknown,
  numericValue: DataSourceKind,
  camelLabel: string,
) {
  const pascalLabel = dataSourceKindLabels[numericValue]
  return (
    value === numericValue ||
    String(value) === String(numericValue) ||
    String(value) === pascalLabel ||
    String(value).toLocaleLowerCase() === camelLabel.toLocaleLowerCase()
  )
}

function resolveQueryKeyParts(
  queryKeyParts:
    | readonly unknown[]
    | ((context: PresentationDataSourceBindingFactoryContext) => readonly unknown[]),
  context: PresentationDataSourceBindingFactoryContext,
) {
  return typeof queryKeyParts === 'function'
    ? queryKeyParts(context)
    : queryKeyParts
}

function resolveDataSourceQueryInterpretationTransforms(
  interpretation: DataSourceQueryInterpretation | undefined,
  registryTransforms: DataSourceQueryInterpretationRegistry['transforms'],
  context: PresentationDataSourceBindingFactoryContext,
) {
  const resolvedRegistryTransforms = resolveDataSourceQueryTransforms(
    registryTransforms,
    context,
  )
  const resolvedInterpretationTransforms = resolveDataSourceQueryTransforms(
    interpretation?.transforms,
    context,
  )

  if (!resolvedRegistryTransforms) {
    return resolvedInterpretationTransforms
  }

  if (!resolvedInterpretationTransforms) {
    return resolvedRegistryTransforms
  }

  return {
    ...resolvedRegistryTransforms,
    ...resolvedInterpretationTransforms,
  }
}

function resolveDataSourceQueryTransforms(
  transforms: DataSourceQueryInterpretationRegistry['transforms'],
  context: PresentationDataSourceBindingFactoryContext,
) {
  return typeof transforms === 'function'
    ? transforms(context)
    : transforms
}

function mergeDataSourceQueryLoweringTransforms(
  targetTransforms:
    | Readonly<Record<string, DataSourceQueryLoweringTransform>>
    | undefined,
  localTransforms:
    | Readonly<Record<string, DataSourceQueryLoweringTransform>>
    | undefined,
) {
  if (!targetTransforms) {
    return localTransforms
  }

  if (!localTransforms) {
    return targetTransforms
  }

  return {
    ...targetTransforms,
    ...localTransforms,
  }
}

function resolveDataSourceQueryInterpretationValue(
  interpretation: DataSourceQueryInterpretation | undefined,
  context: PresentationDataSourceBindingFactoryContext,
) {
  if (!interpretation?.normalizeValue && !interpretation?.readValue) {
    return undefined
  }

  const value = interpretation.readValue
    ? interpretation.readValue(context)
    : readPresentationQueryFormAppliedValue(context.context, context.dataSource.Id)
  return interpretation.normalizeValue
    ? interpretation.normalizeValue(value, context)
    : value
}

/**
 * Reads target binding options from a backend presentation binding.
 *
 * Option names are matched case-insensitively and may use either camelCase or
 * PascalCase.
 */
export function readPresentationDataSourceTargetBindingOptions(
  binding: PresentationBindingDefinition | null | undefined,
): PresentationDataSourceTargetBindingOptions {
  return {
    authorizationPolicyId: readPresentationBindingStringOption(
      binding?.Options,
      'authorizationPolicyId',
    ),
    executionTargetId: readPresentationBindingStringOption(
      binding?.Options,
      'executionTargetId',
    ),
  }
}

/**
 * Reads runtime query defaults from the standard
 * `cohesive.presentation.data-source.runtime` data-source annotation.
 */
export function readDataSourceBindingRuntimeOptions(
  dataSource: Pick<DataSourceDefinition, 'Annotations'> | null | undefined,
): PresentationDataSourceBindingRuntimeOptions {
  const annotation = dataSource?.Annotations.find(
    (candidate) =>
      candidate.Name === 'cohesive.presentation.data-source.runtime' ||
      candidate.Name.toLocaleLowerCase() === 'cohesive.presentation.data-source.runtime',
  )
  if (!annotation || !annotation.Value || typeof annotation.Value !== 'object') {
    return {}
  }

  const options = annotation.Value as Readonly<Record<string, unknown>>
  const refetchInterval =
    readNumberOption(options, 'refetchIntervalMs') ??
    readSecondsOptionAsMilliseconds(options, 'refetchIntervalSeconds')
  const staleTime =
    readNumberOption(options, 'staleTimeMs') ??
    readSecondsOptionAsMilliseconds(options, 'staleTimeSeconds')

  return {
    emptyMessage: readStringOption(options, 'emptyMessage'),
    fallbackData: readObjectOption(options, 'fallbackData'),
    pendingLabel: readStringOption(options, 'pendingLabel'),
    refetchInterval,
    retry: readRetryOption(options, 'retry'),
    staleTime,
  }
}

/**
 * Resolves the endpoint executor selected by a binding's `executionTargetId`,
 * falling back to the default API endpoint executor on the target
 * interpretation.
 */
export function resolvePresentationDataSourceTargetEndpointExecutor(
  targetInterpretation: PresentationDataSourceTargetInterpretation | undefined,
  binding: PresentationBindingDefinition | null | undefined,
): PresentationDataSourceEndpointExecutor | undefined {
  const executionTargetId = readPresentationDataSourceTargetBindingOptions(
    binding,
  ).executionTargetId
  if (executionTargetId) {
    return targetInterpretation?.apiEndpoint?.executeEndpointByTargetId?.[executionTargetId]
  }

  return targetInterpretation?.apiEndpoint?.executeEndpoint
}

async function materializeDataSourceAggregateItems({
  aggregation,
  sourceDataSource,
  sourceEndpointId,
  targetInterpretation,
  transforms,
  value,
}: {
  readonly aggregation: DataSourceAggregateQuery
  readonly sourceDataSource: DataSourceDefinition
  readonly sourceEndpointId: string
  readonly targetInterpretation: PresentationDataSourceTargetInterpretation
  readonly transforms?: Readonly<Record<string, DataSourceQueryLoweringTransform>>
  readonly value: unknown
}): Promise<readonly unknown[]> {
  const materializeAllPages = matchesDataSourceAggregateMaterializationKind(
    aggregation.Materialization.Kind,
    dataSourceAggregateMaterializationKinds.allPages,
    'allPages',
  )
  const pageSize =
    aggregation.Materialization.PageSize ??
    sourceDataSource.Query?.Pagination?.DefaultPageSize ??
    100
  const endpointBinding = findDataSourceQueryEndpointBinding(
    sourceDataSource,
    sourceEndpointId,
  )
  const items: unknown[] = []
  const seenCursors = new Set<string>()
  let cursor: string | null | undefined = null

  do {
    const paginationRequest = materializeAllPages
      ? createDataSourcePaginationRequest(sourceDataSource, {
          cursor,
          limit: pageSize,
        })
      : null
    const query = createDataSourceEndpointQueryRequest<Record<string, unknown>>({
      dataSource: sourceDataSource,
      endpointId: sourceEndpointId,
      paginationRequest,
      transforms,
      value,
    })
    const response = await executePresentationDataSourceTargetEndpoint(
      targetInterpretation,
      sourceEndpointId,
      { query },
      { binding: sourceDataSource.Binding ?? null },
    )
    const pageItems = readAggregateResponseItems(response, endpointBinding?.ItemsPath)
    items.push(...pageItems)

    const nextCursor = readStringObjectPath(
      response,
      sourceDataSource.Query?.Pagination?.Response.CursorField,
    )
    cursor = materializeAllPages && nextCursor && !seenCursors.has(nextCursor)
      ? nextCursor
      : null
    if (cursor) {
      seenCursors.add(cursor)
    }
  } while (cursor)

  return items
}

function evaluateDataSourceAggregateQuery(
  aggregation: DataSourceAggregateQuery,
  items: readonly unknown[],
) {
  const result: Record<string, unknown> = {}
  for (const measure of aggregation.Measures) {
    const predicate = measure.Predicate
    const matchingItems = predicate
      ? items.filter((item) => evaluateDataSourceAggregatePredicate(predicate, item))
      : items
    writeObjectPath(
      result,
      measure.TargetPath,
      evaluateDataSourceAggregateMeasure(measure.Operator, measure.SourceField, matchingItems),
    )
  }

  return result
}

function evaluateDataSourceAggregateMeasure(
  operator: AggregateOperator,
  sourceField: DataSourceAggregateQuery['Measures'][number]['SourceField'],
  items: readonly unknown[],
) {
  if (matchesAggregateOperator(operator, aggregateOperators.count, 'count')) {
    return items.length
  }

  const values: readonly unknown[] = sourceField
    ? items.map((item) => readObjectPath(item, fieldPathToString(sourceField)))
    : items

  if (matchesAggregateOperator(operator, aggregateOperators.sum, 'sum')) {
    return values.reduce<number>(
      (sum, value) => sum + (typeof value === 'number' ? value : 0),
      0,
    )
  }

  if (matchesAggregateOperator(operator, aggregateOperators.min, 'min')) {
    return values
      .filter((value): value is number => typeof value === 'number')
      .reduce<number | null>((min, value) => min === null ? value : Math.min(min, value), null)
  }

  if (matchesAggregateOperator(operator, aggregateOperators.max, 'max')) {
    return values
      .filter((value): value is number => typeof value === 'number')
      .reduce<number | null>((max, value) => max === null ? value : Math.max(max, value), null)
  }

  if (matchesAggregateOperator(operator, aggregateOperators.any, 'any')) {
    return values.some(Boolean)
  }

  if (matchesAggregateOperator(operator, aggregateOperators.all, 'all')) {
    return values.every(Boolean)
  }

  return null
}

function evaluateDataSourceAggregatePredicate(
  predicate: DataSourceAggregatePredicate,
  item: unknown,
): boolean {
  if (
    matchesDataSourceAggregatePredicateKind(
      predicate.Kind,
      dataSourceAggregatePredicateKinds.and,
      'and',
    )
  ) {
    return (predicate.Terms ?? []).every((term) =>
      evaluateDataSourceAggregatePredicate(term, item))
  }

  if (
    matchesDataSourceAggregatePredicateKind(
      predicate.Kind,
      dataSourceAggregatePredicateKinds.or,
      'or',
    )
  ) {
    return (predicate.Terms ?? []).some((term) =>
      evaluateDataSourceAggregatePredicate(term, item))
  }

  if (
    matchesDataSourceAggregatePredicateKind(
      predicate.Kind,
      dataSourceAggregatePredicateKinds.not,
      'not',
    )
  ) {
    const [term] = predicate.Terms ?? []
    return term ? !evaluateDataSourceAggregatePredicate(term, item) : true
  }

  const value = readObjectPath(item, fieldPathToString(predicate.Field))
  if (
    matchesDataSourceAggregatePredicateKind(
      predicate.Kind,
      dataSourceAggregatePredicateKinds.fieldEquals,
      'fieldEquals',
    )
  ) {
    return String(value) === predicate.Value
  }

  if (
    matchesDataSourceAggregatePredicateKind(
      predicate.Kind,
      dataSourceAggregatePredicateKinds.fieldNotEquals,
      'fieldNotEquals',
    )
  ) {
    return String(value) !== predicate.Value
  }

  if (
    matchesDataSourceAggregatePredicateKind(
      predicate.Kind,
      dataSourceAggregatePredicateKinds.fieldHasValue,
      'fieldHasValue',
    )
  ) {
    return hasAggregateValue(value)
  }

  return false
}

function readPresentationDataSourceEndpointId(
  binding: PresentationBindingDefinition | null | undefined,
) {
  return isApiEndpointBinding(binding) && binding?.EndpointId
    ? binding.EndpointId
    : null
}

function applyDataSourceDefaults(
  binding: PresentationDataSourceBinding,
  dataSource: DataSourceDefinition,
): PresentationDataSourceBinding {
  if (!isTanStackQueryBinding(binding)) {
    return binding
  }

  return {
    ...binding,
    staleTime: binding.staleTime ?? readDataSourceStaleTime(dataSource),
  }
}

function createUnboundDataSourceBinding(
  dataSource: DataSourceDefinition,
  authorization: PresentationDataSourceAuthorizationRequirement | undefined,
): PresentationDataSourceBinding {
  return presentationDataSourceBindings.localValue({
    authorization:
      authorization ??
      presentationDataSourceAuthorization.required({
        blockedLabel: `No frontend binding is registered for '${dataSource.Name}'.`,
        isAuthorized: false,
      }),
    data: undefined,
    dataSourceId: dataSource.Id,
  })
}

function readDataSourceStaleTime(dataSource: DataSourceDefinition) {
  const cache = dataSource.Cache
  if (!cache) {
    return undefined
  }

  if (
    cache.Kind === cachePolicyKinds.reactQuery ||
    String(cache.Kind).toLocaleLowerCase() === 'reactquery'
  ) {
    return cache.StaleAfterSeconds === null ||
      cache.StaleAfterSeconds === undefined
      ? undefined
      : cache.StaleAfterSeconds * 1000
  }

  return undefined
}

function isApiEndpointBinding(
  binding: PresentationBindingDefinition | null | undefined,
) {
  return (
    binding?.Kind === presentationBindingKinds.apiEndpoint ||
    String(binding?.Kind).toLocaleLowerCase() === 'apiendpoint'
  )
}

function isLocalStateBinding(
  binding: PresentationBindingDefinition | null | undefined,
) {
  return (
    binding?.Kind === presentationBindingKinds.localState ||
    String(binding?.Kind).toLocaleLowerCase() === 'localstate'
  )
}

function readPresentationBindingStringOption(
  options: unknown,
  key: string,
): string | null {
  if (!options || typeof options !== 'object') {
    return null
  }

  const record = options as Readonly<Record<string, unknown>>
  const value =
    record[key] ??
    record[capitalizeFirstCharacter(key)] ??
    readCaseInsensitiveProperty(record, key)
  return typeof value === 'string' && value.trim().length > 0
    ? value
    : null
}

function readAggregateResponseItems(response: unknown, itemsPath: string | null | undefined) {
  const value = itemsPath ? readObjectPath(response, itemsPath) : response
  if (Array.isArray(value)) {
    return value
  }

  const items = readObjectPath(response, 'Items')
  return Array.isArray(items) ? items : []
}

function readStringObjectPath(value: unknown, path: string | null | undefined) {
  const raw = readObjectPath(value, path)
  return typeof raw === 'string' && raw.trim().length > 0 ? raw.trim() : null
}

function fieldPathToString(
  field: DataSourceAggregatePredicate['Field'] | NonNullable<DataSourceAggregateQuery['Measures'][number]['SourceField']>,
) {
  return field?.Segments.map((segment) => segment.Segment).filter(Boolean).join('.') ?? ''
}

function hasAggregateValue(value: unknown) {
  if (value === null || value === undefined) {
    return false
  }

  if (typeof value === 'string') {
    return value.trim().length > 0
  }

  if (Array.isArray(value)) {
    return value.length > 0
  }

  return true
}

function matchesAggregateOperator(
  value: unknown,
  numericValue: AggregateOperator,
  camelLabel: string,
) {
  return matchesGeneratedEnum(value, numericValue, camelLabel)
}

function matchesDataSourceAggregateMaterializationKind(
  value: unknown,
  numericValue: number,
  camelLabel: string,
) {
  return matchesGeneratedEnum(value, numericValue, camelLabel)
}

function matchesDataSourceAggregatePredicateKind(
  value: unknown,
  numericValue: number,
  camelLabel: string,
) {
  return matchesGeneratedEnum(value, numericValue, camelLabel)
}

function matchesGeneratedEnum(
  value: unknown,
  numericValue: number,
  camelLabel: string,
) {
  return (
    value === numericValue ||
    String(value) === String(numericValue) ||
    String(value).toLocaleLowerCase() === camelLabel.toLocaleLowerCase()
  )
}

function readStringOption(
  options: Readonly<Record<string, unknown>>,
  key: string,
) {
  const value = readObjectOption(options, key)
  return typeof value === 'string' && value.trim().length > 0
    ? value
    : undefined
}

function readObjectOption(
  options: Readonly<Record<string, unknown>>,
  key: string,
) {
  return Object.prototype.hasOwnProperty.call(options, key)
    ? options[key]
    : readCaseInsensitiveProperty(options, key)
}

function readNumberOption(
  options: Readonly<Record<string, unknown>>,
  key: string,
) {
  const value = readObjectOption(options, key)
  return typeof value === 'number' && Number.isFinite(value)
    ? value
    : undefined
}

function readSecondsOptionAsMilliseconds(
  options: Readonly<Record<string, unknown>>,
  key: string,
) {
  const seconds = readNumberOption(options, key)
  return seconds === undefined ? undefined : seconds * 1000
}

function readRetryOption(
  options: Readonly<Record<string, unknown>>,
  key: string,
) {
  const value = readObjectOption(options, key)
  return typeof value === 'boolean' || typeof value === 'number'
    ? value
    : undefined
}

function readCaseInsensitiveProperty(
  record: Readonly<Record<string, unknown>>,
  key: string,
) {
  const normalizedKey = key.toLocaleLowerCase()
  const match = Object.keys(record).find(
    (candidate) => candidate.toLocaleLowerCase() === normalizedKey,
  )
  return match ? record[match] : undefined
}

function capitalizeFirstCharacter(value: string) {
  return value.length > 0
    ? `${value.slice(0, 1).toLocaleUpperCase()}${value.slice(1)}`
    : value
}

function projectDataSourceRouteParameters(
  parameters: readonly ParameterDefinition[],
  routeParameters: Readonly<Record<string, string | undefined>> | undefined,
) {
  return Object.fromEntries(
    parameters.map((parameter) => [
      parameter.Name,
      readRouteParameterValue(routeParameters, parameter.Name),
    ]),
  )
}

function hasRequiredDataSourceRouteParameters(
  parameters: readonly ParameterDefinition[],
  routeParameters: Readonly<Record<string, string | null | undefined>>,
) {
  return parameters.every(
    (parameter) =>
      !parameter.IsRequired ||
      typeof routeParameters[parameter.Name] === 'string' &&
        routeParameters[parameter.Name]!.length > 0,
  )
}

function readRouteParameterValue(
  routeParameters: Readonly<Record<string, string | undefined>> | undefined,
  parameterName: string,
) {
  if (!routeParameters) {
    return undefined
  }

  if (Object.prototype.hasOwnProperty.call(routeParameters, parameterName)) {
    return routeParameters[parameterName]
  }

  const match = Object.keys(routeParameters).find(
    (candidate) => candidate.toLocaleLowerCase() === parameterName.toLocaleLowerCase(),
  )
  return match ? routeParameters[match] : undefined
}

function hasOwn<TValue>(
  values: Readonly<Record<string, TValue>>,
  key: string,
) {
  return Object.prototype.hasOwnProperty.call(values, key)
}

function resolveApiEndpointDataSourcePendingLabel(
  label: ApiEndpointTanStackQueryDataSourceBindingProjectionRegistryOptions['pendingLabel'],
  context: PresentationDataSourceApiEndpointFactoryContext,
) {
  if (typeof label === 'function') {
    return label(context)
  }

  return label ?? `Loading ${context.dataSource.Name.toLocaleLowerCase()}...`
}

function createDefaultApiEndpointDataSourceQueryKey({
  context,
  dataSource,
  endpointId,
  routeParameters,
}: PresentationDataSourceApiEndpointFactoryContext) {
  return [
    'presentation-data-source',
    dataSource.Id,
    endpointId,
    routeParameters,
    ...(context.queryKeyParts ?? []),
  ] as const
}

function mapFactories<TFactory, TMappedFactory>(
  factories: Readonly<Record<string, TFactory>> | undefined,
  mapFactory: (factory: TFactory) => TMappedFactory,
) {
  if (!factories) {
    return undefined
  }

  return Object.fromEntries(
    Object.entries(factories).map(([key, factory]) => [
      key,
      mapFactory(factory as TFactory),
    ]),
  )
}
