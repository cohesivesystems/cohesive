import type {
  ActionDefinition,
} from './module'
import type {
  PresentationActionRuntimeBinding,
} from './presentation-action-runtime-binding'
import {
  createPresentationActionRuntimeBinding,
} from './presentation-action-runtime-binding'
import {
  actionKinds,
  presentationBindingKinds,
} from '@cohesive/presentation-contracts'

export interface ProjectPresentationNavigationActionRuntimeBindingsOptions {
  readonly navigateHref: (href: string) => void
  readonly resolveHref: (action: ActionDefinition) => string | null
}

/**
 * Projects parameterless navigation actions into local navigation runtime
 * bindings. The presentation IR declares route navigation semantics; the host
 * still supplies href construction and navigation side effects.
 */
export function projectPresentationNavigationActionRuntimeBindings<
  TExecuteContext = unknown,
  TLabel = string,
>({
  navigateHref,
  resolveHref,
}: ProjectPresentationNavigationActionRuntimeBindingsOptions): readonly PresentationActionRuntimeBinding<
  TExecuteContext,
  TLabel
>[] {
  return [
    createPresentationActionRuntimeBinding<TExecuteContext, TLabel>({
      bindingKind: presentationBindingKinds.navigationRoute,
      id: 'parameterless-navigation-route',
      kind: actionKinds.navigationAction,
      predicate: ({ action }) => action.Parameters.length === 0,
      project: ({ action }) => {
        const href = resolveHref(action)
        return {
          execute: () => {
            if (href) {
              navigateHref(href)
            }
          },
          isDisabled: !href,
        }
      },
    }),
  ]
}
