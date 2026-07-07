import type {
  ActionDefinition,
  ActionPlacementDefinition,
  PresentationModuleDefinition,
} from './module'
import {
  findPresentationAction,
} from './module'
import type {
  PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'
import {
  createPresentationEnumDiscriminator,
  matchesPresentationEnum,
} from './target-bindings'
import {
  actionSemanticsKinds,
  localDocumentEditorActionKindLabels,
  localDocumentEditorActionKinds,
} from '@cohesive/presentation-contracts'

export type LocalDocumentEditorActionBindingIntent = 'format' | 'reset'

export interface ProjectLocalDocumentEditorActionBindingDiagnosticsOptions {
  readonly actionPlacements: readonly ActionPlacementDefinition[]
  readonly module: Pick<PresentationModuleDefinition, 'Actions'> | null
  readonly supportedIntents: readonly LocalDocumentEditorActionBindingIntent[]
}

/**
 * Reports local document editor semantics that the current frontend adapter has
 * not interpreted. These diagnostics are intentionally about the frontend
 * binding boundary: the backend declares the action meaning, while Monaco owns
 * the local execution model.
 */
export function projectLocalDocumentEditorActionBindingDiagnostics({
  actionPlacements,
  module,
  supportedIntents,
}: ProjectLocalDocumentEditorActionBindingDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  const supportedIntentSet = new Set(supportedIntents)
  return resolvePlacedActions(module, actionPlacements).flatMap(({ action, placement }) => {
    if (!matchesLocalDocumentEditorActionSemantics(action)) {
      return []
    }

    const localDocumentEditor = action.Semantics?.LocalDocumentEditor
    if (!localDocumentEditor) {
      return [
        createDiagnostic({
          action,
          message:
            `Action '${action.Name}' declares local document editor semantics ` +
            'but does not specify an editor action kind.',
          placement,
          reason: 'missing-local-document-editor-action-kind',
          severity: 'error',
        }),
      ]
    }

    const intent = resolveLocalDocumentEditorActionIntent(localDocumentEditor.Kind)
    if (!intent) {
      return [
        createDiagnostic({
          action,
          details: {
            localDocumentEditorActionKind: formatLocalDocumentEditorActionKind(
              localDocumentEditor.Kind,
            ),
          },
          message:
            `Action '${action.Name}' declares local document editor action ` +
            `'${formatLocalDocumentEditorActionKind(localDocumentEditor.Kind)}', ` +
            'but this frontend adapter does not recognize that semantic kind.',
          placement,
          reason: 'unknown-local-document-editor-action-kind',
          severity: 'warning',
        }),
      ]
    }

    if (supportedIntentSet.has(intent)) {
      return []
    }

    return [
      createDiagnostic({
        action,
        details: { localDocumentEditorActionKind: intent },
        message:
          `Action '${action.Name}' declares local document editor action '${intent}', ` +
          'but this frontend adapter has not bound an interpretation for it.',
        placement,
        reason: 'unbound-local-document-editor-action-kind',
        severity: 'warning',
      }),
    ]
  })
}

function resolvePlacedActions(
  module: Pick<PresentationModuleDefinition, 'Actions'> | null,
  actionPlacements: readonly ActionPlacementDefinition[],
) {
  return actionPlacements.flatMap((placement) => {
    const action = findPresentationAction<ActionDefinition>(module, placement.ActionId)
    return action ? [{ action, placement }] : []
  })
}

function matchesLocalDocumentEditorActionSemantics(action: ActionDefinition) {
  return Boolean(
    action.Semantics &&
      matchesPresentationEnum(action.Semantics.Kind, localDocumentEditorActionSemanticsKind),
  )
}

function resolveLocalDocumentEditorActionIntent(
  value: string | number | null | undefined,
): LocalDocumentEditorActionBindingIntent | null {
  if (value === null || value === undefined) {
    return null
  }

  if (matchesPresentationEnum(value, localDocumentEditorActionKindByIntent.format)) {
    return 'format'
  }

  if (matchesPresentationEnum(value, localDocumentEditorActionKindByIntent.reset)) {
    return 'reset'
  }

  return null
}

function formatLocalDocumentEditorActionKind(value: string | number | null | undefined) {
  return typeof value === 'number'
    ? localDocumentEditorActionKindLabels[
      value as keyof typeof localDocumentEditorActionKindLabels
    ] ?? String(value)
    : String(value ?? 'unknown')
}

function createDiagnostic({
  action,
  details,
  message,
  placement,
  reason,
  severity,
}: {
  readonly action: ActionDefinition
  readonly details?: Readonly<Record<string, unknown>>
  readonly message: string
  readonly placement: ActionPlacementDefinition
  readonly reason: string
  readonly severity: PresentationProjectionDiagnostic['severity']
}): PresentationProjectionDiagnostic {
  return {
    details: {
      actionId: action.Id,
      placementIntent: placement.Intent,
      placementRegion: placement.Region,
      ...details,
    },
    id: `local-document-editor-action.${action.Id}.${reason}`,
    message,
    severity,
    source: 'document-action-projection',
    subject: {
      id: action.Id,
      kind: 'action',
      name: action.Name,
    },
  }
}

const localDocumentEditorActionSemanticsKind = createPresentationEnumDiscriminator(
  actionSemanticsKinds,
  'localDocumentEditor',
  'LocalDocumentEditor',
)

const localDocumentEditorActionKindByIntent = {
  format: createPresentationEnumDiscriminator(
    localDocumentEditorActionKinds,
    'format',
    'Format',
  ),
  reset: createPresentationEnumDiscriminator(
    localDocumentEditorActionKinds,
    'reset',
    'Reset',
  ),
} as const
