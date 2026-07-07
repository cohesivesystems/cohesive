import {
  normalizePresentationScopeRequestSelection,
  type PresentationScopeRequestSelection,
} from './scope-selection'

/**
 * Generated API scope policy metadata projected into frontend runtimes.
 */
export interface ApiScopePolicyMetadata {
  readonly kind: string
  readonly cardinality: 'single' | 'multiple'
  readonly binding: 'ambient' | 'header' | 'query' | 'route' | 'body' | 'resource'
  readonly access: 'requireSelected' | 'filterToAccessible' | 'validateAccessible'
  readonly singleScopeParameterName?: string
  readonly multipleScopesParameterName?: string
  readonly scopeModeParameterName?: string
  readonly resourceParameterName?: string
  readonly resourceDerivation?: {
    readonly strategy: string
    readonly format?: string
    readonly scopeField?: string
  }
  readonly allowDefaultScope: boolean
}

/**
 * Minimal HTTP client contract used by generated API clients.
 */
export type ApiRequestHttpClient = (
  path: string,
  init: RequestInit,
) => Promise<unknown>

/**
 * Projected request after scope policies have been applied.
 */
export interface ScopedApiRequest {
  readonly path: string
  readonly init: RequestInit
}

/**
 * Applies generated API scope policies to a request using the current ambient selection.
 */
export function projectApiScopeRequest(
  path: string,
  init: RequestInit,
  selection: PresentationScopeRequestSelection,
  policies: readonly ApiScopePolicyMetadata[],
): ScopedApiRequest {
  const normalizedSelection = normalizePresentationScopeRequestSelection(selection)
  let projectedPath = path
  let projectedInit = init

  for (const policy of policies) {
    if (policy.binding === 'header') {
      const next = applyHeaderScopePolicy(
        projectedPath,
        projectedInit,
        normalizedSelection,
        policy,
      )
      projectedPath = next.path
      projectedInit = next.init
      continue
    }

    if (policy.binding === 'query') {
      const next = applyQueryScopePolicy(
        projectedPath,
        projectedInit,
        normalizedSelection,
        policy,
      )
      projectedPath = next.path
      projectedInit = next.init
    }
  }

  return {
    init: projectedInit,
    path: projectedPath,
  }
}

/**
 * Wraps an HTTP client so scope metadata is applied at request time.
 */
export function createScopedApiHttpClient<TClient extends ApiRequestHttpClient>(
  http: TClient,
  {
    getSelection,
    policies,
  }: {
    readonly getSelection: () => PresentationScopeRequestSelection
    readonly policies: readonly ApiScopePolicyMetadata[]
  },
): TClient {
  return (async (path, init) => {
    const projected = projectApiScopeRequest(path, init, getSelection(), policies)
    return await http(projected.path, projected.init)
  }) as TClient
}

function applyHeaderScopePolicy(
  path: string,
  init: RequestInit,
  selection: PresentationScopeRequestSelection,
  policy: ApiScopePolicyMetadata,
): ScopedApiRequest {
  if (policy.cardinality !== 'single' || !policy.singleScopeParameterName) {
    return { init, path }
  }

  if (selection.mode !== 'single' || selection.scopeId === null) {
    return { init, path }
  }

  const headers = new Headers(init.headers)
  if (headers.has(policy.singleScopeParameterName)) {
    return { init, path }
  }

  headers.set(policy.singleScopeParameterName, selection.scopeId)
  return {
    init: {
      ...init,
      headers,
    },
    path,
  }
}

function applyQueryScopePolicy(
  path: string,
  init: RequestInit,
  selection: PresentationScopeRequestSelection,
  policy: ApiScopePolicyMetadata,
): ScopedApiRequest {
  if (policy.cardinality !== 'multiple' || !policy.multipleScopesParameterName) {
    return { init, path }
  }

  const scopeIds = readRequestedScopeIds(selection)
  if (scopeIds.length === 0) {
    return { init, path }
  }

  const url = new URL(path, scopeProjectionBaseUrl)
  if (url.searchParams.has(policy.multipleScopesParameterName)) {
    return { init, path }
  }

  for (const scopeId of scopeIds) {
    url.searchParams.append(policy.multipleScopesParameterName, scopeId)
  }

  return {
    init,
    path: formatProjectedPath(path, url),
  }
}

function readRequestedScopeIds(selection: PresentationScopeRequestSelection) {
  switch (selection.mode) {
    case 'single':
      return selection.scopeId ? [selection.scopeId] : []
    case 'multiple':
      return [...selection.scopeIds]
    case 'all':
    case 'default':
      return []
  }
}

function formatProjectedPath(originalPath: string, url: URL) {
  return isAbsoluteUrl(originalPath)
    ? url.toString()
    : `${url.pathname}${url.search}${url.hash}`
}

function isAbsoluteUrl(value: string) {
  return /^[a-zA-Z][a-zA-Z\d+.-]*:/.test(value)
}

const scopeProjectionBaseUrl = 'https://cohesive.local'
