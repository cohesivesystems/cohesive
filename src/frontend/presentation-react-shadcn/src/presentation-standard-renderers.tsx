import type { ReactNode } from 'react'

import {
  createPresentationViewActivityState,
  createPresentationTestAttributes,
  createRelationQueryInputFormTarget,
  defaultPresentationComponentSet,
  findPresentationQueryForm,
  findPresentationInputForm,
  findPresentationInputFormForView,
  findPresentationQueryFormForView,
  getRegionViewIds,
  isViewChromeSlotKind,
  readPresentationDataSourceItems,
  resolveCollectionChromeSlotRenderer,
  type CollectionChromeSlotRendererRegistry,
  type CollectionChromeSlotDefinition,
  type PresentationActionRuntimeBinding,
  type PresentationDataSourceState,
  type ProjectedCollectionActionExecutionContext,
  type ProjectedCollectionPaginationInput,
  type ProjectedCollectionRuntime,
  type ViewChromeSlotDefinition,
  type ViewDefinition,
  type ViewRegionDefinition,
} from '@cohesivesystems/presentation-core'
import {
  renderStandardCollectionChromeSlot,
} from './standard-collection-chrome-slot-renderers'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  renderStandardViewChromeSlot,
} from './standard-view-chrome-slot-renderers'
import type {
  PresentationDesignSystem,
} from '@cohesivesystems/presentation-tailwind'
import type {
  ProjectedCollectionChromeSlotRenderContext,
  ProjectedCollectionDetailFieldRenderContext,
  ProjectedFieldRenderContext,
} from './projected-collection-view'
import {
  mergePresentationRendererRegistries,
  type PresentationRendererRegistry,
  type PresentationViewRenderContext,
  type PresentationViewRenderer,
} from '@cohesivesystems/presentation-react'
import {
  ProjectedActivityStateBoundary,
  ProjectedStatusBlock,
} from './projected-activity-state'
import { ProjectedCollectionView } from './projected-collection-view'
import { ProjectedMetricDashboard } from './projected-metric-dashboard'
import { ProjectedMetricStrip } from './projected-metric-strip'
import {
  ProjectedRecordDetails,
  type ProjectedRecordFieldRenderContext,
} from './projected-record-details'
import {
  ProjectedInputForm,
  type ProjectedInputFormChoiceClassNameResolver,
  type ProjectedInputFormChoiceToneResolver,
  type ProjectedInputFormFieldRenderer,
  type ProjectedInputFormRuntime,
} from './projected-input-form'
import { ProjectedInlineList } from './projected-inline-list'
import {
  ProjectedQueryForm,
  type ProjectedQueryFormChoiceClassNameResolver,
  type ProjectedQueryFormChoiceToneResolver,
  type ProjectedQueryFormFieldRenderer,
  type ProjectedQueryFormRuntime,
} from './projected-query-form'
import { ProjectedTabsView } from './projected-tabs-view'
import {
  ProjectedViewSurface,
  type ProjectedViewSurfaceContentTopInset,
  type ProjectedViewSurfaceVerticalResizeOptions,
} from './projected-view-surface'
import {
  PresentationActionGroup,
  type PresentationActionGroupOptions,
} from './presentation-action-group'
import {
  type ViewKind,
  fieldDisplayKinds,
  viewChromeSlotKinds,
  viewKindLabels,
  viewKinds,
  viewRegionKindLabels,
  viewRegionKinds,
} from '@cohesivesystems/presentation-contracts'
import {
  presentationViewComponentRoles,
} from '@cohesivesystems/presentation-contracts'

export type PresentationFieldRenderer<TData extends object = object> = (
  context: ProjectedFieldRenderContext<TData>,
) => ReactNode

export type PresentationRecordFieldRenderer<TData extends object = object> = (
  context: ProjectedRecordFieldRenderContext<TData>,
) => ReactNode

export type PresentationCollectionDetailFieldRenderer<TData extends object = object> = (
  context: ProjectedCollectionDetailFieldRenderContext<TData>,
) => ReactNode

/**
 * App-level customization for the standard presentation renderer set. The
 * options style is intentionally declarative: callers provide styling hooks,
 * icon maps, field renderers, and escape hatches while traversal and data-source
 * resolution stay owned by the projection runtime.
 */
export interface PresentationStandardRendererOptions<TContext> {
  /** Component implementation set used by standard projected renderers. */
  readonly componentSystem: PresentationRequiredValueOption<TContext, PresentationComponentSystem>

  /** Component binding set used to resolve target-specific component keys. */
  readonly componentSet?: PresentationStringOption<TContext>

  /** Design-system interpretation used by standard projected renderers. */
  readonly designSystem: PresentationRequiredValueOption<TContext, PresentationDesignSystem>

  /** Configures rendering and execution behavior for view action placements. */
  readonly actionGroup?: PresentationActionGroupOptions<TContext>

  /** Customizes table-like collection views rendered from primary data sources. */
  readonly collection?: {
    /** Frontend-local interpretations for projected collection row/selection actions. */
    readonly actionRuntimeBindings?: PresentationValueOption<
      TContext,
      readonly PresentationActionRuntimeBinding<
        ProjectedCollectionActionExecutionContext<object>,
        ReactNode
      >[]
    >

    /** Message shown when the resolved collection has no visible records. */
    readonly emptyMessage?: PresentationStringOption<TContext>

    /** Field-specific cell renderers keyed by presentation field id or field path. */
    readonly fieldRenderers?: PresentationValueOption<
      TContext,
      Readonly<Record<string, PresentationFieldRenderer>>
    >

    /** Field-specific renderers for inline detail views projected from selected rows. */
    readonly detailFieldRenderers?: PresentationValueOption<
      TContext,
      Readonly<Record<string, PresentationCollectionDetailFieldRenderer>>
    >

    /** Resolves pagination/windowing runtime state for the collection projection. */
    readonly pagination?: (
      context: PresentationCollectionRenderContext<TContext>,
    ) => ProjectedCollectionPaginationInput | null | undefined

    /** Additional class name applied to metric summary slots. */
    readonly summaryClassName?: PresentationStringOption<TContext>

    /** Renders supplemental collection chrome such as pagination controls. */
    readonly renderFooter?: (
      context: PresentationCollectionRenderContext<TContext>,
    ) => ReactNode

    /** Slot renderer registry keyed by Collection.Chrome slot kind and placement. */
    readonly chromeSlotRenderers?: PresentationValueOption<
      TContext,
      CollectionChromeSlotRendererRegistry<
        PresentationCollectionChromeSlotRenderContext<TContext>,
        ReactNode
      >
    >

    /** Interprets collection chrome slots declared by Collection.Chrome. */
    readonly renderChromeSlot?: (
      context: PresentationCollectionChromeSlotRenderContext<TContext>,
    ) => ReactNode
  }

  /** Customizes compact list views rendered from projected collection fields. */
  readonly inlineList?: {
    /** Message or node-compatible text shown when the inline list has no items. */
    readonly emptyMessage?: PresentationStringOption<TContext>

    /** Optional escape hatch for app-specific inline-list item rendering. */
    readonly renderItems?: (
      context: PresentationInlineListRenderContext<TContext>,
    ) => ReactNode
  }

  /** Customizes dashboard and metric-summary views. */
  readonly metricDashboard?: {
    /** Additional class name applied to the dashboard container. */
    readonly className?: PresentationStringOption<TContext>

    /** Optional dashboard description shown alongside the projected metrics. */
    readonly description?: PresentationStringOption<TContext>

    /** Icons keyed by metric field id for projected metric cards. */
    readonly iconByFieldId?: PresentationValueOption<
      TContext,
      Readonly<Record<string, ReactNode>>
    >

    /** Loading label used while dashboard data sources are pending. */
    readonly pendingLabel?: PresentationStringOption<TContext>
  }

  /** Customizes generic input-form views rendered from InputFormDefinition. */
  readonly inputForm?: {
    /** Maps projected choices to target toggle classes, usually through an app design-system binding. */
    readonly choiceToggleClassName?: PresentationValueOption<
      TContext,
      ProjectedInputFormChoiceClassNameResolver
    >

    /** Resolves semantic tone for projected choices before design-system class binding. */
    readonly choiceTone?: PresentationValueOption<
      TContext,
      ProjectedInputFormChoiceToneResolver
    >

    /** Additional class name applied to the input form surface. */
    readonly className?: PresentationStringOption<TContext>

    /** Additional class name applied to the input form content area. */
    readonly contentClassName?: PresentationStringOption<TContext>

    /** Field-specific input controls keyed by input-form field id or presentation field id. */
    readonly fieldRenderers?: PresentationValueOption<
      TContext,
      Readonly<Record<string, ProjectedInputFormFieldRenderer>>
    >

    /** Resolves the mutable form runtime for the current route context. */
    readonly runtime: (
      context: PresentationViewRenderContext<TContext>,
    ) => ProjectedInputFormRuntime | null
  }

  /** Customizes query-form views rendered from InputFormDefinition plus a relation-query target. */
  readonly queryForm?: {
    /** Maps projected choices to target toggle classes, usually through an app design-system binding. */
    readonly choiceToggleClassName?: PresentationValueOption<
      TContext,
      ProjectedQueryFormChoiceClassNameResolver
    >

    /** Resolves semantic tone for projected choices before design-system class binding. */
    readonly choiceTone?: PresentationValueOption<
      TContext,
      ProjectedQueryFormChoiceToneResolver
    >

    /** Additional class name applied to the query form surface. */
    readonly className?: PresentationStringOption<TContext>

    /** Additional class name applied to the query form content area. */
    readonly contentClassName?: PresentationStringOption<TContext>

    /** Field-specific query controls keyed by query-form field id or presentation field id. */
    readonly fieldRenderers?: PresentationValueOption<
      TContext,
      Readonly<Record<string, ProjectedQueryFormFieldRenderer>>
    >

    /** Resolves the mutable form runtime for the current route context. */
    readonly runtime: (
      context: PresentationViewRenderContext<TContext>,
    ) => ProjectedQueryFormRuntime | null
  }

  /** Final registry layer that can replace or extend standard renderer choices. */
  readonly overrides?: PresentationRendererRegistry<TContext>

  /** Customizes record-detail views rendered from a primary record data source. */
  readonly recordDetail?: {
    /** Additional class name applied to the detail surface container. */
    readonly className?: PresentationStringOption<TContext>

    /** Additional class name applied to the detail surface content area. */
    readonly contentClassName?: PresentationStringOption<TContext>

    /** Message shown when no record detail data is available. */
    readonly emptyMessage?: PresentationStringOption<TContext>

    /** Field-specific detail renderers keyed by presentation field id or field path. */
    readonly fieldRenderers?: PresentationValueOption<
      TContext,
      Readonly<Record<string, PresentationRecordFieldRenderer>>
    >

    /** Detail field ids or field paths whose label cell should not be rendered. */
    readonly hiddenFieldLabels?: PresentationValueOption<TContext, readonly string[]>

    /** Title override for the detail surface; defaults to the view name. */
    readonly title?: PresentationStringOption<TContext>
  }

  /** Customizes general nested surface views. */
  readonly surface?: {
    /** Additional class name applied to the surface container. */
    readonly className?: PresentationStringOption<TContext>

    /** Additional class name applied to the surface content area. */
    readonly contentClassName?: PresentationStringOption<TContext>

    /** Controls the semantic top inset between surface chrome and content. */
    readonly contentTopInset?: PresentationValueOption<
      TContext,
      ProjectedViewSurfaceContentTopInset | null
    >

    /** Small label rendered above the surface title. */
    readonly eyebrow?: PresentationStringOption<TContext>

    /** Title override for the surface; defaults to the projected view chrome. */
    readonly title?: PresentationStringOption<TContext>

    /** Enables vertical resize affordances for the surface. */
    readonly verticalResize?: PresentationValueOption<
      TContext,
      boolean | ProjectedViewSurfaceVerticalResizeOptions | null
    >
  }

  /** Customizes tabbed-surface views. */
  readonly tabs?: {
    /** Icons keyed by tab region id. */
    readonly iconByRegionId?: PresentationValueOption<
      TContext,
      Readonly<Record<string, ReactNode>>
    >
  }
}

/**
 * Specialized list renderers receive already resolved items so app code does
 * not need to know how a semantic view binds to its primary data source.
 */
export interface PresentationInlineListRenderContext<TContext>
  extends PresentationViewRenderContext<TContext> {
  readonly dataSource: PresentationDataSourceState | undefined
  readonly items: readonly object[]
}

/**
 * Collection escape-hatch renderers receive the same resolved data source and
 * item projection as the standard table renderer.
 */
export interface PresentationCollectionRenderContext<TContext>
  extends PresentationViewRenderContext<TContext> {
  readonly collectionRuntime?: ProjectedCollectionRuntime<object>
  readonly dataSource: PresentationDataSourceState | undefined
  readonly items: readonly object[]
}

export type PresentationCollectionChromeSlotRenderContext<TContext> =
  PresentationCollectionRenderContext<TContext> &
  ProjectedCollectionChromeSlotRenderContext<object> & {
    readonly collectionRuntime: ProjectedCollectionRuntime<object>
  }

type PresentationStringOption<TContext> =
  | ((context: PresentationViewRenderContext<TContext>) => string | undefined)
  | string

type PresentationValueOption<TContext, TValue> =
  | ((context: PresentationViewRenderContext<TContext>) => TValue | undefined)
  | TValue

type PresentationRequiredValueOption<TContext, TValue> =
  | ((context: PresentationViewRenderContext<TContext>) => TValue)
  | TValue

/**
 * Creates the app-wide default renderer registry for common semantic view
 * constructs: surface roots, dashboards, surfaces, tabbed surfaces, collections,
 * and simple inline lists. Specific apps can layer overrides on top without
 * reimplementing recursive rendering or activity/data-source handling.
 */
export function createStandardPresentationRendererRegistry<TContext>(
  options: PresentationStandardRendererOptions<TContext>,
): PresentationRendererRegistry<TContext> {
  const collectionRenderer = createCollectionRenderer(options)
  const inputFormRenderer = createInputFormRenderer(options)
  const drawerRenderer = createDrawerRenderer()
  const metricDashboardRenderer = createMetricDashboardRenderer(options)
  const queryFormRenderer = createQueryFormRenderer(options)
  const recordDetailRenderer = createRecordDetailRenderer(options)
  const surfaceRenderer = createSurfaceRenderer(options)
  const tabsRenderer = createTabsRenderer(options)

  const registry = {
    composites: {
      byComponentRole: {
        [presentationViewComponentRoles.collectionView]: collectionRenderer,
        [presentationViewComponentRoles.inputForm]: inputFormRenderer,
        [presentationViewComponentRoles.metricDashboard]: metricDashboardRenderer,
        [presentationViewComponentRoles.queryForm]: queryFormRenderer,
        [presentationViewComponentRoles.tabsView]: tabsRenderer,
        [presentationViewComponentRoles.viewSurface]: surfaceRenderer,
      },
      bySemanticRole: {
        collection: collectionRenderer,
        dashboard: metricDashboardRenderer,
        detail: recordDetailRenderer,
        drawer: drawerRenderer,
        form: surfaceRenderer,
        'inline-list': createInlineListRenderer(options),
        'input-form': inputFormRenderer,
        prompt: surfaceRenderer,
        query: queryFormRenderer,
        recordDetail: recordDetailRenderer,
        search: queryFormRenderer,
        summary: metricDashboardRenderer,
        surface: surfaceRenderer,
        'surface-root': renderSurfaceRoot,
        tabs: tabsRenderer,
        'record-detail': recordDetailRenderer,
      },
      byViewKind: createStandardViewKindRenderers({
        collectionRenderer,
        drawerRenderer,
        metricDashboardRenderer,
        queryFormRenderer,
        recordDetailRenderer,
        surfaceRenderer,
        tabsRenderer,
      }),
    },
  } satisfies PresentationRendererRegistry<TContext>

  return options.overrides
    ? mergePresentationRendererRegistries(registry, options.overrides)
    : registry
}

function createStandardViewKindRenderers<TContext>({
  collectionRenderer,
  drawerRenderer,
  metricDashboardRenderer,
  queryFormRenderer,
  recordDetailRenderer,
  surfaceRenderer,
  tabsRenderer,
}: {
  readonly collectionRenderer: PresentationViewRenderer<TContext>
  readonly drawerRenderer: PresentationViewRenderer<TContext>
  readonly metricDashboardRenderer: PresentationViewRenderer<TContext>
  readonly queryFormRenderer: PresentationViewRenderer<TContext>
  readonly recordDetailRenderer: PresentationViewRenderer<TContext>
  readonly surfaceRenderer: PresentationViewRenderer<TContext>
  readonly tabsRenderer: PresentationViewRenderer<TContext>
}) {
  const renderers: Record<string, PresentationViewRenderer<TContext>> = {}

  addViewKindRenderer(renderers, viewKinds.collection, collectionRenderer)
  addViewKindRenderer(renderers, viewKinds.dashboard, metricDashboardRenderer)
  addViewKindRenderer(renderers, viewKinds.drawer, drawerRenderer)
  addViewKindRenderer(renderers, viewKinds.form, surfaceRenderer)
  addViewKindRenderer(renderers, viewKinds.graph, surfaceRenderer)
  addViewKindRenderer(renderers, viewKinds.page, renderSurfaceRoot)
  addViewKindRenderer(renderers, viewKinds.prompt, surfaceRenderer)
  addViewKindRenderer(renderers, viewKinds.recordDetail, recordDetailRenderer)
  addViewKindRenderer(renderers, viewKinds.search, queryFormRenderer)
  addViewKindRenderer(renderers, viewKinds.surface, surfaceRenderer)
  addViewKindRenderer(renderers, viewKinds.tabbedSurface, tabsRenderer)
  addViewKindRenderer(renderers, viewKinds.timeline, surfaceRenderer)

  return renderers
}

function createDrawerRenderer<TContext>() {
  return function renderDrawer(context: PresentationViewRenderContext<TContext>) {
    return <div className="grid gap-2">{context.renderRegions(context.view)}</div>
  }
}

function addViewKindRenderer<TContext>(
  renderers: Record<string, PresentationViewRenderer<TContext>>,
  kind: ViewKind,
  renderer: PresentationViewRenderer<TContext>,
) {
  const label = viewKindLabels[kind]

  renderers[String(kind)] = renderer
  renderers[label] = renderer
  renderers[label.charAt(0).toLowerCase() + label.slice(1)] = renderer
}

/**
 * Root surfaces are structural containers. Their renderer should project child
 * regions and avoid adding visual chrome of its own.
 */
function renderSurfaceRoot<TContext>({
  renderView,
  view,
}: PresentationViewRenderContext<TContext>) {
  return (
    <div
      className="flex min-h-0 flex-1 flex-col gap-5"
      {...createPresentationTestAttributes({ viewId: view.Id })}
    >
      {renderSurfaceRegionViews({
        regions: orderSurfaceRegions(view.Regions),
        renderView,
        stretchPrimaryRegions: true,
      })}
    </div>
  )
}

function createMetricDashboardRenderer<TContext>(
  options: PresentationStandardRendererOptions<TContext>,
) {
  return function renderMetricDashboard(
    context: PresentationViewRenderContext<TContext>,
  ) {
    const { dataSourceResolver, view } = context
    const dataSource = dataSourceResolver.resolveViewPrimary(view)

    return (
      <ProjectedActivityStateBoundary
        state={createPresentationViewActivityState({
          dataSourceResolver,
          pendingLabel:
            resolveStringOption(options.metricDashboard?.pendingLabel, context) ??
            'Loading view data...',
          view,
        })}
      >
        <ProjectedMetricDashboard
          action={renderPresentationViewActionFallback({ context, options, view })}
          className={resolveStringOption(options.metricDashboard?.className, context)}
          componentSystem={resolveRequiredValueOption(options.componentSystem, context)}
          dataSourceResolver={dataSourceResolver}
          description={resolveStringOption(options.metricDashboard?.description, context)}
          iconByFieldId={resolveValueOption(
            options.metricDashboard?.iconByFieldId,
            context,
          )}
          renderChromeSlot={createPresentationViewChromeSlotRenderer({
            context,
            options,
            resource: dataSource?.data,
          })}
          view={view}
          viewId={view.Id}
        />
      </ProjectedActivityStateBoundary>
    )
  }
}

function createInputFormRenderer<TContext>(
  options: PresentationStandardRendererOptions<TContext>,
) {
  return function renderInputForm(context: PresentationViewRenderContext<TContext>) {
    const { dataSourceResolver, module, view } = context
    const inputForm = findPresentationInputFormForView(module, view)
    const dataSource = dataSourceResolver.resolveViewPrimary(view)

    return (
      <ProjectedInputForm
        choiceToggleClassName={resolveValueOption(
          options.inputForm?.choiceToggleClassName,
          context,
        )}
        choiceTone={resolveValueOption(options.inputForm?.choiceTone, context)}
        className={resolveStringOption(options.inputForm?.className, context)}
        componentSet={resolveStandardPresentationComponentSet(options, context)}
        componentSystem={resolveRequiredValueOption(options.componentSystem, context)}
        contentClassName={resolveStringOption(options.inputForm?.contentClassName, context)}
        dataSourceResolver={dataSourceResolver}
        designSystem={resolveRequiredValueOption(options.designSystem, context)}
        fieldRenderers={resolveValueOption(options.inputForm?.fieldRenderers, context)}
        inputForm={inputForm}
        module={module}
        renderChromeSlot={createPresentationViewChromeSlotRenderer({
          context,
          options,
          resource: dataSource?.data,
        })}
        runtime={options.inputForm?.runtime(context) ?? null}
        view={view}
      />
    )
  }
}

function createQueryFormRenderer<TContext>(
  options: PresentationStandardRendererOptions<TContext>,
) {
  return function renderQueryForm(context: PresentationViewRenderContext<TContext>) {
    const { dataSourceResolver, module, view } = context
    const queryForm = findPresentationQueryFormForView(module, view)
    const dataSource = dataSourceResolver.resolveViewPrimary(view)

    if (queryForm && options.queryForm) {
      return (
        <ProjectedQueryForm
          choiceToggleClassName={resolveValueOption(
            options.queryForm.choiceToggleClassName,
            context,
          )}
          choiceTone={resolveValueOption(options.queryForm.choiceTone, context)}
          className={resolveStringOption(options.queryForm.className, context)}
          componentSet={resolveStandardPresentationComponentSet(options, context)}
          componentSystem={resolveRequiredValueOption(options.componentSystem, context)}
          contentClassName={resolveStringOption(options.queryForm.contentClassName, context)}
          dataSourceResolver={dataSourceResolver}
          designSystem={resolveRequiredValueOption(options.designSystem, context)}
          fieldRenderers={resolveValueOption(options.queryForm.fieldRenderers, context)}
          module={module}
          queryForm={queryForm}
          renderChromeSlot={createPresentationViewChromeSlotRenderer({
            context,
            options,
            resource: dataSource?.data,
          })}
          runtime={options.queryForm.runtime(context)}
          view={view}
        />
      )
    }

    if (options.inputForm) {
      const inputForm = queryForm
        ? findPresentationInputForm(module, queryForm.FormId)
        : findPresentationInputFormForView(module, view)
      const target = queryForm && inputForm
        ? createRelationQueryInputFormTarget({
            dataSourceResolver,
            inputForm,
            queryForm,
            view,
          })
        : null

      return (
        <ProjectedInputForm
          choiceToggleClassName={resolveValueOption(
            options.inputForm.choiceToggleClassName,
            context,
          )}
          choiceTone={resolveValueOption(options.inputForm.choiceTone, context)}
          className={resolveStringOption(options.inputForm.className, context)}
          componentSet={resolveStandardPresentationComponentSet(options, context)}
          componentSystem={resolveRequiredValueOption(options.componentSystem, context)}
          contentClassName={resolveStringOption(options.inputForm.contentClassName, context)}
          dataSourceResolver={dataSourceResolver}
          designSystem={resolveRequiredValueOption(options.designSystem, context)}
          fieldRenderers={resolveValueOption(options.inputForm.fieldRenderers, context)}
          inputForm={inputForm}
          module={module}
          renderChromeSlot={createPresentationViewChromeSlotRenderer({
            context,
            options,
            resource: dataSource?.data,
          })}
          runtime={options.inputForm.runtime(context)}
          target={target}
          view={view}
        />
      )
    }

    return (
      <ProjectedQueryForm
        choiceToggleClassName={resolveValueOption(
          options.queryForm?.choiceToggleClassName,
          context,
        )}
        choiceTone={resolveValueOption(options.queryForm?.choiceTone, context)}
        className={resolveStringOption(options.queryForm?.className, context)}
        componentSet={resolveStandardPresentationComponentSet(options, context)}
        componentSystem={resolveRequiredValueOption(options.componentSystem, context)}
        contentClassName={resolveStringOption(options.queryForm?.contentClassName, context)}
        dataSourceResolver={dataSourceResolver}
        designSystem={resolveRequiredValueOption(options.designSystem, context)}
        fieldRenderers={resolveValueOption(options.queryForm?.fieldRenderers, context)}
        module={module}
        queryForm={queryForm}
        renderChromeSlot={createPresentationViewChromeSlotRenderer({
          context,
          options,
          resource: dataSource?.data,
        })}
        runtime={options.queryForm?.runtime(context) ?? null}
        view={view}
      />
    )
  }
}

function createSurfaceRenderer<TContext>(
  options: PresentationStandardRendererOptions<TContext>,
) {
  return function renderSurface(context: PresentationViewRenderContext<TContext>) {
    const { dataSourceResolver, renderView, view } = context
    const dataSource = dataSourceResolver.resolveViewPrimary(view)
    const verticalResize = resolveValueOption(options.surface?.verticalResize, context)
    const isVerticallyResizable = isSurfaceVerticalResizeEnabled(verticalResize)
    const contentClassName = resolveSurfaceContentClassName({
      className: resolveStringOption(
        options.surface?.contentClassName,
        context,
      ),
      isVerticallyResizable,
      view,
    })

    return (
      <ProjectedViewSurface
        action={renderPresentationViewActionFallback({ context, options, view })}
        className={resolveStringOption(options.surface?.className, context)}
        componentSystem={resolveRequiredValueOption(options.componentSystem, context)}
        contentClassName={contentClassName}
        contentTopInset={resolveValueOption(options.surface?.contentTopInset, context)}
        eyebrow={resolveStringOption(options.surface?.eyebrow, context)}
        renderChromeSlot={createPresentationViewChromeSlotRenderer({
          context,
          options,
          resource: dataSource?.data,
        })}
        title={resolveStringOption(options.surface?.title, context)}
        verticalResize={verticalResize}
        view={view}
      >
        {renderSurfaceRegionViews({
          regions: orderSurfaceRegions(view.Regions),
          renderView,
          stretchPrimaryRegions: isVerticallyResizable,
        })}
      </ProjectedViewSurface>
    )
  }
}

function createTabsRenderer<TContext>(
  options: PresentationStandardRendererOptions<TContext>,
) {
  return function renderTabs(context: PresentationViewRenderContext<TContext>) {
    const { dataSourceResolver, renderView, view } = context
    const dataSource = dataSourceResolver.resolveViewPrimary(view)
    const componentSystem = resolveRequiredValueOption(options.componentSystem, context)

    return (
      <ProjectedTabsView
        componentSystem={componentSystem}
        iconByRegionId={resolveValueOption(options.tabs?.iconByRegionId, context)}
        renderChromeSlot={createPresentationViewChromeSlotRenderer({
          context,
          options,
          resource: dataSource?.data,
        })}
        renderView={renderView}
        view={view}
        viewId={view.Id}
      />
    )
  }
}

function createCollectionRenderer<TContext>(
  options: PresentationStandardRendererOptions<TContext>,
) {
  return function renderCollection(
    context: PresentationViewRenderContext<TContext>,
  ) {
    const { dataSourceResolver, view } = context
    const dataSource = dataSourceResolver.resolveViewPrimary(view)
    const items = readPresentationDataSourceItems(dataSource)
    const chromeSlotRenderers = resolveValueOption(
      options.collection?.chromeSlotRenderers,
      context,
    )
    const componentSystem = resolveRequiredValueOption(options.componentSystem, context)
    const collectionContext = {
      ...context,
      dataSource,
      items,
    } satisfies PresentationCollectionRenderContext<TContext>

    return (
      <ProjectedActivityStateBoundary
        state={createPresentationViewActivityState({ dataSourceResolver, view })}
      >
        <ProjectedCollectionView
          actionRuntimeBindings={resolveValueOption(
            options.collection?.actionRuntimeBindings,
            context,
          )}
          componentSet={resolveStandardPresentationComponentSet(options, context)}
          componentSystem={componentSystem}
          data={items}
          detailFieldRenderers={resolveValueOption(
            options.collection?.detailFieldRenderers,
            context,
          )}
          emptyMessage={
            resolveStringOption(options.collection?.emptyMessage, context) ??
            dataSource?.emptyMessage ??
            `No ${view.Name.toLocaleLowerCase()} records are visible.`
          }
          fieldRenderers={resolveValueOption(
            options.collection?.fieldRenderers,
            context,
          )}
          footer={options.collection?.renderFooter
            ? (collectionRuntime) =>
                options.collection?.renderFooter?.({
                  ...collectionContext,
                  collectionRuntime: collectionRuntime as ProjectedCollectionRuntime<object>,
                })
            : null}
          pagination={options.collection?.pagination?.(collectionContext)}
          renderChromeSlot={({ collectionRuntime, slot, ...slotContext }) =>
            renderPresentationCollectionChromeSlot({
              context: {
                ...collectionContext,
                ...slotContext,
                collectionRuntime: collectionRuntime as ProjectedCollectionRuntime<object>,
                module: collectionContext.module,
                renderQueryFormSlot: (slot) =>
                  renderPresentationCollectionQueryFormSlot({
                    context: collectionContext,
                    options,
                    slot,
                  }),
                renderSummarySlot: (slot) =>
                  renderPresentationCollectionSummarySlot({
                    context: collectionContext,
                    options,
                    slot,
                  }),
                slot,
              },
              registry: chromeSlotRenderers,
              renderFallback: options.collection?.renderChromeSlot,
            })}
          viewId={view.Id}
        />
      </ProjectedActivityStateBoundary>
    )
  }
}

function renderPresentationCollectionChromeSlot<TContext>({
  context,
  registry,
  renderFallback,
}: {
  readonly context: PresentationCollectionChromeSlotRenderContext<TContext>
  readonly registry:
    | CollectionChromeSlotRendererRegistry<
        PresentationCollectionChromeSlotRenderContext<TContext>,
        ReactNode
      >
    | undefined
  readonly renderFallback?: (
    context: PresentationCollectionChromeSlotRenderContext<TContext>,
  ) => ReactNode
}) {
  const renderer = resolveCollectionChromeSlotRenderer(registry, context.slot)
  return renderer?.(context) ??
    renderStandardCollectionChromeSlot(context) ??
    renderFallback?.(context) ??
    null
}

function renderPresentationCollectionQueryFormSlot<TContext>({
  context,
  options,
  slot,
}: {
  readonly context: PresentationCollectionRenderContext<TContext>
  readonly options: PresentationStandardRendererOptions<TContext>
  readonly slot: CollectionChromeSlotDefinition
}) {
  const { dataSourceResolver, module, view } = context
  const queryForm = slot.QueryFormId
    ? findPresentationQueryForm(module, slot.QueryFormId)
    : findPresentationQueryFormForView(module, view)

  return (
    <ProjectedQueryForm
      choiceToggleClassName={resolveValueOption(
        options.queryForm?.choiceToggleClassName,
        context,
      )}
      choiceTone={resolveValueOption(options.queryForm?.choiceTone, context)}
      className={resolveStringOption(options.queryForm?.className, context)}
      componentSet={resolveStandardPresentationComponentSet(options, context)}
      componentSystem={resolveRequiredValueOption(options.componentSystem, context)}
      contentClassName={resolveStringOption(options.queryForm?.contentClassName, context)}
      dataSourceResolver={dataSourceResolver}
      designSystem={resolveRequiredValueOption(options.designSystem, context)}
      fieldRenderers={resolveValueOption(options.queryForm?.fieldRenderers, context)}
      module={module}
      queryForm={queryForm}
      runtime={options.queryForm?.runtime(context) ?? null}
      view={view}
    />
  )
}

function resolveSurfaceContentClassName({
  className,
  isVerticallyResizable,
  view,
}: {
  readonly className?: string
  readonly isVerticallyResizable: boolean
  readonly view: ViewDefinition
}) {
  if (hasSidecarSurfaceRegions(view.Regions)) {
    return cn(
      'grid min-h-0 flex-1 gap-y-4 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-start lg:gap-x-0 lg:has-[>[data-surface-region-kind=sidecar]:not(:empty)]:gap-x-4',
      className,
    )
  }

  if (isVerticallyResizable) {
    return cn(
      'flex min-h-0 flex-1 flex-col gap-4 overflow-hidden',
      className,
    )
  }

  return className ?? (view.Regions.length > 0 ? 'grid w-full min-w-0 gap-4' : undefined)
}

function orderSurfaceRegions(
  regions: readonly ViewRegionDefinition[],
): readonly ViewRegionDefinition[] {
  if (!hasSidecarSurfaceRegions(regions)) {
    return regions
  }

  return [
    ...regions.filter((region) => isPrimarySurfaceRegionKind(region.Kind)),
    ...regions.filter((region) => isSidecarSurfaceRegionKind(region.Kind)),
    ...regions.filter((region) =>
      !isPrimarySurfaceRegionKind(region.Kind) &&
      !isSidecarSurfaceRegionKind(region.Kind)),
  ]
}

function renderSurfaceRegionViews({
  regions,
  renderView,
  stretchPrimaryRegions,
}: {
  readonly regions: readonly ViewRegionDefinition[]
  readonly renderView: (viewId: string) => ReactNode
  readonly stretchPrimaryRegions?: boolean
}) {
  return regions.flatMap((region) =>
    getRegionViewIds(region).map((childViewId) => (
      <div
        className={resolveSurfaceRegionClassName(region, { stretchPrimaryRegions })}
        data-surface-region-kind={resolveSurfaceRegionLayoutKind(region)}
        key={`${region.Id}:${childViewId}`}
      >
        {renderView(childViewId)}
      </div>
    )),
  )
}

function hasSidecarSurfaceRegions(regions: readonly ViewRegionDefinition[]) {
  if (regions.length < 2) {
    return false
  }

  return regions.some((region) => isPrimarySurfaceRegionKind(region.Kind)) &&
    regions.some((region) => isSidecarSurfaceRegionKind(region.Kind))
}

function resolveSurfaceRegionClassName(
  region: ViewRegionDefinition,
  options: { readonly stretchPrimaryRegions?: boolean } = {},
) {
  const layoutKind = resolveSurfaceRegionLayoutKind(region)
  if (layoutKind === 'sidecar') {
    return 'min-h-0 min-w-0 empty:hidden lg:w-[clamp(18rem,25vw,24rem)]'
  }

  if (layoutKind === 'primary' && options.stretchPrimaryRegions) {
    return 'flex min-h-0 w-full min-w-0 flex-1 flex-col overflow-hidden'
  }

  return 'min-h-0 w-full min-w-0'
}

function resolveSurfaceRegionLayoutKind(region: ViewRegionDefinition) {
  if (isSidecarSurfaceRegionKind(region.Kind)) {
    return 'sidecar'
  }

  if (isPrimarySurfaceRegionKind(region.Kind)) {
    return 'primary'
  }

  return 'supporting'
}

function isPrimarySurfaceRegionKind(kind: string | number) {
  return matchesViewRegionKind(kind, viewRegionKinds.primary) ||
    matchesViewRegionKind(kind, viewRegionKinds.content) ||
    matchesViewRegionKind(kind, viewRegionKinds.surface) ||
    matchesViewRegionKind(kind, viewRegionKinds.collection) ||
    matchesViewRegionKind(kind, viewRegionKinds.detail)
}

function isSidecarSurfaceRegionKind(kind: string | number) {
  return matchesViewRegionKind(kind, viewRegionKinds.sidebar) ||
    matchesViewRegionKind(kind, viewRegionKinds.statusList) ||
    matchesViewRegionKind(kind, viewRegionKinds.list) ||
    matchesViewRegionKind(kind, viewRegionKinds.inspector) ||
    matchesViewRegionKind(kind, viewRegionKinds.panel)
}

function matchesViewRegionKind(kind: string | number, expected: string | number) {
  const expectedLabel =
    viewRegionKindLabels[expected as keyof typeof viewRegionKindLabels] ?? expected
  return kind === expected ||
    normalizeEnumDiscriminator(kind) === normalizeEnumDiscriminator(expectedLabel)
}

function renderPresentationCollectionSummarySlot<TContext>({
  context,
  options,
  slot,
}: {
  readonly context: PresentationCollectionRenderContext<TContext>
  readonly options: PresentationStandardRendererOptions<TContext>
  readonly slot: CollectionChromeSlotDefinition
}) {
  const fieldIds = resolveCollectionSummaryFieldIds(context.module, slot)
  if (fieldIds.length === 0) {
    return null
  }

  return (
    <ProjectedMetricStrip
      className={resolveStringOption(options.collection?.summaryClassName, context)}
      componentSystem={resolveRequiredValueOption(options.componentSystem, context)}
      dataSourceResolver={context.dataSourceResolver}
      fieldIds={fieldIds}
      iconByFieldId={resolveValueOption(options.metricDashboard?.iconByFieldId, context)}
    />
  )
}

function resolveCollectionSummaryFieldIds(
  module: PresentationCollectionRenderContext<unknown>['module'],
  slot: CollectionChromeSlotDefinition,
) {
  if (slot.FieldIds.length > 0) {
    return slot.FieldIds
  }

  const slotDataSourceIds = new Set(slot.DataSourceIds)
  return module.Fields
    .filter((field) =>
      field.Source?.DataSourceId &&
      slotDataSourceIds.has(field.Source.DataSourceId) &&
      isMetricField(field.DisplayKind))
    .map((field) => field.Id)
}

function isMetricField(displayKind: unknown) {
  return displayKind === fieldDisplayKinds.metric ||
    String(displayKind).toLocaleLowerCase() === 'metric'
}

function createPresentationViewChromeSlotRenderer<TContext>({
  context,
  options,
  resource,
}: {
  readonly context: PresentationViewRenderContext<TContext>
  readonly options: PresentationStandardRendererOptions<TContext>
  readonly resource?: unknown
}) {
  return (slot: ViewChromeSlotDefinition, view: ViewDefinition | null) =>
    renderStandardViewChromeSlot({
      actionContext: context.context,
      actionGroupOptions: options.actionGroup,
      componentSystem: resolveRequiredValueOption(options.componentSystem, context),
      dataSourceResolver: context.dataSourceResolver,
      designSystem: resolveRequiredValueOption(options.designSystem, context),
      documentViewIds: slot.ViewIds,
      module: context.module,
      resource,
      slot,
      view: view ?? context.view,
      workspaceView: view ?? context.view,
    })
}

function renderPresentationViewActionFallback<TContext>({
  context,
  options,
  view,
}: {
  readonly context: PresentationViewRenderContext<TContext>
  readonly options: PresentationStandardRendererOptions<TContext>
  readonly view: ViewDefinition
}) {
  if (!options.actionGroup || hasViewActionsChromeSlot(view)) {
    return null
  }

  return (
    <PresentationActionGroup
      context={context.context}
      dataSourceResolver={context.dataSourceResolver}
      module={context.module}
      options={options.actionGroup}
      view={view}
    />
  )
}

function hasViewActionsChromeSlot(view: ViewDefinition) {
  return view.Chrome?.Slots.some((slot) =>
    isViewChromeSlotKind(slot, viewChromeSlotKinds.actions)) ?? false
}

function createRecordDetailRenderer<TContext>(
  options: PresentationStandardRendererOptions<TContext>,
) {
  return function renderRecordDetail(context: PresentationViewRenderContext<TContext>) {
    const { dataSourceResolver, view } = context
    const dataSource = dataSourceResolver.resolveViewPrimary(view)
    const data = dataSource?.data
    const componentSystem = resolveRequiredValueOption(options.componentSystem, context)

    return (
      <ProjectedActivityStateBoundary
        state={createPresentationViewActivityState({ dataSourceResolver, view })}
      >
        <ProjectedViewSurface
          action={renderPresentationViewActionFallback({ context, options, view })}
          className={resolveStringOption(options.recordDetail?.className, context)}
          componentSystem={componentSystem}
          contentClassName={
            resolveStringOption(options.recordDetail?.contentClassName, context) ??
            'grid gap-4'
          }
          renderChromeSlot={createPresentationViewChromeSlotRenderer({
            context,
            options,
            resource: data,
          })}
          title={resolveStringOption(options.recordDetail?.title, context) ?? view.Name}
          view={view}
        >
          {data && typeof data === 'object' ? (
            <ProjectedRecordDetails
              componentSystem={componentSystem}
              data={data as object}
              emptyMessage={
                resolveStringOption(options.recordDetail?.emptyMessage, context) ??
                dataSource?.emptyMessage
              }
              fieldRenderers={resolveValueOption(
                options.recordDetail?.fieldRenderers,
                context,
              )}
              hiddenFieldLabels={
                resolveValueOption(options.recordDetail?.hiddenFieldLabels, context)
              }
              viewId={view.Id}
            />
          ) : (
            <ProjectedStatusBlock
              label={
                resolveStringOption(options.recordDetail?.emptyMessage, context) ??
                dataSource?.emptyMessage ??
                'No details are available.'
              }
            />
          )}
        </ProjectedViewSurface>
      </ProjectedActivityStateBoundary>
    )
  }
}

function createInlineListRenderer<TContext>(
  options: PresentationStandardRendererOptions<TContext>,
) {
  return function renderInlineList(context: PresentationViewRenderContext<TContext>) {
    const { dataSourceResolver, view } = context
    const dataSource = dataSourceResolver.resolveViewPrimary(view)
    const items = readPresentationDataSourceItems(dataSource)
    const componentSystem = resolveRequiredValueOption(options.componentSystem, context)

    return (
      <ProjectedActivityStateBoundary
        state={createPresentationViewActivityState({ dataSourceResolver, view })}
      >
        {items.length === 0 && options.inlineList?.renderItems ? (
          resolveStringOption(options.inlineList?.emptyMessage, context) ??
          dataSource?.emptyMessage ??
          null
        ) : (
          options.inlineList?.renderItems?.({ ...context, dataSource, items }) ?? (
            <ProjectedInlineList
              actionRuntimeBindings={resolveValueOption(
                options.collection?.actionRuntimeBindings,
                context,
              )}
              componentSet={resolveStandardPresentationComponentSet(options, context)}
              componentSystem={componentSystem}
              data={items}
              emptyMessage={
                resolveStringOption(options.inlineList?.emptyMessage, context) ??
                dataSource?.emptyMessage
              }
              fieldRenderers={resolveValueOption(
                options.collection?.fieldRenderers,
                context,
              )}
              title={view.Name}
              viewId={view.Id}
            />
          )
        )}
      </ProjectedActivityStateBoundary>
    )
  }
}

function resolveStandardPresentationComponentSet<TContext>(
  options: PresentationStandardRendererOptions<TContext>,
  context: PresentationViewRenderContext<TContext>,
) {
  return resolveStringOption(options.componentSet, context) ??
    defaultPresentationComponentSet
}

function resolveStringOption<TContext>(
  option: PresentationStringOption<TContext> | undefined,
  context: PresentationViewRenderContext<TContext>,
) {
  return resolveValueOption(option, context)
}

function resolveRequiredValueOption<TContext, TValue>(
  option: PresentationRequiredValueOption<TContext, TValue>,
  context: PresentationViewRenderContext<TContext>,
) {
  return typeof option === 'function'
    ? (option as (context: PresentationViewRenderContext<TContext>) => TValue)(context)
    : option
}

function resolveValueOption<TContext, TValue>(
  option: PresentationValueOption<TContext, TValue> | undefined,
  context: PresentationViewRenderContext<TContext>,
) {
  return typeof option === 'function'
    ? (option as (context: PresentationViewRenderContext<TContext>) => TValue | undefined)(context)
    : option
}

function normalizeEnumDiscriminator(value: string | number) {
  return String(value).replace(/[-_\s]/g, '').toLocaleLowerCase()
}

function isSurfaceVerticalResizeEnabled(
  value: boolean | ProjectedViewSurfaceVerticalResizeOptions | null | undefined,
) {
  return value === true ||
    (typeof value === 'object' && value !== null && value.enabled !== false)
}

function cn(...values: readonly (false | null | string | undefined)[]) {
  return values.filter(Boolean).join(' ')
}
