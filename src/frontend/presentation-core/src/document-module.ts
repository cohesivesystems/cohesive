import type {
  ActionPlacementDefinition,
  DataSourceDefinition,
  DocumentActionStatusNoticeDefinition,
  DocumentProcessTaskNoticeDefinition,
  PresentationModuleDefinition,
  ProcessTaskSelectorDefinition,
  WorkspaceRefDefinition,
} from './module'
import type { PresentationDataSourceBinding } from './presentation-data-source-binding-model'
import {
  projectPresentationDataSourceBindings,
  type PresentationDataSourceBindingProjectionRegistry,
  type PresentationDataSourceProjectionContext,
} from './data-source-projection'
import {
  readPresentationFieldValue,
} from './presentation-value-resolution'
import type {
  DataSourceRefDefinition,
  DocumentActionRuntimeProfileDefinition,
  DocumentDataSourceActivationPolicyDefinition,
  DocumentAddressDefinition,
  DocumentDataSourceRole,
  DocumentIdentityDefinition,
  DocumentMetricSourceDefinition,
  PageHostDefinition,
} from '@cohesive/presentation-contracts'
import {
  dataSourceKinds,
  documentKindLabels,
  documentDataSourceRoles,
  documentDataSourceRoleLabels,
} from '@cohesive/presentation-contracts'

/**
 * Frontend projection of a backend document profile.
 *
 * A document profile describes the semantic document being edited or viewed,
 * the document-kind discriminator, and the profile-scoped data sources that
 * supply resource, metadata, summary, or auxiliary state.
 */
export interface DocumentProfileProjection {
  readonly Actions?: readonly ActionPlacementDefinition[]
  readonly DataSources?: readonly DocumentProfileDataSourceProjection[]
  readonly Document?: {
    readonly Address?: DocumentAddressDefinition | null
    readonly DataSource?: DataSourceRefDefinition | null
    readonly Id?: string
    readonly Identity?: DocumentIdentityDefinition | null
    readonly Kind: string | number
  } | null
  readonly Id: string
  readonly Name?: string
  readonly MetricSources?: readonly DocumentMetricSourceDefinition[]
  readonly ActionStatusNotices?: readonly DocumentActionStatusNoticeDefinition[]
  readonly ActionRuntimeProfiles?: readonly DocumentActionRuntimeProfileDefinition[]
  readonly ProcessTaskNotices?: readonly DocumentProcessTaskNoticeDefinition[]
  readonly ProcessTaskSelectors?: readonly ProcessTaskSelectorDefinition[]
}

/**
 * Runtime identity for a presentation document kind.
 *
 * The backend owns the enum value; this projection provides stable frontend
 * text and key forms without requiring document-specific handwritten unions.
 */
export interface ProjectedDocumentKind {
  readonly id: string
  readonly label: string
  readonly value: string | number
}

/**
 * Opaque runtime resource loaded for a document profile.
 *
 * Generic document helpers read identity, document payload, display metadata,
 * and version metadata through DocumentWorkspaceResourceProjection instead of
 * assuming product-specific property names.
 */
export type ProjectedDocumentResource = Readonly<Record<string, unknown>>

/**
 * Generic runtime projection for a document workspace profile.
 *
 * Product-specific workspaces can extend this with extra capabilities while
 * relying on the shared document module for resource, summary, metadata, and
 * editor-address interpretation.
 */
export interface DocumentWorkspaceProfileProjection {
  /** Data source that loads and saves the primary JSON document resource. */
  readonly dataSourceId: string
  /** Backend-declared document kind projected into stable runtime identity metadata. */
  readonly documentKind: ProjectedDocumentKind
  /** Stable editor URI shown for local preview/editor integrations. */
  readonly editorPath: (resourceId: string) => string
  /** Human-readable label for workspace chrome and fallback messages. */
  readonly label: string
  /** Optional metadata data source projection attached to the profile. */
  readonly metadata?: DocumentWorkspaceMetadataProjection
  /** Original document profile that produced this runtime projection. */
  readonly profile: DocumentProfileProjection
  /** Stable profile id used for document-instance and editor-state identity. */
  readonly profileId: string
  /** IR-derived field paths used to interpret otherwise opaque resource shapes. */
  readonly resource: DocumentWorkspaceResourceProjection
  /** Optional summary data source projection for header metrics. */
  readonly summary?: DocumentWorkspaceSummaryProjection
}

/** Resource field-path projection for generic document workspace state. */
export interface DocumentWorkspaceResourceProjection {
  readonly createdAtPath?: string | null
  readonly documentPath: string
  readonly idPath: string
  readonly titlePath?: string | null
  readonly updatedAtPath?: string | null
  readonly versionPath?: string | null
}

/** Data source projection for resource metadata associated with a document. */
export interface DocumentWorkspaceMetadataProjection {
  readonly dataSourceId: string
}

/**
 * Summary projection for document header metrics.
 *
 * Field paths come from profile metric sources that target the summary data
 * source, allowing summary rendering to stay profile-driven.
 */
export interface DocumentWorkspaceSummaryProjection {
  readonly dataSourceId: string
  readonly fieldPaths: readonly string[]
}

/** Options for projecting a document profile into a workspace runtime shape. */
export interface ProjectDocumentWorkspaceProfileOptions {
  /** URI scheme used when the profile does not declare a document source address template. */
  readonly fallbackAddressScheme?: string
}

/**
 * Profile-scoped reference to a presentation data source.
 *
 * The role and activation policy let a workspace decide which data sources
 * should be bound for the current document state without hard-coding concrete
 * resource types in React components.
 */
export interface DocumentProfileDataSourceProjection {
  readonly Activation?: DocumentDataSourceActivationPolicyDefinition | null
  readonly DataSource: DataSourceRefDefinition
  readonly Description?: string | null
  readonly Id: string
  readonly IsRequired: boolean
  readonly Role: DocumentDataSourceRole
}

/** Metric source projection preserved from the presentation IR document profile. */
export type DocumentMetricSourceProjection = DocumentMetricSourceDefinition

/**
 * Minimal workspace shape required by document-profile resolution.
 *
 * The generated presentation module may contain richer workspace definitions;
 * these utilities only depend on the document-profile subset.
 */
interface DocumentWorkspaceProjection {
  readonly DefaultDocumentProfileId?: string | null
  readonly DocumentProfiles: readonly DocumentProfileProjection[]
  readonly Id: string
}

type DocumentWorkspaceRefProjection = Pick<
  WorkspaceRefDefinition,
  'DocumentProfileId' | 'WorkspaceId'
>

/** Options for projecting active document-profile data sources into bindings. */
export interface ProjectDocumentProfileDataSourceBindingsOptions {
  /** Current view/projection/route state used to evaluate activation policies. */
  readonly activation?: DocumentDataSourceActivationState
  /** Optional projection context forwarded to lower-level data-source binding projection. */
  readonly context?: PresentationDataSourceProjectionContext
  /** Presentation module that owns canonical data-source definitions. */
  readonly module: PresentationModuleDefinition | null
  /** Document profile whose scoped data sources should be projected. */
  readonly profile: DocumentProfileProjection | null
  /** Registry used to interpret concrete data-source binding kinds. */
  readonly registry: PresentationDataSourceBindingProjectionRegistry
  /** Optional role filter applied before activation and binding projection. */
  readonly roles?: readonly DocumentDataSourceRole[]
}

/**
 * Runtime state used to determine whether profile-scoped data sources are
 * active for the current document view.
 */
export interface DocumentDataSourceActivationState {
  readonly activeLayoutModeId?: string | null
  readonly activeProjectionId?: string | null
  readonly activeViewId?: string | null
  readonly routeParameters?: Readonly<Record<string, string | null | undefined>>
}

/**
 * Resolves the document profile referenced by a page host.
 *
 * Page hosts refer to workspaces; workspaces own one or more document profiles.
 * This helper follows that indirection and returns the selected profile, or
 * null when the page host does not identify a usable document profile.
 */
export function resolvePageHostDocumentProfile<TProfile extends DocumentProfileProjection>(
  module: { readonly Workspaces: readonly DocumentWorkspaceProjection[] },
  pageHost: Pick<PageHostDefinition, 'Workspace'>,
): TProfile | null {
  return resolveWorkspaceDocumentProfile(module, pageHost.Workspace ?? null)
}

/**
 * Resolves the active document profile for a workspace reference.
 *
 * Selection order is explicit workspace-ref profile id, workspace default
 * profile id, then the first profile declared on the workspace.
 */
export function resolveWorkspaceDocumentProfile<TProfile extends DocumentProfileProjection>(
  module: { readonly Workspaces: readonly DocumentWorkspaceProjection[] },
  workspaceRef: DocumentWorkspaceRefProjection | null,
): TProfile | null {
  if (!workspaceRef) {
    return null
  }

  const workspace = findDocumentWorkspace(module, workspaceRef.WorkspaceId)
  if (!workspace) {
    return null
  }

  const documentProfileId =
    workspaceRef.DocumentProfileId ??
    workspace.DefaultDocumentProfileId ??
    workspace.DocumentProfiles?.[0]?.Id ??
    null

  return documentProfileId
    ? ((workspace.DocumentProfiles?.find((profile) => profile.Id === documentProfileId) ??
        null) as TProfile | null)
    : null
}

/** Finds a document workspace by id from the presentation module workspace set. */
export function findDocumentWorkspace<TWorkspace extends DocumentWorkspaceProjection>(
  module: { readonly Workspaces: readonly DocumentWorkspaceProjection[] },
  workspaceId: string,
): TWorkspace | null {
  return (
    (module.Workspaces?.find((workspace) => workspace.Id === workspaceId) as
      | TWorkspace
      | undefined) ?? null
  )
}

/**
 * Finds a profile data source by semantic role and optional profile-local id.
 *
 * Role matching accepts generated enum values, numeric values, and generated
 * labels so callers are insulated from representation differences in emitted
 * presentation IR.
 */
export function findDocumentProfileDataSource(
  profile: DocumentProfileProjection | null,
  role: DocumentDataSourceRole,
  id?: string,
): DocumentProfileDataSourceProjection | null {
  return profile?.DataSources?.find(
    (dataSource) =>
      isDocumentDataSourceRole(dataSource.Role, role) && (!id || dataSource.Id === id),
  ) ?? null
}

/**
 * Returns profile data sources, optionally filtered by semantic role.
 *
 * This does not evaluate activation policy; use
 * projectDocumentProfileDataSourceBindings when the current view state matters.
 */
export function getDocumentProfileDataSources(
  profile: DocumentProfileProjection | null,
  roles?: readonly DocumentDataSourceRole[],
): readonly DocumentProfileDataSourceProjection[] {
  const dataSources = profile?.DataSources ?? []
  if (!roles?.length) {
    return dataSources
  }

  const roleSet = new Set(roles)
  return dataSources.filter((dataSource) =>
    Array.from(roleSet).some((role) => isDocumentDataSourceRole(dataSource.Role, role)),
  )
}

/** Returns data-source ids for the selected profile-scoped data-source roles. */
export function getDocumentProfileDataSourceIds(
  profile: DocumentProfileProjection | null,
  roles?: readonly DocumentDataSourceRole[],
): readonly string[] {
  return getDocumentProfileDataSources(profile, roles).map(
    (dataSource) => dataSource.DataSource.DataSourceId,
  )
}

/** Returns profile data sources marked as required by the backend profile. */
export function getRequiredDocumentProfileDataSources(
  profile: DocumentProfileProjection | null,
): readonly DocumentProfileDataSourceProjection[] {
  return profile?.DataSources?.filter((dataSource) => dataSource.IsRequired) ?? []
}

/**
 * Resolves route parameter names that can identify the profile's primary
 * document resource.
 *
 * Resource identity should come from the resource data-source contract when it
 * exists. Falling back to document identity keeps locally declared profiles
 * usable even before their resource endpoint is modeled as a parameterized
 * data source.
 */
export function resolveDocumentProfileResourceRouteParameterNames(
  module: Pick<PresentationModuleDefinition, 'DataSources'> | null,
  profile: DocumentProfileProjection | null,
): readonly string[] {
  const resourceDataSource = findDocumentProfileDataSource(
    profile,
    documentDataSourceRoles.resource,
  )
  const dataSource = module?.DataSources?.find(
    (candidate) => candidate.Id === resourceDataSource?.DataSource.DataSourceId,
  )
  const parameterNames = dataSource?.Parameters
    ?.map((parameter) => parameter.Name?.trim() ?? '')
    .filter((name) => name.length > 0) ?? []

  if (parameterNames.length > 0) {
    return parameterNames
  }

  const documentIdField = profile?.Document?.Identity?.DocumentIdField?.trim()
  return documentIdField ? [lowercaseFirstCharacter(documentIdField)] : []
}

/** Returns the source-address template declared for the profile document. */
export function getDocumentSourceAddressTemplate(
  profile: DocumentProfileProjection | null,
) {
  return profile?.Document?.Address?.Root ?? null
}

/** Projects the backend document kind into stable runtime identity metadata. */
export function projectDocumentKind(
  profile: DocumentProfileProjection | null,
): ProjectedDocumentKind | null {
  const value = profile?.Document?.Kind
  if (value === null || value === undefined) {
    return null
  }

  const label = resolveDocumentKindLabel(value)
  return {
    id: toKebabCase(label ?? String(value)),
    label: label ? formatPascalCaseLabel(label) : String(value),
    value,
  }
}

/**
 * Projects a backend document profile into the generic runtime shape consumed
 * by document-backed workspaces.
 *
 * Returns null when the profile is missing, unsupported, or lacks a primary
 * resource data source.
 */
export function projectDocumentWorkspaceProfile(
  profile: DocumentProfileProjection | null,
  options: ProjectDocumentWorkspaceProfileOptions = {},
): DocumentWorkspaceProfileProjection | null {
  const documentKind = projectDocumentKind(profile)
  const resourceDataSource = findDocumentProfileDataSource(
    profile,
    documentDataSourceRoles.resource,
  )
  if (!profile || !documentKind || !resourceDataSource) {
    return null
  }

  const metadataDataSource = findDocumentProfileDataSource(
    profile,
    documentDataSourceRoles.metadata,
  )
  const summaryDataSource = findDocumentProfileDataSource(
    profile,
    documentDataSourceRoles.summary,
  )

  return {
    dataSourceId: resourceDataSource.DataSource.DataSourceId,
    documentKind,
    editorPath: (resourceId) =>
      formatDocumentSourceAddress(profile, { Id: resourceId, id: resourceId }) ??
      formatFallbackDocumentSourceAddress(
        options.fallbackAddressScheme ?? 'document',
        documentKind,
        resourceId,
      ),
    label: profile.Name ?? documentKind.label,
    metadata: metadataDataSource
      ? { dataSourceId: metadataDataSource.DataSource.DataSourceId }
      : undefined,
    profile,
    profileId: profile.Id,
    resource: projectDocumentWorkspaceResource(profile, resourceDataSource),
    summary: summaryDataSource
      ? {
          dataSourceId: summaryDataSource.DataSource.DataSourceId,
          fieldPaths: projectDocumentSummaryFieldPaths(
            profile.MetricSources ?? [],
            summaryDataSource.DataSource.DataSourceId,
          ),
        }
      : undefined,
  }
}

function projectDocumentWorkspaceResource(
  profile: DocumentProfileProjection,
  resourceDataSource: DocumentProfileDataSourceProjection,
): DocumentWorkspaceResourceProjection {
  return {
    createdAtPath: firstAvailablePath('CreatedAtUtc', 'CreatedAt', 'createdAtUtc'),
    documentPath:
      profile.Document?.DataSource?.FieldPath ??
      resourceDataSource.DataSource.FieldPath ??
      'Document',
    idPath: profile.Document?.Identity?.DocumentIdField?.trim() || 'Id',
    titlePath: firstAvailablePath('Name', 'Title', 'Label'),
    updatedAtPath: firstAvailablePath('UpdatedAtUtc', 'UpdatedAt', 'updatedAtUtc'),
    versionPath: profile.Document?.Identity?.VersionField ?? 'EntityVersion',
  }
}

/**
 * Creates the editor identity key used to decide whether local editor state
 * still belongs to the currently loaded resource revision.
 */
export function createDocumentResourceEditorKey(
  projection: Pick<DocumentWorkspaceProfileProjection, 'documentKind' | 'profileId' | 'resource'>,
  resource: ProjectedDocumentResource,
) {
  const resourceId = readDocumentResourceId(projection, resource)
  const version = readDocumentResourceVersion(projection, resource)
  const updatedAt = readDocumentResourceTextPath(resource, projection.resource.updatedAtPath)
  const createdAt = readDocumentResourceTextPath(resource, projection.resource.createdAtPath)

  return [
    projection.profileId,
    projection.documentKind.id,
    resourceId ?? 'unknown',
    version ?? 'unversioned',
    updatedAt ?? createdAt,
  ].join(':')
}

/** Reads the semantic document id from an opaque resource using profile IR. */
export function readDocumentResourceId(
  projection: Pick<DocumentWorkspaceProfileProjection, 'resource'>,
  resource: unknown,
) {
  return readDocumentResourceTextPath(resource, projection.resource.idPath)
}

/** Reads the JSON document payload from an opaque resource using profile IR. */
export function readDocumentResourceDocument(
  projection: Pick<DocumentWorkspaceProfileProjection, 'resource'>,
  resource: unknown,
) {
  return readPresentationFieldValue(resource, projection.resource.documentPath)
}

/** Reads a human-facing resource title from an opaque resource using profile IR. */
export function readDocumentResourceTitle(
  projection: Pick<DocumentWorkspaceProfileProjection, 'label' | 'resource'>,
  resource: unknown,
) {
  const title = readDocumentResourceTextPath(resource, projection.resource.titlePath)
  return title ?? projection.label
}

/** Reads the resource version from an opaque resource using profile IR. */
export function readDocumentResourceVersion(
  projection: Pick<DocumentWorkspaceProfileProjection, 'resource'>,
  resource: unknown,
) {
  const version = readPresentationFieldValue(resource, projection.resource.versionPath)
  return typeof version === 'number' || typeof version === 'string'
    ? version
    : null
}

function readDocumentResourceTextPath(
  resource: unknown,
  path: string | null | undefined,
) {
  const value = readPresentationFieldValue(resource, path)
  return typeof value === 'string' && value.trim().length > 0
    ? value.trim()
    : null
}

function firstAvailablePath(...paths: readonly string[]) {
  return paths.find((path) => path.trim().length > 0) ?? null
}

/**
 * Tests document kind values across generated numeric and backend string enum
 * representations.
 */
export function isDocumentKind(
  value: string | number | null | undefined,
  expected: string | number,
) {
  const expectedLabel = resolveDocumentKindLabel(expected)?.toLocaleLowerCase()
  const normalizedValue = String(value).toLocaleLowerCase()
  return (
    value === expected ||
    normalizedValue === String(expected).toLocaleLowerCase() ||
    normalizedValue === expectedLabel
  )
}

/**
 * Formats a profile document source address from a declared address template.
 *
 * Template placeholders are resolved from exact property names first and then
 * lower-camel-case names. Returns null when the profile has no template or any
 * placeholder value is missing.
 */
export function formatDocumentSourceAddress(
  profile: DocumentProfileProjection | null,
  values: Readonly<Record<string, string | number | boolean | null | undefined>>,
) {
  const template = getDocumentSourceAddressTemplate(profile)
  if (!template) {
    return null
  }

  let hasMissingValue = false
  const address = template.replace(/\{([^}]+)\}/g, (match, key: string) => {
    const value = values[key] ?? values[lowercaseFirstCharacter(key)]
    if (value === null || value === undefined) {
      hasMissingValue = true
      return match
    }

    return encodeURIComponent(String(value))
  })

  return hasMissingValue ? null : address
}

/**
 * Projects active profile-scoped data sources into runtime binding
 * descriptors.
 *
 * Profile data sources are first filtered by role and activation policy. The
 * remaining ids are then interpreted through the generic presentation
 * data-source projection registry, with local-state fallback definitions for
 * data sources that are only scoped by the document profile.
 */
export function projectDocumentProfileDataSourceBindings({
  activation,
  context,
  module,
  profile,
  registry,
  roles,
}: ProjectDocumentProfileDataSourceBindingsOptions): readonly PresentationDataSourceBinding[] {
  const profileDataSources = getDocumentProfileDataSources(profile, roles).filter(
    (dataSource) => isDocumentProfileDataSourceActive(dataSource, activation),
  )

  return projectPresentationDataSourceBindings({
    context,
    dataSourceDefinitions: profileDataSources.map(
      createDocumentProfileDataSourceFallbackDefinition,
    ),
    dataSourceIds: profileDataSources.map(
      (dataSource) => dataSource.DataSource.DataSourceId,
    ),
    module,
    registry,
  })
}

/**
 * Evaluates whether a profile data source is active for the current document
 * runtime state.
 *
 * A source is active when all required route parameters are present and, when
 * projection/view/layout constraints are declared, at least one active semantic
 * id matches the policy.
 */
export function isDocumentProfileDataSourceActive(
  dataSource: DocumentProfileDataSourceProjection,
  state: DocumentDataSourceActivationState | undefined,
) {
  const policy = dataSource.Activation
  if (!policy) {
    return true
  }

  if (
    !policy.RequiredRouteParameterNames.every((parameterName) =>
      hasRouteParameterValue(state?.routeParameters, parameterName),
    )
  ) {
    return false
  }

  const hasStateKey =
    policy.ProjectionIds.length > 0 ||
    policy.ViewIds.length > 0 ||
    policy.LayoutModeIds.length > 0

  if (!hasStateKey) {
    return true
  }

  return (
    includesSemanticId(policy.ProjectionIds, state?.activeProjectionId) ||
    includesSemanticId(policy.ViewIds, state?.activeViewId) ||
    includesSemanticId(policy.LayoutModeIds, state?.activeLayoutModeId)
  )
}

function createDocumentProfileDataSourceFallbackDefinition(
  dataSource: DocumentProfileDataSourceProjection,
): DataSourceDefinition {
  return {
    Annotations: [],
    Binding: null,
    Cache: null,
    DefaultSort: [],
    Id: dataSource.DataSource.DataSourceId,
    Invalidation: null,
    Kind: dataSourceKinds.localState,
    Name: dataSource.Description ?? dataSource.Id,
    Parameters: [],
    Residency: null,
    ResultShape: 'unknown',
  }
}

function formatFallbackDocumentSourceAddress(
  scheme: string,
  documentKind: ProjectedDocumentKind,
  resourceId: string,
) {
  return `${scheme}://${documentKind.id}/${encodeURIComponent(resourceId)}.json`
}

function projectDocumentSummaryFieldPaths(
  metricSources: DocumentProfileProjection['MetricSources'],
  dataSourceId: string,
): readonly string[] {
  return Array.from(
    new Set(
      (metricSources ?? [])
        .filter((metricSource) => metricSource.Source.DataSourceId === dataSourceId)
        .map((metricSource) => metricSource.Source.FieldPath ?? '')
        .filter(Boolean),
    ),
  )
}

function lowercaseFirstCharacter(value: string) {
  return value.length > 0
    ? `${value.slice(0, 1).toLocaleLowerCase()}${value.slice(1)}`
    : value
}

function resolveDocumentKindLabel(value: string | number) {
  if (typeof value === 'number') {
    return documentKindLabels[value as keyof typeof documentKindLabels] ?? null
  }

  const normalizedValue = value.toLocaleLowerCase()
  const match = Object.values(documentKindLabels).find(
    (label) => label.toLocaleLowerCase() === normalizedValue,
  )
  return match ?? value
}

function formatPascalCaseLabel(value: string) {
  return value.replace(/([a-z0-9])([A-Z])/g, '$1 $2')
}

function toKebabCase(value: string) {
  return formatPascalCaseLabel(value)
    .trim()
    .replace(/[^a-zA-Z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .toLocaleLowerCase()
}

function hasRouteParameterValue(
  routeParameters: Readonly<Record<string, string | null | undefined>> | undefined,
  parameterName: string,
) {
  const value = routeParameters?.[parameterName]
  return typeof value === 'string' && value.length > 0
}

function includesSemanticId(ids: readonly string[], value: string | null | undefined) {
  if (!value) {
    return false
  }

  return ids.some((id) => id.toLocaleLowerCase() === value.toLocaleLowerCase())
}

function isDocumentDataSourceRole(
  value: DocumentDataSourceRole | string | null | undefined,
  role: DocumentDataSourceRole,
) {
  const roleLabel = documentDataSourceRoleLabels[role]?.toLocaleLowerCase()
  const normalizedValue = String(value).toLocaleLowerCase()
  return (
    value === role ||
    normalizedValue === String(role).toLocaleLowerCase() ||
    normalizedValue === roleLabel
  )
}
