import { useMemo, type ReactNode } from 'react'

import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  type PresentationActionButtonSize,
  type PresentationActionButtonVariant,
  type PresentationDesignSystem,
} from '@cohesive/presentation-tailwind'
import {
  defaultPresentationComponentSet,
  findPresentationAction,
  getPresentationViewProjectedActionPlacements,
  isPresentationViewFetching,
  presentationActionIconIds,
  projectPresentationActionRuntimeBindingDiagnostics,
  createPresentationTestAttributes,
  resolvePresentationViewDataSourceIds,
  type ActionDefinition,
  type ActionPlacementDefinition,
  type PresentationDataSourceResolver,
  type PresentationModuleDefinition,
  type PresentationActionRuntimeRegistry,
  type ViewDefinition,
} from '@cohesive/presentation-core'
import {
  renderPresentationIcon,
  standardLucidePresentationIconRegistry,
  type PresentationIconRegistry,
} from './presentation-icon-registry'
import {
  projectPresentationActionIconDiagnostics,
  projectPresentationIconDiagnostics,
} from './presentation-icon-diagnostics'
import {
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesive/presentation-react'

export interface PresentationActionGroupOptions<TContext> {
  /** Default button size used when no per-action resolver or design rule applies. */
  readonly buttonSize?: PresentationActionButtonSize

  /** Default button variant used when no per-action resolver or design rule applies. */
  readonly buttonVariant?: PresentationActionButtonVariant

  /** Host predicate that decides whether a projected action can execute. */
  readonly canExecuteAction?: (context: PresentationActionRenderContext<TContext>) => boolean

  /** CSS class applied to the action group container. */
  readonly className?: string

  /** Component set identifier used for icon and diagnostic projection. */
  readonly componentSet?: string

  /** Concrete component system that supplies the action button implementation. */
  readonly componentSystem?: PresentationComponentSystem

  /** Design system used to resolve default action button presentation. */
  readonly designSystem?: PresentationDesignSystem

  /** Host executor invoked when an enabled action button is clicked. */
  readonly executeAction?: (context: PresentationActionRenderContext<TContext>) => Promise<void> | void

  /** Icon registry used to render action and pending-state icons. */
  readonly iconRegistry?: PresentationIconRegistry<ActionPlacementDefinition>

  /** Host predicate that reports whether an action has an executable runtime binding. */
  readonly isActionRuntimeBound?: (context: PresentationActionRenderContext<TContext>) => boolean

  /** Optional override for rendering the icon portion of each action button. */
  readonly renderActionIcon?: (context: PresentationActionRenderContext<TContext>) => ReactNode

  /** Optional override for rendering the label portion of each action button. */
  readonly renderActionLabel?: (context: PresentationActionRenderContext<TContext>) => ReactNode

  /** Optional content rendered before the projected action buttons. */
  readonly renderLeading?: (context: PresentationActionGroupRenderContext<TContext>) => ReactNode

  /** Resolves transient render state such as hidden, disabled, pending, or label override. */
  readonly resolveActionState?: (
    context: PresentationActionRenderContext<TContext>,
  ) => PresentationActionRenderState | null | undefined

  /** Resolves a per-action button size override. */
  readonly resolveButtonSize?: (
    context: PresentationActionRenderContext<TContext>,
  ) => PresentationActionButtonSize | undefined

  /** Resolves a per-action button variant override. */
  readonly resolveButtonVariant?: (
    context: PresentationActionRenderContext<TContext>,
  ) => PresentationActionButtonVariant | undefined

  /** Resolves data sources that block this action while they are busy. */
  readonly resolveInvalidatedDataSourceIds?: (context: PresentationActionRenderContext<TContext>) => readonly string[]

  /** Runtime registry used for projection diagnostics and host execution binding. */
  readonly runtimes?: PresentationActionRuntimeRegistry<
    PresentationActionRenderContext<TContext>,
    ReactNode
  >
}

/**
 * Host-resolved render state for a projected action button.
 */
export interface PresentationActionRenderState {
  /** Tooltip text or richer node explaining why the action is disabled. */
  readonly disabledReason?: ReactNode

  /** Whether the action should render disabled even if runtime checks pass. */
  readonly isDisabled?: boolean

  /** Whether the action should be omitted from the group. */
  readonly isHidden?: boolean

  /** Whether the action is currently pending host work. */
  readonly isPending?: boolean

  /** Label override for the action button. */
  readonly label?: ReactNode
}

/**
 * Shared render context for the whole action group.
 */
export interface PresentationActionGroupRenderContext<TContext> {
  /** Host-specific context threaded through action rendering and execution. */
  readonly context: TContext

  /** Resolver used to inspect data-source fetch and block state. */
  readonly dataSourceResolver: PresentationDataSourceResolver

  /** Module fragment used to resolve actions and targets. */
  readonly module: Pick<PresentationModuleDefinition, 'Actions' | 'Targets'>

  /** View whose projected actions are rendered by the group. */
  readonly view: ViewDefinition
}

/**
 * Render and execution context for a single projected action placement.
 */
export interface PresentationActionRenderContext<TContext>
  extends PresentationActionGroupRenderContext<TContext> {
  /** Resolved module action for the placement, or null when the module is incomplete. */
  readonly action: ActionDefinition | null

  /** Data sources invalidated by the action or implied by the view. */
  readonly invalidatedDataSourceIds: readonly string[]

  /** Whether the action is blocked by active view/data-source fetch state. */
  readonly isFetching: boolean

  /** View-local placement that introduced this action button. */
  readonly placement: ActionPlacementDefinition
}

/**
 * Props for rendering a group of actions projected by a presentation view.
 */
export interface PresentationActionGroupProps<TContext>
  extends PresentationActionGroupRenderContext<TContext> {
  /** Optional host integration and rendering overrides. */
  readonly options?: PresentationActionGroupOptions<TContext>
}

/**
 * Renders the view-scoped action placements declared by Cohesive presentation
 * IR. The component keeps module action semantics, host runtime binding, design
 * defaults, diagnostics, and data-source blocking in a single reusable action
 * surface.
 */
export function PresentationActionGroup<TContext>({
  context,
  dataSourceResolver,
  module,
  options,
  view,
}: PresentationActionGroupProps<TContext>) {
  const ActionButton = options?.componentSystem?.actions.ActionButton
  const designSystem = options?.designSystem
  const actionPlacements = useMemo(
    () => getPresentationViewProjectedActionPlacements(view),
    [view],
  )
  const iconRegistry =
    options?.iconRegistry ??
    (standardLucidePresentationIconRegistry as PresentationIconRegistry<ActionPlacementDefinition>)
  const componentSet = options?.componentSet ?? defaultPresentationComponentSet
  const actionIconDiagnostics = useMemo(
    () => [
      ...projectPresentationActionIconDiagnostics({
        actionPlacements,
        module,
        componentSet,
        registry: iconRegistry,
        source: `presentation-action-group-icons:${view.Id}`,
        surfaceId: view.Id,
        surfaceName: view.Name,
      }),
      ...projectPresentationIconDiagnostics({
        icons: actionPlacements.length === 0
          ? []
          : [{
              icon: presentationActionIconIds.pending,
              id: `${view.Id}:pending`,
              kind: 'action-state-icon',
              label: 'Pending action',
            }],
        module,
        componentSet,
        source: `presentation-action-group-icons:${view.Id}`,
        surfaceId: view.Id,
        surfaceName: view.Name,
      }),
    ],
    [
      actionPlacements,
      componentSet,
      iconRegistry,
      module,
      view.Id,
      view.Name,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    `presentation-action-group-icons:${view.Id}`,
    actionIconDiagnostics,
  )

  const actionRuntimeDiagnostics = useMemo(
    () => {
      if (!options?.isActionRuntimeBound) {
        return []
      }

      const runtimes: Record<
        string,
        NonNullable<PresentationActionRuntimeRegistry<
          PresentationActionRenderContext<TContext>,
          ReactNode
        >[string]>
      > = {}
      for (const [actionId, runtime] of Object.entries(options.runtimes ?? {})) {
        if (runtime) {
          runtimes[actionId] = runtime
        }
      }
      for (const placement of actionPlacements) {
        const actionContext = createActionRenderContext({
          context,
          dataSourceResolver,
          module,
          placement,
          view,
        })
        if (options.isActionRuntimeBound(actionContext)) {
          runtimes[placement.ActionId] ??= { execute: () => undefined }
        }
      }

      return projectPresentationActionRuntimeBindingDiagnostics({
        actionPlacements,
        module,
        runtimes,
        source: `presentation-action-group-runtime:${view.Id}`,
      })
    },
    [
      actionPlacements,
      context,
      dataSourceResolver,
      module,
      options,
      view,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    `presentation-action-group-runtime:${view.Id}`,
    actionRuntimeDiagnostics,
  )

  if (!ActionButton || !designSystem) {
    return null
  }
  if (actionPlacements.length === 0) {
    return null
  }

  const groupContext = {
    context,
    dataSourceResolver,
    module,
    view,
  } satisfies PresentationActionGroupRenderContext<TContext>
  const leading = options?.renderLeading?.(groupContext)

  return (
    <div className={options?.className ?? 'flex flex-wrap items-center gap-2'}>
      {leading}
      {actionPlacements.map((placement) => {
        const actionContext = createActionRenderContext({
          context,
          dataSourceResolver,
          groupContext,
          module,
          placement,
          view,
        })
        const { action, invalidatedDataSourceIds, isFetching } = actionContext
        const resolvedInvalidatedDataSourceIds =
          options?.resolveInvalidatedDataSourceIds?.(actionContext) ??
          invalidatedDataSourceIds
        const isBlocked = resolvedInvalidatedDataSourceIds.some(
          (dataSourceId) => dataSourceResolver.resolve(dataSourceId)?.isBlocked,
        )
        const resolvedActionContext = {
          ...actionContext,
          invalidatedDataSourceIds: resolvedInvalidatedDataSourceIds,
        } satisfies PresentationActionRenderContext<TContext>
        const isRuntimeBound =
          options?.isActionRuntimeBound?.(resolvedActionContext) ??
          Boolean(options?.executeAction)
        const canExecute = options?.canExecuteAction?.(resolvedActionContext) ?? true
        const actionState = options?.resolveActionState?.(resolvedActionContext) ?? {}
        if (actionState.isHidden) {
          return null
        }
        const isPending = Boolean(actionState.isPending)
        const disabledTitle = typeof actionState.disabledReason === 'string'
          ? actionState.disabledReason
          : undefined
        const renderedLabel =
          actionState.label ??
          options?.renderActionLabel?.(resolvedActionContext) ??
          placement.Label ??
          action?.Name ??
          placement.ActionId

        return (
          <ActionButton
            aria-label={action?.Accessibility?.Label ?? String(renderedLabel)}
            {...createPresentationTestAttributes({
              actionId: placement.ActionId,
              viewId: view.Id,
            })}
            disabled={
              !options?.executeAction ||
              !isRuntimeBound ||
              !canExecute ||
              Boolean(actionState.isDisabled) ||
              isBlocked ||
              isFetching ||
              isPending
            }
            key={`${placement.Region}:${placement.ActionId}`}
            onClick={() => {
              void options?.executeAction?.(resolvedActionContext)
            }}
            size={
              options?.resolveButtonSize?.(resolvedActionContext) ??
              options?.buttonSize ??
              designSystem.components.actionButton.size({
                action,
                placement,
              })
            }
            title={disabledTitle}
            type="button"
            variant={
              options?.resolveButtonVariant?.(resolvedActionContext) ??
              options?.buttonVariant ??
              designSystem.components.actionButton.variant({
                action,
                placement,
              })
            }
          >
            {options?.renderActionIcon?.(resolvedActionContext) ??
              renderDefaultActionIcon({
                iconRegistry: options?.iconRegistry,
                isPending: isFetching || isPending,
                componentSet,
                module,
                placement,
              })}
            {renderedLabel}
          </ActionButton>
        )
      })}
    </div>
  )
}

function createActionRenderContext<TContext>({
  context,
  dataSourceResolver,
  groupContext,
  module,
  placement,
  view,
}: {
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly groupContext?: PresentationActionGroupRenderContext<TContext>
  readonly module: Pick<PresentationModuleDefinition, 'Actions' | 'Targets'>
  readonly placement: ActionPlacementDefinition
  readonly view: ViewDefinition
}): PresentationActionRenderContext<TContext> {
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

  return {
    ...(groupContext ?? {
      context,
      dataSourceResolver,
      module,
      view,
    }),
    action,
    invalidatedDataSourceIds,
    isFetching,
    placement,
  } satisfies PresentationActionRenderContext<TContext>
}

function renderDefaultActionIcon({
  componentSet = defaultPresentationComponentSet,
  iconRegistry = standardLucidePresentationIconRegistry as PresentationIconRegistry<ActionPlacementDefinition>,
  isPending,
  module,
  placement,
}: {
  readonly componentSet?: string
  readonly iconRegistry?: PresentationIconRegistry<ActionPlacementDefinition>
  readonly isPending: boolean
  readonly module: Pick<PresentationModuleDefinition, 'Targets'>
  readonly placement: ActionPlacementDefinition
}) {
  return isPending ? (
    renderPresentationIcon({
      className: 'size-3.5 animate-spin',
      componentSet,
      icon: presentationActionIconIds.pending,
      module,
      registry: iconRegistry,
      subject: placement,
    }) ?? renderPresentationIcon({
      className: 'size-3.5 animate-spin',
      icon: 'loader-circle',
      registry: iconRegistry,
      subject: placement,
    })
  ) : renderPresentationIcon({
    componentSet,
    icon: placement.Icon,
    module,
    registry: iconRegistry,
    subject: placement,
  })
}
