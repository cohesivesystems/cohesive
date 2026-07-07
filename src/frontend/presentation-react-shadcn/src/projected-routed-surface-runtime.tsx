import { useCallback, useMemo, useState, type Dispatch, type SetStateAction } from 'react'
import { useLocation, useNavigate } from 'react-router'

import {
  applyPresentationPaginationToRequest,
  applyPresentationActionResultPolicyStateWrites,
  applyPresentationActionResultStateWrites,
  createInitialPresentationPaginationState,
  createNextPresentationPaginationState,
  projectPresentationDataSourceBindings,
  createPresentationCollectionPaginationBindings,
  createPresentationPaginationSearch,
  createPreviousPresentationPaginationState,
  defaultPresentationComponentSet,
  getPresentationPaginationPageIndex,
  getPresentationSurfaceDataSourceIds,
  getPresentationSurfaceViewTree,
  normalizePresentationPaginationState,
  projectPresentationCollectionPaginationDiagnostics,
  projectPresentationDataSourceCoverageDiagnostics,
  readPresentationPaginationStateFromSearch,
  resolvePresentationSurface,
  type NavigationTarget,
  type ActionDefinition,
  type ActionResultStateWriteDefinition,
  type PresentationDataSourceBindingProjectionRegistry,
  type PresentationActionResultStateValues,
  type PresentationDataSourceTargetInterpretation,
  type PresentationCollectionPaginationRequestMap,
  type PresentationModuleDefinition,
  type PresentationNavigationRuntime,
  type PresentationPaginationBinding,
  type PresentationPaginationRuntime,
  type PresentationPaginationState,
  type PresentationProjectionDiagnostic,
  type PresentationQueryFormStateMap,
  type PresentationSurface,
  type QueryFormDefinition,
  type ViewDefinition,
} from '@cohesivesystems/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  createPresentationQueryFormRuntime,
  resolvePresentationQueryFormStateAdapters,
  usePresentationModule,
  usePresentationNavigationRuntime,
  usePresentationQueryFormStateEntries,
  usePresentationQueryFormStateMap,
  useRegisterPresentationProjectionDiagnostics,
  type PresentationQueryFormStateAdapter,
  type PresentationQueryFormStateAdapterRegistry,
  type PresentationQueryFormStateEntry,
  type PresentationRendererRegistry,
  type RouteParameterValues,
} from '@cohesivesystems/presentation-react'
import type { PresentationDesignSystem } from '@cohesivesystems/presentation-tailwind'
import {
  projectPresentationRoutedSurfaceLayoutDiagnostics,
  resolvePresentationRoutedSurfaceLayout,
} from '@cohesivesystems/presentation-tailwind'
import {
  PresentationRoutedSurfaceHost,
} from './presentation-routed-surface-host'

export interface ProjectedRoutedSurfaceRouteContext {
  readonly collectionPagination?: Readonly<Record<string, PresentationPaginationRuntime>>
  readonly queryFormRuntimes?: Readonly<Record<string, unknown>>
  readonly routeParameters?: RouteParameterValues
}

export type ProjectedRoutedSurfacePaginationRequestMap =
  PresentationCollectionPaginationRequestMap

export interface ProjectedRoutedSurfaceRuntimeProps<TContext = ProjectedRoutedSurfaceRouteContext> {
  readonly className?: string
  readonly componentSet?: string
  readonly componentSystem: PresentationComponentSystem
  readonly createCollectionPaginationBindings?: (
    context: ProjectedRoutedSurfaceCollectionPaginationBindingContext,
  ) => readonly PresentationPaginationBinding[]
  readonly createDataSourceBindingRegistry: (
    context: ProjectedRoutedSurfaceRuntimeContext<TContext>,
  ) => PresentationDataSourceBindingProjectionRegistry
  readonly createPaginationRequests?: (
    bindings: readonly PresentationPaginationBinding[],
    states: Readonly<Record<string, PresentationPaginationState>>,
  ) => ProjectedRoutedSurfacePaginationRequestMap
  readonly createRendererRegistry: (
    context: ProjectedRoutedSurfaceRendererRegistryContext<TContext>,
  ) => PresentationRendererRegistry<TContext>
  readonly createRouteContext?: (
    context: ProjectedRoutedSurfaceRuntimeContext<TContext>,
  ) => TContext
  readonly dataSourceTargetInterpretation: PresentationDataSourceTargetInterpretation
  /** Fallback collection page size used when the presentation IR has no default. */
  readonly defaultCollectionPageSize?: number
  readonly designSystem: PresentationDesignSystem
  readonly navigationTarget: NavigationTarget
  readonly projectCollectionPaginationDiagnostics?: (
    context: ProjectedRoutedSurfaceCollectionPaginationDiagnosticsContext,
  ) => readonly PresentationProjectionDiagnostic[]
  readonly queryFormStateAdapterRegistry: PresentationQueryFormStateAdapterRegistry
  readonly resolveContentClassName?: (
    context: ProjectedRoutedSurfaceRuntimeContext<TContext>,
  ) => string | undefined
  readonly routeParameters: RouteParameterValues
}

export interface ProjectedRoutedSurfaceCollectionPaginationBindingContext {
  readonly componentSystem: PresentationComponentSystem
  readonly dataSourceIds: readonly string[]
  /** Fallback collection page size used when the presentation IR has no default. */
  readonly defaultPageSize?: number
  readonly designSystem: PresentationDesignSystem
  readonly module: PresentationModuleDefinition | null
  readonly views: readonly ViewDefinition[]
}

export interface ProjectedRoutedSurfaceCollectionPaginationDiagnosticsContext {
  readonly bindings: readonly PresentationPaginationBinding[]
  readonly componentSystem: PresentationComponentSystem
  readonly designSystem: PresentationDesignSystem
  readonly module: PresentationModuleDefinition | null
  readonly sourceId: string
  readonly views: readonly ViewDefinition[]
}

export interface ProjectedRoutedSurfaceRuntimeContext<TContext = ProjectedRoutedSurfaceRouteContext> {
  readonly actionResultState: ProjectedRoutedSurfaceActionResultStateRuntime
  readonly collectionPagination: Readonly<Record<string, PresentationPaginationRuntime>>
  readonly collectionPaginationBindings: readonly PresentationPaginationBinding[]
  readonly collectionPaginationStates: Readonly<Record<string, PresentationPaginationState>>
  readonly componentSystem: PresentationComponentSystem
  readonly dataSourceIds: readonly string[]
  readonly designSystem: PresentationDesignSystem
  readonly module: PresentationModuleDefinition | null
  readonly navigationRuntime: PresentationNavigationRuntime
  readonly navigationTarget: NavigationTarget
  readonly paginationRequestsByDataSourceId: ProjectedRoutedSurfacePaginationRequestMap
  readonly queryFormRuntimes: Readonly<Record<string, unknown>>
  readonly queryFormStates: PresentationQueryFormStateMap
  readonly routeContext?: TContext
  readonly routeParameters: RouteParameterValues
  readonly surface: PresentationSurface | null
  readonly surfaceViews: readonly ViewDefinition[]
}

export interface ProjectedRoutedSurfaceActionResultStateRuntime {
  readonly applyActionResult: (
    action: Pick<ActionDefinition, 'Result'> | null | undefined,
    result: unknown,
  ) => void
  readonly applyStateWrites: (
    writes: readonly ActionResultStateWriteDefinition[] | null | undefined,
    result: unknown,
  ) => void
  readonly values: PresentationActionResultStateValues
}

export interface ProjectedRoutedSurfaceRendererRegistryContext<TContext = ProjectedRoutedSurfaceRouteContext>
  extends ProjectedRoutedSurfaceRuntimeContext<TContext> {
  readonly routeContext: TContext
}

/**
 * Standard React runtime for a routed semantic presentation surface.
 *
 * The backend presentation module declares the route target, surface tree,
 * query forms, and data sources. This runtime handles the target-independent
 * mechanics of projecting those constructs into React state, URL state,
 * pagination windows, data-source bindings, and diagnostics. Product code
 * supplies target interpretations for auth, concrete data execution, and
 * renderer registries.
 */
export function ProjectedRoutedSurfaceRuntime<TContext = ProjectedRoutedSurfaceRouteContext>({
  className,
  componentSet = defaultPresentationComponentSet,
  componentSystem,
  createCollectionPaginationBindings = createPresentationCollectionPaginationBindings,
  createDataSourceBindingRegistry,
  createPaginationRequests = createProjectedRoutedSurfacePaginationRequests,
  createRendererRegistry,
  createRouteContext,
  dataSourceTargetInterpretation,
  defaultCollectionPageSize,
  designSystem,
  navigationTarget,
  projectCollectionPaginationDiagnostics = projectPresentationCollectionPaginationDiagnostics,
  queryFormStateAdapterRegistry,
  resolveContentClassName,
  routeParameters,
}: ProjectedRoutedSurfaceRuntimeProps<TContext>) {
  const navigationRuntime = usePresentationNavigationRuntime()
  const module = usePresentationModule()
  const location = useLocation()
  const navigate = useNavigate()
  const queryFormStates = usePresentationQueryFormStateMap()
  const queryFormStateEntries = usePresentationQueryFormStateEntries()
  const [actionResultStateValues, setActionResultStateValues] = useState<PresentationActionResultStateValues>({})
  const applyActionResult = useCallback(
    (
      action: Pick<ActionDefinition, 'Result'> | null | undefined,
      result: unknown,
    ) => {
      setActionResultStateValues((current) =>
        applyPresentationActionResultPolicyStateWrites({
          policy: action?.Result,
          result,
          state: current,
        }),
      )
    },
    [],
  )
  const applyStateWrites = useCallback(
    (
      writes: readonly ActionResultStateWriteDefinition[] | null | undefined,
      result: unknown,
    ) => {
      setActionResultStateValues((current) =>
        applyPresentationActionResultStateWrites({
          result,
          state: current,
          writes,
        }),
      )
    },
    [],
  )
  const actionResultState = useMemo(
    () => ({
      applyActionResult,
      applyStateWrites,
      values: actionResultStateValues,
    }) satisfies ProjectedRoutedSurfaceActionResultStateRuntime,
    [
      actionResultStateValues,
      applyActionResult,
      applyStateWrites,
    ],
  )
  const queryFormStateAdapters = useMemo(
    () => resolvePresentationQueryFormStateAdapters(module, queryFormStateAdapterRegistry),
    [module, queryFormStateAdapterRegistry],
  )
  const surface = useMemo(
    () => module ? resolvePresentationSurface(module, navigationTarget) : null,
    [module, navigationTarget],
  )
  const dataSourceIds = useMemo(
    () => getPresentationSurfaceDataSourceIds(module, surface),
    [module, surface],
  )
  const surfaceViews = useMemo(
    () => getPresentationSurfaceViewTree(module, surface),
    [module, surface],
  )
  const collectionPaginationBindings = useMemo(
    () => createCollectionPaginationBindings({
      componentSystem,
      dataSourceIds,
      defaultPageSize: defaultCollectionPageSize,
      designSystem,
      module,
      views: surfaceViews,
    }),
    [
      createCollectionPaginationBindings,
      componentSystem,
      dataSourceIds,
      defaultCollectionPageSize,
      designSystem,
      module,
      surfaceViews,
    ],
  )
  const collectionPaginationStates = useMemo(
    () =>
      Object.fromEntries(
        collectionPaginationBindings.map((binding) => [
          binding.dataSourceId,
          readPresentationPaginationStateFromSearch(location.search, binding),
        ]),
      ) as Readonly<Record<string, PresentationPaginationState>>,
    [collectionPaginationBindings, location.search],
  )
  const paginationRequestsByDataSourceId = useMemo(
    () => createPaginationRequests(
      collectionPaginationBindings,
      collectionPaginationStates,
    ),
    [
      collectionPaginationBindings,
      collectionPaginationStates,
      createPaginationRequests,
    ],
  )
  const commitRoutedSurfaceSearch = useCallback(
    (
      search: string,
      options: { readonly replace?: boolean } = {},
    ) => {
      const normalizedSearch = search
        ? search.startsWith('?')
          ? search
          : `?${search}`
        : ''
      const nextHref = `${location.pathname}${normalizedSearch}${location.hash}`
      const currentHref = `${location.pathname}${location.search}${location.hash}`
      if (nextHref !== currentHref) {
        void navigate(nextHref, {
          flushSync: true,
          replace: options.replace ?? false,
        })
      }
    },
    [
      location.hash,
      location.pathname,
      location.search,
      navigate,
    ],
  )
  const commitCollectionPagination = useCallback(
    (
      binding: PresentationPaginationBinding,
      nextState: PresentationPaginationState,
      options: { readonly replace?: boolean } = {},
    ) => {
      const search = createPresentationPaginationSearch(
        location.search,
        binding,
        normalizePresentationPaginationState(nextState),
      )
      commitRoutedSurfaceSearch(search, options)
    },
    [
      commitRoutedSurfaceSearch,
      location.search,
    ],
  )
  const queryFormRuntimes = useMemo(
    () =>
      createQueryFormRuntimes({
        adapters: queryFormStateAdapters,
        collectionPaginationBindings,
        commitRoutedSurfaceSearch,
        entries: queryFormStateEntries,
        locationSearch: location.search,
        module,
      }),
    [
      collectionPaginationBindings,
      commitRoutedSurfaceSearch,
      location.search,
      module,
      queryFormStateAdapters,
      queryFormStateEntries,
    ],
  )
  const collectionPagination = useMemo(
    () =>
      Object.fromEntries(
        collectionPaginationBindings.map((binding) => {
          const state = collectionPaginationStates[binding.dataSourceId] ??
            createInitialPresentationPaginationState(binding)
          const pageIndex = getPresentationPaginationPageIndex(state)
          return [
            binding.dataSourceId,
            {
              binding,
              canGoPreviousPage: pageIndex > 0,
              dataSourceId: binding.dataSourceId,
              goToFirstPage: () =>
                commitCollectionPagination(
                  binding,
                  createInitialPresentationPaginationState(binding),
                ),
              goToNextPage: (response?: unknown) =>
                commitCollectionPagination(
                  binding,
                  createNextPresentationPaginationState(binding, state, response),
                ),
              goToPreviousPage: () =>
                commitCollectionPagination(
                  binding,
                  createPreviousPresentationPaginationState(state),
                ),
              pageIndex,
              pageSize: state.pageSize,
              state,
            } satisfies PresentationPaginationRuntime,
          ]
        }),
      ) as Readonly<Record<string, PresentationPaginationRuntime>>,
    [
      collectionPaginationBindings,
      collectionPaginationStates,
      commitCollectionPagination,
    ],
  )
  const runtimeContext = useMemo(
    () => ({
      actionResultState,
      collectionPagination,
      collectionPaginationBindings,
      collectionPaginationStates,
      componentSystem,
      dataSourceIds,
      designSystem,
      module,
      navigationRuntime,
      navigationTarget,
      paginationRequestsByDataSourceId,
      queryFormRuntimes,
      queryFormStates,
      routeParameters,
      surface,
      surfaceViews,
    }) satisfies ProjectedRoutedSurfaceRuntimeContext<TContext>,
    [
      actionResultState,
      collectionPagination,
      collectionPaginationBindings,
      collectionPaginationStates,
      componentSystem,
      dataSourceIds,
      designSystem,
      module,
      navigationRuntime,
      navigationTarget,
      paginationRequestsByDataSourceId,
      queryFormRuntimes,
      queryFormStates,
      routeParameters,
      surface,
      surfaceViews,
    ],
  )
  const routeContext = useMemo(
    () =>
      createRouteContext?.(runtimeContext) ??
      ({
        collectionPagination,
        queryFormRuntimes,
        routeParameters,
      } satisfies ProjectedRoutedSurfaceRouteContext) as TContext,
    [
      collectionPagination,
      createRouteContext,
      queryFormRuntimes,
      routeParameters,
      runtimeContext,
    ],
  )
  const runtimeContextWithRouteContext = useMemo(
    () => ({
      ...runtimeContext,
      routeContext,
    }),
    [routeContext, runtimeContext],
  )
  const dataSourceBindingRegistry = useMemo(
    () => createDataSourceBindingRegistry(runtimeContextWithRouteContext),
    [createDataSourceBindingRegistry, runtimeContextWithRouteContext],
  )
  const bindings = useMemo(
    () =>
      projectPresentationDataSourceBindings({
        context: {
          queryFormStates,
          routeParameters,
          workspaceId: surface?.workspaceRef?.WorkspaceId ?? null,
        },
        dataSourceIds,
        module,
        registry: dataSourceBindingRegistry,
      }),
    [
      dataSourceBindingRegistry,
      dataSourceIds,
      module,
      queryFormStates,
      routeParameters,
      surface,
    ],
  )
  const dataSourceDiagnosticsSourceId = `routed-surface-data-sources:${navigationTarget.route.Id}`
  const dataSourceCoverageDiagnostics = useMemo(
    () =>
      projectPresentationDataSourceCoverageDiagnostics({
        bindings,
        dataSourceIds,
        module,
        routeParameters,
        sourceId: dataSourceDiagnosticsSourceId,
        targetInterpretation: dataSourceTargetInterpretation,
      }),
    [
      bindings,
      dataSourceDiagnosticsSourceId,
      dataSourceIds,
      dataSourceTargetInterpretation,
      module,
      routeParameters,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    dataSourceDiagnosticsSourceId,
    dataSourceCoverageDiagnostics,
  )

  const paginationDiagnosticsSourceId =
    `routed-surface-collection-pagination:${navigationTarget.route.Id}`
  const collectionPaginationDiagnostics = useMemo(
    () =>
      projectCollectionPaginationDiagnostics?.({
        bindings: collectionPaginationBindings,
        componentSystem,
        designSystem,
        module,
        sourceId: paginationDiagnosticsSourceId,
        views: surfaceViews,
      }) ?? [],
    [
      collectionPaginationBindings,
      componentSystem,
      designSystem,
      module,
      paginationDiagnosticsSourceId,
      projectCollectionPaginationDiagnostics,
      surfaceViews,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    paginationDiagnosticsSourceId,
    collectionPaginationDiagnostics,
  )

  const layoutDiagnosticsSourceId =
    `routed-surface-layout:${navigationTarget.route.Id}`
  const routedSurfaceLayoutDiagnostics = useMemo(
    () =>
      projectPresentationRoutedSurfaceLayoutDiagnostics({
        designSystem,
        sourceId: layoutDiagnosticsSourceId,
        surface,
      }),
    [
      designSystem,
      layoutDiagnosticsSourceId,
      surface,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    layoutDiagnosticsSourceId,
    routedSurfaceLayoutDiagnostics,
  )

  const rendererRegistry = useMemo(
    () => createRendererRegistry(runtimeContextWithRouteContext),
    [createRendererRegistry, runtimeContextWithRouteContext],
  )
  const routedSurfaceLayout = useMemo(
    () => resolvePresentationRoutedSurfaceLayout({ designSystem, surface }),
    [designSystem, surface],
  )
  const contentClassName = useMemo(
    () =>
      resolveContentClassName?.(runtimeContextWithRouteContext) ??
      routedSurfaceLayout.contentClassName,
    [resolveContentClassName, routedSurfaceLayout, runtimeContextWithRouteContext],
  )

  return (
    <PresentationRoutedSurfaceHost
      bindings={bindings}
      className={className ?? routedSurfaceLayout.className}
      componentSet={componentSet}
      contentClassName={contentClassName}
      context={routeContext}
      rendererRegistry={rendererRegistry}
      surface={surface}
    />
  )
}

export function createProjectedRoutedSurfacePaginationRequests(
  bindings: readonly PresentationPaginationBinding[],
  states: Readonly<Record<string, PresentationPaginationState>>,
): ProjectedRoutedSurfacePaginationRequestMap {
  return Object.fromEntries(
    bindings.map((binding) => [
      binding.dataSourceId,
      applyPresentationPaginationToRequest(
        {},
        binding,
        states[binding.dataSourceId],
      ),
    ]),
  )
}

interface CreateQueryFormRuntimesOptions {
  readonly adapters: readonly PresentationQueryFormStateAdapter[]
  readonly collectionPaginationBindings: readonly PresentationPaginationBinding[]
  readonly commitRoutedSurfaceSearch: (
    search: string,
    options?: { readonly replace?: boolean },
  ) => void
  readonly entries: Readonly<Record<string, PresentationQueryFormStateEntry>>
  readonly locationSearch: string
  readonly module: PresentationModuleDefinition | null
}

function createQueryFormRuntimes({
  adapters,
  collectionPaginationBindings,
  commitRoutedSurfaceSearch,
  entries,
  locationSearch,
  module,
}: CreateQueryFormRuntimesOptions) {
  return Object.fromEntries(
    adapters.flatMap((adapter) => {
      const entry = entries[adapter.queryFormId]
      if (!entry?.queryForm) {
        return []
      }

      const queryForm = entry.queryForm
      const runtime = createPresentationQueryFormRuntime({
        applyValue: ({ choiceValuesByFieldId, value }) => {
          entry.setAppliedValue(value)
          const resetSearch = createQueryFormPaginationResetSearch(
            locationSearch,
            queryForm,
            collectionPaginationBindings,
          )
          const nextSearch = adapter.createAppliedSearch?.({
            choiceValuesByFieldId,
            inputForm: entry.inputForm,
            module,
            queryForm,
            search: resetSearch,
            value,
          }) ?? resetSearch
          commitRoutedSurfaceSearch(nextSearch)
        },
        createDefaultValue: ({ choiceValuesByFieldId }) =>
          adapter.createDefaultValue({
            choiceValuesByFieldId,
            inputForm: entry.inputForm,
            module,
            queryForm,
            search: locationSearch,
          }),
        normalizeValue: adapter.normalizeValue
          ? ({ choiceValuesByFieldId, value }) =>
              adapter.normalizeValue?.({
                choiceValuesByFieldId,
                inputForm: entry.inputForm,
                module,
                queryForm,
                search: locationSearch,
                value,
              }) ?? value
          : undefined,
        queryForm,
        setDraftValue: entry.setDraftValue as Dispatch<SetStateAction<object>>,
        value: coerceObjectValue(entry.draftValue),
      })

      return runtime ? [[queryForm.Id, runtime] as const] : []
    }),
  )
}

function createQueryFormPaginationResetSearch(
  search: string,
  queryForm: QueryFormDefinition,
  paginationBindings: readonly PresentationPaginationBinding[],
) {
  const targetDataSourceIds = getQueryFormTargetDataSourceIds(queryForm)
  return paginationBindings.reduce(
    (nextSearch, binding) =>
      targetDataSourceIds.has(binding.dataSourceId)
        ? createPresentationPaginationSearch(
            nextSearch,
            binding,
            createInitialPresentationPaginationState(binding),
          )
        : nextSearch,
    search,
  )
}

function getQueryFormTargetDataSourceIds(queryForm: QueryFormDefinition) {
  const state = queryForm.Target.State
  return new Set(
    [
      state.AppliedDataSourceId,
      state.ResultDataSourceId,
      ...state.SynchronizedDataSourceIds,
    ].filter((dataSourceId): dataSourceId is string => Boolean(dataSourceId)),
  )
}

function coerceObjectValue(value: unknown) {
  return value && typeof value === 'object' ? value as object : {}
}
