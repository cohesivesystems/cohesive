import { useQuery, type QueryKey } from '@tanstack/react-query'
import {
  useCallback,
  useLayoutEffect,
  useMemo,
  useState,
  type PropsWithChildren,
} from 'react'

import {
  formatPresentationScopeSelectionQuerySuffix,
  normalizeScopeIds,
  type PresentationScopeAccess,
  type PresentationScopeRequestStore,
} from '@cohesivesystems/presentation-core'
import {
  PresentationScopeSelectionContext,
  type PresentationScopeSelectionContextValue,
} from './scope-selection-context'

interface SelectedScopeOverride {
  readonly identityKey: string
  readonly scopeId: string
}

interface SelectedScopeIdsOverride {
  readonly identityKey: string
  readonly scopeIds: readonly string[]
}

/**
 * Options for the generic presentation scope-selection provider.
 */
export interface PresentationScopeSelectionProviderProps<
  TScopeContext,
  TScopeMetadata = unknown,
> extends PropsWithChildren {
  /** Whether scope context may be loaded. */
  readonly enabled: boolean

  /** Identity/session partition key used for cache and persisted selection state. */
  readonly identityKey: string

  /** Query key used to load the source scope context. */
  readonly queryKey: QueryKey

  /** Loads the source scope context. */
  readonly loadScopeContext: () => Promise<TScopeContext>

  /** Projects accessible scopes from the source context. */
  readonly getScopes: (
    scopeContext: TScopeContext
  ) => readonly PresentationScopeAccess<TScopeMetadata>[]

  /** Resolves the default scope id from the source context and projected scopes. */
  readonly getDefaultScopeId?: (
    scopeContext: TScopeContext,
    scopes: readonly PresentationScopeAccess<TScopeMetadata>[]
  ) => string | null

  /** Prefix used for persisted local selection keys. */
  readonly storageKeyPrefix: string

  /** Optional request-scope store to synchronize with the primary selected scope. */
  readonly requestStore?: PresentationScopeRequestStore

  /** Label used for single-scope query/cache suffixes. */
  readonly singleScopeQueryLabel?: string

  /** Label used for multi-scope query/cache suffixes. */
  readonly multipleScopesQueryLabel?: string
}

/**
 * Provides generic authorized-scope selection state for presentation hosts.
 */
export function PresentationScopeSelectionProvider<
  TScopeContext,
  TScopeMetadata = unknown,
>({
  children,
  enabled,
  getDefaultScopeId,
  getScopes,
  identityKey,
  loadScopeContext,
  multipleScopesQueryLabel,
  queryKey,
  requestStore,
  singleScopeQueryLabel,
  storageKeyPrefix,
}: PresentationScopeSelectionProviderProps<TScopeContext, TScopeMetadata>) {
  const [selectedScopeOverride, setSelectedScopeOverride] =
    useState<SelectedScopeOverride | null>(null)
  const [selectedScopeIdsOverrides, setSelectedScopeIdsOverrides] =
    useState<Record<string, SelectedScopeIdsOverride>>({})
  const scopeContextQuery = useQuery<TScopeContext>({
    enabled,
    queryFn: loadScopeContext,
    queryKey,
    retry: false,
  })
  const scopeContext = scopeContextQuery.data ?? null
  const scopes = useMemo<readonly PresentationScopeAccess<TScopeMetadata>[]>(
    () => scopeContext ? getScopes(scopeContext) : [],
    [getScopes, scopeContext],
  )
  const scopeIdSet = useMemo(
    () => new Set(scopes.map((scope) => scope.id)),
    [scopes],
  )
  const selectedScopeId = useMemo(() => {
    if (!enabled || !scopeContext) {
      return null
    }

    if (
      selectedScopeOverride?.identityKey === identityKey &&
      isAllowedScope(selectedScopeOverride.scopeId, scopeIdSet)
    ) {
      return selectedScopeOverride.scopeId
    }

    const storedScopeId = readStoredScopeId(storageKeyPrefix, identityKey, 'single')
    if (isAllowedScope(storedScopeId, scopeIdSet)) {
      return storedScopeId
    }

    return resolveDefaultScopeId(scopeContext, scopes, getDefaultScopeId)
  }, [
    enabled,
    getDefaultScopeId,
    identityKey,
    scopeContext,
    scopeIdSet,
    scopes,
    selectedScopeOverride,
    storageKeyPrefix,
  ])
  const singleScopeQuerySuffix = formatPresentationScopeSelectionQuerySuffix(
    {
      mode: 'single',
      scopeId: selectedScopeId,
    },
    {
      multipleScopesLabel: multipleScopesQueryLabel,
      singleScopeLabel: singleScopeQueryLabel,
    },
  )

  useLayoutEffect(() => {
    requestStore?.setSelection({
      mode: 'single',
      scopeId: selectedScopeId,
    })
  }, [requestStore, selectedScopeId])

  const setSelectedScopeId = useCallback(
    (scopeId: string) => {
      if (!scopeIdSet.has(scopeId)) {
        return
      }

      setSelectedScopeOverride({ identityKey, scopeId })
      writeStoredScopeId(storageKeyPrefix, identityKey, 'single', scopeId)
    },
    [identityKey, scopeIdSet, storageKeyPrefix],
  )
  const getSelectedScopeIds = useCallback(
    (purpose: string, fallbackScopeIds: readonly string[] = []) => {
      const scopeIds =
        selectedScopeIdsOverrides[purpose]?.identityKey === identityKey
          ? selectedScopeIdsOverrides[purpose].scopeIds
          : readStoredScopeIds(storageKeyPrefix, identityKey, purpose)

      return normalizeSelectedScopeIds(scopeIds, scopeIdSet, fallbackScopeIds)
    },
    [
      identityKey,
      scopeIdSet,
      selectedScopeIdsOverrides,
      storageKeyPrefix,
    ],
  )
  const setSelectedScopeIds = useCallback(
    (purpose: string, scopeIds: readonly string[]) => {
      const fallbackScopeIds = selectedScopeId ? [selectedScopeId] : []
      const nextScopeIds = normalizeSelectedScopeIds(scopeIds, scopeIdSet, fallbackScopeIds)
      setSelectedScopeIdsOverrides((current) => ({
        ...current,
        [purpose]: {
          identityKey,
          scopeIds: nextScopeIds,
        },
      }))
      writeStoredScopeIds(storageKeyPrefix, identityKey, purpose, nextScopeIds)
    },
    [identityKey, scopeIdSet, selectedScopeId, storageKeyPrefix],
  )
  const value = useMemo<PresentationScopeSelectionContextValue<TScopeContext, TScopeMetadata>>(
    () => ({
      getSelectedScopeIds,
      isLoading: scopeContextQuery.isLoading,
      scopes,
      scopeContext,
      selectedScopeId,
      setSelectedScopeId,
      setSelectedScopeIds,
      singleScopeQuerySuffix,
    }),
    [
      getSelectedScopeIds,
      scopeContext,
      scopeContextQuery.isLoading,
      scopes,
      selectedScopeId,
      setSelectedScopeId,
      setSelectedScopeIds,
      singleScopeQuerySuffix,
    ],
  )

  return (
    <PresentationScopeSelectionContext.Provider value={value}>
      {children}
    </PresentationScopeSelectionContext.Provider>
  )
}

function resolveDefaultScopeId<TScopeContext, TScopeMetadata>(
  scopeContext: TScopeContext,
  scopes: readonly PresentationScopeAccess<TScopeMetadata>[],
  getDefaultScopeId:
    | ((
      scopeContext: TScopeContext,
      scopes: readonly PresentationScopeAccess<TScopeMetadata>[]
    ) => string | null)
    | undefined,
) {
  const scopeIds = new Set(scopes.map((scope) => scope.id))
  const defaultScopeId = getDefaultScopeId?.(scopeContext, scopes)
  if (defaultScopeId && scopeIds.has(defaultScopeId)) {
    return defaultScopeId
  }

  return scopes.find((scope) => scope.isDefault)?.id ?? scopes[0]?.id ?? null
}

function normalizeSelectedScopeIds(
  scopeIds: readonly string[],
  allowedScopeIds: ReadonlySet<string>,
  fallbackScopeIds: readonly string[],
) {
  const selected = normalizeScopeIds(scopeIds)
    .filter((scopeId) => allowedScopeIds.has(scopeId))
  if (selected.length > 0) {
    return selected
  }

  return normalizeScopeIds(fallbackScopeIds)
    .filter((scopeId) => allowedScopeIds.has(scopeId))
}

function isAllowedScope(
  scopeId: string | null,
  allowedScopeIds: ReadonlySet<string>,
): scopeId is string {
  return scopeId !== null && allowedScopeIds.has(scopeId)
}

function readStoredScopeId(storageKeyPrefix: string, identityKey: string, purpose: string) {
  if (typeof window === 'undefined') {
    return null
  }

  const value = window.localStorage.getItem(createStorageKey(storageKeyPrefix, identityKey, purpose))
  return value && value.trim().length > 0 ? value : null
}

function writeStoredScopeId(
  storageKeyPrefix: string,
  identityKey: string,
  purpose: string,
  scopeId: string,
) {
  if (typeof window === 'undefined') {
    return
  }

  window.localStorage.setItem(createStorageKey(storageKeyPrefix, identityKey, purpose), scopeId)
}

function readStoredScopeIds(storageKeyPrefix: string, identityKey: string, purpose: string) {
  if (typeof window === 'undefined') {
    return []
  }

  try {
    const value = window.localStorage.getItem(createStorageKey(storageKeyPrefix, identityKey, purpose))
    return value ? JSON.parse(value) as string[] : []
  } catch {
    return []
  }
}

function writeStoredScopeIds(
  storageKeyPrefix: string,
  identityKey: string,
  purpose: string,
  scopeIds: readonly string[],
) {
  if (typeof window === 'undefined') {
    return
  }

  window.localStorage.setItem(
    createStorageKey(storageKeyPrefix, identityKey, purpose),
    JSON.stringify(scopeIds),
  )
}

function createStorageKey(storageKeyPrefix: string, identityKey: string, purpose: string) {
  return `${storageKeyPrefix}.${purpose}.${identityKey}`
}
