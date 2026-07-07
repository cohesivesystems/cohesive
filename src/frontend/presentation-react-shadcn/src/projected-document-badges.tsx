import { useMemo, type ReactNode } from 'react'

import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import {
  documentBadgeIconIds,
} from '@cohesive/presentation-core'
import {
  renderPresentationIcon,
} from './presentation-icon-registry'
import {
  projectPresentationIconDiagnostics,
} from './presentation-icon-diagnostics'
import {
  usePresentationModule,
  useRegisterPresentationProjectionDiagnostics,
} from '@cohesive/presentation-react'
import {
  getPresentationTextBadgeClassName,
  type PresentationTextBadgeTone,
} from '@cohesive/presentation-tailwind'

export function TextBadge({
  children,
  componentSystem,
  tone = 'slate',
}: {
  readonly children: ReactNode
  readonly componentSystem: PresentationComponentSystem
  readonly tone?: PresentationTextBadgeTone
}) {
  if (children === null || children === undefined || children === '') {
    return null
  }

  const BadgeComponent = componentSystem.badges.Badge
  return (
    <BadgeComponent className={getPresentationTextBadgeClassName(tone)} variant="outline">
      {children}
    </BadgeComponent>
  )
}

export function OriginBadge({
  componentSystem,
  label,
}: {
  readonly componentSystem: PresentationComponentSystem
  readonly label: string
}) {
  return <TextBadge componentSystem={componentSystem} tone="teal">{label}</TextBadge>
}

export function ObservationVersionBadge({
  componentSystem,
  version,
}: {
  readonly componentSystem: PresentationComponentSystem
  readonly version?: number | null
}) {
  return (
    <TextBadge componentSystem={componentSystem}>
      {version === null || version === undefined
        ? 'Observation unversioned'
        : `Observation v${version.toLocaleString()}`}
    </TextBadge>
  )
}

export function ResourceLinkBadge({
  componentSystem,
  disabled,
  label,
  onClick,
  prefix,
}: {
  readonly componentSystem: PresentationComponentSystem
  readonly disabled?: boolean
  readonly label: string
  readonly onClick: () => void
  readonly prefix: string
}) {
  const module = usePresentationModule()
  const ActionButton = componentSystem.actions.ActionButton
  const iconDiagnostics = useMemo(
    () => projectPresentationIconDiagnostics({
      icons: [{
        details: {
          prefix,
        },
        icon: documentBadgeIconIds.open,
        id: `${prefix}:${label}:open`,
        kind: 'document-badge-icon',
        label: `Open ${prefix}`,
      }],
      module,
      source: `document-badge-icons:${prefix}:${label}`,
    }),
    [
      label,
      module,
      prefix,
    ],
  )
  useRegisterPresentationProjectionDiagnostics(
    `document-badge-icons:${prefix}:${label}`,
    iconDiagnostics,
  )

  return (
    <ActionButton
      aria-label={`Open ${prefix}: ${label}`}
      className="h-6 max-w-full rounded-md border-sky-700/15 bg-sky-50 px-2 text-xs font-medium text-sky-700 hover:bg-sky-100 hover:text-sky-900"
      disabled={disabled}
      onClick={onClick}
      size="sm"
      type="button"
      variant="outline"
    >
      <span className="truncate">
        {prefix}: {label}
      </span>
      {renderPresentationIcon({
        className: 'size-3',
        icon: documentBadgeIconIds.open,
        module,
      }) ?? renderPresentationIcon({
        className: 'size-3',
        icon: 'square-arrow-out-up-right',
      })}
    </ActionButton>
  )
}
