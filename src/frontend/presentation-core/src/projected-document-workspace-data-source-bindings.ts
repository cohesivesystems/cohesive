import {
  projectDocumentProfileDataSourceBindings,
  type DocumentWorkspaceProfileProjection,
} from './document-module'
import type { PresentationModuleDefinition } from './module'
import {
  createApiEndpointTanStackQueryDataSourceBindingProjectionRegistry,
  type PresentationDataSourceApiEndpointFactoryContext,
  type PresentationDataSourceEndpointExecutor,
  type PresentationDataSourceTargetInterpretation,
} from './data-source-projection'
import {
  type PresentationDataSourceAuthorizationRequirement,
  type PresentationDataSourceBinding,
} from './presentation-data-source-binding-model'
import {
  documentDataSourceRoles,
  type DocumentDataSourceRole,
} from '@cohesive/presentation-contracts'

const defaultDocumentWorkspaceDataSourceRoles = [
  documentDataSourceRoles.resource,
  documentDataSourceRoles.metadata,
] as const
const emptyQueryKeyParts = [] as const

/**
 * Runtime subset required to decide which profile-scoped data sources are
 * active for the current document workspace projection.
 */
export interface DocumentWorkspaceDataSourceRuntimeState {
  readonly activeLayoutModeId: string
  readonly activeProjectionId: string | null
  readonly activeViewId: string | null
  readonly projectionViewIds: readonly string[]
}

/** Context used to create stable cache keys for document workspace data sources. */
export interface DocumentWorkspaceDataSourceQueryKeyContext {
  readonly dataSourceId: string
  readonly queryKeyParts: readonly unknown[]
  readonly resourceId: string
  readonly routeParameters: Readonly<Record<string, string | null | undefined>>
}

/** Creates a cache key for a projected document workspace data source. */
export type DocumentWorkspaceDataSourceQueryKeyFactory = (
  context: DocumentWorkspaceDataSourceQueryKeyContext,
) => readonly unknown[]

/** Options for projecting a document profile's load-time data sources. */
export interface ProjectDocumentWorkspaceDataSourceBindingsOptions {
  /** Authorization applied to API-backed document workspace data sources when no target interpretation supplies it. */
  readonly authorization?: PresentationDataSourceAuthorizationRequirement
  /** Cache-key factory supplied by the app/runtime cache domain. */
  readonly createQueryKey?: DocumentWorkspaceDataSourceQueryKeyFactory
  /** Concrete endpoint executor for API-backed presentation data sources when no target interpretation supplies it. */
  readonly executeEndpoint?: PresentationDataSourceEndpointExecutor
  /** Optional app-level enablement predicate layered over route-parameter checks. */
  readonly isEnabled?: (context: PresentationDataSourceApiEndpointFactoryContext) => boolean
  /** Presentation module containing canonical data-source definitions. */
  readonly module: PresentationModuleDefinition | null
  /** Projected document workspace profile whose data sources should be bound. */
  readonly projection: Pick<DocumentWorkspaceProfileProjection, 'dataSourceId' | 'profile'>
  /** Additional cache-key parts, such as auth or tenant scope. */
  readonly queryKeyParts?: readonly unknown[]
  /** Concrete document resource id from the current route. */
  readonly resourceId: string
  /** Route parameter name used by document resource endpoints. */
  readonly resourceRouteParameterName?: string
  /** Data-source roles to bind; defaults to resource and metadata. */
  readonly roles?: readonly DocumentDataSourceRole[]
  /** Current document workspace runtime state used for activation policies. */
  readonly runtime: DocumentWorkspaceDataSourceRuntimeState
  /** Target-level interpretation for data-source execution and authorization semantics. */
  readonly targetInterpretation?: PresentationDataSourceTargetInterpretation
}

/** Result of projecting document workspace data-source bindings. */
export interface ProjectDocumentWorkspaceDataSourceBindingsResult {
  readonly bindings: readonly PresentationDataSourceBinding[]
  readonly resourceQueryKey: readonly unknown[]
}

/**
 * Projects a document workspace profile's load-time data sources to concrete
 * frontend bindings.
 *
 * The backend declares which data sources exist and when they are active. This
 * helper contributes the reusable document-workspace interpretation: route
 * parameter wiring, active-view activation, API endpoint binding, cache-key
 * creation, and primary resource query-key exposure for downstream mutations.
 */
export function projectDocumentWorkspaceDataSourceBindings({
  authorization,
  createQueryKey = createDocumentWorkspaceDataSourceQueryKey,
  executeEndpoint,
  isEnabled,
  module,
  projection,
  queryKeyParts = emptyQueryKeyParts,
  resourceId,
  resourceRouteParameterName = 'id',
  roles = defaultDocumentWorkspaceDataSourceRoles,
  runtime,
  targetInterpretation,
}: ProjectDocumentWorkspaceDataSourceBindingsOptions): ProjectDocumentWorkspaceDataSourceBindingsResult {
  const routeParameters = {
    [resourceRouteParameterName]: resourceId,
  } satisfies Readonly<Record<string, string>>
  const registry = createApiEndpointTanStackQueryDataSourceBindingProjectionRegistry({
    createQueryKey: ({ dataSource, routeParameters }) =>
      createQueryKey({
        dataSourceId: dataSource.Id,
        queryKeyParts,
        resourceId,
        routeParameters,
      }),
    defaultAuthorization: authorization && !targetInterpretation
      ? () => authorization
      : undefined,
    executeEndpoint,
    isEnabled: createDocumentWorkspaceDataSourceEnabledPredicate(resourceId, isEnabled),
    pendingLabel: ({ dataSource }) =>
      dataSource.Id === projection.dataSourceId
        ? 'Loading document...'
        : `Loading ${dataSource.Name.toLocaleLowerCase()}...`,
    retry: false,
    targetInterpretation,
  })

  return {
    bindings: projectDocumentProfileDataSourceBindings({
      activation: {
        activeLayoutModeId: runtime.activeLayoutModeId,
        activeProjectionId: runtime.activeProjectionId,
        activeViewId: resolveActiveDocumentViewId(runtime),
        routeParameters,
      },
      context: {
        routeParameters,
        queryKeyParts,
      },
      module,
      profile: projection.profile,
      registry,
      roles,
    }),
    resourceQueryKey: createQueryKey({
      dataSourceId: projection.dataSourceId,
      queryKeyParts,
      resourceId,
      routeParameters,
    }),
  }
}

/** Default cache key for document workspace data sources when an app does not supply one. */
export function createDocumentWorkspaceDataSourceQueryKey({
  dataSourceId,
  queryKeyParts,
  resourceId,
}: DocumentWorkspaceDataSourceQueryKeyContext) {
  return ['document-workspace', dataSourceId, resourceId, ...queryKeyParts] as const
}

function createDocumentWorkspaceDataSourceEnabledPredicate(
  resourceId: string,
  isEnabled: ProjectDocumentWorkspaceDataSourceBindingsOptions['isEnabled'],
) {
  if (resourceId.length === 0) {
    return () => false
  }

  return isEnabled
}

function resolveActiveDocumentViewId(runtime: DocumentWorkspaceDataSourceRuntimeState) {
  return runtime.activeViewId && runtime.projectionViewIds.includes(runtime.activeViewId)
    ? runtime.activeViewId
    : (runtime.projectionViewIds[0] ?? '')
}
