import {
  defaultPresentationComponentSet,
  resolvePresentationIcon,
  type PresentationIconModuleProjection,
  type PresentationIconRegistry,
} from './presentation-icon-registry'
import {
  createPresentationProjectionDiagnostic,
  type PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'

export interface PresentationActionIconPlacement {
  readonly ActionId: string
  readonly Icon?: string | null
  readonly Label?: string | null
  readonly Region?: string | null
}

export interface PresentationIconDiagnosticSubject {
  readonly details?: Readonly<Record<string, unknown>>
  readonly icon: string | null | undefined
  readonly id: string
  readonly kind: string
  readonly label?: string | null
}

export interface ProjectPresentationIconDiagnosticsOptions<
  TSubject extends PresentationIconDiagnosticSubject,
> {
  readonly componentSet?: string
  readonly icons: readonly TSubject[]
  readonly module?: PresentationIconModuleProjection | null
  readonly registry?: PresentationIconRegistry<TSubject> | null
  readonly source: string
  readonly surfaceId?: string | null
  readonly surfaceName?: string | null
}

export interface ProjectPresentationActionIconDiagnosticsOptions<
  TPlacement extends PresentationActionIconPlacement,
> {
  readonly actionPlacements: readonly TPlacement[]
  readonly componentSet?: string
  readonly module?: PresentationIconModuleProjection | null
  readonly registry?: PresentationIconRegistry<TPlacement> | null
  readonly source: string
  readonly surfaceId?: string | null
  readonly surfaceName?: string | null
}

/**
 * Reports arbitrary semantic icon ids that are not fully interpreted by the
 * frontend icon target.
 */
export function projectPresentationIconDiagnostics<
  TSubject extends PresentationIconDiagnosticSubject,
>({
  componentSet = defaultPresentationComponentSet,
  icons,
  module,
  registry,
  source,
  surfaceId,
  surfaceName,
}: ProjectPresentationIconDiagnosticsOptions<TSubject>): readonly PresentationProjectionDiagnostic[] {
  return icons.flatMap((subject) => {
    const icon = subject.icon
    if (!icon) {
      return []
    }

    const resolution = resolvePresentationIcon({
      componentSet,
      icon,
      module,
      registry,
    })

    if (!resolution.renderer) {
      return [
        createGenericIconDiagnostic({
          details: {
            componentKey: resolution.componentKey,
            componentRole: resolution.componentRole,
            icon,
            surfaceId,
            ...subject.details,
          },
          icon,
          message:
            `${formatIconSubject(subject)} declares icon '${icon}', ` +
            'but the frontend icon registry has no renderer for it.',
          reason: 'missing-icon-renderer',
          severity: 'warning',
          source,
          status: 'unbound',
          subject,
          surfaceName,
          target: 'presentation-icon-registry',
        }),
      ]
    }

    if (resolution.targetBindingSource) {
      if (resolution.resolutionSource === 'icon-key') {
        return [
          createGenericIconDiagnostic({
            details: {
              componentKey: resolution.componentKey,
              componentRole: resolution.componentRole,
              icon,
              surfaceId,
              ...subject.details,
            },
            icon,
            message:
              `${formatIconSubject(subject)} declares icon '${icon}', ` +
              'but its target binding was not resolved by the frontend icon registry; raw icon-key fallback was used.',
            reason: 'icon-target-binding-fallback',
            severity: 'warning',
            source,
            status: 'locally-interpreted',
            subject,
            surfaceName,
            target: 'presentation-icon-target-binding',
          }),
        ]
      }

      return []
    }

    return [
      createGenericIconDiagnostic({
        details: {
          icon,
          surfaceId,
          ...subject.details,
        },
        icon,
        message:
          `${formatIconSubject(subject)} declares icon '${icon}', ` +
          'but no presentation icon target binding was found; raw icon-key fallback was used.',
        reason: 'missing-icon-target-binding',
        severity: 'info',
        source,
        status: 'locally-interpreted',
        subject,
        surfaceName,
        target: 'presentation-icon-target-binding',
      }),
    ]
  })
}

/**
 * Reports action icon placements that are not fully interpreted by the
 * frontend icon target. The UI can still render through raw icon-key fallback,
 * but this keeps IR target-binding coverage visible in the developer toolbar.
 */
export function projectPresentationActionIconDiagnostics<
  TPlacement extends PresentationActionIconPlacement,
>({
  actionPlacements,
  componentSet = defaultPresentationComponentSet,
  module,
  registry,
  source,
  surfaceId,
  surfaceName,
}: ProjectPresentationActionIconDiagnosticsOptions<TPlacement>): readonly PresentationProjectionDiagnostic[] {
  return actionPlacements.flatMap((placement) => {
    const icon = placement.Icon
    if (!icon) {
      return []
    }

    const resolution = resolvePresentationIcon({
      componentSet,
      icon,
      module,
      registry,
    })

    if (!resolution.renderer) {
      return [
        createIconDiagnostic({
          details: {
            componentKey: resolution.componentKey,
            componentRole: resolution.componentRole,
            icon,
            region: placement.Region,
            surfaceId,
          },
          icon,
          message:
            `Action '${formatActionPlacement(placement)}' declares icon '${icon}', ` +
            'but the frontend icon registry has no renderer for it.',
          placement,
          reason: 'missing-icon-renderer',
          severity: 'warning',
          source,
          status: 'unbound',
          surfaceName,
          target: 'presentation-icon-registry',
        }),
      ]
    }

    if (resolution.targetBindingSource) {
      if (resolution.resolutionSource === 'icon-key') {
        return [
          createIconDiagnostic({
            details: {
              componentKey: resolution.componentKey,
              componentRole: resolution.componentRole,
              icon,
              region: placement.Region,
              surfaceId,
            },
            icon,
            message:
              `Action '${formatActionPlacement(placement)}' declares icon '${icon}', ` +
              'but its target binding was not resolved by the frontend icon registry; raw icon-key fallback was used.',
            placement,
            reason: 'icon-target-binding-fallback',
            severity: 'warning',
            source,
            status: 'locally-interpreted',
            surfaceName,
            target: 'presentation-icon-target-binding',
          }),
        ]
      }

      return []
    }

    return [
      createIconDiagnostic({
        details: {
          icon,
          region: placement.Region,
          surfaceId,
        },
        icon,
        message:
          `Action '${formatActionPlacement(placement)}' declares icon '${icon}', ` +
          'but no presentation icon target binding was found; raw icon-key fallback was used.',
        placement,
        reason: 'missing-icon-target-binding',
        severity: 'info',
        source,
        status: 'locally-interpreted',
        surfaceName,
        target: 'presentation-icon-target-binding',
      }),
    ]
  })
}

function createIconDiagnostic({
  details,
  icon,
  message,
  placement,
  reason,
  severity,
  source,
  status,
  surfaceName,
  target,
}: {
  readonly details: Readonly<Record<string, unknown>>
  readonly icon: string
  readonly message: string
  readonly placement: PresentationActionIconPlacement
  readonly reason: string
  readonly severity: PresentationProjectionDiagnostic['severity']
  readonly source: string
  readonly status: NonNullable<PresentationProjectionDiagnostic['interpretation']>['status']
  readonly surfaceName?: string | null
  readonly target: string
}) {
  return createPresentationProjectionDiagnostic({
    category: reason === 'missing-icon-renderer'
      ? 'missing-binding'
      : 'local-interpretation',
    details,
    id: `action-icon.${formatDiagnosticToken(placement.ActionId)}.${formatDiagnosticToken(placement.Region ?? 'default')}.${formatDiagnosticToken(icon)}.${reason}`,
    interpretation: {
      status,
      target,
    },
    message,
    severity,
    source,
    subject: {
      id: placement.ActionId,
      kind: 'action-placement',
      name: placement.Label ?? surfaceName ?? null,
    },
    suggestedNextStep: reason === 'missing-icon-target-binding'
      ? 'Add a presentation icon target binding for this icon id.'
      : 'Bind the icon target to a frontend icon renderer.',
  })
}

function createGenericIconDiagnostic({
  details,
  icon,
  message,
  reason,
  severity,
  source,
  status,
  subject,
  surfaceName,
  target,
}: {
  readonly details: Readonly<Record<string, unknown>>
  readonly icon: string
  readonly message: string
  readonly reason: string
  readonly severity: PresentationProjectionDiagnostic['severity']
  readonly source: string
  readonly status: NonNullable<PresentationProjectionDiagnostic['interpretation']>['status']
  readonly subject: PresentationIconDiagnosticSubject
  readonly surfaceName?: string | null
  readonly target: string
}) {
  return createPresentationProjectionDiagnostic({
    category: reason === 'missing-icon-renderer'
      ? 'missing-binding'
      : 'local-interpretation',
    details,
    id: `icon.${formatDiagnosticToken(subject.kind)}.${formatDiagnosticToken(subject.id)}.${formatDiagnosticToken(icon)}.${reason}`,
    interpretation: {
      status,
      target,
    },
    message,
    severity,
    source,
    subject: {
      id: subject.id,
      kind: subject.kind,
      name: subject.label ?? surfaceName ?? null,
    },
    suggestedNextStep: reason === 'missing-icon-target-binding'
      ? 'Add a presentation icon target binding for this semantic icon id.'
      : 'Bind the icon target to a frontend icon renderer.',
  })
}

function formatActionPlacement(placement: PresentationActionIconPlacement) {
  return placement.Label ?? placement.ActionId
}

function formatIconSubject(subject: PresentationIconDiagnosticSubject) {
  return subject.label
    ? `${subject.kind} '${subject.label}'`
    : `${subject.kind} '${subject.id}'`
}

function formatDiagnosticToken(value: string) {
  return value.replaceAll(/[^a-zA-Z0-9_.-]+/g, '-')
}
