import type {
  ActionDefinition,
} from './module'
import type {
  PresentationActionRuntimeBinding,
} from './presentation-action-runtime-binding'
import {
  createPresentationActionRuntimeBinding,
} from './presentation-action-runtime-binding'
import type {
  ProjectedCollectionActionExecutionContext,
  ProjectedNavigateHref,
} from './projected-collection-runtime'
import {
  actionKinds,
  presentationBindingKinds,
} from '@cohesive/presentation-contracts'

export interface ProjectProjectedCollectionActionRuntimeBindingsOptions {
  readonly navigateHref?: ProjectedNavigateHref
}

/**
 * Projects collection row/selection action semantics onto frontend-local
 * execution. The collection runtime supplies row or selection context at
 * invocation time; these bindings only decide how an action id is interpreted.
 */
export function projectProjectedCollectionActionRuntimeBindings<
  TData extends object,
  TLabel = string,
>({
  navigateHref,
}: ProjectProjectedCollectionActionRuntimeBindingsOptions): readonly PresentationActionRuntimeBinding<
  ProjectedCollectionActionExecutionContext<TData>,
  TLabel
>[] {
  return [
    createPresentationActionRuntimeBinding<
      ProjectedCollectionActionExecutionContext<TData>,
      TLabel
    >({
      id: 'collection-context-action',
      predicate: ({ action }) => isCollectionNavigationAction(action),
      project: () => ({
        canExecute: (context) =>
          canExecuteCollectionContextAction({
            context,
            navigateHref,
          }),
        execute: (context) => {
          if (context.href) {
            navigateHref?.(context.href)
          }
        },
      }),
    }),
  ]
}

function canExecuteCollectionContextAction<TData extends object>({
  context,
  navigateHref,
}: {
  readonly context: ProjectedCollectionActionExecutionContext<TData>
  readonly navigateHref?: ProjectedNavigateHref
}) {
  return Boolean(navigateHref && context.href)
}

function isCollectionNavigationAction(action: ActionDefinition) {
  return action.Kind === actionKinds.navigationAction ||
    String(action.Kind).toLocaleLowerCase() === 'navigationaction' ||
    action.Binding.Kind === presentationBindingKinds.navigationRoute ||
    String(action.Binding.Kind).toLocaleLowerCase() === 'navigationroute' ||
    Boolean(action.Binding.RouteId || action.Result?.NavigateToRouteId)
}
