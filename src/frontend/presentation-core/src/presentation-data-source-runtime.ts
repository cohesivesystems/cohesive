import type {
  DataSourceDefinition,
  ViewDefinition,
} from './module'
import {
  readObjectPath,
} from './object-path'
import {
  createQueryActivityState,
  type ProjectedActivityState,
} from './projected-activity-state-model'
import {
  getPresentationViewProjectedDataSourceIds,
} from './presentation-semantics'
import {
  resolveCollectionChromePrimaryDataSourceId,
} from './collection-chrome-runtime'

export {
  readObjectPath,
  readObjectProperty,
} from './object-path'

export interface PresentationDataSourceState<TData = unknown> {
  readonly blockedLabel?: string
  readonly data?: TData
  readonly definition?: DataSourceDefinition | null
  readonly emptyMessage?: string
  readonly error?: unknown
  readonly isBlocked?: boolean
  readonly isFetching?: boolean
  readonly isPending?: boolean
  readonly pendingLabel?: string
  readonly refetch?: () => Promise<unknown> | unknown
}

export type PresentationDataSourceStateMap = Readonly<Record<string, PresentationDataSourceState>>

/**
 * Resolves presentation data sources from their semantic identifiers without
 * flattening the state map into an ad hoc component data context.
 */
export interface PresentationDataSourceResolver {
  /**
   * Bound runtime state keyed by semantic presentation data source id.
   */
  readonly dataSources: PresentationDataSourceStateMap

  /**
   * Reads the raw data payload for a data source.
   */
  readonly read: <TData = unknown>(dataSourceId: string) => TData | undefined

  /**
   * Reads a nested value from a data source payload using a dot-separated field path.
   */
  readonly readPath: (dataSourceId: string, fieldPath?: string | null) => unknown

  /**
   * Resolves the full runtime state for a data source, including activity and refetch metadata.
   */
  readonly resolve: <TData = unknown>(dataSourceId: string) => PresentationDataSourceState<TData> | undefined

  /**
   * Resolves all data source states declared by a view subject and explicit data source ids.
   */
  readonly resolveViewDataSources: (view: Pick<ViewDefinition, 'Collection' | 'DataSourceIds' | 'Subject'>) => readonly PresentationDataSourceState[]

  /**
   * Resolves the primary data source state for a view, preferring the view subject source.
   */
  readonly resolveViewPrimary: <TData = unknown>(view: Pick<ViewDefinition, 'Collection' | 'DataSourceIds' | 'Subject'>) => PresentationDataSourceState<TData> | undefined
}

/**
 * Creates a stable semantic resolver over a bound presentation data source map.
 */
export function createPresentationDataSourceResolver(dataSources: PresentationDataSourceStateMap): PresentationDataSourceResolver {
  return {
    dataSources,
    read<TData = unknown>(dataSourceId: string) {
      return dataSources[dataSourceId]?.data as TData | undefined
    },
    readPath(dataSourceId: string, fieldPath?: string | null) {
      return readObjectPath(dataSources[dataSourceId]?.data, fieldPath ?? '')
    },
    resolve<TData = unknown>(dataSourceId: string) {
      return dataSources[dataSourceId] as PresentationDataSourceState<TData> | undefined
    },
    resolveViewDataSources(view: Pick<ViewDefinition, 'Collection' | 'DataSourceIds' | 'Subject'>) {
      return resolvePresentationViewDataSourceIds(view)
        .map((dataSourceId) => dataSources[dataSourceId])
        .filter((state): state is PresentationDataSourceState => Boolean(state))
    },
    resolveViewPrimary<TData = unknown>(
      view: Pick<ViewDefinition, 'Collection' | 'DataSourceIds' | 'Subject'>,
    ) {
      const dataSourceId = resolvePresentationViewPrimaryDataSourceId(view)
      return dataSourceId
        ? (dataSources[dataSourceId] as PresentationDataSourceState<TData> | undefined)
        : undefined
    },
  }
}

export function createPresentationViewActivityState({
  blockedLabel = 'Attach an authenticated context to load this view.',
  dataSourceResolver,
  pendingLabel = 'Loading view data...',
  view,
}: {
  readonly blockedLabel?: string
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly pendingLabel?: string
  readonly view: ViewDefinition
}): ProjectedActivityState {
  const states = dataSourceResolver.resolveViewDataSources(view)
  const blockedState = states.find((state) => state.isBlocked)
  const pendingState = states.find((state) => state.isPending)
  const errorState = states.find((state) => state.error)

  return createQueryActivityState({
    blockedLabel: blockedState?.blockedLabel ?? blockedLabel,
    error: errorState?.error,
    isBlocked: Boolean(blockedState),
    isPending: Boolean(pendingState),
    pendingLabel: pendingState?.pendingLabel ?? pendingLabel,
  })
}

export function isPresentationViewFetching(
  view: ViewDefinition,
  dataSourceResolver: PresentationDataSourceResolver,
) {
  return resolvePresentationViewDataSourceIds(view).some(
    (dataSourceId) => dataSourceResolver.resolve(dataSourceId)?.isFetching,
  )
}

export function readPresentationDataSourceItems<TItem extends object = object>(
  state: PresentationDataSourceState | null | undefined,
): readonly TItem[] {
  const data = state?.data
  if (Array.isArray(data)) {
    return data as readonly TItem[]
  }

  if (data && typeof data === 'object') {
    const items = (data as { readonly Items?: unknown }).Items
    if (Array.isArray(items)) {
      return items as readonly TItem[]
    }
  }

  return []
}

export async function refreshPresentationDataSources(
  dataSourceResolver: PresentationDataSourceResolver,
  dataSourceIds: readonly string[],
) {
  const uniqueDataSourceIds = Array.from(new Set(dataSourceIds))
  await Promise.all(
    uniqueDataSourceIds.map((dataSourceId) =>
      Promise.resolve(dataSourceResolver.resolve(dataSourceId)?.refetch?.()),
    ),
  )
  return undefined
}

export function resolvePresentationViewDataSourceIds(
  view: Pick<ViewDefinition, 'Collection' | 'DataSourceIds' | 'Subject'>,
) {
  return getPresentationViewProjectedDataSourceIds(view)
}

export function resolvePresentationViewPrimaryDataSourceId(
  view: Pick<ViewDefinition, 'Collection' | 'DataSourceIds' | 'Subject'>,
) {
  return view.Subject.DataSourceId ??
    resolvePresentationViewCollectionPrimarySlotDataSourceId(view) ??
    view.DataSourceIds[0] ??
    null
}

function resolvePresentationViewCollectionPrimarySlotDataSourceId(
  view: Pick<ViewDefinition, 'Collection'>,
) {
  return resolveCollectionChromePrimaryDataSourceId(view.Collection)
}
