import type { ReactNode } from 'react'

import type {
  ActionDefinition,
  ActionPlacementDefinition,
  PresentationDataSourceResolver,
  PresentationModuleDefinition,
  ViewDefinition,
} from '@cohesive/presentation-core'
import type {
  PresentationActionRuntimeRegistry,
} from '@cohesive/presentation-core'

export interface PresentationActionRenderState {
  readonly disabledReason?: ReactNode
  readonly isDisabled?: boolean
  readonly isHidden?: boolean
  readonly isPending?: boolean
  readonly label?: ReactNode
}

export interface PresentationActionGroupRenderContext<TContext> {
  readonly context: TContext
  readonly dataSourceResolver: PresentationDataSourceResolver
  readonly module: Pick<PresentationModuleDefinition, 'Actions' | 'Targets'>
  readonly view: ViewDefinition
}

export interface PresentationActionRenderContext<TContext>
  extends PresentationActionGroupRenderContext<TContext> {
  readonly action: ActionDefinition | null
  readonly invalidatedDataSourceIds: readonly string[]
  readonly isFetching: boolean
  readonly placement: ActionPlacementDefinition
}

export interface PresentationActionGroupOptions<TContext> {
  readonly canExecuteAction?: (context: PresentationActionRenderContext<TContext>) => boolean
  readonly executeAction?: (context: PresentationActionRenderContext<TContext>) => Promise<void> | void
  readonly renderActionLabel?: (context: PresentationActionRenderContext<TContext>) => ReactNode
  readonly resolveActionState?: (
    context: PresentationActionRenderContext<TContext>,
  ) => PresentationActionRenderState | null | undefined
}

export interface PresentationActionGroupRuntimeOptions<TContext>
  extends PresentationActionGroupOptions<TContext> {
  readonly runtimes?: PresentationActionRuntimeRegistry<
    PresentationActionRenderContext<TContext>,
    ReactNode
  >
}

/**
 * Creates standard action-group options from a semantic action runtime registry.
 */
export function createPresentationActionGroupRuntimeOptions<TContext>({
  canExecuteAction,
  executeAction,
  renderActionLabel,
  resolveActionState,
  runtimes,
  ...options
}: PresentationActionGroupRuntimeOptions<TContext> = {}): PresentationActionGroupOptions<TContext> {
  const resolvedExecuteAction =
    runtimes || executeAction
      ? (context: PresentationActionRenderContext<TContext>) => {
          const runtime = resolveRuntime(runtimes, context)
          if (
            runtime?.execute &&
            !runtime.isDisabled &&
            !runtime.isHidden &&
            (runtime.canExecute?.(context) ?? true)
          ) {
            return runtime.execute(context)
          }

          return executeAction?.(context)
        }
      : undefined

  return {
    ...options,
    canExecuteAction: (context) => {
      const runtime = resolveRuntime(runtimes, context)
      if (runtime?.isDisabled || runtime?.isHidden) {
        return false
      }

      if (runtime?.canExecute && !runtime.canExecute(context)) {
        return false
      }

      return canExecuteAction?.(context) ?? true
    },
    executeAction: resolvedExecuteAction,
    renderActionLabel,
    resolveActionState: (context) => {
      const runtime = resolveRuntime(runtimes, context)
      const baseState = resolveActionState?.(context)
      return {
        ...baseState,
        disabledReason: runtime?.disabledReason ?? baseState?.disabledReason,
        isDisabled: Boolean(baseState?.isDisabled || runtime?.isDisabled),
        isHidden: Boolean(baseState?.isHidden || runtime?.isHidden),
        isPending: Boolean(baseState?.isPending || runtime?.isPending),
        label:
          runtime?.isPending && runtime.pendingLabel
            ? runtime.pendingLabel
            : (runtime?.label ?? baseState?.label),
      }
    },
  }
}

function resolveRuntime<TContext>(
  runtimes: PresentationActionGroupRuntimeOptions<TContext>['runtimes'] | undefined,
  context: PresentationActionRenderContext<TContext>,
) {
  return runtimes?.[context.placement.ActionId] ?? null
}
