import type {
  ActionDefinition,
  ActionPlacementDefinition,
  PresentationModuleDefinition,
} from './module'
import {
  findPresentationAction,
} from './module'
import type {
  PresentationActionRuntimeRegistry,
} from './presentation-action-runtime-projection'
import type {
  PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'

export interface ProjectPresentationActionRuntimeBindingDiagnosticsOptions<
  TExecuteContext,
  TLabel,
> {
  readonly actionPlacements: readonly ActionPlacementDefinition[]
  readonly module: Pick<PresentationModuleDefinition, 'Actions'> | null
  readonly runtimes: PresentationActionRuntimeRegistry<TExecuteContext, TLabel>
  readonly source: string
}

/**
 * Reports placed actions that reached the frontend but did not receive an
 * executable runtime interpretation.
 *
 * This keeps IR expansion honest: adding a backend action should either bind a
 * frontend runtime or show up as a projection TODO.
 */
export function projectPresentationActionRuntimeBindingDiagnostics<
  TExecuteContext,
  TLabel,
>({
  actionPlacements,
  module,
  runtimes,
  source,
}: ProjectPresentationActionRuntimeBindingDiagnosticsOptions<
  TExecuteContext,
  TLabel
>): readonly PresentationProjectionDiagnostic[] {
  return actionPlacements.flatMap((placement) => {
    const action = findPresentationAction<ActionDefinition>(module, placement.ActionId)
    if (!action) {
      return [
        {
          details: {
            actionId: placement.ActionId,
            placementIntent: placement.Intent,
            placementRegion: placement.Region,
          },
          id: `action-runtime.${placement.ActionId}.missing-action`,
          message:
            `Placed action '${placement.ActionId}' is referenced by the presentation IR ` +
            'but is not present in the action catalog.',
          severity: 'error',
          source,
          subject: {
            id: placement.ActionId,
            kind: 'action',
          },
        } satisfies PresentationProjectionDiagnostic,
      ]
    }

    const runtime = runtimes[action.Id]
    if (runtime?.execute) {
      return []
    }

    return [
      createActionRuntimeDiagnostic({
        action,
        message:
          `Action '${action.Name}' is placed in '${placement.Region}' but has no ` +
          'frontend runtime binding.',
        placement,
        reason: 'missing-execute-binding',
        source,
      }),
    ]
  })
}

function createActionRuntimeDiagnostic({
  action,
  message,
  placement,
  reason,
  source,
}: {
  readonly action: ActionDefinition
  readonly message: string
  readonly placement: ActionPlacementDefinition
  readonly reason: string
  readonly source: string
}): PresentationProjectionDiagnostic {
  return {
    details: {
      actionId: action.Id,
      bindingKind: action.Binding.Kind,
      kind: action.Kind,
      placementIntent: placement.Intent,
      placementRegion: placement.Region,
      scope: action.Scope,
    },
    id: `action-runtime.${action.Id}.${reason}`,
    message,
    severity: 'warning',
    source,
    subject: {
      id: action.Id,
      kind: 'action',
      name: action.Name,
    },
  }
}
