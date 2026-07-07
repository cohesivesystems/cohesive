import { Fragment, useEffect, useMemo, type MouseEvent, type ReactNode } from 'react'

import {
  findPresentationAction,
  getPresentationViewProjectedActionPlacements,
  isPresentationViewFetching,
  isViewChromeSlotKind,
  isViewChromeSlotPlacementValue,
  promptChromeIconIds,
  resolvePresentationViewDataSourceIds,
  type ActionDefinition,
  type ActionPlacementDefinition,
  type PresentationDataSourceResolver,
  type PresentationModuleDefinition,
  type ViewChromeSlotDefinition,
  type ViewDefinition,
  type ViewRegionDefinition,
} from '@cohesivesystems/presentation-core'
import {
  PresentationActionGroup,
  type PresentationActionGroupOptions,
  type PresentationActionRenderContext,
} from './presentation-action-group'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type { PresentationDesignSystem } from '@cohesivesystems/presentation-tailwind'
import {
  renderPresentationIcon,
} from './presentation-icon-registry'
import {
  projectPresentationIconDiagnostics,
} from './presentation-icon-diagnostics'
import {
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesivesystems/presentation-react'
import {
  ProjectedViewChrome,
  resolveViewChromeSlotPlacement,
} from './projected-view-chrome'
import type {
  ProjectedViewSurfaceChromeSlotRenderer,
} from './projected-view-surface'
import {
  viewChromeSlotKinds,
  viewChromeSlotPlacements,
  viewRegionKinds,
} from '@cohesivesystems/presentation-contracts'

export interface ProjectedPromptViewProps<TContext> {
  readonly actionGroupOptions?: PresentationActionGroupOptions<TContext>
  readonly actionRegionId?: string
  readonly className?: string
  readonly componentSystem: PresentationComponentSystem
  readonly contentClassName?: string
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly description?: ReactNode
  readonly module: PresentationModuleDefinition
  readonly regionRenderers?: Readonly<Record<string, ProjectedPromptRegionRenderer<TContext>>>
  readonly renderChromeSlot?: ProjectedViewSurfaceChromeSlotRenderer
  readonly renderRegion?: ProjectedPromptRegionRenderer<TContext>
  readonly renderView?: (viewId: string) => ReactNode
  readonly title?: ReactNode
  readonly view: ViewDefinition
}

export interface ProjectedPromptRegionRenderContext<TContext> {
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly module: PresentationModuleDefinition
  readonly region: ViewRegionDefinition
  readonly renderView?: (viewId: string) => ReactNode
  readonly view: ViewDefinition
}

export type ProjectedPromptRegionRenderer<TContext> = (
  context: ProjectedPromptRegionRenderContext<TContext>,
) => ReactNode | undefined

export function ProjectedPromptView<TContext>({
  actionGroupOptions,
  actionRegionId = 'footer',
  className,
  componentSystem,
  contentClassName,
  context,
  dataSourceResolver,
  designSystem,
  description,
  module,
  regionRenderers,
  renderChromeSlot,
  renderRegion,
  renderView,
  title,
  view,
}: ProjectedPromptViewProps<TContext>) {
  const titleId = `${toDomId(view.Id)}-title`
  const ActionButton = componentSystem.actions.ActionButton
  const layoutClasses = resolvePromptLayoutClasses(view, dataSourceResolver)
  const bodyRegions = view.Regions.filter((region) => !isActionRegion(region, actionRegionId))
  const actionView = {
    ...view,
    Actions: getPresentationViewProjectedActionPlacements(view)
      .filter((action) => action.Region === actionRegionId),
    Chrome: null,
  } satisfies ViewDefinition
  const hasFooterActionChrome = hasActionChromeSlotAtPlacement(
    view,
    viewChromeSlotPlacements.footer,
  )
  const hasDescriptionStatusChrome =
    Boolean(renderChromeSlot) &&
    hasStatusChromeSlotAtPlacement(view, viewChromeSlotPlacements.beforeContent)
  const dismissAction = resolvePromptDismissAction({
    actionGroupOptions,
    context,
    dataSourceResolver,
    module,
    view,
  })
  const promptIconDiagnostics = useMemo(
    () => projectPresentationIconDiagnostics({
      icons: view.PromptDismiss?.ShowCloseButton
        ? [{
            icon: promptChromeIconIds.close,
            id: `${view.Id}:close`,
            kind: 'prompt-chrome-icon',
            label: 'Close prompt',
          }]
        : [],
      module,
      source: `projected-prompt-icons:${view.Id}`,
      surfaceId: view.Id,
      surfaceName: view.Name,
    }),
    [
      module,
      view.Id,
      view.Name,
      view.PromptDismiss?.ShowCloseButton,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    `projected-prompt-icons:${view.Id}`,
    promptIconDiagnostics,
  )

  useEffect(() => {
    if (
      !view.PromptDismiss?.DismissOnEscape ||
      !dismissAction ||
      dismissAction.isHidden ||
      !dismissAction.canDismiss
    ) {
      return
    }

    const executeDismiss = dismissAction.execute
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key !== 'Escape') {
        return
      }

      event.preventDefault()
      executeDismiss()
    }

    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [dismissAction, view.PromptDismiss?.DismissOnEscape])

  function handleBackdropMouseDown(event: MouseEvent<HTMLDivElement>) {
    if (
      event.target !== event.currentTarget ||
      !view.PromptDismiss?.DismissOnBackdrop ||
      !dismissAction ||
      dismissAction.isHidden ||
      !dismissAction.canDismiss
    ) {
      return
    }

    event.preventDefault()
    dismissAction.execute()
  }
  const renderPromptChromeSlot = (
    slot: ViewChromeSlotDefinition,
    chromeView: ViewDefinition | null,
  ) => {
    if (isViewChromeSlotKind(slot, viewChromeSlotKinds.actions)) {
      return renderPromptActionGroup({
        actionGroupOptions,
        actions: slot.Actions.length > 0 ? slot.Actions : actionView.Actions,
        componentSystem,
        context,
        dataSourceResolver,
        designSystem,
        module,
        view,
      })
    }

    return renderChromeSlot?.(slot, chromeView) ?? null
  }

  const headerActions = componentSystem.prompts.PromptHeaderActions({
    actions: (
      <ProjectedViewChrome
        componentSystem={componentSystem}
        placement={viewChromeSlotPlacements.header}
        renderSlot={(slot) => renderPromptChromeSlot(slot, view)}
        view={view}
      />
    ),
    closeButton: view.PromptDismiss?.ShowCloseButton && dismissAction && !dismissAction.isHidden ? (
      <ActionButton
        aria-label="Close"
        disabled={!dismissAction.canDismiss}
        onClick={dismissAction.execute}
        size="icon-sm"
        type="button"
        variant="ghost"
      >
        {renderPresentationIcon({
          className: 'size-4',
          icon: promptChromeIconIds.close,
          module,
        })}
      </ActionButton>
    ) : null,
    viewId: view.Id,
  })
  const footer = componentSystem.prompts.PromptFooter({
    children: hasFooterActionChrome ? (
      <ProjectedViewChrome
        componentSystem={componentSystem}
        placement={viewChromeSlotPlacements.footer}
        renderSlot={(slot) => renderPromptChromeSlot(slot, view)}
        view={view}
      />
    ) : (
      renderPromptActionFooter({
        actionGroupOptions,
        componentSystem,
        context,
        dataSourceResolver,
        designSystem,
        module,
        view: actionView,
      })
    ),
    viewId: view.Id,
  })

  return componentSystem.prompts.PromptModal({
    ariaLabelledBy: titleId,
    className: cn(layoutClasses.containerClassName, className),
    description: description && !hasDescriptionStatusChrome ? description : null,
    footer,
    headerActions,
    onBackdropMouseDown: handleBackdropMouseDown,
    role: view.Accessibility?.Role ?? 'dialog',
    title: title ?? view.Name,
    titleId,
    children: (
      <>
        <ProjectedViewChrome
          componentSystem={componentSystem}
          placement={viewChromeSlotPlacements.beforeContent}
          renderSlot={(slot) => renderPromptChromeSlot(slot, view)}
          view={view}
        />

        {componentSystem.prompts.PromptContent({
          className: cn(layoutClasses.contentClassName, contentClassName),
          viewId: view.Id,
          children: (
            <>
              {bodyRegions.map((region) => renderPromptRegion({
                componentSystem,
                context,
                dataSourceResolver,
                module,
                region,
                regionRenderers,
                renderRegion,
                renderView,
                view,
              }))}
            </>
          ),
        })}

        <ProjectedViewChrome
          componentSystem={componentSystem}
          placement={viewChromeSlotPlacements.afterContent}
          renderSlot={(slot) => renderPromptChromeSlot(slot, view)}
          view={view}
        />
      </>
    ),
  })
}

function renderPromptActionFooter<TContext>({
  actionGroupOptions,
  componentSystem,
  context,
  dataSourceResolver,
  designSystem,
  module,
  view,
}: {
  readonly actionGroupOptions?: PresentationActionGroupOptions<TContext>
  readonly componentSystem: PresentationComponentSystem
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly module: PresentationModuleDefinition
  readonly view: ViewDefinition
}) {
  if (view.Actions.length === 0 || !actionGroupOptions) {
    return null
  }

  return (
    <PresentationActionGroup
      context={context}
      dataSourceResolver={dataSourceResolver}
      module={module}
      options={{
        ...actionGroupOptions,
        className: actionGroupOptions.className ?? 'flex flex-wrap items-center justify-end gap-2',
        componentSystem,
        designSystem,
      }}
      view={view}
    />
  )
}

function renderPromptActionGroup<TContext>({
  actionGroupOptions,
  actions,
  componentSystem,
  context,
  dataSourceResolver,
  designSystem,
  module,
  view,
}: {
  readonly actionGroupOptions?: PresentationActionGroupOptions<TContext>
  readonly actions: readonly ActionPlacementDefinition[]
  readonly componentSystem: PresentationComponentSystem
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly designSystem: PresentationDesignSystem
  readonly module: PresentationModuleDefinition
  readonly view: ViewDefinition
}) {
  if (actions.length === 0 || !actionGroupOptions) {
    return null
  }

  return (
    <PresentationActionGroup
      context={context}
      dataSourceResolver={dataSourceResolver}
      module={module}
      options={{
        ...actionGroupOptions,
        className: actionGroupOptions.className ?? 'flex flex-wrap items-center justify-end gap-2',
        componentSystem,
        designSystem,
      }}
      view={{
        ...view,
        Actions: [...actions],
        Chrome: null,
      }}
    />
  )
}

function hasActionChromeSlotAtPlacement(
  view: ViewDefinition,
  placement: string | number,
) {
  return view.Chrome?.Slots.some((slot) =>
    isViewChromeSlotKind(slot, viewChromeSlotKinds.actions) &&
    isViewChromeSlotPlacementValue(resolveViewChromeSlotPlacement(slot), placement)) ?? false
}

function hasStatusChromeSlotAtPlacement(
  view: ViewDefinition,
  placement: string | number,
) {
  return view.Chrome?.Slots.some((slot) =>
    isViewChromeSlotKind(slot, viewChromeSlotKinds.status) &&
    isViewChromeSlotPlacementValue(resolveViewChromeSlotPlacement(slot), placement)) ?? false
}

function resolvePromptLayoutClasses(
  view: ViewDefinition,
  dataSourceResolver: PresentationDataSourceResolver,
) {
  if (view.Design?.Layout === 'expanded') {
    return {
      containerClassName: 'h-full max-w-6xl overflow-hidden',
      contentClassName: 'min-h-0 flex-1 overflow-hidden',
    }
  }

  if (view.Design?.Layout === 'active-content-expanded' && isPromptContentActive(view, dataSourceResolver)) {
    return {
      containerClassName: 'h-full max-w-6xl overflow-hidden',
      contentClassName: 'min-h-0 flex-1 overflow-hidden',
    }
  }

  return {}
}

function isPromptContentActive(
  view: ViewDefinition,
  dataSourceResolver: PresentationDataSourceResolver,
) {
  return resolvePresentationViewDataSourceIds(view).some((dataSourceId) => {
    const state = dataSourceResolver.resolve(dataSourceId)
    return Boolean(
      state?.isPending ||
      state?.isFetching ||
      state?.data !== null && state?.data !== undefined,
    )
  })
}

function renderPromptRegion<TContext>({
  componentSystem,
  context,
  dataSourceResolver,
  module,
  region,
  regionRenderers,
  renderRegion,
  renderView,
  view,
}: ProjectedPromptRegionRenderContext<TContext> & {
  readonly componentSystem: PresentationComponentSystem
  readonly regionRenderers?: Readonly<Record<string, ProjectedPromptRegionRenderer<TContext>>>
  readonly renderRegion?: ProjectedPromptRegionRenderer<TContext>
}) {
  const regionContext = {
    context,
    dataSourceResolver,
    module,
    region,
    renderView,
    view,
  } satisfies ProjectedPromptRegionRenderContext<TContext>
  const rendered = regionRenderers?.[region.Id]?.(regionContext) ?? renderRegion?.(regionContext)
  if (rendered !== undefined) {
    return (
      <Fragment key={region.Id}>
        {componentSystem.prompts.PromptRegion({
          region,
          viewId: view.Id,
          children: rendered,
        })}
      </Fragment>
    )
  }

  if (!renderView || region.ViewIds.length === 0) {
    return null
  }

  return (
    <Fragment key={region.Id}>
      {componentSystem.prompts.PromptRegion({
        region,
        viewId: view.Id,
        children: region.ViewIds.map((viewId) => (
          <div className="h-full min-h-0" key={viewId}>
            {renderView(viewId)}
          </div>
        )),
      })}
    </Fragment>
  )
}

function isActionRegion(region: ViewRegionDefinition, actionRegionId: string) {
  return region.Id === actionRegionId || region.Kind === viewRegionKinds.footer
}

function resolvePromptDismissAction<TContext>({
  actionGroupOptions,
  context,
  dataSourceResolver,
  module,
  view,
}: {
  readonly actionGroupOptions?: PresentationActionGroupOptions<TContext>
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly module: PresentationModuleDefinition
  readonly view: ViewDefinition
}) {
  const dismissPolicy = view.PromptDismiss
  if (!dismissPolicy || !actionGroupOptions?.executeAction) {
    return null
  }

  const placement =
    getPresentationViewProjectedActionPlacements(view)
      .find((candidate) => candidate.ActionId === dismissPolicy.DismissActionId) ??
    ({
      ActionId: dismissPolicy.DismissActionId,
      Icon: 'x',
      Label: 'Close',
      Region: 'dismiss',
    } satisfies ActionPlacementDefinition)
  const action = findPresentationAction<ActionDefinition>(
    module,
    placement.ActionId,
  )
  const invalidatedDataSourceIds =
    action?.Result?.InvalidateDataSourceIds ??
    resolvePresentationViewDataSourceIds(view)
  const isFetching = isPresentationViewFetching(
    {
      ...view,
      DataSourceIds: invalidatedDataSourceIds,
    },
    dataSourceResolver,
  )
  const baseContext = {
    action,
    context,
    dataSourceResolver,
    invalidatedDataSourceIds,
    isFetching,
    module,
    placement,
    view,
  } satisfies PresentationActionRenderContext<TContext>
  const resolvedInvalidatedDataSourceIds =
    actionGroupOptions.resolveInvalidatedDataSourceIds?.(baseContext) ??
    invalidatedDataSourceIds
  const resolvedContext = {
    ...baseContext,
    invalidatedDataSourceIds: resolvedInvalidatedDataSourceIds,
  } satisfies PresentationActionRenderContext<TContext>
  const actionState = actionGroupOptions.resolveActionState?.(resolvedContext) ?? {}
  const isBlocked = resolvedInvalidatedDataSourceIds.some(
    (dataSourceId) => dataSourceResolver.resolve(dataSourceId)?.isBlocked,
  )
  const canExecute = actionGroupOptions.canExecuteAction?.(resolvedContext) ?? true
  const isHidden = Boolean(actionState.isHidden)
  const isPending = Boolean(actionState.isPending)
  const canDismiss = Boolean(
    canExecute &&
    !actionState.isDisabled &&
    !isBlocked &&
    !isFetching &&
    (!dismissPolicy.DisableWhenActionPending || !isPending),
  )

  return {
    canDismiss,
    execute: () => {
      if (isHidden || !canDismiss) {
        return
      }

      void actionGroupOptions.executeAction?.(resolvedContext)
    },
    isHidden,
  }
}

function toDomId(value: string) {
  return value.replace(/[^a-zA-Z0-9_-]+/g, '-')
}

function cn(...values: readonly (false | null | string | undefined)[]) {
  return values.filter(Boolean).join(' ')
}
