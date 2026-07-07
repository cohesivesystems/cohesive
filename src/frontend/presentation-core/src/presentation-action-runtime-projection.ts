import type {
  PresentationModuleDefinition,
} from './module'
import type {
  PresentationActionRuntime,
  PresentationActionRuntimeBinding,
  PresentationActionRuntimeBindingContext,
} from './presentation-action-runtime-binding'

export type PresentationActionRuntimeRegistry<TExecuteContext, TLabel> = Readonly<
  Record<string, PresentationActionRuntime<TExecuteContext, TLabel> | null | undefined>
>

export interface ProjectPresentationActionRuntimeRegistryOptions<
  TExecuteContext,
  TLabel,
> {
  readonly actionIds?: readonly string[]
  readonly module: PresentationModuleDefinition | null
  readonly projections: readonly PresentationActionRuntimeBinding<TExecuteContext, TLabel>[]
}

/**
 * Interprets backend-declared actions through ordered frontend runtime
 * projections.
 *
 * The IR owns action identity and semantics; the frontend contributes concrete
 * local effects by registering projection bindings that match those semantics.
 */
export function projectPresentationActionRuntimeRegistry<TExecuteContext, TLabel>({
  actionIds,
  module,
  projections,
}: ProjectPresentationActionRuntimeRegistryOptions<
  TExecuteContext,
  TLabel
>): PresentationActionRuntimeRegistry<TExecuteContext, TLabel> {
  if (!module) {
    return {}
  }

  const actionsById = new Map(module.Actions.map((action) => [action.Id, action]))
  const candidateActionIds = actionIds ?? module.Actions.map((action) => action.Id)
  const registry: Record<
    string,
    PresentationActionRuntime<TExecuteContext, TLabel> | null | undefined
  > = {}

  for (const actionId of new Set(candidateActionIds)) {
    const action = actionsById.get(actionId)
    if (!action) {
      continue
    }

    const context = { action, actionId, module } satisfies PresentationActionRuntimeBindingContext
    for (const projection of projections) {
      if (!projection.matches(context)) {
        continue
      }

      registry[actionId] = projection.project(context)
      break
    }
  }

  return registry
}
