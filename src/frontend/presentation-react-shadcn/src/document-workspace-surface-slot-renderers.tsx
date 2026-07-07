import type { ReactNode } from 'react'

import {
  documentWorkspaceSurfaceSlotRoleKeys,
  resolveDocumentWorkspaceSurfaceSlotRoleKey,
  type ResolvedDocumentWorkspaceSurfaceSlot,
} from '@cohesive/presentation-core'
import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  createPresentationProjectionDiagnostic,
  type PresentationProjectionDiagnostic,
} from '@cohesive/presentation-core'
import { ProjectedViewSurface } from './projected-view-surface'
import type {
  ViewChromeSlotDefinition,
  ViewDefinition,
} from '@cohesive/presentation-core'
import {
  documentWorkspaceSurfaceSlotComponentRoles,
} from '@cohesive/presentation-contracts'

const standardDocumentWorkspaceSurfaceSlotComponentRoleIds = Object.values(
  documentWorkspaceSurfaceSlotComponentRoles,
)

export type DocumentWorkspaceSurfaceSlotRenderer<TActionContext> = (
  context: DocumentWorkspaceSurfaceSlotRenderContext<TActionContext>,
) => ReactNode

export interface DocumentWorkspaceSurfaceSlotRendererRegistry<TActionContext> {
  readonly byComponentKey?: Readonly<Record<string, DocumentWorkspaceSurfaceSlotRenderer<TActionContext>>>
  readonly byComponentRole?: Readonly<Record<string, DocumentWorkspaceSurfaceSlotRenderer<TActionContext>>>
  readonly byRole?: Readonly<Record<string, DocumentWorkspaceSurfaceSlotRenderer<TActionContext>>>
}

export interface DocumentWorkspaceSurfaceSlotRenderContext<TActionContext> {
  readonly actionContext: TActionContext
  readonly auxiliaryContent?: ReactNode
  readonly componentSystem: PresentationComponentSystem
  readonly documentContent: ReactNode
  readonly headerContent?: ReactNode
  readonly headerDescription: string
  readonly headerTitle: string
  readonly pageView: ViewDefinition | null
  readonly pageViewId: string
  readonly renderChromeSlot: (
    slot: ViewChromeSlotDefinition,
    view: ViewDefinition | null,
  ) => ReactNode
  readonly renderDocumentViewState?: (content: ReactNode) => ReactNode
  readonly slot: ResolvedDocumentWorkspaceSurfaceSlot
  readonly workspaceTitle: string
  readonly workspaceView: ViewDefinition | null
}

export interface ResolvedDocumentWorkspaceSurfaceSlotRenderer<TActionContext> {
  readonly bindingSource: 'component-key' | 'component-role' | 'role'
  readonly componentKey?: string | null
  readonly componentRole?: string | null
  readonly renderer: DocumentWorkspaceSurfaceSlotRenderer<TActionContext>
  readonly roleKey: string
}

export const standardDocumentWorkspaceSurfaceSlotRenderers = {
  byComponentRole: {
    [documentWorkspaceSurfaceSlotComponentRoles.header]:
      renderDocumentWorkspaceHeaderSurfaceSlot,
    [documentWorkspaceSurfaceSlotComponentRoles.primarySurface]:
      renderDocumentWorkspacePrimarySurfaceSlot,
    [documentWorkspaceSurfaceSlotComponentRoles.auxiliary]:
      renderDocumentWorkspaceAuxiliarySlot,
  },
  byRole: {
    [documentWorkspaceSurfaceSlotRoleKeys.header]: renderDocumentWorkspaceHeaderSurfaceSlot,
    [documentWorkspaceSurfaceSlotRoleKeys.primarySurface]:
      renderDocumentWorkspacePrimarySurfaceSlot,
    [documentWorkspaceSurfaceSlotRoleKeys.auxiliary]: renderDocumentWorkspaceAuxiliarySlot,
  },
} as const satisfies DocumentWorkspaceSurfaceSlotRendererRegistry<unknown>

export function mergeDocumentWorkspaceSurfaceSlotRendererRegistries<TActionContext>(
  ...registries: readonly (
    DocumentWorkspaceSurfaceSlotRendererRegistry<TActionContext> | null | undefined
  )[]
): DocumentWorkspaceSurfaceSlotRendererRegistry<TActionContext> {
  return {
    byComponentKey: mergeRendererMaps(
      registries.map((registry) => registry?.byComponentKey),
    ),
    byComponentRole: mergeRendererMaps(
      registries.map((registry) => registry?.byComponentRole),
    ),
    byRole: mergeRendererMaps(registries.map((registry) => registry?.byRole)),
  }
}

export function resolveDocumentWorkspaceSurfaceSlotRenderer<TActionContext>(
  slot: ResolvedDocumentWorkspaceSurfaceSlot,
  renderers: DocumentWorkspaceSurfaceSlotRendererRegistry<TActionContext>,
): ResolvedDocumentWorkspaceSurfaceSlotRenderer<TActionContext> | null {
  const roleKey = resolveDocumentWorkspaceSurfaceSlotRoleKey(slot.role, slot.roleLabel)
  const componentRole = slot.renderer?.ComponentRole ?? null
  const componentKey = slot.renderer?.ComponentKey ?? null

  if (componentRole) {
    const renderer = renderers.byComponentRole?.[componentRole]
    if (renderer) {
      return {
        bindingSource: 'component-role',
        componentRole,
        renderer,
        roleKey,
      }
    }
  }

  if (componentKey) {
    const renderer = renderers.byComponentKey?.[componentKey]
    if (renderer) {
      return {
        bindingSource: 'component-key',
        componentKey,
        renderer,
        roleKey,
      }
    }
  }

  const roleRenderer = renderers.byRole?.[roleKey]
  return roleRenderer
    ? {
        bindingSource: 'role',
        componentKey,
        componentRole,
        renderer: roleRenderer,
        roleKey,
      }
    : null
}

export function projectDocumentWorkspaceSurfaceSlotRendererDiagnostics<TActionContext>({
  renderers,
  slots,
  sourceId,
}: {
  readonly renderers: DocumentWorkspaceSurfaceSlotRendererRegistry<TActionContext>
  readonly slots: readonly ResolvedDocumentWorkspaceSurfaceSlot[]
  readonly sourceId: string
}): readonly PresentationProjectionDiagnostic[] {
  const diagnostics: PresentationProjectionDiagnostic[] = []
  diagnostics.push(...projectStandardDocumentWorkspaceSurfaceSlotRendererCoverageDiagnostics({
    renderers,
    sourceId,
  }))

  for (const slot of slots) {
    const renderer = slot.renderer
    if (!renderer) {
      continue
    }

    const componentRole = renderer.ComponentRole ?? null
    const componentKey = renderer.ComponentKey ?? null
    const hasComponentRoleRenderer = componentRole
      ? Boolean(renderers.byComponentRole?.[componentRole])
      : false
    const hasComponentKeyRenderer = componentKey
      ? Boolean(renderers.byComponentKey?.[componentKey])
      : false

    if (!componentRole && !componentKey) {
      diagnostics.push(createSurfaceSlotRendererDiagnostic({
        id: `document-workspace-surface-slot:${slot.id}:renderer-missing-target`,
        message:
          `Document workspace surface slot '${slot.id}' declares a renderer binding ` +
          'without ComponentRole or ComponentKey.',
        severity: 'warning',
        sourceId,
        slot,
        status: 'unbound',
        suggestedNextStep:
          'Set ComponentRole on the slot Renderer binding, or remove the binding and use role fallback.',
      }))
      continue
    }

    if (componentRole && !hasComponentRoleRenderer) {
      diagnostics.push(createSurfaceSlotRendererDiagnostic({
        details: { componentRole },
        id: `document-workspace-surface-slot:${slot.id}:component-role-unbound`,
        message:
          `Document workspace surface slot '${slot.id}' targets ComponentRole ` +
          `'${componentRole}', but no frontend renderer is registered for that role.`,
        severity: 'warning',
        sourceId,
        slot,
        status: renderers.byRole?.[
          resolveDocumentWorkspaceSurfaceSlotRoleKey(slot.role, slot.roleLabel)
        ]
          ? 'locally-interpreted'
          : 'unbound',
        suggestedNextStep:
          'Register a surface-slot renderer for this ComponentRole, or change the backend Renderer binding.',
      }))
    }

    if (!hasComponentRoleRenderer && componentKey && !hasComponentKeyRenderer) {
      diagnostics.push(createSurfaceSlotRendererDiagnostic({
        details: { componentKey },
        id: `document-workspace-surface-slot:${slot.id}:component-key-unbound`,
        message:
          `Document workspace surface slot '${slot.id}' targets ComponentKey ` +
          `'${componentKey}', but no frontend renderer is registered for that key.`,
        severity: 'warning',
        sourceId,
        slot,
        status: renderers.byRole?.[
          resolveDocumentWorkspaceSurfaceSlotRoleKey(slot.role, slot.roleLabel)
        ]
          ? 'locally-interpreted'
          : 'unbound',
        suggestedNextStep:
          'Register a surface-slot renderer for this ComponentKey, or change the backend Renderer binding.',
      }))
    }
  }

  return diagnostics
}

function projectStandardDocumentWorkspaceSurfaceSlotRendererCoverageDiagnostics<TActionContext>({
  renderers,
  sourceId,
}: {
  readonly renderers: DocumentWorkspaceSurfaceSlotRendererRegistry<TActionContext>
  readonly sourceId: string
}): readonly PresentationProjectionDiagnostic[] {
  return standardDocumentWorkspaceSurfaceSlotComponentRoleIds.flatMap((componentRole) => {
    if (renderers.byComponentRole?.[componentRole]) {
      return []
    }

    return [
      createPresentationProjectionDiagnostic({
        category: 'missing-binding',
        details: { componentRole },
        id: `document-workspace-surface-slot-renderer-role:${componentRole}:missing`,
        interpretation: {
          status: 'unbound',
          target: 'react',
        },
        message:
          `Standard document workspace surface-slot renderer role '${componentRole}' ` +
          'has no frontend renderer binding.',
        severity: 'warning',
        source: sourceId,
        subject: {
          id: componentRole,
          kind: 'document-workspace-surface-slot-renderer-role',
        },
        suggestedNextStep:
          'Register a renderer in the document workspace surface-slot renderer registry.',
      }),
    ]
  })
}

function renderDocumentWorkspaceHeaderSurfaceSlot<TActionContext>({
  componentSystem,
  headerContent,
  headerDescription,
  headerTitle,
  renderChromeSlot,
  slot,
}: DocumentWorkspaceSurfaceSlotRenderContext<TActionContext>) {
  const view = slot.view
  const chrome = view?.Chrome ?? null

  return componentSystem.documentWorkspaces.DocumentWorkspaceSurfaceSlot({
    regionId: slot.region?.Id ?? null,
    role: slot.role,
    slot: slot.id,
    viewId: view?.Id ?? null,
    renderSurface: (surfaceOptions) => (
      <ProjectedViewSurface
        className={surfaceOptions.className}
        collapsible={chrome?.Collapsible ?? surfaceOptions.collapsible}
        componentSystem={componentSystem}
        contentClassName={surfaceOptions.contentClassName}
        description={headerDescription}
        renderChromeSlot={renderChromeSlot}
        title={headerTitle}
        view={view}
      >
        {headerContent}
      </ProjectedViewSurface>
    ),
  })
}

function renderDocumentWorkspacePrimarySurfaceSlot<TActionContext>({
  componentSystem,
  documentContent,
  renderChromeSlot,
  renderDocumentViewState,
  slot,
  workspaceTitle,
}: DocumentWorkspaceSurfaceSlotRenderContext<TActionContext>) {
  const view = slot.view

  return componentSystem.documentWorkspaces.DocumentWorkspaceSurfaceSlot({
    regionId: slot.region?.Id ?? null,
    role: slot.role,
    slot: slot.id,
    viewId: view?.Id ?? null,
    renderSurface: (surfaceOptions) => (
      <ProjectedViewSurface
        chromeHeaderClassName={surfaceOptions.chromeHeaderClassName}
        className={surfaceOptions.className}
        componentSystem={componentSystem}
        contentClassName={surfaceOptions.contentClassName}
        renderChromeSlot={renderChromeSlot}
        title={workspaceTitle}
        view={view}
      >
        {renderDocumentViewState ? renderDocumentViewState(documentContent) : documentContent}
      </ProjectedViewSurface>
    ),
  })
}

function renderDocumentWorkspaceAuxiliarySlot<TActionContext>({
  auxiliaryContent,
  componentSystem,
  pageView,
  pageViewId,
  slot,
}: DocumentWorkspaceSurfaceSlotRenderContext<TActionContext>) {
  return componentSystem.documentWorkspaces.DocumentWorkspaceSurfaceSlot({
    children: auxiliaryContent,
    role: slot.role,
    slot: slot.id,
    viewId: pageView?.Id ?? pageViewId,
  })
}

function mergeRendererMaps<TActionContext>(
  maps: readonly (
    Readonly<Record<string, DocumentWorkspaceSurfaceSlotRenderer<TActionContext>>> |
    null |
    undefined
  )[],
) {
  return Object.assign({}, ...maps.filter(Boolean))
}

function createSurfaceSlotRendererDiagnostic({
  details,
  id,
  message,
  severity,
  slot,
  sourceId,
  status,
  suggestedNextStep,
}: {
  readonly details?: Record<string, unknown>
  readonly id: string
  readonly message: string
  readonly severity: PresentationProjectionDiagnostic['severity']
  readonly slot: ResolvedDocumentWorkspaceSurfaceSlot
  readonly sourceId: string
  readonly status: NonNullable<PresentationProjectionDiagnostic['interpretation']>['status']
  readonly suggestedNextStep: string
}) {
  return createPresentationProjectionDiagnostic({
    category: 'incomplete-projection',
    details,
    id,
    interpretation: {
      status,
      target: 'react',
    },
    message,
    severity,
    source: sourceId,
    subject: {
      id: slot.id,
      kind: 'document-workspace-surface-slot-renderer',
    },
    suggestedNextStep,
  })
}
