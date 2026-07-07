import type {
  PresentationModuleDefinition,
} from '@cohesive/presentation-core'
import {
  createPresentationProjectionDiagnostic,
  type PresentationProjectionDiagnostic,
} from '@cohesive/presentation-core'
import {
  createPresentationEnumDiscriminator,
  findPresentationComponentBinding,
} from '@cohesive/presentation-core'
import type {
  DocumentWorkspaceRuntimeSnapshot,
} from './document-workspace-runtime'
import {
  workspaceRuntimeComponentRoles,
} from '@cohesive/presentation-contracts'
import {
  presentationBindingKinds,
  presentationTargetKinds,
} from '@cohesive/presentation-contracts'

export interface ProjectDocumentWorkspaceRuntimeBindingDiagnosticsOptions {
  readonly componentSet?: string | null
  readonly module: PresentationModuleDefinition | null
  readonly runtime: DocumentWorkspaceRuntimeSnapshot
  readonly sourceId?: string
  readonly supportedComponentRoles?: readonly string[]
}

const defaultSupportedDocumentWorkspaceRuntimeRoles = [
  workspaceRuntimeComponentRoles.documentWorkspace,
] as const

/**
 * Reports whether the backend WorkspaceRuntime target binding is projected as
 * a semantic component role that this frontend document workspace host
 * interprets. The host still mounts the runtime locally, but this diagnostic
 * keeps the target binding honest and prevents concrete ComponentKey bindings
 * from quietly lingering.
 */
export function projectDocumentWorkspaceRuntimeBindingDiagnostics({
  componentSet,
  module,
  runtime,
  sourceId = 'document-workspace-runtime-binding',
  supportedComponentRoles = defaultSupportedDocumentWorkspaceRuntimeRoles,
}: ProjectDocumentWorkspaceRuntimeBindingDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  if (!module) {
    return []
  }

  const binding = findPresentationComponentBinding(module, {
    bindingKind: createPresentationEnumDiscriminator(
      presentationBindingKinds,
      'workspaceRuntime',
      'WorkspaceRuntime',
    ),
    componentSet,
    id: runtime.workspace.Id,
    targetKind: createPresentationEnumDiscriminator(
      presentationTargetKinds,
      'react',
      'React',
    ),
  })
  const componentKey = binding?.ComponentKey ?? null
  const componentRole = binding?.ComponentRole ?? null
  const supportedRoleSet = new Set(supportedComponentRoles)
  const diagnostics: PresentationProjectionDiagnostic[] = []

  if (!binding) {
    diagnostics.push(
      createWorkspaceRuntimeDiagnostic({
        category: 'missing-binding',
        componentKey,
        componentRole,
        idSuffix: 'missing-binding',
        message:
          `Document workspace '${runtime.workspace.Id}' has no WorkspaceRuntime ` +
          'target binding for the active frontend target.',
        severity: 'warning',
        sourceId,
        status: 'unbound',
        suggestedNextStep:
          'Declare a WorkspaceRuntime target binding with ComponentRole for this document workspace.',
        runtime,
      }),
    )
    return diagnostics
  }

  if (componentKey) {
    diagnostics.push(
      createWorkspaceRuntimeDiagnostic({
        category: 'escape-hatch',
        componentKey,
        componentRole,
        idSuffix: 'component-key-escape-hatch',
        message:
          `Document workspace '${runtime.workspace.Id}' declares concrete ` +
          `workspace runtime component key '${componentKey}'.`,
        severity: 'warning',
        sourceId,
        status: 'escape-hatch',
        suggestedNextStep:
          'Use WorkspaceRuntime ComponentRole for document workspace runtimes and reserve ComponentKey for adapter-specific overrides.',
        runtime,
      }),
    )
  }

  if (!componentRole) {
    diagnostics.push(
      createWorkspaceRuntimeDiagnostic({
        category: 'missing-binding',
        componentKey,
        componentRole,
        idSuffix: 'missing-component-role',
        message:
          `Document workspace '${runtime.workspace.Id}' WorkspaceRuntime binding ` +
          'does not declare a component role.',
        severity: 'warning',
        sourceId,
        status: 'unbound',
        suggestedNextStep:
          `Set ComponentRole to '${workspaceRuntimeComponentRoles.documentWorkspace}'.`,
        runtime,
      }),
    )
    return diagnostics
  }

  const isSupportedRole = supportedRoleSet.has(componentRole)

  diagnostics.push(
    createWorkspaceRuntimeDiagnostic({
      category: isSupportedRole ? 'local-interpretation' : 'missing-binding',
      componentKey,
      componentRole,
      idSuffix: 'component-role-coverage',
      message: isSupportedRole
        ? `Document workspace '${runtime.workspace.Id}' WorkspaceRuntime role ` +
          `'${componentRole}' is interpreted by the local document workspace host.`
        : `Document workspace '${runtime.workspace.Id}' WorkspaceRuntime role ` +
          `'${componentRole}' is not interpreted by the local document workspace host.`,
      severity: isSupportedRole ? 'info' : 'warning',
      sourceId,
      status: isSupportedRole ? 'locally-interpreted' : 'unbound',
      suggestedNextStep: isSupportedRole
        ? undefined
        : 'Bind this WorkspaceRuntime component role in the frontend document workspace host adapter.',
      runtime,
    }),
  )

  return diagnostics
}

function createWorkspaceRuntimeDiagnostic({
  category,
  componentKey,
  componentRole,
  idSuffix,
  message,
  runtime,
  severity,
  sourceId,
  status,
  suggestedNextStep,
}: {
  readonly category: PresentationProjectionDiagnostic['category']
  readonly componentKey: string | null
  readonly componentRole: string | null
  readonly idSuffix: string
  readonly message: string
  readonly runtime: DocumentWorkspaceRuntimeSnapshot
  readonly severity: PresentationProjectionDiagnostic['severity']
  readonly sourceId: string
  readonly status: NonNullable<PresentationProjectionDiagnostic['interpretation']>['status']
  readonly suggestedNextStep?: string
}) {
  return createPresentationProjectionDiagnostic({
    category,
    details: {
      componentKey,
      componentRole,
      documentProfileId: runtime.documentProfile.Id,
      pageViewId: runtime.pageView.Id,
      workspaceId: runtime.workspace.Id,
      workspaceViewId: runtime.workspaceView.Id,
    },
    id: `document-workspace-runtime.${runtime.workspace.Id}.${idSuffix}`,
    interpretation: {
      status,
      target: 'workspace-runtime-component-role',
    },
    message,
    severity,
    source: sourceId,
    subject: {
      id: runtime.workspace.Id,
      kind: 'document-workspace-runtime',
      name: runtime.workspace.Name,
    },
    suggestedNextStep,
  })
}
