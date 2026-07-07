/**
 * Request-selected authorization scope for API calls made by a presentation host.
 */
export type PresentationScopeRequestSelection =
  | { readonly mode: 'default' }
  | { readonly mode: 'single'; readonly scopeId: string | null }
  | { readonly mode: 'multiple'; readonly scopeIds: readonly string[] }
  | { readonly mode: 'all' }

/**
 * Scope visible to an authenticated principal.
 */
export interface PresentationScopeAccess<TMetadata = unknown> {
  /** Stable scope id. */
  readonly id: string

  /** Human-facing scope label. */
  readonly label: string

  /** Optional semantic scope kind, for example tenant, workspace, or organization. */
  readonly kind?: string | null

  /** Capability ids granted inside this scope. */
  readonly capabilities?: readonly string[]

  /** Whether this scope is the principal's default scope. */
  readonly isDefault?: boolean

  /** Host-specific source object for adapters that need to preserve more detail. */
  readonly metadata?: TMetadata
}

/**
 * Header names and formatting used to project selected scopes into API requests.
 */
export interface PresentationScopeRequestHeaderOptions {
  /** Header carrying one selected scope id. */
  readonly singleScopeHeaderName: string

  /** Header carrying comma-separated selected scope ids. */
  readonly multipleScopesHeaderName: string

  /** Header carrying the requested selection mode. */
  readonly scopeModeHeaderName: string

  /** Header value used for multi-scope requests. */
  readonly multipleModeHeaderValue?: string

  /** Header value used for all-accessible-scope requests. */
  readonly allModeHeaderValue?: string
}

/**
 * Label options for stable query/cache suffixes.
 */
export interface PresentationScopeSelectionQuerySuffixOptions {
  /** Label used for single-scope suffixes. */
  readonly singleScopeLabel?: string

  /** Label used for multi-scope suffixes. */
  readonly multipleScopesLabel?: string
}

/**
 * Mutable request-scope store used by host HTTP clients.
 */
export interface PresentationScopeRequestStore {
  /** Stores the current request-scope selection. */
  readonly setSelection: (selection: PresentationScopeRequestSelection) => void

  /** Reads the current normalized request-scope selection. */
  readonly getSelection: () => PresentationScopeRequestSelection

  /** Returns request headers for the current selection. */
  readonly getHeaders: () => Readonly<Record<string, string>> | undefined

  /** Returns a stable query/cache suffix for a selection. */
  readonly formatQuerySuffix: (selection: PresentationScopeRequestSelection) => string

  /** Returns a normalized copy of a selection. */
  readonly normalizeSelection: (selection: PresentationScopeRequestSelection) => PresentationScopeRequestSelection
}

/**
 * Creates a host request-scope store that can project selected scopes into headers.
 */
export function createPresentationScopeRequestStore({
  headerOptions,
  querySuffixOptions,
}: {
  readonly headerOptions: PresentationScopeRequestHeaderOptions
  readonly querySuffixOptions?: PresentationScopeSelectionQuerySuffixOptions
}): PresentationScopeRequestStore {
  let currentSelection: PresentationScopeRequestSelection = { mode: 'default' }

  return {
    setSelection(selection) {
      currentSelection = normalizePresentationScopeRequestSelection(selection)
    },
    getSelection() {
      return currentSelection
    },
    getHeaders() {
      return createPresentationScopeRequestHeaders(currentSelection, headerOptions)
    },
    formatQuerySuffix(selection) {
      return formatPresentationScopeSelectionQuerySuffix(selection, querySuffixOptions)
    },
    normalizeSelection: normalizePresentationScopeRequestSelection,
  }
}

/**
 * Returns a normalized copy of a request-scope selection.
 */
export function normalizePresentationScopeRequestSelection(
  selection: PresentationScopeRequestSelection,
): PresentationScopeRequestSelection {
  switch (selection.mode) {
    case 'single':
      return {
        mode: 'single',
        scopeId: normalizeScopeId(selection.scopeId),
      }
    case 'multiple':
      return {
        mode: 'multiple',
        scopeIds: normalizeScopeIds(selection.scopeIds),
      }
    case 'all':
    case 'default':
      return selection
  }
}

/**
 * Returns request headers for a selection.
 */
export function createPresentationScopeRequestHeaders(
  selection: PresentationScopeRequestSelection,
  options: PresentationScopeRequestHeaderOptions,
): Readonly<Record<string, string>> | undefined {
  const normalized = normalizePresentationScopeRequestSelection(selection)
  switch (normalized.mode) {
    case 'single':
      return normalized.scopeId
        ? { [options.singleScopeHeaderName]: normalized.scopeId }
        : undefined
    case 'multiple':
      return normalized.scopeIds.length > 0
        ? {
            [options.multipleScopesHeaderName]: normalized.scopeIds.join(','),
            [options.scopeModeHeaderName]: options.multipleModeHeaderValue ?? 'multiple',
          }
        : undefined
    case 'all':
      return { [options.scopeModeHeaderName]: options.allModeHeaderValue ?? 'all' }
    case 'default':
      return undefined
  }
}

/**
 * Returns a stable query/cache suffix for a selection.
 */
export function formatPresentationScopeSelectionQuerySuffix(
  selection: PresentationScopeRequestSelection,
  options: PresentationScopeSelectionQuerySuffixOptions = {},
) {
  const singleScopeLabel = options.singleScopeLabel ?? 'scope'
  const multipleScopesLabel = options.multipleScopesLabel ?? 'scopes'
  const normalized = normalizePresentationScopeRequestSelection(selection)
  switch (normalized.mode) {
    case 'single':
      return `${singleScopeLabel}:${normalized.scopeId ?? 'default'}`
    case 'multiple':
      return `${multipleScopesLabel}:${normalized.scopeIds.join(',') || 'none'}`
    case 'all':
      return `${multipleScopesLabel}:all`
    case 'default':
      return `${singleScopeLabel}:default`
  }
}

/**
 * Returns normalized unique scope ids in stable order.
 */
export function normalizeScopeIds(scopeIds: readonly string[]) {
  return Array.from(
    new Set(
      scopeIds
        .map(normalizeScopeId)
        .filter((scopeId): scopeId is string => scopeId !== null),
    ),
  ).sort((left, right) => left.localeCompare(right))
}

function normalizeScopeId(scopeId: string | null | undefined) {
  const trimmed = scopeId?.trim()
  return trimmed && trimmed.length > 0 ? trimmed : null
}
