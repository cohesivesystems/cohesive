import type {
  DocumentProfileProjection,
} from './document-module'
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
  findDocumentProcessPreviewActions,
} from './projected-document-action-projection'

export interface ProjectDocumentActionRuntimeProfileDiagnosticsOptions {
  readonly actionPlacements: readonly ActionPlacementDefinition[]
  readonly dataSourceId: string
  readonly documentProfile: Pick<
    DocumentProfileProjection,
    'ActionRuntimeProfiles' | 'Id' | 'Name'
  >
  readonly module: Pick<PresentationModuleDefinition, 'Actions'> | null
  readonly source?: string
}

/**
 * Reports process-preview actions that are declared on a document profile but
 * have no matching document action runtime profile.
 */
export function projectDocumentActionRuntimeProfileDiagnostics({
  actionPlacements,
  dataSourceId,
  documentProfile,
  module,
  source = 'document-action-runtime-profile-projection',
}: ProjectDocumentActionRuntimeProfileDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  const actionRuntimeProfiles = documentProfile.ActionRuntimeProfiles ?? []
  const placedActionIds = new Set(actionPlacements.map((placement) => placement.ActionId))
  const profiledActionIds = new Set(
    actionRuntimeProfiles.map((profile) => profile.ActionId),
  )
  const processPreviewActions = distinctActionsById(
    findDocumentProcessPreviewActions({
      actionPlacements,
      dataSourceId,
      module,
    }),
  )
  const processPreviewActionIds = new Set(processPreviewActions.map((action) => action.Id))

  const missingProfileDiagnostics = processPreviewActions
    .filter((action) => !profiledActionIds.has(action.Id))
    .map((action) =>
      createMissingActionRuntimeProfileDiagnostic({
        action,
        documentProfile,
        source,
      }),
    )

  const invalidProfileDiagnostics = actionRuntimeProfiles.flatMap((profile) => {
    const action = findPresentationAction<ActionDefinition>(module, profile.ActionId)
    if (!action) {
      return [
        createInvalidActionRuntimeProfileDiagnostic({
          details: {
            actionId: profile.ActionId,
            documentProfileId: documentProfile.Id,
            runtimeProfileId: profile.Id,
          },
          documentProfile,
          message:
            `Document action runtime profile '${profile.Name ?? profile.Id}' references ` +
            `action '${profile.ActionId}', but that action is not present in the catalog.`,
          reason: 'missing-action',
          severity: 'error',
          source,
          subject: {
            id: profile.Id,
            kind: 'document-action-runtime-profile',
            name: profile.Name,
          },
        }),
      ]
    }

    if (!placedActionIds.has(profile.ActionId)) {
      return [
        createInvalidActionRuntimeProfileDiagnostic({
          details: {
            actionId: profile.ActionId,
            documentProfileId: documentProfile.Id,
            runtimeProfileId: profile.Id,
          },
          documentProfile,
          message:
            `Document action runtime profile '${profile.Name ?? profile.Id}' references ` +
            `action '${action.Name}', but that action is not placed on this document profile.`,
          reason: 'unplaced-action',
          severity: 'warning',
          source,
          subject: {
            id: profile.ActionId,
            kind: 'action',
            name: action.Name,
          },
        }),
      ]
    }

    if (!processPreviewActionIds.has(profile.ActionId)) {
      return [
        createInvalidActionRuntimeProfileDiagnostic({
          details: {
            actionId: profile.ActionId,
            bindingKind: action.Binding.Kind,
            documentProfileId: documentProfile.Id,
            flowId: action.Preparation?.FlowId ?? null,
            kind: action.Kind,
            runtimeProfileId: profile.Id,
          },
          documentProfile,
          message:
            `Document action runtime profile '${profile.Name ?? profile.Id}' references ` +
            `action '${action.Name}', but that action is not a document process-preview action.`,
          reason: 'unsupported-action',
          severity: 'warning',
          source,
          subject: {
            id: profile.ActionId,
            kind: 'action',
            name: action.Name,
          },
        }),
      ]
    }

    if (
      profile.FlowId &&
      action.Preparation?.FlowId &&
      profile.FlowId !== action.Preparation.FlowId
    ) {
      return [
        createInvalidActionRuntimeProfileDiagnostic({
          details: {
            actionFlowId: action.Preparation.FlowId,
            actionId: profile.ActionId,
            documentProfileId: documentProfile.Id,
            runtimeProfileFlowId: profile.FlowId,
            runtimeProfileId: profile.Id,
          },
          documentProfile,
          message:
            `Document action runtime profile '${profile.Name ?? profile.Id}' declares flow ` +
            `'${profile.FlowId}', but action '${action.Name}' prepares flow ` +
            `'${action.Preparation.FlowId}'.`,
          reason: 'flow-mismatch',
          severity: 'error',
          source,
          subject: {
            id: profile.ActionId,
            kind: 'action',
            name: action.Name,
          },
        }),
      ]
    }

    return []
  })

  return [
    ...missingProfileDiagnostics,
    ...invalidProfileDiagnostics,
  ]
}

function createMissingActionRuntimeProfileDiagnostic({
  action,
  documentProfile,
  source,
}: {
  readonly action: ActionDefinition
  readonly documentProfile: Pick<DocumentProfileProjection, 'Id' | 'Name'>
  readonly source: string
}): PresentationProjectionDiagnostic {
  return {
    details: {
      actionId: action.Id,
      documentProfileId: documentProfile.Id,
      flowId: action.Preparation?.FlowId ?? null,
    },
    id: `document-action-runtime-profile.${documentProfile.Id}.${action.Id}.missing`,
    message:
      `Document profile '${documentProfile.Name ?? documentProfile.Id}' places ` +
      `process-preview action '${action.Name}', but does not declare an action runtime profile.`,
    severity: 'warning',
    source,
    subject: {
      id: action.Id,
      kind: 'action',
      name: action.Name,
    },
  }
}

function createInvalidActionRuntimeProfileDiagnostic({
  details,
  documentProfile,
  message,
  reason,
  severity,
  source,
  subject,
}: {
  readonly details: Readonly<Record<string, unknown>>
  readonly documentProfile: Pick<DocumentProfileProjection, 'Id'>
  readonly message: string
  readonly reason: string
  readonly severity: PresentationProjectionDiagnostic['severity']
  readonly source: string
  readonly subject: PresentationProjectionDiagnostic['subject']
}): PresentationProjectionDiagnostic {
  return {
    details,
    id: `document-action-runtime-profile.${documentProfile.Id}.${details.runtimeProfileId}.${reason}`,
    message,
    severity,
    source,
    subject,
  }
}

function distinctActionsById(
  actions: readonly ActionDefinition[],
): readonly ActionDefinition[] {
  const seen = new Set<string>()
  return actions.filter((action) => {
    if (seen.has(action.Id)) {
      return false
    }

    seen.add(action.Id)
    return true
  })
}
