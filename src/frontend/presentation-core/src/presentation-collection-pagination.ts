import {
  findPresentationDataSource,
  type DataSourceDefinition,
  type PresentationModuleDefinition,
  type ViewDefinition,
} from './module'
import {
  createDataSourcePaginationParameterPrefix,
  inferPresentationPaginationBinding,
  type PresentationPaginationBinding,
} from './presentation-pagination'
import {
  createPresentationProjectionDiagnostic,
  type PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'
import {
  createProjectedCollectionChromeRuntime,
  isProjectedCollectionPaginationEnabled,
  isProjectedCollectionPaginationFooterEnabled,
  resolveProjectedCollectionPaginationDataSourceId,
} from './projected-collection-runtime'
import { findPresentationQueryFormForResultDataSource } from './query-form-lowering'
import {
  dataSourceKindLabels,
  dataSourceKinds,
  type DataSourceKind,
} from '@cohesivesystems/presentation-contracts'

export type PresentationCollectionPaginationRequestMap = Readonly<Record<string, object>>

/**
 * Fallback page size used when neither the data source nor a synchronized query
 * form declares a default page size.
 */
export const defaultPresentationCollectionPageSize = 10

/**
 * Options for projecting collection pagination bindings from a presentation
 * module.
 */
export interface CreatePresentationCollectionPaginationBindingsOptions {
  /** Data source ids that may require collection pagination bindings. */
  readonly dataSourceIds: readonly string[]

  /** Fallback page size used when no IR-level default is declared. */
  readonly defaultPageSize?: number

  /** Presentation module containing data-source and query-form definitions. */
  readonly module: PresentationModuleDefinition | null

  /** Optional views used to restrict pagination projection to enabled collections. */
  readonly views?: readonly Pick<ViewDefinition, 'Collection'>[]
}

export function createPresentationCollectionPaginationBindings({
  dataSourceIds,
  defaultPageSize = defaultPresentationCollectionPageSize,
  module,
  views,
}: CreatePresentationCollectionPaginationBindingsOptions): readonly PresentationPaginationBinding[] {
  const fallbackPageSize =
    readPositiveInteger(defaultPageSize) ?? defaultPresentationCollectionPageSize

  return resolveCollectionPaginationDataSourceIds(dataSourceIds, views).flatMap((dataSourceId) => {
    const dataSource = findPresentationDataSource(module, dataSourceId)
    if (!dataSource || !isCollectionQueryDataSource(dataSource)) {
      return []
    }

    const binding = inferPresentationCollectionPaginationBinding(
      dataSource,
      module,
      fallbackPageSize,
    )
    return binding ? [binding] : []
  })
}

function resolveCollectionPaginationDataSourceIds(
  dataSourceIds: readonly string[],
  views: readonly Pick<ViewDefinition, 'Collection'>[] | undefined,
) {
  const collectionViews = views?.filter((view) => view.Collection) ?? []
  if (collectionViews.length === 0) {
    return Array.from(new Set(dataSourceIds))
  }

  return Array.from(
    new Set(
      collectionViews.flatMap((view) => {
        const collection = view.Collection
        if (!collection || !isProjectedCollectionPaginationEnabled(collection)) {
          return []
        }

        const dataSourceId = resolveProjectedCollectionPaginationDataSourceId(collection)
        return dataSourceId ? [dataSourceId] : []
      }),
    ),
  )
}

export function projectPresentationCollectionPaginationDiagnostics({
  bindings,
  footerRendererBound = true,
  module,
  sourceId,
  views,
}: {
  readonly bindings: readonly PresentationPaginationBinding[]
  readonly footerRendererBound?: boolean
  readonly module: PresentationModuleDefinition | null
  readonly sourceId: string
  readonly views?: readonly Pick<ViewDefinition, 'Collection' | 'Id' | 'Name'>[]
}): readonly PresentationProjectionDiagnostic[] {
  const collectionPaginationViews = resolveEnabledCollectionPaginationViews(views)
  if (collectionPaginationViews.length === 0) {
    return bindings.map((binding) => {
      const dataSource = findPresentationDataSource(module, binding.dataSourceId)

      return createCollectionPaginationRuntimeDiagnostic({
        binding,
        dataSource,
        sourceId,
      })
    })
  }

  return collectionPaginationViews.flatMap((view) =>
    projectCollectionViewPaginationDiagnostics({
      bindings,
      footerRendererBound,
      module,
      sourceId,
      view,
    }))
}

function projectCollectionViewPaginationDiagnostics({
  bindings,
  footerRendererBound,
  module,
  sourceId,
  view,
}: {
  readonly bindings: readonly PresentationPaginationBinding[]
  readonly footerRendererBound: boolean
  readonly module: PresentationModuleDefinition | null
  readonly sourceId: string
  readonly view: Pick<ViewDefinition, 'Collection' | 'Id' | 'Name'>
}): readonly PresentationProjectionDiagnostic[] {
  const collection = view.Collection
  if (!collection || !isProjectedCollectionPaginationEnabled(collection)) {
    return []
  }

  const chrome = createProjectedCollectionChromeRuntime(collection)
  const paginationSlot = chrome.paginationSlot
  const dataSourceId = resolveProjectedCollectionPaginationDataSourceId(collection)
  const diagnostics: PresentationProjectionDiagnostic[] = []

  if (!dataSourceId) {
    diagnostics.push(createPresentationProjectionDiagnostic({
      category: 'missing-definition',
      details: {
        collectionChromeSlotId: paginationSlot?.Id ?? null,
        paginationPlacement: paginationSlot?.Placement ?? null,
        viewId: view.Id,
      },
      id: `collection-pagination.${view.Id}.missing-slot-data-source`,
      interpretation: {
        status: 'unbound',
        target: 'projected-collection-runtime.pagination',
      },
      message:
        `Collection view '${view.Name}' declares pagination chrome, ` +
        'but the pagination slot does not declare a data source.',
      severity: 'error',
      source: sourceId,
      subject: {
        id: view.Id,
        kind: 'view',
        name: view.Name,
      },
      suggestedNextStep:
        'Add the paginated collection data source id to the Collection.Chrome pagination slot.',
    }))

    return diagnostics
  }

  const dataSource = findPresentationDataSource(module, dataSourceId)
  const binding = bindings.find((candidate) => candidate.dataSourceId === dataSourceId)

  if (!dataSource) {
    diagnostics.push(createPresentationProjectionDiagnostic({
      category: 'missing-definition',
      details: {
        collectionChromeSlotId: paginationSlot?.Id ?? null,
        dataSourceId,
        paginationPlacement: paginationSlot?.Placement ?? null,
        viewId: view.Id,
      },
      id: `collection-pagination.${view.Id}.${dataSourceId}.missing-data-source`,
      interpretation: {
        status: 'unbound',
        target: 'projected-collection-runtime.pagination',
      },
      message:
        `Collection view '${view.Name}' enables pagination through data source ` +
        `'${dataSourceId}', but that data source is not present in the presentation module.`,
      severity: 'error',
      source: sourceId,
      subject: {
        id: view.Id,
        kind: 'view',
        name: view.Name,
      },
      suggestedNextStep:
        'Declare the pagination data source in the presentation module or update the Collection.Chrome pagination slot.',
    }))
  } else if (!binding) {
    diagnostics.push(createPresentationProjectionDiagnostic({
      category: 'missing-binding',
      details: {
        collectionChromeSlotId: paginationSlot?.Id ?? null,
        dataSourceId,
        dataSourceKind: dataSource.Kind,
        paginationPlacement: paginationSlot?.Placement ?? null,
        viewId: view.Id,
      },
      id: `collection-pagination.${view.Id}.${dataSourceId}.missing-runtime-binding`,
      interpretation: {
        status: 'unbound',
        target: 'projected-collection-runtime.pagination.window',
      },
      message:
        `Collection view '${view.Name}' enables pagination for '${dataSource.Name}', ` +
        'but no frontend pagination runtime binding was created for that data source.',
      severity: 'warning',
      source: sourceId,
      subject: {
        id: view.Id,
        kind: 'view',
        name: view.Name,
      },
      suggestedNextStep:
        'Ensure the data source is a collection-query data source with declared or inferable pagination request and response bindings.',
    }))
  } else {
    diagnostics.push(createCollectionPaginationRuntimeDiagnostic({
      binding,
      dataSource,
      paginationSlotId: paginationSlot?.Id ?? null,
      sourceId,
      view,
    }))
  }

  if (isProjectedCollectionPaginationFooterEnabled(collection) && !footerRendererBound) {
    diagnostics.push(createPresentationProjectionDiagnostic({
      category: 'missing-binding',
      details: {
        collectionChromeSlotId: paginationSlot?.Id ?? null,
        dataSourceId,
        paginationPlacement: paginationSlot?.Placement ?? null,
        viewId: view.Id,
      },
      id: `collection-pagination.${view.Id}.missing-footer-renderer`,
      interpretation: {
        status: 'unbound',
        target: 'collection-pagination-footer-chrome',
      },
      message:
        `Collection view '${view.Name}' requests footer pagination chrome, ` +
        'but no collection footer renderer is bound.',
      severity: 'warning',
      source: sourceId,
      subject: {
        id: view.Id,
        kind: 'view',
        name: view.Name,
      },
      suggestedNextStep:
        'Bind a collection footer renderer that projects collectionRuntime.pagination.window, or change the collection pagination placement.',
    }))
  }

  return diagnostics
}

function createCollectionPaginationRuntimeDiagnostic({
  binding,
  dataSource,
  paginationSlotId = null,
  sourceId,
  view,
}: {
  readonly binding: PresentationPaginationBinding
  readonly dataSource: DataSourceDefinition | null
  readonly paginationSlotId?: string | null
  readonly sourceId: string
  readonly view?: Pick<ViewDefinition, 'Id' | 'Name'> | null
}) {
  return createPresentationProjectionDiagnostic({
    category: 'local-interpretation',
    details: {
      collectionChromeSlotId: paginationSlotId,
      dataSourceId: binding.dataSourceId,
      defaultPageSize: binding.defaultPageSize,
      paginationKind: binding.kind,
      urlEnabled: binding.url.enabled,
      urlParameterPrefix: binding.url.parameterPrefix,
      viewId: view?.Id ?? null,
    },
    id: view
      ? `collection-pagination.${view.Id}.${binding.dataSourceId}.runtime-interpretation`
      : `collection-pagination.${binding.dataSourceId}.runtime-interpretation`,
    interpretation: {
      status: 'locally-interpreted',
      target: 'projected-collection-runtime.pagination.window',
    },
    message:
      `Collection pagination for '${dataSource?.Name ?? binding.dataSourceId}' ` +
      `is interpreted through Collection.Chrome by ProjectedCollectionRuntime as ${binding.kind} windowing.`,
    severity: 'info',
    source: sourceId,
    subject: view
      ? {
          id: view.Id,
          kind: 'view',
          name: view.Name,
        }
      : {
          id: binding.dataSourceId,
          kind: 'data-source',
          name: dataSource?.Name,
        },
    suggestedNextStep:
      'Extend collection IR with page-size controls, column selection, and row selection chips as the grid component system grows.',
  })
}

function resolveEnabledCollectionPaginationViews(
  views: readonly Pick<ViewDefinition, 'Collection' | 'Id' | 'Name'>[] | undefined,
) {
  return (views ?? []).filter((view) =>
    isProjectedCollectionPaginationEnabled(view.Collection))
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
    String(value) === camelLabel
  )
}

function inferPresentationCollectionPaginationBinding(
  dataSource: DataSourceDefinition,
  module: PresentationModuleDefinition | null,
  fallbackPageSize: number,
): PresentationPaginationBinding | null {
  const urlPolicy = resolveCollectionPaginationUrlPolicy(dataSource, module)
  return inferPresentationPaginationBinding({
    dataSource,
    defaultPageSize: resolveCollectionPageSize(dataSource, module, fallbackPageSize),
    parameterPrefix: urlPolicy.parameterPrefix,
    useUrl: urlPolicy.enabled,
  })
}

function resolveCollectionPageSize(
  dataSource: DataSourceDefinition,
  module: PresentationModuleDefinition | null,
  fallbackPageSize: number,
) {
  const queryFormDefaultLimit = findPresentationQueryFormForResultDataSource(
    module,
    dataSource.Id,
  )?.Target.Result.DefaultLimit
  return readPositiveInteger(queryFormDefaultLimit) ?? fallbackPageSize
}

function resolveCollectionPaginationUrlPolicy(
  dataSource: DataSourceDefinition,
  module: PresentationModuleDefinition | null,
) {
  const dataSourceUrlPolicy = dataSource.Query?.Pagination?.Url
  if (dataSourceUrlPolicy) {
    return {
      enabled: dataSourceUrlPolicy.IsEnabled,
      parameterPrefix:
        dataSourceUrlPolicy.ParameterPrefix ??
        createDataSourcePaginationParameterPrefix(dataSource.Id),
    }
  }

  const queryForm = findPresentationQueryFormForResultDataSource(module, dataSource.Id)
  const queryFormUrlPolicy = queryForm?.Target.State.Url
  if (queryFormUrlPolicy?.IsEnabled && queryFormUrlPolicy.IncludePagination) {
    return {
      enabled: true,
      parameterPrefix:
        queryFormUrlPolicy.ParameterPrefix ??
        createDataSourcePaginationParameterPrefix(dataSource.Id),
    }
  }

  return {
    enabled: false,
    parameterPrefix: createDataSourcePaginationParameterPrefix(dataSource.Id),
  }
}

function readPositiveInteger(value: unknown) {
  return typeof value === 'number' && Number.isInteger(value) && value > 0
    ? value
    : null
}
