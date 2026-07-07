import {
  findPresentationView,
  type DocumentWorkspaceSurfaceSlotDefinition,
  type DocumentWorkspaceSurfaceSlotRole,
  type PresentationBindingDefinition,
  type PresentationModuleDefinition,
  type ViewDefinition,
  type ViewRegionDefinition,
  type WorkspaceDefinition,
} from './module'
import {
  createPresentationProjectionDiagnostic,
  type PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'
import {
  documentWorkspaceSurfaceSlotRoleLabels,
  documentWorkspaceSurfaceSlotRoles,
  viewRegionKindLabels,
  viewRegionKinds,
} from '@cohesivesystems/presentation-contracts'

export interface DocumentWorkspaceSurfaceSlotDescriptor {
  readonly expectsHostedView: boolean
  readonly id: string
  readonly regionKind?: string | number | null
  readonly regionKindLabel?: string
  readonly role: string | number
  readonly roleLabel?: string
  readonly renderer?: PresentationBindingDefinition | null
}

export interface ResolvedDocumentWorkspaceSurfaceSlot {
  readonly descriptor: DocumentWorkspaceSurfaceSlotDescriptor
  readonly expectsHostedView: boolean
  readonly id: string
  readonly region: ViewRegionDefinition | null
  readonly role: string | number
  readonly roleLabel?: string
  readonly renderer?: PresentationBindingDefinition | null
  readonly view: ViewDefinition | null
}

export interface ResolvedDocumentWorkspaceSurfaceSlots {
  readonly all: readonly ResolvedDocumentWorkspaceSurfaceSlot[]
  readonly auxiliary: ResolvedDocumentWorkspaceSurfaceSlot
  readonly header: ResolvedDocumentWorkspaceSurfaceSlot
  readonly primarySurface: ResolvedDocumentWorkspaceSurfaceSlot
}

export const documentWorkspaceSurfaceSlotRoleKeys = {
  header: 'header',
  primarySurface: 'primarySurface',
  auxiliary: 'auxiliary',
  custom: 'custom',
} as const

export const documentWorkspaceSurfaceSlotIds = {
  auxiliary: 'auxiliary',
  header: 'header',
  workspace: 'workspace',
} as const

export const documentWorkspaceSurfaceSlotDescriptors = {
  header: {
    expectsHostedView: true,
    id: documentWorkspaceSurfaceSlotIds.header,
    regionKind: viewRegionKinds.header,
    regionKindLabel: viewRegionKindLabels[viewRegionKinds.header],
    role: documentWorkspaceSurfaceSlotRoles.header,
    roleLabel: documentWorkspaceSurfaceSlotRoleLabels[
      documentWorkspaceSurfaceSlotRoles.header
    ],
    renderer: null,
  },
  workspace: {
    expectsHostedView: true,
    id: documentWorkspaceSurfaceSlotIds.workspace,
    regionKind: viewRegionKinds.surface,
    regionKindLabel: viewRegionKindLabels[viewRegionKinds.surface],
    role: documentWorkspaceSurfaceSlotRoles.primarySurface,
    roleLabel: documentWorkspaceSurfaceSlotRoleLabels[
      documentWorkspaceSurfaceSlotRoles.primarySurface
    ],
    renderer: null,
  },
  auxiliary: {
    expectsHostedView: false,
    id: documentWorkspaceSurfaceSlotIds.auxiliary,
    role: documentWorkspaceSurfaceSlotRoles.auxiliary,
    roleLabel: documentWorkspaceSurfaceSlotRoleLabels[
      documentWorkspaceSurfaceSlotRoles.auxiliary
    ],
    renderer: null,
  },
} as const satisfies Record<string, DocumentWorkspaceSurfaceSlotDescriptor>

export function resolveDocumentWorkspaceSurfaceSlots(
  module: Pick<PresentationModuleDefinition, 'Views'> | null,
  pageView: ViewDefinition | null,
  workspace?: Pick<WorkspaceDefinition, 'SurfaceSlots'> | null,
): ResolvedDocumentWorkspaceSurfaceSlots {
  const descriptors = resolveDocumentWorkspaceSurfaceSlotDescriptors(workspace)
  const resolvedSlots = descriptors.map((descriptor) =>
    resolveDocumentWorkspaceSurfaceSlot(
      module,
      pageView,
      descriptor,
    ))
  const header = resolveDocumentWorkspaceSurfaceSlotByRole(
    resolvedSlots,
    module,
    pageView,
    documentWorkspaceSurfaceSlotDescriptors.header,
  )
  const primarySurface = resolveDocumentWorkspaceSurfaceSlotByRole(
    resolvedSlots,
    module,
    pageView,
    documentWorkspaceSurfaceSlotDescriptors.workspace,
  )
  const auxiliary = resolveDocumentWorkspaceSurfaceSlotByRole(
    resolvedSlots,
    module,
    pageView,
    documentWorkspaceSurfaceSlotDescriptors.auxiliary,
  )

  return {
    all: resolvedSlots,
    auxiliary,
    header,
    primarySurface,
  }
}

export function projectDocumentWorkspaceSurfaceSlotDiagnostics({
  declaredSlots,
  pageView,
  renderedSlots,
  sourceId,
}: {
  readonly declaredSlots: readonly ResolvedDocumentWorkspaceSurfaceSlot[]
  readonly pageView: ViewDefinition | null
  readonly renderedSlots: readonly ResolvedDocumentWorkspaceSurfaceSlot[]
  readonly sourceId: string
}): readonly PresentationProjectionDiagnostic[] {
  const diagnostics: PresentationProjectionDiagnostic[] = []
  const declaredSlotIds = new Set(declaredSlots.map((slot) => slot.id))
  const renderedSlotIds = new Set(renderedSlots.map((slot) => slot.id))

  diagnostics.push(...projectDuplicateDocumentWorkspaceSurfaceSlotRoleDiagnostics({
    declaredSlots,
    sourceId,
  }))

  for (const slotId of declaredSlotIds) {
    if (!renderedSlotIds.has(slotId)) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'incomplete-projection',
        id: `document-workspace-surface-slot:${slotId}:not-rendered`,
        interpretation: {
          status: 'unbound',
          target: 'react',
        },
        message:
          `Document workspace surface slot '${slotId}' is declared but is not rendered by the ` +
          'document editor surface interpreter.',
        severity: 'warning',
        source: sourceId,
        subject: {
          id: slotId,
          kind: 'document-workspace-surface-slot',
        },
        suggestedNextStep:
          'Bind a surface-slot renderer for this slot role or remove it from the workspace SurfaceSlots IR.',
      }))
    }
  }

  for (const slot of distinctDocumentWorkspaceSurfaceSlots([
    ...declaredSlots,
    ...renderedSlots,
  ])) {
    if (slot.expectsHostedView && !slot.view) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'missing-definition',
        details: {
          pageViewId: pageView?.Id ?? null,
          regionId: slot.region?.Id ?? null,
          role: resolveDocumentWorkspaceSurfaceSlotRoleKey(slot.role, slot.roleLabel),
        },
        id: `document-workspace-surface-slot:${slot.id}:missing-hosted-view`,
        interpretation: {
          status: 'unbound',
          target: 'react',
        },
        message:
          `Document workspace surface slot '${slot.id}' requires a hosted view but has no ` +
          'hosted view on the page view.',
        severity: 'warning',
        source: sourceId,
        subject: {
          id: slot.id,
          kind: 'document-workspace-surface-slot',
        },
        suggestedNextStep:
          'Declare a page region with the expected ViewRegionKind and a hosted view id for this slot.',
      }))
    }
  }

  for (const slot of renderedSlots) {
    if (!declaredSlotIds.has(slot.id)) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'local-interpretation',
        id: `document-workspace-surface-slot:${slot.id}:fallback`,
        interpretation: {
          status: 'locally-interpreted',
          target: 'react',
        },
        message:
          `Document workspace surface slot '${slot.id}' is rendered through the default ` +
          'surface-slot fallback because it is not declared on the workspace SurfaceSlots IR.',
        severity: 'info',
        source: sourceId,
        subject: {
          id: slot.id,
          kind: 'document-workspace-surface-slot',
        },
        suggestedNextStep:
          'Add this slot to the workspace SurfaceSlots declaration if it is a first-class slot.',
      }))
    }
  }

  return diagnostics
}

export function resolveDocumentWorkspaceSurfaceSlotRoleKey(
  role: string | number,
  roleLabel?: string,
) {
  for (const [roleKey, roleValue] of Object.entries(documentWorkspaceSurfaceSlotRoles)) {
    if (
      isEnumDiscriminator(
        role,
        roleValue,
        documentWorkspaceSurfaceSlotRoleLabels[
          roleValue as keyof typeof documentWorkspaceSurfaceSlotRoleLabels
        ],
      )
    ) {
      return roleKey
    }
  }

  return normalizeEnumDiscriminator(roleLabel ?? role)
}

export function isStandardDocumentWorkspaceSurfaceSlotRoleKey(roleKey: string) {
  return (
    roleKey === documentWorkspaceSurfaceSlotRoleKeys.header ||
    roleKey === documentWorkspaceSurfaceSlotRoleKeys.primarySurface ||
    roleKey === documentWorkspaceSurfaceSlotRoleKeys.auxiliary
  )
}

function resolveDocumentWorkspaceSurfaceSlotDescriptors(
  workspace?: Pick<WorkspaceDefinition, 'SurfaceSlots'> | null,
): readonly DocumentWorkspaceSurfaceSlotDescriptor[] {
  const slots = workspace?.SurfaceSlots ?? []
  if (slots.length === 0) {
    return Object.values(documentWorkspaceSurfaceSlotDescriptors)
  }

  return slots.map(createDocumentWorkspaceSurfaceSlotDescriptor)
}

function createDocumentWorkspaceSurfaceSlotDescriptor(
  slot: DocumentWorkspaceSurfaceSlotDefinition,
): DocumentWorkspaceSurfaceSlotDescriptor {
  const regionKind = slot.RegionKind ?? undefined
  return {
    expectsHostedView: slot.RequiresHostedView,
    id: slot.Id,
    regionKind,
    regionKindLabel: resolveViewRegionKindLabel(regionKind),
    role: slot.Role,
    roleLabel: resolveDocumentWorkspaceSurfaceSlotRoleLabel(slot.Role),
    renderer: slot.Renderer ?? null,
  }
}

function resolveDocumentWorkspaceSurfaceSlotByRole(
  resolvedSlots: readonly ResolvedDocumentWorkspaceSurfaceSlot[],
  module: Pick<PresentationModuleDefinition, 'Views'> | null,
  pageView: ViewDefinition | null,
  fallbackDescriptor: DocumentWorkspaceSurfaceSlotDescriptor,
): ResolvedDocumentWorkspaceSurfaceSlot {
  return (
    resolvedSlots.find((slot) =>
      isEnumDiscriminator(
        slot.role,
        fallbackDescriptor.role,
        fallbackDescriptor.roleLabel ?? String(fallbackDescriptor.role),
      )) ??
    resolveDocumentWorkspaceSurfaceSlot(module, pageView, fallbackDescriptor)
  )
}

function resolveDocumentWorkspaceSurfaceSlot(
  module: Pick<PresentationModuleDefinition, 'Views'> | null,
  pageView: ViewDefinition | null,
  descriptor: DocumentWorkspaceSurfaceSlotDescriptor,
): ResolvedDocumentWorkspaceSurfaceSlot {
  const regionKind = descriptor.regionKind
  const regionKindLabel = descriptor.regionKindLabel
  const region =
    regionKind === undefined || regionKind === null || regionKindLabel === undefined
      ? null
      : pageView?.Regions?.find((candidate) =>
          isEnumDiscriminator(
            candidate.Kind,
            regionKind,
            regionKindLabel,
          )) ?? null
  const viewId = region?.ViewIds?.[0]

  return {
    descriptor,
    expectsHostedView: descriptor.expectsHostedView,
    id: descriptor.id,
    region,
    role: descriptor.role,
    roleLabel: descriptor.roleLabel,
    renderer: descriptor.renderer ?? null,
    view: viewId ? findPresentationView(module, viewId) : null,
  }
}

function isEnumDiscriminator(
  value: string | number,
  expected: string | number,
  expectedLabel: string,
) {
  const valueLabel = resolveViewRegionKindLabel(value)

  return (
    value === expected ||
    String(value) === String(expected) ||
    (valueLabel !== undefined &&
      normalizeEnumDiscriminator(valueLabel) === normalizeEnumDiscriminator(expectedLabel)) ||
    normalizeEnumDiscriminator(value) === normalizeEnumDiscriminator(expectedLabel)
  )
}

function normalizeEnumDiscriminator(value: string | number) {
  return String(value).replace(/[-_\s]/g, '').toLocaleLowerCase()
}

function resolveViewRegionKindLabel(regionKind: string | number | null | undefined) {
  if (regionKind === null || regionKind === undefined) {
    return undefined
  }

  if (
    typeof regionKind === 'number' &&
    Object.prototype.hasOwnProperty.call(viewRegionKindLabels, regionKind)
  ) {
    return viewRegionKindLabels[regionKind as keyof typeof viewRegionKindLabels]
  }

  return String(regionKind)
}

function resolveDocumentWorkspaceSurfaceSlotRoleLabel(
  role: DocumentWorkspaceSurfaceSlotRole | string | number | null | undefined,
) {
  if (role === null || role === undefined) {
    return undefined
  }

  if (
    typeof role === 'number' &&
    Object.prototype.hasOwnProperty.call(documentWorkspaceSurfaceSlotRoleLabels, role)
  ) {
    return documentWorkspaceSurfaceSlotRoleLabels[
      role as keyof typeof documentWorkspaceSurfaceSlotRoleLabels
    ]
  }

  return String(role)
}

function projectDuplicateDocumentWorkspaceSurfaceSlotRoleDiagnostics({
  declaredSlots,
  sourceId,
}: {
  readonly declaredSlots: readonly ResolvedDocumentWorkspaceSurfaceSlot[]
  readonly sourceId: string
}): readonly PresentationProjectionDiagnostic[] {
  const slotsByRoleKey = new Map<string, ResolvedDocumentWorkspaceSurfaceSlot[]>()

  for (const slot of declaredSlots) {
    const roleKey = resolveDocumentWorkspaceSurfaceSlotRoleKey(slot.role, slot.roleLabel)
    if (!isStandardDocumentWorkspaceSurfaceSlotRoleKey(roleKey)) {
      continue
    }

    const slots = slotsByRoleKey.get(roleKey) ?? []
    slots.push(slot)
    slotsByRoleKey.set(roleKey, slots)
  }

  const diagnostics: PresentationProjectionDiagnostic[] = []
  for (const [roleKey, slots] of slotsByRoleKey) {
    if (slots.length <= 1) {
      continue
    }

    diagnostics.push(createPresentationProjectionDiagnostic({
      category: 'incomplete-projection',
      details: {
        role: roleKey,
        slotIds: slots.map((slot) => slot.id),
      },
      id: `document-workspace-surface-slot-role:${roleKey}:duplicate`,
      interpretation: {
        status: 'unbound',
        target: 'react',
      },
      message:
        `Document workspace surface role '${roleKey}' is declared by multiple slots: ` +
        slots.map((slot) => `'${slot.id}'`).join(', '),
      severity: 'warning',
      source: sourceId,
      subject: {
        id: roleKey,
        kind: 'document-workspace-surface-slot-role',
      },
      suggestedNextStep:
        'Keep one standard slot per role, or introduce a custom role with an explicit renderer.',
    }))
  }

  return diagnostics
}

function distinctDocumentWorkspaceSurfaceSlots(
  slots: readonly ResolvedDocumentWorkspaceSurfaceSlot[],
) {
  const distinctSlots = new Map<string, ResolvedDocumentWorkspaceSurfaceSlot>()
  for (const slot of slots) {
    distinctSlots.set(slot.id, slot)
  }

  return [...distinctSlots.values()]
}
